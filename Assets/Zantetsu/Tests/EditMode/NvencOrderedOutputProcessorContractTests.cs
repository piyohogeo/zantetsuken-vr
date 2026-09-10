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
    /// Contract tests for the Phase 0.11 Ordered Output Processor: the
    /// threadless boundary that drains the fixed Submit-to-Output Queue in FIFO
    /// order and connects each record to the Collector, Run Chunk Sink, the
    /// release/recovery coordinators, and the Frame Completion boundary. Uses
    /// fakes for the output source and the chunk writer; no GPU, NVENC, worker,
    /// or native resource is used.
    /// </summary>
    public class NvencOrderedOutputProcessorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        // -------------------------------------------------------------------
        // Normal Submitted path
        // -------------------------------------------------------------------

        [Test]
        public void Process_SubmittedSuccess_CollectSinkSucceededCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);

            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(1));

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.None));
            Assert.That(h.State.IsRunAbandoned, Is.False);
        }

        [Test]
        public void Process_FifoOrder_ThreeSubmitted()
        {
            Harness h = new Harness();

            for (long frame = 1; frame <= 3; frame++)
            {
                h.Enqueue(h.CreateSubmitted(frame));
            }

            for (int i = 0; i < 3; i++)
            {
                Assert.That(h.Processor.TryProcessNext(), Is.True);
            }

            Assert.That(h.Sink.AppendedCount, Is.EqualTo(3));
            Assert.That(h.Sink.LastFrameId, Is.EqualTo(3));

            long[] expected = { 1, 2, 3 };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.WorkToken.CaptureFrameId, Is.EqualTo(expected[i]));
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            }

            Assert.That(h.Boundary.TryCollect(out _), Is.False);
        }

        [Test]
        public void Process_CollectorPending_HoldsNextRecord()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord first = h.CreateSubmitted(1);
            NvencSubmitToOutputRecord second = h.CreateSubmitted(2);
            h.Enqueue(first);
            h.Enqueue(second);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Exception holderError = null;

            h.Source.SourceEntered = sourceEntered;
            h.Source.WaitForGateHeld = gateHeld;

            Thread holder = new Thread(() =>
            {
                try
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
                }
                catch (Exception ex)
                {
                    holderError = ex;
                }
            })
            {
                IsBackground = true,
            };
            bool holderJoined = false;
            holder.Start();
            try
            {
                // The collector parks; the processor holds the current and never
                // touches the second record.
                Assert.That(h.Processor.TryProcessNext(), Is.False);
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.OutputQueue.Count, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Resume: first completes without re-contacting the source.
            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Processor.HasCurrentWork, Is.False);

            // The second record is then processed normally.
            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(2));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(2));
        }

        [Test]
        public void Process_SinkPending_NoRerunOfCollectorSourceWriter()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Enqueue(record);

            ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Exception holderError = null;

            h.Writer.Entered = writerEntered;
            h.Writer.WaitForGateHeld = gateHeld;

            Thread holder = new Thread(() =>
            {
                try
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
                }
                catch (Exception ex)
                {
                    holderError = ex;
                }
            })
            {
                IsBackground = true,
            };
            bool holderJoined = false;
            holder.Start();
            try
            {
                // The sink parks behind the post-writer gate; the processor holds
                // the current, the owned lease, and the sink stage.
                Assert.That(h.Processor.TryProcessNext(), Is.False);
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
                Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Resume: converges without re-running the source or the writer.
            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(1));
            Assert.That(h.Processor.HasCurrentWork, Is.False);

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
        }

        [Test]
        public void Process_Busy_HoldsCurrentThenConvergesExactlyOnce()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Enqueue(record);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Exception holderError = null;

            h.Source.SourceEntered = sourceEntered;
            h.Source.WaitForGateHeld = gateHeld;

            Thread holder = new Thread(() =>
            {
                try
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
                }
                catch (Exception ex)
                {
                    holderError = ex;
                }
            })
            {
                IsBackground = true,
            };
            bool holderJoined = false;
            holder.Start();
            try
            {
                Assert.That(h.Processor.TryProcessNext(), Is.False);
                Assert.That(h.Processor.HasCurrentWork, Is.True);
                Assert.That(h.Processor.HasPendingWork, Is.True);
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Processor.HasCurrentWork, Is.False);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Writer.AppendCount, Is.EqualTo(1));

            // Exactly one completion.
            Assert.That(h.Boundary.TryCollect(out _), Is.True);
            Assert.That(h.Boundary.TryCollect(out _), Is.False);
        }

        // -------------------------------------------------------------------
        // Controlled failure paths
        // -------------------------------------------------------------------

        [Test]
        public void Process_CollectorControlledFailure_NoSink_FailedCompletion_RunAbandoned()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Source.Result = false;
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);

            // The Sink and its writer are never contacted.
            Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
            Assert.That(h.Source.CallCount, Is.EqualTo(1));

            Assert.That(h.State.IsRunAbandoned, Is.True);
            Assert.That(h.State.IsDraining, Is.True);

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.OutputCollectControlledFailure));
        }

        [Test]
        public void Process_SinkControlledFailure_FailedCompletion_RunAbandoned()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Writer.Outcome = NvencRunChunkAppendOutcome.RejectedBeforeWrite;
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);

            Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));

            Assert.That(h.State.IsRunAbandoned, Is.True);
            Assert.That(h.State.IsDraining, Is.True);

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.RunChunkControlledFailure));
        }

        [Test]
        public void Process_ControlledFailure_AdmissionRejectsNewWork()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Source.Result = false;
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.State.IsRunAbandoned, Is.True);

            // Admission and slot reservation reject new work after abandonment.
            Assert.That(h.State.IsAccepting, Is.False);
            Assert.That(h.WorkSlots.TryRent(out NvencCaptureWorkSlotLease work), Is.False);
            Assert.That(work.IsValid, Is.False);
        }

        // -------------------------------------------------------------------
        // FailedBeforeSubmit path
        // -------------------------------------------------------------------

        [Test]
        public void Process_FailedBeforeSubmit_NoCollectorSinkContact()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed);
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);

            Assert.That(h.Source.CallCount, Is.EqualTo(0));
            Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            Assert.That(h.State.IsRunAbandoned, Is.True);

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.GpuConversionFailed));
        }

        [Test]
        public void Process_FailedBeforeSubmit_SampleReturned_CreditsRemainUntilCollect()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed);
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);

            // Sample returned by the release boundary; Work and Frame credits
            // stay active until the Main Thread collects.
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
            Assert.That(h.WorkSlots.IsActive(record.WorkSlot), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(record.FrameCompletionCredit), Is.True);
            // Submit-to-Output credit was returned at publish.
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.False);

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(h.WorkSlots.IsActive(record.WorkSlot), Is.False);
            Assert.That(h.FrameCompletionCredits.IsActive(record.FrameCompletionCredit), Is.False);
        }

        // -------------------------------------------------------------------
        // Submitted after Run Abandoned
        // -------------------------------------------------------------------

        [Test]
        public void Process_AbandonedSubmitted_RecoversWithoutAppend()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Enqueue(record);

            // Run is already abandoned before this Submitted record is seen.
            Assert.That(h.State.TryBeginRunAbandoned(), Is.True);
            Assert.That(h.State.IsRunAbandoned, Is.True);

            Assert.That(h.Processor.TryProcessNext(), Is.True);

            // The output is recovered through the collector and the Owned
            // Access Unit is returned, but nothing is appended to the chunk.
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
            Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.CancelledAfterRunAbandoned));
        }

        // -------------------------------------------------------------------
        // Mixed FIFO
        // -------------------------------------------------------------------

        [Test]
        public void Process_MixedRecords_FifoCompletions()
        {
            Harness h = new Harness();

            // Frame 1: Submitted (succeeds and appends). Frame 2: a controlled
            // failure that records Run Abandoned. Frame 3: a Submitted record
            // seen after the abandonment is recovered, never appended.
            h.Enqueue(h.CreateSubmitted(1));
            h.Enqueue(h.CreateFailedBeforeSubmit(2, NvencFailedBeforeSubmitReason.GpuConversionFailed));
            h.Enqueue(h.CreateSubmitted(3));

            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Processor.TryProcessNext(), Is.True);

            long[] expectedFrames = { 1, 2, 3 };
            CaptureFrameCompletionStatus[] expectedStatus =
            {
                CaptureFrameCompletionStatus.Succeeded,
                CaptureFrameCompletionStatus.Failed,
                CaptureFrameCompletionStatus.Cancelled,
            };

            for (int i = 0; i < 3; i++)
            {
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.WorkToken.CaptureFrameId, Is.EqualTo(expectedFrames[i]));
                Assert.That(completion.Status, Is.EqualTo(expectedStatus[i]));
            }

            Assert.That(h.Boundary.TryCollect(out _), Is.False);
            Assert.That(h.State.IsRunAbandoned, Is.True);
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(1));
        }

        // -------------------------------------------------------------------
        // Exceptions and ownership breaks
        // -------------------------------------------------------------------

        [Test]
        public void Process_SourceException_PropagatesPoisonsNoCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            InvalidOperationException boom = new InvalidOperationException("boom");
            h.Source.ExceptionToThrow = boom;
            h.Enqueue(record);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Processor.TryProcessNext());

            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            Assert.That(h.Boundary.TryCollect(out _), Is.False);
        }

        [Test]
        public void Process_WriterException_PropagatesPoisonsNoCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            InvalidOperationException boom = new InvalidOperationException("boom");
            h.Writer.ExceptionToThrow = boom;
            h.Enqueue(record);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Processor.TryProcessNext());

            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Boundary.TryCollect(out _), Is.False);
        }

        [Test]
        public void Process_ForeignRecord_PoisonsNoCompletion()
        {
            Harness h = new Harness();
            Harness other = new Harness();
            NvencSubmitToOutputRecord foreign = other.CreateSubmitted(1);
            h.Enqueue(foreign);

            Assert.Throws<InvalidOperationException>(() => h.Processor.TryProcessNext());

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            Assert.That(h.Boundary.TryCollect(out _), Is.False);
        }

        [Test]
        public void Process_NoDoubleCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Enqueue(record);

            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Processor.HasCurrentWork, Is.False);

            // Exactly one completion was enqueued.
            Assert.That(h.Boundary.TryCollect(out _), Is.True);
            Assert.That(h.Boundary.TryCollect(out _), Is.False);

            // The Submit-to-Output credit was returned at publish: re-running
            // the same record through a fresh processor poisons instead of
            // double-enqueueing a completion.
            Harness h2 = new Harness();
            h2.Enqueue(record);
            Assert.Throws<InvalidOperationException>(() => h2.Processor.TryProcessNext());
            Assert.That(h2.State.IsPoisoned, Is.True);
        }

        // -------------------------------------------------------------------
        // Occupancy and poison
        // -------------------------------------------------------------------

        [Test]
        public void Process_Collect_AllSlotsZero()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord a = h.CreateSubmitted(1);
            NvencSubmitToOutputRecord b = h.CreateFailedBeforeSubmit(
                2, NvencFailedBeforeSubmitReason.NvencSubmitFailed);
            h.Enqueue(a);
            h.Enqueue(b);

            Assert.That(h.Processor.TryProcessNext(), Is.True);
            Assert.That(h.Processor.TryProcessNext(), Is.True);

            Assert.That(h.Boundary.TryCollect(out _), Is.True);
            Assert.That(h.Boundary.TryCollect(out _), Is.True);

            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Process_Poisoned_NoProgress()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
            h.Enqueue(record);

            Assert.That(h.State.TryPoison(), Is.True);

            Assert.That(h.Processor.TryProcessNext(), Is.False);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
            Assert.That(h.Writer.AppendCount, Is.EqualTo(0));
            Assert.That(h.Boundary.TryCollect(out _), Is.False);
            Assert.That(h.Processor.HasCurrentWork, Is.False);
        }

        // -------------------------------------------------------------------
        // Shape / source audits
        // -------------------------------------------------------------------

        [Test]
        public void Processor_QueueCapacity8_NoExtraCollections()
        {
            Assert.That(new NvencFixedSpscQueue<NvencSubmitToOutputRecord>().Capacity, Is.EqualTo(8));

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputProcessor.cs"));

            string[] forbidden =
            {
                "new []", "new List", "new Dictionary", "new Queue", "new Stack", "new HashSet",
                "Enumerable", "System.Linq", ".ToList(", ".ToArray(", ".Select(", ".Where(",
                "Array.Sort", "OrderBy", "Reverse", "Peek", "Array.",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "Ordered Output Processor must not reference: " + word);
            }
        }

        [Test]
        public void Processor_TypeShape_SealedNonDisposable_ExactDependencies_SingleEntry()
        {
            Type type = typeof(NvencOrderedOutputProcessor);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(14));

            Type[] dependencyTypes =
            {
                typeof(NvencCaptureProcessState),
                typeof(NvencFixedSpscQueue<NvencSubmitToOutputRecord>),
                typeof(NvencSubmittedOutputCollector),
                typeof(NvencRunChunkSink),
                typeof(NvencFailedBeforeSubmitReleaseCoordinator),
                typeof(NvencSubmittedOutputAbandonRecoveryCoordinator),
                typeof(NvencFrameCompletionBoundary),
            };

            int dependencies = 0;
            foreach (FieldInfo field in fields)
            {
                if (Array.IndexOf(dependencyTypes, field.FieldType) >= 0)
                {
                    dependencies++;
                    Assert.That(field.IsInitOnly, Is.True, field.Name + " dependency must be readonly.");
                }
            }

            Assert.That(dependencies, Is.EqualTo(7), "The processor must hold exactly its seven dependencies.");

            // Single entry plus the three diagnostics.
            MethodInfo tryProcess = type.GetMethod(
                "TryProcessNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(tryProcess, Is.Not.Null);
            Assert.That(tryProcess.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(tryProcess.GetParameters().Length, Is.EqualTo(0));

            Assert.That(type.GetProperty("HasCurrentWork", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), Is.Not.Null);
            Assert.That(type.GetProperty("HasPendingWork", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), Is.Not.Null);
            Assert.That(type.GetProperty("IsRunAbandoned", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), Is.Not.Null);

            int exposedMethods = 0;
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!method.IsPrivate && !method.IsSpecialName)
                {
                    exposedMethods++;
                }
            }

            Assert.That(exposedMethods, Is.EqualTo(3),
                "The processor must expose only TryProcessNext and the two correlation predicates.");

            MethodInfo isCorrelated = type.GetMethod(
                "IsCorrelatedWith", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(isCorrelated, Is.Not.Null);
            Assert.That(isCorrelated.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(isCorrelated.GetParameters().Length, Is.EqualTo(3));

            MethodInfo isCorrelatedWithResources = type.GetMethod(
                "IsCorrelatedWithResources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(isCorrelatedWithResources, Is.Not.Null);
            Assert.That(isCorrelatedWithResources.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That(isCorrelatedWithResources.GetParameters().Length, Is.EqualTo(5));
        }

        [Test]
        public void Processor_Source_NoThreadTaskSleepIoNative()
        {
            string directory = RuntimeDirectory();
            string source = File.ReadAllText(Path.Combine(directory, "NvencOrderedOutputProcessor.cs"));

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "Thread.Sleep",
                "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word),
                    "Ordered Output Processor source must not reference: " + word);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            internal int CallCount;
            internal bool Result = true;
            internal int ResultLength = 1024;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim WaitForGateHeld;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                CallCount++;

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
            internal int AppendCount;
            internal NvencRunChunkAppendOutcome Outcome = NvencRunChunkAppendOutcome.Appended;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim WaitForGateHeld;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                AppendCount++;

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

        private sealed class Harness
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
        }
    }
}
