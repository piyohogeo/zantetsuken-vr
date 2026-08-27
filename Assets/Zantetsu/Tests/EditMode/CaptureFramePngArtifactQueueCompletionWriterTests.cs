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
    public class CaptureFramePngArtifactQueueCompletionWriterTests
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

        private static void RegisterRecord(CaptureFrameRecordRegistry registry, TraceRunManifest manifest, long captureFrameId, out CaptureFrameRequest request)
        {
            CaptureFrameRecord record = MakeRecord(manifest, captureFrameId, out request);
            Assert.That(registry.TryRegister(record), Is.True);
        }

        private static CaptureFramePngArtifactQueueCompletionWriter MakeQueueCompletionWriter(CaptureFrameRecordRegistry registry)
        {
            CaptureFramePngArtifactCompletionWriter completionWriter =
                new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore()));
            return new CaptureFramePngArtifactQueueCompletionWriter(completionWriter);
        }

        /// <summary>
        /// Registers a record, enqueues a PNG, and prepares it into the artifact
        /// queue through the public pipeline.
        /// </summary>
        private static void PrepareOneArtifact(
            CaptureFrameRecordRegistry registry,
            TraceRunManifest manifest,
            long captureFrameId,
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            string dir,
            string pngFileName,
            out CaptureFrameRequest request)
        {
            RegisterRecord(registry, manifest, captureFrameId, out request);
            EnqueuePng(pngQueue, request, MakePngBytes(32));

            CaptureFramePngArtifactQueuePreparer queuePreparer =
                new CaptureFramePngArtifactQueuePreparer(new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore())));

            Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, pngFileName)), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-queuecompletion-" + Guid.NewGuid().ToString("N"));
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
        public void Constructor_NullDependency_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactQueueCompletionWriter(null));
        }

        [Test]
        public void NullArtifactQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);

            Assert.Throws<ArgumentNullException>(() => writer.TryCompleteNext(null, "C:\\x\\out.json", out _, out _));
        }

        [Test]
        public void EmptyQueue_None_Null_Null()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            CaptureFramePngArtifact completed = null;
            CaptureFramePngArtifactSaveReceipt receipt = null;
            Assert.That(writer.TryCompleteNext(artifactQueue, "C:\\x\\out.json", out completed, out receipt), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.None));
            Assert.That(completed, Is.Null);
            Assert.That(receipt, Is.Null);
        }

        [Test]
        public void EmptyQueue_PathNotValidated()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            Assert.That(writer.TryCompleteNext(artifactQueue, null, out _, out _), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.None));
            Assert.That(writer.TryCompleteNext(artifactQueue, "relative.json", out _, out _), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.None));
            Assert.That(artifactQueue.Count, Is.EqualTo(0));
            Assert.That(artifactQueue.TotalAccepted, Is.EqualTo(0));
            Assert.That(artifactQueue.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void Success_Completed()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    Assert.That(writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "frame.json"), out CaptureFramePngArtifact completed, out CaptureFramePngArtifactSaveReceipt receipt), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.Completed));
                    Assert.That(completed, Is.Not.Null);
                    Assert.That(receipt, Is.Not.Null);
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Success_SidecarExists()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string sidecar = Path.Combine(dir, "frame.json");
                    writer.TryCompleteNext(artifactQueue, sidecar, out _, out _);

                    Assert.That(File.Exists(sidecar), Is.True);
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Success_OnlyHeadDequeued()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    PrepareOneArtifact(registry, manifest, 1, pngQueue, artifactQueue, dir, "a.png", out _);
                    PrepareOneArtifact(registry, manifest, 2, pngQueue, artifactQueue, dir, "b.png", out _);
                    Assert.That(artifactQueue.Count, Is.EqualTo(2));

                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "a.json"), out _, out _);

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.TryDequeue(out CaptureFramePngArtifact remaining), Is.True);
                    Assert.That(remaining.CaptureFrameId, Is.EqualTo(2));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Success_OnlyMatchingRecordRemoved()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "a.png", out _);
                    PrepareOneArtifact(registry, manifest, 11, pngQueue, artifactQueue, dir, "b.png", out _);

                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "a.json"), out _, out _);

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(MakeRequest(10), out _), Is.False);
                    Assert.That(registry.TryGet(MakeRequest(11), out _), Is.True);
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Success_LaterRecordsAndArtifactsKept()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "a.png", out _);
                    PrepareOneArtifact(registry, manifest, 11, pngQueue, artifactQueue, dir, "b.png", out _);

                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "a.json"), out _, out _);

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.TryPeek(out CaptureFramePngArtifact later), Is.True);
                    Assert.That(later.CaptureFrameId, Is.EqualTo(11));
                    Assert.That(registry.TryGet(MakeRequest(11), out _), Is.True);
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Success_CompletedArtifactKeepsFrameRecordAndReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    Assert.That(artifactQueue.TryPeek(out CaptureFramePngArtifact queued), Is.True);
                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "frame.json"), out CaptureFramePngArtifact completed, out _);

                    Assert.That(completed.FrameRecord, Is.SameAs(queued.FrameRecord));
                    Assert.That(completed.PngReceipt, Is.SameAs(queued.PngReceipt));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SidecarReceipt_HasActualPathByteCountSha256()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string sidecar = Path.Combine(dir, "frame.json");
                    writer.TryCompleteNext(artifactQueue, sidecar, out _, out CaptureFramePngArtifactSaveReceipt receipt);

                    byte[] canonical = File.ReadAllBytes(sidecar);
                    Assert.That(receipt.DestinationPath, Is.EqualTo(Path.GetFullPath(sidecar)));
                    Assert.That(receipt.ByteCount, Is.EqualTo(canonical.Length));
                    Assert.That(receipt.ContentSha256, Is.EqualTo(Sha256Hex(canonical)));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SavedSidecar_LoadsAndVerifiesViaLoader()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string sidecar = Path.Combine(dir, "frame.json");
                    writer.TryCompleteNext(artifactQueue, sidecar, out _, out _);

                    CaptureFramePngArtifact loaded = new CaptureFramePngArtifactLoader(
                        new CaptureFramePngArtifactFileStore(),
                        new CaptureFramePngArtifactVerifier()).LoadVerified(sidecar, manifest);

                    Assert.That(loaded, Is.Not.Null);
                    Assert.That(loaded.CaptureFrameId, Is.EqualTo(10));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DestinationExisting_Failure_QueueRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string sidecar = Path.Combine(dir, "frame.json");
                    File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3, 4 });

                    Assert.Throws<IOException>(() => writer.TryCompleteNext(artifactQueue, sidecar, out _, out _));

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MissingParentDir_Failure_QueueRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    // Remove the directory that holds the PNG and must hold the sidecar.
                    Directory.Delete(dir, true);

                    Assert.Throws<DirectoryNotFoundException>(() => writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "frame.json"), out _, out _));

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void InvalidPath_Failure_QueueRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    Assert.Throws<ArgumentException>(() => writer.TryCompleteNext(artifactQueue, "relative.json", out _, out _));

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void SaveFailure_OutArgsNull()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string sidecar = Path.Combine(dir, "frame.json");
                    File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });

                    CaptureFramePngArtifact completed = null;
                    CaptureFramePngArtifactSaveReceipt receipt = null;
                    Assert.Throws<IOException>(() => writer.TryCompleteNext(artifactQueue, sidecar, out completed, out receipt));

                    Assert.That(completed, Is.Null);
                    Assert.That(receipt, Is.Null);
                }
                finally
                {
                    pngQueue.Dispose();
                }
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
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string sidecar = Path.Combine(dir, "frame.json");
                    File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });
                    Assert.Throws<IOException>(() => writer.TryCompleteNext(artifactQueue, sidecar, out _, out _));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));

                    string alt = Path.Combine(dir, "alt.json");
                    Assert.That(writer.TryCompleteNext(artifactQueue, alt, out CaptureFramePngArtifact completed, out _), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.Completed));

                    Assert.That(completed, Is.Not.Null);
                    Assert.That(File.Exists(alt), Is.True);
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(0));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void MultipleArtifacts_FifoCompletion()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    PrepareOneArtifact(registry, manifest, 1, pngQueue, artifactQueue, dir, "a.png", out _);
                    PrepareOneArtifact(registry, manifest, 2, pngQueue, artifactQueue, dir, "b.png", out _);
                    PrepareOneArtifact(registry, manifest, 3, pngQueue, artifactQueue, dir, "c.png", out _);

                    Assert.That(writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "a.json"), out CaptureFramePngArtifact a, out _), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.Completed));
                    Assert.That(writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "b.json"), out CaptureFramePngArtifact b, out _), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.Completed));
                    Assert.That(writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "c.json"), out CaptureFramePngArtifact c, out _), Is.EqualTo(CaptureFramePngArtifactCompletionStatus.Completed));

                    Assert.That(a.CaptureFrameId, Is.EqualTo(1));
                    Assert.That(b.CaptureFrameId, Is.EqualTo(2));
                    Assert.That(c.CaptureFrameId, Is.EqualTo(3));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(0));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void PngUnchanged_SuccessAndFailure()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);

                    string pngPath = Path.Combine(dir, "frame.png");
                    byte[] pngBytes = File.ReadAllBytes(pngPath);
                    DateTime pngMtime = File.GetLastWriteTimeUtc(pngPath);

                    // Failure path: existing destination.
                    string sidecar = Path.Combine(dir, "frame.json");
                    File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });
                    Assert.Throws<IOException>(() => writer.TryCompleteNext(artifactQueue, sidecar, out _, out _));

                    Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBytes));
                    Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(pngMtime));

                    // Success path.
                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "alt.json"), out _, out _);

                    Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(pngBytes));
                    Assert.That(File.GetLastWriteTimeUtc(pngPath), Is.EqualTo(pngMtime));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void AfterCompletion_ReRegisterSameId()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    PrepareOneArtifact(registry, manifest, 10, pngQueue, artifactQueue, dir, "frame.png", out _);
                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "frame.json"), out _, out _);
                    Assert.That(registry.Count, Is.EqualTo(0));

                    CaptureFrameRecord replacement = MakeRecord(manifest, 10, out _);
                    Assert.That(registry.TryRegister(replacement), Is.True);
                    Assert.That(registry.Count, Is.EqualTo(1));
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DoesNotDisposeOrClearDependencies()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactQueueCompletionWriter writer = MakeQueueCompletionWriter(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    PrepareOneArtifact(registry, manifest, 1, pngQueue, artifactQueue, dir, "a.png", out _);
                    PrepareOneArtifact(registry, manifest, 2, pngQueue, artifactQueue, dir, "b.png", out _);

                    writer.TryCompleteNext(artifactQueue, Path.Combine(dir, "a.json"), out _, out _);

                    // The second artifact and its record remain, and the PNG queue is untouched.
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(pngQueue.IsCreated, Is.True);
                }
                finally
                {
                    pngQueue.Dispose();
                }
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NotIDisposable()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactQueueCompletionWriter)), Is.False);
        }
    }
}
