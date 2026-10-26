#if ZANTETSU_STATS_RECOVERY_PROBE && DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Sandbox
{
    /// <summary>Isolated device check of the production sender/transport. Synthetic data, loopback, no PlayerPrefs.</summary>
    public sealed class PlayStatisticsRecoveryProbe : MonoBehaviour
    {
        public const string Package = "com.zantetsuken.statisticsrecoveryprobe";
        private const string Prefix = "STATISTICS RECOVERY CHECK: ";
        [Serializable] private sealed class Report
        {
            public bool passed;
            public string error;
            public string[] cases;
        }
        private readonly List<string> passed = new List<string>();
        private string status = "Checking local HTTP recovery...";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (Application.identifier != Package) return;
            SandboxScenario.HoldForDevelopmentReplay();
            Application.runInBackground = true;
            Application.targetFrameRate = 30;
            DontDestroyOnLoad(new GameObject("Statistics recovery check", typeof(PlayStatisticsRecoveryProbe)));
        }

        private IEnumerator Start()
        {
            var check = Run();
            while (true)
            {
                object next;
                try { if (!check.MoveNext()) break; next = check.Current; }
                catch (Exception error) { Finish(error.Message); yield break; }
                yield return next;
            }
            Finish(null);
        }

        private IEnumerator Run()
        {
            foreach (string kind in new[] { "refused", "unavailable", "timeout", "withdraw" })
            {
                status = "Checking " + kind;
                var statistics = new PlayStatistics { BuildId = "local-network-recovery-check" };
                var consent = new PlayStatisticsConsent(new MemoryPlayStatisticsStore());
                consent.Agree();
                var collector = new PlayStatisticsCollector(statistics);
                collector.Agree();
                var conditions = new PlayConditions { Scene = "SyntheticRecoveryCheck", Mode = "check" };
                for (int i = 0; i < 6; i++) collector.Frame(true, false, true, conditions, 16f, default, 1);
                var config = ScriptableObject.CreateInstance<PlayStatisticsConfig>();
                config.endpointUrl = "http://127.0.0.1:" + (kind == "refused" ? 18766 : 18765) + "/" + kind;
                config.ingestToken = "local-check-only";
                config.timeoutSeconds = kind == "timeout" || kind == "withdraw" ? 1 : 5;
                config.sendEnabled = true;
                config.testerRole = "developer";
                var sender = new PlayStatisticsSender(statistics, consent, new PlayStatisticsUnityTransport(this, config), "developer");
                var results = new List<PlayStatisticsSendResult>();
                sender.Completed += (snapshot, result) => results.Add(result);
                PlayStatisticsOffer first = sender.Offer();
                Require(first.Kind == PlayStatisticsOfferKind.Sending, kind + ": initial send missing");
                if (kind == "withdraw")
                {
                    float cancelAt = Time.realtimeSinceStartup + .3f;
                    while (Time.realtimeSinceStartup < cancelAt) yield return null;
                    consent.Decline(); sender.Withdraw();
                    Require(!sender.Busy && !sender.HasPending && sender.Aborted == 1, "withdraw did not abandon the request");
                    float until = Time.realtimeSinceStartup + 2f;
                    while (Time.realtimeSinceStartup < until) yield return null;
                    Require(results.Count == 0 && sender.Offer().Kind == PlayStatisticsOfferKind.NotAgreed, "withdraw allowed a callback or resend");
                }
                else
                {
                    float until = Time.realtimeSinceStartup + 12f;
                    while (results.Count < 1 && Time.realtimeSinceStartup < until) yield return null;
                    Require(results.Count == 1 && !results[0].Success && !sender.Busy, kind + ": failure was not reported");
                    if (kind == "unavailable") Require(results[0].StatusCode == 503, "expected HTTP 503");
                    if (kind == "timeout") Require(results[0].TimedOut, "timeout was not flagged");
                    if (kind == "refused") Require(results[0].StatusCode == 0, "connection refusal unexpectedly received HTTP");
                    until = Time.realtimeSinceStartup + .25f;
                    while (Time.realtimeSinceStartup < until) yield return null;
                    Require(sender.Started == 1, "failure retried without the next offer");
                    if (kind == "refused")
                    {
                        using (var control = UnityWebRequest.Get("http://127.0.0.1:18765/enable-recovery"))
                        {
                            control.timeout = 5;
                            yield return control.SendWebRequest();
                            Require(control.result == UnityWebRequest.Result.Success, "local receiver could not be restored");
                        }
                    }
                    PlayStatisticsOffer retry = sender.Offer();
                    Require(retry.Kind == PlayStatisticsOfferKind.Sending && ReferenceEquals(retry.Snapshot, first.Snapshot), "retry changed the snapshot/revision/bytes");
                    until = Time.realtimeSinceStartup + 12f;
                    while (results.Count < 2 && Time.realtimeSinceStartup < until) yield return null;
                    Require(results.Count == 2 && results[1].Success && results[1].StatusCode == 201, kind + ": recovery was not accepted");
                    Require(sender.Offer().Kind == PlayStatisticsOfferKind.Unchanged && sender.Started == 2, "accepted snapshot was sent again");
                }
                Destroy(config);
                passed.Add(kind);
                Debug.Log(Prefix + "PASS " + kind);
            }
        }

        private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
        private void Finish(string error)
        {
            var report = new Report { passed = error == null, error = error, cases = passed.ToArray() };
            status = error == null ? "PASS: " + string.Join(", ", passed) : "FAILED: " + error;
            File.WriteAllText(Path.Combine(Application.persistentDataPath, "statistics-recovery.json"), JsonUtility.ToJson(report, true));
            Debug.Log(Prefix + status);
        }
        private void OnGUI()
        {
            GUI.Label(new Rect(24, 24, Screen.width - 48, Screen.height - 48), status,
                new GUIStyle(GUI.skin.label) { fontSize = 26, wordWrap = true });
        }
    }
}
#endif
