using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The always-kinematic building's cut limit (TL, 2026-10-01): N display geometries (live, non-empty, committed display
    /// fragments), the drop D(n) = D0 (1 - log2 n / log2 N)^p from the settled count n a cut goes on from. A building that
    /// reaches N -- exactly or past it in one cut -- is cut no more: its last cut runs to its end (display commit, drop, hull
    /// update, refused or not), and no later sweep cuts it. A cut while the last one's display is still committing waits
    /// and goes on from the settled n; same-frame hits and several held requests cannot slip past N; another building goes
    /// on; a re-cut mid-drop below N still stops the drop where it is. The formula's values, the flipped normal, the move in
    /// the plane, and the display's and the hull's same move.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const double LimitP = 1.0, LimitD0 = 0.5;

        private CutWorldRoot NewLimitWorld(int limit, double p = LimitP, double d0 = LimitD0)
        {
            CutWorldRoot root = NewKinematicWorld(more: profile =>
            {
                SetPrivate(profile, "buildingHullGeometryLimit", limit);
                SetPrivate(profile, "buildingHullDropExponent", (float)p);
                SetPrivate(profile, "buildingHullDropBaseMetres", (float)d0);
            });
            Assert.That(root.Hulls.Settings.LimitOn && root.Hulls.Settings.geometryLimit == limit, Is.True, "the limit read back");
            return root;
        }

        private static bool Quiet(BuildingHullFusion h) => h.IsSettled && h.DisplayOperationsOpen == 0 && h.AnimationsRunning == 0 && h.HullUpdatesInFlight == 0 && h.WaitingHullRequests == 0 && h.CutsInProgress == 0;

        private static void WriteLimit(BuildingHullFusion h, string what)
        {
            TestContext.Out.WriteLine(what + ":");
            foreach (string r in h.LimitRecords) TestContext.Out.WriteLine("  " + r);
            foreach (BuildingHullFusion.HullHit hit in h.Hits) TestContext.Out.WriteLine("  hit " + hit.id + " slash " + hit.slashId + " => " + (hit.outcome ?? "PENDING") + " (n before " + hit.geometriesBefore + ", after " + hit.geometriesAfter + ", distance " + hit.limitDistance.ToString("R") + ", display operations " + hit.displayOperations.Count + ", hull: " + (hit.hullOutcome ?? "-") + ")");
        }

        private static double D(int n, int limit) => LimitD0 * System.Math.Pow(1.0 - System.Math.Log(n) / System.Math.Log(limit), LimitP);

        // One update of a wave in the plane z = at, its span along y, travelling along x: it crosses every piece the level and the
        // upright (x = const) cuts made, which all still span the box's z.
        private static SlashSweep Across(long slash, float at)
        {
            return new SlashSweep(slash, 0.0, false, new Plane(Vector3.forward, -at), Vector3.right, Vector3.up,
                new Vector3(-3f, -3f, at), new Vector3(-3f, 3f, at), new Vector3(3f, -3f, at), new Vector3(3f, 3f, at));
        }

        /// <summary>**N - 1 to N: the last cut runs to its end, and the next sweep cuts nothing.** N = 4: 1 -> 2 -> 4.</summary>
        [UnityTest]
        public IEnumerator HullLimit_ReachingN_TheLastCutEnds_AndNothingCutsAgain()
        {
            CutWorldRoot root = NewLimitWorld(4);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(root, () => h.GroupCuts == 1 && Quiet(h), 30f, "the first cut ended");
            Assert.That(h.Hits[0].geometriesBefore, Is.EqualTo(1));
            Assert.That(h.Hits[0].geometriesAfter, Is.EqualTo(2));
            Assert.That(h.Hits[0].limitDistance, Is.EqualTo(D(1, 4)).Within(1e-9), "D(1) = D0");
            Assert.That(math.length(h.Hits[0].slideWorld), Is.EqualTo((float)D(1, 4)).Within(1e-5f), "the display's and the hull's move is D(n)");
            Assert.That(math.abs(math.dot(h.Hits[0].slideWorld, h.Hits[0].normalWorld)), Is.LessThan(1e-5f), "in the plane");
            Assert.That(h.IsCutStopped(building.Building), Is.False);

            Evaluate(detector, Upright(2, 0.3f), 2);
            yield return UntilHull(root, () => h.GroupCuts == 2 && Quiet(h), 30f, "the second cut ended (display, drop and hull)");
            WriteLimit(h, "N - 1 to N");
            Assert.That(h.Hits[1].geometriesBefore, Is.EqualTo(2));
            Assert.That(h.Hits[1].geometriesAfter, Is.EqualTo(4), "the cut applied to every geometry it crossed");
            Assert.That(h.Hits[1].limitDistance, Is.EqualTo(D(2, 4)).Within(1e-9));
            Assert.That(h.IsCutStopped(building.Building), Is.True, "N reached: stopped");
            Assert.That(h.AnimationsCompleted + h.AnimationsStoppedByReCut, Is.EqualTo(h.AnimationsStarted), "the last drop ran to its end");
            Assert.That(h.HullUpdatesAdopted + h.HullRequestsDropped, Is.GreaterThanOrEqualTo(1), "the last hull update ended");
            foreach (CutOperationId op in h.Hits[1].displayOperations) Assert.That(root.Geometry.StageOf(op), Is.EqualTo(CutGeometryStage.Committed));

            int cuts = h.GroupCuts, hits = h.Hits.Count;
            Evaluate(detector, Upright(3, -0.3f), 3);
            Assert.That(detector.HitCount, Is.Zero, "a stopped building is no target");
            yield return Steps(10);
            Assert.That(h.GroupCuts == cuts && h.Hits.Count == hits, Is.True, "nothing cut again");
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>**Past N in one cut: applied to every geometry it crosses, nothing cut short; then stopped.** N = 3: 1 -> 2 -> 4.</summary>
        [UnityTest]
        public IEnumerator HullLimit_PastNInOneCut_IsAppliedWhole_ThenStops()
        {
            CutWorldRoot root = NewLimitWorld(3);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(root, () => h.GroupCuts == 1 && Quiet(h), 30f, "the first cut ended");
            Evaluate(detector, Upright(2, 0.3f), 2);
            yield return UntilHull(root, () => h.GroupCuts == 2 && Quiet(h), 30f, "the second cut ended");
            WriteLimit(h, "past N in one cut");
            Assert.That(h.Hits[1].limitDistance, Is.EqualTo(D(2, 3)).Within(1e-9));
            Assert.That(h.Hits[1].displayOperations.Count, Is.EqualTo(2), "both geometries crossed were cut");
            Assert.That(h.Hits[1].geometriesAfter, Is.EqualTo(4), "past N = 3: nothing cut short");
            Assert.That(h.IsCutStopped(building.Building), Is.True);
            Evaluate(detector, Upright(3, -0.3f), 3);
            Assert.That(detector.HitCount, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A cut while the last one's display is still committing waits, and goes on from the settled n; same-frame hits
        /// and several held requests cannot slip past N.** N = 4: a level cut; the moment it is published (its display
        /// operations still open) a second Slash is held and resumes from n = 2; then three Slashes in one evaluation: one
        /// goes on (2 -> 4), the two held end with the limit's reason once n settles at 4.
        /// </summary>
        [UnityTest]
        public IEnumerator HullLimit_AHeldCutGoesOnFromTheSettledCount_AndNoHeldRequestSlipsPastN()
        {
            CutWorldRoot root = NewLimitWorld(8);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(root, () => h.GroupCuts == 1, 30f, "the first cut published");
            bool open = h.DisplayOperationsOpen > 0;
            TestContext.Out.WriteLine("at the first cut's publication its display operations open: " + h.DisplayOperationsOpen);
            Evaluate(detector, Across(2, 0.1f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            if (open) Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Held), "held while the last display commits");
            yield return UntilHull(root, () => h.GroupCuts == 2 && Quiet(h), 30f, "the held cut went on and ended");
            Assert.That(h.Hits[1].geometriesBefore, Is.EqualTo(2), "from the settled n, not the count at its acceptance");
            Assert.That(h.Hits[1].geometriesAfter, Is.EqualTo(4));

            // Three Slashes in one evaluation, n = 4 < 8: one goes on (4 -> 8, it crosses every piece), the other two are held and end at the stop.
            detector.Evaluate(new[] { Upright(3, 0.3f), Upright(4, -0.4f), Upright(5, 0.6f) }, new long[] { 3, 4, 5 });
            var answers = new List<ProvisionalCutAcceptance>();
            for (int i = 0; i < detector.HitCount; i++) answers.Add(detector.HitAt(i).Acceptance);
            TestContext.Out.WriteLine("three in one evaluation: " + string.Join(", ", answers));
            Assert.That(answers.FindAll(a => a == ProvisionalCutAcceptance.Pending).Count, Is.EqualTo(1));
            Assert.That(answers.FindAll(a => a == ProvisionalCutAcceptance.Held).Count, Is.EqualTo(2));
            yield return UntilHull(root, () => h.GroupCuts == 3 && Quiet(h) && h.HeldNow == 0, 30f, "the one cut ended and the held ones were answered");
            WriteLimit(h, "held and same-frame");
            Assert.That(h.IsCutStopped(building.Building), Is.True, "n = 8: stopped");
            Assert.That(h.HeldEndedByLimit, Is.EqualTo(2), "the two held requests ended by the stop");
            Assert.That(h.GroupCuts, Is.EqualTo(3), "nothing slipped past N");
            int ended = 0;
            foreach (BuildingHullFusion.HullHit hit in h.Hits) if (hit.outcome != null && hit.outcome.Contains("cut limit")) ended++;
            Assert.That(ended, Is.EqualTo(2), "each with the limit's reason");
            Assert.That(h.StopRecords.ContainsKey(building.Building), Is.True);
            TestContext.Out.WriteLine("stop: " + h.StopRecords[building.Building]);
            yield return EndWorld(root);
        }

        /// <summary>**Stopped with its hull update refused: the old hull kept, the last drop ended, and still no target.**</summary>
        [UnityTest]
        public IEnumerator HullLimit_StoppedWithItsHullUpdateRefused_StaysStopped()
        {
            CutWorldRoot root = NewLimitWorld(2);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.refuseHullForTest = true;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(root, () => h.GroupCuts == 1 && Quiet(h), 30f, "the cut ended, its hull update refused");
            Assert.That(h.HullUpdatesRefused, Is.GreaterThanOrEqualTo(1), "refused");
            Assert.That(h.AnimationsCompleted, Is.EqualTo(1), "the drop ran to its end");
            Assert.That(h.IsCutStopped(building.Building), Is.True, "n = 2 = N: stopped");
            h.refuseHullForTest = false;
            Evaluate(detector, Upright(2, 0.3f), 2);
            Assert.That(detector.HitCount, Is.Zero, "the refused update gives no way back");
            yield return Steps(10);
            Assert.That(h.GroupCuts, Is.EqualTo(1));
            yield return EndWorld(root);
        }

        /// <summary>**Another building goes on when one is stopped.**</summary>
        [UnityTest]
        public IEnumerator HullLimit_AnotherBuildingGoesOn()
        {
            CutWorldRoot root = NewLimitWorld(2);
            BuildingHullFusion h = root.Hulls;
            HullGroup a = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup b = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(root, () => h.GroupCuts == 1 && Quiet(h), 30f, "building A cut");
            Assert.That(h.IsCutStopped(a.Building) && !h.IsCutStopped(b.Building), Is.True, "A stopped, B not");
            Evaluate(detector, Level(2, 0.2f, 17f, 23f), 2);
            yield return UntilHull(root, () => h.GroupCuts == 2 && Quiet(h), 30f, "building B cut");
            Assert.That(h.Hits[h.Hits.Count - 1].outcome, Is.EqualTo("Published"), "B goes on");
            yield return EndWorld(root);
        }

        /// <summary>**A re-cut mid-drop below N still stops the drop where it is** (N = 16).</summary>
        [UnityTest]
        public IEnumerator HullLimit_AReCutMidDropBelowN_StillStopsTheDrop()
        {
            CutWorldRoot second = NewLimitWorld(16);
            BuildingHullFusion h2 = second.Hulls;
            HullGroup building = AddHullBuilding(second, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector2 = new SlashHitDetector(second, in k_hitSettings);
            yield return null;
            Evaluate(detector2, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(second, () => h2.GroupCuts == 1, 30f, "the first cut");
            yield return UntilHull(second, () => h2.DisplayOperationsOpen == 0, 30f, "its display committed");
            yield return Steps(3);
            Assert.That(h2.AnimationsRunning, Is.EqualTo(1), "still dropping");
            Evaluate(detector2, Upright(2, 0.1f), 2);
            yield return UntilHull(second, () => h2.AnimationsStoppedByReCut == 1, 30f, "the drop stopped by the re-cut");
            Assert.That(h2.LastStopFraction, Is.GreaterThan(0.0).And.LessThan(1.0), "part way");
            yield return UntilHull(second, () => h2.GroupCuts == 2 && Quiet(h2), 30f, "the re-cut ended");
            Assert.That(h2.Hits[1].geometriesBefore, Is.EqualTo(2));
            Assert.That(h2.Hits[1].limitDistance, Is.EqualTo(D(2, 16)).Within(1e-9));
            yield return EndWorld(second);
        }

        /// <summary>**The formula's values; the settings' validity; a flipped normal drops the same side by the same D(n).**</summary>
        [UnityTest]
        public IEnumerator HullLimit_TheFormula_TheSettings_AndAFlippedNormal()
        {
            double[] expected = { 0.5, 0.375, 0.25, 0.125 };
            int[] counts = { 1, 2, 4, 8 };
            for (int i = 0; i < counts.Length; i++) Assert.That(BuildingHullFusion.LimitDistance(counts[i], 16, 1.0, 0.5), Is.EqualTo(expected[i]).Within(1e-12), "D(" + counts[i] + ")");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => BuildingHullFusion.LimitDistance(16, 16, 1.0, 0.5), "n = N is no cut");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => BuildingHullFusion.LimitDistance(0, 16, 1.0, 0.5), "n = 0 is no cut");
            BuildingHullSettings S(int n, double p, double d0, bool kinematic = true) => new BuildingHullSettings(true, 0.5, 0.002, kinematicDisplay: kinematic, geometryLimit: n, dropExponent: p, dropBaseMetres: d0);
            Assert.That(S(16, 1.0, 0.5).IsValid && S(2, 0.5, 0.1).IsValid && S(0, 1.0, 0.5).IsValid, Is.True);
            Assert.That(S(1, 1.0, 0.5).IsValid || S(16, 0.0, 0.5).IsValid || S(16, double.NaN, 0.5).IsValid || S(16, double.PositiveInfinity, 0.5).IsValid
                || S(16, 1.0, 0.0).IsValid || S(16, 1.0, double.NaN).IsValid || S(16, 1.0, 0.5, false).IsValid, Is.False, "N >= 2, p finite above 0, D0 above 0, the always-kinematic mode only");

            CutWorldRoot root = NewLimitWorld(16);
            BuildingHullFusion h = root.Hulls;
            HullGroup up = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup down = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Vector3 o1 = up.Members[0].root.transform.position, o2 = down.Members[0].root.transform.position;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            detector.Evaluate(new[] { new SlashSweep(2, 0.0, false, new Plane(Vector3.down, 0.2f), Vector3.right, Vector3.forward, new Vector3(17f, 0.2f, -3f), new Vector3(17f, 0.2f, 3f), new Vector3(23f, 0.2f, -3f), new Vector3(23f, 0.2f, 3f)) }, new long[] { 2 });
            yield return UntilHull(root, () => h.GroupCuts == 2 && Quiet(h), 30f, "both cut and settled");
            AssertOneMoved(up, o1, new Vector3((float)D(1, 16), 0f, 0f), Vector3.up, "normal up: D(1) along the travel");
            AssertOneMoved(down, o2, new Vector3((float)D(1, 16), 0f, 0f), Vector3.down, "normal down: the same move");
            AssertMovedIsUpper(root, up, o1, h.Hits[0], true, "normal up");
            AssertMovedIsUpper(root, down, o2, h.Hits[1], false, "normal down");
            yield return EndWorld(root);
        }
    }
}
