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
    public class CaptureFramePngArtifactPersistencePumpDestinationTests
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

        private static CaptureFramePngArtifactDestination MakeDestination(long captureFrameId, string dir, string pngName, string sidecarName)
        {
            return new CaptureFramePngArtifactDestination(captureFrameId, Path.Combine(dir, pngName), Path.Combine(dir, sidecarName));
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-pumpdest-" + Guid.NewGuid().ToString("N"));
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
        public void BothQueuesEmpty_NullDestination_None()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
            try
            {
                Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, null, out CaptureFramePngArtifact completed, out CaptureFramePngArtifactSaveReceipt receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
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
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPersistencePump pump = MakePump(registry);
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
            try
            {
                Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, null, out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.None));
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(pngQueue.Count, Is.EqualTo(0));
                Assert.That(artifactQueue.Count, Is.EqualTo(0));
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void PngHeadIdMatch_PngPrepared()
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

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(File.Exists(Path.Combine(dir, "out.png")), Is.True);
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
        public void PngHeadIdMismatch_RejectedBeforeIo()
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

                    Assert.Throws<ArgumentException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(99, dir, "out.png", "out.json"), out _, out _));

                    Assert.That(File.Exists(Path.Combine(dir, "out.png")), Is.False);
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
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
        public void PngMismatch_QueuesRegistryCountersUnchanged()
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

                    int pngCount = pngQueue.Count;
                    long pngAccepted = pngQueue.TotalAccepted;
                    long pngRejected = pngQueue.TotalRejected;
                    int artifactCount = artifactQueue.Count;
                    int registryCount = registry.Count;

                    Assert.Throws<ArgumentException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(99, dir, "out.png", "out.json"), out _, out _));

                    Assert.That(pngQueue.Count, Is.EqualTo(pngCount));
                    Assert.That(pngQueue.TotalAccepted, Is.EqualTo(pngAccepted));
                    Assert.That(pngQueue.TotalRejected, Is.EqualTo(pngRejected));
                    Assert.That(artifactQueue.Count, Is.EqualTo(artifactCount));
                    Assert.That(registry.Count, Is.EqualTo(registryCount));
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
        public void ArtifactHeadIdMatch_SidecarCompleted()
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
                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out _, out _); // PngPrepared

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out CaptureFramePngArtifact completed, out CaptureFramePngArtifactSaveReceipt receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void ArtifactHeadIdMismatch_RejectedBeforeIo()
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
                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out _, out _); // PngPrepared

                    Assert.Throws<ArgumentException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(99, dir, "out.png", "out.json"), out _, out _));

                    Assert.That(File.Exists(Path.Combine(dir, "out.json")), Is.False);
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
        public void ArtifactMismatch_ArtifactRegistryPngUnchanged()
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
                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(1, dir, "a.png", "a.json"), out _, out _); // PngPrepared

                    RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    int pngCount = pngQueue.Count;
                    int artifactCount = artifactQueue.Count;
                    int registryCount = registry.Count;

                    // Destination for the pending PNG (id 2) does not match the
                    // artifact queue head (id 1).
                    Assert.Throws<ArgumentException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(2, dir, "b.png", "b.json"), out _, out _));

                    Assert.That(pngQueue.Count, Is.EqualTo(pngCount));
                    Assert.That(artifactQueue.Count, Is.EqualTo(artifactCount));
                    Assert.That(registry.Count, Is.EqualTo(registryCount));
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
        public void BothQueues_ArtifactHeadPriority()
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

                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(1, dir, "a.png", "a.json"), out _, out _); // PngPrepared id 1

                    // Both queues non-empty: the artifact queue head (id 1) wins.
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(1, dir, "a.png", "a.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

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
        public void PendingAndNextPng_DifferentIds_NextPngDestinationRejected()
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

                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(1, dir, "a.png", "a.json"), out _, out _); // PngPrepared id 1

                    // Destination for the next PNG (id 2) is rejected while a
                    // pending artifact (id 1) is still queued.
                    Assert.Throws<ArgumentException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(2, dir, "b.png", "b.json"), out _, out _));

                    Assert.That(File.Exists(Path.Combine(dir, "b.png")), Is.False);
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
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
        public void MatchingPendingDestination_CompletesSidecar_PngQueueKept()
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

                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(1, dir, "a.png", "a.json"), out _, out _); // PngPrepared id 1

                    // Matching pending destination completes the sidecar and keeps
                    // the PNG queue untouched.
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(1, dir, "a.png", "a.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));

                    Assert.That(File.Exists(Path.Combine(dir, "a.json")), Is.True);
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
        public void PngSaveFailure_RetryContractMaintained()
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

                    File.WriteAllBytes(Path.Combine(dir, "out.png"), new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
                    Assert.Throws<IOException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out _, out _));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.Count, Is.EqualTo(0));

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "alt.png", "alt.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
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
        public void SidecarSaveFailure_RetryContractMaintained()
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
                    pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out _, out _); // PngPrepared

                    File.WriteAllBytes(Path.Combine(dir, "out.json"), new byte[] { 1, 2, 3 });
                    Assert.Throws<IOException>(() => pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out _, out _));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));

                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "alt.json"), out _, out _), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
        public void NewOverload_OutArgsOnlyOnSuccess()
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

                    // PNG stage leaves out args null.
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out completed, out receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.PngPrepared));
                    Assert.That(completed, Is.Null);
                    Assert.That(receipt, Is.Null);

                    // Sidecar stage sets out args.
                    Assert.That(pump.TryAdvanceNext(pngQueue, artifactQueue, MakeDestination(10, dir, "out.png", "out.json"), out completed, out receipt), Is.EqualTo(CaptureFramePngArtifactPersistenceStatus.SidecarCompleted));
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
    }
}
