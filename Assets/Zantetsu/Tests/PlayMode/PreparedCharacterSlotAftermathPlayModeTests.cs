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
        private IEnumerator NeedleReady(List<object> made)
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            ActivateStill(slot);
            yield return null;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(slot.Handle);
            made.Add(slot); made.Add(setup); made.Add(detector);
        }

        private IEnumerator EndColdWorld(GameObject setup)
        {
            Object.Destroy(setup);
            yield return null;
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }

        /// <summary>
        /// **A failed cut is an allowed result only when a failure record names it** (MobPlanSlashAftermath, TL 2026-10-01).
        /// A needle face makes the character's cut fail and abort (its geometry reclaimed, the driver done with it): with the
        /// driver's failure records the follower finishes and passes, one allowed failure; with no record naming it the same
        /// evaluation never passes -- it waits for a record and times out, saying so.
        /// </summary>
        [UnityTest]
        public IEnumerator Aftermath_AFailedCut_PassesOnlyWhenARecordNamesIt()
        {
            var made = new List<object>();
            yield return NeedleReady(made);
            var slot = (SandboxNpcCharacter)made[0];
            var detector = (SlashHitDetector)made[2];
            var tally = new MobPlanCutFailureTally();
            tally.Attach(coldWorld.Driver);
            var (points, _) = NeedleHull();
            double3 edge = math.normalize(points[1] - points[0]);
            double t = 60 * math.PI / 180;
            double3 n = math.cos(t) * new double3(0, 1, 0) + math.sin(t) * math.cross(edge, new double3(0, 1, 0));
            int opsBefore = coldWorld.Ledger.OperationCount;
            List<SlashHitConfirmed> hits = Evaluate(detector, PlaneSweep(1, n, points[0], edge), 1);
            Assert.That(hits.Count == 1 && hits[0].Acceptance == ProvisionalCutAcceptance.Published, Is.True);
            var named = new MobPlanSlashAftermath(coldWorld, 1, hits, new[] { slot.Handle }, opsBefore, 0, op => tally.TryGet(op, out _));
            var unnamed = new MobPlanSlashAftermath(coldWorld, 1, hits, new[] { slot.Handle }, opsBefore, 0, null);
            yield return named.Wait(30f);
            TestContext.Out.WriteLine("named: " + named.Describe());
            Assert.That(named.Passed, Is.True, "a failure the driver's records name is allowed");
            Assert.That(named.AllowedFailures, Is.EqualTo(1));
            yield return unnamed.Wait(1f);
            TestContext.Out.WriteLine("unnamed: " + unnamed.Describe());
            Assert.That(unnamed.Passed || unnamed.Finished, Is.False, "the same failure, named by no record, never ends acceptably");
            Assert.That(unnamed.TimedOut, Is.True);
            Assert.That(unnamed.Describe(), Does.Contain("no failure record names it"));
            Assert.That(unnamed.Acceptable, Is.False, "the work left at the deadline is named as an anomaly");
            tally.Detach();
            yield return EndColdWorld((GameObject)made[1]);
        }

        /// <summary>
        /// **An abnormal immediate answer is never acceptable; an operation it left is still followed to its end.** An
        /// ordinary refusal (EmptySide) passes; InvalidRequest with no operation finishes at once, not acceptable; Aborted
        /// with an operation left is followed to that operation's commit and still not acceptable.
        /// </summary>
        [UnityTest]
        public IEnumerator Aftermath_AnAbnormalImmediateAnswer_IsNeverAcceptable()
        {
            var made = new List<object>();
            yield return NeedleReady(made);
            var slot = (SandboxNpcCharacter)made[0];
            var detector = (SlashHitDetector)made[2];
            List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
            Assert.That(cut.Count == 1 && cut[0].Acceptance == ProvisionalCutAcceptance.Published, Is.True);
            CutOperationId op = cut[0].Operation;
            LogicalFragmentId fragment = cut[0].Fragment;
            var none = new VpPreparedCharacterCut[0];

            var refusal = new MobPlanSlashAftermath(coldWorld, 5, new[] { new SlashHitConfirmed(5, 0, false, fragment, 0f, ProvisionalCutAcceptance.EmptySide, LogicalCutAdmission.NoOp, default) }, none, coldWorld.Ledger.OperationCount, 0, null);
            Assert.That(refusal.Poll() && refusal.Passed, Is.True, "an ordinary refusal ends acceptably: " + refusal.Describe());

            var invalid = new MobPlanSlashAftermath(coldWorld, 6, new[] { new SlashHitConfirmed(6, 0, false, fragment, 0f, ProvisionalCutAcceptance.InvalidRequest, LogicalCutAdmission.NoOp, default) }, none, coldWorld.Ledger.OperationCount, 0, null);
            Assert.That(invalid.Poll(), Is.True, "nothing left to follow");
            TestContext.Out.WriteLine("invalid: " + invalid.Describe());
            Assert.That(invalid.Acceptable || invalid.Passed, Is.False, "InvalidRequest is an anomaly");
            Assert.That(invalid.Describe(), Does.Contain("InvalidRequest"));

            var aborted = new MobPlanSlashAftermath(coldWorld, 7, new[] { new SlashHitConfirmed(7, 0, false, fragment, 0f, ProvisionalCutAcceptance.Aborted, LogicalCutAdmission.Admitted, op) }, none, op.value - 1, 0, null);
            Assert.That(aborted.Poll(), Is.False, "the operation it left is followed");
            yield return aborted.Wait(30f);
            TestContext.Out.WriteLine("aborted: " + aborted.Describe());
            Assert.That(aborted.Finished && !aborted.TimedOut, Is.True, "followed to its end");
            Assert.That(coldWorld.Geometry.StageOf(op), Is.EqualTo(CutGeometryStage.Committed));
            Assert.That(aborted.Acceptable || aborted.Passed, Is.False, "and still not acceptable");
            Assert.That(aborted.Describe(), Does.Contain("Completed / Committed"));
            yield return EndColdWorld((GameObject)made[1]);
        }

        /// <summary>**The world ending during the wait is no end:** finished, the world's end recorded, not passed.</summary>
        [UnityTest]
        public IEnumerator Aftermath_TheWorldEndingDuringTheWait_IsNoEnd()
        {
            var made = new List<object>();
            yield return NeedleReady(made);
            var slot = (SandboxNpcCharacter)made[0];
            var detector = (SlashHitDetector)made[2];
            int opsBefore = coldWorld.Ledger.OperationCount;
            List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
            var aftermath = new MobPlanSlashAftermath(coldWorld, 1, cut, new[] { slot.Handle }, opsBefore, 0, null);
            Assert.That(aftermath.Poll(), Is.False, "the cut not committed yet");
            Object.Destroy((GameObject)made[1]);
            coldWorld.Shutdown();
            Assert.That(aftermath.Poll(), Is.True, "finished: nothing more will come");
            TestContext.Out.WriteLine("ended: " + aftermath.Describe());
            Assert.That(aftermath.WorldEnded && !aftermath.Passed && !aftermath.Acceptable, Is.True, "the world's end is no end");
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }
    }
}
