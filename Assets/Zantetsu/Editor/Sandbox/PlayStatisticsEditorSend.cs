using System;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>
    /// One POST of a play statistics body from the Editor (DESIGN 21.18), to learn whether the receiver takes the
    /// body's shape before any Player is built for it: a record of made-up values in the real format -- marked as a
    /// check by its build id and scene -- written by <see cref="PlayStatisticsJson"/> and sent once with the ingest
    /// token. The endpoint and the token come from the environment (<c>ZANTETSU_STATS_ENDPOINT</c>,
    /// <c>ZANTETSU_INGEST_TOKEN</c>) and are in no file; the token is never logged. For <c>-executeMethod</c>: exit 0
    /// where the receiver answered 201 with ok:true, 3 where it answered otherwise, 2 where nothing was sent.
    /// </summary>
    public static class PlayStatisticsEditorSend
    {
        public const string Prefix = "PLAY STATISTICS SEND CHECK: ";

        public static void Run()
        {
            int code = 2;
            try
            {
                code = Send();
            }
            catch (Exception e)
            {
                Debug.Log(Prefix + "not sent: " + e.GetType().Name + ": " + e.Message);
            }

            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        private static int Send()
        {
            string endpoint = Environment.GetEnvironmentVariable("ZANTETSU_STATS_ENDPOINT");
            string token = Environment.GetEnvironmentVariable("ZANTETSU_INGEST_TOKEN");
            string role = Environment.GetEnvironmentVariable("ZANTETSU_STATS_ROLE");
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(token))
            {
                Debug.Log(Prefix + "not sent: endpoint " + (string.IsNullOrEmpty(endpoint) ? "absent" : "present") + ", token " + (string.IsNullOrEmpty(token) ? "absent" : "present"));
                return 2;
            }

            PlayStatistics statistics = MadeUp();
            string session = Guid.NewGuid().ToString(), install = Guid.NewGuid().ToString();
            var json = new StringBuilder(8192);
            PlayStatisticsJson.Write(json, statistics, session, install, 0, string.IsNullOrEmpty(role) ? "developer" : role);
            byte[] body = Encoding.UTF8.GetBytes(json.ToString());
            Debug.Log(Prefix + "sending " + body.Length + " bytes to " + endpoint + ": session " + session + ", revision 0, a made-up record (build '" + statistics.BuildId + "')");
            if (body.Length > PlayStatisticsSender.MaxBodyBytes)
            {
                Debug.Log(Prefix + "not sent: over the receiver's " + PlayStatisticsSender.MaxBodyBytes + " bytes");
                return 2;
            }

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                request.Headers.TryAddWithoutValidation("X-Telemetry-Token", token);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult())
                {
                    string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                    watch.Stop();
                    string compact = text.Replace(" ", "").Replace("\n", "").Replace("\r", "");
                    bool accepted = (int)response.StatusCode == 201 && compact.Contains("\"ok\":true");
                    Debug.Log(Prefix + "answer: HTTP " + (int)response.StatusCode + " in " + watch.ElapsedMilliseconds + " ms, body " + (text.Length > 300 ? text.Substring(0, 300) : text)
                        + " -- " + (accepted ? "ACCEPTED; expected key sessions/" + session + "/000000.json" : "NOT accepted"));
                    return accepted ? 0 : 3;
                }
            }
        }

        // A record in the real format with made-up values, small and plainly a check.
        private static PlayStatistics MadeUp()
        {
            var s = new PlayStatistics
            {
                BuildId = "editor-send-check/" + Application.unityVersion, Platform = Application.platform.ToString(), OperatingSystem = SystemInfo.operatingSystem ?? "",
                Cpu = SystemInfo.processorType ?? "", Gpu = SystemInfo.graphicsDeviceName ?? "", GraphicsApi = SystemInfo.graphicsDeviceVersion ?? "",
                GpuDriver = GraphicsDriverVersion.Read(SystemInfo.graphicsDeviceVendorID, SystemInfo.graphicsDeviceID),
                XrDevice = null, XrRuntime = "none", XrRuntimeVersion = null, TimeSource = "made-up (Editor send check)",
            };
            var collector = new PlayStatisticsCollector(s);
            collector.Agree();
            var conditions = new PlayConditions { Scene = "EditorSendCheck", Mode = "check", DrawRoute = "none", XrRuntime = "none", RefreshRateHz = 90f, RenderScale = 1f, EyeTextureWidth = 0, EyeTextureHeight = 0, MsaaSamples = 4 };
            var random = new System.Random(20261014);
            for (int i = 0; i < 400; i++)
            {
                var sample = new FrameTimeSample { HasMain = true, MainMs = 6f + (float)random.NextDouble() * 6f, HasRender = true, RenderMs = 2f + (float)random.NextDouble() * 2f, HasGpu = true, GpuMs = 2f + (float)random.NextDouble() * 3f };
                collector.Frame(true, false, true, in conditions, 11.1f, sample, 1 + (i / 50));
            }

            collector.Live(12);
            collector.Total(PlayTotalKind.AcceptedCuts, 1, 0);
            collector.Total(PlayTotalKind.AcceptedCuts, 1, 7);
            collector.Capacity(PlayCapacityKind.DrawCommandSlots, 1, 300, 1024, 65536, 0, 0);
            return s;
        }
    }
}
