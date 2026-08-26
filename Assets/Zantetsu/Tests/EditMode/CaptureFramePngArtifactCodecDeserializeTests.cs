using System;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactCodecDeserializeTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FixedPngHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string GoldenJson =
            "{\"schemaVersion\":1"
            + ",\"captureFrameId\":10"
            + ",\"unityFrameId\":20"
            + ",\"openXRFrameId\":30"
            + ",\"timestamp\":1"
            + ",\"fixedStepId\":3"
            + ",\"threadId\":4"
            + ",\"testRunId\":1"
            + ",\"testCaseId\":100"
            + ",\"buildId\":\"build-1\""
            + ",\"sceneId\":\"scene-1\""
            + ",\"randomSeed\":12345"
            + ",\"slashId\":5"
            + ",\"frontEdgeId\":6"
            + ",\"objectId\":7"
            + ",\"objectGeneration\":8"
            + ",\"taskId\":9"
            + ",\"commitPathId\":1"
            + ",\"captureSource\":1"
            + ",\"eye\":1"
            + ",\"imageRect\":{\"x\":0,\"y\":0,\"width\":2,\"height\":2}"
            + ",\"arrayIndex\":0"
            + ",\"pixelLayout\":{\"format\":1,\"width\":2,\"height\":2,\"bytesPerPixel\":4,\"rowStrideBytes\":8,\"byteCount\":16}"
            + ",\"timing\":{\"predictedDisplayTimeSeconds\":0.5,\"predictedDisplayPeriodSeconds\":0.01,\"shouldRender\":true,\"appGpuTimeMilliseconds\":3.5,\"compositorGpuTimeMilliseconds\":1.25,\"droppedFrameCount\":7}"
            + ",\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}"
            + ",\"leftControllerPose\":{\"available\":true,\"position\":{\"x\":4,\"y\":5,\"z\":6},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}"
            + ",\"rightControllerPose\":{\"available\":true,\"position\":{\"x\":7,\"y\":8,\"z\":9},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}"
            + ",\"captureProfileId\":5"
            + ",\"runManifestContentSha256\":\"82beaf9acb7f9e6126317e494eae33ad2eac1d84e1665a5dd211b6621a609284\""
            + ",\"pngFileName\":\"out.png\""
            + ",\"pngByteCount\":32"
            + ",\"pngContentSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\""
            + "}";

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunManifest MakeManifest(string buildId = "build-1", long testRunId = 1, double fixedDeltaTimeSeconds = 0.02)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                buildId,
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                fixedDeltaTimeSeconds,
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

        private static CaptureRunReference MakeRun(TraceRunManifest manifest)
        {
            return new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameRequest MakeRequest()
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, 10, 30, 1, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureRunReference run, CaptureFrameRequest request, CaptureFrameTiming timing, CapturePoseSample head, CapturePoseSample left, CapturePoseSample right)
        {
            return new CaptureFrameRecord(run, request, timing, head, left, right, 1);
        }

        private static CaptureFramePngSaveReceipt MakeReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);

            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static CaptureFramePngArtifact MakeGoldenArtifact(out TraceRunManifest manifest)
        {
            manifest = MakeManifest();
            CaptureRunReference run = MakeRun(manifest);
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngSaveReceipt receipt = MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash);
            return new CaptureFramePngArtifact(record, request, receipt);
        }

        private static byte[] GoldenBytes()
        {
            return Encoding.UTF8.GetBytes(GoldenJson);
        }

        private static string MakePngDirectory()
        {
            return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "zantetsuken-deserialize-" + Guid.NewGuid().ToString("N")));
        }

        private static CaptureFramePngArtifact Deserialize(byte[] json, TraceRunManifest manifest, string dir)
        {
            return CaptureFramePngArtifactCodec.DeserializeCanonical(json, manifest, dir);
        }

        [Test]
        public void RoundTrip_ByteExact()
        {
            TraceRunManifest manifest;
            CaptureFramePngArtifact original = MakeGoldenArtifact(out manifest);

            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(original);
            string dir = MakePngDirectory();

            CaptureFramePngArtifact restored = Deserialize(bytes, manifest, dir);

            Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(restored), Is.EqualTo(bytes));
        }

        [Test]
        public void RoundTrip_AllFields()
        {
            TraceRunManifest manifest;
            CaptureFramePngArtifact original = MakeGoldenArtifact(out manifest);

            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(original);
            string dir = MakePngDirectory();
            CaptureFramePngArtifact restored = Deserialize(bytes, manifest, dir);

            Assert.That(restored.CaptureFrameId, Is.EqualTo(10L));
            Assert.That(restored.FrameRecord.TestRunId, Is.EqualTo(1L));
            Assert.That(restored.FrameRecord.TestCaseId, Is.EqualTo(100L));
            Assert.That(restored.FrameRecord.BuildId, Is.EqualTo("build-1"));
            Assert.That(restored.FrameRecord.SceneId, Is.EqualTo("scene-1"));
            Assert.That(restored.FrameRecord.RandomSeed, Is.EqualTo(12345L));
            Assert.That(restored.FrameRecord.CommitPathId, Is.EqualTo(1));
            Assert.That(restored.FrameRecord.CaptureProfileId, Is.EqualTo(5));
            Assert.That(restored.FrameRecord.RunManifestContentSha256, Is.EqualTo(manifestRunHash(manifest)));

            CaptureFrameRequest request = restored.FrameRecord.Request;
            Assert.That(request.TraceContext.Timestamp, Is.EqualTo(1L));
            Assert.That(request.TraceContext.UnityFrameId, Is.EqualTo(20L));
            Assert.That(request.TraceContext.FixedStepId, Is.EqualTo(3L));
            Assert.That(request.TraceContext.ThreadId, Is.EqualTo(4));
            Assert.That(request.TraceContext.CaptureFrameId, Is.EqualTo(10L));
            Assert.That(request.TraceContext.OpenXRFrameId, Is.EqualTo(30L));
            Assert.That(request.TraceContext.TestRunId, Is.EqualTo(1L));
            Assert.That(request.TraceContext.SlashId, Is.EqualTo(5L));
            Assert.That(request.TraceContext.FrontEdgeId, Is.EqualTo(6L));
            Assert.That(request.TraceContext.ObjectId, Is.EqualTo(7L));
            Assert.That(request.TraceContext.ObjectGeneration, Is.EqualTo(8u));
            Assert.That(request.TraceContext.TaskId, Is.EqualTo(9L));
            Assert.That(request.Source, Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That(request.Eye, Is.EqualTo(CaptureEye.Left));
            Assert.That(request.ImageRect.X, Is.EqualTo(0));
            Assert.That(request.ImageRect.Y, Is.EqualTo(0));
            Assert.That(request.ImageRect.Width, Is.EqualTo(2));
            Assert.That(request.ImageRect.Height, Is.EqualTo(2));
            Assert.That(request.ArrayIndex, Is.EqualTo(0));
            Assert.That(request.PixelLayout.Format, Is.EqualTo(CapturePixelFormat.Rgba32));

            Assert.That(restored.FrameRecord.Timing.PredictedDisplayTimeSeconds, Is.EqualTo(0.5));
            Assert.That(restored.FrameRecord.Timing.PredictedDisplayPeriodSeconds, Is.EqualTo(0.01));
            Assert.That(restored.FrameRecord.Timing.ShouldRender, Is.True);
            Assert.That(restored.FrameRecord.Timing.AppGpuTimeMilliseconds, Is.EqualTo(3.5));
            Assert.That(restored.FrameRecord.Timing.CompositorGpuTimeMilliseconds, Is.EqualTo(1.25));
            Assert.That(restored.FrameRecord.Timing.DroppedFrameCount, Is.EqualTo(7L));

            Assert.That(restored.FrameRecord.HeadPose.IsAvailable, Is.True);
            Assert.That(restored.FrameRecord.HeadPose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(restored.FrameRecord.LeftControllerPose.Position, Is.EqualTo(new Vector3(4f, 5f, 6f)));
            Assert.That(restored.FrameRecord.RightControllerPose.Position, Is.EqualTo(new Vector3(7f, 8f, 9f)));

            Assert.That(restored.PngReceipt.DestinationPath, Is.EqualTo(Path.GetFullPath(Path.Combine(dir, "out.png"))));
            Assert.That(restored.PngReceipt.ByteCount, Is.EqualTo(32));
            Assert.That(restored.PngReceipt.ContentSha256, Is.EqualTo(FixedPngHash));
        }

        private static string manifestRunHash(TraceRunManifest manifest)
        {
            return TraceRunManifestCodec.ComputeContentSha256(manifest);
        }

        [Test]
        public void AvailableAndUnavailablePose_RoundTrip()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureRunReference run = MakeRun(manifest);
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), CapturePoseSample.Unavailable, MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            CaptureFramePngArtifact restored = Deserialize(bytes, manifest, MakePngDirectory());

            Assert.That(restored.FrameRecord.HeadPose.IsAvailable, Is.False);
            Assert.That(restored.FrameRecord.HeadPose.Rotation, Is.EqualTo(default(Quaternion)));
            Assert.That(restored.FrameRecord.LeftControllerPose.IsAvailable, Is.True);
            Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(restored), Is.EqualTo(bytes));
        }

        [Test]
        public void PngDestination_DirectoryPlusFileName()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);
            string dir = MakePngDirectory();

            CaptureFramePngArtifact restored = Deserialize(GoldenBytes(), manifest, dir);

            Assert.That(restored.PngReceipt.DestinationPath, Is.EqualTo(Path.GetFullPath(Path.Combine(dir, "out.png"))));
        }

        [Test]
        public void NonExistentDirectory_StillRestores_AndCreatesNothing()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);
            string dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "zantetsuken-no-such-dir-" + Guid.NewGuid().ToString("N")));

            CaptureFramePngArtifact restored = Deserialize(GoldenBytes(), manifest, dir);

            Assert.That(restored.PngReceipt.DestinationPath, Is.EqualTo(Path.GetFullPath(Path.Combine(dir, "out.png"))));
            Assert.That(Directory.Exists(dir), Is.False);
            Assert.That(File.Exists(restored.PngReceipt.DestinationPath), Is.False);
        }

        [Test]
        public void NullArguments_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<ArgumentNullException>(() => Deserialize(null, manifest, MakePngDirectory()));
            Assert.Throws<ArgumentNullException>(() => Deserialize(GoldenBytes(), null, MakePngDirectory()));
            Assert.Throws<ArgumentNullException>(() => Deserialize(GoldenBytes(), manifest, null));
        }

        [Test]
        public void EmptyInput_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(new byte[0], manifest, MakePngDirectory()));
        }

        [Test]
        public void Bom_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            byte[] golden = GoldenBytes();
            byte[] withBom = new byte[golden.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Array.Copy(golden, 0, withBom, 3, golden.Length);

            Assert.Throws<InvalidDataException>(() => Deserialize(withBom, manifest, MakePngDirectory()));
        }

        [Test]
        public void InvalidUtf8_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            byte[] invalid = { 0x7B, 0xFF, 0xFF, 0x7D };

            Assert.Throws<InvalidDataException>(() => Deserialize(invalid, manifest, MakePngDirectory()));
        }

        [Test]
        public void MalformedJson_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes("not json"), manifest, MakePngDirectory()));
        }

        [Test]
        public void SizeLimitExceeded_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            byte[] oversized = new byte[CaptureFramePngArtifactCodec.MaximumCanonicalByteCount + 1];

            Assert.Throws<InvalidDataException>(() => Deserialize(oversized, manifest, MakePngDirectory()));
        }

        [Test]
        public void SchemaMismatch_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")), manifest, MakePngDirectory()));
        }

        [Test]
        public void RunManifest_TestRunIdMismatch_Rejected()
        {
            TraceRunManifest manifest = MakeManifest(testRunId: 2);

            Assert.Throws<InvalidDataException>(() => Deserialize(GoldenBytes(), manifest, MakePngDirectory()));
        }

        [Test]
        public void RunManifest_BuildIdMismatch_Rejected()
        {
            TraceRunManifest manifest = MakeManifest(buildId: "build-2");

            Assert.Throws<InvalidDataException>(() => Deserialize(GoldenBytes(), manifest, MakePngDirectory()));
        }

        [Test]
        public void RunManifest_SceneIdMismatch_Rejected()
        {
            TraceRunManifest mismatched = MakeManifestWithScene("scene-2");

            Assert.Throws<InvalidDataException>(() => Deserialize(GoldenBytes(), mismatched, MakePngDirectory()));
        }

        private static TraceRunManifest MakeManifestWithScene(string sceneId)
        {
            TraceRunContext context = new TraceRunContext(1, 1000, "build-1", "6000.3.22f1", ValidSha256, sceneId, 12345, 0.02, 3, "High", 1, new Vector3(0f, -4.9f, 0f));
            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                logger.Enqueue(Event(1));
                recorder.TryTrigger();
                return TraceRunManifest.Create(recorder.CreateFrozenSnapshot(), context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void RunManifest_RandomSeedMismatch_Rejected()
        {
            // Same manifest fields except a different random seed; the random
            // seed check runs before the hash check.
            TraceRunManifest mismatched = MakeManifestWithRandomSeed(999);

            Assert.Throws<InvalidDataException>(() => Deserialize(GoldenBytes(), mismatched, MakePngDirectory()));
        }

        private static TraceRunManifest MakeManifestWithRandomSeed(long randomSeed)
        {
            TraceRunContext context = new TraceRunContext(1, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", randomSeed, 0.02, 3, "High", 1, new Vector3(0f, -4.9f, 0f));
            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                logger.Enqueue(Event(1));
                recorder.TryTrigger();
                return TraceRunManifest.Create(recorder.CreateFrozenSnapshot(), context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void RunManifest_HashMismatch_Rejected()
        {
            TraceRunManifest manifest = MakeManifest(fixedDeltaTimeSeconds: 0.03);

            Assert.Throws<InvalidDataException>(() => Deserialize(GoldenBytes(), manifest, MakePngDirectory()));
        }

        [Test]
        public void UnknownProperty_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Insert(GoldenJson.Length - 1, ",\"unknownProp\":1");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void MissingProperty_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace(",\"captureSource\":1", "");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void DuplicateProperty_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace(",\"captureFrameId\":10", ",\"captureFrameId\":10,\"captureFrameId\":10");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void PropertyOrderChanged_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace(",\"captureFrameId\":10,\"unityFrameId\":20", ",\"unityFrameId\":20,\"captureFrameId\":10");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void WhitespaceOrTrailingNewline_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson + "\n"), manifest, MakePngDirectory()));
            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("{\"schemaVersion\":1", "{ \"schemaVersion\":1")), manifest, MakePngDirectory()));
        }

        [Test]
        public void NonCanonicalNumber_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace("\"position\":{\"x\":1,\"y\":2", "\"position\":{\"x\":1.0,\"y\":2");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void NonCanonicalEscape_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace("\"buildId\":\"build-1\"", "\"buildId\":\"\\u0062uild-1\"");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void UppercaseHash_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace("82beaf9acb7f9e6126317e494eae33ad2eac1d84e1665a5dd211b6621a609284", "82BEAF9ACB7F9E6126317E494EAE33AD2EAC1D84E1665A5DD211B6621A609284");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void PngFileName_AbsolutePath_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace("\"pngFileName\":\"out.png\"", "\"pngFileName\":\"C:\\\\out.png\"");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void PngFileName_RelativeHierarchy_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace("\"pngFileName\":\"out.png\"", "\"pngFileName\":\"sub\\\\out.png\"");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void PngFileName_DotAndDotDot_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"pngFileName\":\"out.png\"", "\"pngFileName\":\".\"")), manifest, MakePngDirectory()));
            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"pngFileName\":\"out.png\"", "\"pngFileName\":\"..\"")), manifest, MakePngDirectory()));
        }

        [Test]
        public void PngFileName_SeparatorAndNonPng_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"pngFileName\":\"out.png\"", "\"pngFileName\":\"a/b.png\"")), manifest, MakePngDirectory()));
            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"pngFileName\":\"out.png\"", "\"pngFileName\":\"out.txt\"")), manifest, MakePngDirectory()));
        }

        [Test]
        public void UnknownEnum_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"captureSource\":1", "\"captureSource\":99")), manifest, MakePngDirectory()));
        }

        [Test]
        public void NoneEnum_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"captureSource\":1", "\"captureSource\":0")), manifest, MakePngDirectory()));
        }

        [Test]
        public void InvalidImageRect_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"imageRect\":{\"x\":0,\"y\":0,\"width\":2", "\"imageRect\":{\"x\":0,\"y\":0,\"width\":0")), manifest, MakePngDirectory()));
        }

        [Test]
        public void InvalidArrayIndex_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"arrayIndex\":0", "\"arrayIndex\":-1")), manifest, MakePngDirectory()));
        }

        [Test]
        public void InvalidPixelFormat_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"pixelLayout\":{\"format\":1", "\"pixelLayout\":{\"format\":0")), manifest, MakePngDirectory()));
        }

        [Test]
        public void InvalidTiming_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"predictedDisplayPeriodSeconds\":0.01", "\"predictedDisplayPeriodSeconds\":0")), manifest, MakePngDirectory()));
        }

        [Test]
        public void AvailablePose_ZeroQuaternion_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace(
                "\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}",
                "\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":0}}");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void AvailablePose_NaNInfinity_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"position\":{\"x\":1", "\"position\":{\"x\":NaN")), manifest, MakePngDirectory()));
            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(GoldenJson.Replace("\"position\":{\"x\":1", "\"position\":{\"x\":Infinity")), manifest, MakePngDirectory()));
        }

        [Test]
        public void UnavailablePose_NonDefaultValues_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace(
                "\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}",
                "\"headPose\":{\"available\":false,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":0}}");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void ConstructorException_WrappedInInvalidData()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace("\"imageRect\":{\"x\":0,\"y\":0,\"width\":2", "\"imageRect\":{\"x\":0,\"y\":0,\"width\":0");

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));

            Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void NonIdentityQuaternion_RoundTrip_ByteExact()
        {
            TraceRunManifest manifest = MakeManifest();
            CaptureRunReference run = MakeRun(manifest);
            CaptureFrameRequest request = MakeRequest();
            CapturePoseSample head = new CapturePoseSample(new Vector3(0.5f, -1.0f, 2.5f), new Quaternion(0.1f, 0.2f, 0.3f, 0.4f));
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), head, MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            CaptureFramePngArtifact restored = Deserialize(bytes, manifest, MakePngDirectory());

            Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(restored), Is.EqualTo(bytes));
        }

        [Test]
        public void NonUnitQuaternionJson_Rejected()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string modified = GoldenJson.Replace(
                "\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}",
                "\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0.1,\"y\":0.2,\"z\":0.3,\"w\":0.4}}");

            Assert.Throws<InvalidDataException>(() => Deserialize(Encoding.UTF8.GetBytes(modified), manifest, MakePngDirectory()));
        }

        [Test]
        public void PngDirectory_TrailingSeparatorAndDriveRoot()
        {
            TraceRunManifest manifest;
            MakeGoldenArtifact(out manifest);

            string baseDir = Path.Combine(Path.GetTempPath(), "zantetsuken-dirsep-" + Guid.NewGuid().ToString("N"));
            string expected = Path.GetFullPath(Path.Combine(baseDir, "out.png"));

            CaptureFramePngArtifact without = Deserialize(GoldenBytes(), manifest, baseDir);
            Assert.That(without.PngReceipt.DestinationPath, Is.EqualTo(expected));

            CaptureFramePngArtifact with = Deserialize(GoldenBytes(), manifest, baseDir + Path.DirectorySeparatorChar);
            Assert.That(with.PngReceipt.DestinationPath, Is.EqualTo(expected));

            string root = Path.GetPathRoot(baseDir);
            CaptureFramePngArtifact atRoot = Deserialize(GoldenBytes(), manifest, root);
            Assert.That(atRoot.PngReceipt.DestinationPath, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "out.png"))));
        }
    }
}
