using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameCadencedSubmissionCoordinatorTests
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

        private static CaptureFrameTiming MakeTiming(double predictedDisplayTimeSeconds, bool shouldRender)
        {
            return new CaptureFrameTiming(predictedDisplayTimeSeconds, 1.0 / 90.0, shouldRender, 0.0, 0.0, 0L);
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

        private static CaptureFrameCadencedSubmissionCoordinator MakeCoordinator(
            TraceLogger logger,
            int queueCapacity,
            int registryCapacity,
            double targetFramesPerSecond,
            out CaptureFrameRequestQueue queue,
            out CaptureFrameRecordRegistry registry,
            out CaptureFrameIdSequence sequence,
            out CaptureFrameCadenceSelector selector)
        {
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            queue = new CaptureFrameRequestQueue(queueCapacity);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(queue, observer);
            registry = new CaptureFrameRecordRegistry(registryCapacity);
            CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);
            sequence = MakeSequence();
            CaptureFrameRecordFactory factory = MakeFactory(sequence: sequence);
            CaptureFrameRecordSubmissionCoordinator submission = new CaptureFrameRecordSubmissionCoordinator(factory, recordScheduler);
            selector = new CaptureFrameCadenceSelector(targetFramesPerSecond);
            return new CaptureFrameCadencedSubmissionCoordinator(selector, submission);
        }

        private static CaptureFrameCadencedSubmissionStatus Submit(
            CaptureFrameCadencedSubmissionCoordinator coordinator,
            out CaptureFrameRecord accepted,
            double predictedDisplayTimeSeconds,
            bool shouldRender = true,
            int commitPathId = 1)
        {
            return coordinator.TrySubmit(
                1000,
                200,
                300,
                4,
                500,
                600,
                700,
                800,
                9,
                1000,
                MakeTiming(predictedDisplayTimeSeconds, shouldRender),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                commitPathId,
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
                MakeTiming(0.0, true),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        [Test]
        public void Enum_ValuesFixed()
        {
            Type type = typeof(CaptureFrameCadencedSubmissionStatus);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetName(type, 0), Is.EqualTo(nameof(CaptureFrameCadencedSubmissionStatus.None)));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo(nameof(CaptureFrameCadencedSubmissionStatus.NotSelected)));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo(nameof(CaptureFrameCadencedSubmissionStatus.Submitted)));
            Assert.That(Enum.GetName(type, 3), Is.EqualTo(nameof(CaptureFrameCadencedSubmissionStatus.Backpressured)));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(4));
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
                CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, registry, observer);
                CaptureFrameRecordSubmissionCoordinator submission = new CaptureFrameRecordSubmissionCoordinator(MakeFactory(), recordScheduler);

                CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector();

                Assert.Throws<ArgumentNullException>(() => new CaptureFrameCadencedSubmissionCoordinator(null, submission));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameCadencedSubmissionCoordinator(selector, null));
            }
        }

        [Test]
        public void FirstRenderableFrame_Submitted()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out _, out _, out _, out _);

                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, 0.0);

                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(accepted, Is.Not.Null);
                Assert.That(accepted.CaptureFrameId, Is.EqualTo(1));
            }
        }

        [Test]
        public void IntervalBelow_NotSelected()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out _, out _, out _, out _);

                Assert.That(Submit(coordinator, out _, 0.0), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, 0.01);
                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);
            }
        }

        [Test]
        public void ShouldRenderFalse_NotSelected()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out _, out _, out _, out _);

                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, 0.0, shouldRender: false);

                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);
            }
        }

        [Test]
        public void NotSelected_LeavesEverythingUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out _);

                Submit(coordinator, out _, 0.0);
                logger.Drain();

                long lastIssued = sequence.LastIssued;
                int queueCount = queue.Count;
                int registryCount = registry.Count;
                long queueAccepted = queue.TotalAccepted;
                long queueRejected = queue.TotalRejected;
                long registryAccepted = registry.TotalAccepted;
                long registryRejected = registry.TotalRejected;

                Assert.That(Submit(coordinator, out _, 0.01), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));

                Assert.That(sequence.LastIssued, Is.EqualTo(lastIssued));
                Assert.That(queue.Count, Is.EqualTo(queueCount));
                Assert.That(registry.Count, Is.EqualTo(registryCount));
                Assert.That(queue.TotalAccepted, Is.EqualTo(queueAccepted));
                Assert.That(queue.TotalRejected, Is.EqualTo(queueRejected));
                Assert.That(registry.TotalAccepted, Is.EqualTo(registryAccepted));
                Assert.That(registry.TotalRejected, Is.EqualTo(registryRejected));

                Assert.That(logger.Drain(), Is.EqualTo(0));
            }
        }

        [Test]
        public void IntervalBoundary_NextSubmitted()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out _, out _, out _, out _);
                double interval = 1.0 / 45.0;

                Submit(coordinator, out _, 0.0);
                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, interval);

                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(accepted.CaptureFrameId, Is.EqualTo(2));
            }
        }

        [Test]
        public void NinetyHz_To45Fps_OnlySubmitSelected()
        {
            using (TraceLogger logger = new TraceLogger(64))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 64, 64, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out _);
                List<long> submittedIds = new List<long>();

                const int frames = 20;
                for (int k = 0; k < frames; k++)
                {
                    CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, k / 90.0);
                    if (status == CaptureFrameCadencedSubmissionStatus.Submitted)
                    {
                        submittedIds.Add(accepted.CaptureFrameId);
                    }
                    else
                    {
                        Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                    }
                }

                Assert.That(submittedIds.Count, Is.EqualTo(frames / 2));
                for (int i = 0; i < submittedIds.Count; i++)
                {
                    Assert.That(submittedIds[i], Is.EqualTo(i + 1));
                }

                Assert.That(queue.Count, Is.EqualTo(frames / 2));
                Assert.That(registry.Count, Is.EqualTo(frames / 2));
            }
        }

        [Test]
        public void NinetyHz_To30Fps_OnlySubmitSelected()
        {
            using (TraceLogger logger = new TraceLogger(64))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 64, 64, 30.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out _, out _);
                List<long> submittedIds = new List<long>();

                const int frames = 30;
                for (int k = 0; k < frames; k++)
                {
                    CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, k / 90.0);
                    if (status == CaptureFrameCadencedSubmissionStatus.Submitted)
                    {
                        submittedIds.Add(accepted.CaptureFrameId);
                    }
                    else
                    {
                        Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                    }
                }

                Assert.That(submittedIds.Count, Is.EqualTo(frames / 3));
                for (int i = 0; i < submittedIds.Count; i++)
                {
                    Assert.That(submittedIds[i], Is.EqualTo(i + 1));
                }

                Assert.That(queue.Count, Is.EqualTo(frames / 3));
                Assert.That(registry.Count, Is.EqualTo(frames / 3));
            }
        }

        [Test]
        public void QueueFull_Backpressured()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 1, 4, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out _);

                Submit(coordinator, out CaptureFrameRecord first, 0.0);
                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord second, 0.03);

                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(second, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(2));
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(registry.Count, Is.EqualTo(1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
            }
        }

        [Test]
        public void RegistryFull_Backpressured()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 1, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out _);

                Submit(coordinator, out CaptureFrameRecord first, 0.0);
                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord second, 0.03);

                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(second, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(2));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(queue.Count, Is.EqualTo(1));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));
            }
        }

        [Test]
        public void Backpressured_SameTimestampReentry_NotSelected()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 1, 4, 45.0, out _, out _, out CaptureFrameIdSequence sequence, out _);

                Submit(coordinator, out _, 0.0);
                Assert.That(Submit(coordinator, out _, 0.03), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));

                // Re-entering the already-selected timestamp adds no ID or drop.
                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, 0.03);
                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(2));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void InvalidTiming_SelectorException_NoIdConsumed()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out _, out _, out CaptureFrameIdSequence sequence, out _);

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.Throws<ArgumentException>(() => coordinator.TrySubmit(
                    1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                    default(CaptureFrameTiming),
                    MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f),
                    1,
                    out accepted));

                Assert.That(accepted, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(0));
            }
        }

        [Test]
        public void TimestampRegression_Exception_SubmissionUntouched()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out _);

                Submit(coordinator, out _, 0.0);
                Submit(coordinator, out _, 0.05);

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.Throws<ArgumentOutOfRangeException>(() => Submit(coordinator, out accepted, 0.04));

                Assert.That(accepted, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(2));
                Assert.That(queue.Count, Is.EqualTo(2));
                Assert.That(registry.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void RecordGenerationFailure_CadenceMaintained_IdConsumed()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out CaptureFrameCadenceSelector selector);

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.Throws<ArgumentOutOfRangeException>(() => Submit(coordinator, out accepted, 0.0, commitPathId: 0));

                Assert.That(accepted, Is.Null);

                // The cadence selection was recorded and the issued ID consumed.
                Assert.That(selector.HasSelectedTimestamp, Is.True);
                Assert.That(selector.LastSelectedTimestampSeconds, Is.EqualTo(0.0));
                Assert.That(sequence.LastIssued, Is.EqualTo(1));

                // Nothing reached the queue or registry.
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void SchedulerExceptionAfterRegistration_RollbackMaintained()
        {
            TraceLogger logger = new TraceLogger(16);
            try
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out CaptureFrameCadenceSelector selector);

                logger.Dispose();

                CaptureFrameRecord accepted = MakeSentinelRecord();
                Assert.Throws<ObjectDisposedException>(() => Submit(coordinator, out accepted, 0.0));

                Assert.That(accepted, Is.Null);

                // Scheduler rolled the registration back.
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(queue.Count, Is.EqualTo(0));

                // Cadence and ID were both consumed.
                Assert.That(selector.HasSelectedTimestamp, Is.True);
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void ExternalReset_ThenNextFrameReselected()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out _, out _, out _, out CaptureFrameCadenceSelector selector);

                Submit(coordinator, out _, 0.0);
                Submit(coordinator, out _, 0.05);

                selector.Reset();

                CaptureFrameCadencedSubmissionStatus status = Submit(coordinator, out CaptureFrameRecord accepted, 1.0);
                Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(accepted.CaptureFrameId, Is.EqualTo(3));
            }
        }

        [Test]
        public void DoesNotDisposeOrClearDependencies()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameCadencedSubmissionCoordinator coordinator = MakeCoordinator(logger, 4, 4, 45.0, out CaptureFrameRequestQueue queue, out CaptureFrameRecordRegistry registry, out CaptureFrameIdSequence sequence, out _);

                Submit(coordinator, out _, 0.0);

                Assert.That(logger.IsCreated, Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(sequence.Next(), Is.EqualTo(2));
            }
        }

        [Test]
        public void HoldsNoRecordQueueRegistryOrLogger()
        {
            foreach (FieldInfo field in typeof(CaptureFrameCadencedSubmissionCoordinator).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
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
        public void SealedNotIDisposableNotMonoBehaviour()
        {
            Assert.That(typeof(CaptureFrameCadencedSubmissionCoordinator).IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameCadencedSubmissionCoordinator)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureFrameCadencedSubmissionCoordinator)), Is.False);
        }
    }
}
