using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactCompletionWriterTests
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
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-completion-" + Guid.NewGuid().ToString("N"));
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
        public void Constructor_NullDependencies_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactWriter writer = new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore());

            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactCompletionWriter(null, writer));
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactCompletionWriter(registry, null));
        }

        [Test]
        public void NullReceiptAndDefaultRequest_RejectedBeforeFileCreation()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string sidecar = Path.Combine(dir, "out.json");

                CaptureFramePngArtifact artifact = null;
                Assert.Throws<ArgumentNullException>(() => writer.SaveAtomic(sidecar, request, null, out artifact));
                Assert.That(artifact, Is.Null);
                Assert.That(File.Exists(sidecar), Is.False);

                Assert.Throws<ArgumentException>(() => writer.SaveAtomic(sidecar, default, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out artifact));
                Assert.That(artifact, Is.Null);
                Assert.That(File.Exists(sidecar), Is.False);
                Assert.That(registry.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MissingRecord_InvalidOperationException_FileNotCreated()
        {
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifact artifact = null;
                Assert.Throws<InvalidOperationException>(() => writer.SaveAtomic(sidecar, MakeRequest(10), MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(File.Exists(sidecar), Is.False);
                Assert.That(registry.Count, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MismatchedRequest_Rejected_RegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out _);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                CaptureFrameRequest mismatched = MakeRequest(10, unityFrameId: 99);
                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifact artifact = null;
                Assert.Throws<InvalidOperationException>(() => writer.SaveAtomic(sidecar, mismatched, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(File.Exists(sidecar), Is.False);
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TryGet(MakeRequest(10), out CaptureFrameRecord kept), Is.True);
                Assert.That(kept, Is.SameAs(record));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveAtomic_Success_ReturnsReceiptAndArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifactSaveReceipt receipt = writer.SaveAtomic(sidecar, request, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out CaptureFramePngArtifact artifact);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(artifact, Is.Not.Null);
                Assert.That(File.Exists(sidecar), Is.True);
                Assert.That(artifact.FrameRecord, Is.SameAs(record));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveAtomic_Success_OnlyMatchingRecordRemoved()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord target = MakeRecord(manifest, 10, out CaptureFrameRequest targetRequest);
                CaptureFrameRecord other = MakeRecord(manifest, 11, out _);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(target);
                registry.TryRegister(other);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                writer.SaveAtomic(Path.Combine(dir, "out.json"), targetRequest, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out _);

                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TryGet(MakeRequest(10), out _), Is.False);
                Assert.That(registry.TryGet(MakeRequest(11), out CaptureFrameRecord kept), Is.True);
                Assert.That(kept, Is.SameAs(other));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveAtomic_Success_LoadsAndCanonicalMatches()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactFileStore fileStore = new CaptureFramePngArtifactFileStore();
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(fileStore));

                string sidecar = Path.Combine(dir, "out.json");
                writer.SaveAtomic(sidecar, request, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out CaptureFramePngArtifact artifact);

                CaptureFramePngArtifact loaded = fileStore.Load(sidecar, manifest);
                Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(loaded), Is.EqualTo(CaptureFramePngArtifactCodec.SerializeCanonical(artifact)));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExisting_SaveFails_RegistryUnchanged_ArtifactNull()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3, 4 });

                CaptureFramePngArtifact artifact = null;
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, request, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TryGet(MakeRequest(10), out CaptureFrameRecord kept), Is.True);
                Assert.That(kept, Is.SameAs(record));
                Assert.That(File.ReadAllBytes(sidecar), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MissingParentDirectory_SaveFails_RegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string missing = Path.Combine(dir, "missing");
                CaptureFramePngArtifact artifact = null;
                Assert.Throws<DirectoryNotFoundException>(() => writer.SaveAtomic(Path.Combine(missing, "out.json"), request, MakePngReceipt(Path.Combine(missing, "out.png"), 32, FixedPngHash), out artifact));

                Assert.That(artifact, Is.Null);
                Assert.That(Directory.Exists(missing), Is.False);
                Assert.That(registry.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveFailure_RetrySucceeds_RecordRemovedAtThatPoint()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 9, 9, 9 });
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, request, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out _));
                Assert.That(registry.Count, Is.EqualTo(1));

                string alt = Path.Combine(dir, "alt.json");
                CaptureFramePngArtifactSaveReceipt receipt = writer.SaveAtomic(alt, request, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out CaptureFramePngArtifact artifact);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(artifact, Is.Not.Null);
                Assert.That(File.Exists(alt), Is.True);
                Assert.That(registry.Count, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngNonexistent_SidecarSaveSucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string pngPath = Path.Combine(dir, "nonexistent.png");
                string sidecar = Path.Combine(dir, "out.json");
                CaptureFramePngArtifactSaveReceipt receipt = writer.SaveAtomic(sidecar, request, MakePngReceipt(pngPath, 32, FixedPngHash), out CaptureFramePngArtifact artifact);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(artifact, Is.Not.Null);
                Assert.That(File.Exists(sidecar), Is.True);
                Assert.That(File.Exists(pngPath), Is.False);
                Assert.That(registry.Count, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngFile_UnchangedAfterSuccess()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string pngPath = Path.Combine(dir, "out.png");
                byte[] pngBytes = MakePngBytes(32);
                File.WriteAllBytes(pngPath, pngBytes);
                DateTime beforeMtime = File.GetLastWriteTimeUtc(pngPath);

                writer.SaveAtomic(Path.Combine(dir, "out.json"), request, MakePngReceipt(pngPath, pngBytes.Length, Sha256Hex(pngBytes)), out _);

                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBytes));
                Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(beforeMtime));
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
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                string pngPath = Path.Combine(dir, "out.png");
                byte[] pngBytes = MakePngBytes(32);
                File.WriteAllBytes(pngPath, pngBytes);
                DateTime beforeMtime = File.GetLastWriteTimeUtc(pngPath);

                string sidecar = Path.Combine(dir, "out.json");
                File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });
                Assert.Throws<IOException>(() => writer.SaveAtomic(sidecar, request, MakePngReceipt(pngPath, pngBytes.Length, Sha256Hex(pngBytes)), out _));

                Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBytes));
                Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(beforeMtime));
                Assert.That(registry.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Integration_QueueFileWriterThenCompletionWriter()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 77, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                NativeArray<byte> pngBytes = new NativeArray<byte>(MakePngBytes(32), Allocator.Temp);
                try
                {
                    Assert.That(queue.TryEnqueue(request, pngBytes), Is.True);
                    pngBytes = default;

                    CaptureFramePngQueueFileWriter queueWriter = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());
                    string pngPath = Path.Combine(dir, "frame.png");
                    Assert.That(queueWriter.TrySaveNext(queue, pngPath, out CaptureFrameRequest savedRequest, out CaptureFramePngSaveReceipt pngReceipt), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                    Assert.That(pngReceipt, Is.Not.Null);
                    Assert.That(savedRequest.TraceContext.CaptureFrameId, Is.EqualTo(77));
                    Assert.That(File.Exists(pngPath), Is.True);

                    CaptureFramePngArtifactCompletionWriter writer =
                        new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                    string sidecarPath = Path.Combine(dir, "frame.json");
                    CaptureFramePngArtifactSaveReceipt sidecarReceipt = writer.SaveAtomic(sidecarPath, savedRequest, pngReceipt, out CaptureFramePngArtifact artifact);

                    Assert.That(sidecarReceipt, Is.Not.Null);
                    Assert.That(artifact, Is.Not.Null);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(0));
                    Assert.That(registry.TryGet(savedRequest, out _), Is.False);
                    Assert.That(File.Exists(pngPath), Is.True);
                    Assert.That(File.Exists(sidecarPath), Is.True);
                }
                finally
                {
                    if (pngBytes.IsCreated)
                    {
                        pngBytes.Dispose();
                    }

                    queue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Writer_DoesNotDisposeOrClearDependencies()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactCompletionWriter)), Is.False);

            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord first = MakeRecord(manifest, 10, out CaptureFrameRequest firstRequest);
                CaptureFrameRecord second = MakeRecord(manifest, 11, out CaptureFrameRequest secondRequest);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(first);
                registry.TryRegister(second);
                CaptureFramePngArtifactFileStore fileStore = new CaptureFramePngArtifactFileStore();
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(fileStore));

                writer.SaveAtomic(Path.Combine(dir, "a.json"), firstRequest, MakePngReceipt(Path.Combine(dir, "a.png"), 32, FixedPngHash), out _);

                // The registry still holds the other record and the file store is reusable.
                Assert.That(registry.Count, Is.EqualTo(1));
                Assert.That(registry.TryGet(MakeRequest(11), out _), Is.True);

                writer.SaveAtomic(Path.Combine(dir, "b.json"), secondRequest, MakePngReceipt(Path.Combine(dir, "b.png"), 32, FixedPngHash), out _);
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(File.Exists(Path.Combine(dir, "b.json")), Is.True);
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void AfterSuccess_SameCaptureFrameId_ReRegisterSucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord first = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(first);
                CaptureFramePngArtifactCompletionWriter writer =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));

                writer.SaveAtomic(Path.Combine(dir, "out.json"), request, MakePngReceipt(Path.Combine(dir, "out.png"), 32, FixedPngHash), out _);
                Assert.That(registry.Count, Is.EqualTo(0));

                CaptureFrameRecord replacement = MakeRecord(manifest, 10, out _);
                Assert.That(registry.TryRegister(replacement), Is.True);
                Assert.That(registry.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }
    }
}
