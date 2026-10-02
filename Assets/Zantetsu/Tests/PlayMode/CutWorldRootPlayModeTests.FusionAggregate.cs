using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The aggregation of a building's groups (2026-09-30): by a deadline in real time or by the next Slash, the
    /// groups an earlier cut left become at most one resting group and one free group; a hit during the aggregation
    /// is held and cut after it; the free groups' union keeps its momentum; a stale physics-cost estimate is measured
    /// again by a verification step so that the steps resume.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private CutWorldRoot NewAggregatingWorld(double deadlineSeconds, float floorTop = -1f, int perFrame = 8)
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile =>
            {
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingWorldEnabled", false);
                SetPrivate(profile, "buildingFusionEnabled", true);
                SetPrivate(profile, "buildingFusionPerFrame", perFrame);
                SetPrivate(profile, "buildingFusionDeadlineSeconds", (float)deadlineSeconds);
            });
            Assert.That(root.Fusion, Is.Not.Null, "the fusion is on");
            Assert.That(root.Fusion.Settings.deadlineSeconds, Is.EqualTo(deadlineSeconds).Within(1e-6));
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, floorTop - 0.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
            return root;
        }

        /// <summary>An ask as the hit detector would make it: the plane in the member's shape frame, the Slash, and the adopted plane's identity and value (the same identity for the same plane value, another for any other, however close).</summary>
        private static ProvisionalCutAsk SlashAsk(CutWorldRoot root, LogicalFragmentId member, float4 planeWorld, long slashId, float impulse)
        {
            ProvisionalCutAsk ask = Ask(member, InShapeFrame(root, member, planeWorld));
            ask.slashId = slashId;
            ask.positiveSeparationImpulse = impulse;
            ask.negativeSeparationImpulse = impulse;
            ask.adoptedPlaneId = (long)math.hash(planeWorld) + 1;
            ask.adoptedPlaneWorld = planeWorld;
            return ask;
        }

        /// <summary>The real bodies a set of groups' members stand on: the distinct attachedRigidbody of every member collider (what the physics scene holds, not what the fusion says).</summary>
        private static int RealBodiesOf(CutWorldRoot root, IEnumerable<FusedGroup> groups)
        {
            var bodies = new HashSet<Rigidbody>();
            foreach (FusedGroup g in groups)
            {
                foreach (LogicalFragmentId f in g.Fragments)
                {
                    if (!root.Owners.TryGet(f, out PhysicsFragmentOwner o) || o.Root == null) continue;
                    foreach (MeshCollider c in o.Root.GetComponentsInChildren<MeshCollider>()) if (c.enabled && c.attachedRigidbody != null) bodies.Add(c.attachedRigidbody);
                }
            }

            return bodies.Count;
        }

        private static void WriteAggregateRecord(CutWorldRoot root, string what)
        {
            BuildingFusion f = root.Fusion;
            WriteFusionRecord(root, what);
            TestContext.Out.WriteLine("  aggregation: begun " + f.AggregationsBegun + " (deadline " + f.AggregationsByDeadline + ", slash " + f.AggregationsBySlash + "), completed " + f.AggregationsCompleted
                + ", max " + (f.MaxAggregationSeconds * 1000).ToString("F1") + " ms, wait frames " + f.AggregationWaitFrames + "; held " + f.HeldRequests + " (holds ended " + f.HeldProcessed + ", routed " + f.HeldRouted + ", refused at the routing " + f.HeldRefused
                + ", max wait " + (f.MaxHeldWaitSeconds * 1000).ToString("F1") + " ms); free unions " + f.FreeUnions + " (" + f.UnionMembersMoved + " members, " + (f.UnionSeconds * 1000).ToString("F3") + " ms, max " + (f.MaxUnionSeconds * 1000).ToString("F3")
                + "); bodies made/destroyed/reused " + f.BodiesMade + "/" + f.BodiesDestroyed + "/" + f.BodiesReused + ", colliders made/destroyed " + f.CollidersMade + "/" + f.CollidersDestroyed);
            foreach (FusedGroup g in f.Groups) TestContext.Out.WriteLine("  group " + g.Root.name + ": members " + g.MemberCount + ", kinematic " + g.Kinematic + ", anchored " + g.Anchored + ", busy " + g.Busy + ", mass " + g.Mass.ToString("F2"));
            foreach (BuildingFusion.HeldOutcome h in f.HeldOutcomes) TestContext.Out.WriteLine("  held hit slash " + h.slashId + " member " + h.fragment + " waited " + (h.waitSeconds * 1000).ToString("F1") + " ms: " + h.outcome);
            TestContext.Out.WriteLine("  real bodies under the groups' members: " + RealBodiesOf(root, f.Groups));
        }

        /// <summary>
        /// **By the deadline, the free sides of a cut become one dynamic group, with the momentum of both, and rest as
        /// one.** Two fused halves on the floor are cut vertically with a separation impulse (both sides free, sliding
        /// apart); the deadline (0.5 s of real time) comes before either rests: the two free groups are united into one
        /// dynamic body whose linear and angular momentum (about the origin) are the sum of the two, the source body is
        /// gone, and later the one group rests as the building's one kinematic body.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionAggregate_ByTheDeadline_TheFreeSidesBecomeOneDynamicGroup_WithTheirMomentum_ThenRestAsOne()
        {
            CutWorldRoot root = NewAggregatingWorld(0.5);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            int key = root.Fusion.Groups[0].Key;
            double whole = root.Fusion.Groups[0].Mass;

            ProvisionalCutAsk ask = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            Assert.That(root.Fusion.SidesDynamic, Is.EqualTo(2), "both sides are free");
            yield return Until(() => root.Fusion.FinalsPublished == 2 || root.Fusion.FinalsFailed > 0, "the Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "two free groups before the deadline");
            int bodiesBefore = root.Fusion.BodiesDestroyed;
            // A known momentum for the union, not the sliding's: both free groups are given the same velocity every frame
            // until they are united (the Step reads the bodies before the physics steps), so the sum is the whole mass
            // times it and never cancels -- by the deadline their own sliding may have stopped or opposed.
            var given = new float3(1f, 0f, 0f);
            double givenMass = 0.0;
            float momentumDeadline = Time.realtimeSinceStartup + 6f;
            while (root.Fusion.AggregationsCompleted == 0 && Time.realtimeSinceStartup < momentumDeadline)
            {
                givenMass = 0.0;
                foreach (FusedGroup g in root.Fusion.Groups)
                {
                    if (g.Body == null || g.Body.isKinematic) continue;
                    g.Body.linearVelocity = (Vector3)given;
                    g.Body.angularVelocity = Vector3.zero;
                    givenMass += g.Body.mass;
                }

                yield return null;
            }

            Assert.That(root.Fusion.AggregationsByDeadline, Is.EqualTo(1), "the aggregation began by the deadline");
            Assert.That(root.Fusion.AggregationsCompleted, Is.EqualTo(1), "and completed within 6 s");
            WriteAggregateRecord(root, "after the aggregation");
            Assert.That(root.Fusion.FreeUnions, Is.EqualTo(1), "the two free groups were united (the parts move as one from here: the counted approximation)");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1), "one group");
            FusedGroup one = root.Fusion.Groups[0];
            Assert.That(one.Kinematic, Is.False, "and it is the free one (dynamic)");
            Assert.That(one.MemberCount, Is.EqualTo(4), "with every child member");
            Assert.That(one.Body.mass, Is.EqualTo(whole).Within(1e-2), "carrying the whole mass");
            Assert.That(root.Fusion.BodiesDestroyed, Is.EqualTo(bodiesBefore + 1), "the other free body is gone");
            Assert.That(root.Fusion.BodiesReused, Is.GreaterThanOrEqualTo(1), "the target's body was kept");
            Assert.That(root.Fusion.UnionMembersMoved, Is.EqualTo(2), "two members moved into the target");
            Assert.That(RealBodiesOf(root, root.Fusion.Groups), Is.EqualTo(1), "the members' colliders sit on one real body (attachedRigidbody)");
            BuildingFusion.UnionRecord u = root.Fusion.lastUnion;
            TestContext.Out.WriteLine("union: linear " + u.linearBefore + " -> " + u.linearAfter + "; angular " + u.angularBefore + " -> " + u.angularAfter + "; moved " + u.membersMoved + ", mass " + u.massAfter.ToString("F2"));
            Assert.That(u.wholeSource, Is.True, "the whole source moved in one turn");
            Assert.That(math.length(u.linearAfter - u.linearBefore), Is.LessThan(2e-2 * math.max(1.0, math.length(u.linearBefore))), "the linear momentum of the united body is the two bodies' sum (the members' composition against the source body's own)");
            Assert.That(math.length(u.angularAfter - u.angularBefore), Is.LessThan(2e-2 * math.max(1.0, math.length(u.angularBefore))), "the angular momentum (about the origin) is the two bodies' sum");
            Assert.That(math.length(u.linearAfter - givenMass * (double3)given), Is.LessThan(5e-2 * givenMass), "the united body carries the momentum given to the two before the union: their whole mass at the given velocity (" + u.linearAfter + " vs " + (givenMass * (double3)given) + ")");
            Assert.That(root.Rest.TryDescribeGroup(one.Body, out string described), Is.True, "the rest follows the united group");
            TestContext.Out.WriteLine("united group: " + described);
            yield return UntilWithin(() => one.Kinematic && one.Body != null && one.Body.isKinematic, RestSettleSeconds, "the united group rested as the building's one kinematic body");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));
            Assert.That(RigidbodiesUnder(one.Fragments, root), Is.EqualTo(1));
            Assert.That(RealBodiesOf(root, root.Fusion.Groups), Is.EqualTo(1), "one real body, kinematic");
            WriteAggregateRecord(root, "rested");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A different Slash begins the aggregation and its hit is held, then cut after it; more hits of the same
        /// Slash are not a next Slash.** Two fused halves are cut by Slash 1; a second hit of Slash 1 with another plane
        /// on the busy side is held as busy (not an aggregation, not refused); a hit of Slash 2 on the uncut member
        /// begins the aggregation and is held (Pending). The aggregation waits for the Finals, unites the two free
        /// sides, and then cuts the held hit's world plane on the united group: a second group cut. The two other held
        /// hits find their member replaced by that cut's Final and are dropped, on record.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionAggregate_ByTheNextSlash_TheHitIsHeld_AndCutAfterTheAggregation()
        {
            CutWorldRoot root = NewAggregatingWorld(30.0);   // the deadline stays far: only the Slash aggregates here
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            int key = root.Fusion.Groups[0].Key;

            LogicalFragmentId lower = Other(upper, halves);
            root.Fusion.holdFinals = true;   // the sides stay busy until the held hit is in: the same-Slash refusal and the next-Slash hold are read with certainty
            ProvisionalCutAsk first = SlashAsk(root, upper, new float4(0f, 1f, 0f, -0.5f), 1, 0f);   // through the upper half only; the lower half goes uncut to the negative side
            Assert.That(root.TryAsk(in first), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (first)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(1));
            Assert.That(root.Fusion.FinalsPublished, Is.Zero, "the sides are busy with their Finals");
            // The same Slash again, another plane, on the uncut member of a busy side: held as busy, not refused, not an aggregation. (A hit on the crossed member itself is refused by the driver, its cut being active.)
            int refusedBefore = root.Fusion.GroupCutsRefused;
            ProvisionalCutAsk again = SlashAsk(root, lower, new float4(1f, 0f, 0f, 0f), 1, 0f);
            Assert.That(root.TryAsk(in again), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (again)");
            Assert.That(root.Fusion.GroupCutsRefused, Is.EqualTo(refusedBefore), "the same Slash's further hit on the busy group is not refused");
            Assert.That(root.Fusion.HeldRequests, Is.EqualTo(1), "held as busy");
            Assert.That(root.Fusion.Hits[1].state == "held" && root.Fusion.Hits[1].Events.Contains("busy"), Is.True, root.Fusion.Hits[1].Events);
            Assert.That(root.Fusion.AggregationsBegun, Is.Zero);
            // The next Slash: the aggregation begins, the hit is held.
            ProvisionalCutAsk next = SlashAsk(root, lower, new float4(1f, 0f, 0f, 0f), 2, 0f);
            Assert.That(root.TryAsk(in next), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (next)");
            Assert.That(root.Fusion.AggregationsBySlash, Is.EqualTo(1), "Slash 2 began the aggregation");
            Assert.That(root.Fusion.HeldRequests, Is.EqualTo(2), "its hit is held");
            Assert.That(root.Fusion.HeldRequestsOf(key), Is.EqualTo(2));
            // A second hit of Slash 2 while the building aggregates: held as well.
            ProvisionalCutAsk more = SlashAsk(root, lower, new float4(0f, 0f, 1f, 0f), 2, 0f);
            Assert.That(root.TryAsk(in more), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (more)");
            Assert.That(root.Fusion.HeldRequests, Is.EqualTo(3), "the second hit is held too");
            root.Fusion.holdFinals = false;
            Assert.That(root.Fusion.IsAggregating(key, out _, out string trigger) && trigger.Contains("slash 2"), Is.True, trigger);
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1), "no new cut is published under the aggregation");
            yield return UntilWithin(() => root.Fusion.AggregationsCompleted == 1, 5f, "the aggregation completed (after the Finals)");
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the held hit's preparation");
            yield return Until(() => root.Fusion.HeldProcessed == 3, "the other two held hits routed once the second cut's Finals replaced their member");
            WriteAggregateRecord(root, "after the aggregation and the held hits");
            Assert.That(root.Fusion.FinalsPublished, Is.GreaterThanOrEqualTo(1), "the first cut's Final came before the union (a busy group is not united)");
            Assert.That(root.Fusion.FreeUnions, Is.EqualTo(1), "the two free sides were united first");
            Assert.That(root.Fusion.HeldProcessed == 3 && root.Fusion.HeldRouted == 1 && root.Fusion.HeldRefused == 2, Is.True, "the Slash 2 hit was routed into a cut after the aggregation; the two others found their member replaced by its Final");
            Assert.That(root.Fusion.HeldOutcomes.Count, Is.EqualTo(3));
            int routedOutcomes = 0, droppedOutcomes = 0;
            foreach (BuildingFusion.HeldOutcome h in root.Fusion.HeldOutcomes) { if (h.routed && h.outcome.Contains("prepared")) routedOutcomes++; if (!h.routed && h.outcome.StartsWith("Dropped")) droppedOutcomes++; }
            Assert.That(routedOutcomes == 1 && droppedOutcomes == 2, Is.True, "one routed into its own preparation, two dropped with the reason recorded");
            int hitsPublished = 0, hitsDropped = 0;
            foreach (BuildingFusion.HitRecord h in root.Fusion.Hits) { if (h.outcome == "Published") hitsPublished++; if (h.outcome != null && h.outcome.StartsWith("Dropped")) hitsDropped++; }
            Assert.That(hitsPublished == 2 && hitsDropped == 2 && root.Fusion.HitsPending == 0, Is.True, "the hits' own outcomes: the first cut's hit and the Slash 2 hit published, the two others dropped");
            Assert.That(root.Fusion.HeldRequestsOf(key), Is.Zero);
            Assert.That(root.Fusion.IsAggregating(key, out _, out _), Is.False);
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(2), "the held hit's cut is the second group cut");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "its two sides");
            Assert.That(root.Fusion.MembersSplit, Is.GreaterThanOrEqualTo(2), "the held plane (vertical, through the united group) crossed members");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "its Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(root.Fusion.MemberOperations.Count, Is.EqualTo(root.Fusion.FinalsPublished), "every crossed member's operation is on record");
            foreach (BuildingFusion.MemberOperation op in root.Fusion.MemberOperations)
            {
                Assert.That(op.Published && root.Ledger.TryGetOperation(op.operation, out LogicalCutOperation record) && record.positive.Equals(op.positive) && record.negative.Equals(op.negative), Is.True, "the record matches the ledger's operation " + op.operation.value);
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **At the aggregation's completion the resting part is one kinematic body and the free parts one dynamic body,
        /// never absorbed into the resting one; once the free part rests the building is one kinematic body.** An
        /// anchored building's two fused halves are cut vertically (the anchored side stays, the other slides off), then
        /// the free side is cut again by the same Slash into two free groups; the deadline aggregates: the anchored group
        /// stands as it is, the two free groups become one dynamic group. Later the free group rests and merges: one.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionAggregate_TheRestingPartIsOneKinematic_TheFreePartsOneDynamic_ThenTheBuildingIsOne()
        {
            CutWorldRoot root = NewAggregatingWorld(0.7);
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "both fused");
            int key = root.Fusion.Groups[0].Key;
            Assert.That(root.Fusion.Groups[0].Anchored, Is.True);

            ProvisionalCutAsk first = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in first), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (first)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            yield return Until(() => root.Fusion.FinalsPublished == 2 || root.Fusion.FinalsFailed > 0, "the Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            FusedGroup main = null, free = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Kinematic) main = g; else free = g; }
            Assert.That(main != null && main.Anchored && main.MemberCount == 2 && free != null && free.MemberCount == 2, Is.True, "the anchored side (two children) and one free side (two children)");
            // The same Slash cuts the free side horizontally between its two children: two free groups, no cook needed.
            LogicalFragmentId freeMember = free.Representative;
            ProvisionalCutAsk second = SlashAsk(root, freeMember, new float4(0f, 1f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in second), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (second)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(2), "the same Slash cuts on (no aggregation yet)");
            Assert.That(root.Fusion.HeldRequests, Is.Zero);
            yield return Until(() => root.Fusion.CutsInProgress == 0, "its Finals, if any");
            int freeGroups = 0; foreach (FusedGroup g in root.Fusion.Groups) if (!g.Kinematic) freeGroups++;
            Assert.That(freeGroups, Is.EqualTo(2), "two free groups and the anchored one");
            yield return UntilWithin(() => root.Fusion.AggregationsCompleted == 1, 5f, "the deadline's aggregation completed");
            WriteAggregateRecord(root, "after the aggregation");
            Assert.That(root.Fusion.AggregationsByDeadline, Is.EqualTo(1));
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "one resting and one free group");
            int resting = 0; freeGroups = 0; FusedGroup freeOne = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Kinematic) { resting++; Assert.That(ReferenceEquals(g, main) && g.Anchored && g.Body.isKinematic, Is.True, "the resting one is the anchored group, as it was"); } else { freeGroups++; freeOne = g; } }
            Assert.That(resting == 1 && freeGroups == 1, Is.True);
            Assert.That(freeOne.MemberCount, Is.EqualTo(2), "the free group holds both free parts");
            Assert.That(main.MemberCount, Is.EqualTo(2), "the free parts were not absorbed into the resting group");
            Assert.That(root.Fusion.FreeUnions == 1 && root.Fusion.UnionMembersMoved == 1, Is.True, "one union moved the other free group's one member");
            Assert.That(RealBodiesOf(root, root.Fusion.Groups), Is.EqualTo(2), "two real bodies: the anchored one and the free one");
            yield return UntilWithin(() => root.Fusion.GroupCount == 1 && root.Fusion.Groups[0].Kinematic, RestSettleSeconds, "the free group rested and merged: the building is one kinematic body");
            WriteAggregateRecord(root, "one");
            Assert.That(root.Fusion.Groups[0].Body.isKinematic && root.Fusion.Groups[0].MemberCount == 4, Is.True);
            Assert.That(RealBodiesOf(root, root.Fusion.Groups), Is.EqualTo(1), "one real body, kinematic");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A completed aggregation is not begun again for the same cuts.** An anchored building high above the floor:
        /// its free side is cut into two free groups by the same Slash; the deadline aggregates them into one free group
        /// that keeps falling. Through several deadlines more, with the resting group and the free group standing apart,
        /// no aggregation begins again (nothing was cut since); a new cut of the free group makes the next generation,
        /// which the deadline aggregates once.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionAggregate_NothingCutSinceTheLastAggregation_IsNotAggregatedAgain()
        {
            CutWorldRoot root = NewAggregatingWorld(0.3, -30f);
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "both fused");
            int key = root.Fusion.Groups[0].Key;
            ProvisionalCutAsk first = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in first), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (first)");
            yield return Until(() => root.Fusion.FinalsPublished == 2 || root.Fusion.FinalsFailed > 0, "the Finals");
            FusedGroup free = null; foreach (FusedGroup g in root.Fusion.Groups) if (!g.Kinematic) free = g;
            Assert.That(free, Is.Not.Null);
            ProvisionalCutAsk second = SlashAsk(root, free.Representative, new float4(0f, 1f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in second), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (second)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(2));
            yield return UntilWithin(() => root.Fusion.AggregationsCompleted == 1, 5f, "the deadline's aggregation completed");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "one resting and one free group");
            int begun = root.Fusion.AggregationsBegun, unions = root.Fusion.FreeUnions;
            double realBefore = root.Fusion.realSeconds();
            // Several deadlines pass with the two groups standing apart (the free one still falling).
            yield return UntilWithin(() => root.Fusion.realSeconds() - realBefore > 4 * root.Fusion.Settings.deadlineSeconds, 5f, "four deadlines later");
            WriteAggregateRecord(root, "four deadlines after the completion");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "the resting and the free group still stand apart");
            Assert.That(root.Fusion.AggregationsBegun, Is.EqualTo(begun), "no aggregation began again: nothing was cut since");
            Assert.That(root.Fusion.FreeUnions, Is.EqualTo(unions));
            Assert.That(root.Fusion.IsAggregating(key, out _, out _), Is.False);
            // A new cut of the free group is the next generation: aggregated once more by the deadline.
            FusedGroup freeNow = null; foreach (FusedGroup g in root.Fusion.Groups) if (!g.Kinematic) freeNow = g;
            Assert.That(freeNow, Is.Not.Null);
            Bounds b = GroupBoundsOf(root, freeNow);
            ProvisionalCutAsk third = SlashAsk(root, freeNow.Representative, new float4(1f, 0f, 0f, -b.center.x), 1, 12f);
            Assert.That(root.TryAsk(in third), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (third)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(3), "the free group was cut again");
            yield return UntilWithin(() => root.Fusion.AggregationsCompleted == 2, 6f, "the next generation was aggregated once");
            Assert.That(root.Fusion.AggregationsBegun, Is.EqualTo(begun + 1));
            yield return EndWorld(root);
        }

        /// <summary>
        /// (Kept from main, 2026-10-02: the same check as PhysicsStep.cs's plain-world case, in a world with a building that
        /// has rested and fused before the estimate is injected.)
        /// **A stale high cost estimate does not hold the physics for ever: a verification step measures again and the
        /// steps resume, with the owed time capped.** The estimate is set as if five simulations had cost a second; the
        /// decisions that are owed a step and have budget skip for the cost alone and are counted; after the bound of
        /// such decisions one verification step runs past the budget, the estimate starts over from its real cost, and
        /// the steps resume at the step's pace -- the time owed never exceeded the cap while the steps were skipped.
        /// </summary>
        [UnityTest]
        public IEnumerator PhysicsStep_AStaleHighEstimate_WithARestedFusedBuilding_IsMeasuredAgainByAVerificationStep_AndTheStepsResume()
        {
            CutWorldRoot root = NewAggregatingWorld(2.0);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            yield return UntilFused(root, 1, "the building rested and fused (a group resting later would ask for a re-evaluation of its own)");
            yield return PhysicsSeconds(0.2);
            ManualPhysicsClock clock = CutPhysicsStep.Clock;
            double cap = ManualPhysicsClock.MaxOwedSteps * clock.StepSeconds;
            int verificationsBefore = CutPhysicsStep.VerificationSteps;
            CutPhysicsStep.InjectExpectedCostForTest(1.0);
            long stepAtInjection = clock.StepId;
            int frames = 0, budgetShort = 0, maxSkips = 0;
            double maxOwed = 0.0;
            float deadline = Time.realtimeSinceStartup + 10f;
            while (CutPhysicsStep.VerificationSteps == verificationsBefore && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                frames++;
                if (CutPhysicsStep.VerificationSteps > verificationsBefore) break;   // the verification step itself
                if (CutPhysicsStep.LastDecisionRemainingSeconds < 0.0) budgetShort++;
                maxSkips = Mathf.Max(maxSkips, CutPhysicsStep.SkippedForCostInARow);
                maxOwed = System.Math.Max(maxOwed, clock.UnsimulatedSeconds);
                Assert.That(clock.StepId, Is.EqualTo(stepAtInjection), "no step while the estimate holds and the bound is not reached");
            }

            TestContext.Out.WriteLine("verification after " + frames + " frames (" + budgetShort + " with the budget already spent): cost " + (CutPhysicsStep.LastVerificationSeconds * 1000).ToString("F3") + " ms, overrun "
                + (CutPhysicsStep.VerificationOverrunSeconds * 1000).ToString("F3") + " ms, skipped decisions counted up to " + maxSkips + ", most owed " + (maxOwed * 1000).ToString("F1") + " ms (cap " + (cap * 1000).ToString("F1")
                + "), dropped " + (clock.DroppedSeconds * 1000).ToString("F1") + " ms over the run, expected now " + (CutPhysicsStep.ExpectedSimulateSeconds * 1000).ToString("F3") + " ms");
            Assert.That(CutPhysicsStep.VerificationSteps, Is.EqualTo(verificationsBefore + 1), "one verification step");
            Assert.That(clock.StepId, Is.EqualTo(stepAtInjection + 1), "and it is the one step taken");
            Assert.That(maxSkips, Is.GreaterThanOrEqualTo(CutPhysicsStep.VerifyAfterSkippedFrames - 1), "after the bound of decisions skipped for the cost alone");
            Assert.That(CutPhysicsStep.ExpectedSimulateSeconds, Is.LessThan(0.01), "the estimate starts over from the real cost");
            Assert.That(maxOwed, Is.LessThanOrEqualTo(cap + 1e-9), "what was owed never exceeded the cap (no catch-up is stored)");
            long resumedFrom = clock.StepId;
            yield return UntilWithin(() => clock.StepId >= resumedFrom + 10, 3f, "the steps resumed");
            Assert.That(CutPhysicsStep.SkippedForCostInARow, Is.Zero);
            yield return EndWorld(root);
        }
    }
}
