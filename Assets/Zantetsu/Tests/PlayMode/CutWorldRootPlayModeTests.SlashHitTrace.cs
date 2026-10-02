using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The Slash hit detector's <see cref="TraceEventType.SlashHitConfirmed"/> records (DESIGN 21.16.6) through the
    /// existing trace lane, paged history and file store: every real hit is one record -- accepted or not, with or
    /// without an operation -- saved and read back as it went in, and a lane that cannot take a record changes nothing
    /// of the hit or its acceptance.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private const int TraceMaxPayload = 32;

        // One main-thread lane carrying this event only, its history, and its drainer: a Run as the file store expects.
        private sealed class HitTraceRun : IDisposable
        {
            internal readonly TraceLaneSet Lanes;
            internal readonly TracePagedHistory History;
            internal readonly TraceLaneDrainer Drainer;

            internal HitTraceRun(int laneIndexCapacity)
            {
                var profile = new TraceLaneSetProfile(
                    TraceMaxPayload, 64, 256, 8,
                    new[]
                    {
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(TraceEventType.SlashHitConfirmed), 4096, laneIndexCapacity),
                    });
                Lanes = new TraceLaneSet(profile);
                History = new TracePagedHistory(profile);
                Drainer = new TraceLaneDrainer(Lanes);
            }

            public void Dispose()
            {
                History.Dispose();
                Lanes.Dispose();
            }
        }

        private sealed unsafe class HitRecords : ITraceRecordDestination
        {
            internal readonly List<TraceEventType> Kinds = new List<TraceEventType>();
            internal readonly List<SlashHitConfirmed> Hits = new List<SlashHitConfirmed>();

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                Kinds.Add(recordKind);
                Assert.That(
                    SlashHitConfirmedTraceRecord.TryRead(new ReadOnlySpan<byte>(payload, payloadLength), out SlashHitConfirmed hit),
                    Is.True, "a SlashHitConfirmed payload");
                Hits.Add(hit);
            }
        }

        private static string TracePath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "zantetsu-slash-hit-trace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "run.ztrace");
        }

        private static void RemoveTrace(string path)
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path));
        }

        private static void AssertSameHit(SlashHitConfirmed read, SlashHitConfirmed hit, string what)
        {
            Assert.That(read.SlashId, Is.EqualTo(hit.SlashId), what + ": Slash");
            Assert.That(read.At, Is.EqualTo(hit.At), what + ": update time");
            Assert.That(read.AtLatch, Is.EqualTo(hit.AtLatch), what + ": latch update");
            Assert.That(read.Fragment, Is.EqualTo(hit.Fragment), what + ": fragment");
            Assert.That(read.Side, Is.EqualTo(hit.Side), what + ": side");
            Assert.That(read.Acceptance, Is.EqualTo(hit.Acceptance), what + ": acceptance");
            Assert.That(read.Admission, Is.EqualTo(hit.Admission), what + ": admission");
            Assert.That(read.Operation, Is.EqualTo(hit.Operation), what + ": operation");
        }

        // DIAGNOSIS ONLY (2026-10-02), off unless ZTK_HIT_TRACE_DIAG=1: lines to the test's own output (no file) with the
        // Main budget and the stage predictions around each evaluation, and every hit's answer and operation. While off,
        // nothing is read or written.
        private static readonly bool s_hitTraceDiag = Environment.GetEnvironmentVariable("ZTK_HIT_TRACE_DIAG") == "1";

        private static void HitDiagBudget(CutWorldRoot root, string what)
        {
            if (!s_hitTraceDiag) return;
            TestContext.Out.WriteLine("[hit trace diag] " + what + ": frame " + Time.frameCount + ", Main remaining "
                + CutPhysicsStep.FrameRemainingMainSeconds.ToString("R") + " s of " + CutPhysicsStep.MainBudgetSeconds.ToString("R")
                + " s, dispatch budget " + root.Dispatcher.RemainingBudget
                + ", build history " + root.Driver.BuildCosts.Count + " (predicted " + root.Driver.BuildCosts.ExpectedSeconds.ToString("R") + " s)"
                + ", publication history " + root.Driver.PublishCosts.Count + " (predicted " + root.Driver.PublishCosts.ExpectedSeconds.ToString("R") + " s)");
        }

        private static void HitDiagHits(CutWorldRoot root, IList<SlashHitConfirmed> hits, string what)
        {
            if (!s_hitTraceDiag) return;
            for (int i = 0; i < hits.Count; i++)
            {
                SlashHitConfirmed h = hits[i];
                string state = h.Operation.IsSet && root.Ledger.TryGetOperation(h.Operation, out LogicalCutOperation op) ? op.state.ToString() : "-";
                TestContext.Out.WriteLine("[hit trace diag] " + what + " hit " + i + ": Slash " + h.SlashId + ", fragment " + h.Fragment
                    + ", acceptance " + h.Acceptance + ", admission " + h.Admission + ", operation " + (h.Operation.IsSet ? h.Operation.ToString() : "none")
                    + " (" + state + ")");
            }
        }

        private static SlashSweep At(SlashSweep sweep, double at)
        {
            return new SlashSweep(
                sweep.SlashId, at, sweep.IsLatch, sweep.SourceSlashPlane, sweep.TravelAxis, sweep.SpanAxis,
                sweep.PreviousA, sweep.PreviousB, sweep.CurrentA, sweep.CurrentB);
        }

        // The acceptance follows the Main budget of the frame it is asked in (DESIGN 7.1.1): Published at once, or Pending --
        // the same operation published at a later opportunity. The record is what the hit answered then, never rewritten.
        // Run alone in a fresh session the first evaluation used up the frame's remainder before the acceptance (2026-10-02,
        // tr-diag-alone: Main remaining +6.7 ms before, -20.0 ms after, both predictions 0 s), so either answer is real.
        private static bool AcceptedEitherWay(SlashHitConfirmed hit)
        {
            return hit.Acceptance == ProvisionalCutAcceptance.Published || hit.Acceptance == ProvisionalCutAcceptance.Pending;
        }

        private static ProvisionalCutPhase? PhaseOf(CutWorldRoot root, CutOperationId operation)
        {
            return root.Driver.TransactionOf(operation)?.Phase;
        }

        /// <summary>
        /// A no-op hit, an accepted one and one passed over for an active source are three records, saved and read back
        /// with their Slash, fragment, update time and what the acceptance said; only the accepted one names an
        /// operation. The accepted cut is Published at once or Pending as the frame's budget had it, and is recorded so.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryRealHit_IsOneSlashHitConfirmedRecord_SavedAndReadBack()
        {
            yield return EveryRealHitRecorded(deferByBudget: false);
        }

        /// <summary>
        /// The same with the accepted cut deferred on purpose (the case's own budget control, for that evaluation only): it
        /// is recorded Pending, its own operation is followed to the Provisional publication before the next Slash, and on
        /// to its commit.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryRealHit_IsOneSlashHitConfirmedRecord_SavedAndReadBack_WhenTheBudgetDefersTheCut()
        {
            yield return EveryRealHitRecorded(deferByBudget: true);
        }

        private IEnumerator EveryRealHitRecorded(bool deferByBudget)
        {
            // The final cut's collection is held until the third Slash is answered: that Slash is meant to land on the
            // Provisional side of the accepted cut, whichever way the second was answered.
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8))) : null,
                null);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            unityJob.HoldEverything = true;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            string path = TracePath();
            var written = new List<SlashHitConfirmed>();
            using (var run = new HitTraceRun(8))
            {
                detector.AttachTrace(run.Lanes.CreateWriter(0));

                HitDiagBudget(root, "before Slash 1");
                Evaluate(detector, At(Level(1, 1f, -3f, 3f), 10.25), 1);
                written.AddRange(Hits(detector));
                HitDiagBudget(root, "after Slash 1, before Slash 2");
                if (deferByBudget) root.Driver.RemainingMainSeconds = () => 0.0;
                Evaluate(detector, At(Level(2, 0.3f, -3f, 3f), 10.5), 1, 2);
                root.Driver.RemainingMainSeconds = null;
                written.AddRange(Hits(detector));
                HitDiagBudget(root, "after Slash 2");
                HitDiagHits(root, written, "recorded");

                Assert.That(written.Count, Is.EqualTo(2));
                SlashHitConfirmed accepted = written[1];
                Assert.That(AcceptedEitherWay(accepted), Is.True, "the second Slash's cut accepted: " + accepted.Acceptance);
                if (deferByBudget) Assert.That(accepted.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "deferred by the case's budget");
                Assert.That(accepted.Operation.IsSet, Is.True, "an accepted cut names its operation, Pending or not");
                if (accepted.Acceptance == ProvisionalCutAcceptance.Pending)
                {
                    Assert.That(PhaseOf(root, accepted.Operation), Is.EqualTo(ProvisionalCutPhase.Accepted), "Pending: nothing of it built yet");
                    yield return Until(() => PhaseOf(root, accepted.Operation) == ProvisionalCutPhase.Published, "Pending: the same operation's Provisional pair published");
                }

                Assert.That(PhaseOf(root, accepted.Operation), Is.EqualTo(ProvisionalCutPhase.Published), "the Provisional pair stands (the final held)");
                Assert.That(root.Ledger.TryGetOperation(accepted.Operation, out LogicalCutOperation operation) && operation.state == LogicalCutOperationState.Admitted, Is.True,
                    "the source is still the operation's: the next Slash meets the Provisional side");
                HitDiagBudget(root, "before Slash 3");
                Evaluate(detector, At(Upright(3, 0.2f), 10.75), 1, 2, 3);
                written.AddRange(Hits(detector));
                HitDiagHits(root, written, "recorded");
                unityJob.HoldEverything = false;

                Assert.That(written.ConvertAll(h => h.Acceptance), Is.EqualTo(new[]
                {
                    ProvisionalCutAcceptance.EmptySide, accepted.Acceptance, ProvisionalCutAcceptance.NotAccepted,
                }), "each record is what its hit answered");
                TracePagedHistoryFileStore.SaveAtomic(path, TracePagedRunResult.Finish(run.Drainer, run.History));
            }

            var read = new HitRecords();
            TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(path, TraceMaxPayload, read);
            RemoveTrace(path);
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
            Assert.That(summary.CommittedRecordCount, Is.EqualTo(3L));
            Assert.That(read.Kinds, Is.All.EqualTo(TraceEventType.SlashHitConfirmed));
            for (int i = 0; i < written.Count; i++)
            {
                AssertSameHit(read.Hits[i], written[i], "record " + i);
            }

            Assert.That(read.Hits[0].Fragment, Is.EqualTo(body));
            Assert.That(read.Hits[0].Operation.IsSet, Is.False, "the no-op issued no operation");
            Assert.That(read.Hits[1].Operation.IsSet, Is.True, "the accepted hit names its operation");
            Assert.That(read.Hits[2].Admission, Is.EqualTo(LogicalCutAdmission.SourceActive));
            Assert.That(read.Hits[2].Side, Is.Not.Zero, "on a Provisional side");
            Assert.That(read.Hits[2].Operation.IsSet, Is.False, "passed over: no operation");
            Assert.That(read.Hits[1].At, Is.EqualTo(10.5), "the update time of the sweep that hit");

            yield return Until(() => root.Geometry.StageOf(written[1].Operation) == CutGeometryStage.Committed, "committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// A lane with room for one record: the second hit's record is dropped and counted, and the hit is still passed
        /// on and accepted (Published at once or Pending, as the frame's budget had it) -- nothing waits for the trace,
        /// and nothing is written again; both cuts go on to their commit.
        /// </summary>
        [UnityTest]
        public IEnumerator ARecordTheLaneCannotTake_IsDroppedAndCounted_AndTheHitStands()
        {
            yield return ARecordDropped(deferByBudget: false);
        }

        /// <summary>The same with both cuts deferred on purpose (the case's own budget control, for that evaluation only).</summary>
        [UnityTest]
        public IEnumerator ARecordTheLaneCannotTake_IsDroppedAndCounted_AndTheHitStands_WhenTheBudgetDefersTheCuts()
        {
            yield return ARecordDropped(deferByBudget: true);
        }

        private IEnumerator ARecordDropped(bool deferByBudget)
        {
            CutWorldRoot root = NewWorld(out Shader _);
            AddBody(root, new Vector3(-4f, 0f, 0f));
            AddBody(root, new Vector3(4f, 0f, 0f));
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            string path = TracePath();
            List<SlashHitConfirmed> hits;
            using (var run = new HitTraceRun(1))
            {
                detector.AttachTrace(run.Lanes.CreateWriter(0));
                HitDiagBudget(root, "before Slash 1");
                if (deferByBudget) root.Driver.RemainingMainSeconds = () => 0.0;
                Evaluate(detector, Level(1, 0.3f, -8f, 8f), 1);
                root.Driver.RemainingMainSeconds = null;
                hits = Hits(detector);
                HitDiagBudget(root, "after Slash 1");
                HitDiagHits(root, hits, "recorded");
                Assert.That(hits.Count, Is.EqualTo(2));
                Assert.That(hits.TrueForAll(h => AcceptedEitherWay(h) && h.Operation.IsSet), Is.True,
                    "both accepted, whatever the trace could take: " + hits[0].Acceptance + ", " + hits[1].Acceptance);
                if (deferByBudget)
                {
                    Assert.That(hits.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Pending), Is.True, "deferred by the case's budget");
                }
                Assert.That(run.Lanes.DropCountOf(0), Is.EqualTo(1L), "one record had no room");
                yield return null;
                Evaluate(detector, Level(1, 0.3f, -8f, 8f), 1);
                Assert.That(detector.HitCount, Is.Zero, "and the hit is not tried again for the trace's sake");
                TracePagedHistoryFileStore.SaveAtomic(path, TracePagedRunResult.Finish(run.Drainer, run.History));
            }

            var read = new HitRecords();
            TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(path, TraceMaxPayload, read);
            RemoveTrace(path);
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete), "a lost record is not hidden");
            Assert.That(summary.LaneDropCount, Is.EqualTo(1L));
            Assert.That(read.Hits.Count, Is.EqualTo(1));
            AssertSameHit(read.Hits[0], hits[0], "the record that had room");

            yield return Until(
                () => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(hits[1].Operation) == CutGeometryStage.Committed,
                "both committed");
            yield return EndWorld(root);
        }
    }
}
