using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRecordFactoryTests
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

        private static CaptureFrameIdSequence MakeSequenceAt(long lastIssued)
        {
            ConstructorInfo ctor = typeof(CaptureFrameIdSequence).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameIdSequence)ctor.Invoke(new object[] { lastIssued });
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecordFactory MakeFactory(
            CaptureRunReference run = null,
            CaptureFrameIdSequence sequence = null,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left)
        {
            return new CaptureFrameRecordFactory(
                run ?? MakeRun(),
                sequence ?? MakeSequence(),
                source,
                eye,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRecord Create(CaptureFrameRecordFactory factory, int commitPathId = 1)
        {
            return factory.Create(
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
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                commitPathId);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameIdSequence sequence = MakeSequence();
            CaptureImageRect rect = new CaptureImageRect(0, 0, 2, 2);

            Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordFactory(null, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecordFactory(run, null, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_InvalidFixedSettings_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameIdSequence sequence = MakeSequence();
            CaptureImageRect rect = new CaptureImageRect(0, 0, 2, 2);

            // Source.
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.None, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, (CaptureSource)999, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));

            // Eye.
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.None, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.UnityRenderTexture, (CaptureEye)999, rect, 0, CapturePixelFormat.Rgba32));

            // Image rectangle (default has zero width and height).
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, default(CaptureImageRect), 0, CapturePixelFormat.Rgba32));

            // Array index.
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, -1, CapturePixelFormat.Rgba32));

            // Pixel format.
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.None));
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecordFactory(run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, (CapturePixelFormat)999));
        }

        [Test]
        public void Create_FirstIdsAreOneTwoThree()
        {
            CaptureFrameRecordFactory factory = MakeFactory();

            Assert.That(Create(factory).CaptureFrameId, Is.EqualTo(1));
            Assert.That(Create(factory).CaptureFrameId, Is.EqualTo(2));
            Assert.That(Create(factory).CaptureFrameId, Is.EqualTo(3));
        }

        [Test]
        public void Factories_WithSeparateSequences_AreIndependent()
        {
            CaptureFrameIdSequence s1 = MakeSequence();
            CaptureFrameIdSequence s2 = MakeSequence();
            CaptureFrameRecordFactory f1 = MakeFactory(sequence: s1);
            CaptureFrameRecordFactory f2 = MakeFactory(sequence: s2);

            Assert.That(Create(f1).CaptureFrameId, Is.EqualTo(1));
            Assert.That(Create(f2).CaptureFrameId, Is.EqualTo(1));
            Assert.That(Create(f1).CaptureFrameId, Is.EqualTo(2));

            Assert.That(s1.LastIssued, Is.EqualTo(2));
            Assert.That(s2.LastIssued, Is.EqualTo(1));
        }

        [Test]
        public void Create_TestRunId_FromRunReference()
        {
            CaptureFrameRecordFactory factory = MakeFactory(run: MakeRun(testRunId: 77));

            CaptureFrameRecord record = Create(factory);

            Assert.That(record.TestRunId, Is.EqualTo(77));
            Assert.That(record.Request.TraceContext.TestRunId, Is.EqualTo(77));
        }

        [Test]
        public void Create_TraceContext_AllFieldsTransferred()
        {
            CaptureFrameRecordFactory factory = MakeFactory(run: MakeRun(testRunId: 7));

            CaptureFrameRecord record = factory.Create(
                111,
                222,
                333,
                44,
                555,
                666,
                777,
                888,
                99,
                1000,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);

            CaptureFrameTraceContext c = record.Request.TraceContext;
            Assert.That(c.Timestamp, Is.EqualTo(111));
            Assert.That(c.UnityFrameId, Is.EqualTo(222));
            Assert.That(c.FixedStepId, Is.EqualTo(333));
            Assert.That(c.ThreadId, Is.EqualTo(44));
            Assert.That(c.CaptureFrameId, Is.EqualTo(1));
            Assert.That(c.OpenXRFrameId, Is.EqualTo(555));
            Assert.That(c.TestRunId, Is.EqualTo(7));
            Assert.That(c.SlashId, Is.EqualTo(666));
            Assert.That(c.FrontEdgeId, Is.EqualTo(777));
            Assert.That(c.ObjectId, Is.EqualTo(888));
            Assert.That(c.ObjectGeneration, Is.EqualTo(99));
            Assert.That(c.TaskId, Is.EqualTo(1000));
        }

        [Test]
        public void Create_FixedCaptureSettings_Transferred()
        {
            CaptureFrameRecordFactory factory = new CaptureFrameRecordFactory(
                MakeRun(),
                MakeSequence(),
                CaptureSource.OpenXRProjection,
                CaptureEye.Right,
                new CaptureImageRect(1, 2, 3, 4),
                7,
                CapturePixelFormat.Rgba32);

            CaptureFrameRecord record = Create(factory);

            Assert.That(record.Source, Is.EqualTo(CaptureSource.OpenXRProjection));
            Assert.That(record.Eye, Is.EqualTo(CaptureEye.Right));
            Assert.That(record.ImageRect.X, Is.EqualTo(1));
            Assert.That(record.ImageRect.Y, Is.EqualTo(2));
            Assert.That(record.ImageRect.Width, Is.EqualTo(3));
            Assert.That(record.ImageRect.Height, Is.EqualTo(4));
            Assert.That(record.ArrayIndex, Is.EqualTo(7));
            Assert.That(record.Request.PixelLayout.Format, Is.EqualTo(CapturePixelFormat.Rgba32));
            Assert.That(record.Request.PixelLayout.Width, Is.EqualTo(3));
            Assert.That(record.Request.PixelLayout.Height, Is.EqualTo(4));
            Assert.That(record.Request.PixelLayout.BytesPerPixel, Is.EqualTo(4));
            Assert.That(record.Request.RequiredByteCount, Is.EqualTo(3 * 4 * 4));
        }

        [Test]
        public void Create_TimingPosesCommitPathAndRun_Preserved()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecordFactory factory = MakeFactory(run: run);

            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample head = MakePose(1f, 2f, 3f);
            CapturePoseSample left = MakePose(4f, 5f, 6f);
            CapturePoseSample right = MakePose(7f, 8f, 9f);

            CaptureFrameRecord record = factory.Create(
                1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                timing, head, left, right, 42);

            Assert.That(record.Run, Is.SameAs(run));

            Assert.That(record.Timing.PredictedDisplayTimeSeconds, Is.EqualTo(timing.PredictedDisplayTimeSeconds));
            Assert.That(record.Timing.PredictedDisplayPeriodSeconds, Is.EqualTo(timing.PredictedDisplayPeriodSeconds));
            Assert.That(record.Timing.ShouldRender, Is.EqualTo(timing.ShouldRender));
            Assert.That(record.Timing.AppGpuTimeMilliseconds, Is.EqualTo(timing.AppGpuTimeMilliseconds));
            Assert.That(record.Timing.CompositorGpuTimeMilliseconds, Is.EqualTo(timing.CompositorGpuTimeMilliseconds));
            Assert.That(record.Timing.DroppedFrameCount, Is.EqualTo(timing.DroppedFrameCount));

            Assert.That(record.HeadPose.IsAvailable, Is.True);
            Assert.That(record.HeadPose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(record.LeftControllerPose.IsAvailable, Is.True);
            Assert.That(record.LeftControllerPose.Position, Is.EqualTo(new Vector3(4f, 5f, 6f)));
            Assert.That(record.RightControllerPose.IsAvailable, Is.True);
            Assert.That(record.RightControllerPose.Position, Is.EqualTo(new Vector3(7f, 8f, 9f)));

            Assert.That(record.CommitPathId, Is.EqualTo(42));
        }

        [Test]
        public void Create_UnavailablePoses_NotCompletedToIdentity()
        {
            CaptureFrameRecordFactory factory = MakeFactory();

            CaptureFrameRecord record = factory.Create(
                1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                MakeTiming(),
                CapturePoseSample.Unavailable,
                MakePose(4f, 5f, 6f),
                CapturePoseSample.Unavailable,
                1);

            Assert.That(record.HeadPose.IsAvailable, Is.False);
            Assert.That(record.RightControllerPose.IsAvailable, Is.False);
            Assert.That(record.LeftControllerPose.IsAvailable, Is.True);
            Assert.That(record.LeftControllerPose.Position, Is.EqualTo(new Vector3(4f, 5f, 6f)));
        }

        [Test]
        public void Create_InvalidRecordInput_ConsumesIssuedId()
        {
            CaptureFrameIdSequence sequence = MakeSequence();
            CaptureFrameRecordFactory factory = MakeFactory(sequence: sequence);

            CaptureFrameRecord first = Create(factory);
            Assert.That(first.CaptureFrameId, Is.EqualTo(1));
            Assert.That(sequence.LastIssued, Is.EqualTo(1));

            // The record constructor rejects an invalid timing. The ID issued
            // before the failure must not be reused.
            Assert.Throws<ArgumentException>(() => factory.Create(
                1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                default(CaptureFrameTiming),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1));
            Assert.That(sequence.LastIssued, Is.EqualTo(2));

            CaptureFrameRecord third = Create(factory);
            Assert.That(third.CaptureFrameId, Is.EqualTo(3));
        }

        [Test]
        public void Sequence_Exhausted_OverflowException()
        {
            CaptureFrameIdSequence sequence = MakeSequenceAt(long.MaxValue - 1);
            CaptureFrameRecordFactory factory = MakeFactory(sequence: sequence);

            CaptureFrameRecord last = Create(factory);
            Assert.That(last.CaptureFrameId, Is.EqualTo(long.MaxValue));

            Assert.Throws<OverflowException>(() => Create(factory));
        }

        [Test]
        public void Factory_DoesNotDisposeOrMutateDependencies()
        {
            CaptureFrameIdSequence sequence = MakeSequence();
            CaptureRunReference run = MakeRun();
            CaptureFrameRecordFactory factory = MakeFactory(run: run, sequence: sequence);

            CaptureFrameRecord record = Create(factory);

            // The run reference and the produced record are returned unchanged.
            Assert.That(record.Run, Is.SameAs(run));

            // The sequence is neither reset nor disposed: it advanced by exactly
            // one issue and remains usable.
            Assert.That(sequence.LastIssued, Is.EqualTo(1));
            Assert.That(sequence.Next(), Is.EqualTo(2));

            // The factory retains no produced record.
            foreach (FieldInfo field in typeof(CaptureFrameRecordFactory).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRecord)), "Factory must not retain a produced record.");
            }
        }

        [Test]
        public void Factory_NotIDisposableOrMonoBehaviour()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameRecordFactory)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureFrameRecordFactory)), Is.False);
            Assert.That(typeof(CaptureFrameRecordFactory).IsSealed, Is.True);
        }
    }
}
