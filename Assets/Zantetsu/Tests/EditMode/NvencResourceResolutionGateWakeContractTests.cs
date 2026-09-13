using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the one thing that can restart a Phase 0.11 Output
    /// Worker which has already taken a record: the release of the shared
    /// resource-resolution gate. A collector whose commit could not take that
    /// gate keeps its record, its write lease and its copy proof and parks,
    /// and no enqueue, drain, or completion is coming for it - so the gate's
    /// own release has to say "try again".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fixture composes both real worker services over one process state
    /// and one set of pools, binds both wakes once, and then creates the
    /// contention for real: a second thread holds the gate across the moment
    /// the output copy returns, so the commit genuinely fails and the
    /// collector genuinely parks. Nothing here reaches into a private field,
    /// reads source text, or counts notifications; the tests release the gate
    /// and then watch the record finish.
    /// </para>
    /// <para>
    /// No test calls the Output Worker's <c>Notify</c> to make progress, and
    /// no test enqueues unrelated work or drains anything to get the parked
    /// record moving.
    /// </para>
    /// </remarks>
    public class NvencResourceResolutionGateWakeContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        /// <summary>
        /// Deliberately short deadline for a probe whose condition must not
        /// become true, so the probe's own failure is the assertion.
        /// </summary>
        private const int BoundedProbeTimeoutMs = 250;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>
        /// The whole point of the unit: a collector parked by a busy gate
        /// resumes when that gate is released, and nothing else is needed.
        /// </summary>
        [Test]
        public void CommitBlockedByABusyGate_ResumesWhenOnlyTheGateIsReleased()
        {
            using (Harness h = Harness.Create())
            {
                h.HoldTheOutputCopy();

                NvencSubmissionRecord record = h.CreateSubmission(1);
                h.CompletionSource.MarkCompleted(record.WorkToken);
                h.EnqueueSubmission(record);
                h.SubmitWorker.Notify();

                Assert.That(h.SourceEntered.Wait(WatchdogTimeoutMs), Is.True,
                    "the Output Worker did not reach the output copy");

                // Held across the copy's return, so the commit that follows it
                // cannot take the gate and must park with its proof.
                h.HoldGateOnAnotherThread();

                h.SettledEvent.Reset();
                h.ReleaseSource.Set();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Output Worker did not park after the blocked commit");

                // Parked, holding the record: it left the queue, nothing was
                // appended, and no completion exists.
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord _), Is.False);

                // And it stays that way for as long as the gate is held.
                h.SettledEvent.Reset();
                Assert.That(h.SettledEvent.Wait(BoundedProbeTimeoutMs), Is.False,
                    "nothing may move while the gate is still held");
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));

                // The only thing this test does to set it going again.
                h.ReleaseGate();

                NvencFrameCompletionRecord completion = WaitForCompletion(
                    h, "the parked record did not resume when the gate was released");

                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);

                // The copy was not repeated: the saved proof was committed, so
                // the output source was contacted exactly once.
                Assert.That(h.OutputSource.CallCount, Is.EqualTo(1));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord _), Is.False);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));

                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.SubmitWorker.TryGetFailure(out Exception _), Is.False);
                Assert.That(h.Worker.TryGetFailure(out Exception _), Is.False);
            }
        }

        /// <summary>
        /// Resuming a parked record changes no order: the record that was
        /// taken first still completes first.
        /// </summary>
        [Test]
        public void ResumeAfterAGateRelease_LeavesQueueOrderUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                h.HoldTheOutputCopy();

                NvencSubmissionRecord first = h.CreateSubmission(1);
                NvencSubmissionRecord second = h.CreateSubmission(2);
                h.CompletionSource.MarkCompleted(first.WorkToken);
                h.CompletionSource.MarkCompleted(second.WorkToken);

                h.SubmitSettledEvent.Reset();
                h.EnqueueSubmission(first);
                h.EnqueueSubmission(second);
                h.SubmitWorker.Notify();
                Assert.That(h.SubmitSettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Submit Worker did not publish both records");

                Assert.That(h.SourceEntered.Wait(WatchdogTimeoutMs), Is.True,
                    "the Output Worker did not reach the output copy");

                // The second record is queued behind the one being held.
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));

                h.HoldGateOnAnotherThread();

                h.SettledEvent.Reset();
                h.ReleaseSource.Set();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Output Worker did not park after the blocked commit");

                // The queued record is not overtaken while the first is parked.
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));

                h.ReleaseGate();

                NvencFrameCompletionRecord firstCompletion = WaitForCompletion(
                    h, "the parked record did not resume");
                NvencFrameCompletionRecord secondCompletion = WaitForCompletion(
                    h, "the queued record did not follow it");

                Assert.That(firstCompletion.WorkToken.IdenticalTo(first.WorkToken), Is.True);
                Assert.That(secondCompletion.WorkToken.IdenticalTo(second.WorkToken), Is.True);
                Assert.That(
                    firstCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(
                    secondCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(2));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        /// <summary>
        /// The connection is made once and released only when the worker it
        /// wakes can no longer need it.
        /// </summary>
        [Test]
        public void TheGateWakeBinding_IsMadeOnceAndReleasedOnlyAfterAPhysicalStop()
        {
            using (Harness h = Harness.Create())
            using (Harness other = Harness.Create())
            {
                // The harness already bound this state to its one worker.
                Assert.Throws<ArgumentNullException>(
                    () => h.State.BindResourceResolutionReleaseNotification(null));
                Assert.Throws<InvalidOperationException>(
                    () => h.State.BindResourceResolutionReleaseNotification(h.Worker));

                Assert.Throws<ArgumentNullException>(
                    () => h.State.UnbindResourceResolutionReleaseNotification(null));

                // A worker this state never bound cannot release the binding.
                Assert.Throws<InvalidOperationException>(
                    () => h.State.UnbindResourceResolutionReleaseNotification(other.Worker));

                // Nor can the bound one while it is still running and may be
                // holding a record that needs the wake.
                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.Throws<InvalidOperationException>(
                    () => h.State.UnbindResourceResolutionReleaseNotification(h.Worker));
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Confirms the real condition - a collected Frame Completion - inside
        /// a watchdog. The Output Worker's settle is only a wake hint and is
        /// re-checked, and this helper never notifies the Output Worker.
        /// </summary>
        private static NvencFrameCompletionRecord WaitForCompletion(Harness h, string message)
        {
            NvencFrameCompletionRecord completion;
            if (h.Boundary.TryCollect(out completion))
            {
                return completion;
            }

            Stopwatch watch = Stopwatch.StartNew();
            while (true)
            {
                h.SettledEvent.Reset();

                if (h.Boundary.TryCollect(out completion))
                {
                    return completion;
                }

                long remaining = WatchdogTimeoutMs - watch.ElapsedMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }

                // The wake hint's own result is deliberately ignored: only the
                // re-check below decides.
                h.SettledEvent.Wait((int)remaining);

                if (h.Boundary.TryCollect(out completion))
                {
                    return completion;
                }

                if (watch.ElapsedMilliseconds >= WatchdogTimeoutMs)
                {
                    break;
                }
            }

            Assert.Fail(message);
            return default;
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);

            CaptureRunInitializationExecutionReceipt receipt =
                new CaptureRunInitializationExecutionCoordinator(
                    new FakeProvisioner(), new FakeMarkerWriter())
                .Execute(new CaptureRunInitializationDocumentSet(layout, InitId));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
            CaptureRunInitializationSessionOwnershipLease ownership =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);

            return CaptureRunInitializationSession.IssuanceProof.Mint(
                ownership,
                CaptureRunLockIdentityEvidence.Create(ownership, ownership.LockPathSet),
                CaptureRunInitializationReadyEvidence.FromFresh(receipt));
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            internal FakeHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(
                CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private int _appendCount;

            internal int AppendCount => Volatile.Read(ref _appendCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                Interlocked.Increment(ref _appendCount);
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(
                NvencRunChunkFinalizationOperation operation)
            {
                return NvencRunChunkFinalizationReceipt.Create(
                    this,
                    operation,
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        operation.ArtifactId, operation.AccumulatedByteLength, Hash64));
            }
        }

        /// <summary>
        /// Stands in for the native output source and, when armed, holds the
        /// Output Worker inside the copy so the test can take the gate before
        /// the commit that follows it.
        /// </summary>
        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            private int _callCount;

            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim WaitForRelease;

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                Interlocked.Increment(ref _callCount);

                if (SourceEntered != null)
                {
                    SourceEntered.Set();
                }

                if (WaitForRelease != null)
                {
                    WaitForRelease.Wait(WatchdogTimeoutMs);
                }

                validLength = 1024;
                return true;
            }
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            private readonly object _gate = new object();
            private readonly List<CaptureFrameWorkToken> _completed = new List<CaptureFrameWorkToken>();

            internal void MarkCompleted(in CaptureFrameWorkToken token)
            {
                lock (_gate)
                {
                    _completed.Add(token);
                }
            }

            public bool TryGetEvidence(
                in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                lock (_gate)
                {
                    foreach (CaptureFrameWorkToken token in _completed)
                    {
                        if (token.IdenticalTo(record.WorkToken))
                        {
                            evidence = NvencSourceReadCompletedEvidence.Create(this, record);
                            return true;
                        }
                    }
                }

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

        /// <summary>
        /// Both real workers over one process state and one set of pools, with
        /// both wakes bound once in the only order they can be composed in,
        /// plus a thread that can hold the shared gate on demand.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencGpuConversionSyncPool SyncSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmissionQueue;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal Guid Owner;
            internal CaptureFrameRenderTargetPool RenderPool;

            internal FakeSourceReadCompletedSource CompletionSource;
            internal NvencSourceResourceReleaseCoordinator ReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer;
            internal FakeOutputSource OutputSource;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;

            internal ManualResetEventSlim SettledEvent;
            internal ManualResetEventSlim SubmitSettledEvent;
            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim ReleaseSource;

            private readonly ManualResetEventSlim _gateHeld = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _releaseGate = new ManualResetEventSlim(false);
            private Thread _gateHolder;

            private readonly Action _settledHandler;
            private readonly Action _submitSettledHandler;
            private readonly List<NvencSubmissionRecord> _records = new List<NvencSubmissionRecord>();

            private Harness()
            {
                State = new NvencCaptureProcessState();
                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                SubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Owner = Guid.NewGuid();
                RenderPool = new CaptureFrameRenderTargetPool(
                    4, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));

                CompletionSource = new FakeSourceReadCompletedSource();
                ReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SyncSlots,
                    SubmitToOutputCredits, FrameCompletionCredits,
                    CompletionSource, new NvencSourceSurfaceReturnBoundary(), Owner);

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                NvencRunChunkSink sink = new NvencRunChunkSink(State, Buffer, Writer);
                NvencRunChunkContext context = new NvencRunChunkContext(
                    MakeIssue(), sink, new NvencRunChunkFinalizationCoordinator(Writer), "chunk/0");
                OutputSource = new FakeOutputSource();
                NvencSubmittedOutputCollector collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer, OutputSource);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer);

                // The one composition order: processor, its worker, the output
                // processor, the output worker, both wakes, then start.
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmissionQueue, OutputQueue, WorkSlots, SampleSlots,
                    ReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);
                Processor = new NvencOrderedOutputProcessor(
                    State,
                    OutputQueue,
                    collector,
                    sink,
                    new NvencFailedBeforeSubmitReleaseCoordinator(
                        State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits),
                    new NvencSubmittedOutputAbandonRecoveryCoordinator(
                        State, collector, SampleSlots, Buffer),
                    Boundary);
                Worker = new NvencOrderedOutputWorkerService(
                    State, Processor, context, SubmitWorker, new FakeTeardown());

                SettledEvent = new ManualResetEventSlim(false);
                SubmitSettledEvent = new ManualResetEventSlim(false);
                SourceEntered = new ManualResetEventSlim(false);
                ReleaseSource = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                _submitSettledHandler = () => SubmitSettledEvent.Set();
                Worker.Settled += _settledHandler;
                SubmitWorker.Settled += _submitSettledHandler;

                State.BindResourceResolutionReleaseNotification(Worker);
                SubmitWorker.Start();
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                // Park both workers on empty queues before any record exists,
                // so every later wake is one a test actually caused.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Output Worker did not park on the empty queue");

                h.SubmitSettledEvent.Reset();
                h.SubmitWorker.Notify();
                Assert.That(h.SubmitSettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Submit Worker did not park on the empty queue");

                return h;
            }

            /// <summary>
            /// Arms the output source so the Output Worker stops inside the
            /// copy, which is where the gate can be taken from under it.
            /// </summary>
            internal void HoldTheOutputCopy()
            {
                OutputSource.SourceEntered = SourceEntered;
                OutputSource.WaitForRelease = ReleaseSource;
            }

            /// <summary>
            /// Takes the shared gate on a thread of this harness's own, since a
            /// monitor can only be released by the thread that holds it, and
            /// returns once it is actually held.
            /// </summary>
            internal void HoldGateOnAnotherThread()
            {
                _gateHolder = new Thread(() =>
                {
                    if (!State.TryBeginResourceResolution())
                    {
                        return;
                    }

                    _gateHeld.Set();
                    _releaseGate.Wait(WatchdogTimeoutMs);
                    State.EndResourceResolution();
                });

                _gateHolder.IsBackground = true;
                _gateHolder.Start();
                Assert.That(_gateHeld.Wait(WatchdogTimeoutMs), Is.True,
                    "the shared gate was not taken");
            }

            /// <summary>Releases the held gate, and nothing else.</summary>
            internal void ReleaseGate()
            {
                _releaseGate.Set();
                Assert.That(_gateHolder.Join(WatchdogTimeoutMs), Is.True,
                    "the gate holder did not release the gate");
                _gateHolder = null;
            }

            internal NvencSubmissionRecord CreateSubmission(long frameId)
            {
                Assert.That(WorkSlots.TryRent(out NvencCaptureWorkSlotLease work), Is.True);
                Assert.That(SampleSlots.TryRent(out NvencEncodeSampleSlotLease sample), Is.True);
                Assert.That(SyncSlots.TryRent(out NvencGpuConversionSyncLease sync), Is.True);
                Assert.That(SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease submit), Is.True);
                Assert.That(FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease frame), Is.True);
                Assert.That(RenderPool.TryRent(out CaptureFrameRenderTargetLease rt), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Owner, work.SlotIndex, work.Generation, 1, frameId);
                CaptureSurfaceLease surface = new CaptureSurfaceLease(RenderPool, rt);
                surface.TransferToBackend(Owner, token);

                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    Owner, token, work, sample, sync, submit, frame, surface,
                    WorkSlots, SampleSlots, SyncSlots, SubmitToOutputCredits, FrameCompletionCredits);
                _records.Add(record);
                return record;
            }

            internal void EnqueueSubmission(NvencSubmissionRecord record)
            {
                Assert.That(SubmissionQueue.TryEnqueue(record), Is.True);
            }

            public void Dispose()
            {
                // Never leave a worker held inside a fake, and never leave the
                // gate held: a poisoned worker still has to be able to exit.
                ReleaseSource.Set();
                _releaseGate.Set();
                if (_gateHolder != null)
                {
                    _gateHolder.Join(WatchdogTimeoutMs);
                    _gateHolder = null;
                }

                StopWorker(
                    () => SubmitWorker.IsStopped, () => SubmitWorker.Notify(),
                    typeof(NvencOrderedSubmitWorkerService), SubmitWorker,
                    "the Submit Worker thread did not physically exit during teardown");
                StopWorker(
                    () => Worker.IsStopped, () => Worker.Notify(),
                    typeof(NvencOrderedOutputWorkerService), Worker,
                    "the Output Worker thread did not physically exit during teardown");

                // The same order the product requires: physically stopped
                // first, then the binding, then disposal.
                if (Worker.IsStopped)
                {
                    State.UnbindResourceResolutionReleaseNotification(Worker);
                }

                SubmitWorker.Dispose();
                Worker.Dispose();
                SubmitWorker.Settled -= _submitSettledHandler;
                Worker.Settled -= _settledHandler;

                while (ReleaseCoordinator.TryApplyPendingRelease())
                {
                }

                foreach (NvencSubmissionRecord record in _records)
                {
                    if (record.Surface != null && record.Surface.IsCreated)
                    {
                        if (record.Surface.IsBackendOwned)
                        {
                            record.Surface.ReleaseFromBackend(Owner, record.WorkToken);
                        }
                        else
                        {
                            record.Surface.Dispose();
                        }
                    }

                    SyncSlots.TryReturn(record.SyncSlot);
                    SampleSlots.TryReturn(record.SampleSlot);
                    WorkSlots.TryReturn(record.WorkSlot);
                    FrameCompletionCredits.TryReturn(record.FrameCompletionCredit);
                    SubmitToOutputCredits.TryReturn(record.SubmitToOutputCredit);
                }

                RenderPool.Dispose();
                _releaseGate.Dispose();
                _gateHeld.Dispose();
                ReleaseSource.Dispose();
                SourceEntered.Dispose();
                SubmitSettledEvent.Dispose();
                SettledEvent.Dispose();
            }

            private void StopWorker(
                Func<bool> isStopped, Action notify, Type workerType, object worker, string message)
            {
                if (!isStopped())
                {
                    State.TryPoison();
                    notify();
                }

                FieldInfo field = workerType.GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread workerThread = (Thread)field?.GetValue(worker);
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }
        }
    }
}
