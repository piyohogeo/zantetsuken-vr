using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.LowLevel;
using Zantetsu.Core.Animation;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox.Editor
{
    /// <summary>Opt-in batch smoke test of the imported private scene, including a real current-pose cut.</summary>
    [InitializeOnLoad]
    public static class MobPlanPlayVerification
    {
        private const string Key = "Zantetsu.MobPlan.Verify";
        private static double started, readyAt;
        private static bool cutRequested, cutAttempted;
        private static SandboxNpcCharacter cutCharacter;
        private static Vector3 initialPosition;
        private static int errors;
        private static int worstLive;
        private static Vector3 initialPlayer;
        private static float playerTravel;
        private static bool installed;
        private static PlayerLoopSystem originalLoop;
        static MobPlanPlayVerification()
        {
            EditorApplication.update += Update;
            Application.logMessageReceived += (message, stack, type) =>
            { if (SessionState.GetBool(Key, false) && (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)) errors++; };
        }
        public static void Run()
        {
            MobPlanSceneBuild.Build();
            MobPlanDataVerification.Verify();
            SessionState.SetBool(Key, true);
            SessionState.SetFloat(Key + ".start", (float)EditorApplication.timeSinceStartup);
            EditorApplication.EnterPlaymode();
        }
        private static void Update()
        {
            if (!SessionState.GetBool(Key, false)) return;
            double wall = EditorApplication.timeSinceStartup;
            if (wall - SessionState.GetFloat(Key + ".start", (float)wall) > 180) { Finish(false, "timeout"); return; }
            if (!EditorApplication.isPlaying) return;
            if (installed) return;
            originalLoop = PlayerLoop.GetCurrentPlayerLoop();
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (int i = 0; i < loop.subSystemList.Length; i++)
                if (loop.subSystemList[i].type == typeof(UnityEngine.PlayerLoop.Update))
                    loop.subSystemList[i].subSystemList = loop.subSystemList[i].subSystemList.Concat(new[]
                    { new PlayerLoopSystem { type = typeof(MobPlanPlayVerification), updateDelegate = CheckFrame } }).ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            installed = true;
        }
        private static void CheckFrame()
        {
            if (!SessionState.GetBool(Key, false)) return;
            double wall = EditorApplication.timeSinceStartup;
            var crowd = UnityEngine.Object.FindFirstObjectByType<MobPlanCrowd>();
            if (crowd == null || !crowd.IsReady) return;
            var input = UnityEngine.Object.FindFirstObjectByType<MobPlanPlayerInput>();
            if (readyAt == 0)
            {
                readyAt = wall; started = crowd.PlanTime; worstLive = 20;
                input.liveInput = false; initialPlayer = input.player.transform.position;
            }
            double elapsed = wall - readyAt;
            if (elapsed > 4 && elapsed < 8) input.Submit(.5f, .1f, Time.deltaTime);
            else if (elapsed > 8 && elapsed < 11) input.Submit(-.5f, -.1f, Time.deltaTime);
            playerTravel = Mathf.Max(playerTravel, Vector3.Distance(initialPlayer, input.player.transform.position));
            worstLive = Mathf.Min(worstLive, crowd.LiveCount);
            var characters = UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None);
            if (cutCharacter == null)
            {
                cutCharacter = characters.FirstOrDefault(c => c.IsTarget && c.Handle != null && c.Handle.IsReady);
                if (cutCharacter != null) initialPosition = cutCharacter.CharacterRoot.transform.position;
            }
            if (!cutAttempted && wall - readyAt > 12 && cutCharacter != null && crowd.PlannerBusy)
            {
                cutCharacter.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyNow(null, 0);
                var mesh = new Mesh();
                cutCharacter.Renderer.BakeMesh(mesh);
                float y = mesh.bounds.center.y;
                UnityEngine.Object.DestroyImmediate(mesh);
                cutAttempted = true;
                var result = cutCharacter.Handle.TryCut(new float4(0, 1, 0, -y), (float3)cutCharacter.Renderer.transform.position);
                cutRequested = result.Outcome == VpCharacterCutOutcome.Requested || result.Outcome == VpCharacterCutOutcome.Held;
                Debug.Log("MOBPLAN cut: " + result.Outcome + " failure=" + cutCharacter.Handle.LastFailure + ", moved=" + Vector3.Distance(initialPosition, cutCharacter.CharacterRoot.transform.position));
            }
            if (wall - readyAt < 32) return;
            bool retired = cutCharacter != null && cutCharacter.Handle.IsWithdrawn;
            bool pass = errors == 0 && crowd.PublishedCycles >= 10 && cutRequested && retired && crowd.LiveCount == 20 && worstLive == 19 && playerTravel > .1f;
            double Percentile(Func<MobPlanCrowd.CycleTiming, double> value, double p)
            { var sorted = crowd.Timings.Select(value).OrderBy(v => v).ToArray(); return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling((sorted.Length - 1) * p)]; }
            string resultText = $"passed={pass} errors={errors} published={crowd.PublishedCycles} stale={crowd.StaleCycles} failed={crowd.FailedCycles} live={crowd.LiveCount} minLive={worstLive} cut={cutRequested} retired={retired} playerTravel={playerTravel:F3} planSeconds={crowd.PlanTime - started:F2} queueP95Ms={Percentile(t => t.queue, .95):F3} computeP95Ms={Percentile(t => t.compute, .95):F3} collectP95Ms={Percentile(t => t.collect, .95):F3} latencyP99Ms={Percentile(t => t.queue+t.compute+t.collect, .99):F3}";
            File.WriteAllLines("Logs/MobPlan/cycles.csv", new[] { "queue_ms,compute_ms,collect_ms" }.Concat(crowd.Timings.Select(t => FormattableString.Invariant($"{t.queue:F3},{t.compute:F3},{t.collect:F3}"))));
            Finish(pass, resultText);
        }
        private static void Finish(bool passed, string result)
        {
            SessionState.SetBool(Key, false);
            if (installed) PlayerLoop.SetPlayerLoop(originalLoop);
            File.WriteAllText("Logs/MobPlan/play-verification.txt", result);
            Debug.Log("MOBPLAN PLAY " + result);
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }
}
