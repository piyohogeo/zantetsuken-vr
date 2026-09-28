using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Sandbox.Editor
{
    // Diagnosis only (the MobPlanSlash unit, planner allocation; not part of any build): the MobPlan crowd's own planning
    // work -- its load, then its steady cycles -- run on a worker thread of its own in the Editor (Mono), with the managed
    // allocation read around each step on that worker, the way MobPlanCrowd's work items run them: the load (dataset,
    // walkable map, tiles, simulation, pose bank), then per cycle AcknowledgeExternal, PrepareExternal, the config clone,
    // LocomotionPlanner.RunCycle and the crowd's relocation check and published plans. The crowd's conditions are copied:
    // 20 NPCs of MobPlanPreset.Scenario, the planner preset, one cycle a second, the player walking slowly and turning now
    // and then, and one individual retired from the baseline every third cycle (a cut). Mono's amounts are for finding the
    // sources; they are not IL2CPP's.
    //   Unity.exe -batchmode -projectPath . -executeMethod Zantetsu.Sandbox.Editor.MobPlanAllocationDiagnosis.Run
    //     -zantetsuDiagnosisOut <dir> -logFile <dir>\editor.log -quit
    public static class MobPlanAllocationDiagnosis
    {
        private const string AssetsPath = "Assets/Licensed/MobPlan/MobPlanAssets.asset";

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-zantetsuDiagnosisOut");
            string outDir = at >= 0 && at + 1 < args.Length ? args[at + 1] : Path.GetFullPath("Temp/MobPlanAllocationDiagnosis");
            int cycles = 60;
            int c = Array.IndexOf(args, "-zantetsuDiagnosisCycles");
            if (c >= 0 && c + 1 < args.Length) cycles = int.Parse(args[c + 1]);
            Directory.CreateDirectory(outDir);
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) throw new InvalidOperationException("no " + AssetsPath);
            byte[] data = assets.dataset.bytes, polygons = assets.polygons.bytes;
            byte[][] tables = assets.poseTables.Select(t => t.bytes).ToArray();

            var text = new StringBuilder();
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { Measure(data, polygons, tables, cycles, text); }
                catch (Exception e) { failure = e; }
            }, 16 * 1024 * 1024) { Name = "MobPlan allocation diagnosis" };
            worker.Start();
            worker.Join();
            File.WriteAllText(Path.Combine(outDir, "mobplan-planner-alloc.txt"), text.ToString());
            Debug.Log("MOBPLAN ALLOC DIAGNOSIS\n" + text);
            if (failure != null) throw failure;
        }

        // The per-thread counter reads 0 in Unity's Mono. The heap in use is read instead: the main thread only waits for
        // this worker, so what grows is the worker's. The heap is first grown and collected, so a step allocating some MB
        // does not set off a collection; a step during which one ran is marked and left out of the figures.
        private static long Now() => GC.GetTotalMemory(false);
        private static int Collections() => GC.CollectionCount(0);

        private static void Measure(byte[] data, byte[] polygons, byte[][] tables, int cycles, StringBuilder text)
        {
            // Room: a large heap, collected, so the steps below run without a collection.
            var room = new byte[768 * 1024 * 1024];
            room[room.Length - 1] = 1;
            room = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // The counter itself, on this thread: one large array, and many small ones.
            long probe = Now();
            var calibration = new byte[1_000_000];
            long calibrated = Now() - probe;
            var small = new List<int[]>();
            long probe2 = Now();
            for (int i = 0; i < 1000; i++) small.Add(new int[250]);
            long calibratedSmall = Now() - probe2;
            text.AppendLine("thread " + Thread.CurrentThread.ManagedThreadId + " (worker) calibration: a 1,000,000-byte array read as " + calibrated
                            + " bytes; 1000 arrays of 250 ints (about 1,000,000 bytes of data) read as " + calibratedSmall + " bytes; collections so far "
                            + Collections() + (calibration.Length > 0 && small.Count > 0 ? "" : ""));

            // The load, as MobPlanCrowd's load work.
            int load0 = Collections();
            long t0 = Now();
            ClipDataset dataset = ClipDataset.Read(data);
            long t1 = Now();
            PolygonWalkable map = PolygonWalkable.Read(polygons, -220, -170, 300, 300);
            long t2 = Now();
            map.BuildAllTiles();
            long t3 = Now();
            var simulation = new LocomotionSimulation(dataset, MobPlanPreset.Planner()) { AutoPlan = false, ExternalPlayer = true };
            simulation.Reset(MobPlanPreset.Scenario(null), map);
            long t4 = Now();
            var bank = new MobPoseBank(dataset, tables);
            long t5 = Now();
            text.AppendLine($"load: dataset {t1 - t0:N0} B, walkable map {t2 - t1:N0} B, tiles {t3 - t2:N0} B, simulation {t4 - t3:N0} B, pose bank {t5 - t4:N0} B, total {t5 - t0:N0} B"
                            + $" (agents {simulation.Agents.Count}, clips {dataset.Count}, tables {bank.Tables.Count}; collections during the load {Collections() - load0})");

            // One goal field and one search by themselves: what a new field and a cycle's search object cost.
            int fieldsBefore = simulation.GoalFieldCount;
            long f0 = Now();
            GoalField oneField = simulation.FieldFor(simulation.FocusX + 37.3f, simulation.FocusZ - 21.9f, MobPlanPreset.Scenario(null).npcRadius, simulation.FocusX, simulation.FocusZ);
            long f1 = Now();
            var oneSearch = new SingleAgentSearch(dataset, map);
            long f2 = Now();
            text.AppendLine($"one new goal field: {f1 - f0:N0} B (fields {fieldsBefore} -> {simulation.GoalFieldCount}, reachable cells {oneField?.ReachableCells}); "
                            + $"one SingleAgentSearch: {f2 - f1:N0} B; map {map.WorldWidth:F0} x {map.WorldHeight:F0} m, field cell {simulation.Config.goalFieldCellMeters} m"
                            + (oneSearch != null ? "" : ""));

            // The crowd's published plans, as it starts.
            int generation = 0;
            var published = simulation.Agents.ToDictionary(a => a.Id, a => new PublishedMobPlan(a.Plan, generation));
            float px = simulation.FocusX, pz = simulation.FocusZ, yaw = 0f;
            CycleResult accepted = null;
            var steps = new[] { "acknowledge", "prepare", "config", "runCycle", "relocated", "plans", "total" };
            var bytes = steps.ToDictionary(s => s, s => new List<long>());
            var phases = new Dictionary<string, List<long>>();
            var rows = new StringBuilder();
            rows.AppendLine("cycle,now,agents,published,status,collections,fieldsBuiltInPrepare,fieldsBuiltInRunCycle,fieldsCached,candidates,expansions,evaluated,regenCandidates,pairChecks,skipped,"
                            + string.Join(",", steps));
            float now = 0f;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                now += 1f;
                // The player: 0.42 m/s along its heading, turning 36 degrees every 8 s, staying where it can walk.
                float speed = 0.42f;
                if (cycle % 8 == 7) yaw += 36f;
                float nx = px + speed * Mathf.Sin(yaw * Mathf.Deg2Rad), nz = pz + speed * Mathf.Cos(yaw * Mathf.Deg2Rad);
                if (map.IsWalkableAt(nx, nz)) { px = nx; pz = nz; } else yaw += 90f;
                float vx = speed * Mathf.Sin(yaw * Mathf.Deg2Rad), vz = speed * Mathf.Cos(yaw * Mathf.Deg2Rad);

                // A cut every third cycle: that individual leaves the baseline, as a retired actor leaves the crowd.
                if (cycle % 3 == 2 && published.Count > 0) published.Remove(published.Keys.First());
                var baseline = new Dictionary<int, PublishedMobPlan>(published);

                int gc0 = Collections();
                int fields0 = simulation.GoalFieldCount;
                long a0 = Now();
                if (accepted != null) simulation.AcknowledgeExternal(accepted);
                long a1 = Now();
                PlanningSnapshot snapshot = simulation.PrepareExternal(now, baseline, px, pz, vx, vz, yaw);
                long a2 = Now();
                int fields1 = simulation.GoalFieldCount;
                PlannerConfig config = simulation.Config.Clone();
                config.cycleBudgetMilliseconds = Math.Min(config.cycleBudgetMilliseconds, 1900);
                long a3 = Now();
                // RunCycle's phases were split once with temporary marks inside it (planner-alloc-4); those marks are gone.
                var cyclePhases = new Dictionary<string, long>();
                CycleResult result = LocomotionPlanner.RunCycle(simulation.Dataset, simulation.Map, config, snapshot);
                long a4 = Now();
                int fields2 = simulation.GoalFieldCount;
                var moved = new List<int>();
                long a5 = a4, a6 = a4;
                if (result.Published)
                {
                    foreach (var agent in snapshot.Agents)
                        if (!baseline.TryGetValue(agent.Id, out var old) ||
                            (old.TryResolve(now, simulation.Dataset, out _, out _, out var p0) &&
                             Pose2.Distance(p0, AgentPlan.PoseAt(simulation.Dataset, agent.Plan.Segments, now)) > .1f)) moved.Add(agent.Id);
                    a5 = Now();
                    generation++;
                    int g = generation;
                    var plans = result.NewPlans.ToDictionary(pair => pair.Key, pair => new PublishedMobPlan(pair.Value, g));
                    a6 = Now();
                    foreach (var pair in plans) published[pair.Key] = pair.Value;
                    accepted = result;
                }

                long[] values = { a1 - a0, a2 - a1, a3 - a2, a4 - a3, a5 - a4, a6 - a5, a6 - a0 };
                int collected = Collections() - gc0;
                if (collected == 0) for (int s = 0; s < steps.Length; s++) bytes[steps[s]].Add(values[s]);
                if (collected == 0 && cycle >= 3)
                    foreach (var pair in cyclePhases) { if (!phases.TryGetValue(pair.Key, out var list)) phases[pair.Key] = list = new List<long>(); list.Add(pair.Value); }
                CycleMetrics m = result.Metrics;
                rows.AppendLine(string.Join(",", cycle, now.ToString("F1"), snapshot.Agents.Count, result.Published ? 1 : 0,
                    (m.status ?? "").Replace(",", ";"), collected, fields1 - fields0, fields2 - fields1, fields2, m.candidatesTotal, m.expansions,
                    m.successorsEvaluated, m.regenerationCandidates, m.pairChecks, m.agentsSkipped, string.Join(",", values)));
            }

            string Stat(List<long> v, int skip)
            {
                var s = v.Skip(skip).OrderBy(x => x).ToList();
                return s.Count == 0 ? "none" : $"median {s[s.Count / 2]:N0} p90 {s[(int)(0.9 * (s.Count - 1))]:N0} max {s[s.Count - 1]:N0}";
            }

            text.AppendLine($"cycles {cycles}; the first 3 are the start, the rest the steady cycles; cycles left out for a collection: {cycles - bytes["total"].Count}");
            foreach (string s in steps)
            {
                text.AppendLine($"  {s,-11} first3 [{string.Join(" ", bytes[s].Take(3).Select(x => x.ToString("N0")))}]  steady {Stat(bytes[s], 3)}");
            }

            text.AppendLine("RunCycle by phase (steady cycles, bytes; marks placed for the diagnosis):");
            foreach (var pair in phases) text.AppendLine($"  {pair.Key,-32} {Stat(pair.Value, 0)} sum {pair.Value.Sum():N0}");
            text.AppendLine("per cycle:");
            text.Append(rows);
        }
    }
}
