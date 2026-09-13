using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceFlightRecorderFreezeReserveTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static ConstructorInfo GetInternalCtor()
        {
            ConstructorInfo ctor = typeof(TraceFlightRecorder).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "Internal constructor not found.");
            return ctor;
        }

        private static TraceFlightRecorder CreateRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            return (TraceFlightRecorder)GetInternalCtor().Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
        }

        private static Exception CtorException(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            try
            {
                GetInternalCtor().Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static string RangeParamName(Exception exception)
        {
            return ((ArgumentOutOfRangeException)exception).ParamName;
        }

        [Test]
        public void PublicConstructor_LegacyReserveZero()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 5);

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(0));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(5));
                Assert.That(recorder.PostRollCapacity, Is.EqualTo(5));
            }
        }

        [Test]
        public void Legacy_ImmediateFreeze_WhenPostRollZero()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void Legacy_AutoFreeze_WhenPostRollFull()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 2; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(2));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void Legacy_ManualFreeze_Works()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                logger.Enqueue(Event(1));
                recorder.Drain();

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(recorder.Freeze(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void InternalConstructor_InvalidValues_ParamName()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                Assert.That(CtorException(null, 0, 0), Is.InstanceOf<ArgumentNullException>());
                Assert.That(RangeParamName(CtorException(logger, -1, 0)), Is.EqualTo("postRollCapacity"));
                Assert.That(RangeParamName(CtorException(logger, 4, -1)), Is.EqualTo("freezeTerminalTraceReserve"));
                Assert.That(RangeParamName(CtorException(logger, 4, 5)), Is.EqualTo("freezeTerminalTraceReserve"));
                Assert.That(RangeParamName(CtorException(logger, int.MaxValue, 0)), Is.EqualTo("postRollCapacity"));
            }
        }

        [Test]
        public void ReserveRecorder_StopsAtNormalPostRollCapacity()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2); // NormalPostRollCapacity == 3

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(2));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(3));

                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));

                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(10));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));

                for (int i = 11; i <= 15; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(5));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            }
        }

        [Test]
        public void ReserveRecorder_Freeze_ReturnsFalse_Unchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                logger.Enqueue(Event(1));
                recorder.Drain();

                int capturedBefore = recorder.CapturedCount;
                int postRollBefore = recorder.CapturedPostRollCount;

                Assert.That(recorder.Freeze(), Is.False);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore));
            }
        }

        [Test]
        public void ReserveRecorder_NormalPostRollZero_StaysCapturingPostRoll()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 2, 2); // NormalPostRollCapacity == 0

                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(0));

                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            }
        }

        [Test]
        public void Reset_KeepsCapacityValues()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                logger.Enqueue(Event(1));
                recorder.Drain();

                recorder.Reset();

                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(2));
                Assert.That(recorder.PostRollCapacity, Is.EqualTo(5));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Armed));
                Assert.That(recorder.CapturedCount, Is.EqualTo(0));
            }
        }
    }
}
