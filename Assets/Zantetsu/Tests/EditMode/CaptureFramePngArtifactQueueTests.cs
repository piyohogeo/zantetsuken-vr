using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngArtifactQueueTests
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

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsuken-artifactqueue-" + Guid.NewGuid().ToString("N"));
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
        /// Builds an artifact through the public construction path: the PNG is
        /// saved by <see cref="CaptureFramePngFileStore"/> (producing a real
        /// receipt) and the artifact is built by <see cref="CaptureFramePngArtifactWriter"/>.
        /// No reflection is used.
        /// </summary>
        private static CaptureFramePngArtifact MakeArtifact(
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

        [Test]
        public void Capacity_ZeroAndNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngArtifactQueue(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngArtifactQueue(-1));
        }

        [Test]
        public void Constructor_InitialState()
        {
            CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(4);

            Assert.That(queue.Capacity, Is.EqualTo(4));
            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.TotalAccepted, Is.EqualTo(0));
            Assert.That(queue.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void EnqueueSuccess_Counters()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, dir, "a.png", "a.json");

                Assert.That(queue.TryEnqueue(artifact), Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void FifoOrder()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(4);
                CaptureFramePngArtifact a = MakeArtifact(manifest, 1, dir, "a.png", "a.json");
                CaptureFramePngArtifact b = MakeArtifact(manifest, 2, dir, "b.png", "b.json");
                CaptureFramePngArtifact c = MakeArtifact(manifest, 3, dir, "c.png", "c.json");

                queue.TryEnqueue(a);
                queue.TryEnqueue(b);
                queue.TryEnqueue(c);

                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact d1), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact d2), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact d3), Is.True);

                Assert.That(d1, Is.SameAs(a));
                Assert.That(d2, Is.SameAs(b));
                Assert.That(d3, Is.SameAs(c));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Peek_DoesNotMutate()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, dir, "a.png", "a.json");
                queue.TryEnqueue(artifact);

                Assert.That(queue.TryPeek(out CaptureFramePngArtifact peeked), Is.True);
                Assert.That(peeked, Is.SameAs(artifact));
                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));

                Assert.That(queue.TryPeek(out CaptureFramePngArtifact peekedAgain), Is.True);
                Assert.That(peekedAgain, Is.SameAs(artifact));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void EmptyPeekDequeue_FalseAndNull()
        {
            CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);

            Assert.That(queue.TryPeek(out CaptureFramePngArtifact peeked), Is.False);
            Assert.That(peeked, Is.Null);

            Assert.That(queue.TryDequeue(out CaptureFramePngArtifact dequeued), Is.False);
            Assert.That(dequeued, Is.Null);
        }

        [Test]
        public void NullArtifact_RejectedNoSideEffects()
        {
            CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);

            Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null));

            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.TotalAccepted, Is.EqualTo(0));
            Assert.That(queue.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void DuplicateSameInstance_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, dir, "a.png", "a.json");

                Assert.That(queue.TryEnqueue(artifact), Is.True);
                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(artifact));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DuplicateDifferentInstanceSameId_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact first = MakeArtifact(manifest, 10, dir, "a.png", "a.json");
                CaptureFramePngArtifact second = MakeArtifact(manifest, 10, dir, "a2.png", "a2.json");

                Assert.That(first, Is.Not.SameAs(second));
                Assert.That(queue.TryEnqueue(first), Is.True);
                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(second));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DifferentIds_Accepted()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                Assert.That(queue.TryEnqueue(MakeArtifact(manifest, 10, dir, "a.png", "a.json")), Is.True);
                Assert.That(queue.TryEnqueue(MakeArtifact(manifest, 11, dir, "b.png", "b.json")), Is.True);
                Assert.That(queue.Count, Is.EqualTo(2));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Full_KeepsElements_IncrementsRejectedOnly()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact a = MakeArtifact(manifest, 1, dir, "a.png", "a.json");
                CaptureFramePngArtifact b = MakeArtifact(manifest, 2, dir, "b.png", "b.json");
                CaptureFramePngArtifact c = MakeArtifact(manifest, 3, dir, "c.png", "c.json");

                queue.TryEnqueue(a);
                queue.TryEnqueue(b);

                Assert.That(queue.TryEnqueue(c), Is.False);

                Assert.That(queue.Count, Is.EqualTo(2));
                Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                Assert.That(queue.TotalRejected, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact d1), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact d2), Is.True);
                Assert.That(d1, Is.SameAs(a));
                Assert.That(d2, Is.SameAs(b));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Dequeue_FreesSlotForReenqueue()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(1);
                CaptureFramePngArtifact a = MakeArtifact(manifest, 1, dir, "a.png", "a.json");

                Assert.That(queue.TryEnqueue(a), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact d), Is.True);
                Assert.That(d, Is.SameAs(a));

                CaptureFramePngArtifact b = MakeArtifact(manifest, 2, dir, "b.png", "b.json");
                Assert.That(queue.TryEnqueue(b), Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void DequeuedId_CanBeReenqueued()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact first = MakeArtifact(manifest, 10, dir, "a.png", "a.json");

                queue.TryEnqueue(first);
                Assert.That(queue.TryDequeue(out _), Is.True);

                CaptureFramePngArtifact second = MakeArtifact(manifest, 10, dir, "a2.png", "a2.json");
                Assert.That(queue.TryEnqueue(second), Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void RingBufferWraparound_FifoOrder()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(3);
                CaptureFramePngArtifact a = MakeArtifact(manifest, 1, dir, "a.png", "a.json");
                CaptureFramePngArtifact b = MakeArtifact(manifest, 2, dir, "b.png", "b.json");
                CaptureFramePngArtifact c = MakeArtifact(manifest, 3, dir, "c.png", "c.json");
                CaptureFramePngArtifact d = MakeArtifact(manifest, 4, dir, "d.png", "d.json");
                CaptureFramePngArtifact e = MakeArtifact(manifest, 5, dir, "e.png", "e.json");

                queue.TryEnqueue(a);
                queue.TryEnqueue(b);
                queue.TryEnqueue(c);

                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact da), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact db), Is.True);
                Assert.That(da, Is.SameAs(a));
                Assert.That(db, Is.SameAs(b));

                // The next two enqueues wrap past the physical end of the array.
                queue.TryEnqueue(d);
                queue.TryEnqueue(e);

                Assert.That(queue.Count, Is.EqualTo(3));

                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact dc), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact dd), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFramePngArtifact de), Is.True);

                Assert.That(dc, Is.SameAs(c));
                Assert.That(dd, Is.SameAs(d));
                Assert.That(de, Is.SameAs(e));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void RingBufferWraparound_DuplicateDetection()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(3);
                queue.TryEnqueue(MakeArtifact(manifest, 1, dir, "a.png", "a.json"));
                queue.TryEnqueue(MakeArtifact(manifest, 2, dir, "b.png", "b.json"));
                queue.TryEnqueue(MakeArtifact(manifest, 3, dir, "c.png", "c.json"));

                queue.TryDequeue(out _); // head advances; ID 1 removed
                queue.TryDequeue(out _); // ID 2 removed

                // Held IDs are now {3}; the next two enqueues wrap the tail.
                queue.TryEnqueue(MakeArtifact(manifest, 4, dir, "d.png", "d.json"));
                queue.TryEnqueue(MakeArtifact(manifest, 5, dir, "e.png", "e.json"));

                // Duplicate of a held ID (3) across the wrapped region is rejected.
                CaptureFramePngArtifact dup3 = MakeArtifact(manifest, 3, dir, "dup3.png", "dup3.json");
                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(dup3));

                Assert.That(queue.Count, Is.EqualTo(3));
                Assert.That(queue.TotalAccepted, Is.EqualTo(5));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Clear_ReusesCapacity()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(1);
                queue.TryEnqueue(MakeArtifact(manifest, 1, dir, "a.png", "a.json"));

                queue.Clear();
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TryPeek(out _), Is.False);

                Assert.That(queue.TryEnqueue(MakeArtifact(manifest, 2, dir, "b.png", "b.json")), Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Clear_KeepsCounters()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                queue.TryEnqueue(MakeArtifact(manifest, 1, dir, "a.png", "a.json"));
                queue.TryEnqueue(MakeArtifact(manifest, 2, dir, "b.png", "b.json"));
                queue.TryEnqueue(MakeArtifact(manifest, 3, dir, "c.png", "c.json")); // rejected

                queue.Clear();

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                Assert.That(queue.TotalRejected, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void Clear_AllowsReenqueueOldId()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                queue.TryEnqueue(MakeArtifact(manifest, 10, dir, "a.png", "a.json"));

                queue.Clear();

                Assert.That(queue.TryEnqueue(MakeArtifact(manifest, 10, dir, "a2.png", "a2.json")), Is.True);
                Assert.That(queue.Count, Is.EqualTo(1));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        [Test]
        public void NotIDisposable()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureFramePngArtifactQueue)), Is.False);
        }

        [Test]
        public void ArtifactAndReceipt_UnchangedThroughOperations()
        {
            TraceRunManifest manifest = MakeManifest();
            string dir = CreateTempDir();
            try
            {
                CaptureFramePngArtifactQueue queue = new CaptureFramePngArtifactQueue(2);
                CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, dir, "a.png", "a.json");

                CaptureFramePngSaveReceipt receipt = artifact.PngReceipt;
                long captureFrameId = artifact.CaptureFrameId;
                string pngPath = receipt.DestinationPath;
                int byteCount = receipt.ByteCount;
                string sha256 = receipt.ContentSha256;
                string frameRecordHash = artifact.FrameRecord.RunManifestContentSha256;

                queue.TryEnqueue(artifact);
                queue.TryPeek(out CaptureFramePngArtifact peeked);
                queue.TryDequeue(out CaptureFramePngArtifact dequeued);
                queue.Clear();

                Assert.That(dequeued, Is.SameAs(artifact));
                Assert.That(peeked, Is.SameAs(artifact));
                Assert.That(dequeued.PngReceipt, Is.SameAs(receipt));
                Assert.That(dequeued.CaptureFrameId, Is.EqualTo(captureFrameId));
                Assert.That(receipt.DestinationPath, Is.EqualTo(pngPath));
                Assert.That(receipt.ByteCount, Is.EqualTo(byteCount));
                Assert.That(receipt.ContentSha256, Is.EqualTo(sha256));
                Assert.That(dequeued.FrameRecord.RunManifestContentSha256, Is.EqualTo(frameRecordHash));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }
    }
}
