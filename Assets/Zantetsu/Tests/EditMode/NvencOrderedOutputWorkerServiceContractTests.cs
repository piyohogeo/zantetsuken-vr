using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 dedicated Output Worker service: the
    /// single background thread that drives the threadless
    /// <see cref="NvencOrderedOutputProcessor"/> and wakes only on a coalescing
    /// notification. Uses fakes for the output source and the chunk writer; no
    /// GPU, NVENC, native resource, or timing assumption is used.
    /// </summary>
    public class NvencOrderedOutputWorkerServiceContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        /// <summary>
        /// Deliberately short deadline for a probe whose condition can never
        /// become true, so the probe's own failure is the assertion.
        /// </summary>
        private const int BoundedProbeTimeoutMs = 250;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Constructor_RejectsNullDependencies()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(null, h.Processor, h.Context, h.SubmitWorker, h.Teardown));
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(h.State, null, h.Context, h.SubmitWorker, h.Teardown));
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(h.State, h.Processor, null, h.SubmitWorker, h.Teardown));
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(h.State, h.Processor, h.Context, null, h.Teardown));
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(h.State, h.Processor, h.Context, h.SubmitWorker, null));
            }
        }

        [Test]
        public void Worker_RunsProcessorOnDedicatedThread()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process the record");

                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Source.ExecutingThreadName, Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
            }
        }

        [Test]
        public void Enqueue_Notify_SubmittedReachesCompletion()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(7);
                h.Enqueue(record);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not reach completion");

                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void FailedBeforeSubmit_And_AbandonedSubmitted_ConvergeOnWorker()
        {
            using (Harness h = Harness.Create())
            {
                h.Enqueue(h.CreateFailedBeforeSubmit(1, NvencFailedBeforeSubmitReason.GpuConversionFailed));
                h.Enqueue(h.CreateSubmitted(2));

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge both records");

                Assert.That(h.State.IsRunAbandoned, Is.True);

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord first), Is.True);
                Assert.That(first.WorkToken.CaptureFrameId, Is.EqualTo(1));
                Assert.That(first.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
                Assert.That(first.Reason, Is.EqualTo(NvencFrameCompletionReason.GpuConversionFailed));

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord second), Is.True);
                Assert.That(second.WorkToken.CaptureFrameId, Is.EqualTo(2));
                Assert.That(second.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
                Assert.That(second.Reason, Is.EqualTo(NvencFrameCompletionReason.CancelledAfterRunAbandoned));

                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void FifoOrder_NoOvertake()
        {
            using (Harness h = Harness.Create())
            {
                for (long frame = 1; frame <= 3; frame++)
                {
                    h.Enqueue(h.CreateSubmitted(frame));
                }

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process three records");

                long[] expected = { 1, 2, 3 };
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                    Assert.That(completion.WorkToken.CaptureFrameId, Is.EqualTo(expected[i]));
                    Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                }

                Assert.That(h.Sink.AppendedCount, Is.EqualTo(3));
            }
        }

        [Test]
        public void CollectorBusy_ParksThenResumesWithoutRerun()
        {
            using (ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

                h.Source.SourceEntered = sourceEntered;
                h.Source.WaitForGateHeld = gateHeld;

                Thread holder = new Thread(() =>
                {
                    if (sourceEntered.Wait(WatchdogTimeoutMs))
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
                    h.SettledEvent.Reset();
                    h.Worker.Notify();
                    WaitSettled(h.SettledEvent, "worker did not park behind the collector gate");

                    // Parked: the source ran once, no completion, and the worker is
                    // still alive.
                    Assert.That(h.Source.CallCount, Is.EqualTo(1));
                    Assert.That(h.Boundary.TryCollect(out _), Is.False);
                    Assert.That(h.Worker.IsStopped, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                // Notify again: resume without re-running the source.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not resume after the gate was released");

                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void SinkBusy_ParksThenResumesWithoutRerun()
        {
            using (ManualResetEventSlim writerEntered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

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
                    h.SettledEvent.Reset();
                    h.Worker.Notify();
                    WaitSettled(h.SettledEvent, "worker did not park behind the sink gate");

                    // Parked: source and writer each ran once, no completion yet.
                    Assert.That(h.Source.CallCount, Is.EqualTo(1));
                    Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                    Assert.That(h.Boundary.TryCollect(out _), Is.False);
                    Assert.That(h.Worker.IsStopped, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not resume after the sink gate was released");

                // Resume converges without re-running the source or the writer.
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void CompletionGateBusy_ParksThenResumesWithoutRerun()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);

                // Park the processor at the publish stage with a terminal sink
                // result, as if the collector and sink had already completed.
                ParkAtSubmittedPublish(h.Processor, record);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        entered.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

                    h.SettledEvent.Reset();
                    h.Worker.Notify();
                    WaitSettled(h.SettledEvent, "worker did not park behind the completion gate");

                    Assert.That(h.Boundary.TryCollect(out _), Is.False);
                    Assert.That(h.Worker.IsStopped, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not resume after the completion gate was released");

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void ReleaseGateBusy_ParksThenResumes()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateFailedBeforeSubmit(
                    1, NvencFailedBeforeSubmitReason.GpuConversionFailed);
                h.Enqueue(record);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        entered.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

                    h.SettledEvent.Reset();
                    h.Worker.Notify();
                    WaitSettled(h.SettledEvent, "worker did not park behind the release gate");

                    // The Sample Slot is still held; nothing progressed.
                    Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.True);
                    Assert.That(h.Boundary.TryCollect(out _), Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not resume after the release gate was released");

                Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            }
        }

        [Test]
        public void QueueEmpty_Running_DoesNotStop()
        {
            using (Harness h = Harness.Create())
            {
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park on the empty queue");
                Assert.That(h.Worker.IsStopped, Is.False);

                // A later record is still processed: the worker never stopped.
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process the later record");
                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void QueueEmpty_Draining_DoesNotStop()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1); // rent while Running
                Assert.That(h.State.TryBeginDrain(), Is.True);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park after drain");
                Assert.That(h.Worker.IsStopped, Is.False);

                h.Enqueue(record);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process the record while draining");
                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void QueueEmpty_RunAbandoned_DoesNotStop()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1); // rent while Running
                Assert.That(h.State.TryBeginRunAbandoned(), Is.True);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park after run abandoned");
                Assert.That(h.Worker.IsStopped, Is.False);

                h.Enqueue(record);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not recover the record after abandonment");
                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
            }
        }

        [Test]
        public void ExternalPoison_Notify_StopsWithoutFailure()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryPoison(), Is.True);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not stop after poison");
                h.WaitForPhysicalStop("worker thread did not physically exit");

                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Worker.TryGetFailure(out _), Is.False);
            }
        }

        [Test]
        public void ProcessorException_HoldsExactFailure_PoisonsStopsNoCompletion()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                InvalidOperationException boom = new InvalidOperationException("boom");
                h.Source.ExceptionToThrow = boom;
                h.Enqueue(record);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not stop after the fatal failure");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(ReferenceEquals(captured, boom), Is.True);
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
                Assert.That(h.Boundary.TryCollect(out _), Is.False);
            }
        }

        [Test]
        public void Poison_NoCompletionAppendOrReturn()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

                Assert.That(h.State.TryPoison(), Is.True);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not stop after poison");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.Source.CallCount, Is.EqualTo(0));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
                Assert.That(h.Boundary.TryCollect(out _), Is.False);
                Assert.That(h.Worker.TryGetFailure(out _), Is.False);
            }
        }

        [Test]
        public void Notify_Coalesces_ExactlyOneCompletion()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

                h.SettledEvent.Reset();
                h.Worker.Notify();
                h.Worker.Notify();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process the record");

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
                Assert.That(h.Boundary.TryCollect(out _), Is.False);
                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Dispose_WhileRunning_DoesNotStopWorker()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<InvalidOperationException>(() => h.Worker.Dispose());
                Assert.That(h.Worker.IsStopped, Is.False);

                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not process after the rejected dispose");

                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }
        }

        [Test]
        public void Dispose_AfterStop_IsIdempotent()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryPoison(), Is.True);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not stop");
                h.WaitForPhysicalStop("worker did not physically exit");
                Assert.That(h.Worker.IsStopped, Is.True);

                h.Worker.Dispose();
                h.Worker.Dispose(); // idempotent
            }
        }

        [Test]
        public void Notify_AfterDispose_ThrowsBeforeSideEffect()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryPoison(), Is.True);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not stop");
                h.WaitForPhysicalStop("worker did not physically exit");
                h.Worker.Dispose();

                Assert.Throws<ObjectDisposedException>(() => h.Worker.Notify());
            }
        }

        // ---- Terminal convergence ----

        [Test]
        public void Terminal_SettleObservedAfterRequest_IsNotEvidence_ConvergenceConfirmsTheRealCondition()
        {
            // The four events outlive the Harness: the Worker can still be
            // invoking a handler snapshot taken before the removal below, so
            // they are released only after the Harness has joined the Worker
            // thread.
            using (ManualResetEventSlim insideRaise = new ManualResetEventSlim(false))
            using (ManualResetEventSlim proceed = new ManualResetEventSlim(false))
            using (ManualResetEventSlim observedSettle = new ManualResetEventSlim(false))
            using (ManualResetEventSlim releaseWorker = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                // Two ordinary Settled observers, no product change and no
                // reflection. The first parks the Worker at the very start of a
                // raise - a point it reaches only after it has already reset its
                // wake signal and re-checked for work - so the terminal request
                // below lands while that raise is in flight. The second
                // publishes the settle the way a fixture's own handler does, and
                // then holds the Worker inside the raise so it cannot advance
                // the terminal while the assertions run.
                Action enterRaise = () =>
                {
                    insideRaise.Set();
                    proceed.Wait(WatchdogTimeoutMs);
                };
                Action publishSettle = () =>
                {
                    observedSettle.Set();
                    releaseWorker.Wait(WatchdogTimeoutMs);
                };

                h.Worker.Settled += enterRaise;
                h.Worker.Settled += publishSettle;
                try
                {
                    // Wake the parked Worker so it converges on nothing and
                    // walks into the raise.
                    h.Worker.Notify();
                    Assert.That(insideRaise.Wait(WatchdogTimeoutMs), Is.True,
                        "worker did not reach its settle raise");

                    // The request is accepted while that raise is in flight, so
                    // the raise carries no information about it.
                    Assert.That(observedSettle.IsSet, Is.False);
                    Assert.That(h.Worker.TryRequestAbandon(), Is.True);

                    // Let the in-flight raise finish: the settle is now observed
                    // strictly after the request was accepted.
                    proceed.Set();
                    Assert.That(observedSettle.Wait(WatchdogTimeoutMs), Is.True,
                        "the in-flight settle was not observed");

                    // Treating that settle as convergence evidence is wrong. The
                    // terminal has not been advanced and is not collectable, and
                    // this holds deterministically because the Worker is still
                    // held inside the raise.
                    Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);

                    // The convergence helper does not accept it either: with the
                    // Worker still held, the real condition never becomes true,
                    // so the helper exhausts its bounded watchdog and fails
                    // instead of reporting a terminal.
                    Assert.Throws<AssertionException>(
                        () => TerminalConvergence.Collect(
                            h.Worker.TryCollectTerminal,
                            h.Worker,
                            observedSettle,
                            BoundedProbeTimeoutMs,
                            "bounded probe against a held worker"),
                        "the convergence helper must not accept a settle as evidence.");
                    Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
                }
                finally
                {
                    proceed.Set();
                    releaseWorker.Set();
                    h.Worker.Settled -= enterRaise;
                    h.Worker.Settled -= publishSettle;
                }

                // Released, the same request converges and the helper collects
                // that terminal exactly once, inside the watchdog.
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(
                    h, "worker did not converge the abandon request");
                Assert.That(outcome.IsNone, Is.False);
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(outcome.IsFinalized, Is.False);

                // Nothing was re-issued and nothing is collected twice: the only
                // repeated call is the non-destructive collect.
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryGetFailure(out _), Is.False);
            }
        }

        // ---- Normal teardown ----

        [Test]
        public void Teardown_AfterCollectedAbandon_ExecutesOnceOnWorkerThread_StopsNormally()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(h.Worker.TeardownCompleted, Is.False);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                Assert.That(h.Worker.TeardownCompleted, Is.True);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Worker.TryGetFailure(out _), Is.False);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
                Assert.That(h.Teardown.ExecutingThreadName, Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));

                // At-most-once: no second request, receipt, or stop.
                Assert.That(h.Worker.TryRequestTeardown(), Is.False);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Teardown_PendingProcessorWork_NotStartedThenRetries()
        {
            using (Harness h = Harness.Create())
            {
                // Rent the record while Running, before the drain, so the
                // pools are still open; it is enqueued only after collection.
                NvencSubmitToOutputRecord pending = h.CreateFailedBeforeSubmit(
                    1, NvencFailedBeforeSubmitReason.GpuConversionFailed);

                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                // A failed-before-submit record enqueued without a
                // notification leaves the processor with pending work
                // deterministically, which must block the teardown request.
                h.Enqueue(pending);
                Assert.That(h.Processor.HasPendingWork, Is.True);

                Assert.That(h.Worker.TryRequestTeardown(), Is.False);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(0));
                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.False);

                // Drain the pending record and park again; the retry is then
                // admitted and completes the normal teardown.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not drain the pending record");
                Assert.That(h.Processor.HasPendingWork, Is.False);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after teardown");

                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TeardownCompleted, Is.True);
                Assert.That(h.Worker.IsStopped, Is.True);
            }
        }

        [Test]
        public void Teardown_Exception_ExactFailurePoisonsNoStopEvidence()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                InvalidOperationException boom = new InvalidOperationException("teardown boom");
                h.Teardown.ExceptionToThrow = boom;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the teardown exception");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(ReferenceEquals(captured, boom), Is.True);
                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Teardown_NullReceipt_PoisonsNoStopEvidence()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                h.Teardown.ReturnNull = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the null receipt");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out _), Is.True);
                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Teardown_ForeignReceipt_PoisonsNoStopEvidence()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                // A receipt issued by a different teardown implementation.
                h.Teardown.ReceiptToReturn = NvencOutputWorkerTeardownReceipt.Issue(new FakeTeardown());

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the foreign receipt");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out _), Is.True);
                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Teardown_ExternalPoisonFirst_NoTeardownContact()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.Worker.TryRequestTeardown(), Is.False);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(0));
                Assert.That(h.Worker.TeardownCompleted, Is.False);
            }
        }

        [Test]
        public void Teardown_PoisonDuringTeardown_NoNormalStopEvidence()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                // Park the teardown inside TearDown() so the main thread can
                // poison deterministically while the teardown is still running.
                h.Teardown.Entered = entered;
                h.Teardown.Release = release;

                try
                {
                    h.SettledEvent.Reset();
                    Assert.That(h.Worker.TryRequestTeardown(), Is.True);

                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "teardown did not enter");

                    // Poison while the teardown is still executing.
                    Assert.That(h.State.TryPoison(), Is.True);

                    // The teardown returns normally, but the process is already
                    // Poisoned: no normal-stop evidence may be published.
                }
                finally
                {
                    release.Set();
                }

                WaitSettled(h.SettledEvent, "worker did not stop after the poison");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.True);
            }
        }

        [Test]
        public void Teardown_GateBusyAtReturn_DoesNotRerun_ConvergesOnRelease()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (ManualResetEventSlim returned = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                // Park the teardown so the worker returns from it only while
                // the Main Thread still holds the process-state gate, exactly
                // the transient contention that previously re-ran TearDown().
                h.Teardown.Entered = entered;
                h.Teardown.Release = release;

                try
                {
                    h.Teardown.Returned = returned;

                    h.SettledEvent.Reset();
                    Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "teardown did not enter");

                    // Hold the gate, as a concurrent submit step would, while the
                    // teardown returns.
                    Assert.That(h.State.TryBeginSubmitStep(), Is.True);
                    try
                    {
                        release.Set();
                        Assert.That(returned.Wait(WatchdogTimeoutMs), Is.True, "teardown did not return");
                    }
                    finally
                    {
                        // Release the gate: the worker must settle the pinned
                        // receipt and converge to a normal stop without any
                        // notification and without re-running the teardown.
                        h.State.EndSubmitStep();
                    }

                    WaitSettled(h.SettledEvent, "worker did not converge after the gate release");
                    h.WaitForPhysicalStop("worker did not physically exit");

                    Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
                    Assert.That(h.Worker.TeardownCompleted, Is.True);
                    Assert.That(h.Worker.IsStopped, Is.True);
                }
                finally
                {
                    release.Set();
                }
            }
        }

        [Test]
        public void Teardown_AfterStop_TerminalAndTeardownRejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;
                Assert.That(h.Context.TryFreezeAcceptedFrames(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                CollectTerminal(h, "worker did not converge the abandon request");

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit");
                Assert.That(h.Worker.IsStopped, Is.True);

                // After the physical stop no further terminal or teardown
                // request is admitted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
                Assert.That(h.Worker.TryRequestTeardown(), Is.False);
            }
        }

        [Test]
        public void TeardownReceipt_TypeShape_ExactIssuerOnly()
        {
            Type type = typeof(NvencOutputWorkerTeardownReceipt);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(INvencOutputWorkerTeardown)));
            Assert.That(fields[0].FieldType, Is.Not.EqualTo(typeof(IntPtr)));
            Assert.That(fields[0].FieldType, Is.Not.EqualTo(typeof(byte[])));
        }

        [Test]
        public void TeardownReceipt_IsIssuedFor_RejectsNullAndForeign()
        {
            FakeTeardown issued = new FakeTeardown();
            NvencOutputWorkerTeardownReceipt receipt = issued.TearDown();

            Assert.That(receipt.IsIssuedFor(issued), Is.True);
            Assert.That(receipt.IsIssuedFor(null), Is.False);
            Assert.That(receipt.IsIssuedFor(new FakeTeardown()), Is.False);
        }

        [Test]
        public void Source_ExactlyOneThread_NoPollingNoExtraQueue()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputWorkerService.cs"));

            Assert.That(CountOccurrences(source, "new Thread("), Is.EqualTo(1),
                "The service must create exactly one worker thread.");
            Assert.That(source, Does.Contain("IsBackground = true"));
            Assert.That(source, Does.Contain("Name = WorkerThreadName"));
            Assert.That(CountOccurrences(source, "_signal.Wait()"), Is.EqualTo(1),
                "The worker must have exactly one blocking wait.");

            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("Timer"));
            Assert.That(source, Does.Not.Contain("SpinWait"));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new Queue"));
            Assert.That(source, Does.Not.Contain("OrderBy"));
            Assert.That(source, Does.Not.Contain("Array.Sort"));
            Assert.That(source, Does.Not.Contain("Peek"));
            Assert.That(source, Does.Not.Contain("System.Linq"));

            // The Output Worker has no normal drain-stop or join API of its
            // own; its terminal acceptance is gated on the injected Submit
            // Worker drain evidence.
            Assert.That(source, Does.Not.Contain("BeginDrain"));
            Assert.That(source, Does.Not.Contain("TryJoin"));
        }

        [Test]
        public void Source_ResetRechecksBeforeWait()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputWorkerService.cs"));

            int reset = source.IndexOf("_signal.Reset()", StringComparison.Ordinal);
            int wait = source.IndexOf("_signal.Wait()", StringComparison.Ordinal);
            Assert.That(reset, Is.GreaterThanOrEqualTo(0), "Missing signal reset.");
            Assert.That(wait, Is.GreaterThanOrEqualTo(0), "Missing signal wait.");
            Assert.That(reset, Is.LessThan(wait), "Reset must precede Wait.");

            string between = source.Substring(reset, wait - reset);
            Assert.That(between, Does.Contain("TryProcessNextSafely"),
                "The worker must re-check for progress between Reset and Wait.");
        }

        [Test]
        public void Source_IsStopped_PhysicalNonWaiting()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputWorkerService.cs"));

            Assert.That(source, Does.Contain(".IsAlive"));
            Assert.That(source, Does.Not.Contain("_workerStopped"));
        }

        [Test]
        public void Source_Teardown_NoTextureDestroyJoinTracePlanPublicationRegistry()
        {
            string worker = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputWorkerService.cs"));
            string teardown = File.ReadAllText(Path.Combine(RuntimeDirectory(), "INvencOutputWorkerTeardown.cs"));
            string receipt = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOutputWorkerTeardownReceipt.cs"));

            // The teardown path performs no Unity Texture destroy, backend
            // TryJoin, Trace, Registry commit, Plan, or Publication work.
            Assert.That(worker, Does.Not.Contain("Destroy"));
            Assert.That(worker, Does.Not.Contain("Texture"));
            Assert.That(worker, Does.Not.Contain("TryJoin"));
            Assert.That(worker, Does.Not.Contain("Trace"));
            Assert.That(worker, Does.Not.Contain("Plan"));
            Assert.That(worker, Does.Not.Contain("Publication"));
            Assert.That(worker, Does.Not.Contain("Registry"));

            // The boundary and receipt hold no native handle or byte array.
            Assert.That(teardown, Does.Not.Contain("IntPtr"));
            Assert.That(receipt, Does.Not.Contain("IntPtr"));
            Assert.That(receipt, Does.Not.Contain("byte["));
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

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
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
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
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
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
        }

        private static void ParkAtSubmittedPublish(
            NvencOrderedOutputProcessor processor,
            NvencSubmitToOutputRecord record)
        {
            SetField(processor, "_current", record);

            FieldInfo stage = typeof(NvencOrderedOutputProcessor).GetField(
                "_stage", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(stage, Is.Not.Null, "_stage field not found.");
            stage.SetValue(processor, Enum.ToObject(stage.FieldType, 3)); // SubmittedPublish

            SetField(processor, "_sinkResult", NvencRunChunkSinkResult.Appended(record.WorkToken, 16));
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

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
            internal NvencRunChunkAppendOutcome Outcome = NvencRunChunkAppendOutcome.Appended;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim WaitForGateHeld;

            internal int AppendCount => Volatile.Read(ref _appendCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                Interlocked.Increment(ref _appendCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

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
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
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
            private int _callCount;
            internal Exception ExceptionToThrow;
            internal bool ReturnNull;
            internal NvencOutputWorkerTeardownReceipt ReceiptToReturn;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;
            internal ManualResetEventSlim Returned;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal string ExecutingThreadName;

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (Release != null)
                {
                    Release.Wait(WatchdogTimeoutMs);
                }

                if (Returned != null)
                {
                    Returned.Set();
                }

                if (ReturnNull)
                {
                    return null;
                }

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

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

            internal NvencCaptureWorkSlotPool SubmitWorkSlots;
            internal NvencEncodeSampleSlotPool SubmitSampleSlots;
            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCreditPool;
            internal NvencFrameCompletionCreditPool SubmitFrameCompletionCredits;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

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

                // Minimal exact Submit Worker bound to the same process state.
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

                // Deterministically park the worker once before returning, so
                // later enqueues are observed only through Notify and never
                // through a stray in-flight processing loop.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            // Write-only convenience: publishes the Submit Worker's monotonic
            // drain completion evidence deterministically so a terminal
            // request can be admitted.
            internal bool SubmitDrained
            {
                set => SetField(SubmitWorker, "_drainCompleted", value);
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

            internal NvencSubmitToOutputRecord CreateFailedBeforeSubmit(
                long frameId,
                NvencFailedBeforeSubmitReason reason)
            {
                RentAll(
                    out NvencCaptureWorkSlotLease work,
                    out NvencEncodeSampleSlotLease sample,
                    out NvencSubmitToOutputCreditLease submit,
                    out NvencFrameCompletionCreditLease frame);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), work.SlotIndex, work.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, work, sample, submit, frame, reason);
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
                    // Guarantee a stop condition exists and wake the worker so
                    // it can exit; physical exit is confirmed by joining below.
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
