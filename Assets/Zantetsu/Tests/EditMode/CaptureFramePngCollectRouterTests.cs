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
    public class CaptureFramePngCollectRouterTests
    {
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

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static void AssertPngSignature(NativeArray<byte> png)
        {
            Assert.That(png.Length, Is.GreaterThan(8));
            for (int i = 0; i < 8; i++)
            {
                Assert.That(png[i], Is.EqualTo(PngSignature[i]), "PNG signature mismatch at byte " + i);
            }
        }

        [Test]
        public void PngCollectStatus_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFramePngCollectStatus);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureFramePngCollectStatus.None, Is.EqualTo(0));
            Assert.That((int)CaptureFramePngCollectStatus.Encoded, Is.EqualTo(1));
            Assert.That((int)CaptureFramePngCollectStatus.Dropped, Is.EqualTo(2));
        }

        [Test]
        public void NoActive_None_Defaults_NoTrace()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

                Assert.That(router.TryCollectAndEncodePng(out CaptureFrameRequest request, out NativeArray<byte> png), Is.EqualTo(CaptureFramePngCollectStatus.None));
                Assert.That(request.IsValid, Is.False);
                Assert.That(png.IsCreated, Is.False);

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void ReadbackError_Dropped_Defaults_TracesOnce()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);

                    Assert.That(router.TryCollectAndEncodePng(out CaptureFrameRequest request, out NativeArray<byte> png), Is.EqualTo(CaptureFramePngCollectStatus.Dropped));
                    Assert.That(request.IsValid, Is.False);
                    Assert.That(png.IsCreated, Is.False);

                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    TraceEvent e = logger.GetHistoryEvent(0);
                    Assert.That(e.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(e.Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Success_Encoded_CorrectCorrelationAndTrace()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png = default;
                try
                {
                    CaptureFrameRequest request = MakeRequest(42);
                    Assert.That(dispatcher.TryStart(request, rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectAndEncodePng(out CaptureFrameRequest frameRequest, out png), Is.EqualTo(CaptureFramePngCollectStatus.Encoded));

                    Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(42));
                    Assert.That(png.IsCreated, Is.True);
                    AssertPngSignature(png);

                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    TraceEvent e = logger.GetHistoryEvent(0);
                    Assert.That(e.EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(e.CaptureFrameId, Is.EqualTo(42));
                    Assert.That(e.Timestamp, Is.EqualTo(1));
                    Assert.That(e.FrameId, Is.EqualTo(2));
                    Assert.That(e.OpenXRFrameId, Is.EqualTo(6));
                    Assert.That(e.TestRunId, Is.EqualTo(7));

                    Assert.That(double.IsNaN(e.Value0), Is.False);
                    Assert.That(double.IsInfinity(e.Value0), Is.False);
                    Assert.That(e.Value0, Is.GreaterThanOrEqualTo(0));
                    Assert.That(e.Value1, Is.EqualTo(png.Length));
                }
                finally
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Success_ReturnedPngIsCallerOwned()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectAndEncodePng(out _, out png), Is.EqualTo(CaptureFramePngCollectStatus.Encoded));

                    // Raw slot already returned; pool reusable.
                    Assert.That(pool.AvailableCount, Is.EqualTo(1));

                    // PNG remains valid after the method returns; caller disposes it.
                    Assert.That(png.IsCreated, Is.True);
                    Assert.That(png.Length, Is.GreaterThan(0));
                    png.Dispose();
                    Assert.That(png.IsCreated, Is.False);
                }
                finally
                {
                    if (png.IsCreated)
                    {
                        png.Dispose();
                    }

                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void DisposedLogger_RecordEncodedFails_SlotReleased_NoSuccess()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

            RenderTexture rt = CreateTex2D(2, 2);
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                logger.Dispose();

                Assert.Throws<ObjectDisposedException>(() => router.TryCollectAndEncodePng(out _, out _));

                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
            }
            finally
            {
                DestroyTexture(rt);
                dispatcher.Dispose();
                pool.Dispose();
            }
        }

        [Test]
        public void MultipleCompletions_DeterministicOrder()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png1 = default;
                NativeArray<byte> png2 = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(100), rt), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(200), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectAndEncodePng(out CaptureFrameRequest request1, out png1), Is.EqualTo(CaptureFramePngCollectStatus.Encoded));
                    Assert.That(router.TryCollectAndEncodePng(out CaptureFrameRequest request2, out png2), Is.EqualTo(CaptureFramePngCollectStatus.Encoded));

                    Assert.That(request1.TraceContext.CaptureFrameId, Is.EqualTo(100));
                    Assert.That(request2.TraceContext.CaptureFrameId, Is.EqualTo(200));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(2));
                    Assert.That(logger.GetHistoryEvent(0).CaptureFrameId, Is.EqualTo(100));
                    Assert.That(logger.GetHistoryEvent(1).CaptureFrameId, Is.EqualTo(200));
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

                    DestroyTexture(rt);
                }
            }
        }
    }
}
