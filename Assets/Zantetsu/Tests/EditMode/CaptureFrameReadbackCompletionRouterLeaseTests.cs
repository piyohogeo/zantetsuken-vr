using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCaptureFrameReadbackCompletionRouterLeaseTests
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

        private static CaptureFrameProfile MakeProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
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

        private sealed class RegisteredEntry
        {
            public readonly CaptureFrameRequest Request;
            public readonly CaptureFrameRenderTargetLease Lease;

            public RegisteredEntry(CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
            {
                Request = request;
                Lease = lease;
            }
        }

        private sealed class LeaseScope
        {
            public TraceLogger Logger;
            public CaptureFrameReadbackBufferPool BufferPool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureFrameTraceObserver Observer;
            public PngJsonCaptureFrameReadbackCompletionRouter Router;
            public CaptureFramePngQueue Queue;
            public CaptureFrameRecordRegistry RecordRegistry;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public NativeArray<byte> DequeuedPng;
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<RenderTexture> Textures = new List<RenderTexture>();
            public readonly List<CaptureFrameRenderTargetPool> ExtraPools = new List<CaptureFrameRenderTargetPool>();

            public RenderTexture CreateTexture(int width, int height)
            {
                RenderTexture rt = CreateTex2D(width, height);
                Textures.Add(rt);
                return rt;
            }

            public CaptureFrameRenderTargetLease Rent()
            {
                Assert.That(Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Held.Add(lease);
                return lease;
            }

            public RenderTexture RentAndRegister(CaptureFrameRequest request, out CaptureFrameRenderTargetLease lease)
            {
                Assert.That(Pool.TryRent(out lease), Is.True);
                Held.Add(lease);

                Assert.That(LeaseRegistry.TryRegister(request, lease), Is.True);
                RemoveFromHeld(lease);
                Registered.Add(new RegisteredEntry(request, lease));
                return Pool.GetRenderTexture(lease);
            }

            private void RemoveFromHeld(CaptureFrameRenderTargetLease lease)
            {
                for (int i = Held.Count - 1; i >= 0; i--)
                {
                    if (Held[i].SlotIndex == lease.SlotIndex)
                    {
                        Held.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        private static LeaseScope NewScope(int poolCapacity, int leaseCapacity, int recordCapacity, int queueCapacity)
        {
            LeaseScope scope = new LeaseScope();
            scope.Logger = new TraceLogger(8);
            scope.BufferPool = new CaptureFrameReadbackBufferPool(2, 64);
            scope.Dispatcher = new UnityRenderTextureReadbackDispatcher(scope.BufferPool);
            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.Router = new PngJsonCaptureFrameReadbackCompletionRouter(scope.Dispatcher, scope.Observer);
            scope.Queue = new CaptureFramePngQueue(queueCapacity);
            scope.RecordRegistry = new CaptureFrameRecordRegistry(recordCapacity);
            scope.Pool = new CaptureFrameRenderTargetPool(poolCapacity, MakeProfile());
            scope.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(leaseCapacity, scope.Pool);
            return scope;
        }

        private static Exception[] CleanupLeaseTest(LeaseScope scope)
        {
            Exception[] errors = null;
            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Dispatcher.IsCreated)
                {
                    while (scope.Dispatcher.TryCollect(out CaptureFrameReadbackResult result))
                    {
                        scope.Dispatcher.Release(result);
                    }
                }
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            if (gpuSafe)
            {
                for (int i = scope.Registered.Count - 1; i >= 0; i--)
                {
                    RegisteredEntry entry = scope.Registered[i];
                    scope.Registered.RemoveAt(i);
                    try
                    {
                        if (scope.LeaseRegistry.TryRemove(entry.Request, out CaptureFrameRenderTargetLease lease))
                        {
                            scope.Pool.Return(lease);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                for (int i = scope.Held.Count - 1; i >= 0; i--)
                {
                    CaptureFrameRenderTargetLease lease = scope.Held[i];
                    scope.Held.RemoveAt(i);
                    try
                    {
                        scope.Pool.Return(lease);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }
            }

            if (gpuSafe)
            {
                for (int i = scope.Textures.Count - 1; i >= 0; i--)
                {
                    RenderTexture rt = scope.Textures[i];
                    scope.Textures.RemoveAt(i);
                    try
                    {
                        DestroyTexture(rt);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }
            }

            if (scope.DequeuedPng.IsCreated)
            {
                try
                {
                    scope.DequeuedPng.Dispose();
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }

                scope.DequeuedPng = default;
            }

            foreach (CaptureFrameRenderTargetPool extra in scope.ExtraPools)
            {
                try
                {
                    extra.Dispose();
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            try
            {
                if (scope.Queue.IsCreated)
                {
                    scope.Queue.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Dispatcher.IsCreated)
                {
                    scope.Dispatcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.BufferPool.IsCreated)
                {
                    scope.BufferPool.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                scope.Pool.Dispose();
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Logger != null && scope.Logger.IsCreated)
                {
                    scope.Logger.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            return errors;
        }

        private static void RunLeaseTest(LeaseScope scope, Action body)
        {
            ExceptionDispatchInfo bodyException = null;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] errors = CleanupLeaseTest(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        private static Exception[] AppendCleanupException(Exception[] cleanupExceptions, Exception ex)
        {
            if (ex == null)
            {
                return cleanupExceptions;
            }

            if (cleanupExceptions == null || cleanupExceptions.Length == 0)
            {
                return new[] { ex };
            }

            Exception[] combined = new Exception[cleanupExceptions.Length + 1];
            Array.Copy(cleanupExceptions, combined, cleanupExceptions.Length);
            combined[cleanupExceptions.Length] = ex;
            return combined;
        }

        private static void ThrowCleanupAndBody(ExceptionDispatchInfo bodyException, Exception[] cleanupExceptions)
        {
            bool hasBody = bodyException != null;
            bool hasCleanup = cleanupExceptions != null && cleanupExceptions.Length > 0;

            if (hasBody && hasCleanup)
            {
                Exception[] all = new Exception[cleanupExceptions.Length + 1];
                all[0] = bodyException.SourceException;
                Array.Copy(cleanupExceptions, 0, all, 1, cleanupExceptions.Length);
                throw new AggregateException(all);
            }

            if (hasBody)
            {
                bodyException.Throw();
            }
            else if (hasCleanup)
            {
                if (cleanupExceptions.Length == 1)
                {
                    ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                }
                else
                {
                    throw new AggregateException(cleanupExceptions);
                }
            }
        }

        [Test]
        public void NullDependencies_Rejected_BeforeCollect()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                RenderTexture rt = scope.CreateTexture(2, 2);

                Assert.That(scope.Dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.Throws<ArgumentNullException>(() => scope.Router.TryCollectEncodeAndEnqueue(null, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, null, scope.LeaseRegistry, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, null, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, null));

                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void DisposedQueue_Rejected_BeforeCollect()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                scope.Queue.Dispose();
                RenderTexture rt = scope.CreateTexture(2, 2);

                Assert.That(scope.Dispatcher.TryStart(MakeRequest(1), rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.Throws<ObjectDisposedException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));

                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void NoCompletion_None_AllUnchanged()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                RegisterRecord(scope.RecordRegistry, 42);

                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.None));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void Success_Queued_RecordKeptLeaseReturned()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out _);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));

                Assert.That(scope.Queue.TryDequeue(out CaptureFrameRequest frameRequest, out NativeArray<byte> png), Is.True);
                scope.DequeuedPng = png;
                Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(42));
                AssertPngSignature(png);

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
            });
        }

        [Test]
        public void ReadbackError_Dropped_LeaseReturnedRecordRemoved()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out _);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                SetForceNextError(scope.Dispatcher);

                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.ReadbackFailed));
            });
        }

        [Test]
        public void QueueFull_Dropped_LeaseReturnedRecordRemoved()
        {
            LeaseScope scope = NewScope(2, 2, 4, 1);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRequest r2 = MakeRequest(2);
                RegisterRecord(scope.RecordRegistry, 1);
                RegisterRecord(scope.RecordRegistry, 2);
                RenderTexture rt1 = scope.RentAndRegister(r1, out _);
                RenderTexture rt2 = scope.RentAndRegister(r2, out _);

                Assert.That(scope.Dispatcher.TryStart(r1, rt1), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                Assert.That(scope.Dispatcher.TryStart(r2, rt2), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Dropped));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.TryGet(r1, out _), Is.True);
                Assert.That(scope.RecordRegistry.TryGet(r2, out _), Is.False);
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(3));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                Assert.That(scope.Logger.GetHistoryEvent(1).EventType, Is.EqualTo(TraceEventType.CaptureFrameEncoded));
                Assert.That(scope.Logger.GetHistoryEvent(2).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(scope.Logger.GetHistoryEvent(2).Value1, Is.EqualTo((int)CaptureFrameDropReason.EncodedPngQueueFull));
            });
        }

        [Test]
        public void EnqueueThrows_LeaseReturnedRecordRemoved()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out _);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();
                SetForceNextEnqueueError(scope.Queue);

                Assert.Throws<ObjectDisposedException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void EncodeTraceThrows_LeaseReturnedRecordRemoved()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out _);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                scope.Logger.Dispose();

                Assert.Throws<ObjectDisposedException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void LeaseNotRegistered_FailClosed()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.CreateTexture(2, 2);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.Throws<InvalidOperationException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void LeaseRequestMismatch_FailClosed()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);

                // Register the lease under a different request with the same CaptureFrameId.
                CaptureFrameRequest otherRequest = new CaptureFrameRequest(
                    new CaptureFrameTraceContext(1, 99, 3, 4, 42, 6, 7, 8, 9, 10, 11, 12),
                    CaptureSource.UnityRenderTexture,
                    CaptureEye.Left,
                    new CaptureImageRect(0, 0, 2, 2),
                    0,
                    CapturePixelFormat.Rgba32);

                RenderTexture rt = scope.RentAndRegister(otherRequest, out _);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.Throws<InvalidOperationException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void WrongPoolLease_FailClosed()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRenderTargetPool otherPool = new CaptureFrameRenderTargetPool(2, MakeProfile());
                scope.ExtraPools.Add(otherPool);

                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out _);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.Throws<InvalidOperationException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, otherPool));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void StaleLease_FailClosed()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out CaptureFrameRenderTargetLease lease);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                // Simulate the lease going stale while still registered.
                scope.Pool.Return(lease);
                scope.Registered.Clear();

                Assert.Throws<InvalidOperationException>(() => scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool));

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void AfterCompletion_SlotReRentable_OldLeaseStale()
        {
            LeaseScope scope = NewScope(1, 1, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                RegisterRecord(scope.RecordRegistry, 42);
                RenderTexture rt = scope.RentAndRegister(request, out CaptureFrameRenderTargetLease lease);

                Assert.That(scope.Dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));

                CaptureFrameRenderTargetLease newLease = scope.Rent();
                Assert.That(newLease.SlotIndex, Is.EqualTo(lease.SlotIndex));
                Assert.Throws<InvalidOperationException>(() => scope.Pool.GetRenderTexture(lease));
            });
        }

        [Test]
        public void MultipleReadbacks_OnePerCall()
        {
            LeaseScope scope = NewScope(2, 2, 2, 2);
            RunLeaseTest(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRequest r2 = MakeRequest(2);
                RegisterRecord(scope.RecordRegistry, 1);
                RegisterRecord(scope.RecordRegistry, 2);
                RenderTexture rt1 = scope.RentAndRegister(r1, out _);
                RenderTexture rt2 = scope.RentAndRegister(r2, out _);

                Assert.That(scope.Dispatcher.TryStart(r1, rt1), Is.True);
                Assert.That(scope.Dispatcher.TryStart(r2, rt2), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));

                Assert.That(scope.Router.TryCollectEncodeAndEnqueue(scope.Queue, scope.RecordRegistry, scope.LeaseRegistry, scope.Pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));

                Assert.That(scope.Queue.Count, Is.EqualTo(2));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(2));
            });
        }

        [Test]
        public void GpuIntegration_RentRegisterPumpStartEncodeLeaseReturnReRent()
        {
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11u, 12);
            CaptureFrameRequest request = new CaptureFrameRequest(context, profile.Source, profile.Eye, profile.ImageRect, profile.ArrayIndex, profile.PixelFormat);

            CaptureFrameRequestQueue requestQueue = new CaptureFrameRequestQueue(1);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(1, request.RequiredByteCount);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);
            CaptureFramePngQueue queue = new CaptureFramePngQueue(1);
            CaptureFrameRecordRegistry recordRegistry = new CaptureFrameRecordRegistry(1);

            RegisterRecord(recordRegistry, 5);

            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;
            NativeArray<byte> png = default;
            bool pngHeld = false;
            bool newLeaseHeld = false;
            CaptureFrameRenderTargetLease newLease = default;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                Assert.That(leaseRegistry.TryRegister(request, lease), Is.True);
                registered = true;
                leaseHeld = false;

                Assert.That(requestQueue.TryEnqueue(request), Is.True);

                Assert.That(pump.TryStartNext(), Is.True);
                Assert.That(requestQueue.Count, Is.EqualTo(0));

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(router.TryCollectEncodeAndEnqueue(queue, recordRegistry, leaseRegistry, pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                registered = false;

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(recordRegistry.Count, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out png), Is.True);
                pngHeld = true;
                Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(5));
                AssertPngSignature(png);

                // The slot is reusable and the old lease is stale.
                Assert.That(pool.TryRent(out newLease), Is.True);
                newLeaseHeld = true;
                Assert.That(newLease.SlotIndex, Is.EqualTo(lease.SlotIndex));
                Assert.Throws<InvalidOperationException>(() => pool.GetRenderTexture(lease));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (pngHeld)
            {
                pngHeld = false;
                try { png.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
                if (dispatcher.IsCreated)
                {
                    while (dispatcher.TryCollect(out CaptureFrameReadbackResult extra))
                    {
                        dispatcher.Release(extra);
                    }
                }
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            if (gpuSafe)
            {
                if (registered)
                {
                    registered = false;
                    try
                    {
                        if (leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
                        {
                            lease = removed;
                            leaseHeld = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                if (leaseHeld)
                {
                    leaseHeld = false;
                    try { pool.Return(lease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }

                if (newLeaseHeld)
                {
                    newLeaseHeld = false;
                    try { pool.Return(newLease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }
            }

            try { pool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            try { if (dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { queue.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { logger.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
