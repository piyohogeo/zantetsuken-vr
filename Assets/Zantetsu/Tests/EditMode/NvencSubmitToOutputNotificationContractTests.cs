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
    /// Contract tests for the one connection between the Phase 0.11 Submit
    /// Worker and Output Worker: once the Submit Processor has put a record in
    /// the Submit-to-Output Queue it wakes the single Output Worker bound to
    /// it, so an Output Worker parked on an empty queue makes progress during a
    /// Run - with no drain, no timer, no polling, and no relayed settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every fixture here composes the two real workers over one shared process
    /// state and one shared set of pools, binds the notification once, and then
    /// drives only the Submit side. No test subscribes the Output Worker's
    /// <c>Notify</c> to the Submit Worker's <c>Settled</c>, and no test calls
    /// the Output Worker's <c>Notify</c> to make progress: the only wake that
    /// can carry a record across is the production one. The Output Worker's own
    /// settle is used solely as a bounded wake hint while the real condition -
    /// a collected Frame Completion - is re-checked.
    /// </para>
    /// <para>
    /// No GPU, NVENC, native resource, file system, or timing assumption is
    /// used; the output source, chunk writer, submitter, source-read completion
    /// authority, and teardown are fakes.
    /// </para>
    /// </remarks>
    public class NvencSubmitToOutputNotificationContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        /// <summary>
        /// Deliberately short deadline for a probe whose condition can never
        /// become true, so the probe's own failure is the assertion.
        /// </summary>
        private const int BoundedProbeTimeoutMs = 250;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>
        /// The whole point of the unit: a Submitted record enqueued by the
        /// Submit Worker reaches Frame Completion on a parked Output Worker,
        /// with nothing but the Submit side ever woken by this test.
        /// </summary>
        [Test]
        public void Submitted_ParkedOutputWorker_ConvergesFromTheSubmitEnqueueAlone()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = h.CreateSubmission(1);
                h.CompletionSource.MarkCompleted(record.WorkToken);
                h.EnqueueSubmission(record);

                // The only wake this test delivers, and it goes to the Submit
                // Worker. The Output Worker is parked on an empty queue.
                h.SubmitWorker.Notify();

                NvencFrameCompletionRecord completion = WaitForCompletion(
                    h, "the parked Output Worker did not complete the frame");

                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));

                // The work really did cross both workers, on their own threads.
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(
                    h.Submitter.ExecutingThreadName,
                    Is.EqualTo(NvencOrderedSubmitWorkerService.WorkerThreadName));
                Assert.That(h.OutputSource.CallCount, Is.EqualTo(1));
                Assert.That(
                    h.OutputSource.ExecutingThreadName,
                    Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));

                // And it happened inside a plain Run: nothing drained, and no
                // drain notification was involved.
                Assert.That(h.State.IsAccepting, Is.True);
                Assert.That(h.State.IsDraining, Is.False);
                Assert.That(h.SubmitWorker.DrainCompleted, Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.SubmitWorker.TryGetFailure(out Exception _), Is.False);
                Assert.That(h.Worker.TryGetFailure(out Exception _), Is.False);
            }
        }

        /// <summary>
        /// The same wake must exist on the FailedBeforeSubmit path: a record
        /// that never reached the encoder is still enqueued, and the parked
        /// Output Worker must converge it without being told anything else.
        /// </summary>
        [Test]
        public void FailedBeforeSubmit_ParkedOutputWorker_ConvergesFromTheSubmitEnqueueAlone()
        {
            using (Harness h = Harness.Create())
            {
                // A controlled submit refusal, which is not fatal: the Submit
                // Processor emits FailedBeforeSubmit instead of Submitted.
                h.Submitter.Result = false;

                NvencSubmissionRecord record = h.CreateSubmission(1);
                h.CompletionSource.MarkCompleted(record.WorkToken);
                h.EnqueueSubmission(record);

                h.SubmitWorker.Notify();

                NvencFrameCompletionRecord completion = WaitForCompletion(
                    h, "the parked Output Worker did not converge the FailedBeforeSubmit record");

                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
                Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.NvencSubmitFailed));

                // Nothing was collected from the encoder for a record that was
                // never submitted, and the Run was abandoned by the Output side.
                Assert.That(h.OutputSource.CallCount, Is.EqualTo(0));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
                Assert.That(h.State.IsRunAbandoned, Is.True);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.SubmitWorker.TryGetFailure(out Exception _), Is.False);
                Assert.That(h.Worker.TryGetFailure(out Exception _), Is.False);
            }
        }

        /// <summary>
        /// The Run Abandoned path is the second place a record reaches the
        /// queue, and it must wake the Output Worker exactly like the ordinary
        /// one: a cancelled record that was never submitted still converges on
        /// a parked Output Worker.
        /// </summary>
        [Test]
        public void CancelledAfterRunAbandoned_ParkedOutputWorker_ConvergesFromTheSubmitEnqueueAlone()
        {
            using (Harness h = Harness.Create())
            {
                // Hold the work inside the Submit Processor first, with its
                // source read still incomplete, so the Run can be abandoned
                // before the record is ever eligible for the encoder.
                NvencSubmissionRecord record = h.CreateSubmission(1);

                h.SubmitSettledEvent.Reset();
                h.EnqueueSubmission(record);
                h.SubmitWorker.Notify();
                Assert.That(h.SubmitSettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Submit Worker did not take the record");
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));

                Assert.That(h.State.TryBeginRunAbandoned(), Is.True);

                h.CompletionSource.MarkCompleted(record.WorkToken);
                h.SubmitWorker.Notify();

                NvencFrameCompletionRecord completion = WaitForCompletion(
                    h, "the parked Output Worker did not converge the cancelled record");

                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
                Assert.That(
                    completion.Reason,
                    Is.EqualTo(NvencFrameCompletionReason.CancelledAfterRunAbandoned));

                // Work that was abandoned before the encoder never reached it.
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
                Assert.That(h.OutputSource.CallCount, Is.EqualTo(0));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.SubmitWorker.TryGetFailure(out Exception _), Is.False);
                Assert.That(h.Worker.TryGetFailure(out Exception _), Is.False);
            }
        }

        /// <summary>
        /// The record is in the queue before the Output Worker is told about
        /// it, and the wake changes no order: what was enqueued first completes
        /// first.
        /// </summary>
        /// <remarks>
        /// The Output Worker is held inside the first record's source copy, so
        /// it provably cannot dequeue anything while the second record is
        /// submitted. What the queue holds once the Submit Worker has finished
        /// that record is therefore exactly what it held when the wake was
        /// delivered.
        /// </remarks>
        [Test]
        public void Notification_FollowsTheEnqueue_AndLeavesFifoOrderUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                h.OutputSource.SourceEntered = h.SourceEntered;
                h.OutputSource.WaitForRelease = h.ReleaseSource;

                NvencSubmissionRecord first = h.CreateSubmission(1);
                h.CompletionSource.MarkCompleted(first.WorkToken);

                h.SubmitSettledEvent.Reset();
                h.EnqueueSubmission(first);
                h.SubmitWorker.Notify();
                Assert.That(h.SubmitSettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Submit Worker did not finish the first record");

                Assert.That(h.SourceEntered.Wait(WatchdogTimeoutMs), Is.True,
                    "the Output Worker was not woken by the first enqueue");

                // Held consumer, empty queue: the first record was taken.
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));

                NvencSubmissionRecord second = h.CreateSubmission(2);
                h.CompletionSource.MarkCompleted(second.WorkToken);

                h.SubmitSettledEvent.Reset();
                h.EnqueueSubmission(second);
                h.SubmitWorker.Notify();
                Assert.That(h.SubmitSettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Submit Worker did not finish the second record");

                // The Submit Worker has already enqueued and notified, and the
                // Output Worker cannot have consumed anything, so this is the
                // queue exactly as the wake found it.
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1),
                    "the record must already be in the queue when the Output Worker is told about it");

                h.ReleaseSource.Set();

                NvencFrameCompletionRecord firstCompletion = WaitForCompletion(
                    h, "the first record did not complete");
                NvencFrameCompletionRecord secondCompletion = WaitForCompletion(
                    h, "the second record did not complete");

                Assert.That(firstCompletion.WorkToken.IdenticalTo(first.WorkToken), Is.True);
                Assert.That(secondCompletion.WorkToken.IdenticalTo(second.WorkToken), Is.True);
                Assert.That(firstCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(secondCompletion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.State.IsAccepting, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        /// <summary>
        /// A Submit Worker pass that enqueues nothing tells the Output Worker
        /// nothing: waiting for the source read to complete is not a wake.
        /// </summary>
        [Test]
        public void NoEnqueue_LeavesTheParkedOutputWorkerUntouched()
        {
            using (Harness h = Harness.Create())
            {
                // The source read is not completed, so the release handoff is
                // refused and the Submit Processor holds the current work with
                // no side effect at all.
                NvencSubmissionRecord record = h.CreateSubmission(1);

                h.SettledEvent.Reset();
                h.SubmitSettledEvent.Reset();
                h.EnqueueSubmission(record);
                h.SubmitWorker.Notify();
                Assert.That(h.SubmitSettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "the Submit Worker did not run");

                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));

                // A wake would bring the Output Worker back around to a settle;
                // this probe's condition can never become true.
                Assert.That(h.SettledEvent.Wait(BoundedProbeTimeoutMs), Is.False,
                    "the Output Worker must not be woken when nothing was enqueued");
                Assert.That(h.OutputSource.CallCount, Is.EqualTo(0));

                // The probe was not vacuous: the same held record converges as
                // soon as the source read completes and the Submit Worker runs
                // again - still with nothing said to the Output Worker here.
                h.CompletionSource.MarkCompleted(record.WorkToken);
                h.SubmitWorker.Notify();

                NvencFrameCompletionRecord completion = WaitForCompletion(
                    h, "the held record did not converge once its source read completed");

                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(h.OutputSource.CallCount, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// The connection is made once at composition and can never be
        /// re-pointed: there is no replacement, no removal, and no registry.
        /// </summary>
        [Test]
        public void Binding_IsMadeExactlyOnce()
        {
            using (Harness h = Harness.Create())
            {
                // The harness already bound this processor to its one Output
                // Worker, exactly as a composition root would.
                Assert.Throws<ArgumentNullException>(
                    () => h.SubmitProcessor.BindOutputWorkerNotification(null));
                Assert.Throws<InvalidOperationException>(
                    () => h.SubmitProcessor.BindOutputWorkerNotification(h.Worker));
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Confirms the real condition - a collected Frame Completion - inside a
        /// watchdog. The Output Worker's settle is only a wake hint and is
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
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity =
                CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence =
                CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(
            CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents =
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch =
                new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(
                    new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
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

            internal int AppendCount => Volatile.Read(ref _appendCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                Interlocked.Increment(ref _appendCount);
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(
                NvencRunChunkFinalizationOperation operation)
            {
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        /// <summary>
        /// Records which thread collected the output and, when armed, holds the
        /// Output Worker inside the copy so the test can observe the queue while
        /// the consumer provably cannot touch it.
        /// </summary>
        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            private int _callCount;

            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim WaitForRelease;

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
            private int _submitCount;

            internal bool Result = true;

            internal int SubmitCount => Volatile.Read(ref _submitCount);

            internal string ExecutingThreadName;

            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                Interlocked.Increment(ref _submitCount);
                ExecutingThreadName = Thread.CurrentThread.Name;
                return Result;
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
        /// Both real workers over one process state and one set of pools, in
        /// the only order they can be composed in: the Submit Processor, the
        /// Submit Worker that owns it, the Output Processor, the Output Worker
        /// that needs the Submit Worker, and then the one notification binding.
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
            internal NvencSourceSurfaceReturnBoundary ReturnBoundary;
            internal NvencSourceResourceReleaseCoordinator ReleaseCoordinator;
            internal FakeSubmitter Submitter;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer;
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator Finalization;
            internal NvencRunChunkContext Context;
            internal FakeOutputSource OutputSource;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencFailedBeforeSubmitReleaseCoordinator FailedReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencOrderedOutputProcessor Processor;
            internal FakeTeardown Teardown;
            internal NvencOrderedOutputWorkerService Worker;

            internal ManualResetEventSlim SettledEvent;
            internal ManualResetEventSlim SubmitSettledEvent;
            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim ReleaseSource;

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

                // Submit side.
                CompletionSource = new FakeSourceReadCompletedSource();
                ReturnBoundary = new NvencSourceSurfaceReturnBoundary();
                ReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SyncSlots,
                    SubmitToOutputCredits, FrameCompletionCredits,
                    CompletionSource, ReturnBoundary, Owner);
                Submitter = new FakeSubmitter();
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmissionQueue, OutputQueue,
                    WorkSlots, SampleSlots, ReleaseCoordinator, Submitter);
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                // Output side. One shared buffer, writer, and sink feed both the
                // processor and the exact Run chunk context.
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                Finalization = new NvencRunChunkFinalizationCoordinator(Writer);
                Context = new NvencRunChunkContext(MakeIssue(), Sink, Finalization, "chunk/0");
                OutputSource = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer, OutputSource);
                FailedReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, FailedReleaseCoordinator,
                    RecoveryCoordinator, Boundary);
                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                SettledEvent = new ManualResetEventSlim(false);
                SubmitSettledEvent = new ManualResetEventSlim(false);
                SourceEntered = new ManualResetEventSlim(false);
                ReleaseSource = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                _submitSettledHandler = () => SubmitSettledEvent.Set();
                Worker.Settled += _settledHandler;
                SubmitWorker.Settled += _submitSettledHandler;

                // The one connection under test, made exactly where a
                // composition root can make it: after the Output Worker exists
                // and before the Submit Worker is started.
                SubmitProcessor.BindOutputWorkerNotification(Worker);
                SubmitWorker.Start();
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                // Park both workers on empty queues before any record exists, so
                // every later wake is one the fixture actually caused. This is
                // the only Output Worker notification in the whole file, and it
                // cannot carry a record: there is none yet.
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
                // Never leave a worker held inside a fake.
                ReleaseSource.Set();

                StopWorker(
                    () => SubmitWorker.IsStopped,
                    () => SubmitWorker.Notify(),
                    typeof(NvencOrderedSubmitWorkerService),
                    SubmitWorker,
                    "the Submit Worker thread did not physically exit during teardown");
                StopWorker(
                    () => Worker.IsStopped,
                    () => Worker.Notify(),
                    typeof(NvencOrderedOutputWorkerService),
                    Worker,
                    "the Output Worker thread did not physically exit during teardown");

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
                ReleaseSource.Dispose();
                SourceEntered.Dispose();
                SubmitSettledEvent.Dispose();
                SettledEvent.Dispose();
            }

            private void StopWorker(
                Func<bool> isStopped,
                Action notify,
                Type workerType,
                object worker,
                string message)
            {
                if (!isStopped())
                {
                    // Guarantee a stop condition exists and wake the worker so
                    // it can exit; this is teardown, never a test observation.
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
