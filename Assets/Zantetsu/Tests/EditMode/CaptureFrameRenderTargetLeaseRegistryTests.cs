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
    public class CaptureFrameRenderTargetLeaseRegistryTests
    {
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

        private sealed class RegistryScope
        {
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry Registry;
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

        private static RegistryScope NewScope(int poolCapacity, int registryCapacity)
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(poolCapacity, MakeProfile());
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(registryCapacity, pool);
            return new RegistryScope { Pool = pool, Registry = registry };
        }

        private static RegistryScope NewScope(int capacity)
        {
            return NewScope(capacity, capacity);
        }

        private static CaptureFrameRenderTargetLease RentTracked(RegistryScope scope)
        {
            Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
            scope.Held.Add(lease);
            return lease;
        }

        private static bool RegisterTracked(RegistryScope scope, CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
        {
            bool result = scope.Registry.TryRegister(request, lease);
            if (result)
            {
                RemoveFromHeld(scope.Held, lease);
                scope.Registered.Add(new RegisteredEntry(request, lease));
            }

            return result;
        }

        private static void RemoveTracked(RegistryScope scope, CaptureFrameRequest request)
        {
            Assert.That(scope.Registry.TryRemove(request, out CaptureFrameRenderTargetLease lease), Is.True);
            RemoveFromRegistered(scope.Registered, request.TraceContext.CaptureFrameId);
            scope.Held.Add(lease);
        }

        private static void ReturnTracked(RegistryScope scope, CaptureFrameRenderTargetLease lease)
        {
            scope.Pool.Return(lease);
            RemoveFromHeld(scope.Held, lease);
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

        private static void RemoveFromRegistered(List<RegisteredEntry> registered, long captureFrameId)
        {
            for (int i = registered.Count - 1; i >= 0; i--)
            {
                if (registered[i].Request.TraceContext.CaptureFrameId == captureFrameId)
                {
                    registered.RemoveAt(i);
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

        private static Exception[] CleanupRegistryTest(RegistryScope scope)
        {
            Exception[] errors = null;

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

        private static void RunRegistryBody(RegistryScope scope, Action body)
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

            Exception[] errors = CleanupRegistryTest(scope);
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
        public void Constructor_NullPoolAndInvalidCapacity_Rejected()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRenderTargetLeaseRegistry(0, pool));
                Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRenderTargetLeaseRegistry(-1, pool));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetLeaseRegistry(1, null));
            }
            finally
            {
                pool.Dispose();
            }
        }

        [Test]
        public void CapacityCountAndCounters()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                Assert.That(scope.Registry.Capacity, Is.EqualTo(2));
                Assert.That(scope.Registry.Count, Is.EqualTo(0));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(0));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(0));

                Assert.That(RegisterTracked(scope, MakeRequest(1), RentTracked(scope)), Is.True);

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(0));
            });
        }

        [Test]
        public void RegisterGetRemove_RoundTripSameLease()
        {
            RegistryScope scope = NewScope(1);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(42);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);

                Assert.That(RegisterTracked(scope, request, lease), Is.True);

                Assert.That(scope.Registry.TryGet(request, out CaptureFrameRenderTargetLease fetched), Is.True);
                Assert.That(fetched.SlotIndex, Is.EqualTo(lease.SlotIndex));

                RemoveTracked(scope, request);

                Assert.That(scope.Held.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Remove_ThenCallerCanReturnToPool()
        {
            RegistryScope scope = NewScope(1);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);

                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                RemoveTracked(scope, request);

                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                ReturnTracked(scope, lease);

                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void Remove_ArbitraryOrder()
        {
            RegistryScope scope = NewScope(3);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRequest r2 = MakeRequest(2);
                CaptureFrameRequest r3 = MakeRequest(3);

                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                CaptureFrameRenderTargetLease l2 = RentTracked(scope);
                CaptureFrameRenderTargetLease l3 = RentTracked(scope);

                Assert.That(RegisterTracked(scope, r1, l1), Is.True);
                Assert.That(RegisterTracked(scope, r2, l2), Is.True);
                Assert.That(RegisterTracked(scope, r3, l3), Is.True);

                RemoveTracked(scope, r2);
                RemoveTracked(scope, r3);
                RemoveTracked(scope, r1);

                Assert.That(scope.Registry.Count, Is.EqualTo(0));
                Assert.That(scope.Held.Count, Is.EqualTo(3));
            });
        }

        [Test]
        public void FullCapacity_ReturnsFalseAndOnlyRejectedIncrements()
        {
            RegistryScope scope = NewScope(3, 2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                CaptureFrameRenderTargetLease l2 = RentTracked(scope);

                Assert.That(RegisterTracked(scope, MakeRequest(1), l1), Is.True);
                Assert.That(RegisterTracked(scope, MakeRequest(2), l2), Is.True);

                long acceptedBefore = scope.Registry.TotalAccepted;
                long rejectedBefore = scope.Registry.TotalRejected;
                int countBefore = scope.Registry.Count;

                CaptureFrameRenderTargetLease l3 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, MakeRequest(3), l3), Is.False);

                Assert.That(scope.Registry.Count, Is.EqualTo(countBefore));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(acceptedBefore));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(rejectedBefore + 1));

                Assert.That(scope.Held.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void FullCapacity_StillRejectsDuplicatesAndConflicts()
        {
            RegistryScope scope = NewScope(2, 1);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, r1, l1), Is.True);

                Assert.Throws<ArgumentException>(() => scope.Registry.TryRegister(r1, l1));

                CaptureFrameRenderTargetLease l2 = RentTracked(scope);

                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(MakeRequestDifferent(1), l2));
                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(r1, l2));

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(0));
            });
        }

        [Test]
        public void Register_DuplicateSameEverything_Throws()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);

                Assert.Throws<ArgumentException>(() => scope.Registry.TryRegister(request, lease));

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(1));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(0));
            });
        }

        [Test]
        public void Register_SameIdDifferentRequest_Throws()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, r1, l1), Is.True);

                CaptureFrameRenderTargetLease l2 = RentTracked(scope);

                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(MakeRequestDifferent(1), l2));

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Register_SameIdSameRequestDifferentLease_Throws()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, l1), Is.True);

                CaptureFrameRenderTargetLease l2 = RentTracked(scope);

                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(request, l2));

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Register_SameLeaseDifferentCaptureFrame_Throws()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, r1, lease), Is.True);

                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(MakeRequest(2), lease));

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Register_InvalidLeases_Rejected()
        {
            RegistryScope scope = NewScope(4);
            RunRegistryBody(scope, () =>
            {
                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(MakeRequest(1), default));

                CaptureFrameRenderTargetLease returned = RentTracked(scope);
                ReturnTracked(scope, returned);
                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(MakeRequest(2), returned));

                CaptureFrameRenderTargetLease stale = RentTracked(scope);
                ReturnTracked(scope, stale);
                RentTracked(scope);
                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRegister(MakeRequest(3), stale));

                Assert.That(scope.Registry.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void Register_ForeignPoolLease_Rejected()
        {
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(2, MakeProfile());
            CaptureFrameRenderTargetPool foreignPool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(2, pool);

            CaptureFrameRenderTargetLease foreignLease = default;
            bool foreignHeld = false;
            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(foreignPool.TryRent(out foreignLease), Is.True);
                foreignHeld = true;

                Assert.Throws<InvalidOperationException>(() => registry.TryRegister(MakeRequest(1), foreignLease));

                Assert.That(registry.Count, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (foreignHeld)
            {
                foreignHeld = false;
                try { foreignPool.Return(foreignLease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            try { foreignPool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { pool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }

        [Test]
        public void InvalidOrDefaultRequest_Rejected()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = RentTracked(scope);

                Assert.Throws<ArgumentException>(() => scope.Registry.TryRegister(default, lease));
                Assert.Throws<ArgumentException>(() => scope.Registry.TryRegister(MakeRequest(0), lease));
                Assert.Throws<ArgumentException>(() => scope.Registry.TryGet(default, out _));
                Assert.Throws<ArgumentException>(() => scope.Registry.TryRemove(default, out _));
            });
        }

        [Test]
        public void GetRemove_NonExistent_ReturnsFalseDefault()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest missing = MakeRequest(999);

                Assert.That(scope.Registry.TryGet(missing, out CaptureFrameRenderTargetLease lease), Is.False);
                Assert.That(lease.IsValid, Is.False);

                Assert.That(scope.Registry.TryRemove(missing, out CaptureFrameRenderTargetLease removed), Is.False);
                Assert.That(removed.IsValid, Is.False);

                Assert.That(scope.Registry.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void GetRemove_IdMatchRequestMismatch_Throws()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, r1, l1), Is.True);

                CaptureFrameRequest r1b = MakeRequestDifferent(1);

                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryGet(r1b, out _));
                Assert.Throws<InvalidOperationException>(() => scope.Registry.TryRemove(r1b, out _));

                Assert.That(scope.Registry.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Get_DoesNotChangeStateOrCounters()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);

                long accepted = scope.Registry.TotalAccepted;
                long rejected = scope.Registry.TotalRejected;
                int count = scope.Registry.Count;
                int rented = scope.Pool.RentedCount;

                Assert.That(scope.Registry.TryGet(request, out CaptureFrameRenderTargetLease fetched), Is.True);
                Assert.That(scope.Registry.TryGet(request, out CaptureFrameRenderTargetLease fetched2), Is.True);
                Assert.That(fetched.SlotIndex, Is.EqualTo(lease.SlotIndex));
                Assert.That(fetched2.SlotIndex, Is.EqualTo(lease.SlotIndex));

                Assert.That(scope.Registry.Count, Is.EqualTo(count));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(accepted));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(rejected));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(rented));
            });
        }

        [Test]
        public void Remove_DoesNotChangeCumulativeCounters()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);
                Assert.That(RegisterTracked(scope, request, lease), Is.True);

                long accepted = scope.Registry.TotalAccepted;
                long rejected = scope.Registry.TotalRejected;

                RemoveTracked(scope, request);

                Assert.That(scope.Registry.Count, Is.EqualTo(0));
                Assert.That(scope.Registry.TotalAccepted, Is.EqualTo(accepted));
                Assert.That(scope.Registry.TotalRejected, Is.EqualTo(rejected));
            });
        }

        [Test]
        public void FailedRegister_LeaseOwnershipStaysWithCaller()
        {
            RegistryScope scope = NewScope(2, 1);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest r1 = MakeRequest(1);
                CaptureFrameRenderTargetLease l1 = RentTracked(scope);
                Assert.That(RegisterTracked(scope, r1, l1), Is.True);

                CaptureFrameRenderTargetLease l2 = RentTracked(scope);
                Assert.That(scope.Registry.TryRegister(MakeRequest(2), l2), Is.False);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(2));

                ReturnTracked(scope, l2);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                CaptureFrameRenderTargetLease l3 = RentTracked(scope);
                Assert.That(l3.SlotIndex, Is.EqualTo(l2.SlotIndex));
            });
        }

        [Test]
        public void Registry_DoesNotDisposeOrModifyPool()
        {
            RegistryScope scope = NewScope(2);
            RunRegistryBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(1);
                CaptureFrameRenderTargetLease lease = RentTracked(scope);

                Assert.That(scope.Pool.IsCreated, Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                Assert.That(RegisterTracked(scope, request, lease), Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                RemoveTracked(scope, request);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.Pool.IsCreated, Is.True);

                ReturnTracked(scope, lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
                Assert.That(scope.Pool.IsCreated, Is.True);
            });
        }

        [Test]
        public void TypeShape_SealedFixedArrayNonDisposableNoClear()
        {
            Type type = typeof(CaptureFrameRenderTargetLeaseRegistry);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            bool hasRequestArray = false;
            bool hasLeaseArray = false;
            bool hasOccupiedArray = false;

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType.IsGenericType)
                {
                    Type definition = field.FieldType.GetGenericTypeDefinition();
                    Assert.That(definition, Is.Not.EqualTo(typeof(List<>)), "Must not use List: " + field.Name);
                    Assert.That(definition, Is.Not.EqualTo(typeof(Dictionary<,>)), "Must not use Dictionary: " + field.Name);
                }

                if (field.FieldType == typeof(CaptureFrameRequest[]))
                {
                    hasRequestArray = true;
                }

                if (field.FieldType == typeof(CaptureFrameRenderTargetLease[]))
                {
                    hasLeaseArray = true;
                }

                if (field.FieldType == typeof(bool[]))
                {
                    hasOccupiedArray = true;
                }
            }

            Assert.That(hasRequestArray, Is.True);
            Assert.That(hasLeaseArray, Is.True);
            Assert.That(hasOccupiedArray, Is.True);

            Assert.That(type.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Null);
            Assert.That(type.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Null);
        }

        [Test]
        public void GpuIntegration_RegisterReadbackRemoveReturn()
        {
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11u, 12);
            CaptureFrameRequest request = new CaptureFrameRequest(context, profile.Source, profile.Eye, profile.ImageRect, profile.ArrayIndex, profile.PixelFormat);

            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            CaptureFrameRenderTargetLeaseRegistry registry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
            CaptureFrameReadbackBufferPool readbackPool = new CaptureFrameReadbackBufferPool(1, request.RequiredByteCount);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(readbackPool);

            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;
            CaptureFrameReadbackResult result = default;
            bool resultHeld = false;
            NativeArray<byte> png = default;
            bool pngHeld = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                Assert.That(registry.TryRegister(request, lease), Is.True);
                registered = true;
                leaseHeld = false;

                Assert.That(pool.RentedCount, Is.EqualTo(1));

                Assert.That(registry.TryGet(request, out CaptureFrameRenderTargetLease fetched), Is.True);
                RenderTexture rt = pool.GetRenderTexture(fetched);
                Assert.That(rt, Is.Not.Null);
                Assert.That(rt.IsCreated(), Is.True);

                FillSolidColor(rt, new Color32(0, 255, 0, 255));

                Assert.That(dispatcher.TryStart(request, rt), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out result), Is.True);
                resultHeld = true;
                Assert.That(result.HasError, Is.False);
                Assert.That(result.FrameRequest.TraceContext.CaptureFrameId, Is.EqualTo(request.TraceContext.CaptureFrameId));
                Assert.That(result.FrameRequest.Source, Is.EqualTo(request.Source));
                Assert.That(result.FrameRequest.Eye, Is.EqualTo(request.Eye));

                NativeArray<byte> buffer = dispatcher.GetBuffer(result);
                png = CaptureFramePngEncoder.Encode(buffer, request.PixelLayout);
                pngHeld = true;
                Assert.That(png.Length, Is.GreaterThan(0));
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

            if (resultHeld)
            {
                resultHeld = false;
                try { dispatcher.Release(result); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
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
                errors = AppendCleanupException(errors, ex);
            }

            if (registered)
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
                if (readbackPool.IsCreated)
                {
                    readbackPool.Dispose();
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
