using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The fusion's Main budget and the preparation off Main (2026-09-30): a cut is prepared on a worker and published
    /// in a later Step; a hit on a group being prepared is held, not refused; under a small budget the Finals and a
    /// union are spread over frames with the physics state whole between them; an offer the dispatcher cancels is
    /// offered again with the budget out; the ending returns the inputs of an unstarted and of a running preparation,
    /// and what is left behind is named, not tolerated; hits while a group is prepared go by group, Slash and plane.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private CutWorldRoot NewBudgetWorld(double mainBudgetMs, float floorTop = -1f, double deadlineSeconds = 2.0, Func<WorkDestination, IWorkExecutor> executors = null)
        {
            CutWorldRoot root = NewWorld(out Shader _, executors, null, profile =>
            {
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingWorldEnabled", false);
                SetPrivate(profile, "buildingFusionEnabled", true);
                SetPrivate(profile, "buildingFusionPerFrame", 64);
                SetPrivate(profile, "buildingFusionDeadlineSeconds", (float)deadlineSeconds);
                SetPrivate(profile, "buildingFusionMainBudgetMs", (float)mainBudgetMs);
            });
            Assert.That(root.Fusion, Is.Not.Null, "the fusion is on");
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, floorTop - 0.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
            return root;
        }

        /// <summary>Every mesh alive now, by instance id, with its name: what the ending must give back is the difference.</summary>
        private static Dictionary<int, string> MeshesNow()
        {
            var meshes = new Dictionary<int, string>();
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>()) meshes[m.GetInstanceID()] = m.name;
            return meshes;
        }

        /// <summary>
        /// The meshes alive now that were not alive before and are not the fixture's own (those it tracks -- the authored
        /// collider mesh of every body it made -- go at its teardown, after the case): each named, so that what remains
        /// has an owner, not a tolerance.
        /// </summary>
        private List<string> MeshesLeftSince(Dictionary<int, string> before)
        {
            var fixtures = new HashSet<int>();
            foreach (UnityEngine.Object o in _objects) if (o is Mesh m) fixtures.Add(m.GetInstanceID());
            var left = new List<string>();
            foreach (KeyValuePair<int, string> m in MeshesNow()) if (!before.ContainsKey(m.Key) && !fixtures.Contains(m.Key)) left.Add(m.Value + " #" + m.Key);
            return left;
        }

        /// <summary>The shapes of a group's members, taken while they stand, to check their work holds after.</summary>
        private static List<PhysicsOwnerShape> ShapesOf(CutWorldRoot root, FusedGroup group)
        {
            var shapes = new List<PhysicsOwnerShape>();
            foreach (LogicalFragmentId f in group.Fragments) if (root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.Shape != null) shapes.Add(o.Shape);
            return shapes;
        }

        /// <summary>The physics state of the groups is whole: every enabled member collider sits on its group's body, no retired member's collider is enabled, and the groups' masses sum to the whole.</summary>
        private static void AssertGroupsWhole(CutWorldRoot root, double whole, string when)
        {
            double sum = 0.0;
            foreach (FusedGroup g in root.Fusion.Groups)
            {
                sum += g.Mass;
                foreach (LogicalFragmentId f in g.Fragments)
                {
                    Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.Root != null, Is.True, when + ": member " + f.value + " stands");
                    Assert.That(root.Ledger.IsCurrentTarget(f), Is.True, when + ": member " + f.value + " is live");
                    foreach (MeshCollider c in o.Root.GetComponentsInChildren<MeshCollider>()) if (c.enabled) Assert.That(c.attachedRigidbody, Is.EqualTo(g.Body), when + ": the collider of " + f.value + " sits on its group's body");
                }
            }

            Assert.That(sum, Is.EqualTo(whole).Within(1e-2), when + ": the groups' masses sum to the whole");
        }

        /// <summary>
        /// **A cut is prepared off Main and published in a later Step.** The ask is answered as a preparation (in flight,
        /// no group cut yet); the scan ran on a worker thread (not Main's); a later Step judges and publishes it, and the
        /// sides' masses were applied by the end of that Step (before the physics stepped).
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_ACutIsPreparedOffMain_AndPublishedInALaterStep()
        {
            CutWorldRoot root = NewBudgetWorld(1.5);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            double whole = root.Fusion.Groups[0].Mass;
            int mainThread = Thread.CurrentThread.ManagedThreadId;

            ProvisionalCutAsk ask = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(1), "the hit began a preparation");
            Assert.That(root.Fusion.GroupCuts, Is.Zero, "nothing published in the frame of the ask");
            Assert.That(root.Fusion.Groups[0].Preparing, Is.EqualTo(1), "the group is being prepared");
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation");
            WriteAggregateRecord(root, "after the preparation");
            Assert.That(root.Fusion.PreparationsPublished, Is.EqualTo(1), "published");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            Assert.That(root.Fusion.lastPreparationThreadId, Is.Not.EqualTo(mainThread).And.Not.EqualTo(0), "the scan ran on a worker thread (" + root.Fusion.lastPreparationThreadId + " vs Main " + mainThread + ")");
            Assert.That(root.Fusion.WorkerSeconds, Is.GreaterThan(0.0));
            Assert.That(root.Fusion.PendingMassCount, Is.Zero, "the sides' masses were applied by the end of the Step");
            foreach (FusedGroup g in root.Fusion.Groups) Assert.That(g.Body.mass, Is.EqualTo(g.Mass).Within(1e-2), "the body carries the side's mass");
            AssertGroupsWhole(root, whole, "after the publication");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A hit on a group whose cut is being prepared is held with its reason, and routed when its member's group is
        /// free; a preparation of a changed group ends one way.** A cut through the upper half only is prepared; a hit
        /// of the same Slash with another plane on the lower member finds the group being prepared: held (not refused,
        /// not a second preparation of the same group), and after the publication its member stands in the lower side,
        /// where it is prepared and published once that group's Finals are done. Then a hit of another Slash on a side
        /// is prepared or held for the aggregation, and every preparation ends published, refused or abandoned.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_APreparationOfAChangedGroup_IsNotPublishedOnTheOldState()
        {
            CutWorldRoot root = NewBudgetWorld(1.5, -1f, 30.0);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            yield return UntilFused(root, 2, "the halves fused");
            FusedGroup group = root.Fusion.Groups[0];
            int generation = group.Generation;

            // A hit, then a second (the same Slash, another plane) on the uncut member while the first is being prepared: held, never a second preparation of the same group.
            root.Fusion.workerGate = new ManualResetEventSlim(false);
            ProvisionalCutAsk first = SlashAsk(root, upper, new float4(0f, 1f, 0f, -0.5f), 1, 0f);   // through the upper half only
            Assert.That(root.TryAsk(in first), Is.True);
            yield return null;
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(1));
            Assert.That(group.Preparing, Is.EqualTo(1));
            ProvisionalCutAsk second = SlashAsk(root, lower, new float4(0f, 0f, 1f, 0f), 1, 0f);
            Assert.That(root.TryAsk(in second), Is.True);
            yield return null;
            WriteAggregateRecord(root, "after the second ask (the first still being prepared: the worker is gated)");
            Assert.That(root.Fusion.GroupCutsRefused, Is.Zero, "not refused");
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(1), "no second preparation of the group");
            Assert.That(root.Fusion.HitsHeld, Is.EqualTo(1), "held");
            Assert.That(root.Fusion.Hits.Count, Is.EqualTo(2), "two hits, one record each");
            Assert.That(root.Fusion.Hits[1].state == "held" && root.Fusion.Hits[1].Events.Contains("preparing") && root.Fusion.Hits[1].IsPending, Is.True, root.Fusion.Hits[1].Events);
            root.Fusion.workerGate.Set();
            yield return Until(() => root.Fusion.PreparationsPublished == 1, "the first preparation");
            Assert.That(group.Root == null || group.Generation != generation, Is.True, "the group went with its sides");
            yield return Until(() => root.Fusion.HeldProcessed == 1 && root.Fusion.PreparationsInFlight == 0, "the held hit's own preparation on the lower side, once that group's Finals are done");
            WriteAggregateRecord(root, "after the held hit");
            Assert.That(root.Fusion.HeldOutcomes[0].routed && root.Fusion.HeldOutcomes[0].outcome.Contains("prepared"), Is.True, "the held hit was routed into a preparation on its member's group: " + root.Fusion.HeldOutcomes[0].outcome);
            Assert.That(root.Fusion.Hits[1].outcome == "Published" && root.Fusion.Hits[1].preparation == 2, Is.True, "the held hit's own record carries its preparation and outcome: " + root.Fusion.Hits[1].Events);
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(2));
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");

            // A preparation that outlives a change: the group is united into another (an aggregation) while its cut is being prepared.
            // Made by hand: a hit on a side, then the other side merged into it before the Step publishes.
            FusedGroup a = root.Fusion.Groups[0];
            int generationA = a.Generation;
            ProvisionalCutAsk third = SlashAsk(root, a.Representative, new float4(1f, 0f, 0f, 0f), 2, 0f);
            Assert.That(root.TryAsk(in third), Is.True);
            yield return null;
            int staleBefore = root.Fusion.PreparationsStale;
            WriteAggregateRecord(root, "third hit prepared (may be held for the aggregation instead)");
            yield return Until(() => root.Fusion.PreparationsInFlight == 0 && root.Fusion.HeldRequestsOf(a.Key) == 0, "the third hit's preparation, or its aggregation and cut");
            WriteAggregateRecord(root, "after the third hit");
            Assert.That(root.Fusion.PreparationsAbandoned + root.Fusion.PreparationsPublished + root.Fusion.PreparationsRefused, Is.EqualTo(root.Fusion.PreparationsMade), "every preparation ended one way");
            Assert.That(root.Fusion.PreparationsInFlight, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Under a small budget the Finals are collected over several frames, the physics state whole between them.**
        /// A column of four fused pieces is cut vertically (four crossed members, four Finals); with a budget of
        /// 0.02 ms the Step collects one Final a frame at most: units are deferred, the Finals arrive over more than one
        /// frame, and after every frame each collected member's colliders belong to its side's body, no retired member
        /// answers, and the sides' masses sum to the whole.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_TheFinalsAreCollectedOverFrames_WithTheStateWholeBetweenThem()
        {
            CutWorldRoot root = NewBudgetWorld(0.02);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            var bottom = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 1f, 0f, 0.5f), bottom);
            yield return UntilFused(root, 4, "four fused in a column");
            double whole = root.Fusion.Groups[0].Mass;
            ProvisionalCutAsk ask = SlashAsk(root, top[0], new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return Until(() => root.Fusion.GroupCuts == 1, "the cut published (the preparation, then the publication under the budget)");
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(4), "four crossed members");
            int finalFrames = 0, lastFinals = 0;
            var framesWithFinals = new List<int>();
            float finalsDeadline = Time.realtimeSinceStartup + 10f;
            while (root.Fusion.CutsInProgress > 0 && Time.realtimeSinceStartup < finalsDeadline)
            {
                yield return null;
                finalFrames++;
                if (root.Fusion.FinalsPublished != lastFinals) { framesWithFinals.Add(root.Fusion.FinalsPublished - lastFinals); lastFinals = root.Fusion.FinalsPublished; }
                AssertGroupsWhole(root, whole, "frame " + finalFrames + " with " + root.Fusion.FinalsPublished + " Finals");
            }

            WriteAggregateRecord(root, "after the Finals: per frame [" + string.Join(",", framesWithFinals) + "]");
            Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(4));
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(framesWithFinals.Count, Is.GreaterThan(1), "the Finals came over more than one frame");
            Assert.That(root.Fusion.DeferredForBudget, Is.GreaterThan(0), "units were deferred for the budget");
            Assert.That(root.Fusion.OverrunFrames, Is.GreaterThan(0), "a unit that began ran past the tiny budget (recorded, not cut short)");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Under a small budget a union is spread over frames, each batch whole.** Two free groups of four members each
        /// (two columns cut apart, both sides free) are united by the deadline with a 0.02 ms budget: the members move
        /// a few a frame; after every frame each member's collider sits on its current group's body and the groups'
        /// masses sum to the whole; the union completes with the momentum of the two.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_AUnionIsSpreadOverFrames_EachBatchWhole()
        {
            CutWorldRoot root = NewBudgetWorld(0.02, -1f, 0.5);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            var bottom = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 1f, 0f, 0.5f), bottom);
            yield return UntilFused(root, 4, "four fused in a column");
            double whole = root.Fusion.Groups[0].Mass;
            ProvisionalCutAsk ask = SlashAsk(root, top[0], new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return Until(() => root.Fusion.GroupCuts == 1, "the cut");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "two free groups of four");
            int frames = 0;
            var moved = new List<int>();
            int lastMoved = 0;
            float deadline = Time.realtimeSinceStartup + 8f;
            while (root.Fusion.AggregationsCompleted == 0 && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                frames++;
                if (root.Fusion.UnionMembersMoved != lastMoved) { moved.Add(root.Fusion.UnionMembersMoved - lastMoved); lastMoved = root.Fusion.UnionMembersMoved; }
                AssertGroupsWhole(root, whole, "frame " + frames + " with " + root.Fusion.UnionMembersMoved + " members moved");
                foreach (FusedGroup g in root.Fusion.Groups) if (g.Body != null && !g.Body.isKinematic) Assert.That(g.Body.mass, Is.EqualTo(g.Mass).Within(1e-2), "frame " + frames + ": the body's mass is its group's between batches");
            }

            WriteAggregateRecord(root, "after the union: per frame [" + string.Join(",", moved) + "]");
            Assert.That(root.Fusion.AggregationsCompleted, Is.EqualTo(1));
            Assert.That(root.Fusion.FreeUnions, Is.EqualTo(1));
            Assert.That(root.Fusion.UnionMembersMoved, Is.EqualTo(4));
            Assert.That(moved.Count, Is.GreaterThan(1), "the members moved over more than one frame");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));
            BuildingFusion.UnionRecord u = root.Fusion.lastUnion;
            Assert.That(u.wholeSource, Is.True);
            Assert.That(math.length(u.linearAfter - u.linearBefore), Is.LessThan(5e-2 * math.max(1.0, math.length(u.linearBefore))), "the last batch's momentum is the two bodies' sum");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The ending leaves nothing in flight behind.** A preparation just made (its scan on the worker), a held hit,
        /// and a union half done under a tiny budget are all in flight when the world ends: the ending completes on the
        /// ordinary frames, nothing throws, no classification or snapshot block outlives it, and every mesh alive after
        /// it was alive before it -- anything else is named in the failure, not tolerated.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_TheEnding_LeavesNothingInFlight()
        {
            Dictionary<int, string> meshesBefore = MeshesNow();
            int classificationsBefore = PhysicsCutClassification.Live, blocksBefore = BuildingFusion.LiveSnapshotBlocks;
            CutWorldRoot root = NewBudgetWorld(0.02, -1f, 0.3);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            yield return UntilFused(root, 3, "three fused");
            ProvisionalCutAsk first = SlashAsk(root, top[0], new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in first), Is.True);
            yield return Until(() => root.Fusion.GroupCuts == 1, "the first cut");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "its Finals");
            // The union begins by the deadline and, under the tiny budget, stays half done; a hit of the next Slash is held; another is being prepared.
            yield return UntilWithin(() => root.Fusion.UnionMembersMoved > 0 && root.Fusion.AggregationsCompleted == 0, 5f, "a union half done");
            FusedGroup any = root.Fusion.Groups[0];
            ProvisionalCutAsk held = SlashAsk(root, any.Representative, new float4(0f, 0f, 1f, 0f), 2, 0f);
            Assert.That(root.TryAsk(in held), Is.True);
            yield return null;
            WriteAggregateRecord(root, "at the ending");
            Assert.That(root.Fusion.HeldRequests + root.Fusion.PreparationsInFlight, Is.GreaterThan(0), "something is in flight");
            yield return EndWorld(root);
            yield return null;
            yield return null;
            Assert.That(root.Fusion, Is.Null);
            Assert.That(GameObject.Find("Fused Group 1 +") == null && GameObject.Find("Fused Group 1 -") == null, Is.True, "no group object remains");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classificationsBefore), "no classification outlives the world");
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore), "no snapshot block outlives the world");
            List<string> left = MeshesLeftSince(meshesBefore);
            TestContext.Out.WriteLine("meshes before " + meshesBefore.Count + ", left after the ending: " + left.Count + (left.Count > 0 ? " [" + string.Join(", ", left) + "]" : ""));
            Assert.That(left, Is.Empty, "meshes left behind by what was in flight, by name");
        }

        /// <summary>
        /// **An offer the dispatcher cancels before it began is offered again, with the budget out right after.** With a
        /// 0.5 ms budget (the snapshot fits in one Step), the Step's offer is taken back from the dispatcher's queue and handed its Cancelled collection
        /// at once (as the dispatcher's shutdown or a reclassification does), and the hook burns the rest of the budget:
        /// nothing is scanned on Main, the preparation is not left waiting for a collection that will not come, its
        /// inputs stay whole (the blocks live, the holds held), the next Step offers it again, and it is published.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_AnOfferTheDispatcherCancels_IsOfferedAgain_WithTheBudgetOut()
        {
            CutWorldRoot root = NewBudgetWorld(0.5);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            FusedGroup group = root.Fusion.Groups[0];
            List<PhysicsOwnerShape> shapes = ShapesOf(root, group);
            int blocksBefore = BuildingFusion.LiveSnapshotBlocks;
            int cancelled = 0;
            root.Fusion.cancelOfferForTest = () =>
            {
                cancelled++;
                var burn = System.Diagnostics.Stopwatch.StartNew();
                while (burn.Elapsed.TotalMilliseconds < 1.0) { }   // the budget (0.02 ms) is out right after the cancellation
                return cancelled == 1;
            };
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            ProvisionalCutAsk ask = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 0f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;   // the driver makes the preparation in its Update
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(1));
            yield return null;   // the Step: the snapshot, the offer, its cancellation, the budget out
            Assert.That(cancelled, Is.EqualTo(1), "the offer was cancelled once");
            Assert.That(root.Fusion.PreparationsInFlight, Is.EqualTo(1), "the preparation is still in flight");
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore + 2), "the two members' blocks are still live: the inputs came back whole");
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.GreaterThanOrEqualTo(1), "the member's shape is still held for the work");
            Assert.That(root.Fusion.OverrunFrames, Is.GreaterThanOrEqualTo(1), "the Step ran past its budget (the burn), recorded");
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation, offered again");
            WriteAggregateRecord(root, "after the re-offer");
            Assert.That(root.Fusion.PreparationsReoffered, Is.EqualTo(1), "offered again once");
            Assert.That(root.Fusion.PreparationsPublished, Is.EqualTo(1), "published");
            Assert.That(root.Fusion.lastPreparationThreadId, Is.Not.EqualTo(mainThread).And.Not.EqualTo(0), "the scan ran on a worker, not Main");
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore), "the blocks went to the classifications or back");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The ending with a preparation that never started returns its inputs.** A hit is asked and the world's
        /// ending begins in the same frame, before any Step could snapshot the group: the preparation holds nothing yet
        /// and is abandoned; the ending completes, the group's Preparing count is back to zero at the abandonment, no
        /// block or classification outlives the world, and every mesh left is named.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_TheEnding_WithAnUnstartedPreparation_ReturnsItsInputs()
        {
            Dictionary<int, string> meshesBefore = MeshesNow();
            int classificationsBefore = PhysicsCutClassification.Live, blocksBefore = BuildingFusion.LiveSnapshotBlocks;
            CutWorldRoot root = NewBudgetWorld(1.5);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            FusedGroup group = root.Fusion.Groups[0];
            List<PhysicsOwnerShape> shapes = ShapesOf(root, group);
            BuildingFusion fusion = root.Fusion;
            ProvisionalCutAsk ask = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 0f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;   // the driver makes the preparation in its Update; this resumes before the Step
            Assert.That(fusion.PreparationsMade, Is.EqualTo(1));
            Assert.That(group.Preparing, Is.EqualTo(1));
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore), "nothing snapshotted at the request");
            root.Shutdown();   // the ending begins before the Step could snapshot: the preparation is left unstarted (the world may even release at once, nothing being outstanding)
            yield return EndWorld(root);
            yield return null;
            Assert.That(fusion.PreparationsAbandoned, Is.EqualTo(1), "the unstarted preparation was abandoned");
            Assert.That(fusion.PreparationsInFlight, Is.Zero);
            Assert.That(group.Preparing, Is.Zero, "the group's count was given back");
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.Zero, "no work hold remains on a member's shape");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classificationsBefore));
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore));
            List<string> left = MeshesLeftSince(meshesBefore);
            TestContext.Out.WriteLine("meshes left after the ending: " + left.Count + (left.Count > 0 ? " [" + string.Join(", ", left) + "]" : ""));
            Assert.That(left, Is.Empty, "meshes left behind, by name");
        }

        /// <summary>
        /// **The ending with a preparation running on the worker waits for it, then returns its inputs.** The worker is
        /// gated so the scan cannot end; the ending begins and does not release (the dispatcher's stop is not confirmed
        /// while the scan runs: the blocks stay live, the holds held); the gate opens, the scan ends, the dispatcher
        /// collects it, and the ending completes with every input given back: no block, classification or hold, and
        /// every mesh left is named.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_TheEnding_WithARunningPreparation_WaitsForTheWorker_ThenReturnsItsInputs()
        {
            Dictionary<int, string> meshesBefore = MeshesNow();
            int classificationsBefore = PhysicsCutClassification.Live, blocksBefore = BuildingFusion.LiveSnapshotBlocks;
            CutWorldRoot root = NewBudgetWorld(1.5);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            FusedGroup group = root.Fusion.Groups[0];
            List<PhysicsOwnerShape> shapes = ShapesOf(root, group);
            BuildingFusion fusion = root.Fusion;
            var gate = new ManualResetEventSlim(false);
            fusion.workerGate = gate;
            ProvisionalCutAsk ask = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 0f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return Until(() => root.Dispatcher.SubmittedCount > 0, "the scan submitted to the worker (gated there)");
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore + 2), "the two members' blocks are live for the scan");
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.GreaterThanOrEqualTo(1), "the member's shape is held for the scan");
            Assert.That(root.Shutdown(), Is.False, "the ending began, and cannot complete while the scan runs");
            yield return null;
            yield return null;
            Assert.That(root.IsReleased, Is.False, "not released while the worker holds the inputs");
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore + 2), "the blocks are still live: nothing was freed under the worker");
            gate.Set();
            yield return EndWorld(root);
            yield return null;
            Assert.That(fusion.PreparationsAbandoned, Is.EqualTo(1), "the running preparation was abandoned at the end, after its collection");
            Assert.That(fusion.PreparationsInFlight, Is.Zero);
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.Zero, "no work hold remains on a member's shape");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classificationsBefore), "the scan's classifications were disposed with the work");
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore));
            List<string> left = MeshesLeftSince(meshesBefore);
            TestContext.Out.WriteLine("meshes left after the ending: " + left.Count + (left.Count > 0 ? " [" + string.Join(", ", left) + "]" : ""));
            Assert.That(left, Is.Empty, "meshes left behind, by name");
        }

        /// <summary>
        /// **Hits while a group is being prepared go by group, Slash and plane.** A column of three is being prepared
        /// (the worker gated) for a cut through its top member by Slash 1. Then, in one frame: the same Slash and plane
        /// on the middle member is attached to that preparation (one result for both); the same Slash with another plane
        /// on the bottom member is held; Slash 1 on another building is a preparation of its own; Slash 2 on the middle
        /// member is held. The gate opens: the two preparations publish; the held hit on the bottom member is prepared
        /// on the lower side once it is free and published; the Slash 2 hit's member was crossed by that cut and replaced
        /// by a Final, so it is dropped on record. Every hit has a disposition, and the counts add up.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_HitsWhileAGroupIsPrepared_GoByGroupSlashAndPlane()
        {
            CutWorldRoot root = NewBudgetWorld(1.5, -1f, 30.0);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            LogicalFragmentId other = AddBuilding(root, new Vector3(6f, 0f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), bottom = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            LogicalFragmentId topMember = UpperByRoot(root, top), middle = Other(topMember, top);
            var otherHalves = new LogicalFragmentId[2];
            yield return CutInTwo(root, other, new float4(0f, 1f, 0f, 0f), otherHalves);
            yield return UntilFused(root, 5, "three fused in a column, two in the other building");
            FusedGroup column = null, otherGroup = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Contains(topMember)) column = g; if (g.Contains(otherHalves[0])) otherGroup = g; }
            Assert.That(column != null && otherGroup != null && !ReferenceEquals(column, otherGroup), Is.True, "two groups, one a building each");
            Assert.That(column.MemberCount, Is.EqualTo(3));

            var gate = new ManualResetEventSlim(false);
            root.Fusion.workerGate = gate;
            float4 planeA = new float4(0f, 1f, 0f, -0.75f);   // through the top member only
            Assert.That(root.TryAsk(SlashAsk(root, topMember, planeA, 1, 0f)), Is.True);
            yield return null;
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(1));
            Assert.That(column.Preparing, Is.EqualTo(1));
            Assert.That(root.TryAsk(SlashAsk(root, middle, planeA, 1, 0f)), Is.True, "the same Slash and plane, another member of the group");
            Assert.That(root.TryAsk(SlashAsk(root, bottom, new float4(0f, 0f, 1f, 0f), 1, 0f)), Is.True, "the same Slash, another plane");
            Assert.That(root.TryAsk(SlashAsk(root, otherHalves[0], new float4(1f, 0f, 0f, -6f), 1, 0f)), Is.True, "the same Slash on another building");
            Assert.That(root.TryAsk(SlashAsk(root, middle, new float4(1f, 0f, 0f, 0f), 2, 0f)), Is.True, "another Slash");
            yield return null;
            WriteAggregateRecord(root, "the five hits routed while the column is being prepared");
            WriteHits(root);
            IReadOnlyList<BuildingFusion.HitRecord> hits = root.Fusion.Hits;
            Assert.That(hits.Count, Is.EqualTo(5), "five hits, one record each");
            Assert.That(hits[0].state == "prepared" && hits[0].preparation == 1, Is.True, hits[0].Events);
            Assert.That(hits[1].state == "attached" && hits[1].preparation == 1, Is.True, "the same request on the group being prepared is attached to that preparation: " + hits[1].Events);
            Assert.That(hits[2].state == "held" && hits[2].Events.Contains("preparing") && hits[2].preparation == 0, Is.True, "the same Slash, another plane: held: " + hits[2].Events);
            Assert.That(hits[3].state == "prepared" && hits[3].preparation == 2, Is.True, "the same request on another group: its own preparation: " + hits[3].Events);
            Assert.That(hits[4].state == "held" && hits[4].Events.Contains("preparing"), Is.True, "another Slash on the group being prepared: held: " + hits[4].Events);
            foreach (BuildingFusion.HitRecord h in hits) Assert.That(h.IsPending, Is.True, "no outcome yet (an attachment is not one): " + h.Events);
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(2));
            Assert.That(root.Fusion.HitsAttached, Is.EqualTo(1));
            Assert.That(root.Fusion.HitsHeld, Is.EqualTo(2));
            Assert.That(root.Fusion.GroupCutsRefused, Is.Zero);

            gate.Set();
            yield return Until(() => root.Fusion.PreparationsPublished == 2, "both preparations published");
            yield return Until(() => root.Fusion.HeldProcessed == 2 && root.Fusion.PreparationsInFlight == 0, "the held hits routed once their groups were free, and the third preparation done");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
            WriteAggregateRecord(root, "after the gate opened");
            WriteHits(root);
            int routed = 0, dropped = 0;
            foreach (BuildingFusion.HeldOutcome h in root.Fusion.HeldOutcomes)
            {
                if (h.routed && h.outcome.Contains("prepared") && h.slashId == 1) routed++;
                if (!h.routed && h.outcome.StartsWith("Dropped") && h.slashId == 2) dropped++;
            }

            Assert.That(routed, Is.EqualTo(1), "the held Slash 1 hit was routed into a preparation on the bottom member's side (its publication is its own record, below)");
            Assert.That(dropped, Is.EqualTo(1), "the held Slash 2 hit's member was replaced by a Final: dropped, on record, not re-cut on a child");
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(3));
            Assert.That(root.Fusion.PreparationsPublished, Is.EqualTo(3));
            Assert.That(hits.Count, Is.EqualTo(5), "still five hits: the routing added events, not hits");
            Assert.That(hits[0].outcome == "Published" && hits[0].preparation == 1, Is.True, hits[0].Events);
            Assert.That(hits[1].outcome == "Published" && hits[1].preparation == 1, Is.True, "the attached hit took its preparation's outcome: " + hits[1].Events);
            Assert.That(hits[2].outcome == "Published" && hits[2].preparation == 3, Is.True, "the held hit was prepared on its member's side (preparation 3) and published: " + hits[2].Events);
            Assert.That(hits[3].outcome == "Published" && hits[3].preparation == 2, Is.True, hits[3].Events);
            Assert.That(hits[4].outcome != null && hits[4].outcome.StartsWith("Dropped") && !hits[4].published, Is.True, "the other Slash's member was replaced by a Final: " + hits[4].Events);
            Assert.That(root.Fusion.HitsPublished == 4 && root.Fusion.HitsRefused == 1 && root.Fusion.HitsPending == 0, Is.True, "every hit has its one outcome");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return EndWorld(root);
        }

        private static void WriteHits(CutWorldRoot root)
        {
            foreach (BuildingFusion.HitRecord h in root.Fusion.Hits) TestContext.Out.WriteLine("  hit " + h.id + " slash " + h.slashId + " plane " + h.planeId + " member " + h.fragment + (h.preparation > 0 ? " preparation " + h.preparation : "") + " => " + (h.outcome ?? "PENDING") + " [" + h.Events + "]");
        }

        /// <summary>
        /// **A snapshot split over frames of a moving group takes one adopted plane.** Four fused pieces rest as one
        /// kinematic group; a vertical cut is asked with the snapshot limited to one member a Step, and the group is
        /// moved 0.6 m a frame while it is taken. Every member's plane, brought back into the group's frame through the
        /// member's own placement, is the one plane adopted at the request; the group moved more than a metre between
        /// the first member and the last; the cut then publishes with four crossed members.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_ASplitSnapshotOfAMovingGroup_TakesOneAdoptedPlane()
        {
            CutWorldRoot root = NewBudgetWorld(1.5, -1f, 30.0);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            var bottom = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 1f, 0f, 0.5f), bottom);
            yield return UntilFused(root, 4, "four fused in a column");
            FusedGroup group = root.Fusion.Groups[0];
            Assert.That(group.MemberCount, Is.EqualTo(4));
            root.Fusion.snapshotMembersPerStepForTest = 1;
            Vector3 startedAt = group.Root.transform.position;
            ProvisionalCutAsk ask = SlashAsk(root, top[0], new float4(1f, 0f, 0f, 0f), 1, 12f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;   // the driver makes the preparation
            var planes = new List<float4>();
            float4 planeGroup = default;
            var seen = new List<string>();
            float deadline = Time.realtimeSinceStartup + 5f;
            Vector3 movedTo = startedAt;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;   // one Step: one member snapshotted
                if (group.Root == null) break;
                bool read = root.Fusion.TryReadSnapshotPlanes(planes, out planeGroup);
                seen.Add((read ? planes.Count : -1) + "@" + group.Root.transform.position.x.ToString("F1"));
                if (read && planes.Count >= 4) break;
                group.Root.transform.position += new Vector3(0.6f, 0f, 0f);   // the group moves between the members' snapshots
                movedTo = group.Root.transform.position;
            }

            TestContext.Out.WriteLine("snapshot progress (members@group x): " + string.Join(" ", seen) + "; plane in the group's frame " + planeGroup);
            Assert.That(planes.Count, Is.EqualTo(4), "all four members snapshotted, one a Step");
            Assert.That(movedTo.x - startedAt.x, Is.GreaterThan(1.2f), "the group moved more than a metre while the snapshot was taken");
            float4 adopted = planeGroup / math.length(planeGroup.xyz);
            for (int i = 0; i < planes.Count; i++)
            {
                Assert.That(math.dot(planes[i].xyz, adopted.xyz), Is.GreaterThan(1f - 1e-4f), "member " + i + ": the same normal in the group's frame: " + planes[i] + " vs " + adopted);
                Assert.That(math.abs(planes[i].w - adopted.w), Is.LessThan(1e-3f), "member " + i + ": the same offset in the group's frame: " + planes[i] + " vs " + adopted);
            }

            root.Fusion.snapshotMembersPerStepForTest = 0;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation");
            Assert.That(root.Fusion.PreparationsPublished, Is.EqualTo(1));
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(4), "the one plane crossed all four members of the moved group");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Two adopted planes close together are two requests: neither attached nor included.** With the worker gated,
        /// a cut through the top member is being prepared (Slash 1, plane A). Slash 1 with plane B, 5 mm from A, on
        /// the bottom member is held, not attached; Slash 1 with plane A itself on the middle member is attached. The
        /// gate opens and the Finals are held so the sides stay busy: Slash 1 with plane C (5 mm from A) on the bottom
        /// member is held, not included; Slash 1 with plane A on it is included. Then everything is let go and every
        /// hit ends with its one outcome, the near planes never as an inclusion.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_TwoAdoptedPlanesCloseTogether_AreTwoRequests()
        {
            CutWorldRoot root = NewBudgetWorld(1.5, -1f, 30.0);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), bottom = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            LogicalFragmentId topMember = UpperByRoot(root, top), middle = Other(topMember, top);
            yield return UntilFused(root, 3, "three fused in a column");
            var gate = new ManualResetEventSlim(false);
            root.Fusion.workerGate = gate;
            float4 planeA = new float4(0f, 1f, 0f, -0.75f), planeB = new float4(0f, 1f, 0f, -0.755f), planeC = new float4(0f, 1f, 0f, -0.745f);
            Assert.That(root.TryAsk(SlashAsk(root, topMember, planeA, 1, 0f)), Is.True);
            yield return null;
            Assert.That(root.Fusion.PreparationsMade, Is.EqualTo(1));
            Assert.That(root.TryAsk(SlashAsk(root, bottom, planeB, 1, 0f)), Is.True, "5 mm from plane A");
            Assert.That(root.TryAsk(SlashAsk(root, middle, planeA, 1, 0f)), Is.True, "plane A itself");
            yield return null;
            WriteHits(root);
            IReadOnlyList<BuildingFusion.HitRecord> hits = root.Fusion.Hits;
            Assert.That(hits.Count, Is.EqualTo(3));
            Assert.That(hits[1].state == "held" && hits[1].preparation == 0, Is.True, "the near plane is another request: held, not attached: " + hits[1].Events);
            Assert.That(hits[2].state == "attached" && hits[2].preparation == 1, Is.True, "the same plane is the same request: attached: " + hits[2].Events);
            Assert.That(root.Fusion.HitsAttached, Is.EqualTo(1));

            root.Fusion.holdFinals = true;   // the sides stay busy after the publication
            gate.Set();
            yield return Until(() => root.Fusion.PreparationsPublished == 1, "the preparation published");
            Assert.That(root.Fusion.IsCutting(bottom), Is.True, "the bottom member's side is busy with the cut");
            Assert.That(root.TryAsk(SlashAsk(root, bottom, planeC, 1, 0f)), Is.True, "5 mm from plane A, on the busy side");
            Assert.That(root.TryAsk(SlashAsk(root, bottom, planeA, 1, 0f)), Is.True, "plane A itself, on the busy side");
            yield return null;
            WriteHits(root);
            Assert.That(hits.Count, Is.EqualTo(5));
            Assert.That(hits[3].state == "held" && hits[3].IsPending && hits[3].Events.Contains("busy"), Is.True, "the near plane on the busy side is held, not included: " + hits[3].Events);
            Assert.That(hits[4].state == "included" && hits[4].published && hits[4].outcome.StartsWith("Included"), Is.True, "the same request on the busy side is included: " + hits[4].Events);
            Assert.That(root.Fusion.HitsIncluded, Is.EqualTo(1));

            root.Fusion.holdFinals = false;
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
            yield return Until(() => root.Fusion.HitsPending == 0 && root.Fusion.PreparationsInFlight == 0, "every hit's outcome");
            yield return Until(() => root.Fusion.CutsInProgress == 0, "the later Finals");
            WriteHits(root);
            Assert.That(root.Fusion.HitsIncluded, Is.EqualTo(1), "no near plane was ever included");
            Assert.That(root.Fusion.HitsAttached, Is.EqualTo(1), "no near plane was ever attached");
            foreach (BuildingFusion.HitRecord h in hits) Assert.That(h.outcome, Is.Not.Null, "hit " + h.id + " has its one outcome");
            Assert.That(hits[1].outcome.StartsWith("Included") || hits[3].outcome.StartsWith("Included"), Is.False, "the near planes' outcomes are their own (a preparation of their own, or dropped): " + hits[1].outcome + " / " + hits[3].outcome);
            Assert.That(root.Fusion.HitsPublished + root.Fusion.HitsRefused, Is.EqualTo(5));
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The ending with a preparation snapshotted and waiting in the dispatcher takes the dispatcher's cancellation.**
        /// The background pool refuses new work, so the offered scan stays in the dispatcher's queue, never submitted
        /// (its blocks live, its holds held); the world's ending begins; the dispatcher's shutdown hands the waiting work
        /// back as Cancelled before the fusion is disposed, and the fusion -- finding it collected -- returns its inputs
        /// at once. This is the path that once leaked: the cancellation was ignored and the fusion waited for a
        /// collection that never came.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionBudget_TheEnding_WithAPreparationWaitingInTheDispatcher_TakesItsCancellation()
        {
            Dictionary<int, string> meshesBefore = MeshesNow();
            int classificationsBefore = PhysicsCutClassification.Live, blocksBefore = BuildingFusion.LiveSnapshotBlocks;
            HoldingExecutor pool = null;
            CutWorldRoot root = NewBudgetWorld(1.5, -1f, 2.0, destination => destination == WorkDestination.BackgroundPool ? pool = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(4))) : null);
            Assert.That(pool, Is.Not.Null, "the background pool is the test's");
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves);
            yield return UntilFused(root, 2, "the halves fused");
            FusedGroup group = root.Fusion.Groups[0];
            List<PhysicsOwnerShape> shapes = ShapesOf(root, group);
            BuildingFusion fusion = root.Fusion;
            pool.RefuseNew = true;   // from here the pool has no room: an offer waits in the dispatcher
            ProvisionalCutAsk ask = SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 0f);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;   // the driver makes the preparation
            yield return null;   // the Step snapshots and offers it; the dispatcher finds no room
            Assert.That(fusion.AnyPreparationOffered, Is.True, "offered to the dispatcher");
            Assert.That(root.Dispatcher.WaitingCount, Is.EqualTo(1), "waiting in the dispatcher's queue, never submitted");
            Assert.That(root.Dispatcher.SubmittedCount, Is.Zero);
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore + 2), "the two members' blocks are live for the scan to come");
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.GreaterThanOrEqualTo(1), "the member's shape is held for the scan to come");
            root.Shutdown();   // nothing is submitted, so the dispatcher's shutdown cancels the waiting work and the ending goes on
            yield return EndWorld(root);
            yield return null;
            Assert.That(fusion.PreparationsCancelledAtEnd, Is.EqualTo(1), "the fusion found the work handed back as Cancelled by the dispatcher's shutdown");
            Assert.That(fusion.PreparationsAbandoned, Is.EqualTo(1));
            Assert.That(fusion.PreparationsInFlight, Is.Zero);
            Assert.That(fusion.Hits[0].outcome, Does.StartWith("Abandoned"), "the hit's outcome says so: " + fusion.Hits[0].Events);
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.Zero, "no work hold remains on a member's shape");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classificationsBefore));
            Assert.That(BuildingFusion.LiveSnapshotBlocks, Is.EqualTo(blocksBefore), "the blocks went back with the cancelled work");
            List<string> left = MeshesLeftSince(meshesBefore);
            TestContext.Out.WriteLine("meshes left after the ending: " + left.Count + (left.Count > 0 ? " [" + string.Join(", ", left) + "]" : ""));
            Assert.That(left, Is.Empty, "meshes left behind, by name");
        }
    }
}
