using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceLoggerSealGateTests
    {
        private const int SlotSealState = 0;
        private const int SlotActiveWriters = 1;
        private const int SlotMutableFailures = 2;
        private const int SlotSealedFailures = 3;
        private const int SlotPostSealAttempts = 4;
        private const int SlotCutoffClosed = 5;

        private static TraceEvent Event(int tag, long testRunId = 0)
        {
            return new TraceEvent { Timestamp = tag, TestRunId = testRunId, EventType = TraceEventType.None };
        }

        // --- Reflection helpers -------------------------------------------------

        private static ConstructorInfo GetCaptureLoggerCtor()
        {
            ConstructorInfo ctor = typeof(TraceLogger).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null, "Capture logger constructor not found.");
            return ctor;
        }

        private static TraceLogger CreateCaptureLogger(int historyCapacity, long testRunId)
        {
            return (TraceLogger)GetCaptureLoggerCtor().Invoke(new object[] { historyCapacity, testRunId });
        }

        private static Exception CaptureLoggerCtorException(int historyCapacity, long testRunId)
        {
            try
            {
                GetCaptureLoggerCtor().Invoke(new object[] { historyCapacity, testRunId });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static SealableTraceWriter GetCaptureRunWriter(TraceLogger logger)
        {
            PropertyInfo prop = typeof(TraceLogger).GetProperty("CaptureRunWriter", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, "CaptureRunWriter property not found.");
            return (SealableTraceWriter)prop.GetValue(logger);
        }

        private static MethodInfo GetSealMethod()
        {
            MethodInfo method = typeof(TraceLogger).GetMethod("SealAndDrainRunForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "SealAndDrainRunForFreeze method not found.");
            return method;
        }

        private static int Seal(TraceLogger logger, long testRunId, TraceFlightRecorder recorder)
        {
            return (int)GetSealMethod().Invoke(logger, new object[] { testRunId, recorder });
        }

        private static Exception SealException(TraceLogger logger, long testRunId, TraceFlightRecorder recorder)
        {
            try
            {
                GetSealMethod().Invoke(logger, new object[] { testRunId, recorder });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static int GetCount(TraceLogger logger, string name)
        {
            PropertyInfo prop = typeof(TraceLogger).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, name + " property not found.");
            return (int)prop.GetValue(logger);
        }

        private static ConstructorInfo GetRecorderCtor()
        {
            ConstructorInfo ctor = typeof(TraceFlightRecorder).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "Internal recorder constructor not found.");
            return ctor;
        }

        private static TraceFlightRecorder CreateRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            return (TraceFlightRecorder)GetRecorderCtor().Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
        }

        private static ConstructorInfo GetWriterCtor()
        {
            ConstructorInfo ctor = typeof(SealableTraceWriter).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(NativeQueue<TraceEvent>.ParallelWriter), typeof(NativeArray<int>), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null, "SealableTraceWriter constructor not found.");
            return ctor;
        }

        private static SealableTraceWriter CreateWriter(NativeQueue<TraceEvent>.ParallelWriter writer, NativeArray<int> gate, long testRunId)
        {
            return (SealableTraceWriter)GetWriterCtor().Invoke(new object[] { writer, gate, testRunId });
        }

        private static void SetGate(NativeArray<int> gate, int slot, int value)
        {
            gate[slot] = value;
        }

        private static NativeQueue<TraceEvent> GetQueue(TraceLogger logger)
        {
            FieldInfo field = typeof(TraceLogger).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_queue field not found.");
            return (NativeQueue<TraceEvent>)field.GetValue(logger);
        }

        private static NativeArray<int> GetGate(TraceLogger logger)
        {
            FieldInfo field = typeof(TraceLogger).GetField("_sealGate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_sealGate field not found.");
            return (NativeArray<int>)field.GetValue(logger);
        }

        private static Exception ThrownBy(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct SealableWriterEnqueueJob : IJob
        {
            public SealableTraceWriter Writer;
            public TraceEvent Event;
            public NativeArray<int> Result;

            public void Execute()
            {
                Result[0] = Writer.TryEnqueue(Event) ? 1 : 0;
            }
        }

        // --- Constructor and legacy compatibility ------------------------------

        [Test]
        public void CaptureConstructor_RejectsNonPositiveHistoryCapacity_ExactParamName()
        {
            ArgumentOutOfRangeException ex = (ArgumentOutOfRangeException)CaptureLoggerCtorException(0, 42);
            Assert.That(ex.ParamName, Is.EqualTo("historyCapacity"));

            ex = (ArgumentOutOfRangeException)CaptureLoggerCtorException(-1, 42);
            Assert.That(ex.ParamName, Is.EqualTo("historyCapacity"));
        }

        [Test]
        public void CaptureConstructor_RejectsNonPositiveTestRunId_ExactParamName()
        {
            ArgumentOutOfRangeException ex = (ArgumentOutOfRangeException)CaptureLoggerCtorException(4, 0);
            Assert.That(ex.ParamName, Is.EqualTo("testRunId"));

            ex = (ArgumentOutOfRangeException)CaptureLoggerCtorException(4, -5);
            Assert.That(ex.ParamName, Is.EqualTo("testRunId"));
        }

        [Test]
        public void Legacy_JobWriterAndEnqueue_Unchanged()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                NativeQueue<TraceEvent>.ParallelWriter writer = logger.JobWriter;
                writer.Enqueue(Event(1));
                logger.Enqueue(Event(2));

                Assert.That(logger.Drain(), Is.EqualTo(2));
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(1).Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void CaptureLogger_RejectsRawJobWriter()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                Assert.Throws<InvalidOperationException>(() => { _ = logger.JobWriter; });
            }
        }

        [Test]
        public void LegacyLogger_RejectsCaptureRunWriter()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                PropertyInfo prop = typeof(TraceLogger).GetProperty("CaptureRunWriter", BindingFlags.NonPublic | BindingFlags.Instance);
                Exception ex = CapturePropertyException(prop, logger);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            }
        }

        private static Exception CapturePropertyException(PropertyInfo prop, TraceLogger logger)
        {
            try
            {
                prop.GetValue(logger);
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        // --- Open-state enqueueing ---------------------------------------------

        [Test]
        public void MainThreadAndWriter_BothEnqueueWhenOpen()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                logger.Enqueue(Event(1, 42));
                Assert.That(writer.TryEnqueue(Event(2, 42)), Is.True);

                Assert.That(logger.Drain(), Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(1).Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void Writer_TestRunIdMismatch_RejectedWithoutResidue()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                Assert.That(writer.TryEnqueue(Event(1, 43)), Is.False);
                Assert.That(writer.TryEnqueue(Event(2, -1)), Is.False);

                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(0));
                Assert.That(GetCount(logger, "SealedTraceEnqueueFailureCount"), Is.EqualTo(0));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(0));
                Assert.That(logger.Drain(), Is.EqualTo(0));
            }
        }

        [Test]
        public void MainThread_TestRunIdMismatch_Throws()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                Assert.Throws<ArgumentException>(() => logger.Enqueue(Event(1, 43)));
            }
        }

        [Test]
        public void WriterCopies_ShareSealStateAndCounts()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                SealableTraceWriter writerA = GetCaptureRunWriter(logger);
                SealableTraceWriter writerB = GetCaptureRunWriter(logger);

                Assert.That(writerA.TryEnqueue(Event(1, 42)), Is.True);
                Assert.That(writerB.TryEnqueue(Event(2, 42)), Is.True);

                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(2));

                Assert.That(writerA.TryEnqueue(Event(3, 42)), Is.False);
                Assert.That(writerB.TryEnqueue(Event(4, 42)), Is.False);

                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(2));
            }
        }

        // --- Final drain content -----------------------------------------------

        [Test]
        public void EventsAcceptedBeforeSeal_LandInFinalDrain()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                logger.Enqueue(Event(1, 42));
                logger.Enqueue(Event(2, 42));
                writer.TryEnqueue(Event(3, 42));
                writer.TryEnqueue(Event(4, 42));
                writer.TryEnqueue(Event(5, 42));

                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(5));
                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(5));
                Assert.That(recorder.GetCapturedEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(recorder.GetCapturedEvent(4).Timestamp, Is.EqualTo(5));
            }
        }

        [Test]
        public void EventsAfterSeal_NotEnqueued()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                logger.Enqueue(Event(1, 42));
                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(1));

                Assert.That(writer.TryEnqueue(Event(999, 42)), Is.False);

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(1));
                Assert.That(recorder.GetCapturedEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(1));
            }
        }

        [Test]
        public void FinalDrain_OverflowAccounted()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 2, 1); // NormalPostRollCapacity == 1
                Assert.That(recorder.TryTrigger(), Is.True);

                for (int i = 1; i <= 5; i++)
                {
                    logger.Enqueue(Event(i, 42));
                }

                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(5));
                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(1));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(4));
            }
        }

        // --- Seal state and counters -------------------------------------------

        [Test]
        public void SealedTraceEnqueueFailureCount_FixedAtSeal()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                // Sealing is observed after the seal CAS, so these are accounted
                // as mutable failures only when racing; after Sealed they are
                // post-seal attempts. The sealed count is fixed by the seal.
                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(0));

                int sealedCount = GetCount(logger, "SealedTraceEnqueueFailureCount");

                for (int i = 0; i < 3; i++)
                {
                    Assert.That(writer.TryEnqueue(Event(100 + i, 42)), Is.False);
                }

                Assert.That(GetCount(logger, "SealedTraceEnqueueFailureCount"), Is.EqualTo(sealedCount));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(3));
            }
        }

        [Test]
        public void Rejections_NoDoubleCounting()
        {
            // Writer-protocol test with a directly controlled gate.
            using (NativeQueue<TraceEvent> queue = new NativeQueue<TraceEvent>(Allocator.Temp))
            using (NativeArray<int> gate = new NativeArray<int>(6, Allocator.Temp))
            {
                SealableTraceWriter writer = CreateWriter(queue.AsParallelWriter(), gate, 42);

                // Sealing with cutoff open -> mutable failures only.
                SetGate(gate, SlotSealState, 1);

                Assert.That(writer.TryEnqueue(Event(1, 42)), Is.False);
                Assert.That(gate[SlotMutableFailures], Is.EqualTo(1));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(0));

                Assert.That(writer.TryEnqueue(Event(2, 42)), Is.False);
                Assert.That(gate[SlotMutableFailures], Is.EqualTo(2));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(0));

                // Close the cutoff -> post-seal attempts only.
                SetGate(gate, SlotCutoffClosed, 1);

                Assert.That(writer.TryEnqueue(Event(3, 42)), Is.False);
                Assert.That(gate[SlotMutableFailures], Is.EqualTo(2));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(1));

                // Sealed -> post-seal attempts only.
                SetGate(gate, SlotSealState, 2);

                Assert.That(writer.TryEnqueue(Event(4, 42)), Is.False);
                Assert.That(gate[SlotMutableFailures], Is.EqualTo(2));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(2));

                Assert.That(gate[SlotActiveWriters], Is.EqualTo(0));
            }
        }

        [Test]
        public void Rejection_ActiveIncrementThenRecheckAfterSeal_Rejected()
        {
            using (NativeQueue<TraceEvent> queue = new NativeQueue<TraceEvent>(Allocator.Temp))
            using (NativeArray<int> gate = new NativeArray<int>(6, Allocator.Temp))
            {
                SealableTraceWriter writer = CreateWriter(queue.AsParallelWriter(), gate, 42);

                // Simulate a writer that already entered its active section
                // before the seal CAS, and a seal that then moved Open -> Sealing.
                SetGate(gate, SlotActiveWriters, 1);
                SetGate(gate, SlotSealState, 1);

                // The recheck now observes Sealing -> reject.
                Assert.That(writer.TryEnqueue(Event(1, 42)), Is.False);

                Assert.That(gate[SlotMutableFailures], Is.EqualTo(1));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(0));
                Assert.That(gate[SlotActiveWriters], Is.EqualTo(1));
            }
        }

        [Test]
        public void Counts_SaturateAtIntMaxValue()
        {
            using (NativeQueue<TraceEvent> queue = new NativeQueue<TraceEvent>(Allocator.Temp))
            using (NativeArray<int> gate = new NativeArray<int>(6, Allocator.Temp))
            {
                SealableTraceWriter writer = CreateWriter(queue.AsParallelWriter(), gate, 42);

                SetGate(gate, SlotSealState, 1);
                SetGate(gate, SlotMutableFailures, int.MaxValue);

                Assert.That(writer.TryEnqueue(Event(1, 42)), Is.False);
                Assert.That(gate[SlotMutableFailures], Is.EqualTo(int.MaxValue));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(0));

                SetGate(gate, SlotCutoffClosed, 1);
                SetGate(gate, SlotPostSealAttempts, int.MaxValue);

                Assert.That(writer.TryEnqueue(Event(2, 42)), Is.False);
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(int.MaxValue));
            }
        }

        [Test]
        public void Writer_EnqueueFailure_RestoresActiveCount_AndCountsMutableFailure()
        {
            NativeQueue<TraceEvent> queue = new NativeQueue<TraceEvent>(Allocator.Persistent);
            NativeArray<int> gate = new NativeArray<int>(6, Allocator.Persistent);
            try
            {
                SealableTraceWriter writer = CreateWriter(queue.AsParallelWriter(), gate, 42);

                queue.Dispose();

                Exception ex = ThrownBy(() => writer.TryEnqueue(Event(1, 42)));
                Assert.That(ex, Is.Not.Null);

                Assert.That(gate[SlotActiveWriters], Is.EqualTo(0));
                Assert.That(gate[SlotMutableFailures], Is.EqualTo(1));
                Assert.That(gate[SlotPostSealAttempts], Is.EqualTo(0));
            }
            finally
            {
                if (queue.IsCreated)
                {
                    queue.Dispose();
                }

                if (gate.IsCreated)
                {
                    gate.Dispose();
                }
            }
        }

        [Test]
        public void MainThread_EnqueueFailure_RestoresActiveCount_AndCountsMutableFailure()
        {
            TraceLogger logger = CreateCaptureLogger(4, 42);
            try
            {
                FieldInfo queueField = typeof(TraceLogger).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance);
                NativeQueue<TraceEvent> queue = (NativeQueue<TraceEvent>)queueField.GetValue(logger);
                queue.Dispose();
                queueField.SetValue(logger, queue);

                Exception ex = ThrownBy(() => logger.Enqueue(Event(1, 42)));
                Assert.That(ex, Is.Not.Null);

                NativeArray<int> gate = GetGate(logger);
                Assert.That(gate[SlotActiveWriters], Is.EqualTo(0));
                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(1));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(0));
            }
            finally
            {
                logger.Dispose();
            }
        }

        // --- Seal preconditions -------------------------------------------------

        [Test]
        public void Seal_RejectsLegacyLogger()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 5);
                Exception ex = SealException(logger, 1, recorder);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            }
        }

        [Test]
        public void Seal_RejectsWrongRunId()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Exception ex = SealException(logger, 43, recorder);
                Assert.That(ex, Is.TypeOf<ArgumentException>());

                ex = SealException(logger, 0, recorder);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
            }
        }

        [Test]
        public void Seal_RejectsNullRecorder()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                Exception ex = SealException(logger, 42, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            }
        }

        [Test]
        public void Seal_RejectsRecorderUsingDifferentLogger()
        {
            using (TraceLogger loggerA = CreateCaptureLogger(4, 42))
            using (TraceLogger loggerB = CreateCaptureLogger(4, 43))
            {
                TraceFlightRecorder recorder = CreateRecorder(loggerA, 10, 1);
                Exception ex = SealException(loggerB, 43, recorder);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
            }
        }

        [Test]
        public void Seal_RejectsRecorderWithoutReserve()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 0);
                Assert.That(recorder.TryTrigger(), Is.True);

                Exception ex = SealException(logger, 42, recorder);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            }
        }

        [Test]
        public void Seal_RejectsRecorderNotCapturingPostRoll()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                // Still Armed; not triggered.

                Exception ex = SealException(logger, 42, recorder);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            }
        }

        [Test]
        public void Seal_RejectsReseal()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(0));

                Exception ex = SealException(logger, 42, recorder);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            }
        }

        [Test]
        public void Seal_PrevalidationFailure_LeavesStateUnchanged()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                // Wrong run ID: pre-validation must not touch the seal state.
                Assert.That(SealException(logger, 43, recorder), Is.TypeOf<ArgumentException>());

                // The logger is still Open, so a subsequent valid seal works.
                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(0));
            }
        }

        [Test]
        public void Seal_RejectsOffThreadCall_BeforeCas_NoSideEffects()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                Exception offThreadError = null;
                Thread worker = new Thread(() =>
                {
                    offThreadError = SealException(logger, 42, recorder);
                });
                worker.IsBackground = true;
                worker.Start();
                worker.Join();

                Assert.That(offThreadError, Is.TypeOf<InvalidOperationException>());

                // No side effects: the run is still Open on the constructing thread.
                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(0));
            }
        }

        // --- Post-seal recorder invariants -------------------------------------

        [Test]
        public void AfterSeal_RecorderStaysCapturingPostRoll_AndReserveUnused()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 3);
                Assert.That(recorder.TryTrigger(), Is.True);

                for (int i = 1; i <= 4; i++)
                {
                    logger.Enqueue(Event(i, 42));
                }

                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(4));

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(4));
                Assert.That(recorder.CapturedPostRollCount, Is.LessThanOrEqualTo(recorder.NormalPostRollCapacity));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));
            }
        }

        // --- Disposal -----------------------------------------------------------

        [Test]
        public void Dispose_FreesOwnContainers_Idempotent()
        {
            TraceLogger logger = CreateCaptureLogger(4, 42);

            Assert.That(logger.IsCreated, Is.True);

            logger.Dispose();
            Assert.That(logger.IsCreated, Is.False);

            logger.Dispose();
            Assert.That(logger.IsCreated, Is.False);
        }

        // --- Concurrency --------------------------------------------------------

        [Test]
        public void Seal_WaitsForInFlightWriters_NoEventsLost()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 1000, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                const int attemptCount = 500;
                using (ManualResetEventSlim started = new ManualResetEventSlim(false))
                {
                    Exception workerError = null;
                    Thread worker = new Thread(() =>
                    {
                        try
                        {
                            started.Set();
                            for (int i = 0; i < attemptCount; i++)
                            {
                                writer.TryEnqueue(Event(1000 + i, 42));
                            }
                        }
                        catch (Exception ex)
                        {
                            workerError = ex;
                        }
                    });
                    worker.IsBackground = true;
                    worker.Start();

                    started.Wait();
                    int drained = Seal(logger, 42, recorder);

                    worker.Join();
                    Assert.That(workerError, Is.Null);

                    Assert.That(logger.Drain(), Is.EqualTo(0));

                    int sealedCount = GetCount(logger, "SealedTraceEnqueueFailureCount");
                    int postSealCount = GetCount(logger, "PostSealTraceEnqueueAttemptCount");

                    // Every attempt is accounted exactly once: accepted events
                    // drained, rejected events counted.
                    Assert.That(drained + sealedCount + postSealCount, Is.EqualTo(attemptCount));
                    Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(drained));
                }
            }
        }

        [Test]
        public void Seal_DoesNotCompleteBeforeInFlightWriterExits()
        {
            using (TraceLogger logger = CreateCaptureLogger(4, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                NativeArray<int> gate = GetGate(logger);

                logger.Enqueue(Event(1, 42));

                // Fix the active writer count at 1 to simulate an in-flight writer.
                gate[SlotActiveWriters] = 1;

                // A releaser thread deterministically observes Sealing and only
                // then releases the in-flight writer.
                Exception releaserError = null;
                bool observedSealing = false;
                Thread releaser = new Thread(() =>
                {
                    try
                    {
                        SpinWait spin = default;
                        while (gate[SlotSealState] == 0)
                        {
                            spin.SpinOnce();
                        }

                        observedSealing = true;
                        gate[SlotActiveWriters] = 0;
                    }
                    catch (Exception ex)
                    {
                        releaserError = ex;
                    }
                });
                releaser.IsBackground = true;
                releaser.Start();

                // The seal runs on the constructing thread (the main thread) and
                // must block until the releaser observes Sealing and releases the
                // in-flight writer.
                int drained = Seal(logger, 42, recorder);

                releaser.Join();
                Assert.That(releaserError, Is.Null);
                Assert.That(observedSealing, Is.True);
                Assert.That(drained, Is.EqualTo(1));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(1));
                Assert.That(logger.Drain(), Is.EqualTo(0));
            }
        }

        // --- Burst compatibility ----------------------------------------------

        [Test]
        public void BurstJob_EnqueueSucceedsWhenOpen()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                using (NativeArray<int> result = new NativeArray<int>(1, Allocator.TempJob))
                {
                    SealableWriterEnqueueJob job = new SealableWriterEnqueueJob
                    {
                        Writer = writer,
                        Event = Event(1, 42),
                        Result = result,
                    };

                    job.Schedule().Complete();

                    Assert.That(result[0], Is.EqualTo(1));
                }

                Assert.That(logger.Drain(), Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(0));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void BurstJob_RejectsWhenSealed()
        {
            using (TraceLogger logger = CreateCaptureLogger(8, 42))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);

                Assert.That(Seal(logger, 42, recorder), Is.EqualTo(0));

                SealableTraceWriter writer = GetCaptureRunWriter(logger);

                using (NativeArray<int> result = new NativeArray<int>(1, Allocator.TempJob))
                {
                    SealableWriterEnqueueJob job = new SealableWriterEnqueueJob
                    {
                        Writer = writer,
                        Event = Event(999, 42),
                        Result = result,
                    };

                    job.Schedule().Complete();

                    Assert.That(result[0], Is.EqualTo(0));
                }

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(1));
                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(0));
            }
        }
    }
}
