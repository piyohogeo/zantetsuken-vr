using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The separation impulse's strength on the direct Final path (DESIGN 7.2): each free side is given what the
    /// strength returns for its own **Final** mass, in place of the caller's single value; a strength that cannot be
    /// used is this publication's physics failure, with neither side published. The strength is the provisional
    /// J = k × mass with this test's own k; the source is at rest, so a side's first velocity is its separation alone.
    /// </summary>
    public unsafe partial class FinalPhysicsPublicationTests
    {
        private const float k_perKg = 1f;

        [Test]
        public void ADirectFinal_GivesEachSideTheStrengthOfItsOwnFinalMass()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                double positiveMass = candidate.Positive.Mass;
                double negativeMass = candidate.Negative.Mass;
                // This fixture's compound splits into halves of equal Final mass; the Provisional case covers two sides
                // of different mass. What is shown here is that the Final masses are the ones asked about.
                Assert.That(positiveMass > 0.0 && negativeMass > 0.0, Is.True, "both children have a Final mass");

                var asked = new List<double>();
                Assert.That(
                    Publish(
                        w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative,
                        out LogicalCutResultOutcome _, float3.zero, 5f,
                        strength: mass =>
                        {
                            asked.Add(mass);
                            return (float)(k_perKg * mass);
                        }),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(asked, Is.EqualTo(new[] { positiveMass, negativeMass }), "asked once for each side's Final mass");
                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner positiveOwner), Is.True);
                Assert.That(w.registry.TryGet(negative, out PhysicsFragmentOwner negativeOwner), Is.True);
                Assert.That(positiveOwner.Body.mass, Is.EqualTo((float)positiveMass).Within(1e-3f), "the mass the body holds is that Final mass");
                Assert.That(
                    (float3)positiveOwner.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, k_perKg, 0f)).Using(Float3Within(1e-3f)),
                    "k × its Final mass moves the positive child off at k m/s -- not the caller's 5 N·s");
                Assert.That(
                    (float3)negativeOwner.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, -k_perKg, 0f)).Using(Float3Within(1e-3f)),
                    "and the negative child at k m/s the other way");
            }
        }

        [Test]
        public void ADirectFinal_WithAStrengthThatCannotBeUsed_PublishesNeitherSide()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                int incompleteBefore = w.ledger.Budget.IncompleteCutOperationCount;
                int asked = 0;

                Assert.That(
                    Publish(
                        w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative,
                        out LogicalCutResultOutcome _, float3.zero, 0f,
                        strength: mass => ++asked == 2 ? -1f : (float)mass),
                    Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished),
                    "a strength that cannot be used is a physics failure of the publication");

                Assert.That(positive.IsSet || negative.IsSet, Is.False, "no child was published");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "neither owner entered the scene");
                Assert.That(candidate.Negative.Root.activeInHierarchy, Is.False);
                Assert.That(w.ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
                Assert.That(record.state, Is.EqualTo(LogicalCutOperationState.Aborted), "the operation ended");
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incompleteBefore - 1),
                    "and gave its incomplete budget unit back");
            }
        }
    }
}
