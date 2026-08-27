using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRecordSubmissionCoordinatorTests
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

        private static CaptureRunReference MakeRun(long testRunId = 1, long testCaseId = 100, int captureProfileId = 5)
        {
            TraceRunManifest manifest = MakeManifest(testRunId);
            return new CaptureRunReference(manifest, testCaseId, captureProfileId, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameIdSequence MakeSequence()
        {
            return new CaptureFrameIdSequence();
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecordFactory MakeFactory(CaptureRunReference run = null, CaptureFrameIdSequence sequence = null)
        {
            return new CaptureFrameRecordFactory(
                run ?? MakeRun(),
                sequence ?? MakeSequence(),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRecordSubmissionCoordinator MakeCoordinator(
            TraceLogger logger,
            int queueCapacity,
            int registryCapacity,
            out CaptureFrameRequestQueue queue,
            out CaptureFrameRecordRegistry registry,
            out CaptureFrameRecordFactory factory,
            out CaptureFrameIdSequence sequence)
        {
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            queue = new CaptureFrameRequestQueue(queueCapacity);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
            registry = new CaptureFrameRecordRegistry(registryCapacity);
            CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);
            sequence = MakeSequence();
            factory = MakeFactory(sequence: sequence);
            return new CaptureFrameRecordSubmissionCoordinator(factory, recordScheduler);
        }

        private static bool Submit(
            CaptureFrameRecordSubmissionCoordinator coordinator,
            out CaptureFrameRecord accepted,
            long timestamp = 1000)
        {
            return coordinator.TrySubmit(
                timestamp,
                200,
                300,
                4,
                500,
                600,
                700,
                800,
                9,
                1000,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1,
                out accepted);
        }

        private static CaptureFrameRecord MakeSentinelRecord()
        {
            return MakeFactory().Create(
                9999,
                9999,
                9999,
                9,
                9999,
                9999,
                9999,
                9999,
                9,
                9999,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        private static void AssertRequestIdentical(in CaptureFrameRequest expected, in CaptureFrameRequest actual)
        {
            CaptureFrameTraceContext e = expected.TraceContext;
            CaptureFrameTraceContext a = actual.TraceContext;

            Assert.That(a.Timestamp, Is.EqualTo(e.Timestamp));
            Assert.That(a.UnityFrameId, Is.EqualTo(e.UnityFrameId));
            Assert.That(a.FixedStepId, Is.EqualTo(e.FixedStepId));
            Assert.That(a.ThreadId, Is.EqualTo(e.ThreadId));
            Assert.That(a.CaptureFrameId, Is.EqualTo(e.CaptureFrameId));
            Assert.That(a.OpenXRFrameId, Is.EqualTo(e.OpenXRFrameId));
            Assert.That(a.TestRunId, Is.EqualTo(e.TestRunId));
            Assert.That(a.SlashId, Is.EqualTo(e.SlashId));
            Assert.That(a.FrontEdgeId, Is.EqualTo(e.FrontEdgeId));
            Assert.That(a.ObjectId, Is.EqualTo(e.ObjectId));
            Assert.That(a.ObjectGeneration, Is.EqualTo(e.ObjectGeneration));
            Assert.That(a.TaskId, Is.EqualTo(e.TaskId));

            Assert.That(actual.Source, Is.EqualTo(expected.Source));
            Assert.That(actual.Eye, Is.EqualTo(expected.Eye));
            Assert.That(actual.ImageRect.X, Is.EqualTo(expected.ImageRect.X));
            Assert.That(actual.ImageRect.Y, Is.EqualTo(expected.ImageRect.Y));
            Assert.That(actual.ImageRect.Width, Is.EqualTo(expected.ImageRect.Width));
            Assert.That(actual.ImageRect.Height, Is.EqualTo(expected.ImageRect.Height));
            Assert.That(actual.ArrayIndex, Is.EqualTo(expected.ArrayIndex));
            Assert.That(actual.PixelLayout.Format, Is.EqualTo(expected.PixelLayout.Format));
            Assert.That(actual.PixelLayout.Width, Is.EqualTo(expected.PixelLayout.Width));
            Assert.That(actual.PixelLayout.Height, Is.EqualTo(expected.PixelLayout.Height));
            Assert.That(actual.PixelLayout.BytesPerPixel, Is.EqualTo(expected.PixelLayout.BytesPerPixel));
            Assert.That(actual.PixelLayout.RowStrideBytes, Is.EqualTo(expected.PixelLayout.RowStrideBytes));
            Assert.That(actual.PixelLayout.ByteCount, Is.EqualTo(expected.PixelLayout.ByteCount));
            Assert.That(actual.RequiredByteCount, Is.EqualTo(expected.RequiredByteCount));
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(4);
                CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFrameRecordScheduler scheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);
                CaptureFrameRecordFactory factory = MakeFactory();

                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordSubmissionCoordinator(null, scheduler));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordSubmissionCoordinator(factory, null));
            }
        }

        [Test]
        public void TrySubmit_Success_ReturnsTrueAndRegistryHoldsSameRecord()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out _, out CaptureFrameRecordRegistry registry, out _, out _);

                Assert.That(Submit(coordinator, out CaptureFrameRecord accepted), Is.True);
                Assert.That(accepted, Is.Not.Null);
                Assert.That(accepted.CaptureFrameId, Is.EqualTo(1));

                Assert.That(registry.TryGet(accepted.Request, out CaptureFrameRecord stored), Is.True);
                Assert.That(ReferenceEquals(stored, accepted), Is.True);
            }
        }

        [Test]
        public void TrySubmit_Success_QueueHoldsMatchingRequest()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out CaptureFrameRequestQueue queue, out _, out _, out _);

                Submit(coordinator, out CaptureFrameRecord accepted);

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TryDequeue(out CaptureFrameRequest queued), Is.True);
                AssertRequestIdentical(accepted.Request, queued);
            }
        }

        [Test]
        public void TrySubmit_Success_RecordsExactlyOneQueuedTrace()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out _, out _, out _, out _);

                Submit(coordinator, out _);

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(0).CaptureFrameId, Is.EqualTo(1));
            }
        }

        [Test]
        public void TrySubmit_ConsecutiveSuccess_IdsIncrease()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out _, out _, out _, out _);

                Submit(coordinator, out CaptureFrameRecord first);
                Submit(coordinator, out CaptureFrameRecord second);

                Assert.That(first.CaptureFrameId, Is.EqualTo(1));
                Assert.That(second.CaptureFrameId, Is.EqualTo(2));
            }
        }

        [Test]
        public void TrySubmit_QueueFull_ReturnsFalseAndNull()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 1, 4, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out CaptureFrameIdSequence sequence);

                Submit(coordinator, out CaptureFrameRecord first);
                bool secondOk = Submit(coordinator, out CaptureFrameRecord second);

                Assert.That(secondOk, Is.False);
                Assert.That(second, Is.Null);

                // Registry and queue are unchanged; the first record remains.
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TryGet(first.Request, out CaptureFrameRecord stored), Is.True);
                Assert.That(ReferenceEquals(stored, first), Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));

                Assert.That(sequence.LastIssued, Is.EqualTo(2));
            }
        }

        [Test]
        public void TrySubmit_RegistryFull_ReturnsFalseAndNull()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 1, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out CaptureFrameIdSequence sequence);

                Submit(coordinator, out CaptureFrameRecord first);
                bool secondOk = Submit(coordinator, out CaptureFrameRecord second);

                Assert.That(secondOk, Is.False);
                Assert.That(second, Is.Null);

                // Registry unchanged (one record), queue untouched (one request).
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(queue.Count, Is.EqualTo(1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));

                Assert.That(sequence.LastIssued, Is.EqualTo(2));
            }
        }

        [Test]
        public void TrySubmit_FactoryFailure_LeavesEverythingUntouched()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out CaptureFrameIdSequence sequence);

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.That(accepted, Is.Not.Null);

                Assert.Throws<ArgumentException>(() => coordinator.TrySubmit(
                    1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                    default(CaptureFrameTiming),
                    MakePose(1f, 2f, 3f),
                    MakePose(4f, 5f, 6f),
                    MakePose(7f, 8f, 9f),
                    1,
                    out accepted));

                Assert.That(accepted, Is.Null);
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(registry.TotalAccepted, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));

                Assert.That(sequence.LastIssued, Is.EqualTo(1));
            }
        }

        [Test]
        public void TrySubmit_DuplicateIdPrecheck_OutNullAndTypePreserved()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out _, out CaptureFrameRecordRegistry registry, out _, out _);

                // Pre-register a record with capture frame ID 1 whose request
                // differs from the coordinator's, so the scheduler's duplicate
                // pre-check throws InvalidOperationException before any
                // registration takes place.
                CaptureFrameRecord preRegistered = MakeFactory().Create(
                    12345, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                    MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
                Assert.That(registry.TryRegister(preRegistered), Is.True);

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.Throws<InvalidOperationException>(() => Submit(coordinator, out accepted));
                Assert.That(accepted, Is.Null);

                // The coordinator did not roll anything back; the pre-registered
                // record is untouched.
                Assert.That(registry.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void TrySubmit_SchedulerRollback_AfterRegistrationFailure()
        {
            TraceLogger logger = new TraceLogger(16);
            try
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out CaptureFrameIdSequence sequence);

                // Dispose the logger so the scheduler's RecordQueued throws
                // ObjectDisposedException after the record has been registered,
                // exercising the scheduler's own rollback path.
                logger.Dispose();

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.Throws<ObjectDisposedException>(() => Submit(coordinator, out accepted));
                Assert.That(accepted, Is.Null);

                // The scheduler rolled the registration back.
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(queue.Count, Is.EqualTo(0));

                // The issued ID is consumed and the cumulative accepted counter
                // is not rolled back.
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void TrySubmit_FailureThenSuccess_DoesNotReuseId()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 1, 1, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out CaptureFrameIdSequence sequence);

                Submit(coordinator, out CaptureFrameRecord first);
                Assert.That(first.CaptureFrameId, Is.EqualTo(1));

                // Queue is full: rejected, ID 2 consumed.
                Assert.That(Submit(coordinator, out CaptureFrameRecord second), Is.False);
                Assert.That(second, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(2));

                // Free both slots, then the next success must use ID 3, not 2.
                Assert.That(queue.TryDequeue(out _), Is.True);
                Assert.That(registry.TryRemove(first.Request, out _), Is.True);

                Assert.That(Submit(coordinator, out CaptureFrameRecord third), Is.True);
                Assert.That(third.CaptureFrameId, Is.EqualTo(3));
            }
        }

        [Test]
        public void Coordinator_DoesNotDisposeOrClearDependencies()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameRecordSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out CaptureFrameIdSequence sequence);

                Submit(coordinator, out _);

                Assert.That(logger.IsCreated, Is.True);
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(sequence.Next(), Is.EqualTo(2));
            }
        }

        [Test]
        public void Coordinator_HoldsNoRecordQueueRegistryOrLogger()
        {
            foreach (FieldInfo field in typeof(CaptureFrameRecordSubmissionCoordinator).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRecord)), "Must not retain a produced record.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRequestQueue)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRecordRegistry)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameTraceObserver)));
                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False);
            }
        }

        [Test]
        public void Coordinator_NotIDisposableOrMonoBehaviour()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameRecordSubmissionCoordinator)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureFrameRecordSubmissionCoordinator)), Is.False);
            Assert.That(typeof(CaptureFrameRecordSubmissionCoordinator).IsSealed, Is.True);
        }
    }
}
