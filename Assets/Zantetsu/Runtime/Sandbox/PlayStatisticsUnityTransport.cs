using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Carries a statistics body to the receiver by <see cref="UnityWebRequest"/> (DESIGN 21.18), as the telemetry
    /// probe did: one POST of UTF-8 JSON with the ingest token in <see cref="TokenHeader"/>, a timeout, and success
    /// only on 201 with <c>"ok":true</c> in the answer. The request runs as a coroutine of the host; an abort stops it
    /// and no word of it comes back. Nothing is retried here.
    /// </summary>
    public sealed class PlayStatisticsUnityTransport : IPlayStatisticsTransport
    {
        public const string TokenHeader = "X-Telemetry-Token";

        private readonly MonoBehaviour _host;
        private readonly PlayStatisticsConfig _config;

        public PlayStatisticsUnityTransport(MonoBehaviour host, PlayStatisticsConfig config)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public IPlayStatisticsRequest Send(byte[] body, Action<PlayStatisticsSendResult> done)
        {
            var request = new Request(this, body, done);
            request.Routine = _host.StartCoroutine(request.Run());
            return request;
        }

        private sealed class Request : IPlayStatisticsRequest
        {
            private readonly PlayStatisticsUnityTransport _owner;
            private readonly byte[] _body;
            private readonly Action<PlayStatisticsSendResult> _done;
            private UnityWebRequest _web;
            private bool _aborted;
            public Coroutine Routine;

            public Request(PlayStatisticsUnityTransport owner, byte[] body, Action<PlayStatisticsSendResult> done)
            {
                _owner = owner;
                _body = body;
                _done = done;
            }

            public IEnumerator Run()
            {
                using (_web = new UnityWebRequest(_owner._config.endpointUrl, UnityWebRequest.kHttpVerbPOST))
                {
                    _web.uploadHandler = new UploadHandlerRaw(_body) { contentType = "application/json" };
                    _web.downloadHandler = new DownloadHandlerBuffer();
                    _web.SetRequestHeader("Content-Type", "application/json");
                    _web.SetRequestHeader(TokenHeader, _owner._config.ingestToken);
                    _web.timeout = Math.Max(1, _owner._config.timeoutSeconds);
                    yield return _web.SendWebRequest();
                    if (_aborted)
                    {
                        yield break;
                    }

                    var result = new PlayStatisticsSendResult { StatusCode = _web.responseCode, Error = _web.error };
                    string text = _web.downloadHandler != null ? _web.downloadHandler.text : null;
                    string compact = text != null ? text.Replace(" ", "").Replace("\n", "").Replace("\r", "") : "";
                    result.Success = _web.result == UnityWebRequest.Result.Success && _web.responseCode == 201 && compact.Contains("\"ok\":true");
                    result.TimedOut = _web.result == UnityWebRequest.Result.ConnectionError && _web.error != null
                        && _web.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                    _web = null;
                    _done?.Invoke(result);
                }
            }

            public void Abort()
            {
                _aborted = true;
                try { _web?.Abort(); } catch (Exception) { }
                if (Routine != null && _owner._host != null)
                {
                    _owner._host.StopCoroutine(Routine);
                }
            }
        }
    }
}
