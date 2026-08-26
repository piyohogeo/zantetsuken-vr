using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRecordTests
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

        private static CaptureFrameRequest MakeRequest(
            long captureFrameId = 10,
            long unityFrameId = 20,
            long openXRFrameId = 30,
            long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1,
                unityFrameId,
                3,
                4,
                captureFrameId,
                openXRFrameId,
                testRunId,
                5,
                6,
                7,
                8u,
                9);

            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming(bool shouldRender = true)
        {
            return new CaptureFrameTiming(1.0, 1.0 / 90.0, shouldRender, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(
            CaptureRunReference run,
            CaptureFrameRequest request,
            CaptureFrameTiming timing,
            CapturePoseSample head,
            CapturePoseSample left,
            CapturePoseSample right,
            int commitPathId = 1)
        {
            return new CaptureFrameRecord(run, request, timing, head, left, right, commitPathId);
        }

        private static void AssertNoFieldOfType(Type type, Type forbiddenFieldType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(forbiddenFieldType), type.Name + " must not hold a " + forbiddenFieldType.Name);
            }
        }

        private static void AssertNoPublicSetters(Type type)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, type.Name + "." + property.Name + " must not have a public setter.");
            }
        }

        private static void AssertNoFieldNameContains(Type type, string fragment)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.Name.IndexOf(fragment, StringComparison.Ordinal), Is.LessThan(0), type.Name + "." + field.Name + " must not duplicate " + fragment);
            }
        }

        [Test]
        public void AllAvailablePoses_Succeeds()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample head = MakePose(0f, 0f, 0f);
            CapturePoseSample left = MakePose(1f, 2f, 3f);
            CapturePoseSample right = MakePose(-1f, -2f, -3f);

            CaptureFrameRecord record = MakeRecord(run, request, timing, head, left, right);

            Assert.That(record.Run, Is.SameAs(run));
            Assert.That(record.HeadPose.IsAvailable, Is.True);
            Assert.That(record.LeftControllerPose.IsAvailable, Is.True);
            Assert.That(record.RightControllerPose.IsAvailable, Is.True);
            Assert.That(record.CommitPathId, Is.EqualTo(1));
        }

        [Test]
        public void UnavailablePoses_PreservedAtAllThreePositions()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample unavailable = CapturePoseSample.Unavailable;

            CaptureFrameRecord record = MakeRecord(run, request, timing, unavailable, unavailable, unavailable);

            Assert.That(record.HeadPose.IsAvailable, Is.False);
            Assert.That(record.LeftControllerPose.IsAvailable, Is.False);
            Assert.That(record.RightControllerPose.IsAvailable, Is.False);
            Assert.That(record.HeadPose.Rotation, Is.Not.EqualTo(Quaternion.identity));
            Assert.That(record.LeftControllerPose.Rotation, Is.Not.EqualTo(Quaternion.identity));
            Assert.That(record.RightControllerPose.Rotation, Is.Not.EqualTo(Quaternion.identity));
        }

        [Test]
        public void NullRun_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureFrameRecord(null, MakeRequest(), MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), 1));
        }

        [Test]
        public void InvalidRequest_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecord(MakeRun(), default, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), 1));
        }

        [Test]
        public void InvalidTiming_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFrameRecord(MakeRun(), MakeRequest(), default, MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), 1));
        }

        [Test]
        public void CommitPathId_ZeroAndNegative_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, request, timing, pose, pose, pose, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, request, timing, pose, pose, pose, -1));
        }

        [Test]
        public void CaptureFrameId_ZeroAndNegative_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, MakeRequest(captureFrameId: 0), timing, pose, pose, pose));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, MakeRequest(captureFrameId: -1), timing, pose, pose, pose));
        }

        [Test]
        public void UnityFrameId_Negative_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, MakeRequest(unityFrameId: -1), timing, pose, pose, pose));
        }

        [Test]
        public void OpenXRFrameId_ZeroAccepted_NegativeRejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            CaptureFrameRecord zero = MakeRecord(run, MakeRequest(openXRFrameId: 0), timing, pose, pose, pose);
            Assert.That(zero.OpenXRFrameId, Is.EqualTo(0));

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, MakeRequest(openXRFrameId: -1), timing, pose, pose, pose));
        }

        [Test]
        public void TestRunId_ZeroAndNegative_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, MakeRequest(testRunId: 0), timing, pose, pose, pose));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeRecord(run, MakeRequest(testRunId: -1), timing, pose, pose, pose));
        }

        [Test]
        public void TestRunIdMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun(testRunId: 1);
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Assert.Throws<ArgumentException>(() => MakeRecord(run, MakeRequest(testRunId: 2), timing, pose, pose, pose));
        }

        [Test]
        public void ForwardingProperties_MatchRequestAndRun()
        {
            CaptureRunReference run = MakeRun(testRunId: 1, testCaseId: 100, captureProfileId: 5);
            CaptureFrameRequest request = MakeRequest(captureFrameId: 10, unityFrameId: 20, openXRFrameId: 30, testRunId: 1);
            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            CaptureFrameRecord record = MakeRecord(run, request, timing, pose, pose, pose);

            Assert.That(record.CaptureFrameId, Is.EqualTo(10));
            Assert.That(record.UnityFrameId, Is.EqualTo(20));
            Assert.That(record.OpenXRFrameId, Is.EqualTo(30));
            Assert.That(record.TestRunId, Is.EqualTo(1));
            Assert.That(record.TestCaseId, Is.EqualTo(100));
            Assert.That(record.BuildId, Is.EqualTo(run.BuildId));
            Assert.That(record.SceneId, Is.EqualTo(run.SceneId));
            Assert.That(record.RandomSeed, Is.EqualTo(run.RandomSeed));
            Assert.That(record.SlashId, Is.EqualTo(5));
            Assert.That(record.FrontEdgeId, Is.EqualTo(6));
            Assert.That(record.ObjectId, Is.EqualTo(7));
            Assert.That(record.ObjectGeneration, Is.EqualTo(8u));
            Assert.That(record.TaskId, Is.EqualTo(9));
            Assert.That(record.Source, Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That(record.Eye, Is.EqualTo(CaptureEye.Left));
            Assert.That(record.ImageRect.X, Is.EqualTo(0));
            Assert.That(record.ImageRect.Y, Is.EqualTo(0));
            Assert.That(record.ImageRect.Width, Is.EqualTo(2));
            Assert.That(record.ImageRect.Height, Is.EqualTo(2));
            Assert.That(record.ArrayIndex, Is.EqualTo(0));
            Assert.That(record.CaptureProfileId, Is.EqualTo(5));
            Assert.That(record.RunManifestContentSha256, Is.EqualTo(run.RunManifestContentSha256));
        }

        [Test]
        public void TimingAndPoses_MatchInput()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameTiming timing = MakeTiming(shouldRender: false);
            CapturePoseSample head = MakePose(0f, 1f, 2f);
            CapturePoseSample left = MakePose(3f, 4f, 5f);
            CapturePoseSample right = MakePose(6f, 7f, 8f);

            CaptureFrameRecord record = MakeRecord(run, request, timing, head, left, right);

            Assert.That(record.Timing.PredictedDisplayTimeSeconds, Is.EqualTo(timing.PredictedDisplayTimeSeconds));
            Assert.That(record.Timing.PredictedDisplayPeriodSeconds, Is.EqualTo(timing.PredictedDisplayPeriodSeconds));
            Assert.That(record.Timing.ShouldRender, Is.EqualTo(false));
            Assert.That(record.Timing.AppGpuTimeMilliseconds, Is.EqualTo(timing.AppGpuTimeMilliseconds));
            Assert.That(record.Timing.CompositorGpuTimeMilliseconds, Is.EqualTo(timing.CompositorGpuTimeMilliseconds));
            Assert.That(record.Timing.DroppedFrameCount, Is.EqualTo(timing.DroppedFrameCount));

            Assert.That(record.HeadPose.Position, Is.EqualTo(head.Position));
            Assert.That(record.LeftControllerPose.Position, Is.EqualTo(left.Position));
            Assert.That(record.RightControllerPose.Position, Is.EqualTo(right.Position));
            Assert.That(record.HeadPose.Rotation, Is.EqualTo(head.Rotation));
            Assert.That(record.LeftControllerPose.Rotation, Is.EqualTo(left.Rotation));
            Assert.That(record.RightControllerPose.Rotation, Is.EqualTo(right.Rotation));
        }

        [Test]
        public void NoFrameSideDuplicateStringFields()
        {
            AssertNoFieldNameContains(typeof(CaptureFrameRecord), "BuildId");
            AssertNoFieldNameContains(typeof(CaptureFrameRecord), "SceneId");
            AssertNoFieldNameContains(typeof(CaptureFrameRecord), "Sha256");
        }

        [Test]
        public void NoPublicSetters()
        {
            AssertNoPublicSetters(typeof(CaptureFrameRecord));
        }

        [Test]
        public void NoForbiddenFieldDependencies()
        {
            foreach (FieldInfo field in typeof(CaptureFrameRecord).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string name = field.FieldType.FullName ?? field.FieldType.Name;

                Assert.That(name.IndexOf("FileStore", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("Queue", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("Router", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("TraceLogger", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("NativeArray", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False);
            }
        }

        [Test]
        public void HashNotRecomputedByGetters()
        {
            AssertNoFieldOfType(typeof(CaptureRunReference), typeof(TraceRunManifest));
            AssertNoFieldOfType(typeof(CaptureFrameRecord), typeof(TraceRunManifest));
        }
    }
}
