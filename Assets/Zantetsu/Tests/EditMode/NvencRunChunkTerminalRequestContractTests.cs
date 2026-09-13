using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Run chunk terminal request boundary:
    /// the fixed one-slot Finalize/Abandon request that the Output Worker
    /// advances exactly once and the Main Thread collects exactly once without
    /// waiting. Uses fakes and a deterministic park signal; no GPU, NVENC,
    /// native resource, real-time sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunChunkTerminalRequestContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Finalize_ExecutesOnWorkerThread_NotCallerThread()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Finalizer.ExecutingThreadName, Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
            }
        }

        [Test]
        public void Abandon_ExecutesOnWorkerThread_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Abandoned));
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));

                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(outcome.Result, Is.Null);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void ProcessorHoldingWork_DoesNotTouchTerminal()
        {
            using (ManualResetEventSlim writerEntered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);

                // The Coordinator already accepted frame 1; the processor's
                // record provides the sink append for that frame.
                Assert.That(h.Context.TryRecordAcceptedFrame(1), Is.True);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // Park the processor mid-record behind the sink writer.
                h.Writer.Entered = writerEntered;
                h.Writer.WaitForGateHeld = gateHeld;

                Thread holder = new Thread(() =>
                {
                    if (writerEntered.Wait(WatchdogTimeoutMs))
                    {
                        if (h.State.TryBeginResourceResolution())
                        {
                            gateHeld.Set();
                            release.Wait(WatchdogTimeoutMs);
                            h.State.EndResourceResolution();
                        }
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    h.Enqueue(record);

                    // Accepting notifies the worker; the worker dequeues the record
                    // and blocks in the sink, so the terminal must not be touched
                    // while the processor holds the current record.
                    Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "worker did not reach the sink");
                    Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
                    Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge after release");

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
            }
        }

        [Test]
        public void Finalize_AfterDrain_ExactlyOnce_ExactResultOnce()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                h.AcceptAndAppendChunk(2, 48, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);

                Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
                Assert.That(ReferenceEquals(outcome.Result, held), Is.True);

                // Collected exactly once.
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Request_RejectedRunningPoisonedDoubleCross()
        {
            using (Harness h = Harness.Create())
            {
                // Running rejects both kinds.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);

                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // Finalize accepts exactly once; double and cross are rejected.
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
            }

            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // Abandon accepts exactly once; double and cross are rejected.
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
            }

            using (Harness h = Harness.Create())
            {
                // Poisoned rejects both kinds.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
            }
        }

        [Test]
        public void Request_RejectedBeforeSubmitWorkerDrainCompleted()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);

                // Draining but the Submit Worker drain evidence is not yet
                // published: the terminal must not be accepted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);

                // Publish the monotonic drain evidence: acceptance now works.
                h.SubmitDrained = true;
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
            }
        }

        [Test]
        public void Request_AfterCompletedOrCollected_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                // Completed, not yet collected: no new request accepted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);

                Assert.That(h.Worker.TryCollectTerminal(out _), Is.True);

                // Collected: still no new request accepted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
            }
        }

        [Test]
        public void WorkerBoundToExactContext_DoesNotTouchForeignContext()
        {
            using (Harness h = Harness.Create())
            {
                // A separate foreign Run chunk context that no worker is bound
                // to. It must never be finalized or abandoned by worker A.
                NvencOwnedAccessUnitBuffer foreignBuffer = new NvencOwnedAccessUnitBuffer(h.State);
                FakeWriter foreignWriter = new FakeWriter();
                NvencRunChunkSink foreignSink = new NvencRunChunkSink(h.State, foreignBuffer, foreignWriter);
                NvencRunChunkFinalizationCoordinator foreignCoordinator = new NvencRunChunkFinalizationCoordinator(foreignWriter);
                NvencRunChunkContext foreignContext = new NvencRunChunkContext(MakeIssue(), foreignSink, foreignCoordinator, "chunk/foreign");

                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the finalize request");
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));

                // The foreign context is untouched: no finalizer contact and
                // still Open.
                Assert.That(foreignWriter.CallCount, Is.EqualTo(0));
                Assert.That(foreignContext.State, Is.EqualTo(NvencRunChunkContextState.Open));
            }
        }

        [Test]
        public void Constructor_RejectsForeignSinkProcessStateOrQueue()
        {
            using (Harness h = Harness.Create())
            {
                // A context whose Sink is not the processor's Sink.
                NvencOwnedAccessUnitBuffer otherBuffer = new NvencOwnedAccessUnitBuffer(h.State);
                FakeWriter otherWriter = new FakeWriter();
                NvencRunChunkSink otherSink = new NvencRunChunkSink(h.State, otherBuffer, otherWriter);
                NvencRunChunkContext otherContext = new NvencRunChunkContext(
                    MakeIssue(), otherSink, new NvencRunChunkFinalizationCoordinator(otherWriter), "chunk/other");

                Assert.Throws<ArgumentException>(() =>
                    new NvencOrderedOutputWorkerService(h.State, h.Processor, otherContext, h.SubmitWorker, h.Teardown));

                // A Submit Worker bound to a different process state.
                NvencCaptureProcessState foreignState = new NvencCaptureProcessState();
                NvencOrderedSubmitWorkerService foreignStateSubmitWorker = BuildSubmitWorker(foreignState);
                Assert.Throws<ArgumentException>(() =>
                    new NvencOrderedOutputWorkerService(h.State, h.Processor, h.Context, foreignStateSubmitWorker, h.Teardown));

                // A Submit Worker bound to the same process state but a
                // different Submit-to-Output Queue.
                NvencOrderedSubmitWorkerService foreignQueueSubmitWorker = BuildSubmitWorker(h.State);
                Assert.Throws<ArgumentException>(() =>
                    new NvencOrderedOutputWorkerService(h.State, h.Processor, h.Context, foreignQueueSubmitWorker, h.Teardown));

                // A Submit Worker whose internal Submit Processor is bound to a
                // different process state.
                NvencOrderedSubmitWorkerService splitSubmitWorker = BuildSplitSubmitWorker(h.State, foreignState);
                Assert.Throws<ArgumentException>(() =>
                    new NvencOrderedOutputWorkerService(h.State, h.Processor, h.Context, splitSubmitWorker, h.Teardown));

                // An Output Processor bound to a different process state.
                NvencOrderedOutputProcessor foreignProcessor = new NvencOrderedOutputProcessor(
                    foreignState, h.OutputQueue, h.Collector, h.Sink, h.ReleaseCoordinator, h.RecoveryCoordinator, h.Boundary);
                Assert.Throws<ArgumentException>(() =>
                    new NvencOrderedOutputWorkerService(h.State, foreignProcessor, h.Context, h.SubmitWorker, h.Teardown));
            }
        }

        [Test]
        public void FinalizeFalse_KeepsRequest_RetryOnNextNotifyConverges()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // The single owned region is temporarily reserved (not Free),
                // so the sink is not finalization-admissible: the finalize
                // pre-verification fails before any finalizer contact.
                CaptureFrameWorkToken pendingToken = MakeToken(2);
                Assert.That(h.Buffer.TryBeginWrite(pendingToken, out NvencAccessUnitWriteLease writeLease), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not park with the held request");

                // Request held, finalizer never contacted, nothing published.
                Assert.That(h.Writer.CallCount, Is.EqualTo(0));
                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);

                // Release the reservation through the normal cancel path and
                // re-notify: the held request converges without any append.
                Assert.That(h.Buffer.TryCancelCollectorReservation(writeLease, out _), Is.True);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge after the reservation was released");

                Assert.That(h.Writer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
            }
        }

        [Test]
        public void FinalizerException_FatalPoisonStopNoTerminal()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                InvalidOperationException boom = new InvalidOperationException("boom");
                h.Finalizer.ExceptionToThrow = boom;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the finalizer exception");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(ReferenceEquals(captured, boom), Is.True);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void AbandonRequest_ContextAlreadyTerminal_InvariantFatal()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);

                // Finalize directly on the caller thread: the accepted abandon
                // request can no longer abandon the context.
                h.Freeze();
                Assert.That(h.Context.TryFinalize(out _), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the abandon invariant failure");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(captured, Is.TypeOf<InvalidOperationException>());
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void TerminalCompletionAlone_DoesNotStopWorker()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the finalize request");
                Assert.That(outcome.IsFinalized, Is.True);

                // Terminal completion alone never stops the worker: it stays
                // alive waiting for an external teardown request.
                Assert.That(h.Worker.IsStopped, Is.False);
            }
            // Dispose is the teardown request: it poisons and physically stops
            // the worker.
        }

        [Test]
        public void RequestNotify_IsNeverMissed()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // The worker is parked; the acceptance's own notify must wake it
                // even when further notifies coalesce.
                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                h.Worker.Notify();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge the request");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Outcome_DefaultIsNone_NotAbandoned()
        {
            NvencRunChunkTerminalOutcome none = default;

            Assert.That(none.IsNone, Is.True);
            Assert.That(none.IsFinalized, Is.False);
            Assert.That(none.IsAbandoned, Is.False);
            Assert.That(none.Result, Is.Null);
        }

        [Test]
        public void Source_NoExtraQueueTaskThreadPoolTimerSleepSpinTokenProof()
        {
            string request = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkTerminalRequest.cs"));

            string[] requestForbidden =
            {
                "new Queue", "new List", "new Dictionary", "Task", "ThreadPool",
                "Timer", "Thread.Sleep", "SpinWait", "Token", "Proof", "Nonce",
                "new Thread", "lock (" + ")", "Monitor",
            };

            foreach (string word in requestForbidden)
            {
                Assert.That(request, Does.Not.Contain(word), "terminal request source must not contain: " + word);
            }

            string service = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputWorkerService.cs"));
            Assert.That(CountOccurrences(service, "new Thread("), Is.EqualTo(1),
                "the service must create exactly one worker thread.");
            Assert.That(CountOccurrences(service, "_signal.Wait()"), Is.EqualTo(1),
                "the worker must have exactly one blocking wait.");

            string[] serviceForbidden =
            {
                "ThreadPool", "Task", "Thread.Sleep", "Timer", "SpinWait",
                "new Queue", "new List", "new Dictionary",
            };

            foreach (string word in serviceForbidden)
            {
                Assert.That(service, Does.Not.Contain(word), "service source must not contain: " + word);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Collects the requested Run chunk terminal by confirming the real
        /// condition inside a watchdog. A settle observed after the request may
        /// be a raise that was already in flight when the request was accepted,
        /// so it is used only as a wake hint; see TerminalConvergence.
        /// </summary>
        private static NvencRunChunkTerminalOutcome CollectTerminal(Harness h, string message)
        {
            return TerminalConvergence.Collect(
                h.Worker.TryCollectTerminal, h.Worker, h.SettledEvent, WatchdogTimeoutMs, message);
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents = new CaptureRunInitializationDocumentSet(layout, InitId);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(documents);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static NvencOrderedSubmitWorkerService BuildSubmitWorker(NvencCaptureProcessState state)
        {
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samples = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool sync = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitCredits = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCredits = new NvencFrameCompletionCreditPool(state);
            NvencSourceResourceReleaseCoordinator release = new NvencSourceResourceReleaseCoordinator(
                state, work, samples, sync, submitCredits, frameCredits,
                new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
            NvencOrderedSubmitProcessor processor = new NvencOrderedSubmitProcessor(
                state,
                new NvencFixedSpscQueue<NvencSubmissionRecord>(),
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>(),
                work, samples, release, new FakeSubmitter());
            return new NvencOrderedSubmitWorkerService(state, processor);
        }

        private static NvencOrderedSubmitWorkerService BuildSplitSubmitWorker(
            NvencCaptureProcessState workerState,
            NvencCaptureProcessState processorState)
        {
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(processorState);
            NvencEncodeSampleSlotPool samples = new NvencEncodeSampleSlotPool(processorState);
            NvencGpuConversionSyncPool sync = new NvencGpuConversionSyncPool(processorState);
            NvencSubmitToOutputCreditPool submitCredits = new NvencSubmitToOutputCreditPool(processorState);
            NvencFrameCompletionCreditPool frameCredits = new NvencFrameCompletionCreditPool(processorState);
            NvencSourceResourceReleaseCoordinator release = new NvencSourceResourceReleaseCoordinator(
                processorState, work, samples, sync, submitCredits, frameCredits,
                new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
            NvencOrderedSubmitProcessor processor = new NvencOrderedSubmitProcessor(
                processorState,
                new NvencFixedSpscQueue<NvencSubmissionRecord>(),
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>(),
                work, samples, release, new FakeSubmitter());
            return new NvencOrderedSubmitWorkerService(workerState, processor);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        // ---- Fakes ----

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private int _appendCount;
            private int _finalizeCount;
            internal NvencRunChunkAppendOutcome Outcome = NvencRunChunkAppendOutcome.Appended;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim WaitForGateHeld;
            internal bool BuildReceipt = true;
            internal NvencRunChunkFinalizationReceipt ReceiptToReturn;
            internal Action OnFinalize;
            internal string ExecutingThreadName;

            internal int AppendCount => Volatile.Read(ref _appendCount);

            internal int CallCount => Volatile.Read(ref _finalizeCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                Interlocked.Increment(ref _appendCount);

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (WaitForGateHeld != null)
                {
                    WaitForGateHeld.Wait(WatchdogTimeoutMs);
                }

                return Outcome;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);
                ExecutingThreadName = Thread.CurrentThread.Name;
                OnFinalize?.Invoke();

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

                if (!BuildReceipt)
                {
                    return null;
                }

                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        private sealed class PatternSource : INvencOutputBitstreamSource
        {
            private readonly int _length;
            private readonly byte _seed;

            internal PatternSource(int length, byte seed)
            {
                _length = length;
                _seed = seed;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            private int _callCount;
            internal bool Result = true;
            internal int ResultLength = 1024;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim WaitForGateHeld;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal string ExecutingThreadName;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (SourceEntered != null)
                {
                    SourceEntered.Set();
                }

                if (WaitForGateHeld != null)
                {
                    WaitForGateHeld.Wait(WatchdogTimeoutMs);
                }

                validLength = ResultLength;
                return Result;
            }
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            public bool TryGetEvidence(in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                evidence = default;
                return false;
            }
        }

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter
        {
            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                return true;
            }
        }

        private sealed class FakeTeardown : INvencOutputWorkerTeardown
        {
            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeOutputSource Source;
            internal FakeWriter Writer;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencRunChunkSink Sink;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal FakeTeardown Teardown;
            internal ManualResetEventSlim SettledEvent;

            internal NvencRunChunkFinalizationCoordinator Coordinator;
            internal NvencRunChunkContext Context;

            // The shared writer is both the processor sink's appender and the
            // chunk context's finalizer.
            internal FakeWriter Finalizer => Writer;

            // Exact Submit Worker bound to the same process state; the worker
            // reads its monotonic DrainCompleted directly.
            internal NvencCaptureWorkSlotPool SubmitWorkSlots;
            internal NvencEncodeSampleSlotPool SubmitSampleSlots;
            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCreditPool;
            internal NvencFrameCompletionCreditPool SubmitFrameCompletionCredits;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            // Write-only convenience: publishes the Submit Worker's monotonic
            // drain completion evidence deterministically, and freezes the Run
            // chunk's accepted sequence (StopAccepting) at that same drain
            // boundary so a terminal request can be admitted.
            internal bool SubmitDrained
            {
                set
                {
                    SetField(SubmitWorker, "_drainCompleted", value);
                    if (value)
                    {
                        Freeze();
                    }
                }
            }

            private readonly Action _settledHandler;

            internal Harness()
            {
                State = new NvencCaptureProcessState();

                // One shared buffer, writer, and sink feed both the processor
                // and the exact Run chunk context, so the processor's append
                // boundary and the context's finalization boundary agree.
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                Coordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                Context = new NvencRunChunkContext(MakeIssue(), Sink, Coordinator, "chunk/0");

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Source = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);

                // Minimal exact Submit Worker bound to the same process state;
                // never started, so DrainCompleted stays false until the test
                // publishes it.
                SubmitWorkSlots = new NvencCaptureWorkSlotPool(State);
                SubmitSampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitSyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitToOutputCreditPool = new NvencSubmitToOutputCreditPool(State);
                SubmitFrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                SubmitSubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                SubmitReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, SubmitWorkSlots, SubmitSampleSlots, SubmitSyncSlots,
                    SubmitToOutputCreditPool, SubmitFrameCompletionCredits,
                    new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmitSubmissionQueue, OutputQueue,
                    SubmitWorkSlots, SubmitSampleSlots, SubmitReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                // Deterministically park the worker once before returning.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                AppendChunk(frameId, length, seed);
            }

            internal void AppendChunk(long frameId, int length, byte seed)
            {
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }

            internal void Freeze()
            {
                Assert.That(Context.TryFreezeAcceptedFrames(out _), Is.True);
            }

            internal NvencSubmitToOutputRecord CreateSubmitted(long frameId)
            {
                RentAll(
                    out NvencCaptureWorkSlotLease work,
                    out NvencEncodeSampleSlotLease sample,
                    out NvencSubmitToOutputCreditLease submit,
                    out NvencFrameCompletionCreditLease frame);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), work.SlotIndex, work.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateSubmitted(token, work, sample, submit, frame);
            }

            internal void Enqueue(NvencSubmitToOutputRecord record)
            {
                Assert.That(OutputQueue.TryEnqueue(record), Is.True);
            }

            internal void WaitForPhysicalStop(string message)
            {
                Thread workerThread = GetWorkerThread();
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }

            private Thread GetWorkerThread()
            {
                FieldInfo field = typeof(NvencOrderedOutputWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                return (Thread)field?.GetValue(Worker);
            }

            private void RentAll(
                out NvencCaptureWorkSlotLease work,
                out NvencEncodeSampleSlotLease sample,
                out NvencSubmitToOutputCreditLease submit,
                out NvencFrameCompletionCreditLease frame)
            {
                Assert.That(WorkSlots.TryRent(out work), Is.True);
                Assert.That(SampleSlots.TryRent(out sample), Is.True);
                Assert.That(SubmitToOutputCredits.TryRent(out submit), Is.True);
                Assert.That(FrameCompletionCredits.TryRent(out frame), Is.True);
            }

            public void Dispose()
            {
                if (!Worker.IsStopped)
                {
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;
                SettledEvent.Dispose();
            }
        }
    }
}
