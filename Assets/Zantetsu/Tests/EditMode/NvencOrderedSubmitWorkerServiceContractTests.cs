using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 dedicated Submit Worker service and
    /// its notification/drain boundary. Uses tiny render target fixtures and
    /// fake collaborators; no real GPU, NVENC, native resource, or timing
    /// assumption is used.
    /// </summary>
    public class NvencOrderedSubmitWorkerServiceContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        [Test]
        public void Process_Notify_WorkerProcessesEnqueuedRecord()
        {
            using (Harness h = Harness.Create(1))
            {
                h.Worker.Start();

                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not emit output");

                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(7));
            }
        }

        [Test]
        public void Process_MultipleRecords_AcceptedOrder_NoSlotSort()
        {
            using (Harness h = Harness.Create(3))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);   // work slot 0
                NvencSubmissionRecord second = h.CreateRecord(2);  // work slot 1
                NvencSubmissionRecord third = h.CreateRecord(3);   // work slot 2
                h.Source.MarkCompleted(first.WorkToken);
                h.Source.MarkCompleted(second.WorkToken);
                h.Source.MarkCompleted(third.WorkToken);

                // Enqueue in reverse slot order; FIFO must preserve enqueue
                // order and never sort by work slot index or generation.
                h.Enqueue(third);
                h.Enqueue(second);
                h.Enqueue(first);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not emit three outputs");

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord a), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord b), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord c), Is.True);

                Assert.That(a.WorkToken.CaptureFrameId, Is.EqualTo(3));
                Assert.That(b.WorkToken.CaptureFrameId, Is.EqualTo(2));
                Assert.That(c.WorkToken.CaptureFrameId, Is.EqualTo(1));
                Assert.That(a.WorkSlot.SlotIndex, Is.EqualTo(2));
                Assert.That(c.WorkSlot.SlotIndex, Is.EqualTo(0));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(3));
            }
        }

        [Test]
        public void Process_HeadIncomplete_HoldsAndDoesNotOvertake()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(second.WorkToken); // only the second is ready

                h.Enqueue(first);
                h.Enqueue(second);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                // The worker dequeues the head, observes its incomplete evidence,
                // and holds it without touching the second work.
                WaitSettled(h.SettledEvent, "worker did not observe head evidence");
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void Process_EvidenceCompletes_ResumesHeldWork()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(second.WorkToken);

                h.Enqueue(first);
                h.Enqueue(second);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not observe head evidence");

                // Complete the held head and notify; the worker resumes from the
                // same held work, then proceeds to the second in order.
                h.Source.MarkCompleted(first.WorkToken);
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not resume the held work");

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord a), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord b), Is.True);
                Assert.That(a.WorkToken.CaptureFrameId, Is.EqualTo(1));
                Assert.That(b.WorkToken.CaptureFrameId, Is.EqualTo(2));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Process_OutputQueueFull_NoSubmit_ResumesAfterCapacityFreed()
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

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                // The worker dequeues and holds the current work because the
                // output queue is full, submitting nothing.
                WaitSettled(h.SettledEvent, "worker did not hold the current work");
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));

                // Free the output capacity and notify; the worker resumes.
                for (int i = 0; i < h.OutputQueue.Capacity; i++)
                {
                    Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord placeholder), Is.True);
                }

                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not resume after capacity was freed");
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(7));
            }
        }

        [Test]
        public void Process_ControlledFalse_OneFailedRecord_Continues_NotPoisoned()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(first.WorkToken);
                h.Source.MarkCompleted(second.WorkToken);
                h.Submitter.SetResult(false);

                h.Enqueue(first);
                h.Enqueue(second);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not emit two failed records");

                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(2));

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord a), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord b), Is.True);
                Assert.That(a.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.FailedBeforeSubmit));
                Assert.That(a.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.NvencSubmitFailed));
                Assert.That(a.WorkToken.CaptureFrameId, Is.EqualTo(1));
                Assert.That(b.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.FailedBeforeSubmit));
                Assert.That(b.WorkToken.CaptureFrameId, Is.EqualTo(2));
            }
        }

        [Test]
        public void Process_CreditsForwardedExactly_SubmittedAndFailed()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord submitted = h.CreateRecord(1);
                NvencSubmissionRecord failed = h.CreateRecord(2);
                h.Source.MarkCompleted(submitted.WorkToken);
                h.Source.MarkCompleted(failed.WorkToken);

                // First submit succeeds, second is a controlled failure.
                h.Submitter.EnqueueResult(true);
                h.Submitter.EnqueueResult(false);

                h.Enqueue(submitted);
                h.Enqueue(failed);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not emit two outputs");

                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord a), Is.True);
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord b), Is.True);

                Assert.That(a.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                Assert.That(a.SubmitToOutputCredit.SlotIndex, Is.EqualTo(submitted.SubmitToOutputCredit.SlotIndex));
                Assert.That(a.SubmitToOutputCredit.Generation, Is.EqualTo(submitted.SubmitToOutputCredit.Generation));
                Assert.That(a.FrameCompletionCredit.SlotIndex, Is.EqualTo(submitted.FrameCompletionCredit.SlotIndex));
                Assert.That(a.FrameCompletionCredit.Generation, Is.EqualTo(submitted.FrameCompletionCredit.Generation));

                Assert.That(b.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.FailedBeforeSubmit));
                Assert.That(b.SubmitToOutputCredit.SlotIndex, Is.EqualTo(failed.SubmitToOutputCredit.SlotIndex));
                Assert.That(b.SubmitToOutputCredit.Generation, Is.EqualTo(failed.SubmitToOutputCredit.Generation));
                Assert.That(b.FrameCompletionCredit.SlotIndex, Is.EqualTo(failed.FrameCompletionCredit.SlotIndex));
                Assert.That(b.FrameCompletionCredit.Generation, Is.EqualTo(failed.FrameCompletionCredit.Generation));
            }
        }

        [Test]
        public void Notify_BeforeWorkerParks_IsNotLost()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(first.WorkToken);
                h.Source.MarkCompleted(second.WorkToken);
                h.Submitter.BlockInSubmit();

                h.Enqueue(first);
                h.Enqueue(second);

                h.Worker.Start();
                h.Worker.Notify();

                // The worker is inside the first submit (deterministic gate).
                Assert.That(h.Submitter.Entered.Wait(WatchdogTimeoutMs), Is.True, "worker did not enter submit");

                // A notification issued while the worker is busy must not be lost.
                h.SettledEvent.Reset();
                h.Worker.Notify();

                h.Submitter.ReleaseSubmit.Set();

                WaitSettled(h.SettledEvent, "early notification was lost");
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Notify_Coalesces_NoDuplicateProcessing()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();
                h.Worker.Notify();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not submit");

                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
                Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(7));
            }
        }

        [Test]
        public void Source_WorkerLoop_BlocksOnSingleSignal_NoPolling()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));

            Assert.That(CountOccurrences(source, "_signal.Wait()"), Is.EqualTo(1),
                "The worker must have exactly one blocking wait.");
            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("Timer"));
            Assert.That(source, Does.Not.Contain("SpinWait"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
        }

        [Test]
        public void Source_WorkerLoop_ResetRechecksBeforeWait()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));

            int reset = source.IndexOf("_signal.Reset()", StringComparison.Ordinal);
            int wait = source.IndexOf("_signal.Wait()", StringComparison.Ordinal);
            Assert.That(reset, Is.GreaterThanOrEqualTo(0), "Missing signal reset.");
            Assert.That(wait, Is.GreaterThanOrEqualTo(0), "Missing signal wait.");

            // A progress re-check between Reset and Wait is what prevents a
            // notification arriving just before the wait from being lost.
            Assert.That(reset, Is.LessThan(wait), "Reset must precede Wait.");
            string between = source.Substring(reset, wait - reset);
            Assert.That(between, Does.Contain("TryProcessNextSafely"),
                "The worker must re-check for progress between Reset and Wait.");
        }

        [Test]
        public void Source_Notify_DoesNotBlock()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));
            string body = ExtractMethodBody(source, "Notify");

            Assert.That(body, Does.Not.Contain("Wait"));
            Assert.That(body, Does.Not.Contain("Monitor"));
            Assert.That(body, Does.Not.Contain("Sleep"));
            Assert.That(body, Does.Not.Contain("Join"));
        }

        [Test]
        public void Source_ExactlyOneThread_NoTaskNoThreadPool_NoQueueNoLinq()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));

            Assert.That(CountOccurrences(source, "new Thread("), Is.EqualTo(1),
                "The service must create exactly one worker thread.");
            Assert.That(source, Does.Contain("IsBackground = true"));
            Assert.That(source, Does.Contain("Name = WorkerThreadName"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new Queue"));
            Assert.That(source, Does.Not.Contain("OrderBy"));
            Assert.That(source, Does.Not.Contain("Array.Sort"));
            Assert.That(source, Does.Not.Contain("Peek"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
        }

        [Test]
        public void Source_IsStopped_PhysicalNonWaiting_NotFlagBased()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));

            // Physical stop is derived from the live thread, never from a flag
            // the worker sets before its stop notification finishes.
            Assert.That(source, Does.Contain(".IsAlive"));
            Assert.That(source, Does.Not.Contain("_workerStopped"));
        }

        [Test]
        public void Source_Settled_NoPerParkAllocation()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));

            // The park/stop notification path must not materialize an invocation
            // list or any managed array on the hot path.
            Assert.That(source, Does.Not.Contain("GetInvocationList"));
            Assert.That(source, Does.Not.Contain("Delegate[]"));
        }

        [Test]
        public void Source_Start_AtomicLifecycle_NoThreadExposure()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedSubmitWorkerService.cs"));

            // Startup right is claimed by a single caller via a CAS flip, so
            // exactly one worker thread can ever be created and started.
            Assert.That(source, Does.Contain("Interlocked.CompareExchange(ref _lifecycleState, StateStarting, StateNotStarted)"));
            Assert.That(source, Does.Contain("StateRunning"));
            Assert.That(source, Does.Contain("StateDisposed"));

            // The owned thread is never exposed; only the non-waiting
            // IsStopped check is public surface.
            Assert.That(source, Does.Not.Contain("internal Thread WorkerThread"));
            Assert.That(source, Does.Not.Contain("WorkerThread =>"));
        }

        [Test]
        public void Drain_EmptyState_Stops()
        {
            using (Harness h = Harness.Create(1))
            {
                h.Worker.Start();

                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);

                WaitSettled(h.SettledEvent, "worker did not stop after an empty drain");
                Assert.That(h.Worker.DrainCompleted, Is.True);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void Drain_WithPendingRecords_ProcessesAllThenStops()
        {
            using (Harness h = Harness.Create(3))
            {
                for (int i = 1; i <= 3; i++)
                {
                    NvencSubmissionRecord record = h.CreateRecord(i);
                    h.Source.MarkCompleted(record.WorkToken);
                    h.Enqueue(record);
                }

                h.Worker.Start();
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);

                WaitSettled(h.SettledEvent, "worker did not stop after draining pending records");
                Assert.That(h.Worker.DrainCompleted, Is.True);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(3));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(3));

                for (int i = 1; i <= 3; i++)
                {
                    Assert.That(h.OutputQueue.TryDequeue(out NvencSubmitToOutputRecord output), Is.True);
                    Assert.That(output.WorkToken.CaptureFrameId, Is.EqualTo(i));
                    Assert.That(output.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
                }
            }
        }

        [Test]
        public void Drain_HeadIncomplete_DoesNotStopEarly()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Enqueue(record); // source evidence not completed

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not observe head evidence");

                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);

                // The drain re-evaluation observes the still-incomplete evidence
                // and must not declare completion while the head is held.
                WaitSettled(h.SettledEvent, "worker did not re-evaluate after drain");
                Assert.That(h.Source.ObserveCount, Is.GreaterThanOrEqualTo(2));
                Assert.That(h.Worker.DrainCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.False);

                // Completing the evidence lets the held work finish, then stop.
                h.Source.MarkCompleted(record.WorkToken);
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not stop after drain + evidence");
                Assert.That(h.Worker.DrainCompleted, Is.True);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void Drain_StopsWithOutputQueueRemaining()
        {
            using (Harness h = Harness.Create(2))
            {
                for (int i = 1; i <= 2; i++)
                {
                    NvencSubmissionRecord record = h.CreateRecord(i);
                    h.Source.MarkCompleted(record.WorkToken);
                    h.Enqueue(record);
                }

                h.Worker.Start();
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);

                WaitSettled(h.SettledEvent, "worker did not stop");
                Assert.That(h.Worker.DrainCompleted, Is.True);

                // Worker stop does not drain the Submit-to-Output Queue.
                Assert.That(h.OutputQueue.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void Drain_NoResumeAfterStop()
        {
            using (Harness h = Harness.Create(1))
            {
                // Create while Running, but enqueue only after the worker stops.
                NvencSubmissionRecord late = h.CreateRecord(9);
                h.Source.MarkCompleted(late.WorkToken);

                h.Worker.Start();
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after empty drain");

                // A late enqueue plus notification cannot restart a stopped worker.
                h.Enqueue(late);
                h.Worker.Notify();

                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.SubmissionQueue.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void Drain_BeginDrainIdempotent()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                h.Worker.Start();
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);
                Assert.That(h.Worker.BeginDrain(), Is.True); // idempotent

                WaitSettled(h.SettledEvent, "worker did not stop");
                Assert.That(h.Worker.DrainCompleted, Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void SubmitterThrows_RecordsExactFailure_Poisons_NoOutput_Stops()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                InvalidOperationException failure = new InvalidOperationException("submit boom");
                h.Submitter.Throw(failure);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not stop after the fatal failure");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.DrainCompleted, Is.False);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(ReferenceEquals(captured, failure), Is.True);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void SubmitterThrows_DoesNotTouchSubsequentWork()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(1);
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(first.WorkToken);
                h.Source.MarkCompleted(second.WorkToken);
                h.Submitter.Throw(new InvalidOperationException("boom"));

                h.Enqueue(first);
                h.Enqueue(second);

                h.Worker.Start();
                h.SettledEvent.Reset();
                h.Worker.Notify();

                WaitSettled(h.SettledEvent, "worker did not stop after the fatal failure");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(1)); // only the head was touched
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));      // no record for the head
                Assert.That(h.SubmissionQueue.Count, Is.EqualTo(1));  // the second work is untouched
            }
        }

        [Test]
        public void PoisonStop_NotTreatedAsDrainCompletion()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                Assert.That(h.State.TryPoison(), Is.True);
                h.SettledEvent.Reset();
                h.Worker.Start();

                WaitSettled(h.SettledEvent, "worker did not stop after poison");

                Assert.That(h.Worker.DrainCompleted, Is.False);
                Assert.That(h.Worker.TryGetFailure(out _), Is.False);
                Assert.That(h.OutputQueue.Count, Is.EqualTo(0));
                Assert.That(h.Submitter.SubmitCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void BeginDrain_WhileRunning_IsRejected_WorkerContinues()
        {
            using (Harness h = Harness.Create(2))
            {
                h.Worker.Start();

                // While Running, BeginDrain is rejected with no state change.
                Assert.That(h.Worker.BeginDrain(), Is.False);
                Assert.That(h.Worker.DrainCompleted, Is.False);

                NvencSubmissionRecord first = h.CreateRecord(1);
                h.Source.MarkCompleted(first.WorkToken);
                h.Enqueue(first);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process the accepted work");
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.That(h.Worker.DrainCompleted, Is.False);

                // A later Accepted work is still processed: the rejected
                // BeginDrain did not stop the worker.
                NvencSubmissionRecord second = h.CreateRecord(2);
                h.Source.MarkCompleted(second.WorkToken);
                h.Enqueue(second);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process the later work");
                Assert.That(h.OutputQueue.Count, Is.EqualTo(2));
                Assert.That(h.Worker.IsStopped, Is.False);
            }
        }

        [Test]
        public void Dispose_WhileRunning_DoesNotStopWorker()
        {
            using (Harness h = Harness.Create(1))
            {
                h.Worker.Start();
                Assert.Throws<InvalidOperationException>(() => h.Worker.Dispose()); // running: never force-stops

                Assert.That(h.Worker.IsStopped, Is.False);

                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                h.Enqueue(record);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process after the no-op dispose");
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void Dispose_AfterStop_IsIdempotent()
        {
            using (Harness h = Harness.Create(1))
            {
                h.Worker.Start();
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SettledEvent.Reset();
                Assert.That(h.Worker.BeginDrain(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop");
                h.WaitForPhysicalStop("worker thread did not physically exit");
                Assert.That(h.Worker.IsStopped, Is.True);

                h.Worker.Dispose();
                h.Worker.Dispose(); // idempotent
            }
        }

        [Test]
        public void Dispose_BeforeStart_IsAllowedAndIdempotent()
        {
            using (Harness h = Harness.Create(1))
            {
                // The owned signal is released before any startup; composition
                // teardown after a failed initialization can still dispose.
                h.Worker.Dispose();
                h.Worker.Dispose(); // idempotent
            }
        }

        [Test]
        public void Start_AfterDispose_NoSideEffect()
        {
            using (Harness h = Harness.Create(1))
            {
                h.Worker.Dispose();

                // Starting a disposed service has no side effect and creates no
                // worker thread.
                h.Worker.Start();

                Assert.That(h.Worker.IsStopped, Is.True);
            }
        }

        [Test]
        public void NotifyAndBeginDrain_AfterDispose_ThrowBeforeSideEffect()
        {
            using (Harness h = Harness.Create(1))
            {
                h.Worker.Dispose();

                Assert.Throws<ObjectDisposedException>(() => h.Worker.Notify());
                Assert.Throws<ObjectDisposedException>(() => h.Worker.BeginDrain());
            }
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

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), "Method signature not found: " + methodName);

            int open = source.IndexOf('{', signature);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Method body not found: " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Method body not terminated: " + methodName);
            return null;
        }

        private static CaptureFrameRenderTargetPool MakeRenderPool(int capacity)
        {
            return new CaptureFrameRenderTargetPool(
                capacity, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            private readonly object _gate = new object();
            private readonly List<CaptureFrameWorkToken> _completed = new List<CaptureFrameWorkToken>();
            private int _observeCount;

            internal int ObserveCount => Volatile.Read(ref _observeCount);

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
                Interlocked.Increment(ref _observeCount);

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

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter, IDisposable
        {
            private bool _result = true;
            private Exception _exception;
            private bool _block;
            private int _submitCount;
            private readonly Queue<bool> _results = new Queue<bool>();
            private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

            internal int SubmitCount => Volatile.Read(ref _submitCount);

            internal ManualResetEventSlim Entered => _entered;

            internal ManualResetEventSlim ReleaseSubmit => _release;

            internal void SetResult(bool result)
            {
                _result = result;
            }

            internal void EnqueueResult(bool result)
            {
                _results.Enqueue(result);
            }

            internal void Throw(Exception exception)
            {
                _exception = exception;
            }

            internal void BlockInSubmit()
            {
                _block = true;
            }

            public void Dispose()
            {
                _entered.Dispose();
                _release.Dispose();
            }

            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                Interlocked.Increment(ref _submitCount);

                if (_exception != null)
                {
                    throw _exception;
                }

                if (_block)
                {
                    _entered.Set();
                    _release.Wait(WatchdogTimeoutMs);
                }

                return _results.Count > 0 ? _results.Dequeue() : _result;
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
            internal NvencOrderedSubmitWorkerService Worker { get; }
            internal ManualResetEventSlim SettledEvent { get; }

            private readonly Action _settledHandler;
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
                Worker = new NvencOrderedSubmitWorkerService(State, Processor);
                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
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
                FieldInfo field = typeof(NvencOrderedSubmitWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                return (Thread)field?.GetValue(Worker);
            }

            public void Dispose()
            {
                if (!Worker.IsStopped)
                {
                    // Guarantee a stop condition exists and wake the worker so
                    // it can exit; physical exit is confirmed by joining below.
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
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

                    SyncPool.TryReturn(record.SyncSlot);
                    SamplePool.TryReturn(record.SampleSlot);
                    WorkPool.TryReturn(record.WorkSlot);
                    FrameCompletionCreditPool.TryReturn(record.FrameCompletionCredit);
                    SubmitToOutputCreditPool.TryReturn(record.SubmitToOutputCredit);
                }

                RenderPool.Dispose();
                Submitter.Dispose();
                SettledEvent.Dispose();
            }
        }
    }
}
