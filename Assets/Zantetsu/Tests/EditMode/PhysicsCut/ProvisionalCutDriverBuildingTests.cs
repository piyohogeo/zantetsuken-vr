using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The building World D6 on the ordinary Provisional-to-Final path (DESIGN 7.2.2, T-094): the depth planned before
    /// the build and published as it is, the constraint on each anchor-less dynamic child, kept through the handoff,
    /// made again one depth deeper for a re-cut child, and gone with the actor that carried it.
    /// </summary>
    public unsafe partial class ProvisionalCutDriverTests
    {
        private static readonly BuildingWorldD6Settings k_building = BuildingWorldD6Settings.Adopted;

        /// <summary>
        /// The swing (joint Y, Z) limit the current engine keeps at least, whatever is asked (DESIGN 7.2.2; Unity
        /// 6000.3 / PhysX, confirmed 2026-09-28). The engine's, not the product's: the product asks for A(d) on every
        /// axis. If the engine changes it, DESIGN 7.2.2 asks for the request, the read-back and the motion again.
        /// </summary>
        private const float k_engineSwingMinimumDegrees = 3f;

        private const float k_near = 1e-4f;

        /// <summary>A placement away from the origin, so the reference of a world constraint is not trivially zero.</summary>
        private static void Displace(World w)
        {
            w.root.transform.SetPositionAndRotation(new Vector3(1.5f, 2f, -3f), Quaternion.Euler(10f, 35f, -5f));
        }

        private static void AssertBuildingWorld(ConfigurableJoint joint, GameObject actor, int depth, string what)
        {
            Assert.That(joint != null, Is.True, what + ": has a building World D6");
            Assert.That(joint.gameObject, Is.SameAs(actor), what + ": on its own actor");
            Assert.That(joint.connectedBody == null, Is.True, what + ": connected to the World");

            // Free in world Y, Limited on the two horizontal axes and every rotation.
            Assert.That(joint.xMotion, Is.EqualTo(ConfigurableJointMotion.Limited), what);
            Assert.That(joint.yMotion, Is.EqualTo(ConfigurableJointMotion.Free), what);
            Assert.That(joint.zMotion, Is.EqualTo(ConfigurableJointMotion.Limited), what);
            Assert.That(joint.angularXMotion, Is.EqualTo(ConfigurableJointMotion.Limited), what);
            Assert.That(joint.angularYMotion, Is.EqualTo(ConfigurableJointMotion.Limited), what);
            Assert.That(joint.angularZMotion, Is.EqualTo(ConfigurableJointMotion.Limited), what);

            // The depth's limits: one distance and one requested symmetric angle, A(d). As the joint reports them back,
            // twist (X) is the request and swing (Y, Z) is the request or the engine's minimum, whichever is larger.
            float requested = k_building.AngleDegrees(depth);
            float swing = Mathf.Max(k_engineSwingMinimumDegrees, requested);
            Assert.That(joint.linearLimit.limit, Is.EqualTo(k_building.LimitMetres(depth)), what + ": L(d)");
            Assert.That(joint.lowAngularXLimit.limit, Is.EqualTo(-requested), what + ": twist -A(d)");
            Assert.That(joint.highAngularXLimit.limit, Is.EqualTo(requested), what + ": twist +A(d)");
            Assert.That(joint.angularYLimit.limit, Is.EqualTo(swing), what + ": swing Y as the engine reports it");
            Assert.That(joint.angularZLimit.limit, Is.EqualTo(swing), what + ": swing Z as the engine reports it");

            // Nothing that restores, snaps or breaks it.
            Assert.That(joint.xDrive.positionSpring + joint.yDrive.positionSpring + joint.zDrive.positionSpring, Is.Zero, what);
            Assert.That(joint.angularXDrive.positionSpring + joint.angularYZDrive.positionSpring + joint.slerpDrive.positionSpring,
                Is.Zero, what);
            Assert.That(joint.linearLimitSpring.spring + joint.angularXLimitSpring.spring + joint.angularYZLimitSpring.spring,
                Is.Zero, what);
            Assert.That(joint.projectionMode, Is.EqualTo(JointProjectionMode.None), what);
            Assert.That(float.IsPositiveInfinity(joint.breakForce) && float.IsPositiveInfinity(joint.breakTorque), Is.True, what);
        }

        /// <summary>
        /// Where the constraint starts from: the actor's pose, with no relative translation or rotation. The connected
        /// anchor is the actor's world position and the joint's axes are the world's.
        /// </summary>
        private static void AssertReferenceIsTheActorsPose(ConfigurableJoint joint, string what)
        {
            Transform actor = joint.transform;
            Assert.That(Vector3.Distance(joint.connectedAnchor, actor.position), Is.LessThan(k_near), what + ": anchor");
            Assert.That(Vector3.Distance(actor.TransformPoint(joint.anchor), actor.position), Is.LessThan(k_near), what);
            Assert.That(Vector3.Angle(actor.rotation * joint.axis, Vector3.right), Is.LessThan(0.01f), what + ": joint X is world X");
            Assert.That(Vector3.Angle(actor.rotation * joint.secondaryAxis, Vector3.up), Is.LessThan(0.01f), what + ": joint Y is world Y");
        }

        /// <summary>
        /// A real joint made as the product makes it, at depths 4 and 5 of the adopted values: the product asks A(d) on
        /// every axis; read back right after it is made and after a physics step, twist keeps -A(d)..+A(d), and swing
        /// is A(d) where that is at least the engine's minimum (depth 4, 3.75 degrees) and the minimum below it (depth 5,
        /// 1.875 asked, 3 read back). How far a body then turns is not asserted here.
        /// </summary>
        [TestCase(4, 3.75f, 3.75f)]
        [TestCase(5, 1.875f, 3f)]
        public void AtDepthsFourAndFive_TwistIsTheRequest_AndSwingReadsBackTheEnginesMinimum(int depth, float requested, float swing)
        {
            Assert.That(k_building.AngleDegrees(depth), Is.EqualTo(requested), "A(d) of the adopted values, asked as it is");
            var actor = new GameObject("Building World D6 at depth " + depth);
            _objects.Add(actor);
            actor.SetActive(false);
            actor.transform.SetPositionAndRotation(new Vector3(1.5f, 2f, -3f), Quaternion.Euler(10f, 35f, -5f));
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            ConfigurableJoint joint = BuildingWorldD6.Create(new PhysicsOwnerSide(true, actor, actor, body), in k_building, depth);

            void Read(string when)
            {
                Assert.That(joint.lowAngularXLimit.limit, Is.EqualTo(-requested), when + ": twist lower");
                Assert.That(joint.highAngularXLimit.limit, Is.EqualTo(requested), when + ": twist upper");
                Assert.That(joint.angularYLimit.limit, Is.EqualTo(swing), when + ": swing Y");
                Assert.That(joint.angularZLimit.limit, Is.EqualTo(swing), when + ": swing Z");
                Assert.That(joint.linearLimit.limit, Is.EqualTo(k_building.LimitMetres(depth)), when + ": L(d)");
            }

            Read("made");
            AssertBuildingWorld(joint, actor, depth, "made");
            AssertReferenceIsTheActorsPose(joint, "made");

            SimulationMode mode = Physics.simulationMode;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                actor.SetActive(true);
                Physics.Simulate(1f / 45f);
            }
            finally
            {
                Physics.simulationMode = mode;
            }

            Read("after a step");
        }

        [Test]
        public void ABuildingCut_PlansTheChildDepthBeforeTheBuild_AndGivesEachFreeSideAWorldConstraint()
        {
            using (World w = NewWorld(building: true))
            {
                Displace(w);
                Assert.That(w.SourceOwner.Building, Is.EqualTo(BuildingLineage.RegisteredBuilding), "registered true, 0");

                ProvisionalCutTransaction transaction = Publish(w);
                ProvisionalOwnerPair pair = transaction.Pair;
                Assert.That(pair.ChildLineage, Is.EqualTo(BuildingLineage.BuildingAt(1)), "planned before the build: 0 + 1");

                AssertBuildingWorld(pair.Positive.BuildingWorld, pair.Positive.Root, 1, "Provisional +");
                AssertBuildingWorld(pair.Negative.BuildingWorld, pair.Negative.Root, 1, "Provisional -");
                AssertReferenceIsTheActorsPose(pair.Positive.BuildingWorld, "Provisional +");
                AssertReferenceIsTheActorsPose(pair.Negative.BuildingWorld, "Provisional -");
                Assert.That(pair.Positive.BuildingWorld, Is.Not.SameAs(pair.Separation), "not the sibling constraint");
                Assert.That(pair.Positive.Root.GetComponents<ConfigurableJoint>().Length, Is.EqualTo(2),
                    "the sibling constraint's actor carries both, separately");
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(3), "one sibling constraint and two building ones");
                Assert.That(w.registry.HeldBySystemConstraint(pair.Positive.Body), Is.True);
                Assert.That(w.SourceOwner.Building, Is.EqualTo(BuildingLineage.RegisteredBuilding),
                    "the source's own depth is not touched by planning its children's");
            }
        }

        [Test]
        public void TheHandoff_PublishesThePlannedDepthOnce_AndKeepsEachConstraintAsItWas()
        {
            using (World w = NewWorld(building: true))
            {
                Displace(w);
                ProvisionalCutTransaction transaction = Publish(w);
                ConfigurableJoint positiveJoint = transaction.Pair.Positive.BuildingWorld;
                ConfigurableJoint negativeJoint = transaction.Pair.Negative.BuildingWorld;
                GameObject positiveActor = transaction.Pair.Positive.Root;
                Vector3 anchor = positiveJoint.connectedAnchor;
                Vector3 axis = positiveJoint.axis;
                Vector3 secondary = positiveJoint.secondaryAxis;
                float limit = positiveJoint.linearLimit.limit;
                float angle = positiveJoint.angularYLimit.limit;

                RunUntil(w, 700, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                Assert.That(w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation published), Is.True);
                Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner positive), Is.True);
                Assert.That(w.registry.TryGet(published.negative, out PhysicsFragmentOwner negative), Is.True);

                Assert.That(positive.Building, Is.EqualTo(BuildingLineage.BuildingAt(1)), "depth 1, not 2: nothing added again");
                Assert.That(negative.Building, Is.EqualTo(BuildingLineage.BuildingAt(1)));
                Assert.That(positive.Root, Is.SameAs(positiveActor), "the same actor");
                Assert.That(positive.BuildingWorldConstraint, Is.SameAs(positiveJoint), "the same constraint, not a new one");
                Assert.That(negative.BuildingWorldConstraint, Is.SameAs(negativeJoint));
                Assert.That(positiveJoint.connectedAnchor, Is.EqualTo(anchor), "its reference was not set again");
                Assert.That(positiveJoint.axis, Is.EqualTo(axis));
                Assert.That(positiveJoint.secondaryAxis, Is.EqualTo(secondary));
                Assert.That(positiveJoint.linearLimit.limit, Is.EqualTo(limit), "nor its limits");
                Assert.That(positiveJoint.angularYLimit.limit, Is.EqualTo(angle));
                Assert.That(positiveActor.GetComponents<ConfigurableJoint>().Length, Is.EqualTo(1), "the sibling one ended");
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(2), "the two building constraints remain");
                Assert.That(w.registry.HeldBySystemConstraint(positive.Body), Is.True, "known to the correspondence");
            }
        }

        [Test]
        public void ANonBuilding_StaysFalseAndZero_AndGetsNoBuildingConstraint()
        {
            using (World w = NewWorld())
            {
                Assert.That(w.SourceOwner.Building, Is.EqualTo(BuildingLineage.NotBuilding));
                ProvisionalCutTransaction transaction = Publish(w);
                Assert.That(transaction.Pair.ChildLineage, Is.EqualTo(BuildingLineage.NotBuilding));
                Assert.That(transaction.Pair.Positive.BuildingWorld == null && transaction.Pair.Negative.BuildingWorld == null, Is.True);
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(1), "the sibling constraint only");

                RunUntil(w, 710, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                Assert.That(w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation published), Is.True);
                Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner positive), Is.True);
                Assert.That(positive.Building, Is.EqualTo(BuildingLineage.NotBuilding), "false and 0 after a split");
                Assert.That(positive.BuildingWorldConstraint == null, Is.True);
                Assert.That(positive.Root.GetComponents<Joint>().Length, Is.Zero);
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);
                Assert.That(w.registry.HeldBySystemConstraint(positive.Body), Is.False);
            }
        }

        [Test]
        public void AnAnchoredSide_IsFixed_AndGetsNoBuildingConstraint_WhileTheFreeSideDoes()
        {
            // One anchor below the plane: the negative side is fixed by it.
            using (World w = NewWorld(building: true, anchors: new[] { new float3(0f, -0.5f, 0f) }))
            {
                ProvisionalCutTransaction transaction = Publish(w);
                Assert.That(transaction.Pair.Negative.FixedByAnchors, Is.True);
                Assert.That(transaction.Pair.Negative.BuildingWorld == null, Is.True, "a fixed side has none");
                AssertBuildingWorld(transaction.Pair.Positive.BuildingWorld, transaction.Pair.Positive.Root, 1, "free +");
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(2));

                RunUntil(w, 720, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                Assert.That(w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation published), Is.True);
                Assert.That(w.registry.TryGet(published.negative, out PhysicsFragmentOwner fixedChild), Is.True);
                Assert.That(fixedChild.Building, Is.EqualTo(BuildingLineage.BuildingAt(1)), "it is still a building child");
                Assert.That(fixedChild.BuildingWorldConstraint == null && fixedChild.Body.isKinematic, Is.True);
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ARecutChild_MakesNewConstraintsOneDeeper_AndItsOwnLeavesTheSceneAtOnce_ThenGoesWithIt()
        {
            using (World w = NewWorld(building: true))
            {
                ProvisionalCutTransaction first = Publish(w);
                RunUntil(w, 730, () => first.Phase == ProvisionalCutPhase.HandedOff, "the first handoff happens");
                Assert.That(w.ledger.TryGetOperation(first.Operation, out LogicalCutOperation published), Is.True);
                Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner parent), Is.True);
                ConfigurableJoint parentJoint = parent.BuildingWorldConstraint;
                GameObject parentActor = parent.Root;

                var again = new ProvisionalCutAsk
                {
                    source = published.positive,
                    plane = new float4(1f, 0f, 0f, 0f),
                    renderAnchor = float3.zero,
                };
                Assert.That(
                    w.driver.RequestCut(in again, out ProvisionalCutTransaction second, out LogicalCutAdmission _),
                    Is.EqualTo(ProvisionalCutAcceptance.Published));
                ProvisionalOwnerPair pair = second.Pair;
                Assert.That(pair.ChildLineage, Is.EqualTo(BuildingLineage.BuildingAt(2)), "one deeper than the parent");
                AssertBuildingWorld(pair.Positive.BuildingWorld, pair.Positive.Root, 2, "re-cut +");
                AssertBuildingWorld(pair.Negative.BuildingWorld, pair.Negative.Root, 2, "re-cut -");
                AssertReferenceIsTheActorsPose(pair.Positive.BuildingWorld, "re-cut +");
                Assert.That(pair.Positive.BuildingWorld, Is.Not.SameAs(parentJoint), "a new one, not the parent's moved over");
                Assert.That(parentJoint.gameObject, Is.SameAs(parentActor), "the parent's stays where it was");

                // The parent left the scene with its publication, so its constraint is not in the next step.
                Assert.That(parentActor.activeInHierarchy, Is.False, "the parent's actor is out of the scene now");
                Physics.Simulate(1f / 45f);
                Assert.That(parentActor.activeInHierarchy, Is.False, "and stays out across a step");
                Assert.That(pair.Positive.Root.activeInHierarchy && pair.Negative.Root.activeInHierarchy, Is.True);
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(5), "2 kept, 1 sibling, 2 new; the parent's until it retires");

                RunUntil(w, 760, () => second.Phase == ProvisionalCutPhase.HandedOff, "the second handoff happens");
                Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner _), Is.False, "the parent retired");
                Assert.That(parentJoint == null, Is.True, "and its constraint went with its actor");
                Assert.That(w.ledger.TryGetOperation(second.Operation, out LogicalCutOperation grand), Is.True);
                Assert.That(w.registry.TryGet(grand.positive, out PhysicsFragmentOwner grandChild), Is.True);
                Assert.That(grandChild.Building, Is.EqualTo(BuildingLineage.BuildingAt(2)));
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(3), "the sibling's child and the two grandchildren");
            }
        }

        [Test]
        public void ACutThatCannotBeEstablished_PublishesNoDepth_AndItsConstraintsGoWithThePair()
        {
            using (World w = NewWorld(building: true))
            {
                ProvisionalCutTransaction transaction = Publish(w);
                ConfigurableJoint positiveJoint = transaction.Pair.Positive.BuildingWorld;
                Assert.That(positiveJoint != null, Is.True);

                RunUntil(w, 770, () => transaction.Cut != null && transaction.Cut.Stage != PhysicsCutStage.Waiting,
                    "the cut is under way");
                Assert.That(w.cook.Abandon(transaction.Cut), Is.True, "the cut is given up");
                RunUntil(w, 780, () => transaction.Phase == ProvisionalCutPhase.Recovered, "it comes back with nothing");

                Assert.That(positiveJoint == null, Is.True, "the constraint ended with the pair");
                Assert.That(w.registry.Count, Is.Zero, "no child was published, with any depth; the source retired");
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);
            }
        }

        [Test]
        public void AStaleCut_PublishesNoDepth_AndTheSourceKeepsItsOwn()
        {
            using (World w = NewWorld(building: true))
            {
                ProvisionalCutTransaction transaction = Publish(w);
                w.ledger.NoteOwnershipChanged(w.source);
                RunUntil(w, 790, () => transaction.Phase == ProvisionalCutPhase.Recovered, "the result is given up");

                Assert.That(w.registry.Count, Is.EqualTo(1), "only the source");
                Assert.That(w.SourceOwner.Building, Is.EqualTo(BuildingLineage.RegisteredBuilding), "at depth 0 still");
                Assert.That(w.registry.SystemConstraintCount, Is.Zero, "the pair's constraints went with it");
            }
        }

        [Test]
        public void ConstraintsThatDoNotFit_CannotBeBuilt_AndTheCutGoesTheAbortWay()
        {
            using (World w = NewWorld(building: true))
            {
                // Two free building sides and the sibling constraint are three; two do not fit.
                w.driver.ConfigureConstraints(k_building, 2);
                ProvisionalCutAsk ask = Ask(w);
                Assert.That(
                    w.driver.RequestCut(in ask, out ProvisionalCutTransaction _, out LogicalCutAdmission _),
                    Is.EqualTo(ProvisionalCutAcceptance.Aborted), "a pair without the constraints it needs is not built");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);
                Assert.That(Object.FindObjectsByType<ConfigurableJoint>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                    Is.Empty, "no joint of a half-built pair is left");
                foreach (Transform any in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Assert.That(any.name == "Authored Source +" || any.name == "Authored Source -", Is.False,
                        "no actor of a half-built pair is left, active or not: " + any.name);
                }
                Assert.That(w.registry.Count, Is.Zero, "the ordinary abort retired the source");
            }

            using (World w = NewWorld(building: true))
            {
                w.driver.ConfigureConstraints(k_building, 3);
                Publish(w);
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(3), "exactly the room there was");
            }

            using (World w = NewWorld())
            {
                w.driver.ConfigureConstraints(k_building, 1);
                Publish(w);
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(1), "an ordinary cut needs its sibling constraint only");
            }
        }

        [Test]
        public void EndingEveryCut_TakesTheOutstandingPairsConstraintsWithIt()
        {
            using (World w = NewWorld(building: true))
            {
                ProvisionalCutTransaction transaction = Publish(w);
                ConfigurableJoint positiveJoint = transaction.Pair.Positive.BuildingWorld;
                w.driver.EndEveryCut();
                Assert.That(positiveJoint == null, Is.True);
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);
            }
        }

        [Test]
        public void EndingTheCorrespondence_TakesEveryPublishedChildsConstraintWithIt()
        {
            using (World w = NewWorld(building: true))
            {
                ProvisionalCutTransaction transaction = Publish(w);
                RunUntil(w, 800, () => transaction.Phase == ProvisionalCutPhase.HandedOff, "the handoff happens");
                Assert.That(w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation published), Is.True);
                Assert.That(w.registry.TryGet(published.positive, out PhysicsFragmentOwner positive), Is.True);
                ConfigurableJoint joint = positive.BuildingWorldConstraint;

                w.registry.Dispose();
                Assert.That(joint == null, Is.True, "gone with its actor");
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);
            }
        }
    }
}
