using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameReadbackCompletionRouterTests
    {
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

        [Test]
        public void DropReason_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFrameDropReason);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureFrameDropReason.None, Is.EqualTo(0));
            Assert.That((int)CaptureFrameDropReason.RequestQueueFull, Is.EqualTo(1));
            Assert.That((int)CaptureFrameDropReason.ReadbackFailed, Is.EqualTo(2));
        }

        [Test]
        public void CollectStatus_EnumShapeAndValues()
        {
            Type type = typeof(CaptureFrameReadbackCollectStatus);

            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetName(type, 0), Is.EqualTo(nameof(CaptureFrameReadbackCollectStatus.None)));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo(nameof(CaptureFrameReadbackCollectStatus.Succeeded)));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo(nameof(CaptureFrameReadbackCollectStatus.Dropped)));
            Assert.That((int)CaptureFrameReadbackCollectStatus.None, Is.EqualTo(0));
            Assert.That((int)CaptureFrameReadbackCollectStatus.Succeeded, Is.EqualTo(1));
            Assert.That((int)CaptureFrameReadbackCollectStatus.Dropped, Is.EqualTo(2));
        }

        [Test]
        public void Observer_AcceptsBothDropReasons()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameTraceContext context = MakeContext();

                observer.RecordDropped(context, CaptureFrameDropReason.RequestQueueFull);
                observer.RecordDropped(context, CaptureFrameDropReason.ReadbackFailed);
                logger.Drain();

                Assert.That(logger.HistoryCount, Is.EqualTo(2));
                Assert.That(logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));
                Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                Assert.That(logger.GetHistoryEvent(1).Reason, Is.EqualTo(TraceReason.None));
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
        public void Router_NullDependencies_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Assert.Throws<ArgumentNullException>(() => new CaptureFrameReadbackCompletionRouter(null, observer));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameReadbackCompletionRouter(dispatcher, null));
            }
        }

        [Test]
        public void Router_NoActive_None_NoTrace()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

                Assert.That(router.TryCollect(out CaptureFrameReadbackResult result), Is.EqualTo(CaptureFrameReadbackCollectStatus.None));
                Assert.That(result.IsValid, Is.False);
                Assert.That(result.OperationId, Is.EqualTo(0));

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Router_Success_Succeeded_SlotRented()
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

                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult result), Is.EqualTo(CaptureFrameReadbackCollectStatus.Succeeded));
                    Assert.That(result.IsValid, Is.True);
                    Assert.That(result.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(5));
                    Assert.That(pool.RentedCount, Is.EqualTo(1));

                    dispatcher.Release(result);
                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Router_Success_NoTraceAdded()
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
                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult result), Is.EqualTo(CaptureFrameReadbackCollectStatus.Succeeded));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));

                    dispatcher.Release(result);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Router_Error_Dropped_TracesAndReleases()
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

                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult result), Is.EqualTo(CaptureFrameReadbackCollectStatus.Dropped));
                    Assert.That(result.IsValid, Is.False);
                    Assert.That(result.OperationId, Is.EqualTo(0));

                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                    Assert.That(router.ActiveReadbackCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                    TraceEvent e = logger.GetHistoryEvent(0);
                    Assert.That(e.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(e.Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                    Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Router_Error_NotRecollected()
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

                    Assert.That(router.TryCollect(out _), Is.EqualTo(CaptureFrameReadbackCollectStatus.Dropped));
                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult second), Is.EqualTo(CaptureFrameReadbackCollectStatus.None));
                    Assert.That(second.IsValid, Is.False);

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Router_Error_SlotReusable()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
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

                    Assert.That(router.TryCollect(out _), Is.EqualTo(CaptureFrameReadbackCollectStatus.Dropped));
                    Assert.That(pool.AvailableCount, Is.EqualTo(1));

                    Assert.That(dispatcher.TryStart(MakeRequest(9), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult r), Is.EqualTo(CaptureFrameReadbackCollectStatus.Succeeded));
                    Assert.That(r.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(9));
                    dispatcher.Release(r);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Router_Error_ObserverDisposed_PreservesException_Releases()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);

            RenderTexture rt = CreateTex2D(2, 2);
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                SetForceNextError(dispatcher);

                logger.Dispose();

                Assert.Throws<ObjectDisposedException>(() => router.TryCollect(out _));

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
        public void Router_DoesNotAutoDrain()
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

                    Assert.That(router.TryCollect(out _), Is.EqualTo(CaptureFrameReadbackCollectStatus.Dropped));

                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Router_OwnerCanContinueUsingDispatcherAndLogger()
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
                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult r), Is.EqualTo(CaptureFrameReadbackCollectStatus.Succeeded));
                    dispatcher.Release(r);

                    observer.RecordQueued(MakeContext());
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(1));

                    Assert.That(dispatcher.TryStart(MakeRequest(9), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollect(out CaptureFrameReadbackResult r2), Is.EqualTo(CaptureFrameReadbackCollectStatus.Succeeded));
                    dispatcher.Release(r2);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }
    }
}
