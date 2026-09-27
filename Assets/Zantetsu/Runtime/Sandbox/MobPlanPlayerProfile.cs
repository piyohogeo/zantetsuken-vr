#if ZANTETSU_MOBPLAN_PROFILE && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>Opt-in actual Player measurement. No frame lifecycle or planner settings are bypassed.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class MobPlanPlayerProfile : MonoBehaviour
    {
        private string directory, scenario;
        private double duration, launched, readyAt = -1, previousFrame, warmupPlan = -1;
        private MobPlanCrowd crowd;
        private MobPlanPlayerInput input;
        private int errors, minLive = 20, maxExpired, expiredFrames, cutAttempts, cutRequests;
        private double distance, lastCut = 15;
        private Vector3 previousPosition;
        private readonly List<string> cycles = new List<string>();
        private readonly List<string> frames = new List<string>();
        private readonly List<string> cuts = new List<string>();
        private readonly List<SandboxNpcCharacter> cutCharacters = new List<SandboxNpcCharacter>();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string key, string fallback)
            { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
            string output = Arg("-mobPlanProfile", null);
            if (output == null) return;
            var host = new GameObject("MobPlan Player measurement").AddComponent<MobPlanPlayerProfile>();
            host.directory = output;
            host.scenario = Arg("-mobPlanScenario", "moving");
            host.duration = double.Parse(Arg("-mobPlanSeconds", "120"), Inv);
            host.launched = Time.realtimeSinceStartupAsDouble;
            host.crowd = FindFirstObjectByType<MobPlanCrowd>();
            host.input = FindFirstObjectByType<MobPlanPlayerInput>();
            if (host.input != null) host.input.liveInput = false;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 90;
            Application.runInBackground = true;
            Directory.CreateDirectory(output);
            Application.logMessageReceived += host.OnLog;
            File.WriteAllText(Path.Combine(output, "environment.txt"),
                $"unity={Application.unityVersion}\nbackend=IL2CPP\ndevelopment={Debug.isDebugBuild}\ncpu={SystemInfo.processorType}\nlogicalProcessors={SystemInfo.processorCount}\ngpu={SystemInfo.graphicsDeviceName}\napi={SystemInfo.graphicsDeviceType}\nos={SystemInfo.operatingSystem}\nresolution={Screen.width}x{Screen.height}\nxrActive={UnityEngine.XR.XRSettings.isDeviceActive}\ntargetFps=90\nvSync=0\nscenario={host.scenario}\nwarmupSeconds=10\nmeasuredSeconds={host.duration}\n");
        }
        private void OnLog(string text, string stack, LogType type)
        { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) errors++; }

        private void Update()
        {
            double wall = Time.realtimeSinceStartupAsDouble;
            if (readyAt < 0)
            {
                if (wall - launched > 180) { Finish("load timeout"); return; }
                if (crowd == null || !crowd.IsReady) return;
                readyAt = previousFrame = wall;
                previousPosition = input.player.transform.position;
                crowd.ProfileCompleted += RecordCycle;
                Debug.Log("MOBPLAN PROFILE READY");
            }
            double elapsed = wall - readyAt;
            if (scenario != "stationary")
            {
                // Repeatable input; the map's ordinary occupancy/sliding decides actual movement.
                double phase = elapsed % 24;
                input.Submit(phase < 16 ? 1 : -.5f, phase < 8 ? .12f : phase < 16 ? -.12f : .25f, Time.deltaTime);
            }
            distance += Vector3.Distance(previousPosition, input.player.transform.position);
            previousPosition = input.player.transform.position;
            if (elapsed >= 10)
            {
                if (warmupPlan < 0) warmupPlan = crowd.PlanTime;
                minLive = Math.Min(minLive, crowd.LiveCount);
                int expired = crowd.ExpiredPlanCount;
                maxExpired = Math.Max(maxExpired, expired);
                if (expired > 0) expiredFrames++;
                frames.Add(FormattableString.Invariant($"{elapsed:F6},{crowd.PlanTime:F6},{(wall - previousFrame) * 1000:F4},{crowd.LiveCount},{expired},{(crowd.PlannerBusy ? 1 : 0)}"));
            }
            previousFrame = wall;
            if (scenario == "cutting" && elapsed >= lastCut && elapsed < duration && crowd.PlannerBusy)
            {
                lastCut = elapsed + 20;
                RequestCut(elapsed);
            }
            if (elapsed >= duration + 10) Finish(null);
        }
        private void RecordCycle(MobPlanCrowd.ProfileCycle c)
        {
            // Keep warmup rows too; the summary explicitly filters by request time.
            var m = c.metrics;
            cycles.Add(FormattableString.Invariant($"{Time.realtimeSinceStartupAsDouble - readyAt:F6},{c.requested:F6},{c.completed:F6},{c.queue:F4},{c.compute:F4},{c.collect:F4},{c.cpu:F4},{(c.accepted ? 1 : 0)},{(m?.timeout == true ? 1 : 0)},{m?.wallMilliseconds ?? 0:F4},{m?.candidateMilliseconds ?? 0:F4},{m?.compatibilityMilliseconds ?? 0:F4},{m?.assignmentMilliseconds ?? 0:F4},{m?.expansions ?? 0},{m?.searchTimeouts ?? 0},{m?.candidatesTotal ?? 0},{m?.assignmentChanges ?? 0},{m?.lodNear ?? 0},{m?.lodMid ?? 0},{m?.lodFar ?? 0},{m?.lodSkipped ?? 0},{m?.blockedAgents ?? 0},{m?.localDeadlocks ?? 0}"));
        }
        private void RequestCut(double elapsed)
        {
            var target = FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.IsTarget && c.Handle != null && c.Handle.IsReady && !c.Handle.IsWithdrawn && !cutCharacters.Contains(c));
            if (target == null) { cuts.Add($"{elapsed.ToString(Inv)},no_target"); return; }
            target.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyNow(null, 0);
            var mesh = new Mesh();
            target.Renderer.BakeMesh(mesh);
            float y = mesh.bounds.center.y;
            Destroy(mesh);
            cutAttempts++;
            var result = target.Handle.TryCut(new float4(0, 1, 0, -y), (float3)target.Renderer.transform.position);
            if (result.Outcome == VpCharacterCutOutcome.Requested || result.Outcome == VpCharacterCutOutcome.Held)
            { cutRequests++; cutCharacters.Add(target); }
            cuts.Add(FormattableString.Invariant($"{elapsed:F4},{result.Outcome},{target.Handle.LastFailure}"));
            Debug.Log("MOBPLAN PROFILE CUT " + cuts[cuts.Count - 1]);
        }
        private void Finish(string failure)
        {
            enabled = false;
            if (crowd != null) crowd.ProfileCompleted -= RecordCycle;
            int retired = cutCharacters.Count(c => c == null || c.Handle.IsWithdrawn);
            bool pass = failure == null && errors == 0 && crowd.FailedCycles == 0 && crowd.PublishedCycles >= duration / 2
                && maxExpired == 0 && crowd.LiveCount == 20 && (scenario != "cutting" || (cutRequests >= 3 && retired == cutRequests));
            File.WriteAllLines(Path.Combine(directory, "cycles.csv"), new[] { "wall_elapsed,requested,completed,queue_ms,compute_ms,collect_ms,cpu_ms,accepted,timeout,planner_ms,candidate_ms,compatibility_ms,assignment_ms,expansions,search_timeouts,candidates,changes,lod_near,lod_mid,lod_far,lod_skipped,blocked,deadlocks" }.Concat(cycles));
            File.WriteAllLines(Path.Combine(directory, "frames.csv"), new[] { "wall_elapsed,plan_time,frame_ms,live,expired,busy" }.Concat(frames));
            File.WriteAllLines(Path.Combine(directory, "cuts.csv"), new[] { "wall_elapsed,outcome,failure" }.Concat(cuts));
            string summary = FormattableString.Invariant($"passed={pass}\nfailure={failure}\nerrors={errors}\npublished={crowd?.PublishedCycles}\nstale={crowd?.StaleCycles}\nfailed={crowd?.FailedCycles}\nlive={crowd?.LiveCount}\nminLive={minLive}\nexpiredFrames={expiredFrames}\nmaxExpired={maxExpired}\ncutAttempts={cutAttempts}\ncutRequests={cutRequests}\nretired={retired}\ntravelMeters={distance:F3}\nloadWallSeconds={readyAt - launched:F3}\nwarmupEndPlanTime={warmupPlan:F6}\nendPlanTime={crowd?.PlanTime:F6}\nwallSeconds={Time.realtimeSinceStartupAsDouble - readyAt:F6}\n");
            File.WriteAllText(Path.Combine(directory, "summary.txt"), summary);
            Debug.Log("MOBPLAN PROFILE END " + summary);
            Application.logMessageReceived -= OnLog;
            Application.Quit(pass ? 0 : 1);
        }
    }
}
#endif
