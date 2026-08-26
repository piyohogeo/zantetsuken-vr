using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRecordSchedulerTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunManifest MakeManifest(long testRunId = 1)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                "build-1",
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));

            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                logger.Enqueue(Event(1));
                recorder.TryTrigger();
                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                return TraceRunManifest.Create(snapshot, context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long unityFrameIdOffset = 0)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1000L + captureFrameId,
                10L + captureFrameId + unityFrameIdOffset,
                20L,
                1,
                captureFrameId,
                30L + captureFrameId,
                1L,
                50L,
                60L,
                70L,
                80u,
                90L);

            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 4, 4),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTraceContext MakeContext()
        {
            return new CaptureFrameTraceContext(12345, 100, 200, 3, 55, 77, 99, 11, 22, 33, 44, 66);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(1.0, 1.0 / 90.0, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureFrameRequest request)
        {
            TraceRunManifest manifest = MakeManifest(request.TraceContext.TestRunId);
            CaptureRunReference run = new CaptureRunReference(
                manifest,
                100,
                5,
                TraceRunManifestCodec.ComputeContentSha256(manifest));

            return new CaptureFrameRecord(
                run,
                request,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        private static CaptureFrameRecordScheduler MakeScheduler(
            int queueCapacity,
            int registryCapacity,
            TraceLogger logger,
            out CaptureFrameRequestQueue queue,
            out CaptureFrameRecordRegistry registry,
            out CaptureFrameRequestScheduler requestScheduler)
        {
            queue = new CaptureFrameRequestQueue(queueCapacity);
            registry = new CaptureFrameRecordRegistry(registryCapacity);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
            return new CaptureFrameRecordScheduler(requestScheduler, registry, observer);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);

                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordScheduler(null, registry, observer));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordScheduler(requestScheduler, null, observer));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordScheduler(requestScheduler, registry, null));
            }
        }

        [Test]
        public void NullRecord_RejectedStateUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);

                Assert.Throws<ArgumentNullException>(() => scheduler.TrySchedule(null));

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(registry.TotalAccepted, Is.EqualTo(0));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Success_QueueAndRegistryFilled_QueuedTraceOnce()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record = MakeRecord(MakeRequest(10));

                Assert.That(scheduler.TrySchedule(record), Is.True);

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(queue.TryDequeue(out CaptureFrameRequest dequeued), Is.True);
                Assert.That(dequeued.TraceContext.CaptureFrameId, Is.EqualTo(10));

                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));
                Assert.That(registry.TryGet(record.Request, out CaptureFrameRecord fetched), Is.True);
                Assert.That(fetched, Is.SameAs(record));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
            }
        }

        [Test]
        public void SameRecordReRegister_Throws_NoSecondQueueEntry()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record = MakeRecord(MakeRequest(10));

                Assert.That(scheduler.TrySchedule(record), Is.True);

                Assert.Throws<ArgumentException>(() => scheduler.TrySchedule(record));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void DuplicateId_SameInstance_WhenQueueFull_ArgumentException_NoSideEffects()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(1, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record = MakeRecord(MakeRequest(10));

                Assert.That(scheduler.TrySchedule(record), Is.True); // queue is now full (capacity 1)

                Assert.Throws<ArgumentException>(() => scheduler.TrySchedule(record));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
            }
        }

        [Test]
        public void DuplicateId_DifferentInstanceSameRequest_ArgumentException_NoSideEffects()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord first = MakeRecord(MakeRequest(10));
                CaptureFrameRecord second = MakeRecord(MakeRequest(10)); // distinct instance, identical request

                Assert.That(scheduler.TrySchedule(first), Is.True);
                Assert.Throws<ArgumentException>(() => scheduler.TrySchedule(second));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void DuplicateId_DifferentRequestFields_InvalidOperationException_NoSideEffects()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord registered = MakeRecord(MakeRequest(10));
                CaptureFrameRecord mismatched = MakeRecord(MakeRequest(10, unityFrameIdOffset: 1));

                Assert.That(scheduler.TrySchedule(registered), Is.True);
                Assert.Throws<InvalidOperationException>(() => scheduler.TrySchedule(mismatched));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void RequestQueueFull_RegistryUnchanged_RequestQueueFullTrace()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(1, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record1 = MakeRecord(MakeRequest(1));
                CaptureFrameRecord record2 = MakeRecord(MakeRequest(2));

                Assert.That(scheduler.TrySchedule(record1), Is.True);
                Assert.That(scheduler.TrySchedule(record2), Is.False);

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(1));

                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));

                TraceEvent dropped = logger.GetHistoryEvent(1);
                Assert.That(dropped.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(dropped.Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
                Assert.That(dropped.Reason, Is.EqualTo(TraceReason.None));
                Assert.That(dropped.CaptureFrameId, Is.EqualTo(2));
            }
        }

        [Test]
        public void RegistryFull_QueueUnchanged_FrameRecordRegistryFullTrace()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 1, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record1 = MakeRecord(MakeRequest(1));
                CaptureFrameRecord record2 = MakeRecord(MakeRequest(2));

                Assert.That(scheduler.TrySchedule(record1), Is.True);
                Assert.That(scheduler.TrySchedule(record2), Is.False);

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));

                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(registry.TotalRejected, Is.EqualTo(1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));

                TraceEvent dropped = logger.GetHistoryEvent(1);
                Assert.That(dropped.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(dropped.Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));
                Assert.That(dropped.Reason, Is.EqualTo(TraceReason.None));
                Assert.That(dropped.CaptureFrameId, Is.EqualTo(2));
            }
        }

        [Test]
        public void RegistryFreesUp_RetrySucceeds()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 1, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record1 = MakeRecord(MakeRequest(1));

                Assert.That(scheduler.TrySchedule(record1), Is.True);

                Assert.That(registry.TryRemove(record1.Request, out CaptureFrameRecord removed), Is.True);
                Assert.That(removed, Is.SameAs(record1));

                CaptureFrameRecord record2 = MakeRecord(MakeRequest(2));
                Assert.That(scheduler.TrySchedule(record2), Is.True);

                Assert.That(queue.Count, Is.EqualTo(2));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TryGet(record2.Request, out CaptureFrameRecord fetched), Is.True);
                Assert.That(fetched, Is.SameAs(record2));
            }
        }

        [Test]
        public void QueueFreesUp_RetrySucceeds()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(1, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record1 = MakeRecord(MakeRequest(1));

                Assert.That(scheduler.TrySchedule(record1), Is.True);

                Assert.That(queue.TryDequeue(out CaptureFrameRequest dequeued), Is.True);
                Assert.That(dequeued.TraceContext.CaptureFrameId, Is.EqualTo(1));

                CaptureFrameRecord record2 = MakeRecord(MakeRequest(2));
                Assert.That(scheduler.TrySchedule(record2), Is.True);

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(registry.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void DisposedLogger_QueuedTraceThrows_RollbackRegistry_QueueEmpty_OriginalException()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
            CaptureFrameRecordScheduler scheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);

            logger.Dispose();

            CaptureFrameRecord record = MakeRecord(MakeRequest(10));
            Assert.Throws<ObjectDisposedException>(() => scheduler.TrySchedule(record));

            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.TotalAccepted, Is.EqualTo(0));
            Assert.That(queue.TotalRejected, Is.EqualTo(0));

            Assert.That(registry.Count, Is.EqualTo(0));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void RollbackFreesSlot_ReRegisterSucceeds()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
            CaptureFrameRecordScheduler scheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);

            logger.Dispose();

            CaptureFrameRecord record = MakeRecord(MakeRequest(10));
            Assert.Throws<ObjectDisposedException>(() => scheduler.TrySchedule(record));

            Assert.That(registry.TryRegister(record), Is.True);
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void DropReason_AppendOnly_ExistingValuesUnchanged_NewValueIs4()
        {
            Type type = typeof(CaptureFrameDropReason);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureFrameDropReason.None, Is.EqualTo(0));
            Assert.That((int)CaptureFrameDropReason.RequestQueueFull, Is.EqualTo(1));
            Assert.That((int)CaptureFrameDropReason.ReadbackFailed, Is.EqualTo(2));
            Assert.That((int)CaptureFrameDropReason.EncodedPngQueueFull, Is.EqualTo(3));
            Assert.That((int)CaptureFrameDropReason.FrameRecordRegistryFull, Is.EqualTo(4));
            Assert.That(Enum.GetName(type, 4), Is.EqualTo(nameof(CaptureFrameDropReason.FrameRecordRegistryFull)));
        }

        [Test]
        public void Observer_AcceptsNewReason_RejectsNoneAndUndefined()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordDropped(MakeContext(), CaptureFrameDropReason.FrameRecordRegistryFull);
                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));
                Assert.That(logger.GetHistoryEvent(0).Reason, Is.EqualTo(TraceReason.None));
            }

            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordDropped(MakeContext(), CaptureFrameDropReason.None));
                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordDropped(MakeContext(), (CaptureFrameDropReason)999));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Scheduler_DoesNotOwnDisposeOrClearDependencies()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameRecordScheduler)), Is.False);

                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _);
                CaptureFrameRecord record = MakeRecord(MakeRequest(10));

                Assert.That(scheduler.TrySchedule(record), Is.True);

                Assert.That(logger.IsCreated, Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(registry.Count, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest dequeued), Is.True);
                Assert.That(dequeued.TraceContext.CaptureFrameId, Is.EqualTo(10));

                Assert.That(registry.TryGet(record.Request, out CaptureFrameRecord fetched), Is.True);
                Assert.That(fetched, Is.SameAs(record));
            }
        }

        [Test]
        public void Trace_EventOrderAndNoDuplicates()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordScheduler scheduler = MakeScheduler(2, 2, logger, out CaptureFrameRequestQueue queue, out _, out _);
                CaptureFrameRecord r1 = MakeRecord(MakeRequest(1));
                CaptureFrameRecord r2 = MakeRecord(MakeRequest(2));
                CaptureFrameRecord r3 = MakeRecord(MakeRequest(3));
                CaptureFrameRecord r4 = MakeRecord(MakeRequest(4));

                Assert.That(scheduler.TrySchedule(r1), Is.True);
                Assert.That(scheduler.TrySchedule(r2), Is.True);
                Assert.That(scheduler.TrySchedule(r3), Is.False);

                Assert.That(queue.TryDequeue(out _), Is.True);

                Assert.That(scheduler.TrySchedule(r4), Is.False);

                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(4));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(2).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(2).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
                Assert.That(logger.GetHistoryEvent(3).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(3).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));
            }
        }
    }
}
