using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The real HTTP transport against a receiver on this machine alone (127.0.0.1; unit 9-C): a 201 with ok:true is a
    /// success; another status, a body without ok, a connection refused and a timeout are failures that come back as
    /// such (the timeout flagged); an abort lets no word back. Through the sender: a failure is resent as the same
    /// bytes at the next offer, and a success is not sent again. Nothing leaves this machine; no token value is real.
    /// </summary>
    public sealed class PlayStatisticsTransportPlayModeTests
    {
        private sealed class Receiver : IDisposable
        {
            public readonly List<(string token, string contentType, string body)> Requests = new List<(string, string, string)>();
            public int Status = 201;
            public string Reply = "{\"ok\":true}";
            public int HoldSeconds;
            private readonly HttpListener _listener;
            private readonly Thread _thread;
            private volatile bool _stopping;
            public readonly string Url;

            public Receiver(int port)
            {
                Url = "http://127.0.0.1:" + port + "/v1/telemetry";
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Start();
                _thread = new Thread(Serve) { IsBackground = true };
                _thread.Start();
            }

            private void Serve()
            {
                while (!_stopping)
                {
                    HttpListenerContext context;
                    try { context = _listener.GetContext(); } catch (Exception) { return; }
                    try
                    {
                        string body;
                        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
                        lock (Requests) Requests.Add((context.Request.Headers["X-Telemetry-Token"], context.Request.ContentType, body));
                        if (HoldSeconds > 0) Thread.Sleep(HoldSeconds * 1000);
                        byte[] reply = Encoding.UTF8.GetBytes(Reply);
                        context.Response.StatusCode = Status;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = reply.Length;
                        context.Response.OutputStream.Write(reply, 0, reply.Length);
                        context.Response.Close();
                    }
                    catch (Exception) { try { context.Response.Abort(); } catch (Exception) { } }
                }
            }

            public int Count { get { lock (Requests) return Requests.Count; } }

            public void Dispose()
            {
                _stopping = true;
                try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
            }
        }

        private static PlayStatisticsConfig Config(string url, int timeout = 5)
        {
            var config = ScriptableObject.CreateInstance<PlayStatisticsConfig>();
            config.endpointUrl = url; config.ingestToken = "local-test-only-not-a-real-token"; config.timeoutSeconds = timeout; config.sendEnabled = true; config.testerRole = "developer";
            return config;
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static IEnumerator Until(Func<bool> done, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        [UnityTest]
        public IEnumerator A201WithOk_IsASuccess_WithTheTokenHeaderAndJsonBody()
        {
            int port = FreePort();
            using (var receiver = new Receiver(port))
            {
                var host = new GameObject("transport host");
                PlayStatisticsConfig config = Config(receiver.Url);
                try
                {
                    var transport = new PlayStatisticsUnityTransport(host.AddComponent<TransportHost>(), config);
                    PlayStatisticsSendResult result = default; bool done = false;
                    transport.Send(Encoding.UTF8.GetBytes("{\"schema_version\":1}"), r => { result = r; done = true; });
                    yield return Until(() => done, 10f);
                    Assert.That(done, Is.True, "the answer came back");
                    Assert.That(result.Success, Is.True);
                    Assert.That(result.StatusCode, Is.EqualTo(201));
                    Assert.That(result.TimedOut, Is.False);
                    Assert.That(receiver.Count, Is.EqualTo(1));
                    Assert.That(receiver.Requests[0].token, Is.EqualTo(config.ingestToken));
                    Assert.That(receiver.Requests[0].contentType, Does.StartWith("application/json"));
                    Assert.That(receiver.Requests[0].body, Is.EqualTo("{\"schema_version\":1}"));
                }
                finally { UnityEngine.Object.Destroy(host); UnityEngine.Object.Destroy(config); }
            }
        }

        [UnityTest]
        public IEnumerator AnotherStatus_OrABodyWithoutOk_OrARefusedConnection_IsAFailure_NotATimeout()
        {
            int port = FreePort();
            var host = new GameObject("transport host");
            PlayStatisticsConfig config = Config("http://127.0.0.1:" + port + "/v1/telemetry");
            try
            {
                var transport = new PlayStatisticsUnityTransport(host.AddComponent<TransportHost>(), config);
                using (var receiver = new Receiver(port) { Status = 500, Reply = "{\"error\":\"boom\"}" })
                {
                    PlayStatisticsSendResult result = default; bool done = false;
                    transport.Send(Encoding.UTF8.GetBytes("{}"), r => { result = r; done = true; });
                    yield return Until(() => done, 10f);
                    Assert.That(done && !result.Success && result.StatusCode == 500 && !result.TimedOut, Is.True, "500: " + result.Error);
                }

                using (var receiver = new Receiver(port) { Status = 201, Reply = "{\"ok\":false}" })
                {
                    PlayStatisticsSendResult result = default; bool done = false;
                    transport.Send(Encoding.UTF8.GetBytes("{}"), r => { result = r; done = true; });
                    yield return Until(() => done, 10f);
                    Assert.That(done && !result.Success && result.StatusCode == 201, Is.True, "201 without ok:true is no success");
                }

                {
                    // Nothing listens on the port now: refused, not timed out.
                    PlayStatisticsSendResult result = default; bool done = false;
                    transport.Send(Encoding.UTF8.GetBytes("{}"), r => { result = r; done = true; });
                    yield return Until(() => done, 10f);
                    Assert.That(done && !result.Success, Is.True, "refused: " + result.Error);
                    Assert.That(result.TimedOut, Is.False, "a refusal is not a timeout: " + result.Error);
                }
            }
            finally { UnityEngine.Object.Destroy(host); UnityEngine.Object.Destroy(config); }
        }

        [UnityTest]
        public IEnumerator AReceiverThatHolds_PastTheTimeout_ComesBackAsTimedOut_AndAnAbortLetsNoWordBack()
        {
            int port = FreePort();
            var host = new GameObject("transport host");
            PlayStatisticsConfig config = Config("http://127.0.0.1:" + port + "/v1/telemetry", timeout: 1);
            try
            {
                var transport = new PlayStatisticsUnityTransport(host.AddComponent<TransportHost>(), config);
                using (var receiver = new Receiver(port) { HoldSeconds = 4 })
                {
                    PlayStatisticsSendResult result = default; bool done = false;
                    transport.Send(Encoding.UTF8.GetBytes("{}"), r => { result = r; done = true; });
                    yield return Until(() => done, 10f);
                    Assert.That(done && !result.Success, Is.True, "timed out: " + result.Error);
                    Assert.That(result.TimedOut, Is.True, "flagged as a timeout: " + result.Error);

                    bool spoke = false;
                    IPlayStatisticsRequest request = transport.Send(Encoding.UTF8.GetBytes("{}"), r => spoke = true);
                    yield return null;
                    request.Abort();
                    float until = Time.realtimeSinceStartup + 3f;
                    while (Time.realtimeSinceStartup < until) yield return null;
                    Assert.That(spoke, Is.False, "no word comes back after an abort");
                }
            }
            finally { UnityEngine.Object.Destroy(host); UnityEngine.Object.Destroy(config); }
        }

        [UnityTest]
        public IEnumerator ThroughTheSender_AFailureIsResentAsTheSameBytes_AndASuccessIsNotSentAgain()
        {
            int port = FreePort();
            var host = new GameObject("transport host");
            PlayStatisticsConfig config = Config("http://127.0.0.1:" + port + "/v1/telemetry");
            try
            {
                var statistics = new PlayStatistics();
                var consent = new PlayStatisticsConsent(new MemoryPlayStatisticsStore());
                consent.Agree();
                var collector = new PlayStatisticsCollector(statistics);
                collector.Agree();
                var conditions = new PlayConditions { Scene = "TestScene", Mode = "play" };
                for (int i = 0; i < 6; i++) collector.Frame(true, false, true, conditions, 14f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
                var transport = new PlayStatisticsUnityTransport(host.AddComponent<TransportHost>(), config);
                var sender = new PlayStatisticsSender(statistics, consent, transport, "developer");
                var results = new List<PlayStatisticsSendResult>();
                sender.Completed += (snapshot, r) => results.Add(r);

                using (var receiver = new Receiver(port) { Status = 503, Reply = "{\"error\":\"down\"}" })
                {
                    Assert.That(sender.Offer().Kind, Is.EqualTo(PlayStatisticsOfferKind.Sending));
                    yield return Until(() => results.Count == 1, 10f);
                    Assert.That(results.Count == 1 && !results[0].Success, Is.True, "the receiver refused");
                    Assert.That(receiver.Count, Is.EqualTo(1));
                    string first = receiver.Requests[0].body;

                    receiver.Status = 201; receiver.Reply = "{\"ok\":true}";
                    Assert.That(sender.Offer().Kind, Is.EqualTo(PlayStatisticsOfferKind.Sending), "the same snapshot is offered again at the next loss of focus");
                    yield return Until(() => results.Count == 2, 10f);
                    Assert.That(results.Count == 2 && results[1].Success, Is.True);
                    Assert.That(receiver.Requests[1].body, Is.EqualTo(first), "the same bytes (the same session and revision) were resent");
                    Assert.That(sender.Offer().Kind, Is.EqualTo(PlayStatisticsOfferKind.Unchanged), "accepted and unchanged: not sent again");
                    yield return null;
                    Assert.That(receiver.Count, Is.EqualTo(2));
                }
            }
            finally { UnityEngine.Object.Destroy(host); UnityEngine.Object.Destroy(config); }
        }

        private sealed class TransportHost : MonoBehaviour { }
    }
}
