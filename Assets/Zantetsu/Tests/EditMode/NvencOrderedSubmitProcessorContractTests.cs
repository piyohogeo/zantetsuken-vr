using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 ordered submit processor and its
    /// encode-picture submit operation. Uses tiny render target fixtures; no
    /// real GPU, NVENC, worker, fence, query, command buffer, or native resource
    /// is used.
    /// </summary>
    public class NvencOrderedSubmitProcessorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        [Test]
        public void Process_FifoOrder_IsPreserved()
        {
            using (Harness h = Harness.Create(4))
            {
                for (int i = 1; i <= 4; i++)
                {
                    NvencSubmissionRecord record = h.CreateRecord(i);
                    h.Source.MarkCompleted(record.WorkToken);
                    h.Enqueue(record);
                }

                for (int i = 0; i < 4; i++)
                {
                    Assert.That(h.Processor.TryProcessNext(), Is.True);
                }

                for (int i = 1; i <= 4; i++)
                {
                    Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                    Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(i));
                }

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord empty), Is.False);
            }
        }

        [Test]
        public void Process_DoesNotOvertakeUncompletedFirst()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);

                // Only the second work is GPU-ready first.
                h.Source.MarkCompleted(second.WorkToken);

                h.Enqueue(first);
                h.Enqueue(second);

                // First work is not completed: held, no overtake, no submit.
                Assert.That(h.Processor.TryProcessNext(), Is.False);
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));

                // Complete the first; it is processed before the second.
                h.Source.MarkCompleted(first.WorkToken);
                Assert.That(h.Processor.TryProcessNext(), Is.True);
                Assert.That(h.Processor.TryProcessNext(), Is.True);

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord firstOut), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord secondOut), Is.True);
                Assert.That(firstOut.WorkToken.CaptureFrameId, Is.EqualTo(1));
                Assert.That(secondOut.WorkToken.CaptureFrameId, Is.EqualTo(2));
            }
        }

        [Test]
        public void Process_NotCompleted_TouchesNeitherSubmitterNorOutput()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Enqueue(record);

                Assert.That(h.Processor.TryProcessNext(), Is.False);

                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.False);
                Assert.That(h.Processor.HasCurrentWork, Is.True);
            }
        }

        [Test]
        public void Process_SubmitSuccess_ExactlyOneSubmittedRecord()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                Assert.That(h.Processor.TryProcessNext(), Is.True);

                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                Assert.That(output.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.None));
                Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(7));
                Assert.That(output.SubmitToOutputCredit.IsValid, Is.True);
                Assert.That(output.FrameCompletionCredit.IsValid, Is.True);
                Assert.That(output.IsValidFor(h.WorkPool, h.SamplePool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord empty), Is.False);

                // Work and Sample remain active for the later output stages.
                Assert.That(h.WorkPool.IsActive(record.WorkSlot), Is.True);
                Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.True);
            }
        }

        [Test]
        public void Process_ControlledFailure_ExactlyOneFailedRecord()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);
                h.Submitter.SetResult(false);

                Assert.That(h.Processor.TryProcessNext(), Is.True);

                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.FailedBeforeSubmit));
                Assert.That(output.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.NvencSubmitFailed));
                Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(7));
                Assert.That(output.SubmitToOutputCredit.IsValid, Is.True);
                Assert.That(output.FrameCompletionCredit.IsValid, Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord empty), Is.False);
            }
        }

        [Test]
        public void Process_SubmitterThrows_PoisonsNoRecordNoRetry()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                InvalidOperationException failure = new InvalidOperationException("submit boom");
                h.Submitter.Throw(failure);

                Assert.Throws<InvalidOperationException>(() => h.Processor.TryProcessNext());

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.False);

                // Current work and all leases are retained; nothing speculative.
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.True);
                Assert.That(h.WorkPool.IsActive(record.WorkSlot), Is.True);
                Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.True);
            }
        }

        [Test]
        public void Operation_StaysValidAfterSurfaceAndSyncReleased()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                NvencEncodePictureSubmitOperation operation =
                    NvencEncodePictureSubmitOperation.Create(record, h.WorkPool, h.SamplePool);

                Assert.That(operation.IsValidFor(h.WorkPool, h.SamplePool), Is.True);

                // Release the surface and sync on the Main Thread.
                h.Source.MarkCompleted(record.WorkToken);
                Assert.That(h.ReleaseCoordinator.TryReleaseSourceResources(record), Is.True);
                Assert.That(h.ReleaseCoordinator.TryApplyPendingRelease(), Is.True);

                Assert.That(record.Surface.IsCreated, Is.False);
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.False);

                // The submit operation is still valid for Work and Sample.
                Assert.That(operation.IsValidFor(h.WorkPool, h.SamplePool), Is.True);
            }
        }

        [Test]
        public void Process_OutputQueueFull_HoldsCurrentNoSideEffect()
        {
            using (Harness h = Harness.Create(1))
            {
                for (int i = 0; i < h.OutputQueue.Capacity; i++)
                {
                    Assert.That(h.OutputQueue.TryEnqueue(default), Is.True);
                }

                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                Assert.That(h.Processor.TryProcessNext(), Is.False);

                // No evidence observation, handoff, or submit occurred.
                Assert.That(h.Source.ObserveCount, Is.EqualTo(0));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.True);

                // The current is processed once the output queue has space.
                for (int i = 0; i < h.OutputQueue.Capacity; i++)
                {
                    Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord placeholder), Is.True);
                }

                Assert.That(h.Processor.TryProcessNext(), Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Process_Draining_StillProcessesAcceptedWork()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                Assert.That(h.State.TryBeginDrain(), Is.True);

                Assert.That(h.Processor.TryProcessNext(), Is.True);

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(7));
            }
        }

        [Test]
        public void Process_Poisoned_ChangesNothing()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(second.WorkToken);

                h.Enqueue(first);
                h.Enqueue(second);

                // First work is not completed, so it is dequeued and held.
                Assert.That(h.Processor.TryProcessNext(), Is.False);
                Assert.That(h.Processor.HasCurrentWork, Is.True);

                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.Processor.TryProcessNext(), Is.False);

                // Current, leases, and queues are all unchanged.
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(first.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(first.SyncSlot), Is.True);
                Assert.That(h.WorkPool.IsActive(first.WorkSlot), Is.True);
                Assert.That(h.SamplePool.IsActive(first.SampleSlot), Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));

                // The second work is still in the submission queue, untouched.
                Assert.That(h.SubmissionQueue.TryDequeue(out NvencSubmissionRecord dequeued), Is.True);
                Assert.That(dequeued.WorkToken.IdenticalTo(second.WorkToken), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.False);
            }
        }

        [Test]
        public void Process_SubmitStepLinearizesBeforePoison()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);
                h.Submitter.BlockInSubmit();

                bool processed = false;
                Thread processorThread = new Thread(() =>
                {
                    processed = h.Processor.TryProcessNext();
                });
                processorThread.IsBackground = true;
                processorThread.Start();

                // Wait until the submitter is inside TrySubmit, holding the gate.
                Assert.That(h.Submitter.Entered.Wait(WatchdogTimeoutMs), Is.True, "Submitter did not enter in time.");

                using (ManualResetEventSlim poisonStarted = new ManualResetEventSlim(false))
                {
                    Thread poisonThread = new Thread(() =>
                    {
                        poisonStarted.Set();
                        h.State.TryPoison();
                    });
                    poisonThread.IsBackground = true;
                    poisonThread.Start();

                    Assert.That(poisonStarted.Wait(WatchdogTimeoutMs), Is.True, "Poison thread did not start in time.");

                    // The submit step holds the gate, so poison cannot complete
                    // until submit and output enqueue finish.
                    Assert.That(h.State.IsPoisoned, Is.False);

                    h.Submitter.ReleaseSubmit.Set();

                    Assert.That(processorThread.Join(WatchdogTimeoutMs), Is.True, "Processor did not finish in time.");
                    Assert.That(poisonThread.Join(WatchdogTimeoutMs), Is.True, "Poison did not finish in time.");
                }

                Assert.That(processed, Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Process_PoisonedBeforeAnyWork_TouchesNothing()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.Processor.TryProcessNext(), Is.False);

                Assert.That(h.Source.ObserveCount, Is.EqualTo(0));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.True);
                Assert.That(h.WorkPool.IsActive(record.WorkSlot), Is.True);
                Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.True);
            }
        }

        [Test]
        public void Process_EightInOrder_OneRecordEach()
        {
            using (Harness h = Harness.Create(8))
            {
                NvencSubmissionRecord[] records = new NvencSubmissionRecord[8];
                for (int i = 0; i < 8; i++)
                {
                    records[i] = h.CreateRecord(i + 1);
                    h.Source.MarkCompleted(records[i].WorkToken);
                    h.Enqueue(records[i]);
                }

                for (int i = 0; i < 8; i++)
                {
                    Assert.That(h.Processor.TryProcessNext(), Is.True);
                }

                for (int i = 1; i <= 8; i++)
                {
                    Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                    Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(i));
                    Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                }

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord empty), Is.False);

                // Work and Sample stay active for the later output stages.
                Assert.That(h.WorkPool.OccupiedCount, Is.EqualTo(8));
                Assert.That(h.SamplePool.OccupiedCount, Is.EqualTo(8));
            }
        }

        [Test]
        public void Operation_HoldsOnlyThreeFields_NoSurfaceSyncEvidence()
        {
            System.Reflection.FieldInfo[] fields = typeof(NvencEncodePictureSubmitOperation).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(3));

            Type[] expected =
            {
                typeof(CaptureFrameWorkToken),
                typeof(NvencCaptureWorkSlotLease),
                typeof(NvencEncodeSampleSlotLease),
            };

            foreach (System.Reflection.FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True);
                Assert.That(expected, Does.Contain(field.FieldType));
            }
        }

        [Test]
        public void Operation_Create_RejectsStaleOrInactiveLeases()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);

                // Return the work lease: the record is no longer valid for the pool.
                Assert.That(h.WorkPool.TryReturn(record.WorkSlot), Is.True);
                Assert.Throws<ArgumentException>(() =>
                    NvencEncodePictureSubmitOperation.Create(record, h.WorkPool, h.SamplePool));
            }
        }

        [Test]
        public void Processor_Source_NoSortScanPeekNoThread()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitProcessor.cs"));

            Assert.That(source, Does.Not.Contain("Array.Sort"));
            Assert.That(source, Does.Not.Contain("OrderBy"));
            Assert.That(source, Does.Not.Contain("Reverse"));
            Assert.That(source, Does.Not.Contain("Peek"));
            Assert.That(source, Does.Not.Contain("Enumerable"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain(".ToList("));
            Assert.That(source, Does.Not.Contain(".ToArray("));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("new Thread"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("SpinWait"));
            Assert.That(source, Does.Not.Contain("while ("));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("NvEnc"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
        }

        private static CaptureFrameRenderTargetPool MakeRenderPool(int capacity)
        {
            return new CaptureFrameRenderTargetPool(
                capacity, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            private readonly List<CaptureFrameWorkToken> _completed = new List<CaptureFrameWorkToken>();

            internal int ObserveCount { get; private set; }

            internal void MarkCompleted(in CaptureFrameWorkToken token)
            {
                _completed.Add(token);
            }

            public bool TryGetEvidence(
                in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                ObserveCount++;
                foreach (CaptureFrameWorkToken token in _completed)
                {
                    if (token.IdenticalTo(record.WorkToken))
                    {
                        evidence = NvencSourceReadCompletedEvidence.Create(this, record);
                        return true;
                    }
                }

                evidence = default;
                return false;
            }
        }

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter
        {
            private bool _result = true;
            private Exception _exception;
            private bool _block;
            private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

            internal int SubmitCount { get; private set; }

            internal NvencEncodePictureSubmitOperation LastOperation { get; private set; }

            internal ManualResetEventSlim Entered => _entered;

            internal ManualResetEventSlim ReleaseSubmit => _release;

            internal void SetResult(bool result)
            {
                _result = result;
            }

            internal void Throw(Exception exception)
            {
                _exception = exception;
            }

            internal void BlockInSubmit()
            {
                _block = true;
            }

            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                SubmitCount++;
                LastOperation = operation;
                if (_exception != null)
                {
                    throw _exception;
                }

                if (_block)
                {
                    _entered.Set();
                    _release.Wait(WatchdogTimeoutMs);
                }

                return _result;
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State { get; }
            internal NvencCaptureWorkSlotPool WorkPool { get; }
            internal NvencEncodeSampleSlotPool SamplePool { get; }
            internal NvencGpuConversionSyncPool SyncPool { get; }
            internal NvencSubmitToOutputCreditPool SubmitToOutputCreditPool { get; }
            internal NvencFrameCompletionCreditPool FrameCompletionCreditPool { get; }
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmissionQueue { get; }
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue { get; }
            internal Guid Owner { get; }
            internal CaptureFrameRenderTargetPool RenderPool { get; }
            internal FakeSourceReadCompletedSource Source { get; }
            internal NvencSourceSurfaceReturnBoundary Boundary { get; }
            internal NvencSourceResourceReleaseCoordinator ReleaseCoordinator { get; }
            internal FakeSubmitter Submitter { get; }
            internal NvencOrderedSubmitProcessor Processor { get; }

            private readonly List<NvencSubmissionRecord> _records = new List<NvencSubmissionRecord>();

            private Harness(int renderCapacity)
            {
                State = new NvencCaptureProcessState();
                WorkPool = new NvencCaptureWorkSlotPool(State);
                SamplePool = new NvencEncodeSampleSlotPool(State);
                SyncPool = new NvencGpuConversionSyncPool(State);
                SubmitToOutputCreditPool = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCreditPool = new NvencFrameCompletionCreditPool(State);
                SubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Owner = Guid.NewGuid();
                RenderPool = MakeRenderPool(renderCapacity);
                Source = new FakeSourceReadCompletedSource();
                Boundary = new NvencSourceSurfaceReturnBoundary();
                ReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkPool, SamplePool, SyncPool,
                    SubmitToOutputCreditPool, FrameCompletionCreditPool,
                    Source, Boundary, Owner);
                Submitter = new FakeSubmitter();
                Processor = new NvencOrderedSubmitProcessor(
                    State, SubmissionQueue, OutputQueue, WorkPool, SamplePool, ReleaseCoordinator, Submitter);
            }

            internal static Harness Create(int renderCapacity)
            {
                return new Harness(renderCapacity);
            }

            internal NvencSubmissionRecord CreateRecord(long frameId)
            {
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease work), Is.True);
                Assert.That(SamplePool.TryRent(out NvencEncodeSampleSlotLease sample), Is.True);
                Assert.That(SyncPool.TryRent(out NvencGpuConversionSyncLease sync), Is.True);
                Assert.That(SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease submitToOutput), Is.True);
                Assert.That(FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease frameCompletion), Is.True);
                Assert.That(RenderPool.TryRent(out CaptureFrameRenderTargetLease rt), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Owner, work.SlotIndex, work.Generation, 1, frameId);
                CaptureSurfaceLease surface = new CaptureSurfaceLease(RenderPool, rt);
                surface.TransferToBackend(Owner, token);

                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    Owner, token, work, sample, sync, submitToOutput, frameCompletion, surface,
                    WorkPool, SamplePool, SyncPool, SubmitToOutputCreditPool, FrameCompletionCreditPool);
                _records.Add(record);
                return record;
            }

            internal void Enqueue(NvencSubmissionRecord record)
            {
                Assert.That(SubmissionQueue.TryEnqueue(record), Is.True);
            }

            public void Dispose()
            {
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

                    SyncPool.TryReturn(record.SyncSlot);
                    SamplePool.TryReturn(record.SampleSlot);
                    WorkPool.TryReturn(record.WorkSlot);
                    FrameCompletionCreditPool.TryReturn(record.FrameCompletionCredit);
                    SubmitToOutputCreditPool.TryReturn(record.SubmitToOutputCredit);
                }

                RenderPool.Dispose();
            }
        }
    }
}
