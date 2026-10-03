using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A deferred placed cuttable's first cut throwing once something of it is the world's (TL, 2026-10-03):
    /// <list type="bullet">
    /// <item>a prop, after its owner is registered and before its cut is admitted: nothing of the attempt stays -- the
    /// fragment retired, the owner (its actor) retired, the base geometry forgotten, the shown geometry taken back once by
    /// the world -- and the instance stands as placed, refused for good;</item>
    /// <item>a prop whose cut was admitted, then threw (before and after the publication's switch): the driver's record
    /// keeps the cut and the world's ending closes it; the candidate gives nothing back;</item>
    /// <item>a building while it is registered into the hull trial (inside the trial's registration, and after it): the
    /// owner, fragment, base geometry and group go; the geometry is given back once, by the caller before the DAG took
    /// it and by the world's reclamation after;</item>
    /// <item>a refused candidate left in its detector: inert, its hit shape (a prop's prepared physics too) held until its
    /// registrar goes, which takes it out of the detector and gives them back.</item>
    /// </list>
    /// Ignored where the licensed inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        // ---- a prop: an exception once its owner is registered, its cut not admitted ----

        [UnityTest]
        public IEnumerator PlacedDeferred_APropWhoseFirstCutThrowsAfterItsOwner_IsUndoneOnce_TheInstanceAsItWas()
        {
            yield return PropThrowsBeforeItsAdmission("after owner");
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_APropWhoseFirstCutThrowsAfterItsBaseGeometry_IsUndoneOnce_TheInstanceAsItWas()
        {
            yield return PropThrowsBeforeItsAdmission("after base geometry");
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_APropWhoseFirstCutThrowsJustBeforeItsRequest_IsUndoneOnce_TheInstanceAsItWas()
        {
            yield return PropThrowsBeforeItsAdmission("before request");
        }

        private IEnumerator PropThrowsBeforeItsAdmission(string stage)
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 throws " + stage, at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                var room = new StorageRoom(root);
                int shown = root.Display.ShownCount, bodies = Bodies(), owners = root.Owners.Count;
                int tracked = root.Geometry.TrackedFragmentCount, released = root.Geometry.VertexGroupsReleased;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                PlacedCuttableCandidate.cutPropHookForTest = s => { if (s == stage) throw new InvalidOperationException("thrown " + stage + " for the test"); };
                InvalidOperationException thrown;
                try
                {
                    thrown = Assert.Throws<InvalidOperationException>(() => Evaluate(detector, ThroughTheMiddle(1, data, at), 1));
                }
                finally
                {
                    PlacedCuttableCandidate.cutPropHookForTest = null;
                }

                LogicalFragmentId fragment = candidate.Source;
                TestContext.Out.WriteLine(stage + ": " + thrown.Message + "; " + candidate.LastRefusal);
                Assert.That(thrown.Message, Does.Contain("for the test"), "the exception passed on");
                Assert.That(fragment.IsSet, Is.True, "the hit identified a fragment");

                // At once: nothing of the attempt stays, the instance as placed.
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused), "refused for good");
                Assert.That(candidate.LastRefusal, Does.Contain("before its admission"));
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance was not touched");
                Assert.That(candidate.IsWithdrawn, Is.False, "not withdrawn");
                Assert.That(root.Ledger.IsCurrentTarget(fragment), Is.False, "its fragment retired");
                Assert.That(root.Ledger.TryGetActiveOperation(fragment, out _), Is.False, "no cut of it");
                Assert.That(root.Owners.TryGet(fragment, out _), Is.False, "no owner of it");
                Assert.That(root.Owners.Count, Is.EqualTo(owners), "its owner retired");
                Assert.That(root.Geometry.TryGetGeometry(fragment, out _), Is.False, "no base geometry of it");
                Assert.That(root.Geometry.TrackedFragmentCount, Is.EqualTo(tracked), "nothing of it kept by the DAG");
                yield return null;
                Assert.That(Bodies(), Is.EqualTo(bodies), "its actor gone");
                Assert.That(GameObject.Find("Prop " + data.name), Is.Null, "no actor of it");

                // The shown geometry is the world's: taken back once, by its reclamation, and no longer drawn.
                yield return UntilWithin(() => room.Same(root) && root.Display.ShownCount == shown, 5f, "the shown geometry let go and taken back by the world");
                room.AssertSame(root, "taken back by the world");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "once");
                for (int f = 0; f < 10; f++) yield return null;
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "and not again");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "not drawn");

                // A next Slash finds no target in it; the world ends as it should.
                Evaluate(detector, ThroughTheMiddle(2, data, at), 2);
                Assert.That(Hits(detector), Is.Empty, "not met again");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced));
                yield return EndWorld(root);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance as placed after the ending");
            }
            finally
            {
                PlacedCuttableCandidate.cutPropHookForTest = null;
                DisposeCandidates();
            }
        }

        // ---- a prop: its cut admitted, then an exception ----

        // Before the publication's switch (its pair taken back out by the publication): the driver's record is left
        // unestablished, the ledger's operation open until the world's ending entrance closes it. The display has the
        // prop; the instance's drawing is off, its colliders stand, nothing is published.
        [UnityTest]
        public IEnumerator PlacedDeferred_APropWhoseCutThrowsAfterItsAdmissionBeforeTheSwitch_IsLeftToTheDriver_TheEndingClosesIt()
        {
            yield return PropThrowsAfterItsAdmission(afterTheSwitch: false);
        }

        // After the switch (the pair in the scene, the source withdrawn): the record keeps the published pair; the instance
        // has left with the source.
        [UnityTest]
        public IEnumerator PlacedDeferred_APropWhoseCutThrowsAfterItsAdmissionAfterTheSwitch_IsLeftToTheDriver_TheInstanceWithdrawn()
        {
            yield return PropThrowsAfterItsAdmission(afterTheSwitch: true);
        }

        private IEnumerator PropThrowsAfterItsAdmission(bool afterTheSwitch)
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("bench_001 admitted, then throws " + (afterTheSwitch ? "after" : "before") + " the switch", at, out Renderer[] renderers, out Collider[] colliders);
                bool[] collidersAsPlaced = colliders.Select(c => c.enabled).ToArray();
                int shown = root.Display.ShownCount;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                root.Driver.RemainingMainSeconds = () => 1.0;   // the build and the publication run in the hit's own call
                if (afterTheSwitch) ProvisionalPhysicsPublication.publishedHook = () => throw new InvalidOperationException("thrown after the switch for the test");
                else ProvisionalPhysicsPublication.establishedHook = side => throw new InvalidOperationException("thrown before the switch for the test");
                InvalidOperationException thrown;
                try
                {
                    thrown = Assert.Throws<InvalidOperationException>(() => Evaluate(detector, ThroughTheMiddle(1, data, at), 1));
                }
                finally
                {
                    ProvisionalPhysicsPublication.publishedHook = null;
                    ProvisionalPhysicsPublication.establishedHook = null;
                }

                LogicalFragmentId fragment = candidate.Source;
                TestContext.Out.WriteLine((afterTheSwitch ? "after" : "before") + " the switch: " + thrown.Message + "; " + candidate.LastRefusal);
                Assert.That(thrown.Message, Does.Contain("for the test"), "the exception passed on");

                // The driver's: the candidate gave nothing back.
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Cut));
                Assert.That(candidate.LastRefusal, Does.Contain("after its admission"));
                Assert.That(root.Ledger.TryGetActiveOperation(fragment, out CutOperationId operation), Is.True, "its cut is the ledger's operation still");
                Assert.That(candidate.Operation, Is.EqualTo(operation), "the candidate names it");
                Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True, "its owner kept, held by the driver's record");
                Assert.That(root.Geometry.TryGetGeometry(fragment, out _), Is.True, "its base geometry kept");
                Assert.That(renderers.All(r => !r.enabled), Is.True, "the display draws it: the instance's drawing off");
                if (afterTheSwitch)
                {
                    Assert.That(owner.IsWithdrawn, Is.True, "the source left the scene at the switch");
                    Assert.That(candidate.IsWithdrawn, Is.True, "and the instance with it");
                    Assert.That(colliders.All(c => !c.enabled), Is.True, "its colliders off");
                }
                else
                {
                    Assert.That(owner.IsWithdrawn, Is.False, "the source stands: the pair was taken back out before the switch");
                    Assert.That(candidate.IsWithdrawn, Is.False, "not withdrawn");
                    Assert.That(colliders.Select(c => c.enabled).ToArray(), Is.EqualTo(collidersAsPlaced), "its colliders stand");
                    Assert.That(root.Display.ShownCount, Is.EqualTo(shown + 1), "drawn by the display");
                }

                // The frames after: nothing given back or published by the candidate; a next Slash cuts nothing of it.
                for (int f = 0; f < 30; f++) yield return null;
                Evaluate(detector, ThroughTheMiddle(2, data, at), 2);
                List<SlashHitConfirmed> after = Hits(detector);
                TestContext.Out.WriteLine("next Slash: " + string.Join("; ", after.Select(h => h.Fragment + " " + h.Acceptance + " " + h.Admission)));
                Assert.That(after.Any(h => h.Fragment == fragment && (h.Acceptance == ProvisionalCutAcceptance.Pending || h.Acceptance == ProvisionalCutAcceptance.Published)), Is.False,
                    "no second cut of its fragment");
                if (!afterTheSwitch)
                {
                    Assert.That(root.Ledger.TryGetActiveOperation(fragment, out _), Is.True, "left unestablished until the ending");
                    Assert.That(candidate.IsWithdrawn, Is.False, "never published");
                    Assert.That(colliders.Select(c => c.enabled).ToArray(), Is.EqualTo(collidersAsPlaced), "its colliders still stand");
                }

                yield return EndWorld(root);
            }
            finally
            {
                ProvisionalPhysicsPublication.publishedHook = null;
                ProvisionalPhysicsPublication.establishedHook = null;
                DisposeCandidates();
            }
        }

        // ---- a building: an exception while it is registered into the hull trial ----

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingThrowingInsideItsRegistration_LeavesNothing_TheGeometryGivenBackByTheCaller()
        {
            yield return BuildingThrowsWhileRegistered("inside", worldTakesTheGeometry: false);
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingThrowingAfterItsRegistration_LeavesNothing_TheGeometryGivenBackByTheCaller()
        {
            yield return BuildingThrowsWhileRegistered("deferred after register", worldTakesTheGeometry: false);
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingThrowingAfterItsOwner_LeavesNothing_TheGeometryGivenBackByTheCaller()
        {
            yield return BuildingThrowsWhileRegistered("deferred after owner", worldTakesTheGeometry: false);
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingThrowingAfterItsBaseGeometry_LeavesNothing_TheGeometryTakenBackByTheWorld()
        {
            yield return BuildingThrowsWhileRegistered("deferred after base geometry", worldTakesTheGeometry: true);
        }

        private IEnumerator BuildingThrowsWhileRegistered(string stage, bool worldTakesTheGeometry)
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 throws " + stage, at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                var room = new StorageRoom(root);
                int hullGroups = root.Hulls.GroupCount, unregistered = root.Hulls.GroupsUnregistered, shown = root.Display.ShownCount, bodies = Bodies();
                int owners = root.Owners.Count, tracked = root.Geometry.TrackedFragmentCount, released = root.Geometry.VertexGroupsReleased;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                if (stage == "inside") BuildingHullFusion.registerHookForTest = () => throw new InvalidOperationException("thrown inside the registration for the test");
                else CutWorldRoot.addBuildingHullHookForTest = s => { if (s == stage) throw new InvalidOperationException("thrown " + stage + " for the test"); };
                try
                {
                    Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                }
                finally
                {
                    BuildingHullFusion.registerHookForTest = null;
                    CutWorldRoot.addBuildingHullHookForTest = null;
                }

                List<SlashHitConfirmed> hits = Hits(detector);
                LogicalFragmentId fragment = candidate.Source;
                TestContext.Out.WriteLine(stage + ": " + string.Join("; ", hits.Select(h => h.Acceptance.ToString())) + "; " + candidate.LastRefusal);

                // The registration's exception is the candidate's refusal for good (its registration threw): nothing of the
                // world's stays, the instance as placed.
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.InvalidRequest));
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused), "refused for good");
                Assert.That(candidate.LastRefusal, Does.Contain("for the test"));
                Assert.That(candidate.Registration, Is.Null);
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "its group gone");
                Assert.That(root.Hulls.GroupsUnregistered, Is.EqualTo(unregistered + 1), "unregistered once");
                Assert.That(root.Ledger.IsCurrentTarget(fragment), Is.False, "its fragment retired");
                Assert.That(root.Owners.TryGet(fragment, out _), Is.False, "no owner of it");
                Assert.That(root.Owners.Count, Is.EqualTo(owners));
                Assert.That(root.Geometry.TryGetGeometry(fragment, out _), Is.False, "no base geometry of it");
                Assert.That(root.Geometry.TrackedFragmentCount, Is.EqualTo(tracked), "nothing of it kept by the DAG");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance was not touched");
                if (worldTakesTheGeometry)
                {
                    // The DAG had taken it: the caller did not give it back; the world's reclamation does, once.
                    Assert.That(candidate.LastRefusal, Does.Not.Contain("the stored geometry ("), "not given back by the caller");
                    yield return UntilWithin(() => room.Same(root), 5f, "the geometry taken back by the world");
                    room.AssertSame(root, "taken back by the world");
                    Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "once, by the world");
                }
                else
                {
                    // Still the caller's: given back by it at once.
                    Assert.That(candidate.LastRefusal, Does.Contain(GivenBackOnce), "given back by the caller");
                    room.AssertSame(root, "given back by the caller at once");
                }

                yield return null;
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");
                Assert.That(GameObject.Find("Building " + college.name), Is.Null, "no actor of it");
                for (int f = 0; f < 10; f++) yield return null;
                room.AssertSame(root, "and not given back again");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(worldTakesTheGeometry ? released + 1 : released), "nothing given back again");

                Evaluate(detector, ThroughTheMiddle(2, college, at), 2);
                Assert.That(Hits(detector), Is.Empty, "not met again");
                yield return EndWorld(root);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance as placed after the ending");
            }
            finally
            {
                BuildingHullFusion.registerHookForTest = null;
                CutWorldRoot.addBuildingHullHookForTest = null;
                DisposeCandidates();
            }
        }

        // ---- a building taken back out with a hit of it held ----

        // Its first cut Pending, a second Slash's hit on its group held; the first cut's publication refused at the display
        // (its source material not bound), so the cut ends without a publication and the group is taken back out at the
        // Step's end. The held hit gets its one outcome, Abandoned -- whether the take-back finds it still held or already
        // routed into a cut of its own that has no outcome yet -- and the first hit keeps its own. Nothing of it is
        // published or given back again in the updates after, nor at the world's ending.
        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingTakenBackWithAHeldHit_TheHeldHitAbandonedOnce_TheFirstOutcomeKept_NothingPublishedAfter()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            CutWorldRoot root = NewKinematicWorld();   // the source materials 7 and 2 bound only: the display refuses material 0 at the publication
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 taken back with a held hit", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                var room = new StorageRoom(root);
                int hullGroups = root.Hulls.GroupCount, shown = root.Display.ShownCount, bodies = Bodies(), owners = root.Owners.Count;
                int tracked = root.Geometry.TrackedFragmentCount, released = root.Geometry.VertexGroupsReleased;
                int takenBack = root.Hulls.UncutTakenBack, handedOver = root.Hulls.HandedOver;
                int hullHits = root.Hulls.Hits.Count, hullRefused = root.Hulls.HitsRefused, hullPublished = root.Hulls.HitsPublished, hullPending = root.Hulls.HitsPending;
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, 0);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);

                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> first = Hits(detector);
                Assert.That(first.Count, Is.EqualTo(1));
                Assert.That(first[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "its first cut accepted, its publication to come");
                HullGroup group = candidate.Registration.Group;
                LogicalFragmentId fragment = candidate.Source;

                // A second Slash in the same update meets the group while its first cut is under way: held.
                Evaluate(detector, ThroughTheMiddle(2, college, at + new Vector3(0f, 0.3f, 0f)), 1, 2);
                List<SlashHitConfirmed> second = Hits(detector);
                TestContext.Out.WriteLine("second Slash: " + string.Join("; ", second.Select(h => h.SlashId + " " + h.Acceptance)));
                Assert.That(second.Count(h => h.SlashId == 2 && h.Acceptance == ProvisionalCutAcceptance.Held), Is.EqualTo(1), "its hit on the group held");
                Assert.That(root.Hulls.HeldNow, Is.EqualTo(1), "one held");
                HullGroupHitPair(root, group, out BuildingHullFusion.HullHit firstHit, out BuildingHullFusion.HullHit heldHit);
                Assert.That(firstHit.IsPending && heldHit.IsPending, Is.True, "neither answered yet");

                // The first cut's publication refused: the group taken back out at a Step's end.
                yield return Until(() => group.State == HullGroupState.Gone, "the group taken back out");
                string firstOutcome = firstHit.outcome, heldOutcome = heldHit.outcome;
                TestContext.Out.WriteLine("first: " + firstOutcome + " | held (" + heldHit.state + "): " + heldOutcome + " | " + candidate.LastRefusal);
                Assert.That(root.Hulls.UncutTakenBack, Is.EqualTo(takenBack + 1), "taken back out once");
                Assert.That(firstOutcome, Is.Not.Null.And.Not.StartWith("Abandoned"), "the first hit keeps its own outcome");
                Assert.That(heldOutcome, Does.StartWith("Abandoned"), "the held hit abandoned");
                Assert.That(root.Hulls.Hits.Count, Is.EqualTo(hullHits + 2), "two hits recorded");
                Assert.That(root.Hulls.HitsRefused, Is.EqualTo(hullRefused + 2), "one outcome each");
                Assert.That(root.Hulls.HitsPublished, Is.EqualTo(hullPublished), "none published");
                Assert.That(root.Hulls.HitsPending, Is.EqualTo(hullPending), "none left without its outcome");
                Assert.That(root.Hulls.HeldNow, Is.Zero, "none held");
                Assert.That(root.Hulls.HandedOver, Is.EqualTo(handedOver), "never handed over");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused), "refused for good");
                Assert.That(candidate.Registration, Is.Null);
                Assert.That(candidate.LastRefusal, Does.Contain("taken back out"));
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its own drawing and colliders never left");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(root.Hulls.GroupCount, Is.EqualTo(hullGroups), "its group gone");
                Assert.That(root.Ledger.IsCurrentTarget(fragment), Is.False, "its fragment retired");
                Assert.That(root.Owners.Count, Is.EqualTo(owners), "its owner retired");
                Assert.That(root.Geometry.TryGetGeometry(fragment, out _), Is.False, "its base geometry forgotten");
                Assert.That(root.Geometry.TrackedFragmentCount, Is.EqualTo(tracked), "nothing of it kept by the DAG");
                yield return UntilWithin(() => room.Same(root), 5f, "its display input's room given back by the world's reclamation");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "once");

                // The updates after, and a next Slash: nothing published, answered again or given back again.
                for (int f = 0; f < 30; f++) yield return null;
                Evaluate(detector, ThroughTheMiddle(3, college, at), 3);
                Assert.That(Hits(detector), Is.Empty, "nothing of it met again");
                for (int f = 0; f < 30; f++) yield return null;
                Assert.That(firstHit.outcome, Is.EqualTo(firstOutcome), "the first outcome unchanged");
                Assert.That(heldHit.outcome, Is.EqualTo(heldOutcome), "the held hit's outcome unchanged");
                Assert.That(root.Hulls.HitsRefused, Is.EqualTo(hullRefused + 2), "no outcome given again");
                Assert.That(root.Hulls.HitsPublished, Is.EqualTo(hullPublished), "no late publication");
                Assert.That(root.Hulls.HandedOver, Is.EqualTo(handedOver), "never handed over");
                Assert.That(root.Hulls.UncutTakenBack, Is.EqualTo(takenBack + 1), "taken back once");
                Assert.That(root.Display.ShownCount, Is.EqualTo(shown), "never shown");
                Assert.That(root.Geometry.VertexGroupsReleased, Is.EqualTo(released + 1), "given back once");
                room.AssertSame(root, "and not given back again");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance as placed");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of it");
                Assert.That(GameObject.Find("Building " + college.name), Is.Null, "no actor of it");

                yield return EndWorld(root);
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "the instance as placed after the ending");
            }
            finally
            {
                DisposeCandidates();
            }
        }

        // The two hits recorded on a group: its first Slash's and its second's.
        private static void HullGroupHitPair(CutWorldRoot root, HullGroup group, out BuildingHullFusion.HullHit first, out BuildingHullFusion.HullHit second)
        {
            List<BuildingHullFusion.HullHit> of = root.Hulls.Hits.Where(h => h.group == group.Id).ToList();
            Assert.That(of.Count, Is.EqualTo(2), "two hits on the group");
            first = of.Single(h => h.slashId == 1);
            second = of.Single(h => h.slashId == 2);
        }

        // ---- a refused candidate left in its detector ----

        // Through its registrar: a building whose first cut threw is refused for good and stays in the detector, inert
        // (never met by a Slash), holding its hit shape; its registrar going takes it out of the detector and gives it back.
        [UnityTest]
        public IEnumerator PlacedDeferred_ARefusedBuilding_StaysInItsDetectorInert_ItsHitShapeHeldUntilItsRegistrarGoes()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            var input = new TextAsset(File.ReadAllText(CollegeInputPath)) { name = "college_001" };
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                PlayableCityCuttable c = PlaceRegistrar(root, input, "college_001 refused in its detector", at, 0f, Vector3.one);
                c.building = true;
                c.mass = 10000f;
                c.deferUntilCut = true;
                c.DetectorForTest = detector;
                yield return Until(() => c.Candidate != null || c.Failure != null, "the registrar made its cut target");
                Assert.That(c.Failure, Is.Null);
                PlacedCuttableCandidate candidate = c.Candidate;
                Assert.That(detector.HasPlaced(candidate), Is.True, "a cut target of the detector");
                Assert.That(candidate.HitShape, Is.Not.Null);

                root.Hulls.refuseHitForTest = g => throw new InvalidOperationException("the group's acceptance threw for the test");
                try
                {
                    Assert.Throws<InvalidOperationException>(() => Evaluate(detector, ThroughTheMiddle(1, college, at), 1));
                }
                finally
                {
                    root.Hulls.refuseHitForTest = null;
                }

                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused));
                Assert.That(detector.HasPlaced(candidate), Is.True, "left in the detector");
                Assert.That(candidate.IsHitTarget, Is.False, "inert");
                Assert.That(candidate.HitShape, Is.Not.Null, "its hit shape held");
                Assert.That(c.Registration, Is.Null, "no registration of it");
                int refusals = candidate.Refusals;
                for (int f = 0; f < 5; f++) yield return null;
                Evaluate(detector, ThroughTheMiddle(2, college, at), 2);
                Assert.That(Hits(detector), Is.Empty, "never met by a Slash");
                Assert.That(candidate.Refusals, Is.EqualTo(refusals), "not even asked");

                // Its registrar goes: out of the detector, its hit shape given back.
                UnityEngine.Object.Destroy(c.gameObject);
                yield return null;
                Assert.That(detector.HasPlaced(candidate), Is.False, "taken out of the detector");
                Assert.That(candidate.HitShape, Is.Null, "its hit shape given back");
                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused), "still refused");
                yield return EndWorld(root);
            }
            finally
            {
                if (root != null && root.Hulls != null) root.Hulls.refuseHitForTest = null;
                TrackPlacedRegistrars();
            }
        }

        // The same for a prop refused before its admission: its hit shape and prepared physics held, both given back when
        // its registrar goes.
        [UnityTest]
        public IEnumerator PlacedDeferred_ARefusedProp_StaysInItsDetectorInert_ItsShapesHeldUntilItsRegistrarGoes()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            var input = new TextAsset(File.ReadAllText(RefusalPropInputPath)) { name = "bench_001" };
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                PlayableCityCuttable c = PlaceRegistrar(root, input, "bench_001 refused in its detector", at, 0f, Vector3.one);
                c.deferUntilCut = true;
                c.DetectorForTest = detector;
                yield return Until(() => c.Candidate != null || c.Failure != null, "the registrar made its cut target");
                Assert.That(c.Failure, Is.Null);
                PlacedCuttableCandidate candidate = c.Candidate;
                Assert.That(candidate.HoldsPreparedPhysicsForTest, Is.True, "its prepared physics");

                PlacedCuttableCandidate.cutPropHookForTest = s => { if (s == "before request") throw new InvalidOperationException("thrown before the request for the test"); };
                try
                {
                    Assert.Throws<InvalidOperationException>(() => Evaluate(detector, ThroughTheMiddle(1, data, at), 1));
                }
                finally
                {
                    PlacedCuttableCandidate.cutPropHookForTest = null;
                }

                Assert.That(candidate.State, Is.EqualTo(PlacedCuttableCandidate.CandidateState.Refused));
                Assert.That(detector.HasPlaced(candidate), Is.True, "left in the detector");
                Assert.That(candidate.IsHitTarget, Is.False, "inert");
                Assert.That(candidate.HitShape, Is.Not.Null, "its hit shape held");
                Assert.That(candidate.HoldsPreparedPhysicsForTest, Is.True, "its prepared physics held (its shape was taken by the owner, now retired)");
                for (int f = 0; f < 5; f++) yield return null;
                Evaluate(detector, ThroughTheMiddle(2, data, at), 2);
                Assert.That(Hits(detector), Is.Empty, "never met by a Slash");

                UnityEngine.Object.Destroy(c.gameObject);
                yield return null;
                Assert.That(detector.HasPlaced(candidate), Is.False, "taken out of the detector");
                Assert.That(candidate.HitShape, Is.Null, "its hit shape given back");
                Assert.That(candidate.HoldsPreparedPhysicsForTest, Is.False, "its prepared physics given back");
                yield return EndWorld(root);
            }
            finally
            {
                PlacedCuttableCandidate.cutPropHookForTest = null;
                TrackPlacedRegistrars();
            }
        }
    }
}
