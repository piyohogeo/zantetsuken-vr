using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The cost comparison of the building fusion (2026-09-29): the same shapes and placement -- boxes on a floor, each
    /// cut into eight pieces on the product's cut path -- with the fusion off (the kinematic rest alone) and on. What is
    /// measured, over the same physical time once everything rests: Rigidbodies, colliders, awake bodies, the simulate
    /// time, the display snapshot's time, the fusion's own preparation and switch time, and then the Main time of one
    /// re-cut's publication in each mode, the memory held, and what is left after the ending. Written to
    /// <c>cost-&lt;mode&gt;.txt</c> in the run's own directory named in ZTK_FUSION_COST_OUT, as a new file (not over an existing one);
    /// without it the measurement is only in the test's output (2026-10-02: no fixed directory).
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string FusionCostOutputVariable = "ZTK_FUSION_COST_OUT";

        private CutWorldRoot NewCostWorld(bool fusion)
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile =>
            {
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingWorldEnabled", false);
                SetPrivate(profile, "buildingFusionEnabled", fusion);
                SetPrivate(profile, "buildingFusionPerFrame", 8);
                SetPrivate(profile, "maxIncompleteCuts", 64);
            });
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(200f, 1f, 200f);
            return root;
        }

        private static int CountRigidbodies() => UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;

        private static int CountColliders() => UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;

        private static int CountAwake()
        {
            int awake = 0;
            foreach (Rigidbody b in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!b.isKinematic && !b.IsSleeping()) awake++;
            }

            return awake;
        }

        private static int CountNamed(string prefix)
        {
            int n = 0;
            foreach (GameObject go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go.name.StartsWith(prefix)) n++;
            }

            return n;
        }

        [UnityTest, Timeout(900000)]
        public IEnumerator FusionCost_FortyBoxesInEightPieces_FusionOffAndOn([Values(false, true)] bool fusion)
        {
            var log = new List<string>();
            void Line(string s) { log.Add(s); TestContext.Out.WriteLine(s); }
            string mode = fusion ? "fusion on" : "fusion off";
            Line("mode " + mode + ": 40 boxes in a grid, each cut into 8 on the cut path, resting on a floor; timeout 1 s");
            long memoryStart = Profiler.GetTotalAllocatedMemoryLong();
            int meshesStart = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            CutWorldRoot root = NewCostWorld(fusion);
            var buildings = new List<LogicalFragmentId>();
            for (int i = 0; i < 40; i++)
            {
                buildings.Add(AddBuilding(root, new Vector3((i % 8) * 3f, 0f, (i / 8) * 3f)));
            }

            yield return null;
            // Eight pieces each: y = 0, then x = 0 of both halves, then z = 0 of the four quarters (planes in the box's frame).
            var pieces = new List<LogicalFragmentId>();
            var cutWatch = System.Diagnostics.Stopwatch.StartNew();
            int cuts = 0;
            foreach (LogicalFragmentId b in buildings)
            {
                var halves = new LogicalFragmentId[2];
                yield return CutInTwo(root, b, new float4(0f, 1f, 0f, 0f), halves); cuts++;
                var quarters = new List<LogicalFragmentId>();
                foreach (LogicalFragmentId h in halves)
                {
                    var q = new LogicalFragmentId[2];
                    yield return CutInTwo(root, h, new float4(1f, 0f, 0f, 0f), q); cuts++;
                    quarters.AddRange(q);
                }

                foreach (LogicalFragmentId q in quarters)
                {
                    var e = new LogicalFragmentId[2];
                    yield return CutInTwo(root, q, new float4(0f, 0f, 1f, 0f), e); cuts++;
                    pieces.AddRange(e);
                }
            }

            cutWatch.Stop();
            Line("cuts " + cuts + " in " + cutWatch.Elapsed.TotalSeconds.ToString("F1") + " s wall; pieces " + pieces.Count);

            // Everything rests: held by the rest, and (fusion on) fused.
            float deadline = Time.realtimeSinceStartup + 120f;
            if (fusion)
            {
                // A cut of a fused member is the group's cut and crosses every member the plane meets, so the pieces are
                // more than the ones this test asked for; "all fused" is every live fragment being a member and nothing awake.
                var live = new List<LogicalFragmentId>();
                int Unfused() { live.Clear(); root.Owners.CopyFragmentsTo(live); int n = 0; foreach (LogicalFragmentId f in live) if (!root.Owners.TryGet(f, out PhysicsFragmentOwner o) || !o.IsFused) n++; return n; }
                yield return Until(() => Unfused() == 0 && CountAwake() == 0 || Time.realtimeSinceStartup > deadline, "all fused");
                Line("live fragments " + live.Count + " (asked for " + pieces.Count + "; the group cuts crossed more), unfused " + Unfused() + ", fused pieces (gave up a body) " + root.Fusion.FusedPieces + ", groups " + root.Fusion.GroupCount + ", group cuts so far " + root.Fusion.GroupCuts + " (crossed " + root.Fusion.MembersSplit + ", finals " + root.Fusion.FinalsPublished + "); fuse Main total " + (root.Fusion.FuseSeconds * 1000).ToString("F2") + " ms, max per turn " + (root.Fusion.MaxFuseSeconds * 1000).ToString("F3") + " ms");
            }
            else
            {
                yield return Until(() => root.Rest.RestedNow >= pieces.Count || Time.realtimeSinceStartup > deadline, "all held");
                Line("held " + root.Rest.RestedNow + " of " + pieces.Count);
            }

            yield return PhysicsSeconds(0.5);
            long memoryResting = Profiler.GetTotalAllocatedMemoryLong();

            // Measured over 120 frames at rest.
            using (var simulate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Simulate", 256))
            using (var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot", 256))
            using (var collect = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Collect", 256))
            using (var restTurn = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.BuildingRest.Step", 256))
            {
                double simSum = 0, simMax = 0, snapSum = 0, snapMax = 0, colSum = 0, colMax = 0, restSum = 0, restMax = 0; int frames = 0, stepped = 0;
                long lastStep = CutPhysicsStep.Clock.StepId;
                for (int i = 0; i < 120; i++)
                {
                    yield return null;
                    frames++;
                    if (CutPhysicsStep.Clock.StepId != lastStep) { stepped++; lastStep = CutPhysicsStep.Clock.StepId; }
                    double sim = simulate.LastValue / 1e6, snap = snapshot.LastValue / 1e6, col = collect.LastValue / 1e6, rest = restTurn.LastValue / 1e6;
                    simSum += sim; simMax = Math.Max(simMax, sim); snapSum += snap; snapMax = Math.Max(snapMax, snap); colSum += col; colMax = Math.Max(colMax, col); restSum += rest; restMax = Math.Max(restMax, rest);
                }

                Line("at rest over " + frames + " frames (" + stepped + " stepped): Rigidbodies " + CountRigidbodies() + ", MeshColliders " + CountColliders() + ", awake bodies " + CountAwake()
                    + ", render fragments " + root.Display.RenderFragmentCount + ", branches " + root.Display.BranchCount
                    + "; simulate ms mean " + (simSum / frames).ToString("F3") + " max " + simMax.ToString("F3") + "; display snapshot ms mean " + (snapSum / frames).ToString("F3") + " max " + snapMax.ToString("F3")
                    + "; collect ms mean " + (colSum / frames).ToString("F3") + " max " + colMax.ToString("F3") + "; rest turn ms mean " + (restSum / frames).ToString("F3") + " max " + restMax.ToString("F3")
                    + "; memory " + (memoryResting / 1048576.0).ToString("F1") + " MB (start " + (memoryStart / 1048576.0).ToString("F1") + ")");
            }

            // One re-cut of a piece of the first box, vertical through its middle: the group's cut (fusion on) or the ordinary Provisional cut.
            for (int round = 0; round < 3; round++)
            {
                LogicalFragmentId target = pieces[round * 8];
                if (!root.Owners.TryGet(target, out PhysicsFragmentOwner targetOwner) || targetOwner.IsWithdrawn) { Line("round " + round + ": the target is gone"); continue; }
                int rigidbodiesBefore = CountRigidbodies();
                using (var driver = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Driver.Update", 256))
                using (var lateDriver = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Driver.LateUpdate", 256))
                using (var fusionStep = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.BuildingFusion.Step", 256))
                {
                    ProvisionalCutAsk ask = Ask(target, new float4(1f, 0f, 0f, -0.25f));
                    Assert.That(root.TryAsk(in ask), Is.True, "round " + round + ": asked");
                    double driverMax = 0, lateMax = 0, fusionMax = 0; int watched = 0;
                    List<CutOperationId> admitted = null;
                    for (int i = 0; i < 90; i++)
                    {
                        yield return null;
                        watched++;
                        driverMax = Math.Max(driverMax, driver.LastValue / 1e6); lateMax = Math.Max(lateMax, lateDriver.LastValue / 1e6); fusionMax = Math.Max(fusionMax, fusionStep.LastValue / 1e6);
                        if (admitted == null || admitted.Count == 0) admitted = AdmittedFor(root, new[] { target });   // the admission comes with the publication, a Step or more after the ask (the preparation)
                        if (admitted.Count > 0 && root.Geometry.StageOf(admitted[0]) == CutGeometryStage.Committed && (root.Fusion == null || root.Fusion.FinalsPublished + root.Fusion.FinalsFailed >= round + 1 && !root.Owners.TryGet(target, out _)))
                        {
                            break;
                        }
                    }

                    string fusionLine = root.Fusion != null
                        ? "; group cut publish Main max " + (root.Fusion.MaxPublishSeconds * 1000).ToString("F3") + " ms (" + root.Fusion.MaxCutMembers + " members), preparations " + root.Fusion.PreparationsMade + "/" + root.Fusion.PreparationsPublished + "/" + root.Fusion.PreparationsRefused + "/" + root.Fusion.PreparationsStale + "/" + root.Fusion.PreparationsAbandoned + " re-offered " + root.Fusion.PreparationsReoffered + " in flight " + root.Fusion.PreparationsInFlight + ", refused " + root.Fusion.GroupCutsRefused + ", members classified " + root.Fusion.MembersClassified + ", crossed " + root.Fusion.MembersSplit + ", finals " + root.Fusion.FinalsPublished + " failed " + root.Fusion.FinalsFailed + ", final Main max " + (root.Fusion.MaxFinalSeconds * 1000).ToString("F3") + " ms, mass max " + (root.Fusion.MaxMassSeconds * 1000).ToString("F3") + " ms (" + root.Fusion.MaxMassMembers + " members), groups " + root.Fusion.GroupCount + ", fusion step max " + fusionMax.ToString("F3") + " ms"
                        : "";
                    Line("round " + round + ": re-cut of piece " + target.value + " admitted " + (admitted != null ? admitted.Count : 0) + ", watched " + watched + " frames; Driver.Update max " + driverMax.ToString("F3") + " ms, Driver.LateUpdate max " + lateMax.ToString("F3") + " ms; Rigidbodies " + rigidbodiesBefore + " -> " + CountRigidbodies() + ", MeshColliders " + CountColliders() + ", awake " + CountAwake() + fusionLine);
                }

                // Rest again before the next round.
                deadline = Time.realtimeSinceStartup + 60f;
                if (fusion) yield return Until(() => root.Fusion.GroupCount <= 40 && CountAwake() == 0 || Time.realtimeSinceStartup > deadline, "round " + round + ": rested and fused again");
                else yield return Until(() => CountAwake() == 0 || Time.realtimeSinceStartup > deadline, "round " + round + ": rested again");
                yield return PhysicsSeconds(0.3);
                Line("round " + round + " settled: Rigidbodies " + CountRigidbodies() + ", MeshColliders " + CountColliders() + ", awake " + CountAwake() + (fusion ? ", groups " + root.Fusion.GroupCount + ", fused pieces " + root.Fusion.FusedPieces + ", merged " + root.Fusion.GroupsMerged : ", held " + root.Rest.RestedNow) + ", memory " + (Profiler.GetTotalAllocatedMemoryLong() / 1048576.0).ToString("F1") + " MB");
            }

            if (fusion)
            {
                WriteFusionRecord(root, "end of the rounds");
            }

            yield return EndWorld(root);
            for (int i = 0; i < 10; i++) yield return null;
            var byName = new Dictionary<string, int>();
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>()) { string k = string.IsNullOrEmpty(m.name) ? "(unnamed)" : m.name; byName[k] = byName.TryGetValue(k, out int c) ? c + 1 : 1; }
            var names = new List<KeyValuePair<string, int>>(byName); names.Sort((x, y) => y.Value.CompareTo(x.Value));
            Line("meshes after the ending by name: " + string.Join(", ", names.GetRange(0, Math.Min(6, names.Count)).ConvertAll(kv => kv.Key + " x" + kv.Value)));
            Line("after the ending: Rigidbodies " + CountRigidbodies() + ", MeshColliders " + CountColliders() + ", 'Fused Group' objects " + CountNamed("Fused Group") + ", 'Shadow of' objects " + CountNamed("Shadow of") + ", memory " + (Profiler.GetTotalAllocatedMemoryLong() / 1048576.0).ToString("F1") + " MB (start " + (memoryStart / 1048576.0).ToString("F1") + "), meshes " + Resources.FindObjectsOfTypeAll<Mesh>().Length + " (start " + meshesStart + ")");
            Assert.That(CountNamed("Fused Group") + CountNamed("Shadow of"), Is.Zero, "nothing of the fusion remains after the ending");
            string costDir = DiagnosisOutput.Optional(FusionCostOutputVariable);
            if (costDir != null)
            {
                DiagnosisOutput.WriteNew(Path.Combine(costDir, "cost-" + (fusion ? "on" : "off") + ".txt"), string.Join("\n", log) + "\n");
            }
        }
    }
}
