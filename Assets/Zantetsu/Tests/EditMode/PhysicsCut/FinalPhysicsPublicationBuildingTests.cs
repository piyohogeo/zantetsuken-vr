using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The building World D6 on a direct Final split (DESIGN 7.2.2, T-094): the Final children are the first 1→2
    /// publication of the lineage, so they are built with the planned depth and their constraints, and published with
    /// that value -- where the source stands at the publication.
    /// </summary>
    public unsafe partial class FinalPhysicsPublicationTests
    {
        [Test]
        public void ADirectFinalOfABuilding_PublishesThePlannedDepth_WithAWorldConstraintFromWhereItIsPublished()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, building: true))
            {
                CutOperationId operation = Admit(w);
                BuildingLineage planned = w.SourceOwner.Building.ChildOfSplit();
                Assert.That(planned, Is.EqualTo(BuildingLineage.BuildingAt(1)));
                PhysicsOwnerCandidate candidate = Build(
                    w, operation, out PhysicsCutProducts products, in w.harness.input, childLineage: planned);
                ConfigurableJoint positiveJoint = candidate.Positive.BuildingWorld;
                ConfigurableJoint negativeJoint = candidate.Negative.BuildingWorld;
                Assert.That(positiveJoint != null && negativeJoint != null, Is.True, "both free sides were built with one");
                Assert.That(candidate.ChildLineage, Is.EqualTo(planned));

                // The source moves and turns after the build: the constraint starts from where it is published.
                var moved = new PhysicsOwnerPlacement(new float3(3f, 1f, -2f), quaternion.EulerXYZ(0.1f, 0.7f, 0f));
                w.SourceOwner.Root.transform.SetPositionAndRotation(moved.position, moved.rotation);

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative,
                        out LogicalCutResultOutcome ledger),
                    Is.EqualTo(PhysicsPublicationOutcome.Published), "published: " + ledger);

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner a), Is.True);
                Assert.That(w.registry.TryGet(negative, out PhysicsFragmentOwner b), Is.True);
                Assert.That(a.Building, Is.EqualTo(planned), "the value planned before the build, published as it is");
                Assert.That(b.Building, Is.EqualTo(planned));
                Assert.That(a.BuildingWorldConstraint, Is.SameAs(positiveJoint));
                Assert.That(b.BuildingWorldConstraint, Is.SameAs(negativeJoint));

                Transform actor = a.Root.transform;
                Assert.That(Vector3.Distance(positiveJoint.connectedAnchor, actor.position), Is.LessThan(1e-4f),
                    "the reference is the published position");
                Assert.That(Vector3.Distance(actor.position, (Vector3)(float3)moved.position), Is.LessThan(1e-4f));
                Assert.That(Vector3.Angle(actor.rotation * positiveJoint.axis, Vector3.right), Is.LessThan(0.01f));
                Assert.That(Vector3.Angle(actor.rotation * positiveJoint.secondaryAxis, Vector3.up), Is.LessThan(0.01f));
                Assert.That(positiveJoint.yMotion, Is.EqualTo(ConfigurableJointMotion.Free));
                Assert.That(positiveJoint.linearLimit.limit, Is.EqualTo(BuildingWorldD6Settings.Provisional.LimitMetres(1)));
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(2));
                candidate.Dispose();
            }
        }

        [Test]
        public void ADirectFinalWithoutRoomForItsConstraints_IsNotBuilt()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, building: true))
            {
                CutOperationId operation = Admit(w);
                PhysicsCutRequest request = w.cook.Submit(in w.harness.input, float4x4.identity);
                w.RunUntil(() => request.IsOver, "the cut and cook end");
                PhysicsCutProducts products = request.Products;
                Assert.That(w.ledger.TryGetSettledAnchorDistribution(operation, out AnchorDistributionResult anchors), Is.True);
                PhysicsFragmentOwner owner = w.SourceOwner;
                int jointsBefore = UnityEngine.Object.FindObjectsByType<ConfigurableJoint>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

                var input = new PhysicsOwnerBuildInput
                {
                    products = products,
                    placement = owner.ReadPlacement(),
                    sourceMotion = owner.ReadMotion(float3.zero),
                    anchors = anchors,
                    parentMass = owner.Mass,
                    inheritedMeshes = owner.Shape.Meshes,
                    childLineage = owner.Building.ChildOfSplit(),
                    buildingWorld = BuildingWorldD6Settings.Provisional,
                    constraintRoom = 1,
                    name = "Unbuilt",
                };
                Assert.That(PhysicsOwnerBuilder.TryBuild(in input, out PhysicsOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome),
                    Is.False);
                Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.BuildFailed), "the existing failure, no reason of its own");
                Assert.That(candidate, Is.Null);
                foreach (Transform any in UnityEngine.Object.FindObjectsByType<Transform>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Assert.That(any.name == "Unbuilt +" || any.name == "Unbuilt -", Is.False,
                        "no actor was made, active or not");
                }
                Assert.That(UnityEngine.Object.FindObjectsByType<ConfigurableJoint>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length, Is.EqualTo(jointsBefore));

                // With room for both it is built.
                input.constraintRoom = 2;
                Assert.That(PhysicsOwnerBuilder.TryBuild(in input, out candidate, out outcome), Is.True, outcome.ToString());
                candidate.Dispose();
                products.Dispose();
            }
        }

        [Test]
        public void ADirectFinalThatCannotBeEstablished_PublishesNoDepth()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, building: true))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(
                    w, operation, out PhysicsCutProducts products, in w.harness.input,
                    childLineage: w.SourceOwner.Building.ChildOfSplit());
                UnityEngine.Object.DestroyImmediate(candidate.Negative.Root);

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished));
                Assert.That(w.registry.Count, Is.Zero, "no child with any depth; the source went the abort way");
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);
                candidate.Dispose();
                products.Dispose();
            }
        }
    }
}
