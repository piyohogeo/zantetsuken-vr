using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngQueueRouterTests
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static CaptureFrameTraceContext MakeContext()
        {
            return new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);
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

        private static RenderTexture CreateTex2D(int width, int height)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            return rt;
        }

        private static void DestroyTexture(RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }

        private static void SetForceNextError(UnityRenderTextureReadbackDispatcher dispatcher)
        {
            FieldInfo field = typeof(UnityRenderTextureReadbackDispatcher).GetField("_forceNextError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(dispatcher, true);
        }

        private static void AssertPngSignature(NativeArray<byte> png)
        {
            Assert.That(png.Length, Is.GreaterThan(8));
            for (int i = 0; i < 8; i++)
            {
                Assert.That(png[i], Is.EqualTo(PngSignature[i]), "PNG signature mismatch at byte " + i);
            }
        }

        private static void DrainActiveReadbacks(UnityRenderTextureReadbackDispatcher dispatcher)
        {
            AsyncGPUReadback.WaitAllRequests();

            while (dispatcher.TryCollect(out CaptureFrameReadbackResult result))
            {
                dispatcher.Release(result);
            }
        }

        [Test]
        public void DropReason_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFrameDropReason);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureFrameDropReason.None, Is.EqualTo(0));
            Assert.That((int)CaptureFrameDropReason.RequestQueueFull, Is.EqualTo(1));
            Assert.That((int)CaptureFrameDropReason.ReadbackFailed, Is.EqualTo(2));
            Assert.That((int)CaptureFrameDropReason.EncodedPngQueueFull, Is.EqualTo(3));
        }

        [Test]
        public void PngQueueStatus_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFramePngQueueStatus);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureFramePngQueueStatus.None, Is.EqualTo(0));
            Assert.That((int)CaptureFramePngQueueStatus.Queued, Is.EqualTo(1));
            Assert.That((int)CaptureFramePngQueueStatus.Dropped, Is.EqualTo(2));
        }

        [Test]
        public void Observer_AcceptsEncodedPngQueueFull()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                observer.RecordDropped(MakeContext(), CaptureFrameDropReason.EncodedPngQueueFull);
                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.EncodedPngQueueFull));
                Assert.That(logger.GetHistoryEvent(0).Reason, Is.EqualTo(TraceReason.None));
            }
        }

        [Test]
        public void Observer_RejectsNoneAndUndefined()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordDropped(MakeContext(), CaptureFrameDropReason.None));
                Assert.Throws<ArgumentOutOfRangeException>(() => observer.RecordDropped(MakeContext(), (CaptureFrameDropReason)999));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void NullQueue_Throws_DoesNotConsumeReadbackOrTrace()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<ArgumentNullException>(() => router.TryCollectEncodeAndEnqueue(null));

                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void DisposedQueue_Throws_BeforeCollect()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                queue.Dispose();

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<ObjectDisposedException>(() => router.TryCollectEncodeAndEnqueue(queue));

                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void NoActive_None_QueueUnchanged()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                try
                {
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.None));

                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                    Assert.That(queue.TotalRejected, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    queue.Dispose();
                }
            }
        }

        [Test]
        public void ReadbackError_Dropped_ReadbackFailedOnce_QueueUnchanged()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(0));
                    Assert.That(queue.TotalRejected, Is.EqualTo(0));
                    Assert.That(pool.RentedCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                }
            }
        }

        [Test]
        public void Success_Queued_DequeueMatches()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(42), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                    Assert.That(queue.TotalRejected, Is.EqualTo(0));
                    Assert.That(pool.RentedCount, Is.EqualTo(0));

                    Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out png), Is.True);
                    Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(42));
                    AssertPngSignature(png);

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                }
                finally
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                }
            }
        }

        [Test]
        public void QueueFull_Dropped_EncodedThenDroppedTraceOrder()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(1);
                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> existingPng = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(1));

                    Assert.That(dispatcher.TryStart(MakeRequest(2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                    Assert.That(queue.TotalRejected, Is.EqualTo(1));
                    Assert.That(pool.RentedCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(3));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(2).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(logger.GetHistoryEvent(2).Value1, Is.EqualTo((int)CaptureFrameDropReason.EncodedPngQueueFull));

                    Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out existingPng), Is.True);
                    Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(1));
                    AssertPngSignature(existingPng);
                }
                finally
                {
                    if (existingPng.IsCreated)
                    {
                        existingPng.Dispose();
                    }

                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                }
            }
        }

        [Test]
        public void ConsecutiveSuccess_FifoOrder()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png1 = default;
                NativeArray<byte> png2 = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(100), rt), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(200), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(queue.Count, Is.EqualTo(2));

                    Assert.That(queue.TryDequeue(out CaptureFrameRequest fr1, out png1), Is.True);
                    Assert.That(queue.TryDequeue(out CaptureFrameRequest fr2, out png2), Is.True);
                    Assert.That(fr1.TraceContext.CaptureFrameId, Is.EqualTo(100));
                    Assert.That(fr2.TraceContext.CaptureFrameId, Is.EqualTo(200));
                    AssertPngSignature(png1);
                    AssertPngSignature(png2);
                }
                finally
                {
                    if (png1.IsCreated)
                    {
                        png1.Dispose();
                    }

                    if (png2.IsCreated)
                    {
                        png2.Dispose();
                    }

                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                }
            }
        }
    }
}
