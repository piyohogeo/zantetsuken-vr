using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The Final handoff when the final mass properties are refused where they meet the bodies
    /// (<see cref="MassPropertiesBoundary"/>, TL 2026-10-03). The check comes at the end of the handoff's preparation,
    /// before its switch: a refusal of either side leaves both actors exactly as they were (the first side's begun mass
    /// written back), withdraws the colliders prepared for them, and returns the ordinary "cannot be established"; the
    /// driver then ends the cut the way it ends any such handoff.
    /// </summary>
    public unsafe partial class ProvisionalCutDriverTests
    {
        private readonly struct Flags
        {
            private readonly bool _automaticCentre, _automaticInertia, _kinematic;

            internal Flags(Rigidbody b)
            {
                _automaticCentre = b.automaticCenterOfMass;
                _automaticInertia = b.automaticInertiaTensor;
                _kinematic = b.isKinematic;
            }

            internal void AssertSame(Rigidbody b, string what)
            {
                Assert.That(b.automaticCenterOfMass, Is.EqualTo(_automaticCentre), what + ": automatic centre flag");
                Assert.That(b.automaticInertiaTensor, Is.EqualTo(_automaticInertia), what + ": automatic inertia flag");
                Assert.That(b.isKinematic, Is.EqualTo(_kinematic), what + ": kinematic flag");
            }
        }

        [TestCase(true, TestName = "AHandoffWhoseMassPropertiesAreRefused_LeavesBothSidesAsTheyWere(first side)")]
        [TestCase(false, TestName = "AHandoffWhoseMassPropertiesAreRefused_LeavesBothSidesAsTheyWere(second side, after the first was begun)")]
        public void AHandoffWhoseMassPropertiesAreRefused_LeavesBothSidesAsTheyWere(bool refuseTheFirstSide)
        {
            using (World w = NewWorld())
            {
                ProvisionalCutTransaction transaction = Publish(w);
                PhysicsOwnerSide positive = transaction.Pair.Positive;
                PhysicsOwnerSide negative = transaction.Pair.Negative;

                // The cut is let finish, and its own handoff is made to throw in the preparation -- which changes nothing
                // and leaves the products with the record -- so that the real products can be handed off here, directly.
                FinalHandoffPublication.preparingHook = side => throw new InvalidOperationException("held for this test");
                try
                {
                    Assert.That(AdvanceUntilItThrows(w, 470), Is.True, "the cut finished and its handoff was held");
                }
                finally
                {
                    FinalHandoffPublication.preparingHook = null;
                }

                Assert.That(transaction.Products, Is.Not.Null, "the products are still the record's");
                var before = new[] { SideState.Of(positive), SideState.Of(negative) };
                var flags = new[] { new Flags(positive.Body), new Flags(negative.Body) };
                int fragmentsBefore = w.ledger.FragmentCount;
                PhysicsShapeSource productsSource = PhysicsShapeSource.For(transaction.Products);
                int usersBefore = productsSource.Users;
                int refusedBefore = MassPropertiesBoundary.RefusedCount;

                Rigidbody refused = refuseTheFirstSide ? positive.Body : negative.Body;
                int asked = 0;
                MassPropertiesBoundary.refuseAfterMassWriteForTest = b =>
                {
                    asked++;
                    return ReferenceEquals(b, refused);
                };

                var handoff = new FinalHandoffInput
                {
                    ledger = w.ledger,
                    registry = w.registry,
                    operation = transaction.Operation,
                    products = transaction.Products,
                    cutFrom = transaction.InputShape,
                    parentMass = transaction.ParentMass,
                };

                PhysicsPublicationOutcome outcome;
                try
                {
                    outcome = FinalHandoffPublication.TryHandOff(
                        in handoff, out LogicalFragmentId publishedPositive, out LogicalFragmentId publishedNegative, out LogicalCutResultOutcome _);
                    Assert.That(publishedPositive.IsSet || publishedNegative.IsSet, Is.False, "no child is named");
                }
                finally
                {
                    MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
                }

                Assert.That(outcome, Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished), "the final set cannot be established");
                Assert.That(asked, Is.EqualTo(refuseTheFirstSide ? 1 : 2), refuseTheFirstSide ? "refused at the first side" : "the first side was begun, the second refused");
                Assert.That(MassPropertiesBoundary.RefusedCount, Is.EqualTo(refusedBefore + 1));
                Assert.That((refuseTheFirstSide ? positive : negative).LastRefusal, Is.EqualTo(MassPropertiesRefusal.RefusedForTest));

                // Both actors as they were: the same colliders and nothing prepared left on them, the frame, the pose,
                // the mass, centre, inertia and axes (the first side's begun mass written back), the motion and the flags.
                before[0].AssertUnchanged(positive, "the positive side");
                before[1].AssertUnchanged(negative, "the negative side");
                flags[0].AssertSame(positive.Body, "the positive side");
                flags[1].AssertSame(negative.Body, "the negative side");

                // Nothing was published, the pair still stands, and the products are still the record's, held as before.
                Assert.That(w.ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "nothing was published");
                Assert.That(transaction.Pair.IsEnded, Is.False, "the pair is not ended");
                Assert.That(w.registry.TryGetProvisional(transaction.Operation, out ProvisionalOwnerPair standing), Is.True);
                Assert.That(ReferenceEquals(standing, transaction.Pair), Is.True);
                Assert.That(transaction.Products, Is.Not.Null);
                Assert.That(productsSource.Users, Is.EqualTo(usersBefore), "this call's hold on the products was let go");

                w.driver.EndEveryCut();
                Assert.That(transaction.Products, Is.Null, "and ending the record gives them back");
            }
        }

        /// <summary>
        /// Through the product entrance: a handoff refused for its mass properties is a final set that cannot be
        /// established, and the driver ends the cut that way (noted as an abort at the final stage), as for any other.
        /// </summary>
        [Test]
        public void AHandoffWhoseMassPropertiesAreRefused_EndsTheCutAsAFinalThatCannotBeEstablished()
        {
            using (World w = NewWorld())
            {
                ProvisionalCutTransaction transaction = Publish(w);
                Rigidbody refused = transaction.Pair.Negative.Body;
                int abortsBefore = w.driver.AbortCount;
                MassPropertiesBoundary.refuseAfterMassWriteForTest = b => ReferenceEquals(b, refused);
                try
                {
                    RunUntil(w, 470, () => w.driver.AbortCount > abortsBefore, "the handoff was refused and the cut ended");
                }
                finally
                {
                    MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
                }

                var abort = w.driver.KeptAbort(w.driver.KeptAbortCount - 1);
                Assert.That(abort.operation, Is.EqualTo(transaction.Operation));
                Assert.That(abort.stage, Is.EqualTo("final"));
                Assert.That(abort.publication, Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished.ToString()));
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "the pair was ended with the cut");
            }
        }

        /// <summary>
        /// **An exception in the middle of the switch, after the first side was committed** (the hook throws after the
        /// positive side's Establish). It is passed on as an internal error, as before; the second side's mass, begun in the
        /// preparation, is written back, so that side is exactly as it was. The first side stands on its final shape with
        /// its final mass properties, the pair is still the correspondence's, and ending the cut takes it all back -- the
        /// existing ending of a cut that cannot go on, not a restoration.
        /// </summary>
        [Test]
        public void AnExceptionAfterTheFirstSideIsCommitted_WritesTheSecondSidesBegunMassBack_AndEndingTheCutTakesThePair()
        {
            using (World w = NewWorld())
            {
                ProvisionalCutTransaction transaction = Publish(w);
                CutOperationId operation = transaction.Operation;
                PhysicsOwnerSide positive = transaction.Pair.Positive;
                PhysicsOwnerSide negative = transaction.Pair.Negative;
                SideState negativeBefore = SideState.Of(negative);
                var negativeFlags = new Flags(negative.Body);
                PhysicsOwnerShape provisionalShape = transaction.Pair.PositiveShape;

                FinalHandoffPublication.establishedHook = side =>
                {
                    if (side)
                    {
                        throw new InvalidOperationException("after the first side was committed");
                    }
                };

                try
                {
                    Assert.That(AdvanceUntilItThrows(w, 520), Is.True, "it threw in the switch, through the product entrance");
                }
                finally
                {
                    FinalHandoffPublication.establishedHook = null;
                }

                negativeBefore.AssertUnchanged(negative, "the second side (its begun mass written back)");
                negativeFlags.AssertSame(negative.Body, "the second side");
                Assert.That(ReferenceEquals(transaction.Pair.PositiveShape, provisionalShape), Is.False, "the first side stands on its final shape");
                Assert.That(positive.LastRefusal, Is.EqualTo(MassPropertiesRefusal.None), "with its final mass properties committed");
                Assert.That(positive.Body.mass, Is.EqualTo(positive.EffectiveMass), "and its body holds what was committed");
                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1), "the pair is still the correspondence's");

                Assert.That(w.driver.EndCut(operation), Is.True, "the explicit ending of a cut that cannot go on");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(transaction.Products, Is.Null, "and the products went back with it");
            }
        }

        /// <summary>
        /// **An exception in the second side's begin**, in the preparation (the boundary's test hook throws after that
        /// side's mass write): the preparation's own exception route, which now also writes the first side's begun mass
        /// back. Both sides are exactly as they were, the prepared colliders are withdrawn, and the error is passed on.
        /// </summary>
        [Test]
        public void AnExceptionInTheSecondSidesBegin_LeavesBothSidesAsTheyWere_AndIsPassedOn()
        {
            using (World w = NewWorld())
            {
                ProvisionalCutTransaction transaction = Publish(w);
                PhysicsOwnerSide positive = transaction.Pair.Positive;
                PhysicsOwnerSide negative = transaction.Pair.Negative;
                var before = new[] { SideState.Of(positive), SideState.Of(negative) };
                var flags = new[] { new Flags(positive.Body), new Flags(negative.Body) };
                Rigidbody throwing = negative.Body;
                int asked = 0;
                MassPropertiesBoundary.refuseAfterMassWriteForTest = b =>
                {
                    asked++;
                    if (ReferenceEquals(b, throwing))
                    {
                        throw new InvalidOperationException("in the second side's begin");
                    }

                    return false;
                };

                try
                {
                    Assert.That(AdvanceUntilItThrows(w, 540), Is.True, "it threw in the preparation, through the product entrance");
                }
                finally
                {
                    MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
                }

                Assert.That(asked, Is.EqualTo(2), "the first side was begun, the second threw");
                before[0].AssertUnchanged(positive, "the first side (its begun mass written back)");
                before[1].AssertUnchanged(negative, "the second side (its own write written back)");
                flags[0].AssertSame(positive.Body, "the first side");
                flags[1].AssertSame(negative.Body, "the second side");
                Assert.That(transaction.Pair.IsEnded, Is.False, "the pair still stands");
                Assert.That(transaction.Products, Is.Not.Null, "the products are still the record's");

                w.driver.EndEveryCut();
                Assert.That(transaction.Products, Is.Null);
            }
        }
    }
}
