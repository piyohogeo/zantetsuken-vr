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
    public class CaptureFramePngArtifactPreparerTests
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
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-preparer-" + Guid.NewGuid().ToString("N"));
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
            CaptureFramePngQueueFileWriter queueWriter = new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore());

            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactPreparer(null, queueWriter));
            Assert.Throws<ArgumentNullException>(() => new CaptureFramePngArtifactPreparer(registry, null));
        }

        [Test]
        public void NullAndDisposedQueue_RejectedBeforeSave()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

            string dir = CreateTempDir();
            try
            {
                Assert.Throws<ArgumentNullException>(() => preparer.TrySaveNext(null, Path.Combine(dir, "out.png"), out _));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                queue.Dispose();
                Assert.Throws<ObjectDisposedException>(() => preparer.TrySaveNext(queue, Path.Combine(dir, "out.png"), out _));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void EmptyQueue_None_NullArtifact_PathNotValidated()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            try
            {
                CaptureFramePngArtifact artifact = null;
                Assert.That(preparer.TrySaveNext(queue, null, out artifact), Is.EqualTo(CaptureFramePngSaveStatus.None));
                Assert.That(artifact, Is.Null);

                Assert.That(preparer.TrySaveNext(queue, "relative-not-fully-qualified.png", out artifact), Is.EqualTo(CaptureFramePngSaveStatus.None));
                Assert.That(artifact, Is.Null);

                Assert.That(registry.Count, Is.EqualTo(0));
            }
            finally
            {
                queue.Dispose();
            }
        }

        [Test]
        public void MissingRecord_FileNotCreated_QueueRegistryUnchanged()
        {
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, MakeRequest(10), MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    CaptureFramePngArtifact artifact = null;
                    Assert.Throws<InvalidOperationException>(() => preparer.TrySaveNext(queue, pngPath, out artifact));

                    Assert.That(artifact, Is.Null);
                    Assert.That(File.Exists(pngPath), Is.False);
                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(0));
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
        public void MismatchedRequest_FileNotCreated_QueueRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out _);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, MakeRequest(10, unityFrameId: 99), MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    CaptureFramePngArtifact artifact = null;
                    Assert.Throws<InvalidOperationException>(() => preparer.TrySaveNext(queue, pngPath, out artifact));

                    Assert.That(artifact, Is.Null);
                    Assert.That(File.Exists(pngPath), Is.False);
                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
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
        public void Success_Saved_PngExists_QueueEmpty()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    Assert.That(preparer.TrySaveNext(queue, pngPath, out CaptureFramePngArtifact artifact), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    Assert.That(artifact, Is.Not.Null);
                    Assert.That(File.Exists(pngPath), Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
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
        public void Success_RegistryKeepsSameRecordReference()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));
                    Assert.That(preparer.TrySaveNext(queue, Path.Combine(dir, "out.png"), out _), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(request, out CaptureFrameRecord kept), Is.True);
                    Assert.That(kept, Is.SameAs(record));
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
        public void Artifact_HoldsSameRecordAndQueueWriterReceipt()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    byte[] pngBytes = MakePngBytes(32);
                    EnqueuePng(queue, request, pngBytes);

                    string pngPath = Path.Combine(dir, "out.png");
                    Assert.That(preparer.TrySaveNext(queue, pngPath, out CaptureFramePngArtifact artifact), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    Assert.That(artifact.FrameRecord, Is.SameAs(record));
                    Assert.That(artifact.PngReceipt, Is.Not.Null);
                    Assert.That(artifact.PngReceipt.DestinationPath, Is.EqualTo(Path.GetFullPath(pngPath)));
                    Assert.That(artifact.PngReceipt.ByteCount, Is.EqualTo(pngBytes.Length));
                    Assert.That(artifact.PngReceipt.ContentSha256, Is.EqualTo(Sha256Hex(pngBytes)));
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
        public void Artifact_RequestMatchesSavedRequest()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));
                    Assert.That(preparer.TrySaveNext(queue, Path.Combine(dir, "out.png"), out CaptureFramePngArtifact artifact), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    CaptureFrameRequest artifactRequest = artifact.FrameRecord.Request;
                    Assert.That(artifactRequest.TraceContext.CaptureFrameId, Is.EqualTo(request.TraceContext.CaptureFrameId));
                    Assert.That(artifactRequest.TraceContext.UnityFrameId, Is.EqualTo(request.TraceContext.UnityFrameId));
                    Assert.That(artifactRequest.TraceContext.OpenXRFrameId, Is.EqualTo(request.TraceContext.OpenXRFrameId));
                    Assert.That(artifactRequest.TraceContext.TestRunId, Is.EqualTo(request.TraceContext.TestRunId));
                    Assert.That(artifactRequest.Source, Is.EqualTo(request.Source));
                    Assert.That(artifactRequest.Eye, Is.EqualTo(request.Eye));
                    Assert.That(artifactRequest.ArrayIndex, Is.EqualTo(request.ArrayIndex));
                    Assert.That(artifactRequest.ImageRect.X, Is.EqualTo(request.ImageRect.X));
                    Assert.That(artifactRequest.ImageRect.Y, Is.EqualTo(request.ImageRect.Y));
                    Assert.That(artifactRequest.ImageRect.Width, Is.EqualTo(request.ImageRect.Width));
                    Assert.That(artifactRequest.ImageRect.Height, Is.EqualTo(request.ImageRect.Height));
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
        public void DestinationExisting_Failure_QueueRegistryUnchanged_ArtifactNull()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

                    CaptureFramePngArtifact artifact = null;
                    Assert.Throws<IOException>(() => preparer.TrySaveNext(queue, pngPath, out artifact));

                    Assert.That(artifact, Is.Null);
                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
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
        public void MissingParentDirectory_Failure_QueueRegistryUnchanged()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));

                    string missing = Path.Combine(dir, "missing");
                    CaptureFramePngArtifact artifact = null;
                    Assert.Throws<DirectoryNotFoundException>(() => preparer.TrySaveNext(queue, Path.Combine(missing, "out.png"), out artifact));

                    Assert.That(artifact, Is.Null);
                    Assert.That(Directory.Exists(missing), Is.False);
                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
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
        public void SaveFailure_RetrySucceeds()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 10, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, request, MakePngBytes(32));

                    string pngPath = Path.Combine(dir, "out.png");
                    File.WriteAllBytes(pngPath, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9 });
                    Assert.Throws<IOException>(() => preparer.TrySaveNext(queue, pngPath, out _));
                    Assert.That(queue.Count, Is.EqualTo(1));

                    string alt = Path.Combine(dir, "alt.png");
                    Assert.That(preparer.TrySaveNext(queue, alt, out CaptureFramePngArtifact artifact), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    Assert.That(artifact, Is.Not.Null);
                    Assert.That(File.Exists(alt), Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(1));
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
        public void Fifo_MultiplePngsPreparedInOrder()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord first = MakeRecord(manifest, 1, out CaptureFrameRequest firstRequest);
                CaptureFrameRecord second = MakeRecord(manifest, 2, out CaptureFrameRequest secondRequest);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(first);
                registry.TryRegister(second);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, firstRequest, MakePngBytes(32));
                    EnqueuePng(queue, secondRequest, MakePngBytes(40));

                    Assert.That(preparer.TrySaveNext(queue, Path.Combine(dir, "a.png"), out CaptureFramePngArtifact a), Is.EqualTo(CaptureFramePngSaveStatus.Saved));
                    Assert.That(preparer.TrySaveNext(queue, Path.Combine(dir, "b.png"), out CaptureFramePngArtifact b), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    Assert.That(a.CaptureFrameId, Is.EqualTo(1));
                    Assert.That(b.CaptureFrameId, Is.EqualTo(2));
                    Assert.That(queue.Count, Is.EqualTo(0));
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
        public void DoesNotUseNonHeadRecord()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord headRecord = MakeRecord(manifest, 2, out _);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(headRecord);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, MakeRequest(1), MakePngBytes(32)); // head has no record
                    EnqueuePng(queue, MakeRequest(2), MakePngBytes(32)); // this record exists

                    string pngPath = Path.Combine(dir, "out.png");
                    CaptureFramePngArtifact artifact = null;
                    Assert.Throws<InvalidOperationException>(() => preparer.TrySaveNext(queue, pngPath, out artifact));

                    Assert.That(artifact, Is.Null);
                    Assert.That(File.Exists(pngPath), Is.False);
                    Assert.That(queue.Count, Is.EqualTo(2));
                    Assert.That(registry.Count, Is.EqualTo(1));
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
        public void DoesNotModifyQueueCounters()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord first = MakeRecord(manifest, 1, out CaptureFrameRequest firstRequest);
                CaptureFrameRecord second = MakeRecord(manifest, 2, out CaptureFrameRequest secondRequest);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(first);
                registry.TryRegister(second);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, firstRequest, MakePngBytes(32));
                    EnqueuePng(queue, secondRequest, MakePngBytes(32));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                    Assert.That(queue.TotalRejected, Is.EqualTo(0));

                    preparer.TrySaveNext(queue, Path.Combine(dir, "a.png"), out _);
                    Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                    Assert.That(queue.TotalRejected, Is.EqualTo(0));

                    preparer.TrySaveNext(queue, Path.Combine(dir, "b.png"), out _);
                    Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                    Assert.That(queue.TotalRejected, Is.EqualTo(0));
                    Assert.That(queue.Count, Is.EqualTo(0));
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
        public void DoesNotDisposeOrClearDependencies()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactPreparer)), Is.False);

            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord first = MakeRecord(manifest, 1, out CaptureFrameRequest firstRequest);
                CaptureFrameRecord second = MakeRecord(manifest, 2, out CaptureFrameRequest secondRequest);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(first);
                registry.TryRegister(second);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    EnqueuePng(queue, firstRequest, MakePngBytes(32));
                    EnqueuePng(queue, secondRequest, MakePngBytes(32));

                    preparer.TrySaveNext(queue, Path.Combine(dir, "a.png"), out _);
                    preparer.TrySaveNext(queue, Path.Combine(dir, "b.png"), out _);

                    // Registry records are never removed or cleared by the preparer.
                    Assert.That(registry.Count, Is.EqualTo(2));
                    Assert.That(registry.TryGet(firstRequest, out _), Is.True);
                    Assert.That(registry.TryGet(secondRequest, out _), Is.True);
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
        public void Integration_WithCompletionWriter()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFrameRecord record = MakeRecord(manifest, 77, out CaptureFrameRequest request);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
                registry.TryRegister(record);
                CaptureFramePngArtifactPreparer preparer = new CaptureFramePngArtifactPreparer(registry, new CaptureFramePngQueueFileWriter(new CaptureFramePngFileStore()));
                CaptureFramePngArtifactFileStore artifactFileStore = new CaptureFramePngArtifactFileStore();
                CaptureFramePngArtifactCompletionWriter completionWriter =
                    new CaptureFramePngArtifactCompletionWriter(registry, new CaptureFramePngArtifactWriter(artifactFileStore));

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    byte[] pngBytes = MakePngBytes(32);
                    EnqueuePng(queue, request, pngBytes);

                    string pngPath = Path.Combine(dir, "frame.png");
                    Assert.That(preparer.TrySaveNext(queue, pngPath, out CaptureFramePngArtifact artifact), Is.EqualTo(CaptureFramePngSaveStatus.Saved));

                    // Record is still held after prepare.
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(File.Exists(pngPath), Is.True);

                    // Sidecar failure first: an existing destination is not overwritten.
                    string sidecarPath = Path.Combine(dir, "frame.json");
                    File.WriteAllBytes(sidecarPath, new byte[] { 1, 2, 3 });
                    Assert.Throws<IOException>(() => completionWriter.SaveAtomic(sidecarPath, artifact.FrameRecord.Request, artifact.PngReceipt, out _));
                    Assert.That(registry.Count, Is.EqualTo(1));

                    // Retry with the prepared artifact's request and receipt.
                    string altSidecar = Path.Combine(dir, "frame-alt.json");
                    CaptureFramePngArtifactSaveReceipt sidecarReceipt =
                        completionWriter.SaveAtomic(altSidecar, artifact.FrameRecord.Request, artifact.PngReceipt, out CaptureFramePngArtifact completed);

                    Assert.That(sidecarReceipt, Is.Not.Null);
                    Assert.That(completed, Is.Not.Null);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(0));
                    Assert.That(File.Exists(pngPath), Is.True);
                    Assert.That(File.Exists(altSidecar), Is.True);

                    CaptureFramePngArtifact loaded = artifactFileStore.Load(altSidecar, manifest);
                    Assert.That(CaptureFramePngArtifactCodec.SerializeCanonical(loaded), Is.EqualTo(CaptureFramePngArtifactCodec.SerializeCanonical(completed)));
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
    }
}
