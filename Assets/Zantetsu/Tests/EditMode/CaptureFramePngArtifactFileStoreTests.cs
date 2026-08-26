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
    public class CaptureFramePngArtifactFileStoreTests
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

        private static CaptureFramePngSaveReceipt MakePngReceipt(string path)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);

            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, 32, FixedPngHash });
        }

        private static CaptureFramePngArtifact MakeArtifact(TraceRunManifest manifest, string pngPath)
        {
            CaptureRunReference run = new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
            CaptureFramePngSaveReceipt pngReceipt = MakePngReceipt(pngPath);
            return new CaptureFramePngArtifact(record, request, pngReceipt);
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-sidecar-" + Guid.NewGuid().ToString("N"));
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

        private static string IndependentSha256(string path)
        {
            byte[] fileBytes = File.ReadAllBytes(path);
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(fileBytes));
            }
        }

        [Test]
        public void SaveThenLoad_RoundTrip_AllFields()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                store.SaveAtomic(Path.Combine(dir, "out.json"), artifact);
                CaptureFramePngArtifact loaded = store.Load(Path.Combine(dir, "out.json"), manifest);

                Assert.That(loaded.CaptureFrameId, Is.EqualTo(artifact.CaptureFrameId));
                Assert.That(loaded.FrameRecord.TestRunId, Is.EqualTo(artifact.FrameRecord.TestRunId));
                Assert.That(loaded.FrameRecord.BuildId, Is.EqualTo(artifact.FrameRecord.BuildId));
                Assert.That(loaded.FrameRecord.SceneId, Is.EqualTo(artifact.FrameRecord.SceneId));
                Assert.That(loaded.FrameRecord.RandomSeed, Is.EqualTo(artifact.FrameRecord.RandomSeed));
                Assert.That(loaded.FrameRecord.HeadPose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
                Assert.That(loaded.FrameRecord.Timing.PredictedDisplayTimeSeconds, Is.EqualTo(0.5));
                Assert.That(loaded.PngReceipt.ContentSha256, Is.EqualTo(FixedPngHash));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NonIdentityQuaternion_RoundTrip_ByteExact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureRunReference run = new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
                CaptureFrameRequest request = MakeRequest();
                CapturePoseSample head = new CapturePoseSample(new Vector3(0.5f, -1.0f, 2.5f), new Quaternion(0.1f, 0.2f, 0.3f, 0.4f));
                CaptureFrameRecord record = MakeRecord(run, request, MakeTiming(), head, MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f));
                CaptureFramePngArtifact artifact = new CaptureFramePngArtifact(record, request, MakePngReceipt(Path.Combine(dir, "out.png")));

                byte[] originalBytes = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();
                store.SaveAtomic(Path.Combine(dir, "out.json"), artifact);
                CaptureFramePngArtifact loaded = store.Load(Path.Combine(dir, "out.json"), manifest);

                Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(loaded), Is.EqualTo(originalBytes));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Receipt_PathByteCountHash()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifactSaveReceipt receipt = store.SaveAtomic(sidecar, artifact);

                Assert.That(receipt.DestinationPath, Is.EqualTo(Path.GetFullPath(sidecar)));
                Assert.That(receipt.ByteCount, Is.EqualTo(new FileInfo(sidecar).Length));
                Assert.That(receipt.ContentSha256, Is.EqualTo(CaptureFramePngArtifactCodec.ComputeContentSha256(artifact)));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void ReceiptHash_MatchesIndependentSha256()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifactSaveReceipt receipt = store.SaveAtomic(sidecar, artifact);

                Assert.That(receipt.ContentSha256, Is.EqualTo(IndependentSha256(sidecar)));
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
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dirA, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dirB, "out.json");
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(sidecar, artifact));

                Assert.That(File.Exists(sidecar), Is.False);
                Assert.That(Directory.GetFileSystemEntries(dirB), Is.Empty);
            }
            finally
            {
                DeleteTempDir(dirA);
                DeleteTempDir(dirB);
            }
        }

        [Test]
        public void InvalidPaths_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                Assert.Throws<ArgumentNullException>(() => store.SaveAtomic(null, artifact));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(string.Empty, artifact));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic("   ", artifact));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic("relative.json", artifact));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(@"C:drive-relative.json", artifact));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(@"\current-drive-rooted.json", artifact));
                Assert.Throws<ArgumentException>(() => store.SaveAtomic(Path.Combine(dir, "out.txt"), artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MissingParentDirectory_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string missing = Path.Combine(dir, "missing");
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(missing, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                Assert.Throws<DirectoryNotFoundException>(() => store.SaveAtomic(Path.Combine(missing, "out.json"), artifact));
                Assert.That(Directory.Exists(missing), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExistingFile_Unchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });

                Assert.Throws<IOException>(() => store.SaveAtomic(sidecar, artifact));
                Assert.That(File.ReadAllBytes(sidecar), Is.EqualTo(new byte[] { 1, 2, 3 }));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExistingDirectory_Unchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                Directory.CreateDirectory(sidecar);

                Assert.Throws<IOException>(() => store.SaveAtomic(sidecar, artifact));
                Assert.That(Directory.Exists(sidecar), Is.True);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveSuccess_NoTempLeftover_AndNoHandle()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                store.SaveAtomic(sidecar, artifact);

                Assert.That(Directory.GetFiles(dir, "*.tmp"), Is.Empty);

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

        [Test]
        public void OversizedSidecar_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string sidecar = Path.Combine(dir, "big.json");
                File.WriteAllBytes(sidecar, new byte[CaptureFramePngArtifactCodec.MaximumCanonicalByteCount + 1]);

                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();
                Assert.Throws<InvalidDataException>(() => store.Load(sidecar, manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void EmptySidecar_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string sidecar = Path.Combine(dir, "empty.json");
                File.WriteAllBytes(sidecar, new byte[0]);

                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();
                Assert.Throws<InvalidDataException>(() => store.Load(sidecar, manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MalformedJson_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string sidecar = Path.Combine(dir, "bad.json");
                File.WriteAllBytes(sidecar, Encoding.UTF8.GetBytes("not json"));

                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();
                Assert.Throws<InvalidDataException>(() => store.Load(sidecar, manifest));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void RunManifestMismatch_Rejected()
        {
            TraceRunManifest manifest = MakeManifest(testRunId: 1);
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                store.SaveAtomic(sidecar, artifact);

                TraceRunManifest other = MakeManifest(testRunId: 2);
                Assert.Throws<InvalidDataException>(() => store.Load(sidecar, other));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void LoadAfterPngDeleted_Succeeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string pngPath = Path.Combine(dir, "out.png");
                File.WriteAllBytes(pngPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

                CaptureFramePngArtifact artifact = MakeArtifact(manifest, pngPath);
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                store.SaveAtomic(sidecar, artifact);

                File.Delete(pngPath);
                Assert.That(File.Exists(pngPath), Is.False);

                CaptureFramePngArtifact loaded = store.Load(sidecar, manifest);
                Assert.That(loaded.CaptureFrameId, Is.EqualTo(artifact.CaptureFrameId));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void LoadFailure_ReleasesHandle()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                string sidecar = Path.Combine(dir, "bad.json");
                File.WriteAllBytes(sidecar, Encoding.UTF8.GetBytes("not json"));

                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();
                Assert.Throws<InvalidDataException>(() => store.Load(sidecar, manifest));

                File.Delete(sidecar);
                Assert.That(File.Exists(sidecar), Is.False);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveFailure_RetrySucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 9, 9, 9 });
                Assert.Throws<IOException>(() => store.SaveAtomic(sidecar, artifact));

                string alt = Path.Combine(dir, "alt.json");
                Assert.That(store.SaveAtomic(alt, artifact), Is.Not.Null);
                Assert.That(File.Exists(alt), Is.True);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Save_DoesNotMutateArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, Path.Combine(dir, "out.png"));
                CaptureFramePngArtifactFileStore store = new CaptureFramePngArtifactFileStore();

                string before = CaptureFramePngArtifactCodec.ComputeContentSha256(artifact);
                store.SaveAtomic(Path.Combine(dir, "out.json"), artifact);
                string after = CaptureFramePngArtifactCodec.ComputeContentSha256(artifact);

                Assert.That(after, Is.EqualTo(before));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }
    }
}
