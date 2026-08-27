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
    public class CaptureFrameRenderTargetCopyPumpTests
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

        private sealed class CopyPumpScope
        {
            public CaptureFrameRequestQueue Queue;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry Registry;
            public CaptureFrameRenderTargetCopyPump CopyPump;
            public CaptureFrameReadbackBufferPool BufferPool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureFrameRenderTargetReadbackPump ReadbackPump;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();
            public readonly List<RenderTexture> Sources = new List<RenderTexture>();
        }

        private static CaptureFrameProfile MakeProfile(int x, int y, int width, int height)
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(x, y, width, height));
        }

        private static CaptureFrameProfile MakeProfile()
        {
            return MakeProfile(0, 0, 2, 2);
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

        private static CaptureFrameRequest MakeRequest(long captureFrameId, int x, int y, int width, int height)
        {
            return new CaptureFrameRequest(
                MakeContext(captureFrameId),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(x, y, width, height),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            return MakeRequest(captureFrameId, 0, 0, 2, 2);
        }

        private static CaptureFrameRequest MakeRequestDifferent(long captureFrameId)
        {
            return new CaptureFrameRequest(
                MakeContext(captureFrameId),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Right,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CopyPumpScope NewScope(
            CaptureFrameProfile profile,
            int queueCapacity,
            int poolCapacity,
            int registryCapacity,
            int bufferSlotCount)
        {
            CopyPumpScope scope = new CopyPumpScope();
            scope.Queue = new CaptureFrameRequestQueue(queueCapacity);
            scope.Pool = new CaptureFrameRenderTargetPool(poolCapacity, profile);
            scope.Registry = new CaptureFrameRenderTargetLeaseRegistry(registryCapacity, scope.Pool);
            scope.CopyPump = new CaptureFrameRenderTargetCopyPump(scope.Queue, scope.Registry, scope.Pool);
            scope.BufferPool = new CaptureFrameReadbackBufferPool(bufferSlotCount, BufferBytesPerSlot);
            scope.Dispatcher = new UnityRenderTextureReadbackDispatcher(scope.BufferPool);
            scope.ReadbackPump = new CaptureFrameRenderTargetReadbackPump(scope.Queue, scope.Dispatcher, scope.Registry, scope.Pool);
            return scope;
        }

        private static RenderTexture CreateSourceTracked(
            CopyPumpScope scope,
            int width,
            int height,
            RenderTextureFormat format = RenderTextureFormat.ARGB32,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.sRGB)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, format, readWrite);
            scope.Sources.Add(rt);
            return rt;
        }

        private static void DestroyTexture(RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            if (rt.IsCreated())
            {
                rt.Release();
            }

            UnityEngine.Object.DestroyImmediate(rt);
        }

        private static void FillSolidColor(RenderTexture rt, Color32 color)
        {
            Color32[] pixels = new Color32[rt.width * rt.height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            FillPixels(rt, pixels);
        }

        private static void FillPixels(RenderTexture rt, Color32[] pixels)
        {
            Texture2D temp = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                temp.SetPixels32(pixels);
                temp.Apply();
                Graphics.CopyTexture(temp, rt);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static byte[] ReadBackTarget(RenderTexture rt, int width, int height)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            AsyncGPUReadback.WaitAllRequests();
            Assert.That(request.hasError, Is.False);

            NativeArray<byte> data = request.GetData<byte>();
            Assert.That(data.Length, Is.EqualTo(width * height * 4));

            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = data[i];
            }

            return result;
        }

        private static CaptureFrameRenderTargetLease RentTracked(CopyPumpScope scope)
        {
            Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
            scope.Held.Add(lease);
            return lease;
        }

        private static bool RegisterTracked(CopyPumpScope scope, CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
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

        private static Exception[] CleanupScope(CopyPumpScope scope)
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
                for (int i = scope.Sources.Count - 1; i >= 0; i--)
                {
                    RenderTexture source = scope.Sources[i];
                    scope.Sources.RemoveAt(i);
                    try
                    {
                        DestroyTexture(source);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

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

        private static void RunCopyBody(CopyPumpScope scope, Action body)
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

            Exception[] errors = CleanupScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        private static void RunCopyWithSource(CopyPumpScope scope, int width, int height, Action<RenderTexture> body)
        {
            ExceptionDispatchInfo bodyException = null;
            try
            {
                RenderTexture source = CreateSourceTracked(scope, width, height);
                source.Create();
                body(source);
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] errors = CleanupScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyBody(scope, () =>
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetCopyPump(null, scope.Registry, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetCopyPump(scope.Queue, null, scope.Pool));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetCopyPump(scope.Queue, scope.Registry, null));
            });
        }

        [Test]
        public void TryCopyNext_EmptyQueue_False_NoSourceValidation()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyBody(scope, () =>
            {
                Assert.That(scope.CopyPump.TryCopyNext(null), Is.False);

                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Registry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryCopyNext_UnregisteredLease_Throws_KeepsQueue()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.CopyPump.TryCopyNext(s));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryCopyNext_RequestMismatch_KeepsRegistryException()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                CaptureFrameRequest queued = MakeRequest(1);
                CaptureFrameRequest registered = MakeRequestDifferent(1);

                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, registered, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(queued), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.CopyPump.TryCopyNext(s));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryCopyNext_StaleLease_KeepsPoolException()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease;
                Assert.That(scope.Pool.TryRent(out lease), Is.True);
                Assert.That(scope.Registry.TryRegister(request, lease), Is.True);

                // Return the slot so the registered lease becomes stale.
                scope.Pool.Return(lease);

                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.CopyPump.TryCopyNext(s));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));

                // Remove the stale entry; the slot is already free, so it is not returned again.
                Assert.That(scope.Registry.TryRemove(request, out _), Is.True);
            });
        }

        [Test]
        public void TryCopyNext_InvalidSource_Rejected_StateUnchanged()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 4, 4, 4, 2);
            RunCopyBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);
                RenderTexture target = scope.Pool.GetRenderTexture(lease);

                // null
                Assert.Throws<ArgumentNullException>(() => scope.CopyPump.TryCopyNext(null));

                // uncreated
                AssertSourceRejected(scope, CreateSourceTracked(scope, 2, 2), typeof(ArgumentException));

                // cube
                RenderTexture cube = CreateSourceTracked(scope, 2, 2);
                cube.dimension = TextureDimension.Cube;
                cube.volumeDepth = 6;
                cube.Create();
                AssertSourceRejected(scope, cube, typeof(ArgumentException));

                // texture array
                RenderTexture array = CreateSourceTracked(scope, 2, 2);
                array.dimension = TextureDimension.Tex2DArray;
                array.volumeDepth = 2;
                array.Create();
                AssertSourceRejected(scope, array, typeof(ArgumentException));

                // MSAA
                RenderTexture msaa = CreateSourceTracked(scope, 2, 2);
                msaa.antiAliasing = 4;
                msaa.Create();
                AssertSourceRejected(scope, msaa, typeof(ArgumentException));

                // too small
                RenderTexture small = CreateSourceTracked(scope, 1, 1);
                small.Create();
                AssertSourceRejected(scope, small, typeof(ArgumentException));

                // format mismatch
                RenderTexture format = CreateSourceTracked(scope, 2, 2, RenderTextureFormat.RGB565);
                format.Create();
                AssertSourceRejected(scope, format, typeof(ArgumentException));

                // target itself (owned by the pool; not destroyed)
                Assert.Throws<ArgumentException>(() => scope.CopyPump.TryCopyNext(target));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        private static void AssertSourceRejected(CopyPumpScope scope, RenderTexture source, Type exceptionType)
        {
            Assert.Throws(exceptionType, () => scope.CopyPump.TryCopyNext(source));

            Assert.That(scope.Queue.Count, Is.EqualTo(1));
            Assert.That(scope.Registry.Count, Is.EqualTo(1));
            Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
        }

        [Test]
        public void TryCopyNext_Success_KeepsQueueRegistryAndRent()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void TryCopyNext_TargetTooSmall_FailClosed_StateUnchanged()
        {
            CopyPumpScope scope = NewScope(MakeProfile(0, 0, 2, 2), 4, 4, 4, 2);
            RunCopyBody(scope, () =>
            {
                // A request rectangle larger than the 2x2 pool target.
                CaptureFrameRequest large = MakeRequest(1, 0, 0, 4, 4);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, large, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(large), Is.True);

                RenderTexture source = CreateSourceTracked(scope, 4, 4);
                source.Create();
                Assert.Throws<InvalidOperationException>(() => scope.CopyPump.TryCopyNext(source));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryCopyNext_TargetOffsetBeyond_FailClosed_StateUnchanged()
        {
            CopyPumpScope scope = NewScope(MakeProfile(0, 0, 2, 2), 4, 4, 4, 2);
            RunCopyBody(scope, () =>
            {
                // An offset rectangle exceeding the 2x2 pool target bounds.
                CaptureFrameRequest offset = MakeRequest(1, 1, 1, 2, 2);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, offset, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(offset), Is.True);

                RenderTexture source = CreateSourceTracked(scope, 3, 3);
                source.Create();
                Assert.Throws<InvalidOperationException>(() => scope.CopyPump.TryCopyNext(source));

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryCopyNext_CopiesOnlyFifoHead()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                Color32 sentinel = new Color32(1, 2, 3, 255);
                Color32 copied = new Color32(200, 201, 202, 255);

                CaptureFrameRequest request1 = MakeRequest(1);
                CaptureFrameRequest request2 = MakeRequest(2);

                CaptureFrameRenderTargetLease lease1 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request1, lease1), Is.True);
                CaptureFrameRenderTargetLease lease2 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request2, lease2), Is.True);

                FillSolidColor(scope.Pool.GetRenderTexture(lease1), sentinel);
                FillSolidColor(scope.Pool.GetRenderTexture(lease2), sentinel);
                FillSolidColor(s, copied);

                Assert.That(scope.Queue.TryEnqueue(request1), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request2), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);
                Assert.That(scope.Queue.Count, Is.EqualTo(2));

                byte[] head = ReadBackTarget(scope.Pool.GetRenderTexture(lease1), 2, 2);
                for (int i = 0; i < head.Length; i += 4)
                {
                    Assert.That(head[i], Is.EqualTo(copied.r));
                    Assert.That(head[i + 1], Is.EqualTo(copied.g));
                    Assert.That(head[i + 2], Is.EqualTo(copied.b));
                    Assert.That(head[i + 3], Is.EqualTo(copied.a));
                }

                byte[] second = ReadBackTarget(scope.Pool.GetRenderTexture(lease2), 2, 2);
                for (int i = 0; i < second.Length; i += 4)
                {
                    Assert.That(second[i], Is.EqualTo(sentinel.r));
                    Assert.That(second[i + 1], Is.EqualTo(sentinel.g));
                    Assert.That(second[i + 2], Is.EqualTo(sentinel.b));
                    Assert.That(second[i + 3], Is.EqualTo(sentinel.a));
                }
            });
        }

        [Test]
        public void TryCopyNext_ThenReadbackPump_StartsSameRequest()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);
                Assert.That(scope.Queue.Count, Is.EqualTo(1));

                Assert.That(scope.ReadbackPump.TryStartNext(), Is.True);
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryCopyNext_AfterReadbackStarted_False()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);
                Assert.That(scope.ReadbackPump.TryStartNext(), Is.True);
                Assert.That(scope.Queue.Count, Is.EqualTo(0));

                Assert.That(scope.CopyPump.TryCopyNext(null), Is.False);
            });
        }

        [Test]
        public void GpuIntegration_KnownColor_RawRgbaMatches()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                Color32 color = new Color32(12, 34, 56, 255);
                FillSolidColor(s, color);

                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);
                Assert.That(scope.ReadbackPump.TryStartNext(), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(scope.Dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                try
                {
                    NativeArray<byte> buffer = scope.Dispatcher.GetBuffer(result);
                    Assert.That(buffer.Length, Is.EqualTo(request.RequiredByteCount));
                    for (int i = 0; i < buffer.Length; i += 4)
                    {
                        Assert.That(buffer[i], Is.EqualTo(color.r));
                        Assert.That(buffer[i + 1], Is.EqualTo(color.g));
                        Assert.That(buffer[i + 2], Is.EqualTo(color.b));
                        Assert.That(buffer[i + 3], Is.EqualTo(color.a));
                    }
                }
                finally
                {
                    scope.Dispatcher.Release(result);
                }
            });
        }

        [Test]
        public void GpuIntegration_NonZeroRect_OnlyRegionCopied()
        {
            CopyPumpScope scope = NewScope(MakeProfile(1, 1, 2, 2), 2, 2, 2, 2);
            RunCopyWithSource(scope, 3, 3, (s) =>
            {
                Color32 regionColor = new Color32(200, 10, 10, 255);
                Color32 otherColor = new Color32(10, 200, 10, 255);

                Color32[] pixels = new Color32[9];
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        bool inRegion = x >= 1 && x <= 2 && y >= 1 && y <= 2;
                        pixels[y * 3 + x] = inRegion ? regionColor : otherColor;
                    }
                }

                FillPixels(s, pixels);

                CaptureFrameRequest request = MakeRequest(1, 1, 1, 2, 2);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);
                Assert.That(scope.ReadbackPump.TryStartNext(), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(scope.Dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                try
                {
                    NativeArray<byte> buffer = scope.Dispatcher.GetBuffer(result);
                    Assert.That(buffer.Length, Is.EqualTo(request.RequiredByteCount));
                    for (int i = 0; i < buffer.Length; i += 4)
                    {
                        Assert.That(buffer[i], Is.EqualTo(regionColor.r));
                        Assert.That(buffer[i + 1], Is.EqualTo(regionColor.g));
                        Assert.That(buffer[i + 2], Is.EqualTo(regionColor.b));
                        Assert.That(buffer[i + 3], Is.EqualTo(regionColor.a));
                    }
                }
                finally
                {
                    scope.Dispatcher.Release(result);
                }
            });
        }

        [Test]
        public void GpuIntegration_SourcePixelsUnchanged()
        {
            CopyPumpScope scope = NewScope(MakeProfile(), 2, 2, 2, 2);
            RunCopyWithSource(scope, 2, 2, (s) =>
            {
                Color32 color = new Color32(90, 80, 70, 255);
                FillSolidColor(s, color);

                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Queue.TryEnqueue(request), Is.True);

                Assert.That(scope.CopyPump.TryCopyNext(s), Is.True);

                byte[] bytes = ReadBackTarget(s, 2, 2);
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    Assert.That(bytes[i], Is.EqualTo(color.r));
                    Assert.That(bytes[i + 1], Is.EqualTo(color.g));
                    Assert.That(bytes[i + 2], Is.EqualTo(color.b));
                    Assert.That(bytes[i + 3], Is.EqualTo(color.a));
                }
            });
        }

        [Test]
        public void TypeShape_SealedNonDisposableNonMonoBehaviour()
        {
            Type type = typeof(CaptureFrameRenderTargetCopyPump);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
        }
    }
}
