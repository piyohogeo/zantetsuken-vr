using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The hull trial's boundaries (TL, 2026-09-30): a display cut the ledger cannot take leaves the physics and the
    /// display both as they were; a transient shortage of ledger room waits with the old state and then publishes; the
    /// dispatcher refusing or cancelling a candidate's offer never runs it on Main and the candidate still completes; a
    /// refused candidate leaves the groups apart and cuttable, is tried again, and is adopted once the refusal lifts.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static float3 CentreOf(HullGroup g)
        {
            g.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
            return math.transform((float4x4)g.Root.transform.localToWorldMatrix, (lo + hi) * 0.5f);
        }

        private static void AssertUnchanged(BuildingHullFusion h, HullGroup g, int generation, int members, Mesh mesh, int groups, string when)
        {
            Assert.That(g.Generation, Is.EqualTo(generation), when + ": the generation");
            Assert.That(g.MemberCount, Is.EqualTo(members), when + ": the display members");
            Assert.That(g.Collider.sharedMesh == mesh, Is.True, when + ": the collider's mesh");
            Assert.That(g.State, Is.EqualTo(HullGroupState.Idle), when + ": idle again");
            Assert.That(g.HullCount, Is.EqualTo(1), when + ": one hull");
            Assert.That(h.GroupCount, Is.EqualTo(groups), when + ": the groups");
            Assert.That(h.CutsInProgress, Is.Zero, when + ": no cut left");
        }

        /// <summary>
        /// **A display cut the ledger cannot take refuses the whole cut, physics and display both old.** The building's
        /// display member is put under another operation; the hull is hit and its cut cooked, and at the publication the
        /// display cut is judged inadmissible first: refused, the group's hull, collider, body, generation and member as
        /// they were, the member still live and placed, the refusal counted. Then the operation is aborted (the member
        /// retired): a next hit is refused for the member not being live, again with nothing changed.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_DisplayCutInadmissible_RefusesWithPhysicsAndDisplayOld()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup group = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f), new float3(0.5f, -0.9f, 0f) }, 12.0, out LogicalFragmentId member);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Mesh mesh = group.Collider.sharedMesh;
            Rigidbody body = group.Body;
            Assert.That(root.Ledger.Admit(member, new float4(0f, 1f, 0f, 0f), true, out CutOperationId foreign), Is.EqualTo(LogicalCutAdmission.Admitted), "the member is under another operation");

            Evaluate(detector, Upright(1, 0.2f), 1);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "the hull is hit and its cut prepares");
            yield return UntilWithin(() => h.CutsInProgress == 0, 30f, "the cut ended");
            WriteHullRecord(root, "after the inadmissible display cut");
            Assert.That(h.GroupCuts, Is.Zero, "not published");
            Assert.That(h.DisplayCutsRefused, Is.EqualTo(1));
            Assert.That(h.HitsRefused, Is.EqualTo(1));
            Assert.That(h.CutsFailed, Is.EqualTo(1));
            Assert.That(h.Hits[0].outcome, Does.StartWith("Refused: display member"), h.Hits[0].outcome);
            Assert.That(h.Hits[0].displayOperations, Is.Empty, "no display operation was admitted");
            AssertUnchanged(h, group, 1, 1, mesh, 1, "after the refusal");
            Assert.That(group.Body == body && body.isKinematic && group.Kinematic, Is.True, "the body, held");
            Assert.That(root.Ledger.IsCurrentTarget(member), Is.True, "the member is live");
            Assert.That(root.Ledger.TryGetActiveOperation(member, out CutOperationId active) && active == foreign, Is.True, "and still under the other operation");
            Assert.That(root.Ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "the ledger holds the other operation only");
            AssertDisplayConsistent(root, "after the refusal");
            Assert.That(group.IsConsumedBy(1), Is.True, "the Slash stays consumed");
            Assert.That(PhysicsCutClassification.Live, Is.Zero, "no classification block left by the refused cut");

            // The other operation aborted: the member retired, the group without a display. A next hit finds no display on
            // either side: no change, no cook, no physics group for an empty display side; nothing changed.
            Assert.That(root.Ledger.Abort(foreign), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(root.Ledger.IsCurrentTarget(member), Is.False);
            int cooks = h.CookRequests;
            Evaluate(detector, Upright(2, -0.2f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilWithin(() => h.CutsInProgress == 0, 30f, "the second cut ended");
            Assert.That(h.GroupCuts, Is.Zero);
            Assert.That(h.CookRequests, Is.EqualTo(cooks), "no cook for a display on no side");
            Assert.That(h.NoChanges, Is.EqualTo(1));
            Assert.That(h.DisplayCutsRefused, Is.EqualTo(1));
            Assert.That(h.Hits[1].outcome, Does.StartWith("NoChange"), h.Hits[1].outcome);
            AssertUnchanged(h, group, 1, 1, mesh, 1, "after the no-change hit");
            WriteHullRecord(root, "at the end");
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero);
        }

        /// <summary>
        /// **Short of ledger room the cut waits with the old state, then publishes; past the ledger's capacity it is
        /// refused.** The ledger's incomplete budget is one. A bare fragment's operation takes it: the hull's cut cooks
        /// and then waits at its publication (counted; the group cutting, its hull, collider and generation old; the hit
        /// pending); the operation aborted, the cut publishes on the next Step. After the fusion the group has two members;
        /// a plane crossing both asks two display cuts of a ledger of one: refused, nothing changed.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_LedgerRoom_WaitsThenPublishes_AndPastCapacityRefuses()
        {
            CutWorldRoot root = NewHullWorld(0.5f, profile => SetPrivate(profile, "maxIncompleteCuts", 1));
            BuildingHullFusion h = root.Hulls;
            Assert.That(root.Ledger.Budget.MaxIncompleteCutOperationCount, Is.EqualTo(1));
            HullGroup group = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Mesh mesh = group.Collider.sharedMesh;
            LogicalFragmentId bare = root.Ledger.AddFragment();
            Assert.That(root.Ledger.Admit(bare, new float4(0f, 1f, 0f, 0f), true, out CutOperationId foreign), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(root.Ledger.Budget.IsFull, Is.True, "no room in the ledger");

            Evaluate(detector, Upright(1, 0.3f), 1);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True);
            yield return UntilWithin(() => h.RoomWaits >= 3, 30f, "the cut waits for room");
            Assert.That(group.State == HullGroupState.Cutting && group.Collider.sharedMesh == mesh && group.Generation == 1 && h.GroupCount == 1 && h.GroupCuts == 0, Is.True, "the old state stands while the cut waits");
            Assert.That(h.Hits[0].IsPending, Is.True);
            Assert.That(h.CutsInProgress, Is.EqualTo(1));

            Assert.That(root.Ledger.Abort(foreign), Is.EqualTo(LogicalCutResultOutcome.Applied), "the room given back");
            yield return UntilWithin(() => h.GroupCuts == 1, 30f, "the waiting cut published");
            WriteHullRecord(root, "after the wait");
            Assert.That(h.RoomWaits, Is.GreaterThanOrEqualTo(3));
            Assert.That(h.GroupCount, Is.EqualTo(2));
            Assert.That(h.DisplayCuts, Is.EqualTo(1));
            Assert.That(h.Hits[0].outcome, Is.EqualTo("Published"));
            yield return UntilOneGroup(root, "fused", 60f);
            HullGroup fused = h.Groups[0];
            Assert.That(fused.MemberCount, Is.EqualTo(2));
            Assert.That(h.DisplayOperationsOpen, Is.Zero, "the display operation ended");
            AssertDisplayConsistent(root, "after the fusion");

            // Two members crossed, a ledger of one: refused before anything is taken.
            int generation = fused.Generation;
            Mesh fusedMesh = fused.Collider.sharedMesh;
            float3 c = CentreOf(fused);
            Evaluate(detector, Level(2, c.y, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1), "the fused hull is hit");
            yield return UntilWithin(() => h.CutsInProgress == 0, 30f, "the cut ended");
            WriteHullRecord(root, "after the refusal past the capacity");
            Assert.That(h.GroupCuts, Is.EqualTo(1), "not published");
            Assert.That(h.DisplayCutsRefused, Is.EqualTo(1));
            Assert.That(h.Hits[h.Hits.Count - 1].outcome, Does.StartWith("Refused: 2 display cuts"), h.Hits[h.Hits.Count - 1].outcome);
            AssertUnchanged(h, fused, generation, 2, fusedMesh, 1, "after the refusal");
            Assert.That(root.Ledger.Budget.IncompleteCutOperationCount, Is.Zero);
            AssertDisplayConsistent(root, "after the refusal");
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero);
        }

        /// <summary>
        /// **The dispatcher refusing or cancelling a fusion's offer: nothing runs on Main, the fusion completes.** Three
        /// offers refused: the work waits and is offered again each Step, then scanned and baked off Main. An offer cancelled
        /// while waiting (as a shutdown would): collected Cancelled before it began, offered again, completed. The meshes
        /// baked count the registration's, two a cut and one a fusion.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_OffersRefusedOrCancelled_FusionStillCompletesOffMain()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f), new float3(0.5f, -0.9f, 0.5f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;

            h.refuseOffersForTest = 3;
            Evaluate(detector, Upright(1, 0.2f), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilWithin(() => h.GroupCuts == 1, 30f, "the cut");
            yield return UntilOneGroup(root, "fused after the refused offers", 60f);
            WriteHullRecord(root, "after the refused offers");
            Assert.That(h.OffersRefused, Is.GreaterThanOrEqualTo(3), "the offers were refused");
            Assert.That(h.refuseOffersForTest, Is.Zero, "and each refusal spent");
            Assert.That(h.Fusions, Is.EqualTo(1));
            Assert.That(h.FusionBakes, Is.EqualTo(1), "baked off Main");
            Assert.That(h.MeshesBaked, Is.EqualTo(1 + 2 + 1));
            AssertDisplayConsistent(root, "after the refused offers");

            int cancels = 0;
            h.cancelOfferForTest = () => cancels++ == 0;
            float3 c = CentreOf(h.Groups[0]);
            Evaluate(detector, Level(2, c.y + 0.1f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilWithin(() => h.GroupCuts == 2, 30f, "the second cut");
            yield return UntilOneGroup(root, "fused after the cancelled offer", 60f);
            h.cancelOfferForTest = null;
            WriteHullRecord(root, "after the cancelled offer");
            Assert.That(h.Reoffers, Is.EqualTo(1), "the cancelled work was offered again");
            Assert.That(h.Fusions, Is.EqualTo(2));
            Assert.That(h.FusionBakes, Is.EqualTo(2));
            Assert.That(h.MeshesBaked, Is.EqualTo(1 + 4 + 2));
            Assert.That(h.CutsFailed, Is.Zero);
            AssertDisplayConsistent(root, "after the cancelled offer");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A refused candidate leaves the groups apart and cuttable, is tried again after the deadline, and is adopted
        /// once the refusal lifts.** The hull scan reports a refusal: the two groups stand apart, each its own hull and
        /// collider, no body merged, the trial not settled (a retry waits). A hit on one of them meanwhile is accepted at
        /// once (not held) and its cut published: the class changed, the refusal's retry is the old class's. The
        /// refusal lifted: the resting groups' next candidate is adopted into one group.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_CandidateRefused_GroupsApartAndCuttable_AdoptedAfterTheRefusalLifts()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f), new float3(0.5f, -0.9f, 0.5f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;

            h.refuseHullForTest = true;
            Evaluate(detector, Upright(1, 0.2f), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilWithin(() => h.GroupCuts == 1, 30f, "the cut");
            yield return UntilWithin(() => h.FusionsRefused >= 1, 60f, "the candidate refused");
            yield return UntilWithin(() => h.FusionsInFlight == 0, 30f, "the refused work collected");
            WriteHullRecord(root, "after the refused candidate");
            Assert.That(h.GroupCount, Is.EqualTo(2), "the groups stand apart");
            foreach (HullGroup g in h.Groups) Assert.That(g.HullCount == 1 && g.State == HullGroupState.Idle && g.Kinematic, Is.True, "group " + g.Id + ": its own hull, idle, resting");
            Assert.That(h.LiveColliders, Is.EqualTo(2));
            Assert.That(h.Unions == 0 && h.Fusions == 0, Is.True, "no body merged");
            Assert.That(h.IsSettled, Is.False, "a refused scan awaiting its retry is not a settled state: " + h.DescribeUnsettled());
            StringAssert.Contains("awaiting a retry 1", h.DescribeUnsettled());
            yield return UntilWithin(() => h.FusionsRetried >= 1, 30f, "the candidate tried again");
            yield return UntilWithin(() => h.FusionsRefused >= 2 && h.FusionsInFlight == 0, 30f, "and refused again");

            // A hit meanwhile: accepted at once, the cut published; the class changed, the old class's retry is over.
            HullGroup target = null;
            foreach (HullGroup g in h.Groups) if (target == null || g.Mass > target.Mass) target = g;
            yield return RecutJudged(root, detector, target, 2, "a group while the candidate is refused");
            Assert.That(h.BuildingsAwaitingRetry, Is.Zero, "the refused class is gone with the cut");

            h.refuseHullForTest = false;
            yield return UntilHull(root, () => h.Fusions >= 1, 90f, "the next candidate adopted (the freed side rested first)");
            yield return UntilOneGroup(root, "one group", 60f);
            WriteHullRecord(root, "at the end");
            Assert.That(h.Groups[0].HullCount, Is.EqualTo(1));
            Assert.That(h.LiveColliders, Is.EqualTo(1));
            Assert.That(h.CutsFailed, Is.Zero);
            Assert.That(h.IsSettled && h.IsOneHullAchieved, Is.True, h.DescribeUnsettled());
            AssertDisplayConsistent(root, "at the end");
            yield return EndWorld(root);
        }
    }
}
