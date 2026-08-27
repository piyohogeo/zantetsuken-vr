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
    public class CaptureFramePngArtifactQueuePreparerTests
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

        private static CaptureFramePngArtifactQueuePreparer MakeQueuePreparer(CaptureFrameRecordRegistry registry)
        {
            CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));
            return new CaptureFramePngArtifactQueuePreparer(preparer);
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-queuepreparer-" + Guid.NewGuid().ToString("N"));
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
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactQueuePreparer(null));
        }

        [Test]
        public void NullPngQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            Assert.Throws<ArgumentNullException>(() => queuePreparer.TryPrepareNext(null, artifactQueue, "C:\\x\\out.png"));
        }

        [Test]
        public void NullArtifactQueue_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);
            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            try
            {
                Assert.Throws<ArgumentNullException>(() => queuePreparer.TryPrepareNext(pngQueue, null, "C:\\x\\out.png"));
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
            CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            pngQueue.Dispose();

            Assert.Throws<ObjectDisposedException>(() => queuePreparer.TryPrepareNext(pngQueue, artifactQueue, "C:\\x\\out.png"));
        }

        [Test]
        public void EmptyPngQueue_None()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            try
            {
                Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, "C:\\x\\out.png"), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.None));
                Assert.That(artifactQueue.Count, Is.EqualTo(0));
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void EmptyPngQueue_InvalidDestinationNotValidated()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);
            CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);

            CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
            try
            {
                Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, null), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.None));
                Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, "relative.png"), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.None));
            }
            finally
            {
                pngQueue.Dispose();
            }
        }

        [Test]
        public void ArtifactQueueFull_Backpressured()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(1);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    artifactQueue.TryEnqueue(MakeArtifactForQueue(manifest, 99, dir, "other.png", "other.json")); // fill the artifact queue

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Backpressured));
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
        public void Backpressured_InvalidDestinationNotValidated()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(1);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    artifactQueue.TryEnqueue(MakeArtifactForQueue(manifest, 99, dir, "other.png", "other.json"));

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, null), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Backpressured));
                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, "relative.png"), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Backpressured));
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
        public void Backpressured_BothQueuesCountersRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(1);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    artifactQueue.TryEnqueue(MakeArtifactForQueue(manifest, 99, dir, "other.png", "other.json"));

                    int pngCount = pngQueue.Count;
                    long pngAccepted = pngQueue.TotalAccepted;
                    long pngRejected = pngQueue.TotalRejected;
                    int artifactCount = artifactQueue.Count;
                    long artifactAccepted = artifactQueue.TotalAccepted;
                    long artifactRejected = artifactQueue.TotalRejected;
                    int registryCount = registry.Count;

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Backpressured));

                    Assert.That(pngQueue.Count, Is.EqualTo(pngCount));
                    Assert.That(pngQueue.TotalAccepted, Is.EqualTo(pngAccepted));
                    Assert.That(pngQueue.TotalRejected, Is.EqualTo(pngRejected));
                    Assert.That(artifactQueue.Count, Is.EqualTo(artifactCount));
                    Assert.That(artifactQueue.TotalAccepted, Is.EqualTo(artifactAccepted));
                    Assert.That(artifactQueue.TotalRejected, Is.EqualTo(artifactRejected));
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
        public void Backpressured_NoFileCreated()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(1);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    artifactQueue.TryEnqueue(MakeArtifactForQueue(manifest, 99, dir, "other.png", "other.json"));

                    string pngPath = Path.Combine(dir, "out.png");
                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, pngPath), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Backpressured));
                    Assert.That(File.Exists(pngPath), Is.False);
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
        public void Success_Queued()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));
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
        public void Success_PngQueueDecrementedByOne()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    Assert.That(pngQueue.Count, Is.EqualTo(1));

                    queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"));

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
        public void Success_ArtifactQueueHasMatchingArtifact()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    byte[] pngBytes = MakePngBytes(32);
                    EnqueuePng(pngQueue, request, pngBytes);

                    string pngPath = Path.Combine(dir, "out.png");
                    queuePreparer.TryPrepareNext(pngQueue, artifactQueue, pngPath);

                    Assert.That(artifactQueue.TryDequeue(out CaptureFramePngArtifact artifact), Is.True);
                    Assert.That(artifact.CaptureFrameId, Is.EqualTo(10));
                    Assert.That(artifact.FrameRecord.Request.TraceContext.CaptureFrameId, Is.EqualTo(request.TraceContext.CaptureFrameId));
                    Assert.That(artifact.PngReceipt.DestinationPath, Is.EqualTo(Path.GetFullPath(pngPath)));
                    Assert.That(artifact.PngReceipt.ByteCount, Is.EqualTo(pngBytes.Length));
                    Assert.That(artifact.PngReceipt.ContentSha256, Is.EqualTo(Sha256Hex(pngBytes)));
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
        public void Success_RegistryRecordKept()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"));

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
        public void Success_PngExistsSidecarDoesNot()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "frame.png");
                    queuePreparer.TryPrepareNext(pngQueue, artifactQueue, pngPath);

                    Assert.That(File.Exists(pngPath), Is.True);
                    Assert.That(File.Exists(Path.Combine(dir, "frame.json")), Is.False);
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
        public void DestinationExisting_Failure_BothQueuesRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    string pngPath = Path.Combine(dir, "out.png");
                    File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

                    Assert.Throws<IOException>(() => queuePreparer.TryPrepareNext(pngQueue, artifactQueue, pngPath));

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
        public void MissingParentDir_Failure_BothQueuesRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    string missing = Path.Combine(dir, "missing");

                    Assert.Throws<DirectoryNotFoundException>(() => queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(missing, "out.png")));

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
        public void SaveFailure_RetrySucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    File.WriteAllBytes(pngPath, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
                    Assert.Throws<IOException>(() => queuePreparer.TryPrepareNext(pngQueue, artifactQueue, pngPath));

                    string alt = Path.Combine(dir, "alt.png");
                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, alt), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));

                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
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
        public void MultipleSuccess_ArtifactQueueFifoOrderByCaptureFrameId()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                RegisterRecord(registry, manifest, 3, out CaptureFrameRequest request3);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(4);
                try
                {
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));
                    EnqueuePng(pngQueue, request3, MakePngBytes(32));

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));
                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "b.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));
                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "c.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));

                    Assert.That(artifactQueue.Count, Is.EqualTo(3));
                    Assert.That(artifactQueue.TryDequeue(out CaptureFramePngArtifact a), Is.True);
                    Assert.That(artifactQueue.TryDequeue(out CaptureFramePngArtifact b), Is.True);
                    Assert.That(artifactQueue.TryDequeue(out CaptureFramePngArtifact c), Is.True);
                    Assert.That(a.CaptureFrameId, Is.EqualTo(1));
                    Assert.That(b.CaptureFrameId, Is.EqualTo(2));
                    Assert.That(c.CaptureFrameId, Is.EqualTo(3));
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
        public void OneSlotFree_SuccessThenBackpressured()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 1, out CaptureFrameRequest request1);
                RegisterRecord(registry, manifest, 2, out CaptureFrameRequest request2);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(4);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(1);
                try
                {
                    EnqueuePng(pngQueue, request1, MakePngBytes(32));
                    EnqueuePng(pngQueue, request2, MakePngBytes(32));

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "a.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Queued));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));

                    Assert.That(queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "b.png")), Is.EqualTo(CaptureFramePngArtifactPreparationStatus.Backpressured));

                    // The second PNG remains in the PNG queue, unsaved.
                    Assert.That(pngQueue.Count, Is.EqualTo(1));
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
        public void DoesNotDisposeOrClearDependencies()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                RegisterRecord(registry, manifest, 10, out CaptureFrameRequest request);
                CaptureFramePngArtifactQueuePreparer queuePreparer = MakeQueuePreparer(registry);

                CaptureFramePngQueue pngQueue = new CaptureFramePngQueue(2);
                CaptureFramePngArtifactQueue artifactQueue = new CaptureFramePngArtifactQueue(2);
                try
                {
                    EnqueuePng(pngQueue, request, MakePngBytes(32));
                    queuePreparer.TryPrepareNext(pngQueue, artifactQueue, Path.Combine(dir, "out.png"));

                    // The registry record is retained and the artifact queue holds the prepared artifact.
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(artifactQueue.Count, Is.EqualTo(1));
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
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactQueuePreparer)), Is.False);
        }

        /// <summary>
        /// Builds an artifact for filling an artifact queue through the public
        /// construction path (real PNG receipt via <see cref="CaptureFramePngFileStore"/>,
        /// real sidecar via <see cref="CaptureFramePngArtifactWriter"/>). No
        /// reflection is used.
        /// </summary>
        private static CaptureFramePngArtifact MakeArtifactForQueue(
            TraceRunManifest manifest,
            long captureFrameId,
            string dir,
            string pngFileName,
            string sidecarFileName)
        {
            CaptureFramePngFileStore pngFileStore = new CaptureFramePngFileStore();
            NativeArray<byte> png = new NativeArray<byte>(MakePngBytes(32), Allocator.Temp);
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
    }
}
