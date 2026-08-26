using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class UnityRenderTextureReadbackDispatcherTests
    {
        private static CaptureFrameRequest MakeRequest(int width, int height, int arrayIndex = 0)
        {
            return MakeRequest(width, height, CaptureSource.UnityRenderTexture, arrayIndex);
        }

        private static CaptureFrameRequest MakeRequest(int width, int height, CaptureSource source, int arrayIndex = 0)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
                source,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, width, height),
                arrayIndex,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRequest MakeRequestWithRect(int x, int y, int width, int height)
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(x, y, width, height),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static RenderTexture CreateTex2D(int width, int height)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            rt.Create();
            return rt;
        }

        private static RenderTexture CreateTex2DArray(int width, int height, int depth)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            rt.dimension = TextureDimension.Tex2DArray;
            rt.volumeDepth = depth;
            rt.Create();
            return rt;
        }

        private static RenderTexture CreateCube(int size)
        {
            RenderTexture rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
            rt.dimension = TextureDimension.Cube;
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

        private static Guid GetOwnerToken(CaptureFrameReadbackResult result)
        {
            PropertyInfo property = typeof(CaptureFrameReadbackResult).GetProperty("OwnerToken", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(property, Is.Not.Null);
            return (Guid)property.GetValue(result);
        }

        private static CaptureFrameReadbackResult ForgeResult(Guid token, long operationId, int slotIndex, bool hasError, CaptureFrameRequest request)
        {
            ConstructorInfo ctor = typeof(CaptureFrameReadbackResult).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(Guid), typeof(long), typeof(CaptureFrameRequest), typeof(int), typeof(bool) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameReadbackResult)ctor.Invoke(new object[] { token, operationId, request, slotIndex, hasError });
        }

        private static void SetStoredHasError(UnityRenderTextureReadbackDispatcher dispatcher, long operationId, bool value)
        {
            FieldInfo idsField = typeof(UnityRenderTextureReadbackDispatcher).GetField("_operationIds", BindingFlags.NonPublic | BindingFlags.Instance);
            long[] ids = (long[])idsField.GetValue(dispatcher);
            int index = Array.IndexOf(ids, operationId);
            Assert.That(index, Is.GreaterThanOrEqualTo(0));

            FieldInfo hasErrorField = typeof(UnityRenderTextureReadbackDispatcher).GetField("_hasError", BindingFlags.NonPublic | BindingFlags.Instance);
            bool[] hasError = (bool[])hasErrorField.GetValue(dispatcher);
            hasError[index] = value;
        }

        [Test]
        public void Result_Default_Invalid_And_NoPublicConstructor()
        {
            CaptureFrameReadbackResult result = default;

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.OperationId, Is.EqualTo(0));
            Assert.That(typeof(CaptureFrameReadbackResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Result_HasNoReferenceFields()
        {
            Type type = typeof(CaptureFrameReadbackResult);

            Assert.That(type.IsValueType, Is.True);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Reference-type field: " + field.Name);
            }
        }

        [Test]
        public void TryStart_NullSource_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                Assert.Throws<ArgumentNullException>(() => dispatcher.TryStart(MakeRequest(2, 2), null));
                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_UncreatedSource_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32);
                try
                {
                    Assert.Throws<ArgumentException>(() => dispatcher.TryStart(MakeRequest(2, 2), rt));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_NonRenderTextureSource_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    CaptureFrameRequest request = MakeRequest(2, 2, CaptureSource.OpenXRProjection);
                    Assert.Throws<ArgumentException>(() => dispatcher.TryStart(request, rt));
                }
                finally
                {
                    DestroyTexture(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_UnsupportedDimension_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateCube(2);
                try
                {
                    Assert.Throws<ArgumentException>(() => dispatcher.TryStart(MakeRequest(2, 2), rt));
                }
                finally
                {
                    DestroyTexture(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_RectOutOfBounds_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(4, 4);
                try
                {
                    CaptureFrameRequest request = MakeRequestWithRect(2, 0, 4, 4);
                    Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.TryStart(request, rt));
                }
                finally
                {
                    DestroyTexture(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_Tex2D_NonZeroArrayIndex_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    CaptureFrameRequest request = MakeRequest(2, 2, 1);
                    Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.TryStart(request, rt));
                }
                finally
                {
                    DestroyTexture(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_Tex2DArray_IndexOutOfRange_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2DArray(2, 2, 2);
                try
                {
                    CaptureFrameRequest request = MakeRequest(2, 2, 2);
                    Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.TryStart(request, rt));
                }
                finally
                {
                    DestroyTexture(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_BufferTooSmall_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 32))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(4, 4);
                try
                {
                    Assert.Throws<InvalidOperationException>(() => dispatcher.TryStart(MakeRequest(4, 4), rt));
                }
                finally
                {
                    DestroyTexture(rt);
                }

                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void TryStart_PoolExhausted_ReturnsFalse()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.False);

                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r), Is.True);
                    dispatcher.Release(r);

                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r2), Is.True);
                    dispatcher.Release(r2);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void TryStart_TwoConcurrent_UniqueOperationIds()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(2));

                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r1), Is.True);
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r2), Is.True);

                    Assert.That(r1.OperationId, Is.GreaterThan(0));
                    Assert.That(r2.OperationId, Is.GreaterThan(0));
                    Assert.That(r1.OperationId, Is.Not.EqualTo(r2.OperationId));

                    dispatcher.Release(r1);
                    dispatcher.Release(r2);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void TryCollect_NoActiveOperations_ReturnsFalse()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.False);
                Assert.That(result.IsValid, Is.False);
                Assert.That(result.OperationId, Is.EqualTo(0));
            }
        }

        [Test]
        public void Readback_RenderTexture_ReturnsExpectedBytes()
        {
            const int width = 8;
            const int height = 4;
            const int bytes = width * height * 4;

            byte[] pixels = new byte[bytes];
            for (int i = 0; i < width * height; i++)
            {
                pixels[i * 4 + 0] = 10;
                pixels[i * 4 + 1] = 20;
                pixels[i * 4 + 2] = 30;
                pixels[i * 4 + 3] = 40;
            }

            Texture2D sourceTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            RenderTexture rt = null;
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, bytes);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);

            try
            {
                sourceTex.SetPixelData(pixels, 0);
                sourceTex.Apply(false, false);

                rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                rt.Create();
                Graphics.CopyTexture(sourceTex, rt);

                CaptureFrameRequest request = MakeRequest(width, height);
                Assert.That(request.RequiredByteCount, Is.EqualTo(bytes));

                Assert.That(dispatcher.TryStart(request, rt), Is.True);
                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.HasError, Is.False);

                NativeArray<byte> data = dispatcher.GetBuffer(result);
                Assert.That(data.Length, Is.EqualTo(bytes));

                for (int i = 0; i < bytes; i++)
                {
                    Assert.That(data[i], Is.EqualTo(pixels[i]), "Byte mismatch at index " + i);
                }

                dispatcher.Release(result);
            }
            finally
            {
                dispatcher.Dispose();
                pool.Dispose();
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }

                UnityEngine.Object.DestroyImmediate(sourceTex);
            }
        }

        [Test]
        public void Collect_KeepsSlotRented_UntilRelease()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);

                    Assert.That(pool.AvailableCount, Is.EqualTo(1));
                    Assert.That(pool.RentedCount, Is.EqualTo(1));

                    dispatcher.Release(result);

                    Assert.That(pool.AvailableCount, Is.EqualTo(2));
                    Assert.That(pool.RentedCount, Is.EqualTo(0));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void ErrorResult_GetBufferRejected_ReleaseAllowed()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult success), Is.True);

                    SetStoredHasError(dispatcher, success.OperationId, true);

                    CaptureFrameReadbackResult errorResult = ForgeResult(
                        GetOwnerToken(success), success.OperationId, success.BufferSlotIndex, true, success.FrameRequest);

                    Assert.Throws<InvalidOperationException>(() => dispatcher.GetBuffer(errorResult));
                    dispatcher.Release(errorResult);

                    Assert.Throws<InvalidOperationException>(() => dispatcher.Release(success));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void ForgedResult_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                CaptureFrameReadbackResult forged = ForgeResult(Guid.Empty, 12345, 0, false, MakeRequest(2, 2));

                Assert.Throws<InvalidOperationException>(() => dispatcher.GetBuffer(forged));
                Assert.Throws<InvalidOperationException>(() => dispatcher.Release(forged));
            }
        }

        [Test]
        public void Release_DoubleRelease_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r), Is.True);

                    dispatcher.Release(r);
                    Assert.Throws<InvalidOperationException>(() => dispatcher.Release(r));
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void ResultFromOtherDispatcher_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher d1 = new UnityRenderTextureReadbackDispatcher(pool))
            using (UnityRenderTextureReadbackDispatcher d2 = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(d1.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(d1.TryCollect(out CaptureFrameReadbackResult r), Is.True);

                    Assert.Throws<InvalidOperationException>(() => d2.GetBuffer(r));
                    Assert.Throws<InvalidOperationException>(() => d2.Release(r));

                    d1.Release(r);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void CrossDispatcher_StaleResult_DoesNotCollide()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher d1 = new UnityRenderTextureReadbackDispatcher(pool))
            using (UnityRenderTextureReadbackDispatcher d2 = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    // A completes and releases; its slot and operation id are recycled.
                    Assert.That(d1.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(d1.TryCollect(out CaptureFrameReadbackResult resultA), Is.True);
                    d1.Release(resultA);

                    // B starts and completes a new operation on the same pool slot.
                    Assert.That(d2.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(d2.TryCollect(out CaptureFrameReadbackResult resultB), Is.True);

                    Assert.That(resultA.OperationId, Is.EqualTo(resultB.OperationId));

                    // A's stale result must not access B's buffer.
                    Assert.Throws<InvalidOperationException>(() => d2.GetBuffer(resultA));
                    Assert.Throws<InvalidOperationException>(() => d2.Release(resultA));

                    // B's result must not be usable by A.
                    Assert.Throws<InvalidOperationException>(() => d1.GetBuffer(resultB));
                    Assert.Throws<InvalidOperationException>(() => d1.Release(resultB));

                    d2.Release(resultB);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void ForgedResult_WrongHasError_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult success), Is.True);

                    CaptureFrameReadbackResult forged = ForgeResult(
                        GetOwnerToken(success), success.OperationId, success.BufferSlotIndex, true, success.FrameRequest);

                    Assert.Throws<InvalidOperationException>(() => dispatcher.GetBuffer(forged));
                    Assert.Throws<InvalidOperationException>(() => dispatcher.Release(forged));

                    dispatcher.Release(success);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void StaleResult_AfterSlotReuse_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            using (UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool))
            {
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult oldResult), Is.True);
                    dispatcher.Release(oldResult);

                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);
                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult newResult), Is.True);

                    Assert.That(newResult.OperationId, Is.Not.EqualTo(oldResult.OperationId));
                    Assert.Throws<InvalidOperationException>(() => dispatcher.GetBuffer(oldResult));
                    Assert.Throws<InvalidOperationException>(() => dispatcher.Release(oldResult));

                    dispatcher.Release(newResult);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Dispose_WithActiveOperation_Rejected()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64))
            {
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                RenderTexture rt = CreateTex2D(2, 2);
                try
                {
                    Assert.That(dispatcher.TryStart(MakeRequest(2, 2), rt), Is.True);

                    Assert.Throws<InvalidOperationException>(() => dispatcher.Dispose());
                    Assert.That(dispatcher.IsCreated, Is.True);
                    Assert.That(dispatcher.ActiveCount, Is.EqualTo(1));

                    AsyncGPUReadback.WaitAllRequests();
                    Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult r), Is.True);
                    dispatcher.Release(r);

                    dispatcher.Dispose();
                    Assert.That(dispatcher.IsCreated, Is.False);
                }
                finally
                {
                    DestroyTexture(rt);
                }
            }
        }

        [Test]
        public void Dispose_MultipleTimesSafe()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            {
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                dispatcher.Dispose();

                Assert.DoesNotThrow(() => dispatcher.Dispose());
            }
        }

        [Test]
        public void Dispose_AllApiContract()
        {
            using (CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(1, 64))
            {
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                dispatcher.Dispose();

                Assert.That(dispatcher.IsCreated, Is.False);
                Assert.Throws<ObjectDisposedException>(() => dispatcher.TryStart(MakeRequest(2, 2), null));
                Assert.Throws<ObjectDisposedException>(() => dispatcher.TryCollect(out _));
                Assert.Throws<ObjectDisposedException>(() => dispatcher.GetBuffer(default));
                Assert.Throws<ObjectDisposedException>(() => dispatcher.Release(default));
                Assert.Throws<ObjectDisposedException>(() => { int _ = dispatcher.ActiveCount; });
                Assert.Throws<ObjectDisposedException>(() => { int _ = dispatcher.Capacity; });
            }
        }

        [Test]
        public void DispatcherDispose_KeepsPoolUsable()
        {
            CaptureFrameReadbackBufferPool pool = new CaptureFrameReadbackBufferPool(2, 64);
            try
            {
                UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(pool);
                dispatcher.Dispose();

                Assert.That(pool.IsCreated, Is.True);
                Assert.That(pool.AvailableCount, Is.EqualTo(2));
            }
            finally
            {
                pool.Dispose();
            }
        }
    }
}
