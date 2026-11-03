using NUnit.Framework;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>SKIN-GPU-FIRST-EVAL-1 review 2 (P2): a Commit reported after the request's last observation is resolved by the final resolve and the case succeeds; the observed column stays unobserved.</summary>
    public sealed class SkinBakeProbeReactionEventsTests
    {
        [Test]
        public void ACommitReportedAfterTheLastObservation_IsResolvedByTheFinalResolve_AndTheCaseSucceeds()
        {
            var events = new SkinBakeProbeReactionEvents(16);
            var operation = new CutOperationId(7);
            int observedCommitFrame = -1;          // the bench's observation column: nothing seen at the last step
            int committedEventFrame = -1; long committedEventTick = 0;
            // the last observation of the round: no Commit reported yet
            Assert.That(events.TryResolve(SkinBakeProbeReactionEvents.Kind.Commit, operation, out int frame, out long tick), Is.False, "no Commit before it is reported");
            Assert.That(SkinBakeProbeReactionEvents.CaseSucceeded(operation.IsSet, committedEventFrame), Is.False, "without the report the case is not a success");
            // the DAG's hook reports the Commit after the last observation (during the reset or a later round)
            events.Add(SkinBakeProbeReactionEvents.Kind.Commit, operation, 0, 700, 0);
            // the final resolve before the rows are written
            if (committedEventFrame < 0 && events.TryResolve(SkinBakeProbeReactionEvents.Kind.Commit, operation, out frame, out tick)) { committedEventFrame = frame; committedEventTick = tick; }
            Assert.That(committedEventFrame, Is.EqualTo(700), "the late Commit is resolved into the event column");
            Assert.That(committedEventTick, Is.Not.EqualTo(0), "with the tick taken inside the hook");
            Assert.That(SkinBakeProbeReactionEvents.CaseSucceeded(operation.IsSet, committedEventFrame), Is.True, "the verdict is the Commit event");
            Assert.That(observedCommitFrame, Is.EqualTo(-1), "the observed column stays unobserved: the Commit came after the last observation, which is no failure");
        }

        [Test]
        public void AnUnsetOperation_IsNeverResolved_AndNeverASuccess()
        {
            var events = new SkinBakeProbeReactionEvents(16);
            events.Add(SkinBakeProbeReactionEvents.Kind.Commit, default, 0, 10, 0);
            Assert.That(events.TryResolve(SkinBakeProbeReactionEvents.Kind.Commit, default, out _, out _), Is.False);
            Assert.That(SkinBakeProbeReactionEvents.CaseSucceeded(false, 10), Is.False, "a request that made no operation is no success whatever frame is given");
        }

        [Test]
        public void ARepeatedReport_IsCountedNotRecordedTwice_AndAFullBufferCountsWhatItDropped()
        {
            var events = new SkinBakeProbeReactionEvents(2);
            var a = new CutOperationId(1); var b = new CutOperationId(2); var c = new CutOperationId(3);
            events.Add(SkinBakeProbeReactionEvents.Kind.Ledger, a, 0, 100, 0);
            events.Add(SkinBakeProbeReactionEvents.Kind.Ledger, a, 0, 101, 0);   // the same event reported again: the first report stands
            Assert.That(events.Count(SkinBakeProbeReactionEvents.Kind.Ledger), Is.EqualTo(1));
            Assert.That(events.Duplicates, Is.EqualTo(1));
            Assert.That(events.TryResolve(SkinBakeProbeReactionEvents.Kind.Ledger, a, out int frame, out _) && frame == 100, Is.True, "the first report is the record");
            events.Add(SkinBakeProbeReactionEvents.Kind.Ledger, b, 0, 102, 0);
            events.Add(SkinBakeProbeReactionEvents.Kind.Ledger, c, 0, 103, 0);   // the buffer is full
            Assert.That(events.Count(SkinBakeProbeReactionEvents.Kind.Ledger), Is.EqualTo(2));
            Assert.That(events.NotKept, Is.EqualTo(1));
            Assert.That(events.TryResolve(SkinBakeProbeReactionEvents.Kind.Ledger, c, out _, out _), Is.False, "what was not kept is not resolved (-1 in the record, never 'did not happen')");
            events.Reset();
            Assert.That(events.Count(SkinBakeProbeReactionEvents.Kind.Ledger) == 0 && events.Duplicates == 0 && events.NotKept == 0, Is.True);
        }

        [Test]
        public void ACollectThatEndedFailed_IsResolvedWithItsOutcome_NeverAsCompleted()
        {
            var events = new SkinBakeProbeReactionEvents(16);
            events.Add(SkinBakeProbeReactionEvents.Kind.Collect, default, 55, 300, (int)VpSkinBakeCollectOutcome.Failed);
            Assert.That(events.TryResolveCollect(55, out int frame, out _, out int code), Is.True);
            Assert.That(frame, Is.EqualTo(300));
            Assert.That(code, Is.EqualTo((int)VpSkinBakeCollectOutcome.Failed));
            Assert.That(code, Is.Not.EqualTo((int)VpSkinBakeCollectOutcome.Completed), "a failed collect is no CPU ready");
            Assert.That(events.TryResolveCollect(0, out _, out _, out _), Is.False, "request id 0 is no request");
            Assert.That(events.TryResolve(SkinBakeProbeReactionEvents.Kind.Collect, new CutOperationId(1), out _, out _), Is.False, "collects are resolved by request id, not by operation");
        }
    }
}
