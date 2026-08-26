using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactCodecTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FixedPngHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string GoldenHashHex = "d68015983bec043935c73f18887cd3ca50a200eae1d69b4cbf87fca4bb008e66";

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

        private static NativeArray<byte> MakePng(int length)
        {
            NativeArray<byte> png = new NativeArray<byte>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < 8; i++)
            {
                png[i] = PngSignature[i];
            }

            for (int i = 8; i < length; i++)
            {
                png[i] = (byte)(i & 0xFF);
            }

            return png;
        }

        private static TraceRunManifest MakeManifest(string buildId = "build-1", long testRunId = 1)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                buildId,
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

        private static CaptureRunReference MakeRun(string buildId = "build-1", long testRunId = 1)
        {
            TraceRunManifest manifest = MakeManifest(buildId, testRunId);
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

        private static CaptureFrameTiming MakeTiming(
            double displayTimeSeconds = 0.5,
            double displayPeriodSeconds = 0.01,
            bool shouldRender = true,
            double appGpuMs = 3.5,
            double compositorGpuMs = 1.25,
            long droppedFrames = 7)
        {
            return new CaptureFrameTiming(displayTimeSeconds, displayPeriodSeconds, shouldRender, appGpuMs, compositorGpuMs, droppedFrames);
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

        private static CaptureFramePngSaveReceipt MakeReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);

            Assert.That(ctor, Is.Not.Null, "Internal (string,int,string) constructor must exist.");
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static CaptureFramePngSaveReceipt SavePngToNewDir(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "zantetsuken-png-artifact-codec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            NativeArray<byte> png = MakePng(32);
            try
            {
                CaptureFramePngFileStore store = new CaptureFramePngFileStore();
                return store.SaveAtomicWithReceipt(Path.Combine(dir, "out.png"), png);
            }
            finally
            {
                png.Dispose();
            }
        }

        private static void DeleteTempDir(string dir)
        {
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0x0F];
            }

            return new string(chars);
        }

        private static CaptureFramePngArtifact MakeGoldenArtifact()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngSaveReceipt receipt = MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash);
            return new CaptureFramePngArtifact(record, request, receipt);
        }

        [Test]
        public void NullArtifact_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => CaptureFramePngArtifactCodec.SerializeCanonical(null));
            Assert.Throws<ArgumentNullException>(() => CaptureFramePngArtifactCodec.ComputeContentSha256(null));
        }

        [Test]
        public void NoUtf8Bom_NoWhitespace_NoTrailingNewline()
        {
            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(MakeGoldenArtifact());
            string json = Encoding.UTF8.GetString(bytes);

            Assert.That(bytes[0], Is.Not.EqualTo(0xEF), "UTF-8 BOM must be absent.");
            foreach (char c in json)
            {
                Assert.That(char.IsWhiteSpace(c), Is.False, "Canonical JSON must contain no whitespace.");
            }

            Assert.That(json.StartsWith("{", StringComparison.Ordinal), Is.True);
            Assert.That(json.EndsWith("}", StringComparison.Ordinal), Is.True);
        }

        [Test]
        public void GoldenCanonicalJson()
        {
            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(MakeGoldenArtifact());

            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(GoldenJson));
        }

        [Test]
        public void GoldenHash()
        {
            Assert.That(CaptureFramePngArtifactCodec.ComputeContentSha256(MakeGoldenArtifact()), Is.EqualTo(GoldenHashHex));
        }

        [Test]
        public void AvailablePose_PositionAndRotation()
        {
            CaptureFramePngArtifact artifact = MakeGoldenArtifact();
            string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

            Assert.That(json, Does.Contain("\"headPose\":{\"available\":true,\"position\":{\"x\":1,\"y\":2,\"z\":3},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1}}"));
        }

        [Test]
        public void UnavailablePose_DefaultValues_NotIdentity()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), CapturePoseSample.Unavailable, MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

            Assert.That(json, Does.Contain("\"headPose\":{\"available\":false,\"position\":{\"x\":0,\"y\":0,\"z\":0},\"rotation\":{\"x\":0,\"y\":0,\"z\":0,\"w\":0}}"));
            Assert.That(json, Does.Contain("\"leftControllerPose\":{\"available\":true"));
        }

        [Test]
        public void NegativeZero_AsZero()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameTiming timing = MakeTiming(displayTimeSeconds: -0.0);
            CapturePoseSample head = new CapturePoseSample(new Vector3(-0f, -0f, -0f), Quaternion.identity);
            CaptureFrameRecord record = MakeRecord(run, request, timing, head, MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

            Assert.That(json, Does.Contain("\"predictedDisplayTimeSeconds\":0"));
            Assert.That(json, Does.Contain("\"position\":{\"x\":0,\"y\":0,\"z\":0}"));
            Assert.That(json, Does.Not.Contain("-0"));
        }

        [Test]
        public void NonAsciiUtf8RoundTrip()
        {
            CaptureRunReference run = MakeRun(buildId: "日本語ビルド");
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            string json = Encoding.UTF8.GetString(bytes);

            Assert.That(json, Does.Contain("日本語ビルド"));
            Assert.That(Encoding.UTF8.GetBytes(json), Is.EqualTo(bytes));
        }

        [Test]
        public void StringEscaping()
        {
            CaptureRunReference run = MakeRun(buildId: "a\"b\\c\nd\te");
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

            Assert.That(json.IndexOf('\n'), Is.LessThan(0));
            Assert.That(json.IndexOf('\r'), Is.LessThan(0));
            Assert.That(json.IndexOf('\t'), Is.LessThan(0));
            Assert.That(json, Does.Contain("\\\""));
            Assert.That(json, Does.Contain("\\\\"));
            Assert.That(json, Does.Contain("\\n"));
            Assert.That(json, Does.Contain("\\t"));
        }

        [Test]
        public void NoAbsolutePath_OnlyPngFileName()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));

            string inputPath = Path.Combine(Path.GetTempPath(), "s3cret-dir-xyz", "out.png");
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(inputPath, 32, FixedPngHash));

            string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

            Assert.That(json, Does.Contain("\"pngFileName\":\"out.png\""));
            Assert.That(json.IndexOf("s3cret-dir-xyz", StringComparison.Ordinal), Is.LessThan(0));
            Assert.That(json.IndexOf(Path.GetDirectoryName(inputPath), StringComparison.Ordinal), Is.LessThan(0));
            Assert.That(json.IndexOf(inputPath, StringComparison.Ordinal), Is.LessThan(0));
        }

        [Test]
        public void PngByteCountAndHash_MatchReceipt()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);
                CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, receipt);

                string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

                Assert.That(json, Does.Contain("\"pngByteCount\":" + receipt.ByteCount));
                Assert.That(json, Does.Contain("\"pngContentSha256\":\"" + receipt.ContentSha256 + "\""));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void RunManifestHash_MatchRecord()
        {
            CaptureFramePngArtifact artifact = MakeGoldenArtifact();

            string json = Encoding.UTF8.GetString(CaptureFramePngArtifactCodec.SerializeCanonical(artifact));

            Assert.That(json, Does.Contain("\"runManifestContentSha256\":\"" + artifact.FrameRecord.RunManifestContentSha256 + "\""));
        }

        [Test]
        public void Deterministic_AcrossCalls()
        {
            CaptureFramePngArtifact artifact = MakeGoldenArtifact();

            byte[] first = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            byte[] second = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

            Assert.That(second, Is.EqualTo(first));
            Assert.That(CaptureFramePngArtifactCodec.ComputeContentSha256(artifact), Is.EqualTo(CaptureFramePngArtifactCodec.ComputeContentSha256(artifact)));
        }

        [Test]
        public void CultureIndependent()
        {
            CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                CaptureFramePngArtifact artifact = MakeGoldenArtifact();
                byte[] invariantBytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
                string invariantHash = CaptureFramePngArtifactCodec.ComputeContentSha256(artifact);

                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                byte[] deBytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
                string deHash = CaptureFramePngArtifactCodec.ComputeContentSha256(artifact);

                Assert.That(deBytes, Is.EqualTo(invariantBytes));
                Assert.That(deHash, Is.EqualTo(invariantHash));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void Hash_MatchesIndependentSha256()
        {
            CaptureFramePngArtifact artifact = MakeGoldenArtifact();

            byte[] bytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            string independent;
            using (SHA256 sha = SHA256.Create())
            {
                independent = ToLowerHex(sha.ComputeHash(bytes));
            }

            Assert.That(CaptureFramePngArtifactCodec.ComputeContentSha256(artifact), Is.EqualTo(independent));
        }

        [Test]
        public void ByteLimitExceeded_RejectedWithoutFileIo()
        {
            CaptureRunReference run = MakeRun(buildId: new string('a', 64400));
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakeReceipt(@"C:\capture\out.png", 32, FixedPngHash));

            Assert.Throws<InvalidOperationException>(() => CaptureFramePngArtifactCodec.SerializeCanonical(artifact));
        }

        [Test]
        public void SerializeAfterPngDeleted_SameBytes()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);
                CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, receipt);

                byte[] before = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
                File.Delete(receipt.DestinationPath);
                byte[] after = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

                Assert.That(after, Is.EqualTo(before));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Serialization_DoesNotMutate()
        {
            CaptureFramePngArtifact artifact = MakeGoldenArtifact();
            string originalHash = CaptureFramePngArtifactCodec.ComputeContentSha256(artifact);

            CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

            Assert.That(CaptureFramePngArtifactCodec.ComputeContentSha256(artifact), Is.EqualTo(originalHash));
            Assert.That(artifact.FrameRecord.CaptureFrameId, Is.EqualTo(10L));
            Assert.That(artifact.PngReceipt.ByteCount, Is.EqualTo(32));
        }
    }
}
