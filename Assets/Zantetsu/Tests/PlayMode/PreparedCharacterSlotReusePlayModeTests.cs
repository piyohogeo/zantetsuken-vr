using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        /// <summary>
        /// **The reused-slot check's pick and its Slash** (TL, 2026-10-01; MobPlanReuseProbe, the Player check's own rule).
        /// A crowd slot carries a first individual, cut through to its commit; the slot comes back, is prepared again and
        /// carries a second. Reuse is read from the slot's history and the individual's generation: the first individual is
        /// not reused, the second is only with its own prepared cut (not the first's), its own generation (not one the
        /// slot has passed), not retired, and registered with the detector -- its fragment (Source) not yet made is no
        /// condition. The small Slash aimed at its current shape, given once to the ordinary detector, hits that
        /// individual's own fragment; the hit is accepted, its own new operation (not the first individual's) is published
        /// and its geometry committed.
        /// </summary>
        [UnityTest]
        public IEnumerator AReusedSlotsIndividual_IsPickedByItsGeneration_AndCutThroughTheDetectorByTheAimedSlash()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            ActivateStill(slot);
            int firstFrame = Time.frameCount;
            VpPreparedCharacterCut first = slot.Handle;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(first);
            yield return null;
            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 1, 1, first, null, false, firstFrame, detector), Does.StartWith("not reused"), "the slot's first individual is not reused");

            List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            CutOperationId firstOp = cut[0].Operation;
            yield return UntilCommitted(firstOp);
            for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(slot.IsReturnReady, Is.True);
            Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
            yield return UntilPrepared(slot);
            ActivateStill(slot);
            int secondFrame = Time.frameCount;
            VpPreparedCharacterCut second = slot.Handle;
            Assert.That(slot.Activations, Is.EqualTo(2));
            Assert.That(second, Is.Not.SameAs(first), "the second individual has its own prepared cut");
            Assert.That(second.Source.IsSet, Is.False, "its fragment is made at its first hit");

            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 2, 2, second, first, false, secondFrame, detector), Is.EqualTo("not a hit target of the detector"), "not until registered");
            detector.AddCharacter(second);
            yield return null;
            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 2, 2, second, first, false, secondFrame, detector), Is.Null, "the reused individual stands as a hit target");
            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 2, 2, first, first, false, secondFrame, detector), Is.EqualTo("the slot's prepared cut is not this individual's"));
            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 2, 1, second, first, false, secondFrame, detector), Does.StartWith("not reused"), "generation 1 is not reused");
            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 3, 3, second, first, false, secondFrame, detector), Does.StartWith("the slot was activated again since"));
            Assert.That(MobPlanReuseProbe.WhyNotTarget(slot, 2, 2, second, first, true, secondFrame, detector), Is.EqualTo("retired"));

            int opsBefore = coldWorld.Ledger.OperationCount;
            Assert.That(MobPlanReuseProbe.TryAim(second, null, Vector3.forward, 2, 0.0, 0.25f, out SlashSweep sweep, out string aimed), Is.True, aimed);
            TestContext.Out.WriteLine("aimed: " + aimed);
            List<SlashHitConfirmed> hits = Evaluate(detector, sweep, 2);
            Assert.That(second.Source.IsSet, Is.True);
            foreach (SlashHitConfirmed h in hits)
                TestContext.Out.WriteLine("hit fragment " + h.Fragment.value + (h.Fragment == second.Source ? " (the second individual)" : coldWorld.Ledger.TryGetOrigin(h.Fragment, out CutOperationId origin, out _) ? " (a piece of operation " + origin.value + ")" : " (another fragment)") + " => " + h.Acceptance);
            List<SlashHitConfirmed> mine = hits.FindAll(h => h.Fragment == second.Source);
            Assert.That(mine.Count, Is.EqualTo(1), "one hit on the second individual's own fragment (the sweep may meet what lies there too)");
            Assert.That(mine[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            CutOperationId op = second.Operation;
            Assert.That(op, Is.EqualTo(mine[0].Operation));
            Assert.That(op, Is.Not.EqualTo(firstOp), "not the first individual's operation");
            Assert.That(op.value, Is.GreaterThan(opsBefore), "an operation admitted by this Slash");
            // The acceptance's Published is the Provisional publication; the ledger leaves Admitted at the Final (Logical) one.
            LogicalCutOperation record = default;
            for (int i = 0; i < 600 && !(coldWorld.Ledger.TryGetOperation(op, out record) && record.state != LogicalCutOperationState.Admitted); i++) yield return null;
            TestContext.Out.WriteLine("operation " + op.value + ": source " + record.source.value + " (the individual's fragment " + second.Source.value + "), state " + record.state);
            Assert.That(record.state, Is.Not.EqualTo(LogicalCutOperationState.Admitted), "Final/Logical published");
            Assert.That(record.source, Is.EqualTo(second.Source), "of its own fragment");
            yield return UntilCommitted(op);
            Assert.That(MobPlanReuseProbe.TryAim(first, null, Vector3.forward, 3, 0.0, 0.25f, out _, out string none), Is.False, "a disposed prepared cut is not aimed at: " + none);

            Object.Destroy(setup);
            yield return null;
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }
    }
}
