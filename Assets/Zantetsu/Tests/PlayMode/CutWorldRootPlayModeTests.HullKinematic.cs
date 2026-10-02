using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The always-kinematic mode (TL, 2026-09-30; an Editor prototype): a building is one kinematic body, one hull and one
    /// enabled collider for good; a hit cuts the display alone and the upper side drops by a short fixed animation; the
    /// hull is exchanged best-effort and kept when the update fails, goes stale or the world ends. The trial's clock is
    /// the physics clock here. Each case writes the display's counts (members held, members with a geometry, the ledger's
    /// history) and times (the classification, the publication, the drops' placement, the snapshot) apart from the physics'.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const float KinematicDropSeconds = 0.25f, DropHorizontal = 0.15f, DropVertical = 0.02f;

        private CutWorldRoot NewKinematicWorld(System.Action<CutWorldRoot> beforeAwake = null, System.Action<CutWorldProfile> more = null)
        {
            CutWorldRoot root = NewHullWorld(0.5f, profile =>
            {
                SetPrivate(profile, "buildingHullKinematicDisplay", true);
                SetPrivate(profile, "buildingHullAnimationSeconds", KinematicDropSeconds);
                SetPrivate(profile, "buildingHullDropHorizontalMetres", DropHorizontal);
                SetPrivate(profile, "buildingHullDropVerticalMetres", DropVertical);
                more?.Invoke(profile);
            }, beforeAwake: beforeAwake);
            BuildingHullFusion h = root.Hulls;
            Assert.That(h.Settings.kinematicDisplay, Is.True, "the mode read back");
            Assert.That(h.Settings.animationSeconds, Is.EqualTo(KinematicDropSeconds).Within(1e-6));
            Assert.That(h.Settings.dropHorizontalMetres, Is.EqualTo(DropHorizontal).Within(1e-6));
            Assert.That(h.Settings.dropVerticalMetres, Is.EqualTo(DropVertical).Within(1e-6));
            h.RealSecondsForTest = () => CutPhysicsStep.Clock.PhysicsSeconds;
            return root;
        }

        /// <summary>One body, one hull and one enabled collider a building (the floor apart), each kinematic, none of them the rest's.</summary>
        private void AssertOneEach(CutWorldRoot root, int buildings, string when)
        {
            BuildingHullFusion h = root.Hulls;
            int bodies = 0, colliders = 0;
            foreach (Rigidbody b in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)) { bodies++; Assert.That(b.isKinematic, Is.True, when + ": body " + b.name + " kinematic"); }
            foreach (Collider c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None)) if (c.gameObject != _hullFloor && c.enabled) colliders++;
            Assert.That(bodies, Is.EqualTo(buildings), when + ": one body a building");
            Assert.That(colliders, Is.EqualTo(buildings), when + ": one enabled collider a building");
            Assert.That(h.GroupCount, Is.EqualTo(buildings), when + ": one group a building");
            Assert.That(h.LiveHulls, Is.EqualTo(buildings), when + ": one hull a building");
            foreach (HullGroup g in h.Groups) Assert.That(g.Kinematic && g.Body.isKinematic && !root.Rest.IsTracked(g.Body), Is.True, when + ": group " + g.Id + " kinematic and not the rest's");
        }

        /// <summary>Frames until a condition, one body/hull/collider a building checked at every frame, the snapshot's time sampled.</summary>
        private IEnumerator UntilKinematic(CutWorldRoot root, int buildings, System.Func<bool> condition, float seconds, string what, List<double> snapshots, ProfilerRecorder snapshot)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until)
            {
                yield return null;
                AssertOneEach(root, buildings, what + " (a frame on the way)");
                if (snapshot.Valid && snapshot.LastValue > 0) snapshots.Add(snapshot.LastValue / 1e6);
            }

            if (!condition())
            {
                WriteHullRecord(root, what + ": timed out after " + seconds + " s; " + root.Hulls.DescribeUnsettled());
                Assert.Fail(what + ": within " + seconds + " s (" + root.Hulls.DescribeUnsettled() + ")");
            }
        }

        private static string KinematicLine(CutWorldRoot root, string label, List<double> snapshots)
        {
            BuildingHullFusion h = root.Hulls;
            h.CountDisplay(out int held, out int committed, out int empty, out int pending, out int fragments, out int operations);
            string line = label + " | display: members held " + held + " (committed " + committed + ", empty " + empty + ", not yet " + pending + "), ledger history " + fragments + " fragments / " + operations + " operations, display cuts " + h.DisplayCuts
                + " | ms: classification " + (h.PrepareDisplaySeconds * 1000).ToString("F3") + ", publication " + (h.PublishDisplaySeconds * 1000).ToString("F3") + ", drops' placement " + (h.AnimationSeconds * 1000).ToString("F3") + ", snapshot " + Stats(snapshots)
                + " | drops: started " + h.AnimationsStarted + ", completed " + h.AnimationsCompleted + ", stopped by a re-cut " + h.AnimationsStoppedByReCut + ", running " + h.AnimationsRunning
                + " | hull updates: begun " + h.HullUpdatesBegun + ", exchanged " + h.HullUpdatesAdopted + ", refused " + h.HullUpdatesRefused + ", stale " + h.HullUpdatesStale + ", cancelled " + h.HullUpdatesCancelled + "; requests made " + h.HullRequestsMade + ", skipped " + h.HullRequestsSkipped + ", dropped " + h.HullRequestsDropped + ", waiting " + h.WaitingHullRequests + " (most running " + h.MaxHullUpdatesInFlight + ", most waiting " + h.MaxWaitingHullRequests + ")"
                + " | hull Main " + (h.HullUpdateMainSeconds * 1000).ToString("F3") + " ms, worker " + (h.HullUpdateWorkerSeconds * 1000).ToString("F3") + " ms | Step max " + (h.MaxStepSeconds * 1000).ToString("F3") + " ms";
            TestContext.Out.WriteLine(line);
            foreach (string r in h.HullUpdateRecords) TestContext.Out.WriteLine("  " + r);
            foreach (BuildingHullFusion.HullHit hit in h.Hits) TestContext.Out.WriteLine("  hit " + hit.id + ": slash " + hit.slashId + " => " + (hit.outcome ?? "PENDING") + " | hull: " + (hit.hullOutcome ?? "-") + " (display operations " + hit.displayOperations.Count + ")");
            return line;
        }

        /// <summary>The member that moved most from a pose (its Root's world move).</summary>
        private static HullGroup.DisplayMember MovedMember(HullGroup g, Vector3 origin)
        {
            HullGroup.DisplayMember pick = null;
            foreach (HullGroup.DisplayMember m in g.Members) if (m.root != null && (pick == null || (m.root.transform.position - origin).sqrMagnitude > (pick.root.transform.position - origin).sqrMagnitude)) pick = m;
            return pick;
        }

        /// <summary>One child moved by the expected world move (in the plane: its dot with the normal is zero), the other stayed.</summary>
        private static void AssertOneMoved(HullGroup g, Vector3 origin, Vector3 expected, Vector3 normal, string what)
        {
            var moves = new List<Vector3>();
            foreach (HullGroup.DisplayMember m in g.Members) if (m.root != null) moves.Add(m.root.transform.position - origin);
            moves.Sort((a, b) => b.sqrMagnitude.CompareTo(a.sqrMagnitude));
            TestContext.Out.WriteLine(what + ": the members' moves " + string.Join(", ", moves.ConvertAll(x => x.ToString("F5"))) + "; expected " + expected.ToString("F5") + ", the move along the normal " + Vector3.Dot(moves[0], normal.normalized).ToString("E2"));
            Assert.That(moves.Count, Is.EqualTo(2), what + ": two children");
            Assert.That((moves[0] - expected).magnitude, Is.LessThan(1e-4f), what + ": one moved by " + expected.ToString("F4"));
            Assert.That(Mathf.Abs(Vector3.Dot(moves[0], normal.normalized)), Is.LessThan(1e-5f), what + ": in the cut plane (d.n = 0)");
            Assert.That(moves[1].magnitude, Is.LessThan(1e-5f), what + ": the other stayed");
        }

        /// <summary>The moved child is the one above the plane: the operation's positive child when the plane's normal points up, its negative child when it points down.</summary>
        private static void AssertMovedIsUpper(CutWorldRoot root, HullGroup g, Vector3 origin, BuildingHullFusion.HullHit hit, bool normalUp, string what)
        {
            Assert.That(hit.displayOperations.Count, Is.EqualTo(1), what + ": one display operation");
            Assert.That(root.Ledger.TryGetOperation(hit.displayOperations[0], out LogicalCutOperation record), Is.True);
            LogicalFragmentId upper = normalUp ? record.positive : record.negative;
            Assert.That(MovedMember(g, origin).fragment, Is.EqualTo(upper), what + ": the moved child is the one above the plane (" + (normalUp ? "positive" : "negative") + ")");
        }

        private static HullGroup.DisplayMember MemberAbove(HullGroup g, bool highest)
        {
            HullGroup.DisplayMember pick = null;
            foreach (HullGroup.DisplayMember m in g.Members)
            {
                if (m.root == null) continue;
                if (pick == null || (highest ? m.root.transform.position.y > pick.root.transform.position.y : m.root.transform.position.y < pick.root.transform.position.y)) pick = m;
            }

            return pick;
        }

        /// <summary>
        /// **A horizontal cut: the display is cut and its upper side drops by the horizontal distance, the building stays one
        /// kinematic body, hull and collider, and the hull is exchanged.** The hit is Pending, then Published by the display
        /// (its operation committed); the upper child drops 0.15 m over 0.25 s, the lower one stays; the hull's update is
        /// exchanged (generation 2) with its top lowered by the drop; at every frame one body, one hull, one enabled collider.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_HorizontalCut_DropsTheUpperSide_AndExchangesTheHull()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            AssertOneEach(root, 1, "registered");
            Mesh before = building.Collider.sharedMesh;
            Vector3 origin = building.Members[0].root.transform.position;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "the hit is pending");
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 1, 30f, "published by the display", snapshots, snapshot);
            Assert.That(h.Hits[0].outcome, Is.EqualTo("Published"), "the hit's outcome is the display's");
            Assert.That(h.Hits[0].hullOutcome, Is.Not.Null, "the hull's apart");
            Assert.That(building.MemberCount, Is.EqualTo(2), "two display children under the one group");
            Assert.That(h.AnimationsRunning, Is.EqualTo(1), "the upper side drops");
            yield return UntilKinematic(root, 1, () => h.AnimationsCompleted == 1 && h.HullUpdatesAdopted == 1 && h.IsSettled, 30f, "the drop done and the hull exchanged", snapshots, snapshot);
            AssertOneMoved(building, origin, new Vector3(DropHorizontal, 0f, 0f), Vector3.up, "the horizontal cut (along the sweep's travel, +X)");
            AssertMovedIsUpper(root, building, origin, h.Hits[0], true, "the horizontal cut");
            StringAssert.Contains("the sweep's travel", h.Hits[0].slideRule);
            Assert.That(building.HullGeneration, Is.EqualTo(2), "one exchange");
            Assert.That(building.Collider.sharedMesh, Is.Not.SameAs(before), "the collider carries the new hull");
            building.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
            TestContext.Out.WriteLine("the exchanged hull's bounds " + lo + " .. " + hi);
            Assert.That(hi.x, Is.EqualTo(1f + DropHorizontal).Within(1e-3f), "its side moved out by the slide");
            Assert.That(hi.y, Is.EqualTo(1f).Within(1e-3f), "its top where it was (the slide is in the plane)");
            Assert.That(lo.y, Is.EqualTo(-1f).Within(1e-3f), "its base where it was");
            foreach (CutOperationId op in h.Hits[0].displayOperations) Assert.That(root.Geometry.StageOf(op), Is.EqualTo(CutGeometryStage.Committed), "the display operation committed");
            KinematicLine(root, "horizontal cut", snapshots);
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The drop's side does not depend on the normal's sign; near vertical it is the side away from the anchors.** The
        /// rule on its own: a normal and its opposite pick the same side of space and the same distance (horizontal, tilted,
        /// near vertical with anchors on one side, with members uneven, even); the distance runs from 2 cm (vertical) to 15
        /// cm (horizontal). Then two buildings cut by a level plane whose normal points up for one and down for the other:
        /// both drop their upper child; a vertical cut beside the anchor drops the far side by 2 cm.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_TheDropsSide_IsTheSameForAFlippedNormal_AndAwayFromTheAnchorsNearVertical()
        {
            var normals = new[] { new float3(0f, 1f, 0f), math.normalize(new float3(1f, 1f, 0f)), math.normalize(new float3(1f, 0.1f, 0f)), math.normalize(new float3(0.3f, 0.05f, 1f)) };
            foreach (float3 n in normals)
            {
                foreach ((int ap, int an, int mp, int mn) in new[] { (0, 1, 3, 3), (1, 0, 2, 5), (0, 0, 2, 5), (0, 0, 3, 3) })
                {
                    BuildingHullFusion.ChooseDrop(n, ap, an, mp, mn, DropHorizontal, DropVertical, out bool pos, out float d, out string rule);
                    BuildingHullFusion.ChooseDrop(-n, an, ap, mn, mp, DropHorizontal, DropVertical, out bool neg, out float d2, out string rule2);
                    Assert.That(pos, Is.EqualTo(!neg), "n " + n + " anchors " + ap + "/" + an + " members " + mp + "/" + mn + ": the same side of space (" + rule + " / " + rule2 + ")");
                    Assert.That(d, Is.EqualTo(d2).Within(1e-7f));
                    Assert.That(d, Is.EqualTo(DropVertical + (DropHorizontal - DropVertical) * math.abs(n.y)).Within(1e-6f), "interpolated by |n.up|");
                    if (math.abs(n.y) >= BuildingHullFusion.NearVerticalCos) Assert.That(pos, Is.EqualTo(n.y > 0f), "the side above");
                    else if ((ap > 0) != (an > 0)) Assert.That(pos, Is.EqualTo(an > 0), "near vertical: away from the anchors");
                }
            }

            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup up = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup down = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup side = AddHullBuilding(root, new Vector3(40f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            Vector3 o1 = up.Members[0].root.transform.position, o2 = down.Members[0].root.transform.position, o3 = side.Members[0].root.transform.position;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            detector.Evaluate(new[] { new SlashSweep(2, 0.0, false, new Plane(Vector3.down, 0.2f), Vector3.right, Vector3.forward, new Vector3(17f, 0.2f, -3f), new Vector3(17f, 0.2f, 3f), new Vector3(23f, 0.2f, -3f), new Vector3(23f, 0.2f, 3f)) }, new long[] { 2 });
            Evaluate(detector, Upright(3, 40.3f), 3);
            yield return UntilKinematic(root, 3, () => h.GroupCuts == 3 && h.AnimationsCompleted == 3 && h.IsSettled, 30f, "three cuts dropped and settled", snapshots, snapshot);
            AssertOneMoved(up, o1, new Vector3(DropHorizontal, 0f, 0f), Vector3.up, "normal up");
            AssertOneMoved(down, o2, new Vector3(DropHorizontal, 0f, 0f), Vector3.down, "normal down (the same move)");
            AssertMovedIsUpper(root, up, o1, h.Hits[0], true, "normal up");
            AssertMovedIsUpper(root, down, o2, h.Hits[1], false, "normal down");
            AssertOneMoved(side, o3, new Vector3(0f, -DropVertical, 0f), Vector3.right, "vertical beside the anchor: down the plane by 2 cm");
            HullGroup.DisplayMember far = MovedMember(side, o3);
            Assert.That(far.root.transform.position.x - o3.x, Is.EqualTo(0f).Within(1e-5f), "not across the plane");
            KinematicLine(root, "the drop's side", snapshots);
            AssertOneEach(root, 3, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A re-cut in the middle of a drop stops it at the pose it has and cuts from there.** A level cut starts the upper
        /// side's drop; five steps later (about half way) a second Slash on the building is accepted: the first drop stops
        /// where it is -- neither back at its start nor on at its end -- and stays there; the second cut's children are made
        /// at that pose; the second drop runs from it.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_AReCutMidDrop_StopsItWhereItIs_AndCutsFromThatPose()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            Vector3 origin = building.Members[0].root.transform.position;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 1, 30f, "the first cut", snapshots, snapshot);
            yield return UntilKinematic(root, 1, () => root.Geometry != null && h.DisplayOperationsOpen == 0, 30f, "its display committed", snapshots, snapshot);
            yield return Steps(3);
            HullGroup.DisplayMember upper = MovedMember(building, origin);
            Assert.That(h.AnimationsRunning, Is.EqualTo(1), "still dropping");
            Evaluate(detector, Upright(2, 0.1f), 2);
            Assert.That(detector.HitCount, Is.GreaterThanOrEqualTo(1));
            yield return UntilKinematic(root, 1, () => h.AnimationsStoppedByReCut == 1, 30f, "the first drop stopped by the re-cut", snapshots, snapshot);
            Vector3 frozenMove = upper.root != null ? upper.root.transform.position - origin : new Vector3(float.NaN, 0f, 0f);
            float frozen = frozenMove.x;
            // The stop's pose is on the drop's curve at the time applied (TL, 2026-10-01): the slide's share and the arc of gravity there.
            BuildingHullFusion.DropRecord first = h.DropRecords[0];
            Vector3 onCurve = BuildingHullFusion.DropPosition(Vector3.zero, first.deltaLocal, first.gravityLocal, first.seconds, first.appliedSeconds);
            TestContext.Out.WriteLine("the first drop stopped at " + frozenMove.ToString("R") + " (t " + first.appliedSeconds.ToString("R") + " s, u " + first.Phase.ToString("R") + "; on the curve " + onCurve.ToString("R") + ")");
            Assert.That(first.end, Is.EqualTo("stopped"));
            Assert.That(frozen, Is.GreaterThan(1e-3f).And.LessThan(DropHorizontal - 1e-3f), "stopped part way: neither the start nor the end");
            Assert.That((frozenMove - onCurve).magnitude, Is.LessThan(1e-5f), "on its curve: the slide's share and the arc at the time applied");
            Assert.That(frozenMove.y, Is.GreaterThan(1e-4f), "above the plane by the arc (g down: 1/2 g t (t - T) is up between 0 and T; the height it had is kept)");
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 2, 30f, "the re-cut published", snapshots, snapshot);
            yield return UntilKinematic(root, 1, () => h.IsSettled, 30f, "settled", snapshots, snapshot);
            // The upper child was crossed by the upright plane: its children stand at the frozen pose, one of them then 2 cm down its plane.
            var moves = new List<Vector3>();
            foreach (HullGroup.DisplayMember m in building.Members) if (m.root != null) moves.Add(m.root.transform.position - origin);
            TestContext.Out.WriteLine("the members' moves at the end " + string.Join(", ", moves.ConvertAll(x => x.ToString("F5"))));
            Assert.That(moves.Exists(v => (v - frozenMove).magnitude < 1e-4f), Is.True, "a child of the re-cut stayed at the frozen pose (its height kept)");
            Assert.That(moves.Exists(v => (v - (frozenMove + new Vector3(0f, -DropVertical, 0f))).magnitude < 1e-4f), Is.True, "the other ran a new curve from the frozen pose and ended 2 cm down the upright plane (no arc left)");
            Assert.That(h.AnimationsStoppedByReCut, Is.EqualTo(1));
            KinematicLine(root, "re-cut mid drop", snapshots);
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Ten short consecutive cuts: one body, hull and collider at every frame; the hull updates coalesced, none piled
        /// up; every hit answered by the display.** Cuts every 3 steps (shorter than a drop), planes turning, through the
        /// building's current hull.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_TenShortCuts_OneOfEach_AtEveryFrame_UpdatesCoalesced()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            int cuts = 0;
            for (int i = 0; i < 10; i++)
            {
                float3 c = CentreOf(building);
                long slash = 10 + i;
                SlashSweep sweep = (i % 3) switch
                {
                    0 => Upright(slash, c.x + 0.15f * ((i % 2) * 2 - 1), c.y),
                    1 => Level(slash, c.y + 0.1f * ((i % 4) - 1.5f), c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f),
                    _ => Wide(slash, math.normalize(new float3(1f, 1f, 0.3f)), (Vector3)c, math.normalize(new float3(1f, -1f, 0f)), Vector3.forward),
                };
                Evaluate(detector, sweep, slash);
                cuts += detector.HitCount;
                long from = CutPhysicsStep.Clock.StepId;
                yield return UntilKinematic(root, 1, () => CutPhysicsStep.Clock.StepId - from >= 3, 20f, "cut " + i + ": three steps", snapshots, snapshot);
            }

            yield return UntilKinematic(root, 1, () => h.IsSettled, 60f, "settled after ten short cuts", snapshots, snapshot);
            KinematicLine(root, "ten short cuts", snapshots);
            WriteHullRecord(root, "ten short cuts (always kinematic)");
            Assert.That(h.HitsPending, Is.Zero, "every hit answered");
            Assert.That(h.GroupCuts, Is.GreaterThanOrEqualTo(5), "the display cut");
            Assert.That(h.HullUpdatesBegun + h.HullRequestsSkipped, Is.EqualTo(h.HullRequestsMade), "every request run or skipped");
            Assert.That(h.MaxHullUpdatesInFlight <= 1 && h.MaxWaitingHullRequests <= 1, Is.True, "one running and one waiting at most");
            Assert.That(h.HullUpdatesAdopted, Is.GreaterThanOrEqualTo(1), "the hull followed");
            Assert.That(h.WaitingHullRequests + h.HullUpdatesInFlight, Is.Zero, "nothing left waiting");
            Assert.That(h.AnimationsCompleted + h.AnimationsStoppedByReCut, Is.EqualTo(h.AnimationsStarted), "every drop finished or stopped by a re-cut");
            Assert.That(h.DisplayOperationsOpen == 0 && h.DisplayOperationsFailed == 0, Is.True, "every display operation committed");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A refused hull update: the display cut and its drop go on, the old hull stays.** Every update's scan is refused:
        /// two cuts are published by the display and drop; the collider keeps its mesh, the generation stays 1, the updates
        /// are refused and their cuts dropped; one body, hull and collider.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_HullUpdateRefused_TheDisplayGoesOn_TheOldHullStays()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            Mesh before = building.Collider.sharedMesh;
            h.refuseHullForTest = true;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilKinematic(root, 1, () => h.IsSettled && h.HullUpdatesRefused >= 1, 30f, "the first cut, its update refused", snapshots, snapshot);
            Evaluate(detector, Upright(2, 0.3f), 2);
            yield return UntilKinematic(root, 1, () => h.IsSettled && h.GroupCuts == 2 && h.HullUpdatesRefused >= 2, 30f, "the second cut, its update refused", snapshots, snapshot);
            KinematicLine(root, "hull updates refused", snapshots);
            Assert.That(h.Hits[0].outcome == "Published" && h.Hits[1].outcome == "Published", Is.True, "both hits published by the display");
            Assert.That(h.AnimationsCompleted, Is.EqualTo(2), "both drops done");
            Assert.That(building.Collider.sharedMesh, Is.SameAs(before), "the old hull's mesh kept");
            Assert.That(building.HullGeneration, Is.EqualTo(1));
            Assert.That(h.HullUpdatesAdopted, Is.Zero);
            Assert.That(h.HullRequestsDropped, Is.EqualTo(2), "the refused updates' requests dropped");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Cuts while the worker is held: the display goes on, one request waits, the older ones are skipped, and the
        /// updates end in a finite count once released.** The dispatcher refuses every offer: the first cut's update is made
        /// and waits to run; five more cuts are published by the display and drop meanwhile, each one's request replacing the
        /// one waiting (one running and one waiting throughout, the replaced ones counted as skipped). Let through: the running
        /// update is exchanged, then the one waiting -- two updates for six cuts -- and nothing is left.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_CutsWhileTheWorkerIsHeld_OneRunningOneWaiting_TheDisplayGoesOn()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            h.refuseOffersForTest = int.MaxValue;
            var sweeps = new[] { Level(1, 0.2f, -3f, 3f), Upright(2, 0.3f), Upright(3, -0.3f), Level(4, -0.4f, -3f, 3f), Upright(5, 0.6f), Level(6, 0.6f, -3f, 3f) };
            for (int i = 0; i < sweeps.Length; i++)
            {
                Evaluate(detector, sweeps[i], i + 1);
                int cuts = i + 1;
                yield return UntilKinematic(root, 1, () => h.Hits.Count >= cuts && !h.Hits[cuts - 1].IsPending && h.DisplayOperationsOpen == 0, 30f, "cut " + cuts + " answered by the display while the worker is held", snapshots, snapshot);
                Assert.That(h.HullUpdatesInFlight, Is.EqualTo(1), "cut " + cuts + ": one update running (held)");
                Assert.That(h.WaitingHullRequests, Is.EqualTo(cuts == 1 ? 0 : 1), "cut " + cuts + ": one request waiting at most");
                Assert.That(h.HullRequestsSkipped, Is.EqualTo(math.max(0, cuts - 2)), "cut " + cuts + ": the older requests waiting skipped");
            }

            Assert.That(h.GroupCuts, Is.EqualTo(6), "the display cut six times while the hull waited");
            Assert.That(building.HullGeneration, Is.EqualTo(1), "the hull not yet exchanged");
            h.refuseOffersForTest = 0;
            yield return UntilKinematic(root, 1, () => h.IsSettled, 30f, "the updates done once released", snapshots, snapshot);
            KinematicLine(root, "cuts while the worker is held", snapshots);
            Assert.That(h.HullUpdatesBegun, Is.EqualTo(2), "two updates for six cuts");
            Assert.That(h.HullRequestsSkipped, Is.EqualTo(4), "four requests skipped");
            Assert.That(h.HullUpdatesAdopted, Is.EqualTo(2));
            Assert.That(building.HullGeneration, Is.EqualTo(3));
            Assert.That(h.MaxHullUpdatesInFlight <= 1 && h.MaxWaitingHullRequests <= 1, Is.True, "one running and one waiting throughout");
            Assert.That(h.WaitingHullRequests + h.HullUpdatesInFlight, Is.Zero);
            foreach (BuildingHullFusion.HullHit hit in h.Hits) Assert.That(hit.outcome, Is.EqualTo("Published").Or.StartWith("NoChange"), "hit " + hit.id + ": the display's outcome stands");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>**A stale candidate is given back and the next update is exchanged.** The first update's generation is moved under it: at its exchange it is stale, its cut waits again, and the next update, made from the hull as it is, is exchanged.</summary>
        [UnityTest]
        public IEnumerator HullKinematic_AStaleCandidate_IsGivenBack_AndTheNextIsExchanged()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            h.bumpHullGenerationForTest = true;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilKinematic(root, 1, () => h.IsSettled && h.HullUpdatesAdopted == 1, 30f, "the stale update given back, the next exchanged", snapshots, snapshot);
            KinematicLine(root, "a stale candidate", snapshots);
            Assert.That(h.HullUpdatesStale, Is.EqualTo(1));
            Assert.That(h.HullUpdatesBegun, Is.EqualTo(2), "made again from the hull as it is");
            Assert.That(building.HullGeneration, Is.EqualTo(3), "the test's move and the exchange");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>**The end while a drop runs and a hull update waits gives everything back.** The update's offer is refused for good; the world ends in the middle of the drop: no body, collider or hull mesh left.</summary>
        [UnityTest]
        public IEnumerator HullKinematic_TheEndWhileADropRunsAndAnUpdateWaits_GivesEverythingBack()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.refuseOffersForTest = int.MaxValue;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilWithin(() => h.GroupCuts == 1, 30f, "published");
            yield return Steps(2);
            Assert.That(h.AnimationsRunning, Is.EqualTo(1), "the drop runs");
            Assert.That(h.HullUpdatesInFlight, Is.EqualTo(1), "an update waits for the dispatcher");
            AssertOneEach(root, 1, "before the end");
            yield return EndWorld(root);
            Assert.That(Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length, Is.Zero);
            Assert.That(Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length, Is.Zero);
            Assert.That(MeshesNamed("Building hull"), Is.Zero, "no hull mesh left");
        }
    }
}
