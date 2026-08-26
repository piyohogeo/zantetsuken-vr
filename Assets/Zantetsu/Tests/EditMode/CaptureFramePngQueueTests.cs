using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngQueueTests
    {
        private static NativeArray<byte> MakePng(int length)
        {
            NativeArray<byte> png = new NativeArray<byte>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < length; i++)
            {
                png[i] = (byte)(i & 0xFF);
            }

            return png;
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, captureFrameId, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        [Test]
        public void Constructor_ZeroOrNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngQueue(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFramePngQueue(-1));
        }

        [Test]
        public void InitialState()
        {
            using (CaptureFramePngQueue queue = new CaptureFramePngQueue(4))
            {
                Assert.That(queue.IsCreated, Is.True);
                Assert.That(queue.Capacity, Is.EqualTo(4));
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
        }

        [Test]
        public void EnqueueDequeue_RoundTrip()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(4);
            NativeArray<byte> png = default;
            NativeArray<byte> dequeued = default;
            try
            {
                png = MakePng(16);

                Assert.That(queue.TryEnqueue(MakeRequest(7), png), Is.True);
                png = default; // ownership moved to queue

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out dequeued), Is.True);
                Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(7));
                Assert.That(dequeued.IsCreated, Is.True);
                Assert.That(dequeued.Length, Is.EqualTo(16));
            }
            finally
            {
                if (dequeued.IsCreated) { dequeued.Dispose(); }
                if (png.IsCreated) { png.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void FifoOrder_And_RequestPngCorrespondence()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png1 = default;
            NativeArray<byte> png2 = default;
            NativeArray<byte> dq1 = default;
            NativeArray<byte> dq2 = default;
            try
            {
                png1 = MakePng(16);
                png2 = MakePng(16);

                Assert.That(queue.TryEnqueue(MakeRequest(100), png1), Is.True);
                png1 = default;
                Assert.That(queue.TryEnqueue(MakeRequest(200), png2), Is.True);
                png2 = default;

                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr1, out dq1), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr2, out dq2), Is.True);

                Assert.That(fr1.TraceContext.CaptureFrameId, Is.EqualTo(100));
                Assert.That(fr2.TraceContext.CaptureFrameId, Is.EqualTo(200));
                Assert.That(dq1.Length, Is.EqualTo(16));
                Assert.That(dq2.Length, Is.EqualTo(16));
            }
            finally
            {
                if (dq1.IsCreated) { dq1.Dispose(); }
                if (dq2.IsCreated) { dq2.Dispose(); }
                if (png1.IsCreated) { png1.Dispose(); }
                if (png2.IsCreated) { png2.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void WrapAround_FifoPreserved()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png1 = default;
            NativeArray<byte> png2 = default;
            NativeArray<byte> png3 = default;
            NativeArray<byte> dq1 = default;
            NativeArray<byte> dq2 = default;
            NativeArray<byte> dq3 = default;
            try
            {
                png1 = MakePng(16);
                png2 = MakePng(16);
                png3 = MakePng(16);

                Assert.That(queue.TryEnqueue(MakeRequest(10), png1), Is.True);
                png1 = default;
                Assert.That(queue.TryEnqueue(MakeRequest(20), png2), Is.True);
                png2 = default;

                Assert.That(queue.TryDequeue(out _, out dq1), Is.True);

                Assert.That(queue.TryEnqueue(MakeRequest(30), png3), Is.True);
                png3 = default;

                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr2, out dq2), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr3, out dq3), Is.True);

                Assert.That(fr2.TraceContext.CaptureFrameId, Is.EqualTo(20));
                Assert.That(fr3.TraceContext.CaptureFrameId, Is.EqualTo(30));
            }
            finally
            {
                if (dq1.IsCreated) { dq1.Dispose(); }
                if (dq2.IsCreated) { dq2.Dispose(); }
                if (dq3.IsCreated) { dq3.Dispose(); }
                if (png1.IsCreated) { png1.Dispose(); }
                if (png2.IsCreated) { png2.Dispose(); }
                if (png3.IsCreated) { png3.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void EmptyDequeue_False_Defaults()
        {
            using (CaptureFramePngQueue queue = new CaptureFramePngQueue(2))
            {
                Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out NativeArray<byte> png), Is.False);
                Assert.That(frameRequest.IsValid, Is.False);
                Assert.That(png.IsCreated, Is.False);
                Assert.That(queue.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void Full_Rejects_IncrementsRejected_PreservesExisting()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png1 = default;
            NativeArray<byte> png2 = default;
            NativeArray<byte> png3 = default;
            NativeArray<byte> dq1 = default;
            NativeArray<byte> dq2 = default;
            try
            {
                png1 = MakePng(16);
                png2 = MakePng(16);
                png3 = MakePng(16);

                Assert.That(queue.TryEnqueue(MakeRequest(1), png1), Is.True);
                png1 = default;
                Assert.That(queue.TryEnqueue(MakeRequest(2), png2), Is.True);
                png2 = default;

                Assert.That(queue.TryEnqueue(MakeRequest(3), png3), Is.False);

                Assert.That(queue.Count, Is.EqualTo(2));
                Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                Assert.That(queue.TotalRejected, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr1, out dq1), Is.True);
                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr2, out dq2), Is.True);
                Assert.That(fr1.TraceContext.CaptureFrameId, Is.EqualTo(1));
                Assert.That(fr2.TraceContext.CaptureFrameId, Is.EqualTo(2));

                // Rejected PNG stays caller-owned.
                Assert.That(png3.IsCreated, Is.True);
            }
            finally
            {
                if (dq1.IsCreated) { dq1.Dispose(); }
                if (dq2.IsCreated) { dq2.Dispose(); }
                if (png3.IsCreated) { png3.Dispose(); }
                if (png1.IsCreated) { png1.Dispose(); }
                if (png2.IsCreated) { png2.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void InvalidRequest_Rejected_CountersUnchanged()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            try
            {
                png = MakePng(16);

                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(default, png));

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void UncreatedOrEmptyPng_Rejected_CountersUnchanged()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> empty = default;
            try
            {
                NativeArray<byte> uncreated = default;
                empty = new NativeArray<byte>(0, Allocator.Persistent);

                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(MakeRequest(1), uncreated));
                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(MakeRequest(1), empty));

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));
            }
            finally
            {
                if (empty.IsCreated) { empty.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void DuplicateAllocation_Rejected_NoDoubleDisposeOnClear()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(4);
            NativeArray<byte> png = default;
            bool ownershipTransferred = false;
            try
            {
                png = MakePng(16);

                ownershipTransferred = queue.TryEnqueue(MakeRequest(1), png);
                Assert.That(ownershipTransferred, Is.True);

                // png is now a non-owning alias of the queue-owned allocation.
                Assert.Throws<ArgumentException>(() => queue.TryEnqueue(MakeRequest(2), png));

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(queue.TotalAccepted, Is.EqualTo(1));

                Assert.DoesNotThrow(() => queue.Clear());
                Assert.That(queue.Count, Is.EqualTo(0));
            }
            finally
            {
                if (!ownershipTransferred && png.IsCreated)
                {
                    png.Dispose();
                }

                queue.Dispose();
            }
        }

        [Test]
        public void DequeuedPng_RemainsValidAfterQueueClear()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            NativeArray<byte> dequeued = default;
            try
            {
                png = MakePng(16);

                Assert.That(queue.TryEnqueue(MakeRequest(1), png), Is.True);
                png = default;

                Assert.That(queue.TryDequeue(out _, out dequeued), Is.True);

                queue.Clear();

                Assert.That(dequeued.IsCreated, Is.True);
                Assert.That(dequeued.Length, Is.EqualTo(16));
            }
            finally
            {
                if (dequeued.IsCreated) { dequeued.Dispose(); }
                if (png.IsCreated) { png.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void Clear_DisposesHeldPngs_ResetsAndReuses()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(4);
            NativeArray<byte> png1 = default;
            NativeArray<byte> png2 = default;
            NativeArray<byte> png3 = default;
            NativeArray<byte> dq = default;
            try
            {
                png1 = MakePng(16);
                png2 = MakePng(16);

                Assert.That(queue.TryEnqueue(MakeRequest(1), png1), Is.True);
                png1 = default;
                Assert.That(queue.TryEnqueue(MakeRequest(2), png2), Is.True);
                png2 = default;

                queue.Clear();

                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.Capacity, Is.EqualTo(4));
                Assert.That(queue.TotalAccepted, Is.EqualTo(2));
                Assert.That(queue.TotalRejected, Is.EqualTo(0));

                png3 = MakePng(16);
                Assert.That(queue.TryEnqueue(MakeRequest(3), png3), Is.True);
                png3 = default;

                Assert.That(queue.TryDequeue(out CaptureFrameRequest fr, out dq), Is.True);
                Assert.That(fr.TraceContext.CaptureFrameId, Is.EqualTo(3));
            }
            finally
            {
                if (dq.IsCreated) { dq.Dispose(); }
                if (png1.IsCreated) { png1.Dispose(); }
                if (png2.IsCreated) { png2.Dispose(); }
                if (png3.IsCreated) { png3.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void Dispose_DisposesRemainingPngs()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(4);
            NativeArray<byte> png = default;
            try
            {
                png = MakePng(16);
                Assert.That(queue.TryEnqueue(MakeRequest(1), png), Is.True);
                png = default;

                queue.Dispose();

                Assert.That(queue.IsCreated, Is.False);
            }
            finally
            {
                if (png.IsCreated) { png.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void Dispose_MultipleTimesSafe()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            queue.Dispose();

            Assert.DoesNotThrow(() => queue.Dispose());
        }

        [Test]
        public void Dispose_AllApiContract()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            queue.Dispose();

            Assert.That(queue.IsCreated, Is.False);
            Assert.Throws<ObjectDisposedException>(() => { int _ = queue.Capacity; });
            Assert.Throws<ObjectDisposedException>(() => { int _ = queue.Count; });
            Assert.Throws<ObjectDisposedException>(() => { long _ = queue.TotalAccepted; });
            Assert.Throws<ObjectDisposedException>(() => { long _ = queue.TotalRejected; });
            Assert.Throws<ObjectDisposedException>(() => queue.TryEnqueue(MakeRequest(1), default));
            Assert.Throws<ObjectDisposedException>(() => queue.TryDequeue(out _, out _));
            Assert.Throws<ObjectDisposedException>(() => queue.Clear());
        }

        [Test]
        public void PngData_UnchangedByEnqueueDequeue()
        {
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            NativeArray<byte> png = default;
            NativeArray<byte> snapshot = default;
            NativeArray<byte> dequeued = default;
            try
            {
                png = new NativeArray<byte>(16, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < 16; i++)
                {
                    png[i] = (byte)(i * 3);
                }

                snapshot = new NativeArray<byte>(16, Allocator.Persistent);
                for (int i = 0; i < 16; i++)
                {
                    snapshot[i] = png[i];
                }

                Assert.That(queue.TryEnqueue(MakeRequest(1), png), Is.True);
                png = default;

                Assert.That(queue.TryDequeue(out _, out dequeued), Is.True);

                for (int i = 0; i < 16; i++)
                {
                    Assert.That(dequeued[i], Is.EqualTo(snapshot[i]), "PNG data changed at index " + i);
                }
            }
            finally
            {
                if (dequeued.IsCreated) { dequeued.Dispose(); }
                if (snapshot.IsCreated) { snapshot.Dispose(); }
                if (png.IsCreated) { png.Dispose(); }
                queue.Dispose();
            }
        }

        [Test]
        public void Queue_HasNoTraceOrDispatcherDependency()
        {
            Type type = typeof(CaptureFramePngQueue);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)), "Field references TraceLogger: " + field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameReadbackCompletionRouter)), "Field references Router: " + field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(UnityRenderTextureReadbackDispatcher)), "Field references Dispatcher: " + field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameReadbackBufferPool)), "Field references Pool: " + field.Name);
            }
        }
    }
}
