using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    // GRAPH-CONNECT-4 unit 2: the graph stage connected to the uncut split (default off). The fixture is two interleaved
    // combs: two groups of convexes joined by contact within each, apart by a gap far over the margin between them, with
    // no plane between the groups (the teeth alternate). The planes find nothing; the graph places the two combs apart.
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutSpatialSplitSettings SplitOnWithGraph(bool cutConvexes = false, int minCutSideTriangles = CutSpatialSplitSettings.DefaultMinCutSideTriangles,
            int deferFirstFrames = 8, int deferMaxFrames = 32, long workBudget = SpatialSplitGraphLimits.DefaultWorkBudget)
        {
            CutSpatialSplitSettings settings = SplitOn();
            settings.cutConvexes = cutConvexes;
            settings.minCutSideTriangles = minCutSideTriangles;
            settings.bentSearch = false;   // the baseline configuration of the verification; the bent partition is switched apart
            settings.graphSearch = true;
            SpatialSplitGraphLimits limits = SpatialSplitGraphLimits.Default;
            limits.deferFirstFrames = deferFirstFrames;
            limits.deferMaxFrames = deferMaxFrames;
            limits.workBudget = workBudget;
            settings.graphLimits = limits;
            return settings;
        }

        private static readonly float3 k_toothHalf = new float3(0.9f, 0.1f, 0.5f);

        /// <summary>
        /// Two interleaved combs: comb A's spine crosses y = 0 (the waist cut leaves its lower part with the teeth), comb B
        /// stands below y = 0 to the right; the teeth alternate in height (A at -0.35 and -0.95, B at -0.65 and -1.25) and
        /// overlap in x, so no plane separates the combs, while the least gap between them is 0.1 m. Six convexes below the waist.
        /// </summary>
        /// <summary>A box's corners turned about z through its centre: a tilted tooth (its box overlaps its neighbours' boxes while the hulls stay apart).</summary>
        private static float3[] TiltedBoxCornersAt(float3 centre, float3 half, float degrees)
        {
            float3[] corners = BoxCornersAt(float3.zero, half);
            quaternion turn = quaternion.RotateZ(math.radians(degrees));
            for (int i = 0; i < corners.Length; i++) corners[i] = math.rotate(turn, corners[i]) + centre;
            return corners;
        }

        private LogicalFragmentId AddInterleavedCombs(CutWorldRoot root, Vector3 at, IReadOnlyList<float3> anchors = null, float toothTiltDegrees = 0f)
        {
            float3[] spineA = BoxCornersAt(new float3(0f, -0.5f, 0f), new float3(0.25f, 0.7f, 0.5f));          // y in [-1.2, 0.2]: cut at the waist
            float3[] toothA1 = TiltedBoxCornersAt(new float3(1.125f, -0.35f, 0f), k_toothHalf, toothTiltDegrees);   // x in [0.225, 2.025]
            float3[] toothA2 = TiltedBoxCornersAt(new float3(1.125f, -0.95f, 0f), k_toothHalf, toothTiltDegrees);
            float3[] spineB = BoxCornersAt(new float3(3f, -0.85f, 0f), new float3(0.25f, 0.75f, 0.5f));         // x in [2.75, 3.25], y in [-1.6, -0.1]
            float3[] toothB1 = TiltedBoxCornersAt(new float3(1.875f, -0.65f, 0f), k_toothHalf, toothTiltDegrees);   // x in [0.975, 2.775]
            float3[] toothB2 = TiltedBoxCornersAt(new float3(1.875f, -1.25f, 0f), k_toothHalf, toothTiltDegrees);
            var convexes = new List<float3[]> { spineA, toothA1, toothA2, spineB, toothB1, toothB2 };
            PhysicsOwnerShape shape = NewBoxesShape(convexes);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, convexes);

            var actor = TrackActor(new GameObject("Interleaved combs"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < convexes.Count; c++)
            {
                MeshCollider collider = actor.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = shape.MeshOf(c);
            }

            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, anchors, false, out LogicalFragmentId fragment);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        /// <summary>The least gap between the two combs' boxes (the teeth's vertical spacing), against the world's margin.</summary>
        private static void AssertCombGapFarOverTheMargin(CutWorldRoot root)
        {
            float margin = CutSpatialSplit.MarginOf(root.Profile);
            const float leastGap = 0.1f;   // A1 bottom -0.45 to B1 top -0.55; B1 bottom -0.75 to A2 top -0.85; A2 bottom -1.05 to B2 top -1.15
            Assert.That(margin, Is.GreaterThan(0f));
            Assert.That(leastGap, Is.GreaterThanOrEqualTo(100f * margin), "the gap is far over 2 x margin and the error margin (margin " + margin + ")");
        }

        private static string DescribeGraphRecord(in SpatialSplitGraphRecord g)
        {
            return "graph: " + g.verdict + " (stop " + g.stop + "), nodes " + g.nodes + ", candidates " + g.candidates + ", cut attempts " + g.cutAttempts + ", GJK limit hits " + g.gjkLimitHit
                + ", work " + g.work + " (prepare " + g.workPrepare + ", search " + g.workSearch + "), native bytes " + g.nativeBytes + ", growths " + g.listGrowths
                + ", span " + g.vertexSpan + " / referenced " + g.referencedVertices + ", convexes +" + g.positiveConvexes + " -" + g.negativeConvexes
                + ", triangles +" + g.positiveTriangles + " -" + g.negativeTriangles + "; " + g.reason;
        }

        /// <summary>The engine's placement (bool arrays) hashed as the replay hashes its normalised sides (1 positive, 0 negative; the convexes then the triangles).</summary>
        private static string EngineSidesHash(bool[] convexPositive, bool[] trianglePositive)
        {
            var bytes = new byte[convexPositive.Length + trianglePositive.Length];
            for (int c = 0; c < convexPositive.Length; c++) bytes[c] = convexPositive[c] ? (byte)1 : (byte)0;
            for (int k = 0; k < trianglePositive.Length; k++) bytes[convexPositive.Length + k] = trianglePositive[k] ? (byte)1 : (byte)0;
            fixed (byte* b = bytes) return SpatialSplitReplay.SidesHash(b, bytes.Length);
        }

        private static void ResetGraphTestHooks()
        {
            CutSpatialSplit.TestGraphFailAllocation = -1;
            CutSpatialSplit.TestGraphGjkMaxIterations = 0;
            CutSpatialSplit.TestFailGraphSidesAllocation = false;
            CutSpatialSplit.TestRefuseIssue = false;
            CutSpatialSplit.TestGraphPrepareFailAllocation = -1;
            CutSpatialSplit.TestGraphShortSides = false;
            CutSpatialSplit.TestGraphRefuseDistribution = false;
            CutSpatialSplit.TestGraphFirst = false;
        }

        private IEnumerator CombsBelowTheWaist(CutWorldRoot root, Vector3 at, LogicalFragmentId[] lowerOut, IReadOnlyList<float3> anchors = null, float toothTiltDegrees = 0f)
        {
            LogicalFragmentId body = AddInterleavedCombs(root, at, anchors, toothTiltDegrees);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 6);
            lowerOut[0] = sides[1];
        }

        /// <summary>
        /// **The combs are left whole by the planes and split by the graph where they stood** (GRAPH-CONNECT-4 §5-3): the
        /// planes alone find nothing and mark the fragment; the graph switched on is another search, the mark says nothing,
        /// the graph stage enters once, places the two combs apart (three convexes a side), and the publication is the
        /// existing one -- two owners where the source stood, with its motion, no convex cut, no mesh made.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_InterleavedCombs_AreLeftByThePlanes_AndSplitByTheGraph_WhereTheyStood()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            AssertCombGapFarOverTheMargin(root);
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            LogicalFragmentId lower = lowers[0];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate));
            Assert.That(split.GraphEffective, Is.False, "the graph is off in the settings");

            // 1. The planes alone: no plane between interleaved combs, and the fragment is marked.
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the plane search ran and found none");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.EqualTo(1));
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed));
            Assert.That(split.GraphSearches, Is.Zero, "the graph stage was not entered: it is off");
            Assert.That(split.LastGraph.Entered, Is.False);

            // 2. The graph on: another search, so the mark says nothing; the main thread prepared the engine.
            split.Settings = SplitOnWithGraph();
            Assert.That(split.GraphPrepared, Is.True, "prepared on the main thread when the graph was asked for");
            Assert.That(split.GraphEffective, Is.True, split.GraphRefusal);
            Assert.That(SpatialSplitGraph.IsPrepared, Is.True);
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the mark was made under another search");
            lowerOwner.Body.useGravity = false;
            lowerOwner.Body.linearVelocity = new Vector3(0.3f, 0f, 1f);
            lowerOwner.Body.angularVelocity = new Vector3(0f, 0.5f, 0f);
            int owners = root.Owners.Count;
            int meshesMade = split.CutMeshesMade;
            OwnerMoment source = default;
            var entered = new List<OwnerMoment>();
            Action<LogicalFragmentId, PhysicsFragmentOwner> onAdded = (fragment, owner) =>
            {
                if (!root.Ledger.TryGetSplitSource(fragment, out LogicalFragmentId of) || of != lower) return;
                if (entered.Count == 0 && root.Owners.TryGet(lower, out PhysicsFragmentOwner standing)) source = OwnerMoment.Of(lower, standing);
                entered.Add(OwnerMoment.Of(fragment, owner));
            };
            root.Owners.OwnerAdded += onAdded;
            remaining[0] = 1.0;
            yield return Until(
                () => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) + split.EndedAs(SpatialSplitEnd.Unfinished) > 1,
                "the split of the combs ended");
            remaining[0] = 0.0;
            root.Owners.OwnerAdded -= onAdded;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
            Assert.That(split.Published, Is.EqualTo(1), "published by the graph: " + split.LastEnd + " / " + split.LastRefusal);
            Assert.That(split.PublishedGraph, Is.EqualTo(1));
            Assert.That(split.GraphSearches, Is.EqualTo(1));
            Assert.That(split.GraphSeparable, Is.EqualTo(1));
            Assert.That(split.GraphUnfinished, Is.Zero);
            Assert.That(split.DeferredNow, Is.Zero);
            // the graph stage's time is clocked and summed in an observing build (the Editor is one), and said as measured
            Assert.That(CutSpatialSplit.GraphTimeMeasured, Is.True, "the Editor observes");
            Assert.That(split.GraphSeconds, Is.GreaterThan(0.0), "the one engine run's time was summed");
            Assert.That(split.MaxGraphSeconds, Is.EqualTo(split.GraphSeconds), "one run: the longest is the sum");
            StringAssert.Contains("; worker ms " + (split.GraphSeconds * 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " (longest ", split.DescribeGraph());
            StringAssert.DoesNotContain("not measured", split.DescribeGraph());
            SpatialSplitGraphRecord g = split.LastGraph;
            Assert.That(g.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Separable));
            Assert.That(g.positiveConvexes + g.negativeConvexes, Is.EqualTo(6));
            Assert.That(Mathf.Min(g.positiveConvexes, g.negativeConvexes), Is.EqualTo(3), "a comb a side");
            Assert.That(g.positiveTriangles + g.negativeTriangles, Is.GreaterThan(0));
            Assert.That(g.cutAttempts, Is.LessThan(8)); Assert.That(g.gjkLimitHit, Is.Zero);
            Assert.That(g.work, Is.GreaterThan(0).And.LessThan(SpatialSplitGraphLimits.DefaultWorkBudget));
            Assert.That(split.LastSearch.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound), "the planes found nothing: the placement is the graph's");
            Assert.That(split.PublishedWithCut, Is.Zero); Assert.That(split.ConvexesCut, Is.Zero);
            Assert.That(split.CutMeshesMade, Is.EqualTo(meshesMade), "no mesh made, nothing cooked");
            // the two owners: where the source stood, with its motion
            Assert.That(root.Owners.Count, Is.EqualTo(owners + 1), "two owners where one stood");
            Assert.That(entered.Count, Is.EqualTo(2));
            foreach (OwnerMoment m in entered)
            {
                Assert.That(Vector3.Distance(m.position, source.position), Is.LessThan(1e-3f), "where the source stood");
                Assert.That(Quaternion.Angle(m.rotation, source.rotation), Is.LessThan(0.1f));
                Assert.That(m.mass, Is.GreaterThan(0f));
            }

            Assert.That(entered[0].mass + entered[1].mass, Is.EqualTo(source.mass).Within(1e-3f), "the mass is shared out, none lost");
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(a.IsSet && b.IsSet && a != b, Is.True);
            Assert.That(root.Ledger.TryGetFragmentState(lower, out LogicalFragmentState lowerState) && lowerState == LogicalFragmentState.Replaced, Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(a) && root.Ledger.IsCurrentTarget(b), Is.True, "both are targets");
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(ownerA.Shape.ConvexCount + ownerB.Shape.ConvexCount, Is.EqualTo(6));
            yield return null;
            Assert.That(IsDrawn(root, a) && IsDrawn(root, b), Is.True);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>**An anchored owner never enters the graph stage** (GRAPH-CONNECT-4 §2): with the graph on, the same combs with an anchor end as the planes alone end them.</summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_AnAnchoredOwner_DoesNotEnterTheGraphStage()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph();
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers, anchors: new[] { new float3(3f, -1.5f, 0f) });
            LogicalFragmentId lower = lowers[0];
            Assert.That(AnchorsOf(root, lower).Count, Is.GreaterThan(0), "the lower side keeps the anchor");
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate));
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the plane search ran and found none");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.GraphSearches, Is.Zero, "anchored: not the graph stage's");
            Assert.That(split.LastGraph.Entered, Is.False);
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.EqualTo(1), "marked, as the planes alone mark it");
            Assert.That(split.DeferredNow, Is.Zero);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An unfinished graph stage defers the retry, which wakes by the deadline alone** (GRAPH-CONNECT-4 §3-5, §5-4): the
        /// engine's allocation fails (injected) -> Unfinished, no mark, the fragment Deferred; the walk stands idle (only its
        /// idle turns count, nothing is examined) until the deadline; the retry then passes the existing budget gate (none is
        /// issued while the budget is short); a second Unfinished doubles the wait and keeps the count; with the failure gone
        /// the retry after the deadline publishes.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_AnUnfinishedStage_DefersTheRetry_WakesByTheDeadlineAlone_PassesTheBudgetGate_AndThenSucceeds()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph(deferFirstFrames: 8, deferMaxFrames: 32);
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            LogicalFragmentId lower = lowers[0];
            try
            {
                CutSpatialSplit.TestGraphFailAllocation = 0;   // the engine's first allocation fails on the worker: Unfinished(allocation)
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 1 && !split.HasAttempt, "the attempt ended unfinished");
                int endedAt = Time.frameCount;
                TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
                Assert.That(split.LastGraph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished));
                Assert.That(split.LastGraph.stop, Is.EqualTo(SpatialSplitGraphStop.Allocation));
                Assert.That(split.GraphUnfinishedAs(SpatialSplitGraphStop.Allocation), Is.EqualTo(1));
                Assert.That(split.SuppressedNow, Is.Zero, "nothing is recorded about the input");
                Assert.That(split.DeferredNow, Is.EqualTo(1));
                Assert.That(split.DeferredCount(lower), Is.EqualTo(1));
                long until = split.DeferredUntil(lower);
                Assert.That(until, Is.GreaterThanOrEqualTo(endedAt + 8 - 1).And.LessThanOrEqualTo(endedAt + 8 + 1), "the first wait");
                Assert.That(split.DeferredWakeFrame, Is.EqualTo(until));
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Deferred));

                // idle until the deadline: the idle turns count, nothing is examined, nothing is issued
                yield return Until(() => split.IsIdle, "the walk stands idle: the one fragment waits (the round that issued it was its last: no new look, no Deferred pass)");
                int examined = split.Examined, issued = split.Issued;
                long idleTurns = split.IdleTurns;
                while (Time.frameCount < until - 2) yield return null;
                Assert.That(split.Examined, Is.EqualTo(examined), "nothing examined before the deadline");
                Assert.That(split.Issued, Is.EqualTo(issued));
                Assert.That(split.IdleTurns, Is.GreaterThan(idleTurns), "the idle turns went on");
                Assert.That(split.IsIdle, Is.True);

                // the deadline with the budget short: the wake ends the idle wait, the issue waits for the budget
                remaining[0] = 0.0;
                while (Time.frameCount < until + 3) yield return null;
                Assert.That(split.IsIdle, Is.False, "the deadline ended the idle wait");
                Assert.That(split.Issued, Is.EqualTo(issued), "nothing issued while the budget is short (the gate is not bypassed)");
                Assert.That(split.DeferredNow, Is.EqualTo(1), "still waiting for its issue");

                // the budget back: the retry is issued, and (still failing) ends unfinished again with the doubled wait and the count kept
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 2 && !split.HasAttempt, "the retry ended unfinished");
                int endedAgainAt = Time.frameCount;
                Assert.That(split.Issued, Is.EqualTo(issued + 1));
                Assert.That(split.DeferredCount(lower), Is.EqualTo(2), "the count went on with the attempt");
                long until2 = split.DeferredUntil(lower);
                Assert.That(until2, Is.GreaterThanOrEqualTo(endedAgainAt + 16 - 1).And.LessThanOrEqualTo(endedAgainAt + 16 + 1), "the wait doubled");
                Assert.That(split.DeferredWakeFrame, Is.EqualTo(until2), "the wake is the new deadline, not the old one");
                Assert.That(split.SuppressedNow, Is.Zero);

                // the failure gone: the retry after the deadline publishes
                CutSpatialSplit.TestGraphFailAllocation = -1;
                yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.Unfinished) > 2 || split.EndedAs(SpatialSplitEnd.NotFound) > 0, "the retry ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                Assert.That(split.Published, Is.EqualTo(1), "published once the allocation succeeds: " + split.LastEnd);
                Assert.That(split.PublishedGraph, Is.EqualTo(1));
                Assert.That(split.DeferredNow, Is.Zero, "nothing waits after the publication");
                Assert.That(split.DeferredWakeFrame, Is.EqualTo(long.MaxValue));
                Assert.That(split.DeferralsMade, Is.EqualTo(2));
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Two waiting owners: the wake is rebuilt, and a due retry is never held by a future deadline** (GRAPH-CONNECT-4 §3-5,
        /// §5-4): A and B both end unfinished; A's retry is held on the worker while B's deadline passes; a look at B from
        /// outside (the public Examine) changes nothing of the schedule; when A ends unfinished again with a later deadline,
        /// B is retried in the next round rather than at A's new deadline.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TwoDeferredOwners_ADueRetry_IsNotHeldByTheOthersNewDeadline_AndALookChangesNothing()
        {
            ResetGraphTestHooks();
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2))) : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph(deferFirstFrames: 8, deferMaxFrames: 64);
            var first = new LogicalFragmentId[1];
            var second = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), first);
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 12f), second);
            try
            {
                CutSpatialSplit.TestGraphFailAllocation = 0;
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 2 && !split.HasAttempt, "both ended unfinished");
                Assert.That(split.DeferredNow, Is.EqualTo(2));
                LogicalFragmentId a = split.DeferredUntil(first[0]) <= split.DeferredUntil(second[0]) ? first[0] : second[0];
                LogicalFragmentId b = a == first[0] ? second[0] : first[0];
                long untilA = split.DeferredUntil(a), untilB = split.DeferredUntil(b);
                Assert.That(untilA, Is.LessThanOrEqualTo(untilB));
                Assert.That(split.DeferredWakeFrame, Is.EqualTo(untilA), "the nearest deadline");

                // A's retry, held on the worker past B's deadline (the hold set before the deadline: the engine's stop takes well under a frame)
                background.HoldEverything = true;
                yield return Until(() => split.HasAttempt && split.AttemptFragment == a, "A is retried at its deadline");
                yield return Until(() => background.HoldingCount > 0, "A's work finished and is held back");
                while (Time.frameCount <= untilB + 1) yield return null;
                Assert.That(split.DeferredNow, Is.EqualTo(1), "B still waits (A's wait went with its issue)");
                Assert.That(split.Examine(b), Is.EqualTo(SpatialSplitPass.Candidate), "B's deadline passed: a candidate to the look");
                Assert.That(split.DeferredNow, Is.EqualTo(1), "the look changed nothing");
                Assert.That(split.DeferredUntil(b), Is.EqualTo(untilB));

                // A ends unfinished again: its new deadline is later than B's, and B is due -- retried next, not at A's deadline
                background.HoldEverything = false;
                background.ReleaseEverything();
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 3 && !split.HasAttempt, "A's retry ended unfinished");
                long untilA2 = split.DeferredUntil(a);
                Assert.That(untilA2, Is.GreaterThan(Time.frameCount + 10), "A's wait doubled to 16 frames");
                Assert.That(split.DeferredDue, Is.True, "B's deadline passed with its retry unissued");
                Assert.That(split.IsIdle, Is.False, "no idle wait while a retry is due");
                yield return Until(() => split.HasAttempt && split.AttemptFragment == b, "B is retried in the next round");
                Assert.That(Time.frameCount, Is.LessThan(untilA2), "well before A's new deadline");
                CutSpatialSplit.TestGraphFailAllocation = -1;
                yield return Until(() => split.Published == 2 || split.EndedAs(SpatialSplitEnd.NotFound) > 0, "both published once the failure is gone");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                Assert.That(split.Published, Is.EqualTo(2));
                Assert.That(split.DeferredNow, Is.Zero);
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A refused issue keeps the wait and its count; a retired owner's wait is forgotten** (GRAPH-CONNECT-4 §3-5): at the
        /// deadline the issue is refused (as a full queue refuses it) -- the fragment still waits with its count; issued
        /// afterwards and unfinished again, its wait doubles from that count; retired while waiting, it leaves the table and
        /// the wake goes back to none.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_ARefusedIssue_KeepsTheWait_AndARetiredOwnersWait_IsForgotten()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph(deferFirstFrames: 8, deferMaxFrames: 32);
            LogicalFragmentId body = AddInterleavedCombs(root, new Vector3(0f, 0f, 0f));
            root.Lifetime.MarkLineage(body);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 6);
            LogicalFragmentId lower = sides[1];
            try
            {
                CutSpatialSplit.TestGraphFailAllocation = 0;
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 1 && !split.HasAttempt, "ended unfinished");
                long until = split.DeferredUntil(lower);
                int issued = split.Issued, queueFull = split.QueueFull;
                CutSpatialSplit.TestRefuseIssue = true;
                while (Time.frameCount <= until + 2) yield return null;
                Assert.That(split.QueueFull, Is.GreaterThan(queueFull), "the issue was refused at the deadline");
                Assert.That(split.Issued, Is.EqualTo(issued));
                Assert.That(split.DeferredNow, Is.EqualTo(1), "the wait stays");
                Assert.That(split.DeferredCount(lower), Is.EqualTo(1), "with its count");
                CutSpatialSplit.TestRefuseIssue = false;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 2 && !split.HasAttempt, "issued once allowed, and unfinished again");
                Assert.That(split.DeferredCount(lower), Is.EqualTo(2), "the count went on from the kept wait");
                Assert.That(split.DeferredUntil(lower) - Time.frameCount, Is.GreaterThanOrEqualTo(14), "the wait doubled");

                // retired while waiting: forgotten at the next round's start, and the wake with it
                Assert.That(root.Lifetime.TryRetire(lower, out string why), Is.True, why);
                yield return null; yield return null; yield return null;
                yield return Until(() => split.DeferredNow == 0, "the retired owner's wait is swept");
                Assert.That(split.DeferredWakeFrame, Is.EqualTo(long.MaxValue));
                Assert.That(split.DeferredDue, Is.False);
                int issuedBefore = split.Issued;
                for (int i = 0; i < 20; i++) yield return null;
                Assert.That(split.Issued, Is.EqualTo(issuedBefore), "nothing is retried for a retired owner");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The graph's one allocation on the main thread fails in its own boundary** (GRAPH-CONNECT-4 §3-4): the attempt is
        /// issued, the planes run as before, the graph stage is skipped as Unfinished(allocation), nothing of the sides is
        /// left behind; the end is Unfinished (deferred), not an exception and not a mark.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheSidesAllocationFailing_LeavesTheExistingSearchesRunning_AndNothingBehind()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph();
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            try
            {
                CutSpatialSplit.TestFailGraphSidesAllocation = true;
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 1 && !split.HasAttempt, "the attempt ran and ended unfinished");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
                Assert.That(split.Issued, Is.EqualTo(1), "issued despite the graph's allocation failing");
                Assert.That(split.LastSearch.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound), "the planes ran as before");
                Assert.That(split.LastGraph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished));
                Assert.That(split.LastGraph.stop, Is.EqualTo(SpatialSplitGraphStop.Allocation));
                Assert.That(split.LastGraph.reason, Does.Contain("main thread"));
                Assert.That(split.LastGraph.work, Is.Zero, "the engine did not run");
                Assert.That(split.GraphUnfinishedAs(SpatialSplitGraphStop.Allocation), Is.EqualTo(1));
                Assert.That(split.SuppressedNow, Is.Zero);
                Assert.That(split.DeferredNow, Is.EqualTo(1));
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The graph and the capture do not run together, whichever comes second, and a running attempt finishes first**
        /// (GRAPH-CONNECT-4 §3-11): the graph asked for with the capture attached is off with its reason; the capture detached
        /// turns it on as another search (the old mark says nothing); the capture attached while an attempt is out changes the
        /// effective state only when that attempt is collected.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheCaptureAndTheGraph_DoNotRunTogether_InEitherOrder_AndARunningAttemptFinishesFirst()
        {
            ResetGraphTestHooks();
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2))) : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            string folder = NewCaptureFolder();
            var sink = new SpatialSplitCaptureSink(Path.Combine(folder, "session"), SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            LogicalFragmentId lower = lowers[0];
            try
            {
                // 1. the capture first, then the graph asked for: off, with the reason
                split.Capture = sink;
                split.Settings = SplitOnWithGraph();
                Assert.That(split.GraphEffective, Is.False);
                Assert.That(split.GraphRefusal, Does.Contain("capture"));
                Assert.That(split.Describe(), Does.Contain("asked for, off"));
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the planes alone ran");
                remaining[0] = 0.0;
                Assert.That(split.GraphSearches, Is.Zero, "the graph stage did not run with the capture on");
                Assert.That(split.SuppressedNow, Is.EqualTo(1));
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed));

                // 2. the capture detached: the graph is on, another search, and the old mark says nothing
                split.Capture = null;
                Assert.That(split.GraphEffective, Is.True, split.GraphRefusal);
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the mark was made under another search");
                background.HoldEverything = true;
                remaining[0] = 1.0;
                yield return Until(() => split.HasAttempt, "an attempt is out");
                yield return Until(() => background.HoldingCount > 0, "its work finished and is held back");

                // 3. the capture attached while the attempt is out: the change waits for the collection
                split.Capture = sink;
                Assert.That(split.GraphEffective, Is.True, "unchanged while the attempt is out");
                background.HoldEverything = false;
                background.ReleaseEverything();
                yield return Until(() => !split.HasAttempt, "the attempt was collected");
                remaining[0] = 0.0;
                Assert.That(split.GraphEffective, Is.False, "applied at the collection");
                Assert.That(split.GraphSearches, Is.EqualTo(1), "that attempt's graph stage ran (issued before the change)");
                Assert.That(split.Published, Is.EqualTo(1), "and published: " + split.LastEnd);
                Assert.That(split.PublishedGraph, Is.EqualTo(1));
                TestContext.Out.WriteLine(split.Describe());
            }
            finally
            {
                split.Capture = null;
                sink.Close(5.0);
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A world whose preparation fails runs without the graph, and a later world's preparation is its own** (GRAPH-CONNECT-4
        /// §3-10): with the engine unprepared and the preparation made to fail, the graph is asked for and off with its reason,
        /// the planes alone run; a second world prepares and runs the graph; the first world stays off.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_APreparationThatFails_LeavesThatWorldWithoutTheGraph_AndAnotherWorldPrepares()
        {
            ResetGraphTestHooks();
            SpatialSplitGraph.ResetPreparedForTests();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            try
            {
                CutSpatialSplit.TestGraphPrepareFailAllocation = 2;
                split.Settings = SplitOnWithGraph();
                Assert.That(split.GraphPrepared, Is.False, "the preparation failed");
                Assert.That(split.GraphEffective, Is.False);
                Assert.That(split.GraphRefusal, Does.Contain("preparation"));
                Assert.That(SpatialSplitGraph.IsPrepared, Is.False);
                var lowers = new LogicalFragmentId[1];
                yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the planes alone ran");
                remaining[0] = 0.0;
                Assert.That(split.GraphSearches, Is.Zero);
                Assert.That(split.LastGraph.Entered, Is.False);
                Assert.That(split.SuppressedNow, Is.EqualTo(1));

                // the ordinary preparation again (the hook off): this world does not ask again; it stays off
                CutSpatialSplit.TestGraphPrepareFailAllocation = -1;
                split.Settings = SplitOnWithGraph(deferFirstFrames: 9);   // another search (the limits differ): re-evaluated
                Assert.That(split.GraphPrepared, Is.False, "a world asks once");
                Assert.That(split.GraphEffective, Is.False);
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);

            // a second world prepares for itself (the process's preparation), and runs the graph
            CutWorldRoot second = NewWorld(out Shader _);
            second.Driver.RemainingMainSeconds = () => 1.0;
            var remaining2 = new double[1];
            CutSpatialSplit split2 = SplitClosed(second, remaining2, theProductsOwnStepEstimate: true);
            split2.Settings = SplitOnWithGraph();
            Assert.That(split2.GraphPrepared, Is.True);
            Assert.That(split2.GraphEffective, Is.True, split2.GraphRefusal);
            Assert.That(SpatialSplitGraph.IsPrepared, Is.True);
            var lowers2 = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(second, new Vector3(0f, 0f, 0f), lowers2);
            remaining2[0] = 1.0;
            yield return Until(() => split2.Published == 1 || split2.EndedAs(SpatialSplitEnd.NotFound) + split2.EndedAs(SpatialSplitEnd.Unfinished) > 0, "the second world's split ended");
            remaining2[0] = 0.0;
            Assert.That(split2.GraphSearches, Is.EqualTo(1));
            Assert.That(split2.Published, Is.EqualTo(1), split2.LastEnd.ToString());
            split2.Settings = default;
            yield return EndWorld(second);
        }

        /// <summary>
        /// **A capture switch while a captured attempt is out waits for its collection, and the result reaches the sink of the
        /// issue** (GRAPH-CONNECT-5 review 1): the capture is detached and then replaced while the attempt is out; the split still
        /// reports the sink of the issue; at the collection the result is written to that sink (one result, none lost), and the
        /// replacement is applied afterwards. The graph stays off here: the capture's own path is what is checked.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_ACaptureSwitchWhileACapturedAttemptIsOut_WaitsForItsCollection_AndTheResultReachesTheSinkOfTheIssue()
        {
            ResetGraphTestHooks();
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2))) : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            string folder = NewCaptureFolder();
            var sink = new SpatialSplitCaptureSink(Path.Combine(folder, "session"), SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            var other = new SpatialSplitCaptureSink(Path.Combine(folder, "other"), SpatialSplitCaptureLimits.Default, "PlayMode case, the replacement", null);
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            try
            {
                split.Capture = sink;
                background.HoldEverything = true;
                remaining[0] = 1.0;
                yield return Until(() => split.HasAttempt, "an attempt is out, its input captured");
                yield return Until(() => background.HoldingCount > 0, "its work finished and is held back");
                split.Capture = null;
                Assert.That(split.Capture, Is.SameAs(sink), "the switch waits: the sink of the issue stands");
                Assert.That(split.CapturePending, Is.True);
                split.Capture = other;
                Assert.That(split.Capture, Is.SameAs(sink));
                background.HoldEverything = false;
                background.ReleaseEverything();
                yield return Until(() => !split.HasAttempt, "the attempt was collected");
                remaining[0] = 0.0;
                Assert.That(split.CapturePending, Is.False);
                Assert.That(split.Capture, Is.SameAs(other), "the last switch asked for, applied at the collection");
                Assert.That(sink.Close(5.0), Is.True, "the sink of the issue wrote everything and ended");
                Assert.That(sink.ResultsWritten, Is.EqualTo(1), "the result reached the sink of the issue: " + sink.Describe());
                Assert.That(sink.ResultsLost, Is.Zero);
                Assert.That(other.ResultsWritten, Is.Zero, "nothing went to the replacement");
                TestContext.Out.WriteLine(split.Describe());
            }
            finally
            {
                split.Capture = null;
                other.Close(5.0);
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>**An invalid graph input ends NotClassified with a mark, not NotFound** (GRAPH-CONNECT-4 §3-2): the engine refuses the input (here a sides buffer declared too short); the attempt ends as an invalid input ends, apart from the not-found counts; nothing waits.</summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_AnInvalidGraphInput_EndsNotClassified_WithAMark_NotAsNotFound()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph();
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            try
            {
                CutSpatialSplit.TestGraphShortSides = true;
                remaining[0] = 1.0;
                yield return Until(() => split.EndedAs(SpatialSplitEnd.NotClassified) + split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.Unfinished) > 0 && !split.HasAttempt, "the attempt ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
                Assert.That(split.LastGraph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Invalid));
                Assert.That(split.LastGraph.stop, Is.EqualTo(SpatialSplitGraphStop.Invalid));
                Assert.That(split.GraphInvalid, Is.EqualTo(1));
                Assert.That(split.EndedAs(SpatialSplitEnd.NotClassified), Is.EqualTo(1), "as an invalid input ends");
                Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.Zero, "not among the not-found");
                Assert.That(split.EndedAs(SpatialSplitEnd.Unfinished), Is.Zero);
                Assert.That(split.SuppressedNow, Is.EqualTo(1), "marked");
                Assert.That(split.Examine(lowers[0]), Is.EqualTo(SpatialSplitPass.Suppressed));
                Assert.That(split.DeferredNow, Is.Zero); Assert.That(split.LimitReachedNow, Is.Zero);
                Assert.That(split.Published, Is.Zero);
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The work budget and a capacity cap are fixed limits: recorded as reached, no wait, no retry, re-evaluated when the
        /// search changes** (GRAPH-CONNECT-5 review 2): a tiny budget ends the combs' stage Unfinished(Work) -> recorded, the walk
        /// idle with nothing deferred; a larger budget is another search -> a candidate again, and the graph publishes; then a
        /// second world with a node cap too small -> Unfinished(Capacity) -> recorded the same way.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheWorkBudgetAndACapacityCap_AreFixedLimits_RecordedNotDeferred_UntilTheSearchChanges()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph(workBudget: 1);
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            LogicalFragmentId lower = lowers[0];
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.Unfinished) == 1 && !split.HasAttempt, "the stage ended on the budget");
            TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
            Assert.That(split.LastGraph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished));
            Assert.That(split.LastGraph.stop, Is.EqualTo(SpatialSplitGraphStop.Work));
            Assert.That(split.LastGraph.work, Is.GreaterThan(0).And.LessThanOrEqualTo(4097), "stopped at the first check point");
            Assert.That(split.GraphUnfinishedAs(SpatialSplitGraphStop.Work), Is.EqualTo(1));
            Assert.That(split.SuppressedNow, Is.Zero, "no mark: unfinished");
            Assert.That(split.DeferredNow, Is.Zero, "no wait: the budget is met at the same place every time");
            Assert.That(split.LimitReachedNow, Is.EqualTo(1));
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.LimitReached));
            int issued = split.Issued;
            for (int i = 0; i < 30; i++) yield return null;
            Assert.That(split.Issued, Is.EqualTo(issued), "not tried again");
            Assert.That(split.IsIdle, Is.True);
            // another search: the budget raised -> the record is of the old search; the graph runs and publishes
            split.Settings = SplitOnWithGraph();
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the limit record was made under another search");
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.Unfinished) + split.EndedAs(SpatialSplitEnd.NotFound) > 1, "the split ended under the new budget");
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.Published, Is.EqualTo(1), split.LastEnd.ToString());
            Assert.That(split.PublishedGraph, Is.EqualTo(1));
            // the old record (of the replaced fragment, under the old search) is swept where the marks are: at the next round's start
            yield return Until(() => split.LimitReachedNow == 0, "the record of the replaced fragment is swept at a round's start");
            remaining[0] = 0.0;
            split.Settings = default;
            yield return EndWorld(root);

            // a capacity cap: the node count over maxNodes -> the same record, no wait
            CutWorldRoot second = NewWorld(out Shader _);
            second.Driver.RemainingMainSeconds = () => 1.0;
            var remaining2 = new double[1];
            CutSpatialSplit split2 = SplitClosed(second, remaining2, theProductsOwnStepEstimate: true);
            CutSpatialSplitSettings capped = SplitOnWithGraph();
            capped.graphLimits.maxNodes = 2;   // the combs have 42 nodes
            split2.Settings = capped;
            var lowers2 = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(second, new Vector3(0f, 0f, 0f), lowers2);
            remaining2[0] = 1.0;
            yield return Until(() => split2.EndedAs(SpatialSplitEnd.Unfinished) == 1 && !split2.HasAttempt, "the stage ended on the node cap");
            TestContext.Out.WriteLine(DescribeGraphRecord(split2.LastGraph));
            Assert.That(split2.LastGraph.stop, Is.EqualTo(SpatialSplitGraphStop.Capacity));
            Assert.That(split2.GraphUnfinishedAs(SpatialSplitGraphStop.Capacity), Is.EqualTo(1));
            Assert.That(split2.DeferredNow, Is.Zero);
            Assert.That(split2.LimitReachedNow, Is.EqualTo(1));
            Assert.That(split2.SuppressedNow, Is.Zero);
            Assert.That(split2.Examine(lowers2[0]), Is.EqualTo(SpatialSplitPass.LimitReached));
            int issued2 = split2.Issued;
            for (int i = 0; i < 30; i++) yield return null;
            Assert.That(split2.Issued, Is.EqualTo(issued2), "not tried again");
            remaining2[0] = 0.0;
            split2.Settings = default;
            yield return EndWorld(second);
        }

        /// <summary>
        /// **The replay of a captured session with the graph stage on the span input is the engine's product path** (GRAPH-CONNECT-6
        /// §2): the combs captured (the graph off: the capture excludes it), every sample replayed with span + graph gives the verdict,
        /// the span and referenced counts, the work and the sides (hashed in the engine's form) that the engine's own product path
        /// (DecideAsProduct) gives the same saved sample; the graph's sharing checks pass; the compact input refuses the graph.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheReplayOfACapturedSession_WithTheGraphOnTheSpanInput_IsTheEnginesProductPath()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            CutSpatialSplitSettings settings = SplitOn();
            settings.bentSearch = false;
            split.Settings = settings;
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the planes alone ran, the input captured");
            remaining[0] = 0.0;
            split.Capture = null;
            Assert.That(sink.Close(20.0), Is.True, "the session wrote everything");
            Assert.That(sink.ResultsWritten, Is.EqualTo(1));

            var spanGraph = new SpatialSplitReplayOptions { spanInput = true, graph = true };
            Assert.Throws<ArgumentException>(() => SpatialSplitReplay.RunSession(session, Path.Combine(folder, "compact-graph"), new SpatialSplitReplayOptions { graph = true }, out _, out _), "the graph needs the span input");
            string summary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph"), spanGraph, out bool allGood, out bool noSample);
            TestContext.Out.WriteLine(summary);
            Assert.That(noSample, Is.False);
            Assert.That(allGood, Is.True, "every listed sample replayed, the graph's sharing checks passed");
            StringAssert.Contains("graph stage: entered 1 (verdicts: Separable 1); led 1, distributed 1, classified 1, kernel Ok 1", summary);
            StringAssert.Contains("bodies through Burst " + (Unity.Burst.BurstCompiler.IsEnabled ? "1, managed 0, not run 0" : "0, managed 1, not run 0"), summary);
            StringAssert.Contains("Separable but incomplete 0", summary);
            StringAssert.Contains("graph sharing checks failed 0", summary);

            // the same sample through the engine's own product path: the same record
            string file = Directory.GetFiles(session, "*.zss.gz").Single();
            SpatialSplitSample sample = SpatialSplitSample.Read(file);
            SpatialSplitReplayResult r = SpatialSplitReplay.Run(sample, spanGraph);
            Assert.That(r.error, Is.Null, r.error);
            Assert.That(r.graphEntered && r.graphLed, Is.True);
            Assert.That(r.graph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Separable));
            Assert.That(r.GraphComplete, Is.True, "shared out, anchors, classified, kernel Ok, checked");
            Assert.That(r.graph.bodiesRan, Is.True);
            Assert.That(r.GraphChecksPass, Is.True, "triangles " + r.graphTrianglesMissing + "/" + r.graphTrianglesExtra + "/" + r.graphTriangleWrongSide + "/" + r.graphTriangleOrderBroken + ", convexes " + r.graphConvexWrongSide + "/" + r.graphConvexNotSided);
            SpatialSplitGraphLimits l = SpatialSplitGraphLimits.Default;
            var engineSettings = new SpatialSplitGraph.GraphSettings
            {
                secondsLimit = 0.0, nearestSelection = true, flowDinic = true, pairsSweep = false, hashPrepare = false, requirePrepared = true,
                maxTriangles = l.maxTriangles, maxConvexes = l.maxConvexes, maxVertexSpan = l.maxVertexSpan, maxNodes = l.maxNodes, maxCandidates = l.maxCandidates, maxGraphBytes = l.maxGraphBytes, workBudget = l.workBudget,
            };
            SpatialSplitGraph.GraphOutcome o = SpatialSplitGraph.DecideAsProduct(sample, engineSettings, true, out SpatialSplitGraph.NativeRun run);
            Assert.That(o.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), o.reason);
            Assert.That(r.graph.vertexSpan, Is.EqualTo(o.vertexSpan), "the span input reproduces the product's span");
            Assert.That(r.graph.referencedVertices, Is.EqualTo(o.referencedVertices));
            Assert.That(r.graph.workPrepare, Is.EqualTo(o.workPrepare), "work 3, the compaction included");
            Assert.That(r.graph.workSearch, Is.EqualTo(o.workSearch));
            Assert.That(r.graph.candidates, Is.EqualTo(o.candidatesA));
            Assert.That(r.graph.nodes, Is.EqualTo(o.convexCount + o.meshComponents));
            Assert.That(r.graph.positiveConvexes == o.positiveConvexes && r.graph.negativeConvexes == o.negativeConvexes && r.graph.positiveTriangles == o.positiveTriangles && r.graph.negativeTriangles == o.negativeTriangles, Is.True);
            Assert.That(r.graph.burstRan, Is.EqualTo(run.burstRan));
            Assert.That(r.graphSidesHash, Is.EqualTo(EngineSidesHash(o.convexPositive, o.trianglePositive)), "the sides, normalised to the engine's form, hash the same");
            TestContext.Out.WriteLine("replay graph: " + SpatialSplitReplay.GraphJson(in r.graph, r.graphSidesHash, System.Globalization.CultureInfo.InvariantCulture));

            // the span input without the graph: the planes' result as the live one (a captured session of one NotFound)
            string summaryOff = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-off"), new SpatialSplitReplayOptions { spanInput = true }, out bool sameOff, out _);
            TestContext.Out.WriteLine(summaryOff);
            Assert.That(sameOff, Is.True, "the span input reproduces the live planes' result");
            string summaryCompact = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "compact-off"), null, out bool sameCompact, out _);
            Assert.That(sameCompact, Is.True, "the compact input (the existing contract) too");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A Separable whose sharing out refused is incomplete: counted apart, never a success** (GRAPH-CONNECT-7 review 1, item 2):
        /// the same captured comb replayed with the graph on while the test refuses the distribution by the engine's sides -- the
        /// engine's verdict stays Separable, the stages after it are not done, the line says complete false / incomplete true, the
        /// summary counts it as "Separable but incomplete 1" and the session is not all good (not a sharing check failure either).
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheReplayOfASeparable_WhoseSharingOutRefused_IsIncomplete_NotASuccess()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            CutSpatialSplitSettings settings = SplitOn();
            settings.bentSearch = false;
            split.Settings = settings;
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the planes alone ran, the input captured");
            remaining[0] = 0.0;
            split.Capture = null;
            Assert.That(sink.Close(20.0), Is.True, "the session wrote everything");
            try
            {
                CutSpatialSplit.TestGraphRefuseDistribution = true;
                var spanGraph = new SpatialSplitReplayOptions { spanInput = true, graph = true };
                string summary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph-refused"), spanGraph, out bool allGood, out bool noSample);
                TestContext.Out.WriteLine(summary);
                Assert.That(noSample, Is.False);
                Assert.That(allGood, Is.False, "an incomplete Separable is not a success");
                StringAssert.Contains("graph stage: entered 1 (verdicts: Separable 1); led 1, distributed 0, classified 0, kernel Ok 0, Separable but incomplete 1", summary);
                StringAssert.Contains("graph sharing checks failed 0", summary);
                StringAssert.Contains("(the live comparison is not the criterion here): NO", summary);
                string file = Directory.GetFiles(session, "*.zss.gz").Single();
                SpatialSplitReplayResult r = SpatialSplitReplay.Run(SpatialSplitSample.Read(file), spanGraph);
                Assert.That(r.error, Is.Null, r.error);
                Assert.That(r.graph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Separable));
                Assert.That(r.graphLed && !r.distributed, Is.True, "led, nothing shared out");
                Assert.That(r.GraphComplete, Is.False);
                Assert.That(r.GraphIncomplete, Is.True);
                StringAssert.Contains("INCOMPLETE", r.checkNote);
                string lines = File.ReadAllText(Path.Combine(folder, "span-graph-refused", "replay.jsonl"));
                StringAssert.Contains("\"complete\":false,\"incomplete\":true", lines);
                StringAssert.Contains("\"inputMode\":\"span\",\"graphOn\":true", lines);
            }
            finally
            {
                ResetGraphTestHooks();
            }
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The graph time's words tell a build that does not clock it from a measured zero** (GRAPH-CONNECT-7 §7: the Link
        /// check's Player said "worker ms 0.000" while the sum was never compiled in): not measured is said as such, a measured
        /// sum is printed with its longest.
        /// </summary>
        [Test]
        public void SpatialSplitGraph_DescribeGraphTime_SaysNotMeasured_ApartFromAMeasuredZero()
        {
            Assert.That(CutSpatialSplit.DescribeGraphTime(false, 0.0, 0.0), Is.EqualTo("worker ms not measured (observation off in this build)"));
            Assert.That(CutSpatialSplit.DescribeGraphTime(false, 0.5, 0.25), Is.EqualTo("worker ms not measured (observation off in this build)"), "a number without a clock is not printed");
            Assert.That(CutSpatialSplit.DescribeGraphTime(true, 0.0, 0.0), Is.EqualTo("worker ms 0.000 (longest 0.000)"));
            Assert.That(CutSpatialSplit.DescribeGraphTime(true, 0.00125, 0.0005), Is.EqualTo("worker ms 1.250 (longest 0.500)"));
        }

        /// <summary>
        /// **A captured bent-found sample replayed with the bent partition left out goes to the graph, or hands on to the mesh-led
        /// plane** (GRAPH-CONNECT-8, the comparison's B condition): the U with a part inside is captured under the bent partition (a
        /// bent result live); replayed with the saved setting the bent stage runs and finds (the history mark and the result both say
        /// so); replayed with bent off the bent stage is neither effective nor entered, the graph stage is entered, and the attempt ends
        /// as a complete graph placement or as a mesh-led plane; with the GJK iteration limit forced the graph is unfinished and the
        /// mesh-led plane is entered. The line carries the requested, saved and effective bent setting and the stages' marks.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheReplayOfABentFoundSample_WithTheBentOff_GoesToTheGraph_OrHandsOnToTheMeshLedPlane()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithBent(cutConvexes: true);
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
            LogicalFragmentId lower = sides[1];
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the side with the U and the part");
            remaining[0] = 1.0;
            // the lower side (the U with the part) is the sample wanted; the upper side (one convex) may be examined first and end NotFound
            // through the mesh-led plane -- so the wait is for the lower side: published, or marked after a NotFound of its own
            yield return Until(() => split.Published == 1 || split.Examine(lower) == SpatialSplitPass.Suppressed, "the bent partition ran live on the lower side");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.EqualTo(1), "the live attempt published by the bent partition: " + split.LastEnd);
            SpatialSplitSearchResult lastLive = split.LastSearch;
            Assert.That(CutSpatialSplit.IsBentResult(in lastLive), Is.True);
            split.Capture = null;
            Assert.That(sink.Close(20.0), Is.True, "the session wrote everything");
            Assert.That(sink.ResultsWritten, Is.GreaterThan(0));
            string file = Directory.GetFiles(session, "*.zss.gz").OrderBy(f => f).Last();
            SpatialSplitSample sample = SpatialSplitSample.Read(file);
            Assert.That(sample.bentSearch, Is.True, "the saved setting");
            Assert.That(sample.anchors == null || sample.anchors.Length == 0, Is.True, "anchor-less: the graph stage applies");
            try
            {
                // the saved setting with the graph on: the bent stage runs and finds; the graph is not entered
                var saved = new SpatialSplitReplayOptions { spanInput = true, graph = true };
                SpatialSplitReplayResult rs = SpatialSplitReplay.Run(sample, saved);
                Assert.That(rs.error, Is.Null, rs.error);
                Assert.That(rs.bentEffective && rs.bentRan && rs.bent, Is.True, "effective, entered, found");
                Assert.That(rs.graphEntered, Is.False);
                // bent off: neither effective nor entered; the graph stage entered; a complete graph placement or the mesh-led plane
                var off = new SpatialSplitReplayOptions { spanInput = true, graph = true, bentOff = true };
                SpatialSplitReplayResult ro = SpatialSplitReplay.Run(sample, off);
                Assert.That(ro.error, Is.Null, ro.error);
                Assert.That(ro.bentSaved, Is.True); Assert.That(ro.bentEffective, Is.False); Assert.That(ro.bentRan, Is.False); Assert.That(ro.bent, Is.False);
                Assert.That(ro.search.bentFramesTried, Is.Zero);
                Assert.That(ro.graphEntered, Is.True, "anchor-less with convexes: the graph stage is entered when the bent partition is left out");
                TestContext.Out.WriteLine("bent off: graph " + ro.graph.verdict + "/" + ro.graph.stop + ", meshLed " + ro.meshLedRan + ", outcome " + ro.outcome + ", cut " + ro.cut);
                Assert.That(ro.GraphComplete || ro.meshLedRan, Is.True, "a complete graph placement, or the mesh-led plane entered");
                if (ro.GraphComplete) Assert.That(ro.meshLedRan, Is.False, "the graph placement leaves the mesh-led plane out");
                // the session, both ways: the lines and the summaries
                string summarySaved = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph-saved"), saved, out bool allSaved, out _);
                string summaryOff = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph-bent-off"), off, out bool allOff, out _);
                TestContext.Out.WriteLine(summarySaved); TestContext.Out.WriteLine(summaryOff);
                Assert.That(allSaved, Is.True, "saved: every listed sample replayed, every check passed");
                Assert.That(allOff, Is.True, "off: the same, and the bent partition neither effective nor entered");
                StringAssert.Contains("options: inputMode span, graph on", summarySaved); StringAssert.Contains(", bent saved, order current, list", summarySaved);
                StringAssert.Contains(", bent off, order current, list", summaryOff);
                StringAssert.Contains("bent stage (off, asked for: the saved setting not followed): effective in 0 samples, ran 0, found 0", summaryOff);
                int samplesInSession = Directory.GetFiles(session, "*.zss.gz").Length;
                StringAssert.Contains("bent stage (the saved setting): effective in " + samplesInSession + " samples, ran 1, found 1", summarySaved, "the bent stage effective on every sample of the session, entered and found on the lower side");
                string linesSaved = File.ReadAllText(Path.Combine(folder, "span-graph-saved", "replay.jsonl"));
                string linesOff = File.ReadAllText(Path.Combine(folder, "span-graph-bent-off", "replay.jsonl"));
                StringAssert.Contains("\"bentRequested\":\"saved\",\"bentSaved\":true,\"bentEffective\":true", linesSaved);
                StringAssert.Contains("\"stagesRan\":{\"plane\":true,\"bent\":true,\"graph\":false,\"meshLed\":false}", linesSaved);
                StringAssert.Contains("\"bentRequested\":\"off\",\"bentSaved\":true,\"bentEffective\":false", linesOff);
                StringAssert.Contains("\"stagesRan\":{\"plane\":true,\"bent\":false,\"graph\":true,\"meshLed\":" + (ro.meshLedRan ? "true" : "false") + "}", linesOff);
                StringAssert.Contains(",\"anchorsDistributed\":" + (ro.anchorsDistributed ? "true" : "false") + ",\"cut\":" + (ro.cut ? "true" : "false"), linesOff);
                // the graph left unfinished by a work budget of 1 (a fixed limit; this input needs no GJK, so the iteration hook would not
                // bite): the mesh-led plane is entered; not a graph placement
                SpatialSplitGraphLimits l = SpatialSplitGraphLimits.Default;
                var tight = new SpatialSplitReplayOptions { spanInput = true, graph = true, bentOff = true, graphLimits = new SpatialSplitGraphLimits(l.maxTriangles, l.maxConvexes, l.maxVertexSpan, l.maxNodes, l.maxCandidates, l.maxGraphBytes, 1, l.deferFirstFrames, l.deferMaxFrames) };
                SpatialSplitReplayResult ru = SpatialSplitReplay.Run(sample, tight);
                Assert.That(ru.error, Is.Null, ru.error);
                Assert.That(ru.graphEntered, Is.True);
                Assert.That(ru.graph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished), ru.graph.reason);
                Assert.That(ru.graphLed, Is.False); Assert.That(ru.GraphComplete, Is.False); Assert.That(ru.GraphIncomplete, Is.False, "unfinished is not an incomplete Separable");
                Assert.That(ru.meshLedRan, Is.True, "the mesh-led plane follows an unfinished graph stage (cutConvexes on)");
                Assert.That(ru.bentRan, Is.False);
            }
            finally
            {
                ResetGraphTestHooks();
            }
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An anchored sample replayed with the bent partition left out does not enter the graph stage** (GRAPH-CONNECT-8): the
        /// graph's conditions (no anchors, two convexes or more) are the product's and are not bypassed by the comparison's switch;
        /// the line says so (anchorCount, the graph not entered, the all-path anchors flag written).
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheReplayOfAnAnchoredSample_WithTheBentOff_DoesNotEnterTheGraph()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOn();
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers, anchors: new[] { new float3(3f, -1.5f, 0f) });
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) + split.Published > 0 && !split.HasAttempt, "the live attempt ended");
            remaining[0] = 0.0;
            split.Capture = null;
            Assert.That(sink.Close(20.0), Is.True, "the session wrote everything");
            string file = Directory.GetFiles(session, "*.zss.gz").OrderBy(f => f).Last();
            SpatialSplitSample sample = SpatialSplitSample.Read(file);
            Assert.That(sample.anchors != null && sample.anchors.Length > 0, Is.True, "an anchored sample");
            var off = new SpatialSplitReplayOptions { spanInput = true, graph = true, bentOff = true };
            SpatialSplitReplayResult r = SpatialSplitReplay.Run(sample, off);
            Assert.That(r.error, Is.Null, r.error);
            Assert.That(r.graphEntered, Is.False, "anchored: the graph stage does not apply, bent off or not");
            Assert.That(r.bentRan, Is.False);
            string summary = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph-bent-off"), off, out bool all, out _);
            TestContext.Out.WriteLine(summary);
            Assert.That(all, Is.True, summary);
            StringAssert.Contains("graph stage: entered 0", summary);
            string lines = File.ReadAllText(Path.Combine(folder, "span-graph-bent-off", "replay.jsonl"));
            StringAssert.Contains("\"anchorCount\":" + sample.anchors.Length, lines);
            StringAssert.Contains("\"stagesRan\":{\"plane\":true,\"bent\":false,\"graph\":false,\"meshLed\":", lines);
            StringAssert.Contains(",\"anchorsDistributed\":", lines);
            StringAssert.Contains("\"graphStages\":{\"entered\":false", lines);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The graph-first order (the configuration C under comparison) runs the bent partition only after an unfinished graph stage**
        /// (GRAPH-CONNECT-10 §1): on the U with a part inside (a bent result under the order as before) the graph is Separable and the
        /// bent partition is not run; with a work budget of 1 the graph is Unfinished(Work) and the bent partition runs after it and
        /// finds; with the sides' allocation refused the pre-recorded Unfinished(Allocation) sends the attempt to the bent partition
        /// without the engine's bodies; an Invalid graph ends the attempt without the bent partition. The line says the order asked for
        /// and whether it applied; the summary counts the fallbacks.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheGraphFirstOrder_RunsTheBentPartition_OnlyAfterAnUnfinishedGraph()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithBent(cutConvexes: true);
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
            LogicalFragmentId lower = sides[1];
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.Examine(lower) == SpatialSplitPass.Suppressed, "the bent partition ran live on the lower side");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.EqualTo(1), "the live attempt published by the bent partition: " + split.LastEnd);
            split.Capture = null;
            Assert.That(sink.Close(20.0), Is.True, "the session wrote everything");
            string file = Directory.GetFiles(session, "*.zss.gz").OrderBy(f => f).Last();
            SpatialSplitSample sample = SpatialSplitSample.Read(file);
            Assert.That(sample.bentSearch, Is.True, "the saved setting: on");
            try
            {
                var c = new SpatialSplitReplayOptions { spanInput = true, graph = true, graphFirst = true };
                // Separable first: no bent partition, no mesh-led plane
                SpatialSplitReplayResult rs = SpatialSplitReplay.Run(sample, c);
                Assert.That(rs.error, Is.Null, rs.error);
                Assert.That(rs.graphFirstEffective, Is.True, "anchor-less with convexes, the graph on: the order applied");
                Assert.That(rs.graphEntered && rs.graph.verdict == SpatialSplitGraphVerdict.Separable && rs.GraphComplete, Is.True, "a complete graph placement");
                Assert.That(rs.bentEffective, Is.True, "the saved setting is followed"); Assert.That(rs.bentRan, Is.False, "not run before the graph, not after a Separable"); Assert.That(rs.meshLedRan, Is.False);
                // Unfinished(Work) by a budget of 1: the bent partition after the graph, and it finds
                SpatialSplitGraphLimits l = SpatialSplitGraphLimits.Default;
                var tight = new SpatialSplitReplayOptions { spanInput = true, graph = true, graphFirst = true, graphLimits = new SpatialSplitGraphLimits(l.maxTriangles, l.maxConvexes, l.maxVertexSpan, l.maxNodes, l.maxCandidates, l.maxGraphBytes, 1, l.deferFirstFrames, l.deferMaxFrames) };
                SpatialSplitReplayResult ru = SpatialSplitReplay.Run(sample, tight);
                Assert.That(ru.error, Is.Null, ru.error);
                Assert.That(ru.graphEntered && ru.graph.verdict == SpatialSplitGraphVerdict.Unfinished, Is.True, "unfinished: " + ru.graph.verdict + "/" + ru.graph.stop);
                Assert.That(ru.bentRan, Is.True, "the bent partition after the unfinished graph");
                Assert.That(ru.bent, Is.True, "and it finds what it found live");
                Assert.That(ru.meshLedRan, Is.False, "the bent result leaves the mesh-led plane out");
                Assert.That(ru.distributed && ru.anchorsDistributed && ru.classified && ru.kernelStatus == "Ok" && ru.checkedSharing && ru.ChecksPass, Is.True, "the bent result complete and checked");
                // the pre-recorded Unfinished(Allocation): no bodies, the bent partition still
                CutSpatialSplit.TestFailGraphSidesAllocation = true;
                SpatialSplitReplayResult ra = SpatialSplitReplay.Run(sample, c);
                CutSpatialSplit.TestFailGraphSidesAllocation = false;
                Assert.That(ra.error, Is.Null, ra.error);
                Assert.That(ra.graphEntered, Is.True, "the record counts as the stage's");
                Assert.That(ra.graph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished)); Assert.That(ra.graph.stop, Is.EqualTo(SpatialSplitGraphStop.Allocation));
                Assert.That(ra.graph.bodiesRan, Is.False, "the engine's bodies were not called");
                Assert.That(ra.bentRan && ra.bent, Is.True, "the bent partition after the pre-recorded unfinished stage");
                // Invalid: the attempt ends, no bent partition, no mesh-led plane
                CutSpatialSplit.TestGraphShortSides = true;
                SpatialSplitReplayResult ri = SpatialSplitReplay.Run(sample, c);
                CutSpatialSplit.TestGraphShortSides = false;
                Assert.That(ri.error, Is.Null, ri.error);
                Assert.That(ri.graph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Invalid));
                Assert.That(ri.bentRan, Is.False, "an invalid input is not hidden by a fallback"); Assert.That(ri.meshLedRan, Is.False); Assert.That(ri.found, Is.False);
                // the session: the line's order fields and the summary's fallback counts
                string summaryC = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph-first"), c, out bool allC, out _);
                TestContext.Out.WriteLine(summaryC);
                Assert.That(allC, Is.True, summaryC);
                StringAssert.Contains(", bent saved, order graph-first, list", summaryC);
                StringAssert.Contains("order: graph-first applied to 1 samples; bent fallback after an unfinished graph: ran 0, found 0", summaryC);
                string lines = File.ReadAllText(Path.Combine(folder, "span-graph-first", "replay.jsonl"));
                StringAssert.Contains("\"bentRequested\":\"saved\",\"bentSaved\":true,\"bentEffective\":true,\"orderRequested\":\"graph-first\",\"graphFirstEffective\":true", lines);
                StringAssert.Contains("\"stagesRan\":{\"plane\":true,\"bent\":false,\"graph\":true,\"meshLed\":false}", lines);
                string summaryT = SpatialSplitReplay.RunSession(session, Path.Combine(folder, "span-graph-first-tight"), tight, out bool allT, out _);
                TestContext.Out.WriteLine(summaryT);
                Assert.That(allT, Is.True, summaryT);
                StringAssert.Contains("bent fallback after an unfinished graph: ran 1, found 1", summaryT);
                string linesT = File.ReadAllText(Path.Combine(folder, "span-graph-first-tight", "replay.jsonl"));
                StringAssert.Contains("\"stagesRan\":{\"plane\":true,\"bent\":true,\"graph\":true,\"meshLed\":false}", linesT);
                // the order as before on the same sample: the bent partition first, the graph not entered (A unchanged)
                SpatialSplitReplayResult rA = SpatialSplitReplay.Run(sample, new SpatialSplitReplayOptions { spanInput = true, graph = true });
                Assert.That(rA.graphFirstEffective, Is.False); Assert.That(rA.bentRan && rA.bent, Is.True); Assert.That(rA.graphEntered, Is.False);
            }
            finally
            {
                ResetGraphTestHooks();
            }
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **In the graph-first order a sample whose saved setting has the bent partition off gets no fallback** (GRAPH-CONNECT-10 §2-3 f):
        /// the combs captured with the bent partition off, replayed graph-first with a work budget of 1: the graph is Unfinished, the bent
        /// partition is not run (the saved setting is respected), the mesh-led plane is not entered either (cutConvexes off).
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheGraphFirstOrder_RespectsASavedBentOff_NoFallback()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            CutSpatialSplitSettings settings = SplitOn();
            settings.bentSearch = false;
            split.Settings = settings;
            string folder = NewCaptureFolder();
            string session = Path.Combine(folder, "session");
            var sink = new SpatialSplitCaptureSink(session, SpatialSplitCaptureLimits.Default, "PlayMode case", null);
            split.Capture = sink;
            var lowers = new LogicalFragmentId[1];
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers);
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the planes alone ran, the input captured");
            remaining[0] = 0.0;
            split.Capture = null;
            Assert.That(sink.Close(20.0), Is.True, "the session wrote everything");
            SpatialSplitSample sample = SpatialSplitSample.Read(Directory.GetFiles(session, "*.zss.gz").Single());
            Assert.That(sample.bentSearch, Is.False, "the saved setting: off");
            SpatialSplitGraphLimits l = SpatialSplitGraphLimits.Default;
            var tight = new SpatialSplitReplayOptions { spanInput = true, graph = true, graphFirst = true, graphLimits = new SpatialSplitGraphLimits(l.maxTriangles, l.maxConvexes, l.maxVertexSpan, l.maxNodes, l.maxCandidates, l.maxGraphBytes, 1, l.deferFirstFrames, l.deferMaxFrames) };
            SpatialSplitReplayResult r = SpatialSplitReplay.Run(sample, tight);
            Assert.That(r.error, Is.Null, r.error);
            Assert.That(r.graphFirstEffective, Is.True);
            Assert.That(r.graph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished), r.graph.reason);
            Assert.That(r.bentEffective, Is.False); Assert.That(r.bentRan, Is.False, "no fallback: the saved setting has the bent partition off");
            Assert.That(r.meshLedRan, Is.False); Assert.That(r.found, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Live, the graph-first order falls back to the bent partition when the engine is not prepared, and keeps the order as before
        /// when the graph is off for the world** (GRAPH-CONNECT-10 §2-3 c, d): the test hook puts every issued attempt in the C order; the
        /// U with a part inside, the graph and the bent partition on; the engine's preparation revoked before the attempt -> the graph
        /// stage ends Unfinished(NotPrepared) and the bent partition publishes; then a preparation that fails leaves the world without
        /// the graph -> the next attempt runs the bent partition first, the graph stage not entered.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheGraphFirstOrderLive_FallsBackOnNotPrepared_AndKeepsTheOrderAsBefore_WhenTheGraphIsOff()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            try
            {
                CutSpatialSplit.TestGraphFirst = true;
                CutSpatialSplitSettings settings = SplitOnWithGraph(cutConvexes: true);
                settings.bentSearch = true;
                settings.bentLimits = SpatialSplitBentLimits.Default;
                split.Settings = settings;
                Assert.That(split.GraphEffective, Is.True, split.GraphRefusal);
                LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
                LogicalFragmentId lower = sides[1];
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate));
                // the preparation revoked: the worker's contract refuses the bodies -> Unfinished(NotPrepared) -> the bent partition
                SpatialSplitGraph.ResetPreparedForTests();
                remaining[0] = 1.0;
                yield return Until(() => split.Published == 1 || split.Examine(lower) == SpatialSplitPass.Suppressed, "the attempt on the lower side ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                Assert.That(split.Published, Is.EqualTo(1), "published by the bent fallback: " + split.LastEnd + " / " + split.LastRefusal);
                Assert.That(split.PublishedBent, Is.EqualTo(1));
                Assert.That(split.GraphSearches, Is.GreaterThanOrEqualTo(1), "the graph stage was entered first");
                Assert.That(split.GraphUnfinishedAs(SpatialSplitGraphStop.NotPrepared), Is.GreaterThanOrEqualTo(1), "and ended unfinished for the preparation contract");
                Assert.That(split.PublishedGraph, Is.Zero);
            }
            finally
            {
                ResetGraphTestHooks();
            }
            split.Settings = default;
            yield return EndWorld(root);

            // a preparation that fails leaves a NEW world without the graph (a world's own successful preparation is never revoked):
            // the order as before there -- the bent partition first, the graph stage not entered -- the C hook notwithstanding
            SpatialSplitGraph.ResetPreparedForTests();
            CutWorldRoot second = NewWorld(out Shader _);
            second.Driver.RemainingMainSeconds = () => 1.0;
            var remaining2 = new double[1];
            CutSpatialSplit split2 = SplitClosed(second, remaining2, theProductsOwnStepEstimate: true);
            try
            {
                CutSpatialSplit.TestGraphPrepareFailAllocation = 2;
                CutSpatialSplit.TestGraphFirst = true;
                CutSpatialSplitSettings settings2 = SplitOnWithGraph(cutConvexes: true);
                settings2.bentSearch = true;
                settings2.bentLimits = SpatialSplitBentLimits.Default;
                split2.Settings = settings2;
                Assert.That(split2.GraphEffective, Is.False, "the graph is off for this world: " + split2.GraphRefusal);
                LogicalFragmentId body2 = AddUWithAPartInside(second, new Vector3(0f, 0f, 0f));
                yield return null;
                var sides2 = new LogicalFragmentId[2];
                yield return CutAtTheWaist(second, body2, sides2, lowerConvexes: 5);
                LogicalFragmentId lower2 = sides2[1];
                remaining2[0] = 1.0;
                yield return Until(() => split2.Published == 1 || split2.Examine(lower2) == SpatialSplitPass.Suppressed, "the attempt on the lower side ended (graph off)");
                remaining2[0] = 0.0;
                Assert.That(split2.Published, Is.EqualTo(1), "published by the bent partition in the order as before: " + split2.LastEnd);
                Assert.That(split2.PublishedBent, Is.EqualTo(1));
                Assert.That(split2.GraphSearches, Is.Zero, "the graph stage was not entered: the graph is off for the world");
            }
            finally
            {
                ResetGraphTestHooks();
                SpatialSplitGraph.Prepare();   // leave the engine prepared for the cases that follow
            }
            split2.Settings = default;
            yield return EndWorld(second);
        }

        /// <summary>
        /// **An unfinished graph stage hands on to the mesh-led plane, whose find is published as mesh-led with the graph
        /// history kept** (GRAPH-CONNECT-4 §5-3): a body whose hand is reached only by cutting a convex across; the graph
        /// stage fails (injected), the mesh-led plane cuts and publishes; the graph's record counts as unfinished, nothing waits.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_AnUnfinishedStage_HandsOnToTheMeshLedPlane_WhichPublishes_WithTheGraphHistoryKept()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph(cutConvexes: true, minCutSideTriangles: 12);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f), convexAcross: true);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 3);
            LogicalFragmentId lower = sides[1];
            try
            {
                CutSpatialSplit.TestGraphFailAllocation = 0;
                remaining[0] = 1.0;
                // (with the convex cut on, the upper side -- one convex -- is a candidate too and ends NotFound without the graph stage: it has one convex)
                yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.Unfinished) + split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 0,
                    "the split of the lower side ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
                Assert.That(split.LastSource, Is.EqualTo(lower));
                Assert.That(split.GraphSearches, Is.EqualTo(1), "the graph stage was entered for the lower side (not for the one-convex upper side)");
                Assert.That(split.GraphUnfinishedAs(SpatialSplitGraphStop.Allocation), Is.EqualTo(1), "and ended unfinished");
                Assert.That(split.LastSearch.meshLed, Is.True, "the mesh-led plane followed");
                Assert.That(split.Published, Is.EqualTo(1), "published as mesh-led: " + split.LastEnd + " / " + split.LastRefusal);
                Assert.That(split.PublishedWithCut, Is.EqualTo(1));
                Assert.That(split.PublishedGraph, Is.Zero, "not the graph's publication");
                Assert.That(split.DeferredNow, Is.Zero, "nothing waits: the attempt was published");
                Assert.That(split.SuppressedNow, Is.EqualTo(1), "the upper side's mark alone (one convex: NotFound by the planes, no graph stage)");
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The engine's fixed search limits are unfinished, not conclusions** (GRAPH-CONNECT-4 §3-2): with one GJK iteration
        /// allowed, the combs' stage ends either separable (certified by cuts) or unfinished for the GJK limit -- never a
        /// NotFound that marks the fragment -- and the limit's hits are counted.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitGraph_TheGjkIterationLimit_IsNeverAConclusion()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph();
            var lowers = new LogicalFragmentId[1];
            // the teeth tilted 10 degrees: the cross-comb teeth's boxes overlap (the hulls stay 0.095 m apart), so stage B tries them by GJK;
            // untilted, the touching pairs are definite contacts and the rest are apart by their boxes: stage A decides, no GJK runs
            yield return CombsBelowTheWaist(root, new Vector3(0f, 0f, 0f), lowers, toothTiltDegrees: 10f);
            try
            {
                CutSpatialSplit.TestGraphGjkMaxIterations = 1;
                remaining[0] = 1.0;
                yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.Unfinished) + split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 0,
                    "the split ended");
                remaining[0] = 0.0;
                SpatialSplitGraphRecord g = split.LastGraph;
                TestContext.Out.WriteLine(DescribeGraphRecord(g));
                Assert.That(split.GraphSearches, Is.EqualTo(1));
                Assert.That(g.candidates, Is.GreaterThan(0), "the tilted teeth are candidate pairs");
                Assert.That(g.gjkLimitHit, Is.GreaterThan(0), "one iteration is not enough for some pair");
                Assert.That(g.verdict == SpatialSplitGraphVerdict.Separable || (g.verdict == SpatialSplitGraphVerdict.Unfinished && g.stop == SpatialSplitGraphStop.GjkIterations), Is.True,
                    "separable, or unfinished for the GJK limit: " + g.verdict + " / " + g.stop);
                Assert.That(split.GraphNotFound, Is.Zero, "never a NotFound reached by the limit");
                if (g.verdict == SpatialSplitGraphVerdict.Unfinished)
                {
                    // a fixed limit reached: recorded apart -- no mark, no wait, no retry under the same input and search
                    Assert.That(split.EndedAs(SpatialSplitEnd.Unfinished), Is.EqualTo(1));
                    Assert.That(split.SuppressedNow, Is.Zero, "no mark for an unfinished search");
                    Assert.That(split.GraphUnfinishedAs(SpatialSplitGraphStop.GjkIterations), Is.EqualTo(1));
                    Assert.That(split.DeferredNow, Is.Zero, "not a wait: the same search would end the same");
                    Assert.That(split.LimitReachedNow, Is.EqualTo(1));
                    Assert.That(split.LimitsReached, Is.EqualTo(1));
                    Assert.That(split.Examine(lowers[0]), Is.EqualTo(SpatialSplitPass.LimitReached));
                    int issued = split.Issued;
                    remaining[0] = 1.0;
                    for (int i = 0; i < 30; i++) yield return null;
                    Assert.That(split.Issued, Is.EqualTo(issued), "not tried again");
                    Assert.That(split.IsIdle, Is.True);
                    // another search (the limits differ): the record is of the old one, and the fragment is a candidate again
                    split.Settings = SplitOnWithGraph(deferFirstFrames: 9);
                    Assert.That(split.Examine(lowers[0]), Is.EqualTo(SpatialSplitPass.Candidate), "the limit record was made under another search");
                    yield return Until(() => split.Issued == issued + 1, "tried again under the new search");
                    yield return Until(() => !split.HasAttempt, "and ended");
                    remaining[0] = 0.0;
                    Assert.That(split.LimitsReached, Is.EqualTo(2), "the same limit again, recorded for the new search");
                    Assert.That(split.LimitReachedNow, Is.EqualTo(1));
                }
                else
                {
                    Assert.That(split.Published, Is.EqualTo(1));
                }
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
