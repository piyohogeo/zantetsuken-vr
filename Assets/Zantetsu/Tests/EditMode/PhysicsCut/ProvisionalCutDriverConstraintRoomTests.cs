using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    // The system constraint room judged before acceptance (2026-09-29): an accepted cut not built yet holds what it may
    // need, so a cut accepted meanwhile cannot take it; a refused source stays and is cut once there is room; a cut ended
    // before its build gives its reservation back.
    public unsafe partial class ProvisionalCutDriverTests
    {
        [Test]
        public void ACutAcceptedBeforeItsBuild_HoldsItsRoom_AndAnotherAcceptedMeanwhileCannotTakeIt()
        {
            using (World w = NewWorld())
            {
                w.driver.ConfigureConstraints(k_building, 1);
                var first = Publish(w);
                RunUntil(w, 810, () => first.Phase == ProvisionalCutPhase.HandedOff, "first children published");
                Assert.That(w.ledger.TryGetOperation(first.Operation, out var children), Is.True);
                Assert.That(w.registry.SystemConstraintCount, Is.Zero, "a handed-off ordinary pair keeps no constraint");

                // No Main left: the first is accepted and waits before its build -- no candidate, only its reservation.
                w.driver.RemainingMainSeconds = () => 0;
                var ask = new ProvisionalCutAsk { source = children.positive, plane = new float4(1, 0, 0, 0) };
                Assert.That(w.driver.RequestCut(ask, out var waiting, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));
                Assert.That(waiting.Candidate, Is.Null, "not built yet");

                // The second may not take the room the first was accepted with.
                ask.source = children.negative;
                Assert.That(w.driver.RequestCut(ask, out var refused, out _), Is.EqualTo(ProvisionalCutAcceptance.NotAccepted));
                Assert.That(refused, Is.Null);
                Assert.That(w.driver.ConstraintRoomRefusals, Is.EqualTo(1));
                Assert.That(w.ledger.IsCurrentTarget(children.negative), Is.True, "the refused source stays");

                // The first is built and published in its own room, as accepted.
                w.driver.RemainingMainSeconds = () => 1;
                RunUntil(w, 810, () => waiting.Pair != null, "the waiting cut built and published");
                Assert.That(w.driver.AbortCount, Is.Zero, "nothing was aborted for room");
            }
        }

        [Test]
        public void ACutAbortedBeforeItsBuild_GivesItsReservationBack_AndTheRefusedSourceIsCutThen()
        {
            using (World w = NewWorld())
            {
                w.driver.ConfigureConstraints(k_building, 1);
                var first = Publish(w);
                RunUntil(w, 810, () => first.Phase == ProvisionalCutPhase.HandedOff, "first children published");
                Assert.That(w.ledger.TryGetOperation(first.Operation, out var children), Is.True);
                w.driver.RemainingMainSeconds = () => 0;
                var ask = new ProvisionalCutAsk { source = children.positive, plane = new float4(1, 0, 0, 0) };
                Assert.That(w.driver.RequestCut(ask, out var waiting, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));
                var other = new ProvisionalCutAsk { source = children.negative, plane = new float4(1, 0, 0, 0) };
                Assert.That(w.driver.RequestCut(other, out _, out _), Is.EqualTo(ProvisionalCutAcceptance.NotAccepted), "the room is held");

                // Every cut ended the abort way (EndEveryCut aborts each record, as a cut that cannot be established is):
                // the waiting one's reservation goes with its record.
                w.driver.EndEveryCut();
                w.driver.RemainingMainSeconds = () => 1;
                Assert.That(w.driver.RequestCut(other, out var taken, out _), Is.EqualTo(ProvisionalCutAcceptance.Published),
                    "the refused source is cut once the room is back");
                Assert.That(taken, Is.Not.Null);
                Assert.That(w.registry.SystemConstraintCount, Is.EqualTo(1));
            }
        }
    }
}
