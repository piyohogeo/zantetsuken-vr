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
    public class CaptureFrameRenderTargetPoolTests
    {
        private static CaptureFrameProfile MakeProfile(int profileId = 1, int x = 0, int y = 0, int width = 2, int height = 2)
        {
            return new CaptureFrameProfile(profileId, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(x, y, width, height), 0, CapturePixelFormat.Rgba32);
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

        private static Exception[] ConcatExceptions(Exception[] first, Exception[] second)
        {
            if (first == null || first.Length == 0)
            {
                return second ?? new Exception[0];
            }

            if (second == null || second.Length == 0)
            {
                return first;
            }

            Exception[] combined = new Exception[first.Length + second.Length];
            Array.Copy(first, combined, first.Length);
            Array.Copy(second, 0, combined, first.Length, second.Length);
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

        private static void ReturnAndUntrack(
            CaptureFrameRenderTargetPool pool,
            List<CaptureFrameRenderTargetLease> outstanding,
            CaptureFrameRenderTargetLease lease)
        {
            pool.Return(lease);

            for (int i = outstanding.Count - 1; i >= 0; i--)
            {
                if (outstanding[i].SlotIndex == lease.SlotIndex)
                {
                    outstanding.RemoveAt(i);
                    return;
                }
            }
        }

        private static Exception[] CleanupPool(
            CaptureFrameRenderTargetPool pool,
            List<CaptureFrameRenderTargetLease> outstanding)
        {
            Exception[] errors = null;

            for (int i = outstanding.Count - 1; i >= 0; i--)
            {
                try
                {
                    pool.Return(outstanding[i]);
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            outstanding.Clear();

            try
            {
                pool.Dispose();
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            return errors;
        }

        private static void RunPoolBody(
            CaptureFrameRenderTargetPool pool,
            List<CaptureFrameRenderTargetLease> outstanding,
            Action body)
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

            Exception[] errors = CleanupPool(pool, outstanding);
            ThrowCleanupAndBody(bodyException, errors);
        }

        [Test]
        public void Constructor_CapacityZeroAndNegative_Rejected()
        {
            CaptureFrameProfile profile = MakeProfile();

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRenderTargetPool(0, profile));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRenderTargetPool(-1, profile));
        }

        [Test]
        public void Constructor_NullProfile_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetPool(1, null));
        }

        [Test]
        public void Constructor_NonUnityRenderTextureSource_Rejected()
        {
            CaptureFrameProfile profile = new CaptureFrameProfile(1, 45.0, CaptureSource.OpenXRProjection, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);

            Assert.Throws<ArgumentException>(() => new CaptureFrameRenderTargetPool(1, profile));
        }

        [Test]
        public void Constructor_NonZeroArrayIndex_Rejected()
        {
            CaptureFrameProfile profile = new CaptureFrameProfile(1, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 1, CapturePixelFormat.Rgba32);

            Assert.Throws<ArgumentException>(() => new CaptureFrameRenderTargetPool(1, profile));
        }

        [Test]
        public void RenderTexture_SettingsReflectProfile()
        {
            CaptureFrameProfile profile = MakeProfile();
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);
                RenderTexture rt = pool.GetRenderTexture(lease);

                Assert.That(rt.width, Is.EqualTo(profile.ImageRect.X + profile.ImageRect.Width));
                Assert.That(rt.height, Is.EqualTo(profile.ImageRect.Y + profile.ImageRect.Height));
                Assert.That(rt.format, Is.EqualTo(RenderTextureFormat.ARGB32));
                Assert.That(rt.sRGB, Is.True);
                Assert.That(rt.dimension, Is.EqualTo(TextureDimension.Tex2D));
                Assert.That(rt.volumeDepth, Is.EqualTo(1));
                Assert.That(rt.antiAliasing, Is.EqualTo(1));
                Assert.That(rt.useMipMap, Is.False);
                Assert.That(rt.enableRandomWrite, Is.False);
                Assert.That(rt.IsCreated(), Is.True);

                ReturnAndUntrack(pool, outstanding, lease);
            });
        }

        [Test]
        public void NonZeroOrigin_ProducesFullRectSize()
        {
            CaptureFrameProfile profile = MakeProfile(x: 10, y: 20, width: 8, height: 6);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);
                RenderTexture rt = pool.GetRenderTexture(lease);

                Assert.That(rt.width, Is.EqualTo(18));
                Assert.That(rt.height, Is.EqualTo(26));

                ReturnAndUntrack(pool, outstanding, lease);
            });
        }

        [Test]
        public void RentUpToCapacity_FullReturnsFalseDefault()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(3, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease l0), Is.True);
                outstanding.Add(l0);
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease l1), Is.True);
                outstanding.Add(l1);
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease l2), Is.True);
                outstanding.Add(l2);
                Assert.That(pool.RentedCount, Is.EqualTo(3));

                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease l3), Is.False);
                Assert.That(l3.IsValid, Is.False);
                Assert.That(pool.RentedCount, Is.EqualTo(3));

                ReturnAndUntrack(pool, outstanding, l0);
                ReturnAndUntrack(pool, outstanding, l1);
                ReturnAndUntrack(pool, outstanding, l2);
            });
        }

        [Test]
        public void ReturnThenReRent()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease first), Is.True);
                outstanding.Add(first);
                ReturnAndUntrack(pool, outstanding, first);

                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease second), Is.True);
                outstanding.Add(second);
                Assert.That(second.SlotIndex, Is.EqualTo(first.SlotIndex));

                ReturnAndUntrack(pool, outstanding, second);
            });
        }

        [Test]
        public void ReRent_OldLeaseStale()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease first), Is.True);
                outstanding.Add(first);
                ReturnAndUntrack(pool, outstanding, first);
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease second), Is.True);
                outstanding.Add(second);

                Assert.Throws<InvalidOperationException>(() => pool.GetRenderTexture(first));
                Assert.Throws<InvalidOperationException>(() => pool.Return(first));

                ReturnAndUntrack(pool, outstanding, second);
            });
        }

        [Test]
        public void LeaseCopy_ReturnOneCopy_OtherCopyRejected()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);
                CaptureFrameRenderTargetLease copy = lease;

                ReturnAndUntrack(pool, outstanding, copy);

                Assert.Throws<InvalidOperationException>(() => pool.GetRenderTexture(lease));
                Assert.Throws<InvalidOperationException>(() => pool.Return(lease));
            });
        }

        [Test]
        public void DoubleReturn_Rejected()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);
                ReturnAndUntrack(pool, outstanding, lease);

                Assert.Throws<InvalidOperationException>(() => pool.Return(lease));
            });
        }

        [Test]
        public void DefaultLease_Rejected()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.Throws<InvalidOperationException>(() => pool.GetRenderTexture(default));
                Assert.Throws<InvalidOperationException>(() => pool.Return(default));
            });
        }

        [Test]
        public void ForeignPoolLease_Rejected()
        {
            CaptureFrameRenderTargetPool pool1 = new CaptureFrameRenderTargetPool(1, MakeProfile(profileId: 1));
            CaptureFrameRenderTargetPool pool2 = new CaptureFrameRenderTargetPool(1, MakeProfile(profileId: 2));
            List<CaptureFrameRenderTargetLease> outstanding1 = new List<CaptureFrameRenderTargetLease>();
            List<CaptureFrameRenderTargetLease> outstanding2 = new List<CaptureFrameRenderTargetLease>();

            ExceptionDispatchInfo bodyException = null;
            try
            {
                Assert.That(pool1.TryRent(out CaptureFrameRenderTargetLease lease1), Is.True);
                outstanding1.Add(lease1);
                Assert.That(pool2.TryRent(out CaptureFrameRenderTargetLease lease2), Is.True);
                outstanding2.Add(lease2);

                Assert.Throws<InvalidOperationException>(() => pool2.GetRenderTexture(lease1));
                Assert.Throws<InvalidOperationException>(() => pool2.Return(lease1));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] cleanupErrors = ConcatExceptions(
                CleanupPool(pool1, outstanding1),
                CleanupPool(pool2, outstanding2));

            ThrowCleanupAndBody(bodyException, cleanupErrors);
        }

        [Test]
        public void GetRenderTexture_SameSlot_SameInstance()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);
                RenderTexture rt1 = pool.GetRenderTexture(lease);
                RenderTexture rt2 = pool.GetRenderTexture(lease);

                Assert.That(ReferenceEquals(rt1, rt2), Is.True);

                ReturnAndUntrack(pool, outstanding, lease);
            });
        }

        [Test]
        public void DisposeWithRentedLease_RejectedAndStatePreserved()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);

                Assert.Throws<InvalidOperationException>(() => pool.Dispose());
                Assert.That(pool.IsCreated, Is.True);

                Assert.That(pool.GetRenderTexture(lease), Is.Not.Null);

                ReturnAndUntrack(pool, outstanding, lease);
                pool.Dispose();
                Assert.That(pool.IsCreated, Is.False);
            });
        }

        [Test]
        public void DisposeAfterAllReturned_MultipleDisposeSafe()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                outstanding.Add(lease);
                ReturnAndUntrack(pool, outstanding, lease);

                pool.Dispose();
                Assert.That(pool.IsCreated, Is.False);

                pool.Dispose();
                pool.Dispose();
            });
        }

        [Test]
        public void DisposedPool_AllApiThrowObjectDisposed()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                pool.Dispose();

                Assert.That(pool.IsCreated, Is.False);

                Assert.Throws<ObjectDisposedException>(() => pool.TryRent(out _));
                Assert.Throws<ObjectDisposedException>(() => pool.GetRenderTexture(default));
                Assert.Throws<ObjectDisposedException>(() => pool.Return(default));
                Assert.Throws<ObjectDisposedException>(() => { int c = pool.Capacity; });
                Assert.Throws<ObjectDisposedException>(() => { int c = pool.RentedCount; });
                Assert.Throws<ObjectDisposedException>(() => { CaptureFrameProfile p = pool.Profile; });
            });
        }

        [Test]
        public void PoolsIndependent()
        {
            CaptureFrameRenderTargetPool pool1 = new CaptureFrameRenderTargetPool(1, MakeProfile(profileId: 1));
            CaptureFrameRenderTargetPool pool2 = new CaptureFrameRenderTargetPool(1, MakeProfile(profileId: 2));
            List<CaptureFrameRenderTargetLease> outstanding1 = new List<CaptureFrameRenderTargetLease>();
            List<CaptureFrameRenderTargetLease> outstanding2 = new List<CaptureFrameRenderTargetLease>();

            ExceptionDispatchInfo bodyException = null;
            try
            {
                Assert.That(pool1.TryRent(out CaptureFrameRenderTargetLease lease1), Is.True);
                outstanding1.Add(lease1);
                Assert.That(pool1.RentedCount, Is.EqualTo(1));
                Assert.That(pool2.RentedCount, Is.EqualTo(0));

                ReturnAndUntrack(pool1, outstanding1, lease1);
                pool1.Dispose();

                Assert.That(pool2.IsCreated, Is.True);
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] cleanupErrors = ConcatExceptions(
                CleanupPool(pool1, outstanding1),
                CleanupPool(pool2, outstanding2));

            ThrowCleanupAndBody(bodyException, cleanupErrors);
        }

        [Test]
        public void ProfileNotOwnedOrMutated()
        {
            CaptureFrameProfile profile = MakeProfile(profileId: 7);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            List<CaptureFrameRenderTargetLease> outstanding = new List<CaptureFrameRenderTargetLease>();

            RunPoolBody(pool, outstanding, () =>
            {
                Assert.That(pool.Profile, Is.SameAs(profile));
            });

            Assert.That(profile.ProfileId, Is.EqualTo(7));
            Assert.That(profile.CreateCadenceSelector(), Is.Not.Null);
        }

        [Test]
        public void Lease_IsValueTypeWithValueTypeFields()
        {
            Type type = typeof(CaptureFrameRenderTargetLease);

            Assert.That(type.IsValueType, Is.True);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Lease field must be a value type: " + field.Name);
            }

            Assert.That(default(CaptureFrameRenderTargetLease).IsValid, Is.False);
        }

        [Test]
        public void Lease_OwnerIdentityUsesGuid()
        {
            FieldInfo ownerField = typeof(CaptureFrameRenderTargetLease).GetField("_ownerToken", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(ownerField, Is.Not.Null);
            Assert.That(ownerField.FieldType, Is.EqualTo(typeof(Guid)));
        }

        [Test]
        public void Pool_SealedIDisposableFixedArray()
        {
            Type type = typeof(CaptureFrameRenderTargetPool);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Type fieldType = field.FieldType;
                if (fieldType.IsGenericType)
                {
                    Type definition = fieldType.GetGenericTypeDefinition();
                    Assert.That(definition, Is.Not.EqualTo(typeof(List<>)), "Field must not use a resizable collection: " + field.Name);
                    Assert.That(definition, Is.Not.EqualTo(typeof(Dictionary<,>)), "Field must not use a resizable collection: " + field.Name);
                    Assert.That(definition, Is.Not.EqualTo(typeof(HashSet<>)), "Field must not use a resizable collection: " + field.Name);
                }
            }
        }

        [Test]
        public void GpuIntegration_RentDrawReadbackReleaseReturnDispose()
        {
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11u, 12);
            CaptureFrameRequest request = new CaptureFrameRequest(context, profile.Source, profile.Eye, profile.ImageRect, profile.ArrayIndex, profile.PixelFormat);

            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            CaptureFrameReadbackBufferPool readbackPool = new CaptureFrameReadbackBufferPool(1, request.RequiredByteCount);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(readbackPool);

            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            CaptureFrameReadbackResult result = default;
            bool resultHeld = false;
            NativeArray<byte> png = default;
            bool pngHeld = false;

            ExceptionDispatchInfo bodyException = null;
            Exception[] cleanupErrors = null;

            try
            {
                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                RenderTexture rt = pool.GetRenderTexture(lease);
                Assert.That(rt, Is.Not.Null);
                Assert.That(rt.IsCreated(), Is.True);

                FillSolidColor(rt, new Color32(255, 0, 0, 255));

                Assert.That(dispatcher.TryStart(request, rt), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out result), Is.True);
                resultHeld = true;
                Assert.That(result.HasError, Is.False);
                Assert.That(result.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(request.TraceContext.CaptureFrameId));

                NativeArray<byte> buffer = dispatcher.GetBuffer(result);
                png = CaptureFramePngEncoder.Encode(buffer, request.PixelLayout);
                pngHeld = true;
                Assert.That(png.Length, Is.GreaterThan(0));
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            if (pngHeld)
            {
                pngHeld = false;
                try
                {
                    png.Dispose();
                }
                catch (Exception ex)
                {
                    cleanupErrors = AppendCleanupException(cleanupErrors, ex);
                }
            }

            if (resultHeld)
            {
                resultHeld = false;
                try
                {
                    dispatcher.Release(result);
                }
                catch (Exception ex)
                {
                    cleanupErrors = AppendCleanupException(cleanupErrors, ex);
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
                cleanupErrors = AppendCleanupException(cleanupErrors, ex);
            }

            if (leaseHeld)
            {
                leaseHeld = false;
                try
                {
                    pool.Return(lease);
                }
                catch (Exception ex)
                {
                    cleanupErrors = AppendCleanupException(cleanupErrors, ex);
                }
            }

            try
            {
                pool.Dispose();
            }
            catch (Exception ex)
            {
                cleanupErrors = AppendCleanupException(cleanupErrors, ex);
            }

            try
            {
                if (dispatcher.IsCreated)
                {
                    dispatcher.Dispose();
                }
            }
            catch (Exception ex)
            {
                cleanupErrors = AppendCleanupException(cleanupErrors, ex);
            }

            try
            {
                if (readbackPool.IsCreated)
                {
                    readbackPool.Dispose();
                }
            }
            catch (Exception ex)
            {
                cleanupErrors = AppendCleanupException(cleanupErrors, ex);
            }

            ThrowCleanupAndBody(bodyException, cleanupErrors);
        }
    }
}
