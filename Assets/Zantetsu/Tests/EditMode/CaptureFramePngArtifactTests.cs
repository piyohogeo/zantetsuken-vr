using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

        private static CaptureRunReference MakeRun(long testRunId = 1)
        {
            TraceRunManifest manifest = MakeManifest(testRunId);
            return new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameRequest MakeRequest(
            long captureFrameId = 10,
            long unityFrameId = 20,
            long openXRFrameId = 30,
            long testRunId = 1,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left,
            int rectX = 0,
            int rectY = 0,
            int rectWidth = 2,
            int rectHeight = 2,
            int arrayIndex = 0,
            uint objectGeneration = 8u)
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
                objectGeneration,
                9);

            return new CaptureFrameRequest(
                context,
                source,
                eye,
                new CaptureImageRect(rectX, rectY, rectWidth, rectHeight),
                arrayIndex,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRecord MakeRecord(CaptureRunReference run, CaptureFrameRequest request)
        {
            return new CaptureFrameRecord(
                run,
                request,
                new CaptureFrameTiming(1.0, 1.0 / 90.0, true, 3.5, 1.25, 7L),
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity),
                new CapturePoseSample(new Vector3(1f, 0f, 0f), Quaternion.identity),
                new CapturePoseSample(new Vector3(-1f, 0f, 0f), Quaternion.identity),
                1);
        }

        private static CaptureFramePngSaveReceipt SavePngToNewDir(out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "zantetsuken-png-artifact-" + Guid.NewGuid().ToString("N"));
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

        [Test]
        public void Construct_Succeeds_WithSameInstances()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, receipt);

                Assert.That(artifact.FrameRecord, Is.SameAs(record));
                Assert.That(artifact.PngReceipt, Is.SameAs(receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ForwardingProperties_MatchSource()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest(captureFrameId: 77);
            CaptureFrameRecord record = MakeRecord(run, request);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, receipt);

                Assert.That(artifact.CaptureFrameId, Is.EqualTo(record.CaptureFrameId));
                Assert.That(artifact.CaptureFrameId, Is.EqualTo(request.TraceContext.CaptureFrameId));
                Assert.That(artifact.DestinationPath, Is.EqualTo(receipt.DestinationPath));
                Assert.That(artifact.PngByteCount, Is.EqualTo(receipt.ByteCount));
                Assert.That(artifact.PngContentSha256, Is.EqualTo(receipt.ContentSha256));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NullFrameRecord_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifact(null, request, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NullPngReceipt_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request);

            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifact(record, request, null));
        }

        [Test]
        public void DefaultSavedRequest_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, default, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void CaptureFrameIdMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(captureFrameId: 10));
            CaptureFrameRequest variant = MakeRequest(captureFrameId: 11);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void TestRunIdMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun(testRunId: 1);
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(testRunId: 1));
            CaptureFrameRequest variant = MakeRequest(testRunId: 2);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SourceMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(source: CaptureSource.UnityRenderTexture));
            CaptureFrameRequest variant = MakeRequest(source: CaptureSource.OpenXRProjection);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void EyeMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(eye: CaptureEye.Left));
            CaptureFrameRequest variant = MakeRequest(eye: CaptureEye.Right);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ImageRectMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(rectX: 0));
            CaptureFrameRequest variant = MakeRequest(rectX: 1);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ArrayIndexMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(arrayIndex: 0));
            CaptureFrameRequest variant = MakeRequest(arrayIndex: 1);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PixelLayoutMismatch_Rejected()
        {
            // PixelLayout is derived from the image rectangle dimensions, so a
            // width change is a pixel-layout (and image-rect) mismatch.
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(rectWidth: 2));
            CaptureFrameRequest variant = MakeRequest(rectWidth: 3);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void TraceContextNonFrameIdMismatch_Rejected()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRecord record = MakeRecord(run, MakeRequest(objectGeneration: 8u));
            CaptureFrameRequest variant = MakeRequest(objectGeneration: 9u);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);

                Assert.Throws<ArgumentException>(() => new CaptureFramePngArtifact(record, variant, receipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ReceiptValues_ReadableAfterPngDeleted()
        {
            CaptureRunReference run = MakeRun();
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request);

            string dir = null;
            try
            {
                CaptureFramePngSaveReceipt receipt = SavePngToNewDir(out dir);
                CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, receipt);

                string expectedPath = receipt.DestinationPath;
                int expectedByteCount = receipt.ByteCount;
                string expectedHash = receipt.ContentSha256;

                File.Delete(expectedPath);

                Assert.That(File.Exists(expectedPath), Is.False);
                Assert.That(artifact.DestinationPath, Is.EqualTo(expectedPath));
                Assert.That(artifact.PngByteCount, Is.EqualTo(expectedByteCount));
                Assert.That(artifact.PngContentSha256, Is.EqualTo(expectedHash));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NoPublicSetters()
        {
            foreach (PropertyInfo property in typeof(CaptureFramePngArtifact).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, property.Name + " must not have a public setter.");
            }
        }

        [Test]
        public void NoForbiddenFields()
        {
            foreach (FieldInfo field in typeof(CaptureFramePngArtifact).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, "Unexpected array field: " + field.Name);
                Assert.That(typeof(Stream).IsAssignableFrom(field.FieldType), Is.False, "Unexpected Stream field: " + field.Name);
                Assert.That(typeof(FileInfo).IsAssignableFrom(field.FieldType), Is.False, "Unexpected FileInfo field: " + field.Name);
                string name = field.FieldType.FullName ?? field.FieldType.Name;
                Assert.That(name.IndexOf("NativeArray", StringComparison.Ordinal), Is.LessThan(0), "Unexpected NativeArray field: " + field.Name);
            }
        }

        [Test]
        public void NotIDisposable()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifact)), Is.False);
        }

        [Test]
        public void NoDuplicateForwardedFields()
        {
            foreach (FieldInfo field in typeof(CaptureFramePngArtifact).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.Name.IndexOf("CaptureFrameId", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(field.Name.IndexOf("DestinationPath", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(field.Name.IndexOf("PngByteCount", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(field.Name.IndexOf("PngContentSha256", StringComparison.Ordinal), Is.LessThan(0));
            }
        }
    }
}
