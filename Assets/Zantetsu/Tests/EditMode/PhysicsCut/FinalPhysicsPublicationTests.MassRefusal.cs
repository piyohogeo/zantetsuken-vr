using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The direct Final publication when a side's mass properties are refused where they meet its body
    /// (<see cref="MassPropertiesBoundary"/>, TL 2026-10-03): the pair cannot be established, which takes the ordinary
    /// abort of a physics failure; the refused body's mass, flags and motion are written back, nothing of the pair stays in
    /// the scene, and the candidate and the products are still the caller's to give back.
    /// </summary>
    public unsafe partial class FinalPhysicsPublicationTests
    {
        [Test]
        public void AMassPropertiesRefusal_IsPhysicsThatCannotBeEstablished_AndLeavesTheCandidateAndProducts()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                GameObject sourceRoot = w.SourceOwner.Root;

                // The negative side is established after the positive one: the refusal comes with one side in the scene.
                Rigidbody refused = candidate.Negative.Body;
                float massBefore = refused.mass;
                bool automaticCentre = refused.automaticCenterOfMass, automaticInertia = refused.automaticInertiaTensor, kinematic = refused.isKinematic;

                var producedMeshes = new List<Mesh>();
                for (int i = 0; i < products.PartCount(true); i++)
                {
                    PhysicsCutPart part = products.Part(true, i);
                    if (!part.borrowed)
                    {
                        producedMeshes.Add(part.mesh);
                    }
                }

                MassPropertiesBoundary.refuseAfterMassWriteForTest = b => ReferenceEquals(b, refused);
                PhysicsPublicationOutcome outcome;
                LogicalCutResultOutcome ledger;
                try
                {
                    outcome = Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out ledger);
                }
                finally
                {
                    MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
                }

                Assert.That(outcome, Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished));
                Assert.That(candidate.Negative.LastRefusal, Is.EqualTo(MassPropertiesRefusal.RefusedForTest));

                // The refused body as it was before the publication touched it.
                Assert.That(refused.mass, Is.EqualTo(massBefore), "its mass written back");
                Assert.That(refused.automaticCenterOfMass, Is.EqualTo(automaticCentre), "its automatic centre flag put back");
                Assert.That(refused.automaticInertiaTensor, Is.EqualTo(automaticInertia), "its automatic inertia flag put back");
                Assert.That(refused.isKinematic, Is.EqualTo(kinematic), "its kinematic flag put back");

                // The ordinary abort of a physics failure: the source is retired, nothing of the pair is in the scene.
                Assert.That(ledger, Is.EqualTo(LogicalCutResultOutcome.Applied), "the abort applied");
                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.False, "the source is retired");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False, "and its physics ended with it");
                Assert.That(sourceRoot == null, Is.True, "its body left the scene");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "the established side was taken back out");
                Assert.That(candidate.Negative.Root.activeInHierarchy, Is.False, "and the refused one with it");

                // The candidate and the products are still the caller's, and giving them back is the caller's.
                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.False, "a failed call leaves the products alone");
                }

                candidate.Dispose();
                products.Dispose();
                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.True, "the caller gives them back itself");
                }
            }
        }
    }
}
