using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceCaptureSnapshotTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static long[] SnapshotTimestamps(TraceCaptureSnapshot snapshot)
        {
            long[] timestamps = new long[snapshot.EventCount];
            for (int i = 0; i < timestamps.Length; i++)
            {
                timestamps[i] = snapshot.GetEvent(i).Timestamp;
            }

            return timestamps;
        }

        [Test]
        public void Armed_RejectsSnapshot()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                Assert.Throws<InvalidOperationException>(() => recorder.CreateFrozenSnapshot());
            }
        }

        [Test]
        public void CapturingPostRoll_RejectsSnapshot()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(1));
                logger.Drain();
                Assert.That(recorder.TryTrigger(), Is.True);
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));

                Assert.Throws<InvalidOperationException>(() => recorder.CreateFrozenSnapshot());
            }
        }

        [Test]
        public void FrozenWithZeroPostRoll_EmptySnapshot()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                Assert.That(recorder.TryTrigger(), Is.True); // empty history, postRoll 0
                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.EventCount, Is.EqualTo(0));
                Assert.That(snapshot.TriggerHistoryCount, Is.EqualTo(0));
                Assert.That(snapshot.CapturedPostRollCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void TriggerHistoryOnly_Snapshot()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.EventCount, Is.EqualTo(2));
                Assert.That(snapshot.TriggerHistoryCount, Is.EqualTo(2));
                Assert.That(snapshot.CapturedPostRollCount, Is.EqualTo(0));
                Assert.That(SnapshotTimestamps(snapshot), Is.EqualTo(new long[] { 1, 2 }));
            }
        }

        [Test]
        public void AutoFreeze_TriggerPlusPostRollOrder()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);

                logger.Enqueue(Event(10));
                logger.Drain();
                recorder.TryTrigger();

                logger.Enqueue(Event(20));
                logger.Enqueue(Event(30));
                recorder.Drain();

                Assert.That(recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.EventCount, Is.EqualTo(3));
                Assert.That(snapshot.TriggerHistoryCount, Is.EqualTo(1));
                Assert.That(snapshot.CapturedPostRollCount, Is.EqualTo(2));
                Assert.That(SnapshotTimestamps(snapshot), Is.EqualTo(new long[] { 10, 20, 30 }));
            }
        }

        [Test]
        public void ManualFreeze_PostRollBelowCapacity_Snapshot()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 5);

                logger.Enqueue(Event(1));
                logger.Drain();
                recorder.TryTrigger();

                logger.Enqueue(Event(2));
                recorder.Drain(); // captures 1 post-roll event

                Assert.That(recorder.Freeze(), Is.True);

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.EventCount, Is.EqualTo(2));
                Assert.That(snapshot.TriggerHistoryCount, Is.EqualTo(1));
                Assert.That(snapshot.CapturedPostRollCount, Is.EqualTo(1));
                Assert.That(SnapshotTimestamps(snapshot), Is.EqualTo(new long[] { 1, 2 }));
            }
        }

        [Test]
        public void WrappedHistory_WasHistoryOverwrittenAtTrigger()
        {
            using (TraceLogger logger = new TraceLogger(3))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                for (int i = 1; i <= 5; i++)
                {
                    logger.Enqueue(Event(i));
                }

                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.WasHistoryOverwrittenAtTrigger, Is.True);
                Assert.That(snapshot.EventCount, Is.EqualTo(3));
                Assert.That(SnapshotTimestamps(snapshot), Is.EqualTo(new long[] { 3, 4, 5 }));
            }
        }

        [Test]
        public void Snapshot_MetadataValues()
        {
            using (TraceLogger logger = new TraceLogger(2))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 1);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Enqueue(Event(3));
                logger.Drain(); // history wraps to [2, 3]

                recorder.TryTrigger(); // triggerHistory=2, CapturingPostRoll
                logger.Enqueue(Event(4));
                recorder.Drain(); // captures 1 post-roll event, auto-freeze

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.EventCount, Is.EqualTo(3));
                Assert.That(snapshot.TriggerHistoryCount, Is.EqualTo(2));
                Assert.That(snapshot.CapturedPostRollCount, Is.EqualTo(1));
                Assert.That(snapshot.WasHistoryOverwrittenAtTrigger, Is.True);
            }
        }

        [Test]
        public void Snapshot_Invariant_EventCountEqualsTriggerPlusPostRoll()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 3);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                logger.Enqueue(Event(3));
                logger.Enqueue(Event(4));
                logger.Enqueue(Event(5));
                recorder.Drain();
                recorder.Freeze();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.That(snapshot.EventCount, Is.EqualTo(snapshot.TriggerHistoryCount + snapshot.CapturedPostRollCount));
            }
        }

        [Test]
        public void Snapshot_ResetDoesNotMutate()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                long[] before = SnapshotTimestamps(snapshot);

                recorder.Reset();

                Assert.That(SnapshotTimestamps(snapshot), Is.EqualTo(before));
            }
        }

        [Test]
        public void Snapshot_RetriggerDoesNotMutate()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                recorder.Reset();
                logger.Enqueue(Event(99));
                logger.Drain();
                recorder.TryTrigger(); // re-trigger with a different event

                Assert.That(snapshot.EventCount, Is.EqualTo(1));
                Assert.That(snapshot.GetEvent(0).Timestamp, Is.EqualTo(1));
            }
        }

        [Test]
        public void Snapshot_LoggerDrainDoesNotMutate()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                logger.Enqueue(Event(2));
                logger.Enqueue(Event(3));
                recorder.Drain(); // logger drains; capture is frozen

                Assert.That(snapshot.EventCount, Is.EqualTo(1));
                Assert.That(snapshot.GetEvent(0).Timestamp, Is.EqualTo(1));
            }
        }

        [Test]
        public void Snapshot_AfterLoggerDispose()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

            logger.Enqueue(Event(1));
            logger.Drain();
            recorder.TryTrigger();

            logger.Dispose();

            TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

            Assert.That(snapshot.EventCount, Is.EqualTo(1));
            Assert.That(snapshot.GetEvent(0).Timestamp, Is.EqualTo(1));
        }

        [Test]
        public void MultipleSnapshots_AreIndependent()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot first = recorder.CreateFrozenSnapshot();
                TraceCaptureSnapshot second = recorder.CreateFrozenSnapshot();

                TraceEvent[] buffer = new TraceEvent[1];
                first.CopyEventsTo(buffer, 0);
                buffer[0].Timestamp = 999; // mutate caller's buffer

                Assert.That(first.GetEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(second.GetEvent(0).Timestamp, Is.EqualTo(1));
            }
        }

        [Test]
        public void GetEvent_RejectsNegativeAndUpperBoundary()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetEvent(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetEvent(2));
                Assert.That(snapshot.GetEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(snapshot.GetEvent(1).Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void CopyEventsTo_Null_Negative_Insufficient_OffsetSuccess()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();

                Assert.Throws<ArgumentNullException>(() => snapshot.CopyEventsTo(null, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.CopyEventsTo(new TraceEvent[4], -1));
                Assert.Throws<ArgumentException>(() => snapshot.CopyEventsTo(new TraceEvent[1], 0));

                TraceEvent[] destination = new TraceEvent[4];
                snapshot.CopyEventsTo(destination, 1);

                Assert.That(destination[1].Timestamp, Is.EqualTo(1));
                Assert.That(destination[2].Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void CopyEventsTo_EmptySnapshot_AtArrayEnd()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                recorder.TryTrigger(); // empty frozen

                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                Assert.That(snapshot.EventCount, Is.EqualTo(0));

                TraceEvent[] destination = new TraceEvent[3];
                Assert.DoesNotThrow(() => snapshot.CopyEventsTo(destination, 3));
            }
        }

        [Test]
        public void Snapshot_HasNoMutablePublicApi()
        {
            Type type = typeof(TraceCaptureSnapshot);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, "Public field exposes an array: " + field.Name);
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.CanWrite, Is.False, "Public property has a setter: " + property.Name);
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType.IsArray, Is.False, "Public method returns an array: " + method.Name);
            }

            Assert.That(type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance), Is.Null);
        }

        [Test]
        public void SnapshotCreation_DoesNotChangeRecorderOrLoggerState()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);

                logger.Enqueue(Event(1));
                logger.Enqueue(Event(2));
                logger.Drain();
                recorder.TryTrigger();

                TraceFlightRecorderState stateBefore = recorder.State;
                int triggerBefore = recorder.TriggerHistoryCount;
                int postBefore = recorder.CapturedPostRollCount;
                bool overwrittenBefore = recorder.WasHistoryOverwrittenAtTrigger;
                int capturedBefore = recorder.CapturedCount;
                int historyBefore = logger.HistoryCount;
                long totalBefore = logger.TotalWritten;
                long overwrittenLoggerBefore = logger.OverwrittenCount;

                recorder.CreateFrozenSnapshot();

                Assert.That(recorder.State, Is.EqualTo(stateBefore));
                Assert.That(recorder.TriggerHistoryCount, Is.EqualTo(triggerBefore));
                Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(postBefore));
                Assert.That(recorder.WasHistoryOverwrittenAtTrigger, Is.EqualTo(overwrittenBefore));
                Assert.That(recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(logger.HistoryCount, Is.EqualTo(historyBefore));
                Assert.That(logger.TotalWritten, Is.EqualTo(totalBefore));
                Assert.That(logger.OverwrittenCount, Is.EqualTo(overwrittenLoggerBefore));
            }
        }
    }
}
