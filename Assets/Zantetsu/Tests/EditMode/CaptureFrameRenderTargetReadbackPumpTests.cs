using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRenderTargetReadbackPumpTests
    {
        private const int BufferBytesPerSlot = 64;

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

        private sealed class PumpScope
        {
            public CaptureFrameRequestQueue Queue;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameReadbackBufferPool BufferPool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureFrameRenderTargetLeaseRegistry Registry;
            public CaptureFrameRenderTargetReadbackPump Pump;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();
        }

        private static CaptureFrameProfile MakeProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
        }

        private static CaptureFrameTraceContext MakeContext(long captureFrameId)
        {
            return new CaptureFrameTraceContext(
                captureFrameId,
                captureFrameId,
                captureFrameId,
                1,
                captureFrameId,
                captureFrameId,
                captureFrameId,
                captureFrameId,
                captureFrameId,
                captureFrameId,
                1u,
                captureFrameId);
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            return new CaptureFrameRequest(MakeContext(captureFrameId), CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRequest MakeRequestDifferent(long captureFrameId)
        {
            return new CaptureFrameRequest(MakeContext(captureFrameId), CaptureSource.UnityRenderTexture, CaptureEye.Right, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static bool RequestsIdentical(in CaptureFrameRequest a, in CaptureFrameRequest b)
        {
            return
                a.TraceContext.Timestamp == b.TraceContext.Timestamp &&
                a.TraceContext.UnityFrameId == b.TraceContext.UnityFrameId &&
                a.TraceContext.FixedStepId == b.TraceContext.FixedStepId &&
                a.TraceContext.ThreadId == b.TraceContext.ThreadId &&
                a.TraceContext.CaptureFrameId == b.TraceContext.CaptureFrameId &&
                a.TraceContext.OpenXRFrameId == b.TraceContext.OpenXRFrameId &&
                a.TraceContext.TestRunId == b.TraceContext.TestRunId &&
                a.TraceContext.SlashId == b.TraceContext.SlashId &&
                a.TraceContext.FrontEdgeId == b.TraceContext.FrontEdgeId &&
                a.TraceContext.ObjectId == b.TraceContext.ObjectId &&
                a.TraceContext.ObjectGeneration == b.TraceContext.ObjectGeneration &&
                a.TraceContext.TaskId == b.TraceContext.TaskId &&
                a.Source == b.Source &&
                a.Eye == b.Eye &&
                a.ImageRect.X == b.ImageRect.X &&
                a.ImageRect.Y == b.ImageRect.Y &&
                a.ImageRect.Width == b.ImageRect.Width &&
                a.ImageRect.Height == b.ImageRect.Height &&
                a.ArrayIndex == b.ArrayIndex &&
                a.PixelLayout.Format == b.PixelLayout.Format &&
                a.PixelLayout.Width == b.PixelLayout.Width &&
                a.PixelLayout.Height == b.PixelLayout.Height &&
                a.PixelLayout.BytesPerPixel == b.PixelLayout.BytesPerPixel &&
                a.PixelLayout.RowStrideBytes == b.PixelLayout.RowStrideBytes &&
                a.PixelLayout.ByteCount == b.PixelLayout.ByteCount;
        }

        private static PumpScope NewScope(int queueCapacity, int poolCapacity, int registryCapacity, int bufferSlotCount)
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(queueCapacity);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(poolCapacity, MakeProfile());
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(bufferSlotCount, BufferBytesPerSlot);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(registryCapacity, pool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(queue, dispatcher, registry, pool);

            return new PumpScope
            {
                Queue = queue,
                Pool = pool,
                BufferPool = bufferPool,
                Dispatcher = dispatcher,
                Registry = registry,
                Pump = pump,
            };
        }

        private static CaptureFrameRenderTargetLease RentTracked(PumpScope scope)
        {
            Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
            scope.Held.Add(lease);
            return lease;
        }

        private static bool RegisterTracked(PumpScope scope, CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
        {
            bool result = scope.Registry.TryRegister(request, lease);
            if (result)
            {
                RemoveFromHeld(scope.Held, lease);
                scope.Registered.Add(new RegisteredEntry(request, lease));
            }

            return result;
        }

        private static void RemoveFromHeld(List<CaptureFrameRenderTargetLease> held, CaptureFrameRenderTargetLease lease)
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                if (held[i].SlotIndex == lease.SlotIndex)
                {
                    held.RemoveAt(i);
                    return;
                }
            }
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
                List<Exception> all = new List<Exception>(cleanupExceptions.Length + 1);
                all.Add(bodyException.SourceException);
                all.AddRange(cleanupExceptions);
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

        private static Exception[] CleanupPumpScope(PumpScope scope)
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
                        if (scope.Registry.TryRemove(entry.Request, out CaptureFrameRenderTargetLease lease))
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

            return errors;
        }

        private static void RunPumpBody(PumpScope scope, Action body)
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

            Exception[] errors = CleanupPumpScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        private static void FillSolidColor(RenderTexture rt, Color32 color)
        {
            int width = rt.width;
            int height = rt.height;
            Texture2D temp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Color32[] pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                temp.SetPixels32(pixels);
                temp.Apply();
                Graphics.CopyTexture(temp, rt);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            PumpScope scope = NewScope(2, 2, 2, 2);
            RunPumpBody(scope, () =>
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetReadbackPump(null, scope.Dispatcher, scope.Registry, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetReadbackPump(scope.Queue, null, scope.Registry, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetReadbackPump(scope.Queue, scope.Dispatcher, null, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetReadbackPump(scope.Queue, scope.Dispatcher, scope.Registry, null));
            });
        }

        [Test]
        public void TryStartNext_EmptyQueue_ReturnsFalseAndUnchanged()
        {
            PumpScope scope = NewScope(2, 2, 2, 2);
            RunPumpBody(scope, () =>
            {
                Assert.That(scope.Pump.TryStartNext(), Is.False);

                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Registry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(scope.Pump.PendingCount, Is.EqualTo(0));
                Assert.That(scope.Pump.ActiveReadbackCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryStartNext_UnregisteredLease_ThrowsAndKeepsQueue()
        {
            PumpScope scope = NewScope(2, 2, 2, 2);
            RunPumpBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.Pump.TryStartNext());

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryStartNext_RequestMismatch_KeepsRegistryException()
        {
            PumpScope scope = NewScope(2, 2, 2, 2);
            RunPumpBody(scope, () =>
            {
                CaptureFrameRequest queued = MakeRequest(1);
                CaptureFrameRequest registered = MakeRequestDifferent(1);

                Assert.That(RegisterTracked(scope, registered, RentTracked(scope)), Is.True);
                Assert.That(scope.Queue.TryEnqueue(queued), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.Pump.TryStartNext());

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryStartNext_StaleLease_ThrowsAndKeepsQueue()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(2, MakeProfile());
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(2, BufferBytesPerSlot);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(2, pool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(queue, dispatcher, registry, pool);

            CaptureFrameRequest request = MakeRequest(1);
            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Assert.That(registry.TryRegister(request, lease), Is.True);
                Assert.That(queue.TryEnqueue(request), Is.True);

                pool.Return(lease);

                Assert.Throws<InvalidOperationException>(() => pump.TryStartNext());

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            try
            {
                if (registry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
                {
                    // The lease is stale (already returned); do not return it again.
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try { if (dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { pool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }

        [Test]
        public void TryStartNext_ForeignPoolLease_ThrowsAndKeepsQueue()
        {
            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(2);
            CaptureFrameRenderTargetPool registryPool = new CaptureFrameRenderTargetPool(2, MakeProfile());
            CaptureFrameRenderTargetPool pumpPool = new CaptureFrameRenderTargetPool(2, MakeProfile());
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(2, BufferBytesPerSlot);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(2, registryPool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(queue, dispatcher, registry, pumpPool);

            CaptureFrameRequest request = MakeRequest(1);
            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(registryPool.TryRent(out lease), Is.True);
                leaseHeld = true;

                Assert.That(registry.TryRegister(request, lease), Is.True);
                leaseHeld = false;

                Assert.That(queue.TryEnqueue(request), Is.True);

                Assert.Throws<InvalidOperationException>(() => pump.TryStartNext());

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (leaseHeld)
            {
                leaseHeld = false;
                try { registryPool.Return(lease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            try
            {
                if (registry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
                {
                    try { registryPool.Return(removed); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try { if (dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { registryPool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { pumpPool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }

        [Test]
        public void TryStartNext_DispatcherFull_ReturnsFalseAndKeepsState()
        {
            PumpScope scope = NewScope(3, 3, 3, 2);
            RunPumpBody(scope, () =>
            {
                CaptureFrameRenderTargetLease fill1 = RentTracked(scope);
                CaptureFrameRenderTargetLease fill2 = RentTracked(scope);

                Assert.That(scope.Dispatcher.TryStart(MakeRequest(101), scope.Pool.GetRenderTexture(fill1)), Is.True);
                Assert.That(scope.Dispatcher.TryStart(MakeRequest(102), scope.Pool.GetRenderTexture(fill2)), Is.True);

                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(2));

                CaptureFrameRequest target = MakeRequest(1);
                CaptureFrameRenderTargetLease targetLease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, target, targetLease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(target), Is.True);

                int queueCount = scope.Queue.Count;
                int registryCount = scope.Registry.Count;
                int rentedCount = scope.Pool.RentedCount;

                Assert.That(scope.Pump.TryStartNext(), Is.False);

                Assert.That(scope.Queue.Count, Is.EqualTo(queueCount));
                Assert.That(scope.Registry.Count, Is.EqualTo(registryCount));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(rentedCount));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(2));
            });
        }

        [Test]
        public void TryStartNext_Success_StartsOneAndDequeuesHead()
        {
            PumpScope scope = NewScope(2, 2, 2, 2);
            RunPumpBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRequest r2 = MakeRequest(2);

                Assert.That(RegisterTracked(scope, r1, RentTracked(scope)), Is.True);
                Assert.That(RegisterTracked(scope, r2, RentTracked(scope)), Is.True);

                Assert.That(scope.Queue.TryEnqueue(r1), Is.True);
                Assert.That(scope.Queue.TryEnqueue(r2), Is.True);

                Assert.That(scope.Pump.TryStartNext(), Is.True);

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(2));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(2));

                Assert.That(scope.Queue.TryPeek(out CaptureFrameRequest head), Is.True);
                Assert.That(head.TraceContext.CaptureFrameId, Is.EqualTo(2));
            });
        }

        [Test]
        public void TryStartNext_FifoOrderPreserved()
        {
            PumpScope scope = NewScope(3, 3, 3, 3);
            RunPumpBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRequest r2 = MakeRequest(2);
                CaptureFrameRequest r3 = MakeRequest(3);

                Assert.That(RegisterTracked(scope, r1, RentTracked(scope)), Is.True);
                Assert.That(RegisterTracked(scope, r2, RentTracked(scope)), Is.True);
                Assert.That(RegisterTracked(scope, r3, RentTracked(scope)), Is.True);

                Assert.That(scope.Queue.TryEnqueue(r1), Is.True);
                Assert.That(scope.Queue.TryEnqueue(r2), Is.True);
                Assert.That(scope.Queue.TryEnqueue(r3), Is.True);

                Assert.That(scope.Pump.TryStartNext(), Is.True);
                Assert.That(scope.Pump.TryStartNext(), Is.True);
                Assert.That(scope.Pump.TryStartNext(), Is.True);

                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(3));
            });
        }

        [Test]
        public void Pump_DoesNotOwnOrDisposeDependencies()
        {
            PumpScope scope = NewScope(2, 2, 2, 2);
            RunPumpBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                Assert.That(RegisterTracked(scope, r1, RentTracked(scope)), Is.True);
                Assert.That(scope.Queue.TryEnqueue(r1), Is.True);

                Assert.That(scope.Pump.TryStartNext(), Is.True);

                Assert.That(scope.Pool.IsCreated, Is.True);
                Assert.That(scope.Dispatcher.IsCreated, Is.True);
                Assert.That(scope.BufferPool.IsCreated, Is.True);
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TypeShape_SealedNonDisposableNonMonoBehaviour()
        {
            Type type = typeof(CaptureFrameRenderTargetReadbackPump);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void GpuIntegration_StartCompleteMatchReleaseRemoveReturn()
        {
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11u, 12);
            CaptureFrameRequest request = new CaptureFrameRequest(context, profile.Source, profile.Eye, profile.ImageRect, profile.ArrayIndex, profile.PixelFormat);

            CaptureFrameRequestQueue queue = new CaptureFrameRequestQueue(1);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(1, request.RequiredByteCount);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(queue, dispatcher, registry, pool);

            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;
            CaptureFrameReadbackResult result = default;
            bool resultHeld = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                Assert.That(registry.TryRegister(request, lease), Is.True);
                registered = true;
                leaseHeld = false;

                RenderTexture rt = pool.GetRenderTexture(lease);
                Assert.That(rt, Is.Not.Null);
                Assert.That(rt.IsCreated(), Is.True);

                FillSolidColor(rt, new Color32(255, 0, 0, 255));

                Assert.That(queue.TryEnqueue(request), Is.True);

                Assert.That(pump.TryStartNext(), Is.True);
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(pump.ActiveReadbackCount, Is.EqualTo(1));
                Assert.That(pool.RentedCount, Is.EqualTo(1));
                Assert.That(registry.Count, Is.EqualTo(1));

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out result), Is.True);
                resultHeld = true;
                Assert.That(result.HasError, Is.False);

                Assert.That(RequestsIdentical(result.FrameRequest, request), Is.True);

                NativeArray<byte> buffer = dispatcher.GetBuffer(result);
                Assert.That(buffer.Length, Is.EqualTo(request.RequiredByteCount));
                Assert.That(buffer[0], Is.EqualTo((byte)255));
                Assert.That(buffer[1], Is.EqualTo((byte)0));
                Assert.That(buffer[2], Is.EqualTo((byte)0));
                Assert.That(buffer[3], Is.EqualTo((byte)255));

                int last = buffer.Length - 4;
                Assert.That(buffer[last], Is.EqualTo((byte)255));
                Assert.That(buffer[last + 1], Is.EqualTo((byte)0));
                Assert.That(buffer[last + 2], Is.EqualTo((byte)0));
                Assert.That(buffer[last + 3], Is.EqualTo((byte)255));

                dispatcher.Release(result);
                resultHeld = false;

                Assert.That(registry.TryRemove(request, out CaptureFrameRenderTargetLease removed), Is.True);
                registered = false;
                lease = removed;
                leaseHeld = true;

                pool.Return(lease);
                leaseHeld = false;

                Assert.That(registry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            bool gpuSafe = true;

            if (resultHeld)
            {
                resultHeld = false;
                try
                {
                    dispatcher.Release(result);
                }
                catch (Exception ex)
                {
                    gpuSafe = false;
                    errors = AppendCleanupException(errors, ex);
                }
            }

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

            if (gpuSafe && registered)
            {
                registered = false;
                try
                {
                    if (registry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
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

            try { pool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            try
            {
                if (dispatcher.IsCreated)
                {
                    dispatcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (bufferPool.IsCreated)
                {
                    bufferPool.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
