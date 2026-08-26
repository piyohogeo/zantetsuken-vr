using System;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRequestSchedulerTests
    {
        private static CaptureFrameRequest MakeRequest(long captureFrameId, int arrayIndex)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(
                    1000L + captureFrameId,
                    10L + captureFrameId,
                    20L,
                    1,
                    captureFrameId,
                    30L + captureFrameId,
                    40L,
                    50L,
                    60L,
                    70L,
                    80,
                    90L),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 4, 4),
                arrayIndex,
                CapturePixelFormat.Rgba32);
        }

        [Test]
        public void DropReason_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFrameDropReason);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetName(type, 0), Is.EqualTo(nameof(CaptureFrameDropReason.None)));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo(nameof(CaptureFrameDropReason.RequestQueueFull)));
            Assert.That((int)CaptureFrameDropReason.None, Is.EqualTo(0));
            Assert.That((int)CaptureFrameDropReason.RequestQueueFull, Is.EqualTo(1));
        }

        [Test]
        public void Scheduler_NullDependencies_Rejected()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRequestScheduler(null, observer));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRequestScheduler(queue, null));
            }
        }

        [Test]
        public void Scheduler_AvailableSlot_EnqueuesAndRecordsQueued()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                Assert.That(scheduler.TrySchedule(MakeRequest(1, 0)), Is.True);

                Assert.That(scheduler.Count, Is.EqualTo(1));
                Assert.That(scheduler.TotalAccepted, Is.EqualTo(1));
                Assert.That(scheduler.TotalRejected, Is.EqualTo(0));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest r), Is.True);
                Assert.That(r.TraceContext.CaptureFrameId, Is.EqualTo(1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
            }
        }

        [Test]
        public void Scheduler_Full_PreservesQueueAndIncrementsRejected()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                scheduler.TrySchedule(MakeRequest(1, 0));
                scheduler.TrySchedule(MakeRequest(2, 1));

                Assert.That(scheduler.TrySchedule(MakeRequest(3, 2)), Is.False);

                // Queue contents and order are unchanged.
                Assert.That(scheduler.Count, Is.EqualTo(2));
                Assert.That(scheduler.TotalAccepted, Is.EqualTo(2));
                Assert.That(scheduler.TotalRejected, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest a), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFrameRequest b), Is.True);
                Assert.That(a.TraceContext.CaptureFrameId, Is.EqualTo(1));
                Assert.That(b.TraceContext.CaptureFrameId, Is.EqualTo(2));
                Assert.That(queue.TryDequeue(out _), Is.False);
            }
        }

        [Test]
        public void Scheduler_FullRejection_RecordsDroppedWithCorrelation()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(1);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                scheduler.TrySchedule(MakeRequest(10, 0)); // fills the single slot
                Assert.That(scheduler.TrySchedule(MakeRequest(77, 1)), Is.False);

                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                TraceEvent queued = logger.GetHistoryEvent(0);
                TraceEvent dropped = logger.GetHistoryEvent(1);

                Assert.That(queued.EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(dropped.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(dropped.Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
                Assert.That(dropped.Reason, Is.EqualTo(TraceReason.None));

                // Correlation values of the dropped event match the rejected request.
                Assert.That(dropped.CaptureFrameId, Is.EqualTo(77));
                Assert.That(dropped.Timestamp, Is.EqualTo(1000L + 77));
                Assert.That(dropped.FrameId, Is.EqualTo(10L + 77));
                Assert.That(dropped.OpenXRFrameId, Is.EqualTo(30L + 77));
            }
        }

        [Test]
        public void Scheduler_ConsecutiveAcceptedRejected_CountersAndTraceMatch()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                Assert.That(scheduler.TrySchedule(MakeRequest(1, 0)), Is.True);
                Assert.That(scheduler.TrySchedule(MakeRequest(2, 0)), Is.True);
                Assert.That(scheduler.TrySchedule(MakeRequest(3, 0)), Is.False); // rejected
                Assert.That(scheduler.TrySchedule(MakeRequest(4, 0)), Is.False); // rejected

                logger.Drain();

                Assert.That(scheduler.TotalAccepted, Is.EqualTo(2));
                Assert.That(scheduler.TotalRejected, Is.EqualTo(2));
                Assert.That(logger.HistoryCount, Is.EqualTo(4));

                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(2).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(3).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
            }
        }

        [Test]
        public void Scheduler_DoesNotAutoDrain()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                scheduler.TrySchedule(MakeRequest(1, 0));

                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(scheduler.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void Scheduler_InvalidRequest_NoChanges()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                Assert.Throws<ArgumentException>(() => scheduler.TrySchedule(default));

                Assert.That(scheduler.Count, Is.EqualTo(0));
                Assert.That(scheduler.TotalAccepted, Is.EqualTo(0));
                Assert.That(scheduler.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Scheduler_DisposedLogger_AvailablePath_ThrowsWithoutModifyingQueue()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

            logger.Dispose();

            Assert.Throws<ObjectDisposedException>(() => scheduler.TrySchedule(MakeRequest(1, 0)));

            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.TotalAccepted, Is.EqualTo(0));
            Assert.That(queue.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void Scheduler_DisposedLogger_FullPath_ThrowsWithoutModifyingQueue()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(1);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

            scheduler.TrySchedule(MakeRequest(1, 0)); // fills the single slot
            logger.Dispose();

            Assert.Throws<ObjectDisposedException>(() => scheduler.TrySchedule(MakeRequest(2, 0)));

            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.TotalAccepted, Is.EqualTo(1));
            Assert.That(queue.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void Scheduler_OwnerCanContinueUsingQueueAndLogger()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler scheduler = new CaptureFrameRequestScheduler(queue, observer);

                scheduler.TrySchedule(MakeRequest(1, 0));
                logger.Drain();

                // Owner uses the queue directly.
                Assert.That(queue.TryDequeue(out CaptureFrameRequest r), Is.True);
                Assert.That(r.TraceContext.CaptureFrameId, Is.EqualTo(1));

                // Owner uses the logger directly afterwards.
                observer.RecordQueued(MakeRequest(2, 0).TraceContext);
                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.IsCreated, Is.True);
            }
        }
    }
}
