using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactVerifierTests
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

        private static CaptureFramePngArtifact MakeArtifact(TraceRunManifest manifest, CaptureFramePngSaveReceipt pngReceipt)
        {
            CaptureRunReference run = new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            return new CaptureFramePngArtifact(record, request, pngReceipt);
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

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(bytes));
            }
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-verify-" + Guid.NewGuid().ToString("N"));
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

        private static CaptureFramePngArtifact WriteAndMakeArtifact(TraceRunManifest manifest, string path, int length)
        {
            byte[] bytes = MakePngBytes(length);
            File.WriteAllBytes(path, bytes);
            return MakeArtifact(manifest, MakePngReceipt(path, bytes.Length, Sha256Hex(bytes)));
        }

        [Test]
        public void Verify_ValidSavedPng_Succeeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 2000);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();

                Assert.DoesNotThrow(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_AfterSidecarSaveLoad_Succeeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1500);

                CaptureFramePngArtifactFileStore fileStore = new CaptureFramePngArtifactFileStore();
                fileStore.SaveAtomic(Path.Combine(dir, "out.json"), artifact);
                CaptureFramePngArtifact loaded = fileStore.Load(Path.Combine(dir, "out.json"), manifest);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.DoesNotThrow(() => verifier.Verify(loaded));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_DifferentBufferSizes_Succeed()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 3000);

                foreach (int bufferSize in new[] { 1, 7, 64, 65536 })
                {
                    CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier(bufferSize);
                    Assert.DoesNotThrow(() => verifier.Verify(artifact));
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_LargePngSmallBuffer_CrossesChunkBoundaries()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 10000);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier(7);
                Assert.DoesNotThrow(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_PngMissing_FileNotFound()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string missing = Path.Combine(dir, "missing.png");
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, MakePngReceipt(missing, 32, Sha256Hex(MakePngBytes(32))));

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<FileNotFoundException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_ParentDirMissing_DirectoryNotFound()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string missing = Path.Combine(dir, "missing", "out.png");
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, MakePngReceipt(missing, 32, Sha256Hex(MakePngBytes(32))));

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<DirectoryNotFoundException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_TruncatedByOne_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                File.WriteAllBytes(pngPath, MakePngBytes(999));

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_AppendedByOne_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                File.WriteAllBytes(pngPath, MakePngBytes(1001));

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_AlteredByte_SameLength_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                byte[] altered = File.ReadAllBytes(pngPath);
                altered[500] ^= 0x01;
                File.WriteAllBytes(pngPath, altered);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_LengthMismatch_CheckedBeforeHash()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                File.WriteAllBytes(pngPath, MakePngBytes(999));

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                InvalidDataException ex = Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));
                Assert.That(ex.Message, Does.Contain("byte count"));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_EmptyFile_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                File.WriteAllBytes(pngPath, new byte[0]);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_TooShortFile_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                File.WriteAllBytes(pngPath, MakePngBytes(8));

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_Failure_ThenDeleteRename()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                byte[] altered = File.ReadAllBytes(pngPath);
                altered[10] ^= 0xFF;
                File.WriteAllBytes(pngPath, altered);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                Assert.Throws<InvalidDataException>(() => verifier.Verify(artifact));

                File.Delete(pngPath);
                Assert.That(File.Exists(pngPath), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_Success_ThenDeleteRename()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                verifier.Verify(artifact);

                string renamed = Path.Combine(dir, "renamed.png");
                File.Move(pngPath, renamed);
                File.Delete(renamed);
                Assert.That(File.Exists(pngPath), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Verify_DoesNotMutateArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                string hashBefore = artifact.PngContentSha256;
                int byteCountBefore = artifact.PngByteCount;
                long frameIdBefore = artifact.FrameRecord.CaptureFrameId;

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                verifier.Verify(artifact);

                Assert.That(artifact.PngContentSha256, Is.EqualTo(hashBefore));
                Assert.That(artifact.PngByteCount, Is.EqualTo(byteCountBefore));
                Assert.That(artifact.FrameRecord.CaptureFrameId, Is.EqualTo(frameIdBefore));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Constructor_ZeroOrNegativeBuffer_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngArtifactVerifier(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngArtifactVerifier(-1));
        }

        [Test]
        public void Verifier_HasSingleReusableBuffer()
        {
            Type type = typeof(CaptureFramePngArtifactVerifier);

            int byteArrayFields = 0;
            foreach (FieldInfo field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(byte[]))
                {
                    byteArrayFields++;
                    Assert.That(field.IsInitOnly, Is.True, "Read buffer field must be readonly.");
                }
                else
                {
                    Assert.That(field.FieldType.IsArray, Is.False, "Unexpected array field: " + field.Name);
                    Assert.That(typeof(Stream).IsAssignableFrom(field.FieldType), Is.False, "Unexpected Stream field: " + field.Name);
                }
            }

            Assert.That(byteArrayFields, Is.EqualTo(1));
        }

        [Test]
        public void Verify_DoesNotTouchSidecar()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                string sidecarPath = Path.Combine(dir, "out.json");
                byte[] sidecarBytes = Encoding.UTF8.GetBytes("sidecar-content");
                File.WriteAllBytes(sidecarPath, sidecarBytes);

                CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();
                verifier.Verify(artifact);

                Assert.That(File.ReadAllBytes(sidecarPath), Is.EqualTo(sidecarBytes));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void FileStoreLoad_StillSucceedsWithoutPng()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                CaptureFramePngArtifact artifact = WriteAndMakeArtifact(manifest, pngPath, 1000);

                CaptureFramePngArtifactFileStore fileStore = new CaptureFramePngArtifactFileStore();
                fileStore.SaveAtomic(Path.Combine(dir, "out.json"), artifact);

                File.Delete(pngPath);

                CaptureFramePngArtifact loaded = fileStore.Load(Path.Combine(dir, "out.json"), manifest);
                Assert.That(loaded.CaptureFrameId, Is.EqualTo(artifact.CaptureFrameId));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }
    }
}
