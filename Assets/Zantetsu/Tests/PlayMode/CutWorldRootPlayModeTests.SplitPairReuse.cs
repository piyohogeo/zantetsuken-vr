using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// NPC-PAIR-REUSE-1: the graph stage of the method as before, after the protected combination placed nothing, in the product's
    /// own world. Where the convexes are all held together the stage is not run and the attempt ends as a search that found nothing;
    /// where the protection alone kept the combination from placing, the stage runs on what the combination's decision left and
    /// publishes as it does from the start; the combination's own stop at a capacity cap is a fixed limit; a refused issue is not
    /// counted as issued; what is counted before the issue is what the attempt holds; and the worker's kept set is gone when the
    /// worker's run returns -- with several attempts out, for a stale result, and for an attempt ended by the switch.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        // the hand's low x where it overlaps the trunk (x in [0.8, 1.3] against the trunk's [-1, 1]): the two convexes' hulls meet
        private const float HandInTheTrunkLowX = 0.8f;

        private static SpatialSplitGraphRecord? s_pairReuseCombsOn, s_pairReuseCombsOff;

        /// <summary>
        /// The interleaved combs made an NPC's as <see cref="AddNpcCombs"/>, with one difference: comb A's spine's boxes are weighted
        /// to comb B's spine's bone too, and comb B's spine's own boxes to its tooth's bone. The mesh that supports comb B's spine's
        /// bone is then comb A's spine's: the protection keeps that convex with that mesh, whatever their boxes say.
        /// </summary>
        private LogicalFragmentId AddNpcCombsHeldByTheProtection(CutWorldRoot root)
        {
            LogicalFragmentId fragment = AddInterleavedCombs(root, new Vector3(0f, 0f, 0f));
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry _), Is.True);
            root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry);
            const int boxes = 6;
            owner.Shape.SetConvexBones(new[] { 0, 1, 2, 3, 4, 5 });
            var weights = new List<ulong>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                for (int box = 0; box < boxes; box++)
                {
                    for (int face = 0; face < k_faces.Length; face++)
                    {
                        if (k_faces[face].submesh != submesh) continue;
                        ulong entry = box == 0 ? NpcVertexBones.Pack(0, 3, NpcVertexBones.None, NpcVertexBones.None)
                            : NpcVertexBones.Pack((ushort)(box == 3 ? 4 : box), NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None);
                        for (int k = 0; k < 4; k++) weights.Add(entry);
                    }
                }
            }

            Assert.That(weights.Count, Is.EqualTo(geometry.vertexCount), "the layout: a weight a vertex");
            NpcVertexBones table = NpcVertexBones.FromEntries(weights.ToArray(), 4);
            var lineage = new NpcBoneLineage(boxes, new[] { -1, 0, 0, 0, 3, 3 }, new[] { true, true, true, true, true, true },
                new[] { new float3(0f, -0.5f, 0f), new float3(1.125f, -0.35f, 0f), new float3(1.125f, -0.95f, 0f), new float3(3f, -0.85f, 0f), new float3(1.875f, -0.65f, 0f), new float3(1.875f, -1.25f, 0f) },
                table, (uint)geometry.vertexStart, geometry.vertexCount, null);
            table.Release();   // the lineage's hold is the one left
            root.NpcBones.RegisterRoot(fragment, lineage);
            return fragment;
        }

        // The trunk with a hand inside it, cut at the waist, decided with the combination on and the graph stage behind its fallback.
        private IEnumerator HeldTogetherBelowTheWaist(bool reuse, bool graphFirst = false)
        {
            ResetGraphTestHooks();
            bool before = CutSpatialSplit.PairReuse;
            CutSpatialSplit.PairReuse = reuse;
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                root.Driver.RemainingMainSeconds = () => 1.0;
                var remaining = new double[1];
                CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
                split.Settings = graphFirst ? SplitOnGraphFirst(cutConvexes: false) : SplitOnWithGraph();
                split.BoneFallback = true;
                split.SetPairProtect(true);
                Assert.That(split.PairEffective && split.GraphEffective, Is.True, split.PairRefusal + split.GraphRefusal);
                long live = SpatialSplitGraph.LiveNativeAllocations;
                LogicalFragmentId body = AddNpcWithASharedBone(root, new Vector3(0f, 0f, 0f), HandInTheTrunkLowX);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                LogicalFragmentId lower = sides[1];

                remaining[0] = 1.0;
                yield return Until(() => !split.HasAttempt && split.PairGraphStages >= 1 && split.Examine(lower) != SpatialSplitPass.Candidate, "the lower side was decided and ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                TestContext.Out.WriteLine(split.DescribePair());
                TestContext.Out.WriteLine(DescribeGraphRecord(split.LastGraph));
                Assert.That(split.PairSeparable == 0 && split.PublishedPair == 0 && split.Published == 0, Is.True, "the layout: the bones connect the two and their convexes meet -- nothing places them apart");
                Assert.That(split.PairFellBack, Is.GreaterThanOrEqualTo(1));
                Assert.That(split.PairGraphStages, Is.EqualTo(1), "the lower side's fallback came to the graph stage");
                Assert.That(split.PairExceptions == 0 && split.PairNotRun == 0, Is.True);
                Assert.That(split.AttemptsHeldBytes, Is.Zero, "everything it held was given back");
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "and every area of the engine and of the kept set");
                SpatialSplitGraphRecord g = split.LastGraph;
                if (reuse)
                {
                    Assert.That(split.PairGraphSkipped == 1 && split.PairLinkChecks == 1, Is.True, "the links' check held: the stage was not run");
                    Assert.That(g.skipped && g.verdict == SpatialSplitGraphVerdict.NotFound && g.linkHeld && !g.unfinished, Is.True, "recorded as a search that found nothing: " + g.reason);
                    Assert.That(split.PairFallbackEndedAs(SpatialSplitEnd.NotFound), Is.GreaterThanOrEqualTo(1), "and the attempt ended as one");
                    Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed), "marked: not looked at again on this input");
                    Assert.That(split.PairGraphHullPairs, Is.Zero, "no hull was read by a stage that did not run");
                    if (graphFirst)
                    {
                        // review 2: in the graph-first order a stage that was not run is a NotFound, and no bent partition is looked for after it
                        Assert.That(split.GraphFirstApplied, Is.GreaterThanOrEqualTo(1), "the layout: the graph-first order applied, with the bent partition on");
                        Assert.That(split.BentSearches == 0 && split.BentFallbackRan == 0, Is.True, "no bent partition after the stage not run: " + split.BentSearches + " / " + split.BentFallbackRan);
                    }
                    else
                    {
                        Assert.That(split.BentSearches, Is.Zero, "the order as before has no bent partition after the stage (and none is set here)");
                    }
                }
                else
                {
                    Assert.That(split.PairGraphSkipped == 0 && split.PairLinkChecks == 0 && split.PairGraphReusedInput == 0 && split.PairGraphReusedPairs == 0, Is.True, "with the reuse off the stage ran from the start");
                    Assert.That(!g.skipped && g.verdict != SpatialSplitGraphVerdict.Separable && g.verdict != SpatialSplitGraphVerdict.Invalid, Is.True, "and placed nothing either: " + g.verdict + " " + g.reason);
                }

                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                CutSpatialSplit.PairReuse = before;
            }
        }

        /// <summary>
        /// **Where the convexes are all held together, the fallback's graph stage is not run.** The trunk and a hand inside it, joined by
        /// a shared bone: the combination places nothing (the two convexes' hulls meet). No graph of that piece could put a convex on
        /// each side; the links' check says so, the stage is not run, and the attempt ends as a search that found nothing -- marked,
        /// nothing held.
        /// </summary>
        [UnityTest]
        public IEnumerator PairReuse_WhereTheConvexesAreHeldTogether_TheFallbacksGraphStageIsNotRun()
        {
            yield return HeldTogetherBelowTheWaist(true);
        }

        /// <summary>
        /// **In the graph-first order, no bent partition is looked for after a stage that was not run** (review 2: the search's policy,
        /// taken for its cost -- not a proof that no bent partition exists). The same piece under the graph-first setting with the bent
        /// partition on: the stage is not run, recorded NotFound, and the attempt ends as a search that found nothing. (A stage that
        /// runs and ends unfinished goes on to the bent partition as before: SplitGraph's graph-first cases.)
        /// </summary>
        [UnityTest]
        public IEnumerator PairReuse_GraphFirst_AfterAStageNotRun_NoBentPartitionIsLookedFor()
        {
            yield return HeldTogetherBelowTheWaist(true, graphFirst: true);
        }

        /// <summary>**The same piece with the reuse off: the stage runs from the start and places nothing either.**</summary>
        [UnityTest]
        public IEnumerator PairReuse_Off_TheSamePiecesGraphStageRunsFromTheStart_AndPlacesNothing()
        {
            yield return HeldTogetherBelowTheWaist(false);
        }

        // The combs the protection holds together for the combination, decided with the graph stage behind the fallback.
        private IEnumerator CombsHeldByTheProtection(bool reuse)
        {
            ResetGraphTestHooks();
            bool before = CutSpatialSplit.PairReuse;
            CutSpatialSplit.PairReuse = reuse;
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                root.Driver.RemainingMainSeconds = () => 1.0;
                var remaining = new double[1];
                CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
                split.Settings = SplitOnWithGraph();
                split.BoneFallback = true;
                split.SetPairProtect(true);
                Assert.That(split.PairEffective && split.GraphEffective, Is.True, split.PairRefusal + split.GraphRefusal);
                long live = SpatialSplitGraph.LiveNativeAllocations;
                LogicalFragmentId body = AddNpcCombsHeldByTheProtection(root);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides, lowerConvexes: 6);
                LogicalFragmentId lower = sides[1];

                remaining[0] = 1.0;
                yield return Until(() => !split.HasAttemptFor(lower) && !root.Ledger.IsCurrentTarget(lower), "the lower side was decided and replaced");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                TestContext.Out.WriteLine(split.DescribePair());
                SpatialSplitGraphRecord g = split.LastGraph;
                TestContext.Out.WriteLine(DescribeGraphRecord(g));
                Assert.That(split.LastSource, Is.EqualTo(lower), "the publication is the lower side's");
                Assert.That(split.PublishedPair, Is.Zero, "the layout: the protection keeps comb B's spine with comb A's spine's mesh -- the combination places nothing (" + split.DescribePair() + ")");
                Assert.That(split.LastPair != null && split.LastPair.protectedPairsOfBones >= 1, Is.True);
                Assert.That(split.PairFellBack >= 1 && split.PublishedPairFallback == 1 && split.PublishedFallbackGraph == 1, Is.True, "the method as before goes on and its graph stage publishes; last refusal: " + split.LastRefusal);
                // (the two combs published are the NPC's and are decided in their turn -- each a comb whose convexes are held together;
                // the counts below hold whatever those attempts have come to, the record is read where it is still the lower side's)
                bool lowersRecord = !g.skipped && g.verdict == SpatialSplitGraphVerdict.Separable;
                Assert.That(split.PairGraphStages - split.PairGraphSkipped, Is.EqualTo(1), "the lower side's stage was entered and run");
                Assert.That(root.NpcBones.SplitsByConvexes == 1 && root.NpcBones.SplitsByNeededBones == 0, Is.True, "its records followed by the fallback's own rule (the convexes' bones)");
                LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
                Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment _) && root.NpcBones.TryGet(b, out NpcBoneFragment _), Is.True, "both sides are the NPC's");
                AssertClosedChild(root, a, "a comb");
                AssertClosedChild(root, b, "the other comb");
                Assert.That(split.PairExceptions == 0 && split.PairNotRun == 0, Is.True);
                if (reuse)
                {
                    Assert.That(split.PairGraphReusedInput, Is.EqualTo(1), "on the combination's compaction and first preparation");
                    Assert.That(split.PairLinkChecks, Is.GreaterThanOrEqualTo(1));
                    if (lowersRecord)
                    {
                        Assert.That(g.reusedInput && g.linkRan && !g.linkHeld && g.linkGroups >= 2, Is.True, "the links' check ran and did not hold: " + g.linkGroups + " groups");
                        s_pairReuseCombsOn = g;
                    }
                }
                else
                {
                    Assert.That(split.PairGraphReusedInput == 0 && split.PairGraphSkipped == 0 && split.PairLinkChecks == 0 && split.PairGraphReusedPairs == 0, Is.True, "from the start");
                    if (lowersRecord) s_pairReuseCombsOff = g;
                }

                if (s_pairReuseCombsOn.HasValue && s_pairReuseCombsOff.HasValue)
                {
                    // whichever of the two cases ran second: the stage on the kept set ended as the stage from the start
                    SpatialSplitGraphRecord on = s_pairReuseCombsOn.Value, off = s_pairReuseCombsOff.Value;
                    Assert.That(new long[] { (long)on.verdict, (long)on.stop, on.nodes, on.meshComponents, on.candidates, on.cutAttempts, on.gjkLimitHit, on.work, on.workPrepare, on.workSearch, on.nativeBytes, on.hullPairs,
                            on.positiveConvexes, on.negativeConvexes, on.positiveTriangles, on.negativeTriangles, on.vertexSpan, on.referencedVertices },
                        Is.EqualTo(new long[] { (long)off.verdict, (long)off.stop, off.nodes, off.meshComponents, off.candidates, off.cutAttempts, off.gjkLimitHit, off.work, off.workPrepare, off.workSearch, off.nativeBytes, off.hullPairs,
                            off.positiveConvexes, off.negativeConvexes, off.positiveTriangles, off.negativeTriangles, off.vertexSpan, off.referencedVertices }),
                        "the stage's record on the kept set against the one from the start");
                }

                yield return Until(() => !split.HasAttempt, "the combs' own attempts ended");
                Assert.That(split.PublishedFallbackGraph == 1 && split.PairExceptions == 0, Is.True, "nothing more was published");
                Assert.That(split.AttemptsHeldBytes, Is.Zero);
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "every area of the engine and of the kept set was given back");
                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                CutSpatialSplit.PairReuse = before;
            }
        }

        /// <summary>
        /// **Where the protection alone keeps the combination from placing, the graph stage runs on what the combination left, and
        /// publishes.** The NPC's combs with comb B's spine's bone supported by comb A's spine's mesh: the combination keeps that convex
        /// with that mesh and places nothing. The method as before has no such tie: no plane, and its graph stage places the two combs
        /// apart -- here on the combination's compaction and first preparation, after the links' check found the convexes in two groups.
        /// The publication, the children's surfaces and the records' following are the fallback's own.
        /// </summary>
        [UnityTest]
        public IEnumerator PairReuse_WhereTheProtectionKeptTheCombinationFromPlacing_TheGraphStageRunsOnWhatItLeft_AndPublishes()
        {
            yield return CombsHeldByTheProtection(true);
        }

        /// <summary>**The same combs with the reuse off: the stage from the start publishes the same split** (its record is held against the other case's).</summary>
        [UnityTest]
        public IEnumerator PairReuse_Off_TheSameCombsArePublishedByTheStageFromTheStart()
        {
            yield return CombsHeldByTheProtection(false);
        }

        /// <summary>
        /// **The combination's own stop at a capacity cap is a fixed limit.** With the NPC fallback off the combination's end is the
        /// attempt's. Under a node cap its decision ends unfinished for the capacity: the same input under the same search stops at the
        /// same place, so the fragment is recorded as at a limit and is not tried again -- no wait is made for it.
        /// </summary>
        [UnityTest]
        public IEnumerator PairLive_TheCombinationsOwnStopAtACap_IsAFixedLimit_NotAWait()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            CutSpatialSplitSettings settings = SplitOnWithGraph();
            SpatialSplitGraphLimits limits = settings.graphLimits;
            limits.maxNodes = 2;   // (the least a valid setting has) against the lower side's two convexes and two surfaces
            settings.graphLimits = limits;
            split.Settings = settings;
            split.BoneFallback = false;
            split.SetPairProtect(true);
            Assert.That(split.PairEffective && split.GraphEffective, Is.True, split.PairRefusal + split.GraphRefusal);
            LogicalFragmentId body = AddNpcWithASharedBone(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];

            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.Examine(lower) != SpatialSplitPass.Candidate, "the lower side was decided and ended");
            for (int k = 0; k < 20; k++) yield return null;
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.PairUnfinished, Is.GreaterThanOrEqualTo(1), "the layout: the combination's decision stopped at the node cap");
            Assert.That(split.LastPair.stop, Is.EqualTo("capacity"), split.LastPair.reason);
            Assert.That(split.PairFellBack, Is.Zero, "the fallback is off: the combination's end is the attempt's");
            Assert.That(split.PairEndedAs(SpatialSplitEnd.Unfinished), Is.GreaterThanOrEqualTo(1));
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.LimitReached), "recorded as at a fixed limit");
            Assert.That(split.DeferralsMade, Is.Zero, "no wait was made");
            Assert.That(split.DeferredNow, Is.Zero);
            Assert.That(split.LimitsReached, Is.EqualTo(split.PairEndedAs(SpatialSplitEnd.Unfinished)), "once an unfinished end, and not issued again: " + split.Issued + " issued");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A refused issue is not an issue; what is counted before the issue is what the attempt holds; and the worker's kept set does
        /// not outlive the worker's run.** One attempt at a time. The queue refuses: nothing is counted as issued, nothing is held.
        /// Allowed: the attempt out holds exactly the bytes the issue counted before taking anything (the marks' room and the kept
        /// set's tables among them). Its decision finished and held back from the collection: the attempt is still out, and the
        /// engine's areas -- the kept set's -- are already given back. Its fragment is cut meanwhile: stale, everything given back.
        /// The next attempt is ended by the switch while it is out: not run, everything given back.
        /// </summary>
        [UnityTest]
        public IEnumerator PairReuse_ARefusedIssueIsNotCounted_TheCountBeforeTheIssueIsWhatIsHeld_AndTheKeptSetEndsWithTheWorkersRun()
        {
            ResetGraphTestHooks();
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool
                    ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph();
            split.MaxAttempts = 2;   // (the bytes are counted before an issue only where several attempts may be out)
            split.BoneFallback = true;
            split.SetPairProtect(true);
            Assert.That(split.PairEffective && split.GraphEffective && CutSpatialSplit.PairReuse, Is.True, split.PairRefusal + split.GraphRefusal);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            LogicalFragmentId body = AddNpcWithASharedBone(root, new Vector3(0f, 0f, 0f), HandInTheTrunkLowX);
            root.Lifetime.MarkLineage(body);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Lifetime.TryRetire(sides[0], out string whyNot), Is.True, whyNot);   // the side above is out of the way: one candidate
            yield return null;

            // over the bytes' limit alone: held with that reason, nothing taken, nothing counted as issued
            long limit = split.MaxHeldBytes;
            int heldBefore = (int)split.IssuesHeldTooLargeAlone;
            split.MaxHeldBytes = 64;
            remaining[0] = 1.0;
            yield return Until(() => split.IssuesHeldTooLargeAlone > heldBefore, "the issue was held: too large alone for the limit");
            remaining[0] = 0.0;
            Assert.That(split.PairIssued == 0 && split.Issued == 0 && !split.HasAttempt && split.AttemptsHeldBytes == 0, Is.True, "nothing was taken for an attempt over the limit");
            Assert.That(split.LastReservationBytes, Is.GreaterThan(64), "what was counted before the issue: " + split.LastReservationBytes + " B");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
            split.MaxHeldBytes = limit;

            // refused by the queue
            try
            {
                CutSpatialSplit.TestRefuseIssue = true;
                int refusedBefore = split.QueueFull;
                remaining[0] = 1.0;
                yield return Until(() => split.QueueFull > refusedBefore, "an issue was refused");
                remaining[0] = 0.0;
                Assert.That(split.PairIssued == 0 && split.Issued == 0, Is.True, "a refused issue is not counted: " + split.PairIssued + " / " + split.Issued);
                Assert.That(split.HasAttempt, Is.False);
                Assert.That(split.AttemptsHeldBytes, Is.Zero, "what it took was given back");
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
            }
            finally
            {
                CutSpatialSplit.TestRefuseIssue = false;
            }

            // issued, its decision held back
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttemptFor(lower), "the lower side's attempt was issued");
            remaining[0] = 0.0;
            Assert.That(split.PairIssued == 1 && split.Issued == 1, Is.True, "counted once the queue took it");
            long reserved = split.LastReservationBytes, held = split.AttemptsHeldBytes;
            TestContext.Out.WriteLine("counted before the issue " + reserved + " B, held by the attempt out " + held + " B");
            Assert.That(held, Is.EqualTo(reserved), "what was counted before the issue is what the attempt holds");
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment record), Is.True);
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            long own = Math.Max(1, record.Lineage.LocalCount) + SpatialSplitGraph.Kept.OwnBytesAtMost(lowerOwner.Shape.ConvexCount, 8);
            Assert.That(own, Is.GreaterThan(Math.Max(1, record.Lineage.LocalCount)), "the layout: the kept set's tables are counted");
            Assert.That(reserved, Is.GreaterThanOrEqualTo(own), "the marks' room and the kept set's tables are in the count");
            yield return Until(() => background.HoldingCount > 0, "its decision finished and is held back");
            Assert.That(split.HasAttemptFor(lower), Is.True, "the attempt is still out");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "and the worker's kept set ended with the worker's run");
            Assert.That(split.AttemptsHeldBytes, Is.EqualTo(held), "the attempt's own blocks are held until it ends");

            // stale
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the re-cut's record is over");
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale) == 1 && split.PairStale == 1, Is.True, "stale, counted once");
            Assert.That(split.PairGraphStages, Is.EqualTo(1), "its fallback had come to the graph stage (not run: " + split.PairGraphSkipped + ")");
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.AttemptsHeldBytes, Is.Zero, "everything it held was given back");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));

            // ended by the switch while out
            background.HoldEverything = true;
            int issued = split.Issued;
            remaining[0] = 1.0;
            yield return Until(() => split.Issued > issued && split.HasAttempt, "a piece of the re-cut was issued");
            remaining[0] = 0.0;
            Assert.That(split.AttemptsHeldBytes, Is.GreaterThan(0));
            yield return Until(() => background.HoldingCount > 0, "its decision finished and is held back");
            split.Settings = default;   // off, with attempts out
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the attempts out were ended");
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.EndedAs(SpatialSplitEnd.NotRun), Is.GreaterThanOrEqualTo(1), "ended by the switch");
            Assert.That(split.PairNotRun, Is.EqualTo(split.EndedAs(SpatialSplitEnd.NotRun)), "each counted once");
            Assert.That(split.AttemptsHeldBytes, Is.Zero);
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
            Assert.That(split.PairExceptions, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **With several attempts out, each is decided on its own kept set, and none of them is left.** Three NPCs' lower sides (the
        /// hand inside the trunk) issued together under the eight-attempt default, their decisions held back: three attempts out, the
        /// bytes held the sum of what was counted, and no area of the engine live. Released: each fallback's graph stage was not run
        /// (its own convexes held together), each ended once, nothing held.
        /// </summary>
        [UnityTest]
        public IEnumerator PairReuse_WithSeveralAttemptsOut_EachHasItsOwnKeptSet_AndNoneIsLeft()
        {
            ResetGraphTestHooks();
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool
                    ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.Settings = SplitOnWithGraph();
            split.BoneFallback = true;
            split.SetPairProtect(true);
            Assert.That(split.MaxAttempts, Is.EqualTo(8), "the layout: the default of eight attempts");
            Assert.That(split.PairEffective && split.GraphEffective && CutSpatialSplit.PairReuse, Is.True, split.PairRefusal + split.GraphRefusal);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            var lowers = new List<LogicalFragmentId>();
            for (int k = 0; k < 3; k++)
            {
                LogicalFragmentId body = AddNpcWithASharedBone(root, new Vector3(8f * k, 0f, 0f), HandInTheTrunkLowX);
                root.Lifetime.MarkLineage(body);
                yield return null;
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides);
                lowers.Add(sides[1]);
                Assert.That(root.Lifetime.TryRetire(sides[0], out string whyNot), Is.True, whyNot);
                yield return null;
            }

            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttemptFor(lowers[0]) && split.HasAttemptFor(lowers[1]) && split.HasAttemptFor(lowers[2]), "the three lower sides' attempts are out together");
            remaining[0] = 0.0;
            Assert.That(split.PairIssued == 3 && split.Issued == 3, Is.True);
            long held = split.AttemptsHeldBytes;
            Assert.That(held, Is.EqualTo(3 * split.LastReservationBytes), "the three hold what was counted for each (the same piece three times)");
            yield return Until(() => background.HoldingCount >= 3, "the three decisions finished and are held back");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "three attempts out and no kept set left: each ended with its worker's run");
            Assert.That(split.AttemptsHeldBytes, Is.EqualTo(held));

            background.HoldEverything = false;
            background.ReleaseEverything();
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PairGraphStages >= 3, "the three ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.MostAttemptsOut, Is.GreaterThanOrEqualTo(3));
            Assert.That(split.PairGraphStages == 3 && split.PairGraphSkipped == 3 && split.PairLinkChecks == 3, Is.True, "each stage not run, by its own check");
            Assert.That(split.PairFallbackEndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(3), "each ended once, as a search that found nothing");
            Assert.That(split.Published == 0 && split.PairExceptions == 0 && split.PairNotRun == 0 && split.PairStale == 0, Is.True);
            Assert.That(split.AttemptsHeldBytes, Is.Zero);
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
