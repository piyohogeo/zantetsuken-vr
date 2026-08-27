using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceFlightRecorderOverflowTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceFlightRecorder CreateRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            ConstructorInfo ctor = typeof(TraceFlightRecorder).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "Internal constructor not found.");
            return (TraceFlightRecorder)ctor.Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
        }

        private static int SaturatingAdd(int current, int delta)
        {
            MethodInfo method = typeof(TraceFlightRecorder).GetMethod("SaturatingAdd", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "SaturatingAdd helper not found.");
            return (int)method.Invoke(null, new object[] { current, delta });
        }

        private static long[] CapturedTimestamps(TraceFlightRecorder recorder)
        {
            long[] timestamps = new long[recorder.CapturedCount];
            for (int i = 0; i < timestamps.Length; i++)
            {
                timestamps[i] = recorder.GetCapturedEvent(i).Timestamp;
            }

            return timestamps;
        }

        [Test]
        public void InitialValue_Zero()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void WithinCapacity_Zero()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2); // NormalPostRollCapacity == 3

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 3; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void OneDrainExceedingCapacity_ExactDelta()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2); // NormalPostRollCapacity == 3

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(10));

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(7));
            }
        }

        [Test]
        public void AfterFull_AdditionalDrains_Accumulate()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain(); // overflow 7

                for (int i = 11; i <= 15; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain(); // all 5 overflow

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(12));
            }
        }

        [Test]
        public void NormalPostRollZero_AllDrainedAdded()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 2, 2); // NormalPostRollCapacity == 0

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 5; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(5));

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(0));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(5));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            }
        }

        [Test]
        public void ReserveRecorder_StaysCapturingPostRoll_AfterOverflow()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();

                Assert.That(recorder.TraceCaptureOverflowCount, Is.GreaterThan(0));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            }
        }

        [Test]
        public void Legacy_CountsOverflow_AndFreezes()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 3);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.Drain(), Is.EqualTo(10));

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(7));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void ArmedAndFrozen_NoIncrease()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 3);

                logger.Enqueue(Event(1));
                recorder.Drain(); // Armed

                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 3; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain(); // fills and freezes

                logger.Enqueue(Event(99));
                recorder.Drain(); // Frozen

                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void TryTrigger_PreTriggerDrain_NotCounted()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                for (int i = 1; i <= 5; i++)
                {
                    logger.Enqueue(Event(i));
                }

                Assert.That(recorder.TryTrigger(), Is.True);

                Assert.That(recorder.TriggerHistoryCount, Is.EqualTo(5));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Reset_ZeroesOverflow_KeepsCapacities()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();
                Assert.That(recorder.TraceCaptureOverflowCount, Is.GreaterThan(0));

                recorder.Reset();

                Assert.That(recorder.TraceCaptureOverflowCount, Is.EqualTo(0));
                Assert.That(recorder.FreezeTerminalTraceReserve, Is.EqualTo(2));
                Assert.That(recorder.PostRollCapacity, Is.EqualTo(5));
                Assert.That(recorder.NormalPostRollCapacity, Is.EqualTo(3));
            }
        }

        [Test]
        public void SaturatingAdd_SaturatesAtIntMax()
        {
            Assert.That(SaturatingAdd(10, 20), Is.EqualTo(30));
            Assert.That(SaturatingAdd(0, int.MaxValue), Is.EqualTo(int.MaxValue));
            Assert.That(SaturatingAdd(int.MaxValue, 0), Is.EqualTo(int.MaxValue));
            Assert.That(SaturatingAdd(int.MaxValue, 1), Is.EqualTo(int.MaxValue));
            Assert.That(SaturatingAdd(int.MaxValue - 1, 5), Is.EqualTo(int.MaxValue));
            Assert.That(SaturatingAdd(int.MaxValue, int.MaxValue), Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void Overflow_DoesNotChangeCapture()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 5, 2);

                Assert.That(recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= 3; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();

                int capturedBefore = recorder.CapturedCount;
                long[] before = CapturedTimestamps(recorder);

                for (int i = 4; i <= 10; i++)
                {
                    logger.Enqueue(Event(i));
                }

                recorder.Drain();

                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(before));
                Assert.That(recorder.TraceCaptureOverflowCount, Is.GreaterThan(0));
            }
        }
    }
}
