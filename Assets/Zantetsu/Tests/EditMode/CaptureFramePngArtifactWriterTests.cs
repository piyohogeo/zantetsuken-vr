using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactWriterTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FixedPngHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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

        private static CaptureFrameRequest MakeRequest(long captureFrameId = 10)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, 1, 5, 6, 7, 8u, 9);
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

        private static CaptureFrameRecord MakeRecord(TraceRunManifest manifest, out CaptureFrameRequest request)
        {
            CaptureRunReference run = new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
            request = MakeRequest();
            return new CaptureFrameRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
        }

        private static CaptureFramePngSaveReceipt MakePngReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);

            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static byte[] MakePngBytes(int length)
        {
            byte[] bytes = new byte[length];
            bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
            bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
            for (int i = 8; i < length; i++)
            {
                bytes[i] = (byte)(i & 0xFF);
            }

            return bytes;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                const string hex = "0123456789abcdef";
                byte[] hash = sha.ComputeHash(bytes);
                char[] chars = new char[hash.Length * 2];
                for (int i = 0; i < hash.Length; i++)
                {
                    chars[i * 2] = hex[hash[i] >> 4];
                    chars[i * 2 + 1] = hex[hash[i] & 0x0F];
                }

                return new string(chars);
            }
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-writer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDir(string dir)
        {
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void SaveAtomic_Success_ReturnsArtifactAndReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                CaptureFramePngArtifactSaveReceipt receipt = writer.SaveAtomic(Path.Combine(dir, "out.json"), record, request, pngReceipt, out CaptureFramePngArtifact artifact);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(artifact, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(dir, "out.json")), Is.True);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SavedArtifact_LoadsAndCanonicalMatches()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactFileStore fileStore = new CaptureFramePngArtifactFileStore();
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(fileStore);

                string sidecar = Path.Combine(dir, "out.json");
                writer.SaveAtomic(sidecar, record, request, pngReceipt, out CaptureFramePngArtifact artifact);

                CaptureFramePngArtifact loaded = fileStore.Load(sidecar, manifest);

                Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(loaded), Is.EqualTo(CaptureFramePngArtifactCodec.SerializeCanonical(artifact)));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ReturnedArtifact_HoldsSameInstances()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                writer.SaveAtomic(Path.Combine(dir, "out.json"), record, request, pngReceipt, out CaptureFramePngArtifact artifact);

                Assert.That(artifact.FrameRecord, Is.SameAs(record));
                Assert.That(artifact.PngReceipt, Is.SameAs(pngReceipt));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SavedRequestMismatch_RejectedBeforeFileCreation()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out _);
                CaptureFrameRequest mismatched = MakeRequest(11);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifact artifact = null;
                Assert.Throws<ArgumentException>(() => writer.SaveAtomic(sidecar, record, mismatched, pngReceipt, out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(File.Exists(sidecar), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NullArguments_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactWriter(null));

                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifact artifact = null;

                Assert.Throws<ArgumentNullException>(() => writer.SaveAtomic(sidecar, null, request, pngReceipt, out artifact));
                Assert.That(artifact, Is.Null);

                Assert.Throws<ArgumentNullException>(() => writer.SaveAtomic(sidecar, record, request, null, out artifact));
                Assert.That(artifact, Is.Null);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExisting_Unchanged_ArtifactNull()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3, 4 });

                CaptureFramePngArtifact artifact = null;
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, record, request, pngReceipt, out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(File.ReadAllBytes(sidecar), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MissingParentDirectory_ArtifactNull()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                string missing = Path.Combine(dir, "missing");
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(missing, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                CaptureFramePngArtifact artifact = null;
                Assert.Throws<DirectoryNotFoundException>(() => writer.SaveAtomic(Path.Combine(missing, "out.json"), record, request, pngReceipt, out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(Directory.Exists(missing), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DirectoryMismatch_RejectedBeforeFileCreation()
        {
            TraceRunManifest manifest = MakeManifest();
            string dirA = CreateTempDir();
            string dirB = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dirA, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dirB, "out.json");
                CaptureFramePngArtifact artifact = null;
                Assert.Throws<ArgumentException>(() => writer.SaveAtomic(sidecar, record, request, pngReceipt, out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(File.Exists(sidecar), Is.False);
            }
            finally
            {
                DeleteTempDir(dirA);
                DeleteTempDir(dirB);
            }
        }

        [Test]
        public void RetryAfterFailure_Succeeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 9, 9, 9 });
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, record, request, pngReceipt, out _));

                string alt = Path.Combine(dir, "alt.json");
                CaptureFramePngArtifactSaveReceipt receipt = writer.SaveAtomic(alt, record, request, pngReceipt, out CaptureFramePngArtifact artifact);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(artifact, Is.Not.Null);
                Assert.That(File.Exists(alt), Is.True);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SidecarSave_DoesNotCheckPngExistence()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                string pngPath = Path.Combine(dir, "nonexistent.png");
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(pngPath, 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifactSaveReceipt receipt = writer.SaveAtomic(sidecar, record, request, pngReceipt, out CaptureFramePngArtifact artifact);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(artifact, Is.Not.Null);
                Assert.That(File.Exists(sidecar), Is.True);
                Assert.That(File.Exists(pngPath), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngFile_UnchangedAfterSave()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                byte[] pngBytes = MakePngBytes(64);
                File.WriteAllBytes(pngPath, pngBytes);
                DateTime lastWrite = File.GetLastWriteTimeUtc(pngPath);

                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(pngPath, pngBytes.Length, Sha256Hex(pngBytes));
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                writer.SaveAtomic(Path.Combine(dir, "out.json"), record, request, pngReceipt, out _);

                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBytes));
                Assert.That(new FileInfo(pngPath).Length, Is.EqualTo(pngBytes.Length));
                Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(lastWrite));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngFile_UnchangedAfterFailure()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                byte[] pngBytes = MakePngBytes(64);
                File.WriteAllBytes(pngPath, pngBytes);
                DateTime lastWrite = File.GetLastWriteTimeUtc(pngPath);

                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(pngPath, pngBytes.Length, Sha256Hex(pngBytes));
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 1 });
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, record, request, pngReceipt, out _));

                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBytes));
                Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(lastWrite));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void AfterFailure_FilesRenamable()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, out CaptureFrameRequest request);
                CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash);
                CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 1 });
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, record, request, pngReceipt, out _));

                string renamed = Path.Combine(dir, "renamed.json");
                File.Move(sidecar, renamed);
                File.Delete(renamed);
                Assert.That(File.Exists(sidecar), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }
    }
}
