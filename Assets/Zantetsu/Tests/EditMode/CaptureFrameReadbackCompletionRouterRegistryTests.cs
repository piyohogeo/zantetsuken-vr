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
    public class CaptureFrameReadbackCompletionRouterRegistryTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunManifest MakeManifest(long testRunId = 7)
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

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long unityFrameId = 2)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, unityFrameId, 3, 4, captureFrameId, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(1.0, 1.0 / 90.0, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureFrameRequest request)
        {
            TraceRunManifest manifest = MakeManifest(request.TraceContext.TestRunId);
            CaptureRunReference run = new CaptureRunReference(
                manifest,
                100,
                5,
                TraceRunManifestCodec.ComputeContentSha256(manifest));

            return new CaptureFrameRecord(
                run,
                request,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        private static CaptureFrameRecord RegisterRecord(CaptureFrameRecordRegistry registry, long captureFrameId, long unityFrameId = 2)
        {
            CaptureFrameRecord record = MakeRecord(MakeRequest(captureFrameId, unityFrameId));
            Assert.That(registry.TryRegister(record), Is.True);
            return record;
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

        private static void SetForceNextEnqueueError(CaptureFramePngQueue queue)
        {
            FieldInfo field = typeof(CaptureFramePngQueue).GetField("_forceNextEnqueueError", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(queue, true);
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
        public void NullRegistry_Rejected_BeforeCollect()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<ArgumentNullException>(() => router.TryCollectEncodeAndEnqueue(queue, null));

                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void NullQueue_Rejected_BeforeCollect()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<ArgumentNullException>(() => router.TryCollectEncodeAndEnqueue(null, registry));

                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(0));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void DisposedQueue_Rejected_BeforeCollect()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                queue.Dispose();

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<ObjectDisposedException>(() => router.TryCollectEncodeAndEnqueue(queue, registry));

                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void NoActive_None_RegistryUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RegisterRecord(registry, 5);

                try
                {
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.None));

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(queue.Count, Is.EqualTo(0));
                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
                finally
                {
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void Success_Queued_RecordKept()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RegisterRecord(registry, 42);

                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(42), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(queue.TotalAccepted, Is.EqualTo(1));
                    Assert.That(pool.RentedCount, Is.EqualTo(0));

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(MakeRequest(42), out CaptureFrameRecord kept), Is.True);
                    Assert.That(kept, Is.Not.Null);

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
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void ReadbackError_Dropped_OnlyMatchingRecordRemoved()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);

                RegisterRecord(registry, 5);
                RegisterRecord(registry, 6);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(MakeRequest(5), out _), Is.False);
                    Assert.That(registry.TryGet(MakeRequest(6), out _), Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
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
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void QueueFull_Dropped_OnlyMatchingRecordRemoved_EncodedThenDroppedTrace()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(1);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);

                RegisterRecord(registry, 1);
                RegisterRecord(registry, 2);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(dispatcher.TryStart(MakeRequest(2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    Assert.That(queue.Count, Is.EqualTo(1));
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(MakeRequest(1), out _), Is.True);
                    Assert.That(registry.TryGet(MakeRequest(2), out _), Is.False);
                    Assert.That(pool.RentedCount, Is.EqualTo(0));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(3));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(2).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(logger.GetHistoryEvent(2).Value1, Is.EqualTo((int)CaptureFrameDropReason.EncodedPngQueueFull));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void MultipleRecords_OutOfOrderCompletion_OnlyMatchingRecordReconciled()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(4, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);

                RegisterRecord(registry, 1);
                RegisterRecord(registry, 2);
                RegisterRecord(registry, 3);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    // Start in reverse registration order: 3, 2, 1.
                    Assert.That(dispatcher.TryStart(MakeRequest(3), rt), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(2), rt), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Queued)); // id 3
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Queued)); // id 2
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Dropped)); // id 1, queue full

                    Assert.That(queue.Count, Is.EqualTo(2));
                    Assert.That(registry.Count, Is.EqualTo(2));
                    Assert.That(registry.TryGet(MakeRequest(3), out _), Is.True);
                    Assert.That(registry.TryGet(MakeRequest(2), out _), Is.True);
                    Assert.That(registry.TryGet(MakeRequest(1), out _), Is.False);
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void MissingRecord_InvalidOperationException_RawSlotReleased()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<InvalidOperationException>(() => router.TryCollectEncodeAndEnqueue(queue, registry));

                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(0));
                    Assert.That(queue.Count, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void MismatchedRequest_InvalidOperationException_RawSlotReleased_RegistryUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RegisterRecord(registry, 5, unityFrameId: 2);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(5, unityFrameId: 99), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.Throws<InvalidOperationException>(() => router.TryCollectEncodeAndEnqueue(queue, registry));

                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(MakeRequest(5, unityFrameId: 2), out _), Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void DisposedLogger_RecordEncodedFails_PngAndRawAndRecordCleaned()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

            RegisterRecord(registry, 5);

            RenderTexture rt = CreateTex2D(2, 2);
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                logger.Dispose();

                Assert.Throws<ObjectDisposedException>(() => router.TryCollectEncodeAndEnqueue(queue, registry));

                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            }
            finally
            {
                DrainActiveReadbacks(dispatcher);
                DestroyTexture(rt);
                queue.Dispose();
                dispatcher.Dispose();
                pool.Dispose();
            }
        }

        [Test]
        public void DisposedLogger_DropTraceFails_RawAndRecordCleaned()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

            RegisterRecord(registry, 5);

            RenderTexture rt = CreateTex2D(2, 2);
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                SetForceNextError(dispatcher);

                logger.Dispose();

                Assert.Throws<ObjectDisposedException>(() => router.TryCollectEncodeAndEnqueue(queue, registry));

                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            }
            finally
            {
                DrainActiveReadbacks(dispatcher);
                DestroyTexture(rt);
                queue.Dispose();
                dispatcher.Dispose();
                pool.Dispose();
            }
        }

        [Test]
        public void EnqueueError_PngAndRecordCleaned()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
            CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

            RegisterRecord(registry, 5);

            RenderTexture rt = CreateTex2D(2, 2);
            try
            {
                Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                SetForceNextEnqueueError(queue);

                Assert.Throws<ObjectDisposedException>(() => router.TryCollectEncodeAndEnqueue(queue, registry));

                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            }
            finally
            {
                DrainActiveReadbacks(dispatcher);
                DestroyTexture(rt);
                queue.Dispose();
                dispatcher.Dispose();
                pool.Dispose();
            }
        }

        [Test]
        public void QueuedPng_NotDisposedByRouter()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RegisterRecord(registry, 42);

                RenderTexture rt = CreateTex2D(2, 2);
                NativeArray<byte> png = default;
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(42), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out png), Is.True);
                    Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(42));
                    AssertPngSignature(png);

                    Assert.That(registry.TryGet(MakeRequest(42), out CaptureFrameRecord kept), Is.True);
                    Assert.That(kept, Is.Not.Null);
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
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void AfterRemove_SameCaptureFrameId_ReRegisterSucceeds()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);

                RegisterRecord(registry, 5);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(5), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);

                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));
                    Assert.That(registry.Count, Is.EqualTo(0));

                    CaptureFrameRecord replacement = MakeRecord(MakeRequest(5));
                    Assert.That(registry.TryRegister(replacement), Is.True);
                    Assert.That(registry.Count, Is.EqualTo(1));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void NonRegistryOverload_BehaviorUnchanged()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(2);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(42), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                    Assert.That(queue.Count, Is.EqualTo(1));

                    Assert.That(dispatcher.TryStart(MakeRequest(43), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    logger.Drain();
                    Assert.That(logger.HistoryCount, Is.EqualTo(2));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }

        [Test]
        public void TraceOrder_MatchesExistingContract()
        {
            using (TraceLogger logger = new TraceLogger(16))
            {
                CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameReadbackCompletionRouter router = new CaptureFrameReadbackCompletionRouter(dispatcher, observer);
                CaptureFramePngQueue queue = new CaptureFramePngQueue(1);
                CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);

                RegisterRecord(registry, 1);
                RegisterRecord(registry, 2);
                RegisterRecord(registry, 3);

                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                    Assert.That(dispatcher.TryStart(MakeRequest(2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    SetForceNextError(dispatcher);
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    Assert.That(dispatcher.TryStart(MakeRequest(3), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(router.TryCollectEncodeAndEnqueue(queue, registry), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                    logger.Drain();

                    Assert.That(logger.HistoryCount, Is.EqualTo(4));
                    Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(logger.GetHistoryEvent(1).Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
                    Assert.That(logger.GetHistoryEvent(2).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                    Assert.That(logger.GetHistoryEvent(3).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                    Assert.That(logger.GetHistoryEvent(3).Value1, Is.EqualTo((int)CaptureFrameDropReason.EncodedPngQueueFull));

                    Assert.That(registry.Count, Is.EqualTo(1));
                    Assert.That(registry.TryGet(MakeRequest(1), out _), Is.True);
                }
                finally
                {
                    DrainActiveReadbacks(dispatcher);
                    DestroyTexture(rt);
                    queue.Dispose();
                    dispatcher.Dispose();
                    pool.Dispose();
                }
            }
        }
    }
}
