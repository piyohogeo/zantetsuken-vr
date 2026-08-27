using System;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactPersistenceCoordinatorTests
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

        private static CaptureFrameRequest MakeRequest(long testRunId = 1, long captureFrameId = 42)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
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
            request = MakeRequest(1, captureFrameId);
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

        private static CaptureFramePngArtifactPersistenceCoordinator MakeCoordinator(CaptureFrameRecordRegistry registry, string directory)
        {
            CaptureFramePngArtifactQueuePreparer queuePreparer = new CaptureFramePngArtifactQueuePreparer(
                new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore())));

            CaptureFramePngArtifactQueueCompletionWriter queueCompletionWriter = new CaptureFramePngArtifactQueueCompletionWriter(
                new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore())));

            CaptureFramePngArtifactPersistencePump pump = new CaptureFramePngArtifactPersistencePump(queuePreparer, queueCompletionWriter);
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory(directory);

            return new CaptureFramePngArtifactPersistenceCoordinator(pump, factory);
        }

        private static string ExpectedPngName(long captureFrameId)
        {
            return "capture-00000000000000000001-" + captureFrameId.ToString("D20", CultureInfo.InvariantCulture) + ".png";
        }

        private static string ExpectedSidecarName(long captureFrameId)
        {
            return "capture-00000000000000000001-" + captureFrameId.ToString("D20", CultureInfo.InvariantCulture) + ".json";
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-coordinator-" + Guid.NewGuid().ToString("N"));
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
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngArtifactDestinationFactory factory = new CaptureFramePngArtifactDestinationFactory("C:\\captures");

            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactPersistenceCoordinator(null, factory));
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactPersistenceCoordinator(pump, null));
        }

        [Test]
        public void NullPngQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, "C:\\captures");
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            Assert.Throws<ArgumentNullException>(() => coordinator.TryAdvanceNext(null, artifactQueue, out _, out _));
        }

        [Test]
        public void NullArtifactQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, "C:\\captures");
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            try
            {
                Assert.Throws<ArgumentNullException>(() => coordinator.TryAdvanceNext(pngQueue, null, out _, out _));
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
            CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, "C:\\captures");
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            pngQueue.Dispose();

            Assert.Throws<ObjectDisposedException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));
        }

        [Test]
        public void BothQueuesEmpty_None_Null_Null()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, "C:\\captures");
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
            try
            {
                CaptureFramePngArtifact completed = null;
                CaptureFramePngArtifactSaveReceipt receipt = null;
                Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out completed, out receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                Assert.That(completed, Is.Null);
                Assert.That(receipt, Is.Null);
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void BothQueuesEmpty_DoesNotTouchFilesystem()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-coordinator-" + Guid.NewGuid().ToString("N"));
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                    Assert.That(Directory.Exists(dir), Is.False);
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
        public void PngQueueOnly_PngPrepared()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
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
        public void PngQueueHead_DeterministicPngPath()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

                    Assert.That(File.Exists(Path.Combine(dir, ExpectedPngName(42))), Is.True);
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
        public void PngPrepared_ArtifactQueueHasArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.TryPeek(out CaptureFramePngArtifact queued), Is.True);
                    Assert.That(queued.CaptureFrameId, Is.EqualTo(42));
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
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

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
        public void NextCall_SidecarCompleted()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void SidecarAtSamePairJsonPath()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

                    Assert.That(File.Exists(Path.Combine(dir, ExpectedPngName(42))), Is.True);
                    Assert.That(File.Exists(Path.Combine(dir, ExpectedSidecarName(42))), Is.True);
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
        public void SidecarCompleted_ArtifactQueueEmpty()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));

                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

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
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

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
        public void CompletedArtifactAndReceiptReturned()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out CaptureFramePngArtifact completed, out CaptureFramePngArtifactSaveReceipt receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

                    Assert.That(completed, Is.Not.Null);
                    Assert.That(receipt, Is.Not.Null);
                    Assert.That(completed.CaptureFrameId, Is.EqualTo(42));
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
        public void BothQueues_PendingSidecarPriority()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _); // PngPrepared id 1

                    // Both non-empty: pending sidecar (id 1) wins.
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

                    Assert.That(artifactQueue.Count, Is.EqualTo(0));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
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
        public void PendingPriority_NextPngUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _); // PngPrepared id 1
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _); // SidecarCompleted id 1

                    // The next PNG (id 2) is untouched.
                    Assert.That(File.Exists(Path.Combine(dir, ExpectedPngName(2))), Is.False);
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
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
        public void MultipleFrames_PngPreparedSidecarCompletedRepeat()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

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
        public void Basename_HasCorrectIds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _);

                    Assert.That(File.Exists(Path.Combine(dir, "capture-00000000000000000001-00000000000000000042.png")), Is.True);
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
        public void PngSaveFailure_RetrySamePath_ExistingCollisionContract()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, ExpectedPngName(42));
                    File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
                    Assert.Throws<IOException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));

                    // Retry regenerates the same path and keeps the collision contract.
                    Assert.Throws<IOException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));
                    Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
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
        public void PngSaveFailure_RemoveCause_SameDeterministicPathSucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, ExpectedPngName(42));
                    File.WriteAllBytes(pngPath, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
                    Assert.Throws<IOException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));

                    File.Delete(pngPath);
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(File.Exists(pngPath), Is.True);
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
        public void SidecarSaveFailure_RemoveCause_SameDeterministicPathSucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _); // PngPrepared

                    string sidecarPath = Path.Combine(dir, ExpectedSidecarName(42));
                    File.WriteAllBytes(sidecarPath, new byte[] { 1, 2, 3 });
                    Assert.Throws<IOException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));

                    File.Delete(sidecarPath);
                    Assert.That(coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
                    Assert.That(File.Exists(sidecarPath), Is.True);
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
        public void DirectoryMissing_DirectoryNotFoundException_QueuesRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            string missing = Path.Combine(dir, "missing");
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, missing);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    Assert.Throws<DirectoryNotFoundException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));

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
        public void DoesNotOverwriteExistingPngSidecar()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 42, out CaptureFrameRequest request);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, ExpectedPngName(42));
                    File.WriteAllBytes(pngPath, new byte[] { 7, 7, 7, 7, 7, 7, 7, 7, 7 });
                    Assert.Throws<IOException>(() => coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _));
                    Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(new byte[] { 7, 7, 7, 7, 7, 7, 7, 7, 7 }));
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
                RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                CaptureFramePngArtifactPersistenceCoordinator coordinator = MakeCoordinator(registry, dir);
                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    coordinator.TryAdvanceNext(pngQueue, artifactQueue, out _, out _); // PngPrepared id 1

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
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactPersistenceCoordinator)), Is.False);
        }

        private static CaptureFramePngArtifactPersistencePump MakePump(CaptureFrameRecordRegistry registry)
        {
            CaptureFramePngArtifactQueuePreparer queuePreparer = new CaptureFramePngArtifactQueuePreparer(
                new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore())));

            CaptureFramePngArtifactQueueCompletionWriter queueCompletionWriter = new CaptureFramePngArtifactQueueCompletionWriter(
                new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(new CaptureFramePngArtifactFileStore())));

            return new CaptureFramePngArtifactPersistencePump(queuePreparer, queueCompletionWriter);
        }
    }
}
