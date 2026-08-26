using System;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceFlightRecorderTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
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
        public void Constructor_RejectsNullLogger()
        {
            Assert.Throws<ArgumentNullException>(() => new TraceFlightRecorder(null, 0));
        }

        [Test]
        public void Constructor_RejectsNegativePostRollCapacity()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new TraceFlightRecorder(logger, -1));
            }
        }

        [Test]
        public void Constructor_RejectsCapacityOverflow()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new TraceFlightRecorder(logger, int.MaxValue));
            }
        }

        [Test]
        public void Armed_Drain_DrainsLoggerNormally()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Enqueue(Event(3));

                int drained = recorder.Drain();

                Assert.That(drained, Is.EqualTo(3));
                Assert.That(logger.HistoryCount, Is.EqualTo(3));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Armed));
                Assert.That(recorder.CapturedCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void TryTrigger_SnapshotsUndrainedAnomalyEvent()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();

                logger.Enqueue(Event(3)); // anomaly, not yet drained

                Assert.That(recorder.TryTrigger(), Is.True);

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(recorder.TriggerHistoryCount, Is.EqualTo(3));
                Assert.That(recorder.CapturedCount, Is.EqualTo(3));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 1, 2, 3 }));
            }
        }

        [Test]
        public void TryTrigger_WrappedHistory_PreservesOldestFirst()
        {
            using (TraceLogger logger = new TraceLogger(3))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                for (int i = 1; i <= 5; i++)
                {
                    logger.Enqueue(Event(i));
                }

                logger.Drain(); // history now holds 3, 4, 5; 1 and 2 overwritten

                Assert.That(recorder.TryTrigger(), Is.True);

                Assert.That(recorder.WasHistoryOverwrittenAtTrigger, Is.True);
                Assert.That(recorder.TriggerHistoryCount, Is.EqualTo(3));
                Assert.That(recorder.CapturedCount, Is.EqualTo(3));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 3, 4, 5 }));
            }
        }

        [Test]
        public void WasHistoryOverwrittenAtTrigger_IsFalseWithoutWrap()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();

                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.WasHistoryOverwrittenAtTrigger, Is.False);
            }
        }

        [Test]
        public void PostRoll_CapturesOnlySpecifiedCount_AndDrainsAllFromLogger()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 3);

                logger.Enqueue(Event(1));
                logger.Drain();

                Assert.That(recorder.TryTrigger(), Is.True);

                for (int i = 2; i <= 6; i++)
                {
                    logger.Enqueue(Event(i));
                }

                int drained = recorder.Drain();

                // All 5 queued events reach the logger history; only 3 fit in
                // the remaining post-roll slots.
                Assert.That(drained, Is.EqualTo(5));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(3));
                Assert.That(recorder.CapturedCount, Is.EqualTo(4));
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 1, 2, 3, 4 }));

                Assert.That(logger.TotalWritten, Is.EqualTo(6));
                Assert.That(logger.OverwrittenCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void PostRoll_Full_AutoFreezes()
        {
            using (TraceLogger logger = new TraceLogger(2))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(10));
                logger.Drain();
                Assert.That(recorder.TryTrigger(), Is.True);

                logger.Enqueue(Event(20));
                logger.Enqueue(Event(30));

                recorder.Drain();

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(2));
                Assert.That(recorder.CapturedCount, Is.EqualTo(3));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 10, 20, 30 }));
            }
        }

        [Test]
        public void Freeze_Manual_StopsPostRollEarly()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 5);

                logger.Enqueue(Event(1));
                logger.Drain();
                Assert.That(recorder.TryTrigger(), Is.True);

                logger.Enqueue(Event(2));
                recorder.Drain(); // captures 1 post-roll event

                Assert.That(recorder.Freeze(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(1));
                Assert.That(recorder.CapturedCount, Is.EqualTo(2));

                logger.Enqueue(Event(3));
                recorder.Drain();

                Assert.That(recorder.CapturedCount, Is.EqualTo(2)); // unchanged after freeze
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 1, 2 }));
            }
        }

        [Test]
        public void PostRollCapacityZero_ImmediatelyFrozen()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Drain();

                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(0));
                Assert.That(recorder.CapturedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Frozen_Drain_LoggerStillDrains_CaptureUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Drain();
                Assert.That(recorder.TryTrigger(), Is.True);

                logger.Enqueue(Event(2));
                logger.Enqueue(Event(3));

                int drained = recorder.Drain();

                Assert.That(drained, Is.EqualTo(2));
                Assert.That(logger.HistoryCount, Is.EqualTo(3));
                Assert.That(recorder.CapturedCount, Is.EqualTo(1));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 1 }));
            }
        }

        [Test]
        public void DoubleTriggerAndFreeze_DoNotModifyCapture()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Drain();
                Assert.That(recorder.TryTrigger(), Is.True);

                Assert.That(recorder.TryTrigger(), Is.False);
                Assert.That(recorder.Freeze(), Is.False);

                Assert.That(recorder.CapturedCount, Is.EqualTo(1));
                Assert.That(CapturedTimestamps(recorder), Is.EqualTo(new long[] { 1 }));
            }
        }

        [Test]
        public void Reset_ReArms_WithoutTouchingLogger()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(1));
                logger.Drain();
                Assert.That(recorder.TryTrigger(), Is.True);

                long totalWrittenBefore = logger.TotalWritten;
                int historyCountBefore = logger.HistoryCount;

                recorder.Reset();

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Armed));
                Assert.That(recorder.CapturedCount, Is.EqualTo(0));
                Assert.That(recorder.TriggerHistoryCount, Is.EqualTo(0));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(0));
                Assert.That(recorder.WasHistoryOverwrittenAtTrigger, Is.False);

                Assert.That(logger.IsCreated, Is.True);
                Assert.That(logger.TotalWritten, Is.EqualTo(totalWrittenBefore));
                Assert.That(logger.HistoryCount, Is.EqualTo(historyCountBefore));
            }
        }

        [Test]
        public void GetCapturedEvent_RejectsOutOfRange()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.GetCapturedEvent(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.GetCapturedEvent(-1));

                logger.Enqueue(Event(1));
                logger.Drain();
                recorder.TryTrigger();

                Assert.That(recorder.GetCapturedEvent(0).Timestamp, Is.EqualTo(1));
                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.GetCapturedEvent(1));
                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.GetCapturedEvent(-1));
            }
        }

        [Test]
        public void CopyCapturedTo_RejectsInvalidDestination()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                Assert.Throws<ArgumentNullException>(() => recorder.CopyCapturedTo(null, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => recorder.CopyCapturedTo(new TraceEvent[4], -1));
                Assert.Throws<ArgumentException>(() => recorder.CopyCapturedTo(new TraceEvent[1], 0));

                TraceEvent[] destination = new TraceEvent[4];
                recorder.CopyCapturedTo(destination, 1);

                Assert.That(destination[1].Timestamp, Is.EqualTo(1));
                Assert.That(destination[2].Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void Recorder_DoesNotDisposeLogger()
        {
            TraceLogger logger = new TraceLogger(4);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(1));
                logger.Drain();
                recorder.TryTrigger();
                recorder.Drain();
                recorder.Reset();

                Assert.That(logger.IsCreated, Is.True);
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void DisposedLogger_RecorderOperationsThrow()
        {
            TraceLogger logger = new TraceLogger(4);
            logger.Dispose();

            Assert.Throws<ObjectDisposedException>(() => new TraceFlightRecorder(logger, 0));
        }

        [Test]
        public void DisposedLogger_AfterConstruction_DrainThrows()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

            logger.Enqueue(Event(1));
            logger.Dispose();

            Assert.Throws<ObjectDisposedException>(() => recorder.Drain());
            Assert.Throws<ObjectDisposedException>(() => recorder.TryTrigger());
        }

        [Test]
        public void PublicTraceLoggerDrain_StillBehavesAsBefore()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));

                int drained = logger.Drain();

                Assert.That(drained, Is.EqualTo(2));
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(1).Timestamp, Is.EqualTo(2));
            }
        }
    }
}
