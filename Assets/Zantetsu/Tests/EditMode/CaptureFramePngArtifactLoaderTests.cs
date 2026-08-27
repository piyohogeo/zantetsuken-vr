using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactLoaderTests
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

        private static CaptureFrameRequest MakeRequest(long captureFrameId = 10, long unityFrameId = 20)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, unityFrameId, 3, 4, captureFrameId, 30, 1, 5, 6, 7, 8u, 9);
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

        private static CaptureFrameRecord MakeRecord(TraceRunManifest manifest, long captureFrameId, out CaptureFrameRequest request)
        {
            CaptureRunReference run = new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
            request = MakeRequest(captureFrameId);
            return new CaptureFrameRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
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

        private static void EnqueuePng(CaptureFramePngQueue queue, CaptureFrameRequest request, byte[] bytes)
        {
            NativeArray<byte> arr = new NativeArray<byte>(bytes, Allocator.Temp);
            try
            {
                Assert.That(queue.TryEnqueue(request, arr), Is.True);
            }
            catch
            {
                if (arr.IsCreated)
                {
                    arr.Dispose();
                }

                throw;
            }
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-loader-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// Saves a real PNG through <see cref="CaptureFramePngFileStore"/> (so the
        /// receipt is never forged) and a real sidecar through
        /// <see cref="CaptureFramePngArtifactWriter"/>, returning the saved artifact.
        /// </summary>
        private static CaptureFramePngArtifact SaveArtifact(
            TraceRunManifest manifest,
            long captureFrameId,
            string dir,
            string pngFileName,
            string sidecarFileName,
            byte[] pngBytes)
        {
            CaptureFramePngFileStore pngFileStore = new CaptureFramePngFileStore();
            NativeArray<byte> png = new NativeArray<byte>(pngBytes, Allocator.Temp);
            CaptureFramePngSaveReceipt receipt;
            try
            {
                receipt = pngFileStore.SaveAtomicWithReceipt(Path.Combine(dir, pngFileName), png);
            }
            finally
            {
                png.Dispose();
            }

            CaptureFrameRecord record = MakeRecord(manifest, captureFrameId, out CaptureFrameRequest request);
            CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());
            writer.SaveAtomic(Path.Combine(dir, sidecarFileName), record, request, receipt, out CaptureFramePngArtifact artifact);
            return artifact;
        }

        private static CaptureFramePngArtifactLoader MakeLoader()
        {
            return new CaptureFramePngArtifactLoader(new CaptureFramePngArtifactFileStore(), new CaptureFramePngArtifactVerifier());
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            CaptureFramePngArtifactFileStore fileStore = new CaptureFramePngArtifactFileStore();
            CaptureFramePngArtifactVerifier verifier = new CaptureFramePngArtifactVerifier();

            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactLoader(null, verifier));
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactLoader(fileStore, null));
        }

        [Test]
        public void LoadVerified_Success_ReturnsArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                CaptureFramePngArtifact loaded = MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.CaptureFrameId, Is.EqualTo(10));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void LoadedArtifact_CanonicalMatchesSavedArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact saved = SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                CaptureFramePngArtifact loaded = MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest);

                Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(loaded), Is.EqualTo(CaptureFramePngArtifactCodec.SerializeCanonical(saved)));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Integration_PreparerCompletionWriterLoader()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 77, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));
                CaptureFramePngArtifactCompletionWriter completionWriter =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "frame.png");
                    Assert.That(preparer.TrySaveNext(queue, pngPath, out CaptureFramePngArtifact prepared), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    string sidecarPath = Path.Combine(dir, "frame.json");
                    completionWriter.SaveAtomic(sidecarPath, prepared.FrameRecord.Request, prepared.PngReceipt, out _);

                    CaptureFramePngArtifact loaded = MakeLoader().LoadVerified(sidecarPath, manifest);

                    Assert.That(loaded, Is.Not.Null);
                    Assert.That(loaded.CaptureFrameId, Is.EqualTo(77));
                    Assert.That(File.Exists(pngPath), Is.True);
                    Assert.That(File.Exists(sidecarPath), Is.True);
                }
                finally
                {
                    queue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SidecarMissing_FileNotFoundException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                Assert.Throws<FileNotFoundException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "missing.json"), manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngMissing_FileNotFoundException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));
                File.Delete(Path.Combine(dir, "frame.png"));

                Assert.Throws<FileNotFoundException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngParentDirectoryMissing_DirectoryNotFoundException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            string pngDir = Path.Combine(dir, "pngdir");
            try
            {
                Directory.CreateDirectory(pngDir);
                SaveArtifact(manifest, 10, pngDir, "frame.png", "frame.json", MakePngBytes(32));

                // Remove the directory that holds the PNG (and sidecar).
                Directory.Delete(pngDir, true);

                Assert.Throws<DirectoryNotFoundException>(() => MakeLoader().LoadVerified(Path.Combine(pngDir, "frame.json"), manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngShorterByOne_InvalidDataException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                byte[] pngBytes = MakePngBytes(32);
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", pngBytes);

                string pngPath = Path.Combine(dir, "frame.png");
                File.WriteAllBytes(pngPath, Truncate(pngBytes, pngBytes.Length - 1));

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngLongerByOne_InvalidDataException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                byte[] pngBytes = MakePngBytes(32);
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", pngBytes);

                string pngPath = Path.Combine(dir, "frame.png");
                byte[] longer = new byte[pngBytes.Length + 1];
                Array.Copy(pngBytes, longer, pngBytes.Length);
                longer[pngBytes.Length] = 0xAA;
                File.WriteAllBytes(pngPath, longer);

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngSameLengthModified_InvalidDataException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                byte[] pngBytes = MakePngBytes(32);
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", pngBytes);

                string pngPath = Path.Combine(dir, "frame.png");
                byte[] modified = (byte[])pngBytes.Clone();
                modified[10] ^= 0xFF;
                File.WriteAllBytes(pngPath, modified);

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SidecarCorrupt_InvalidDataException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                string sidecarPath = Path.Combine(dir, "frame.json");
                File.WriteAllBytes(sidecarPath, new byte[] { 0x7B, 0x20, 0x27, 0x6E, 0x6F, 0x74, 0x20, 0x6A, 0x73, 0x6F, 0x6E }); // "{ 'not json"

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(sidecarPath, manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SidecarNonCanonicalWhitespace_InvalidDataException()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                string sidecarPath = Path.Combine(dir, "frame.json");
                byte[] canonical = File.ReadAllBytes(sidecarPath);
                byte[] withNewline = new byte[canonical.Length + 1];
                Array.Copy(canonical, withNewline, canonical.Length);
                withNewline[canonical.Length] = (byte)'\n';
                File.WriteAllBytes(sidecarPath, withNewline);

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(sidecarPath, manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void RunManifestMismatch_InvalidDataException()
        {
            TraceRunManifest manifest = MakeManifest(1);
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                TraceRunManifest otherManifest = MakeManifest(2);
                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), otherManifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SidecarCorrupt_PngAlsoMissing_SidecarErrorFirst()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                string sidecarPath = Path.Combine(dir, "frame.json");
                File.WriteAllBytes(sidecarPath, new byte[] { 0x7B, 0x20, 0x27, 0x6E, 0x6F, 0x74, 0x20, 0x6A, 0x73, 0x6F, 0x6E });
                File.Delete(Path.Combine(dir, "frame.png"));

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(sidecarPath, manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ManifestMismatch_PngAlsoMissing_SidecarErrorFirst()
        {
            TraceRunManifest manifest = MakeManifest(1);
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));
                File.Delete(Path.Combine(dir, "frame.png"));

                TraceRunManifest otherManifest = MakeManifest(2);
                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), otherManifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void AfterSuccess_FilesRenameAndDeleteable()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));

                MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest);

                string pngPath = Path.Combine(dir, "frame.png");
                string sidecarPath = Path.Combine(dir, "frame.json");
                string movedPng = Path.Combine(dir, "frame.moved.png");

                File.Move(pngPath, movedPng);
                File.Delete(sidecarPath);

                Assert.That(File.Exists(movedPng), Is.True);
                Assert.That(File.Exists(sidecarPath), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void AfterFailure_FilesRenameAndDeleteable()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                byte[] pngBytes = MakePngBytes(32);
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", pngBytes);

                string pngPath = Path.Combine(dir, "frame.png");
                byte[] modified = (byte[])pngBytes.Clone();
                modified[10] ^= 0xFF;
                File.WriteAllBytes(pngPath, modified);

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(Path.Combine(dir, "frame.json"), manifest));

                string sidecarPath = Path.Combine(dir, "frame.json");
                string movedPng = Path.Combine(dir, "frame.moved.png");
                File.Move(pngPath, movedPng);
                File.Delete(sidecarPath);

                Assert.That(File.Exists(movedPng), Is.True);
                Assert.That(File.Exists(sidecarPath), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Success_DoesNotModifyFiles()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                byte[] pngBytes = MakePngBytes(32);
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", pngBytes);

                string pngPath = Path.Combine(dir, "frame.png");
                string sidecarPath = Path.Combine(dir, "frame.json");
                byte[] pngBefore = File.ReadAllBytes(pngPath);
                byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
                DateTime pngMtime = File.GetLastWriteTimeUtc(pngPath);
                DateTime sidecarMtime = File.GetLastWriteTimeUtc(sidecarPath);

                MakeLoader().LoadVerified(sidecarPath, manifest);

                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBefore));
                Assert.That(File.ReadAllBytes(sidecarPath), Is.EqualTo(sidecarBefore));
                Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(pngMtime));
                Assert.That(File.GetLastWriteTimeUtc(sidecarPath), Is.EqualTo(sidecarMtime));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Failure_DoesNotModifyFiles()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                byte[] pngBytes = MakePngBytes(32);
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", pngBytes);

                string pngPath = Path.Combine(dir, "frame.png");
                string sidecarPath = Path.Combine(dir, "frame.json");
                byte[] sidecarBefore = File.ReadAllBytes(sidecarPath);
                DateTime sidecarMtime = File.GetLastWriteTimeUtc(sidecarPath);

                byte[] modified = (byte[])pngBytes.Clone();
                modified[10] ^= 0xFF;
                File.WriteAllBytes(pngPath, modified);
                byte[] pngBefore = File.ReadAllBytes(pngPath);
                DateTime pngMtime = File.GetLastWriteTimeUtc(pngPath);

                Assert.Throws<InvalidDataException>(() => MakeLoader().LoadVerified(sidecarPath, manifest));

                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBefore));
                Assert.That(File.ReadAllBytes(sidecarPath), Is.EqualTo(sidecarBefore));
                Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(pngMtime));
                Assert.That(File.GetLastWriteTimeUtc(sidecarPath), Is.EqualTo(sidecarMtime));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SameLoaderInstance_ReusableSequentially()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "a.png", "a.json", MakePngBytes(32));
                SaveArtifact(manifest, 11, dir, "b.png", "b.json", MakePngBytes(40));

                CaptureFramePngArtifactLoader loader = MakeLoader();

                CaptureFramePngArtifact a = loader.LoadVerified(Path.Combine(dir, "a.json"), manifest);
                CaptureFramePngArtifact b = loader.LoadVerified(Path.Combine(dir, "b.json"), manifest);

                Assert.That(a.CaptureFrameId, Is.EqualTo(10));
                Assert.That(b.CaptureFrameId, Is.EqualTo(11));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void FileStoreLoad_Alone_SucceedsWithoutPng()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                SaveArtifact(manifest, 10, dir, "frame.png", "frame.json", MakePngBytes(32));
                File.Delete(Path.Combine(dir, "frame.png"));

                CaptureFramePngArtifact loaded = new CaptureFramePngArtifactFileStore().Load(Path.Combine(dir, "frame.json"), manifest);

                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded.CaptureFrameId, Is.EqualTo(10));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        private static byte[] Truncate(byte[] bytes, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(bytes, result, length);
            return result;
        }
    }
}
