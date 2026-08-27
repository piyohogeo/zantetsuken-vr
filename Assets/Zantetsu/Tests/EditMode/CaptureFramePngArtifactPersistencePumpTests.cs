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
    public class CaptureFramePngArtifactPersistencePumpTests
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

        private static CaptureFramePngArtifactPersistencePump MakePump(CaptureFrameRecordRegistry registry)
        {
            CaptureFramePngArtifactQueuePreparer queuePreparer = new CaptureFramePngArtifactQueuePreparer(
                new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore())));

            CaptureFramePngArtifactQueueCompletionWriter queueCompletionWriter = new CaptureFramePngArtifactQueueCompletionWriter(
                new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore())));

            return new CaptureFramePngArtifactPersistencePump(queuePreparer, queueCompletionWriter);
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-pump-" + Guid.NewGuid().ToString("N"));
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
            CaptureFramePngArtifactQueuePreparer queuePreparer = new CaptureFramePngArtifactQueuePreparer(
                new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore())));
            CaptureFramePngArtifactQueueCompletionWriter queueCompletionWriter = new CaptureFramePngArtifactQueueCompletionWriter(
                new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore())));

            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactPersistencePump(null, queueCompletionWriter));
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactPersistencePump(queuePreparer, null));
        }

        [Test]
        public void NullPngQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            Assert.Throws<ArgumentNullException>(() => pump.TryAdvanceNext(null, artifactQueue, "C:\\x\\out.png", "C:\\x\\out.json", out _, out _));
        }

        [Test]
        public void NullArtifactQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            try
            {
                Assert.Throws<ArgumentNullException>(() => pump.TryAdvanceNext(pngQueue, null, "C:\\x\\out.png", "C:\\x\\out.json", out _, out _));
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void DisposedPngQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            pngQueue.Dispose();

            Assert.Throws<ObjectDisposedException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, "C:\\x\\out.png", "C:\\x\\out.json", out _, out _));
        }

        [Test]
        public void BothQueuesEmpty_None_Null_Null()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
            try
            {
                CaptureFramePngArtifact completed = null;
                CaptureFramePngArtifactSaveReceipt receipt = null;
                Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, "C:\\x\\out.png", "C:\\x\\out.json", out completed, out receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                Assert.That(completed, Is.Null);
                Assert.That(receipt, Is.Null);
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void None_DoesNotValidatePaths()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
            try
            {
                Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, null, null, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, "relative.png", "relative.json", out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void PngOnly_PngPrepared()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
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
        public void PngPrepared_DoesNotValidateSidecarPath()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), null, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
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
        public void PngPrepared_PngQueueDecrementedArtifactQueueIncremented()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);

                    Assert.That(pngQueue.Count, Is.EqualTo(0));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
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
        public void PngPrepared_RegistryRecordKept()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(request, out _), Is.True);
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
        public void PngPrepared_PngExistsSidecarDoesNot()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "frame.png");
                    string sidecarPath = Path.Combine(dir, "frame.json");
                    pump.TryAdvanceNext(pngQueue, artifactQueue, pngPath, sidecarPath, out _, out _);

                    Assert.That(File.Exists(pngPath), Is.True);
                    Assert.That(File.Exists(sidecarPath), Is.False);
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
        public void PngPrepared_OutArgsNull()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    CaptureFramePngArtifact completed = null;
                    CaptureFramePngArtifactSaveReceipt receipt = null;
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out completed, out receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));

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
        public void PendingArtifact_SidecarCompleted()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _); // PngPrepared

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void SidecarCompleted_DoesNotValidatePngPath()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _); // PngPrepared

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, null, Path.Combine(dir, "out.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void SidecarCompleted_ArtifactQueueDecremented()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));

                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);

                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
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
        public void SidecarCompleted_RegistryRecordRemoved()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);
                    Assert.That(registry.Count, Is.EqualTo(1));

                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);

                    Assert.That(registry.Count, Is.EqualTo(0));
                    Assert.That(registry.TryGet(request, out _), Is.False);
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
        public void SidecarCompleted_OutArgsFromDependencies()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);

                    string sidecar = Path.Combine(dir, "out.json");
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), sidecar, out CaptureFramePngArtifact completed, out CaptureFramePngArtifactSaveReceipt receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

                    Assert.That(completed, Is.Not.Null);
                    Assert.That(receipt, Is.Not.Null);
                    Assert.That(completed.CaptureFrameId, Is.EqualTo(10));
                    Assert.That(receipt.DestinationPath, Is.EqualTo(Path.GetFullPath(sidecar)));
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
        public void PendingAndPng_SidecarPriority_PngQueueUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    // Prepare the first frame into the artifact queue.
                    RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png"), Path.Combine(dir, "a.json"), out _, out _);

                    // A second PNG is now waiting.
                    RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    int pngCountBefore = pngQueue.Count; // 1

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "b.png"), Path.Combine(dir, "b.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

                    // Sidecar was prioritized; the PNG queue is unchanged.
                    Assert.That(pngQueue.Count, Is.EqualTo(pngCountBefore));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
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
        public void SingleCall_DoesNotPublishBoth()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png"), Path.Combine(dir, "a.json"), out _, out _);

                    RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    // One call with both a pending artifact and a waiting PNG.
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "b.png"), Path.Combine(dir, "b.json"), out _, out _);

                    // Only the sidecar was published (to the sidecar path of this
                    // call); the second PNG is still unsaved.
                    Assert.That(File.Exists(Path.Combine(dir, "b.json")), Is.True);
                    Assert.That(File.Exists(Path.Combine(dir, "b.png")), Is.False);
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
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
        public void TwoCalls_PngPreparedThenSidecarCompleted()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void PngSaveFailure_BothQueuesRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

                    Assert.Throws<IOException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, pngPath, Path.Combine(dir, "out.json"), out _, out _));

                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
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
        public void PngSaveFailure_RetrySucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    File.WriteAllBytes(pngPath, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
                    Assert.Throws<IOException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, pngPath, Path.Combine(dir, "out.json"), out _, out _));

                    string alt = Path.Combine(dir, "alt.png");
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, alt, Path.Combine(dir, "alt.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(File.Exists(alt), Is.True);
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
        public void SidecarSaveFailure_BothQueuesRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _); // PngPrepared

                    string sidecar = Path.Combine(dir, "out.json");
                    File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });

                    Assert.Throws<IOException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), sidecar, out _, out _));

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(pngQueue.Count, Is.EqualTo(0));
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
        public void SidecarSaveFailure_RetrySucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), Path.Combine(dir, "out.json"), out _, out _);

                    string sidecar = Path.Combine(dir, "out.json");
                    File.WriteAllBytes(sidecar, new byte[] { 1, 2, 3 });
                    Assert.Throws<IOException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), sidecar, out _, out _));

                    string alt = Path.Combine(dir, "alt.json");
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"), alt, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void MultipleFrames_SidecarPriorityOverNextPngPreparation()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                    RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png"), Path.Combine(dir, "a.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png"), Path.Combine(dir, "a.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "b.png"), Path.Combine(dir, "b.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "b.png"), Path.Combine(dir, "b.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

                    Assert.That(pngQueue.Count, Is.EqualTo(0));
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
        public void DoesNotDisposeOrClearDependencies()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                    RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    pump.TryAdvanceNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png"), Path.Combine(dir, "a.json"), out _, out _); // PngPrepared

                    // Dependencies remain usable and are not cleared.
                    Assert.That(pngQueue.IsCreated, Is.True);
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(2));
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
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactPersistencePump)), Is.False);
        }
    }
}
