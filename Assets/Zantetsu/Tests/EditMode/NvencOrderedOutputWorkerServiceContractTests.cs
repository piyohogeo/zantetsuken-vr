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

        [Test]
        public void Constructor_RejectsNullDependencies()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(null, h.Processor));
                Assert.Throws<ArgumentNullException>(() => new NvencOrderedOutputWorkerService(h.State, null));
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
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

                ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park behind the collector gate");

                // Parked: the source ran once, no completion, and the worker is
                // still alive.
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Boundary.TryCollect(out _), Is.False);
                Assert.That(h.Worker.IsStopped, Is.False);

                release.Set();
                Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");

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
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.Enqueue(record);

                ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);
                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park behind the sink gate");

                // Parked: source and writer each ran once, no completion yet.
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                Assert.That(h.Boundary.TryCollect(out _), Is.False);
                Assert.That(h.Worker.IsStopped, Is.False);

                release.Set();
                Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");

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
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);

                // Park the processor at the publish stage with a terminal sink
                // result, as if the collector and sink had already completed.
                ParkAtSubmittedPublish(h.Processor, record);

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park behind the completion gate");

                Assert.That(h.Boundary.TryCollect(out _), Is.False);
                Assert.That(h.Worker.IsStopped, Is.False);

                release.Set();
                Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");

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
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateFailedBeforeSubmit(
                    1, NvencFailedBeforeSubmitReason.GpuConversionFailed);
                h.Enqueue(record);

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not park behind the release gate");

                // The Sample Slot is still held; nothing progressed.
                Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.True);
                Assert.That(h.Boundary.TryCollect(out _), Is.False);

                release.Set();
                Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");

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

            // No normal drain stop API in this unit.
            Assert.That(source, Does.Not.Contain("BeginDrain"));
            Assert.That(source, Does.Not.Contain("DrainCompleted"));
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

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

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

        private sealed class FakeAppender : INvencRunChunkAppender
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
            internal FakeAppender Writer;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencRunChunkSink Sink;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal ManualResetEventSlim SettledEvent;

            private readonly Action _settledHandler;

            internal Harness()
            {
                State = new NvencCaptureProcessState();
                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Source = new FakeOutputSource();
                Writer = new FakeAppender();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);
                Worker = new NvencOrderedOutputWorkerService(State, Processor);
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
