using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameProfileTests
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

        private static CaptureFrameTiming MakeTiming(double predictedDisplayTimeSeconds, bool shouldRender)
        {
            return new CaptureFrameTiming(predictedDisplayTimeSeconds, 1.0 / 90.0, shouldRender, 0.0, 0.0, 0L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureImageRect MakeRect(int x = 0, int y = 0, int width = 2, int height = 2)
        {
            return new CaptureImageRect(x, y, width, height);
        }

        private static CaptureFrameProfile MakeProfile(
            int profileId = 5,
            double targetFps = 45.0,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left,
            int arrayIndex = 0,
            CapturePixelFormat pixelFormat = CapturePixelFormat.Rgba32)
        {
            return new CaptureFrameProfile(profileId, targetFps, source, eye, MakeRect(), arrayIndex, pixelFormat);
        }

        private static CaptureFrameRecord CreateRecord(CaptureFrameRecordFactory factory)
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
                1);
        }

        [Test]
        public void Constructor_ProfileIdZeroAndNegative_Rejected()
        {
            CaptureImageRect rect = MakeRect();

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(0, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(-1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_FpsInvalid_Rejected()
        {
            CaptureImageRect rect = MakeRect();

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(1, double.NaN, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(1, double.PositiveInfinity, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(1, double.NegativeInfinity, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(1, 0.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(1, -30.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_SourceInvalid_Rejected()
        {
            CaptureImageRect rect = MakeRect();

            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.None, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, (CaptureSource)999, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_EyeInvalid_Rejected()
        {
            CaptureImageRect rect = MakeRect();

            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.None, rect, 0, CapturePixelFormat.Rgba32));
            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, (CaptureEye)999, rect, 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_InvalidRect_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, default(CaptureImageRect), 0, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_NegativeArrayIndex_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, MakeRect(), -1, CapturePixelFormat.Rgba32));
        }

        [Test]
        public void Constructor_UndefinedPixelFormat_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, MakeRect(), 0, CapturePixelFormat.None));
            Assert.Throws<ArgumentException>(() => new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, MakeRect(), 0, (CapturePixelFormat)999));
        }

        [Test]
        public void Properties_MatchInputs()
        {
            CaptureImageRect rect = new CaptureImageRect(1, 2, 3, 4);
            CaptureFrameProfile profile = new CaptureFrameProfile(7, 30.0, CaptureSource.OpenXRProjection, CaptureEye.Right, rect, 5, CapturePixelFormat.Rgba32);

            Assert.That(profile.ProfileId, Is.EqualTo(7));
            Assert.That(profile.TargetFramesPerSecond, Is.EqualTo(30.0));
            Assert.That(profile.Source, Is.EqualTo(CaptureSource.OpenXRProjection));
            Assert.That(profile.Eye, Is.EqualTo(CaptureEye.Right));
            Assert.That(profile.ImageRect.X, Is.EqualTo(1));
            Assert.That(profile.ImageRect.Y, Is.EqualTo(2));
            Assert.That(profile.ImageRect.Width, Is.EqualTo(3));
            Assert.That(profile.ImageRect.Height, Is.EqualTo(4));
            Assert.That(profile.ArrayIndex, Is.EqualTo(5));
            Assert.That(profile.PixelFormat, Is.EqualTo(CapturePixelFormat.Rgba32));
        }

        [Test]
        public void PixelLayout_MatchesRectAndFormat()
        {
            CaptureFrameProfile profile = new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(1, 2, 3, 4), 0, CapturePixelFormat.Rgba32);

            Assert.That(profile.PixelLayout.Format, Is.EqualTo(CapturePixelFormat.Rgba32));
            Assert.That(profile.PixelLayout.Width, Is.EqualTo(3));
            Assert.That(profile.PixelLayout.Height, Is.EqualTo(4));
            Assert.That(profile.PixelLayout.BytesPerPixel, Is.EqualTo(4));
            Assert.That(profile.PixelLayout.RowStrideBytes, Is.EqualTo(12));
            Assert.That(profile.PixelLayout.ByteCount, Is.EqualTo(48));
        }

        [Test]
        public void MinimumIntervalSeconds_MatchesSelector()
        {
            CaptureFrameProfile profile = MakeProfile(targetFps: 45.0);

            Assert.That(profile.MinimumIntervalSeconds, Is.EqualTo(new CaptureFrameCadenceSelector(45.0).MinimumIntervalSeconds));
        }

        [Test]
        public void PhaseZeroStandard_ValuesFixed()
        {
            CaptureImageRect rect = new CaptureImageRect(0, 0, 640, 480);
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(3, rect);

            Assert.That(profile.ProfileId, Is.EqualTo(3));
            Assert.That(profile.TargetFramesPerSecond, Is.EqualTo(45.0));
            Assert.That(profile.Source, Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That(profile.Eye, Is.EqualTo(CaptureEye.Left));
            Assert.That(profile.ArrayIndex, Is.EqualTo(0));
            Assert.That(profile.PixelFormat, Is.EqualTo(CapturePixelFormat.Rgba32));
        }

        [Test]
        public void PhaseZeroStandard_RectUnchanged()
        {
            CaptureImageRect rect = new CaptureImageRect(5, 6, 7, 8);
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(3, rect);

            Assert.That(profile.ImageRect.X, Is.EqualTo(5));
            Assert.That(profile.ImageRect.Y, Is.EqualTo(6));
            Assert.That(profile.ImageRect.Width, Is.EqualTo(7));
            Assert.That(profile.ImageRect.Height, Is.EqualTo(8));
        }

        [Test]
        public void CreateCadenceSelector_IndependentInstances()
        {
            CaptureFrameProfile profile = MakeProfile();

            CaptureFrameCadenceSelector s1 = profile.CreateCadenceSelector();
            CaptureFrameCadenceSelector s2 = profile.CreateCadenceSelector();

            Assert.That(ReferenceEquals(s1, s2), Is.False);

            Assert.That(s1.TrySelect(MakeTiming(0.0, true)), Is.True);
            s1.Reset();

            Assert.That(s2.HasObservedTimestamp, Is.False);
            Assert.That(s2.TargetFramesPerSecond, Is.EqualTo(profile.TargetFramesPerSecond));
        }

        [Test]
        public void CreateRecordFactory_TransfersSettings()
        {
            CaptureImageRect rect = new CaptureImageRect(1, 2, 3, 4);
            CaptureFrameProfile profile = new CaptureFrameProfile(5, 45.0, CaptureSource.OpenXRProjection, CaptureEye.Right, rect, 7, CapturePixelFormat.Rgba32);

            CaptureFrameRecordFactory factory = profile.CreateRecordFactory(MakeRun(captureProfileId: 5), MakeSequence());
            CaptureFrameRecord record = CreateRecord(factory);

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
        }

        [Test]
        public void CreateRecordFactory_MismatchedProfileId_NoSideEffects()
        {
            CaptureFrameProfile profile = MakeProfile(profileId: 5);
            CaptureFrameIdSequence sequence = MakeSequence();
            CaptureRunReference mismatchedRun = MakeRun(captureProfileId: 9);

            Assert.Throws<ArgumentException>(() => profile.CreateRecordFactory(mismatchedRun, sequence));

            // No capture frame ID was issued and the sequence is unchanged.
            Assert.That(sequence.LastIssued, Is.EqualTo(0));
        }

        [Test]
        public void CreateRecordFactory_NullDependencies_Rejected()
        {
            CaptureFrameProfile profile = MakeProfile(profileId: 5);
            CaptureRunReference run = MakeRun(captureProfileId: 5);
            CaptureFrameIdSequence sequence = MakeSequence();

            Assert.Throws<ArgumentNullException>(() => profile.CreateRecordFactory(null, sequence));
            Assert.Throws<ArgumentNullException>(() => profile.CreateRecordFactory(run, null));
        }

        [Test]
        public void GeneratedRecord_CaptureProfileIdMatches()
        {
            CaptureFrameProfile profile = MakeProfile(profileId: 5);
            CaptureRunReference run = MakeRun(captureProfileId: 5);

            CaptureFrameRecord record = CreateRecord(profile.CreateRecordFactory(run, MakeSequence()));

            Assert.That(record.CaptureProfileId, Is.EqualTo(5));
            Assert.That(record.CaptureProfileId, Is.EqualTo(profile.ProfileId));
        }

        [Test]
        public void Profile_DoesNotOwnOrMutateDependencies()
        {
            CaptureFrameProfile profile = MakeProfile(profileId: 5);
            CaptureRunReference run = MakeRun(captureProfileId: 5);
            CaptureFrameIdSequence sequence = MakeSequence();

            CaptureFrameRecord record = CreateRecord(profile.CreateRecordFactory(run, sequence));

            // The sequence advanced by exactly one issue and remains usable.
            Assert.That(sequence.LastIssued, Is.EqualTo(1));
            Assert.That(sequence.Next(), Is.EqualTo(2));

            // The run reference is returned unchanged.
            Assert.That(record.Run, Is.SameAs(run));
        }

        [Test]
        public void Profile_NoPublicSettersOrUnityObjects()
        {
            Type type = typeof(CaptureFrameProfile);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, type.Name + "." + property.Name + " must not have a public setter.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, type.Name + "." + field.Name + " must not be an array.");
                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False, type.Name + "." + field.Name + " must not be a Unity Object.");
            }
        }

        [Test]
        public void Profile_SealedNotIDisposableNotMonoBehaviourNotScriptableObject()
        {
            Assert.That(typeof(CaptureFrameProfile).IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFrameProfile)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureFrameProfile)), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(typeof(CaptureFrameProfile)), Is.False);
        }
    }
}
