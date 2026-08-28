using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFramePngStagingStoreTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string KnownPngSha256 = "630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetStoreType() => GetTypeFromAssembly("CaptureFramePngStagingStore");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        // ---- Input factories ----

        private static object MakeRun(long testRunId = 1)
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceRunContext), typeof(long), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");

            TraceRunContext context = new TraceRunContext(
                testRunId, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", 12345, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));
            return ctor.Invoke(new object[] { context, 100, 1 });
        }

        private static ConstructorInfo GetEntryCtor()
        {
            ConstructorInfo ctor = GetEntryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(long), typeof(NativeArray<byte>), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFramePngStagingEntry constructor not found.");
            return ctor;
        }

        private static object MakeEntry(long captureFrameId, long testRunId, int pngLength)
        {
            // Resolve the constructor before allocating any native memory so a
            // reflection failure never leaks the PNG.
            ConstructorInfo ctor = GetEntryCtor();

            byte[] data = new byte[pngLength];
            for (int i = 0; i < pngLength; i++)
            {
                data[i] = (byte)i;
            }

            NativeArray<byte> png = new NativeArray<byte>(data, Allocator.Persistent);

            try
            {
                return ctor.Invoke(new object[] { testRunId, captureFrameId, png, KnownPngSha256 });
            }
            catch
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }

                throw;
            }
        }

        private static object CreateStore(object run, int maximumEntryCount, long maximumTotalByteCount)
        {
            ConstructorInfo ctor = GetStoreType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRunType(), typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFramePngStagingStore constructor not found.");
            return ctor.Invoke(new object[] { run, maximumEntryCount, maximumTotalByteCount });
        }

        // ---- Store operation helpers ----

        private static bool TryRegister(object store, object entry)
        {
            MethodInfo method = GetStoreType().GetMethod("TryRegister", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryRegister method not found.");
            return (bool)method.Invoke(store, new object[] { entry });
        }

        private static Exception TryRegisterException(object store, object entry)
        {
            try
            {
                TryRegister(store, entry);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static bool TryGet(object store, long captureFrameId, out object entry)
        {
            MethodInfo method = GetStoreType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryGet method not found.");
            object[] args = new object[] { captureFrameId, null };
            bool ok = (bool)method.Invoke(store, args);
            entry = args[1];
            return ok;
        }

        private static Exception TryGetException(object store, long captureFrameId)
        {
            try
            {
                object entry;
                TryGet(store, captureFrameId, out entry);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object RollbackRegistration(object store, long captureFrameId, object expectedEntry)
        {
            MethodInfo method = GetStoreType().GetMethod("RollbackRegistration", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "RollbackRegistration method not found.");
            return method.Invoke(store, new object[] { captureFrameId, expectedEntry });
        }

        private static Exception RollbackException(object store, long captureFrameId, object expectedEntry)
        {
            try
            {
                RollbackRegistration(store, captureFrameId, expectedEntry);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Cleanup helpers ----

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

        private sealed class StoreScope
        {
            public object Run;
            public object Store;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static StoreScope NewScope(int maximumEntryCount, long maximumTotalByteCount, long testRunId = 1)
        {
            StoreScope scope = new StoreScope();
            scope.Run = MakeRun(testRunId);
            scope.Store = CreateStore(scope.Run, maximumEntryCount, maximumTotalByteCount);
            return scope;
        }

        private static object MakeEntryTracked(StoreScope scope, long captureFrameId, int pngLength, long testRunId)
        {
            object entry = MakeEntry(captureFrameId, testRunId, pngLength);
            try
            {
                scope.AllEntries.Add(entry);
            }
            catch
            {
                // Tracking never completed: dispose the entry so its PNG is
                // not leaked.
                ((IDisposable)entry).Dispose();
                throw;
            }

            return entry;
        }

        private static Exception[] CleanupScope(StoreScope scope)
        {
            Exception[] errors = null;

            try
            {
                if (scope.Store != null && (bool)GetProperty(scope.Store, "IsCreated"))
                {
                    ((IDisposable)scope.Store).Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            for (int i = scope.AllEntries.Count - 1; i >= 0; i--)
            {
                object entry = scope.AllEntries[i];
                scope.AllEntries.RemoveAt(i);
                try
                {
                    ((IDisposable)entry).Dispose();
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            return errors;
        }

        private static void RunBody(StoreScope scope, Action body)
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

        // ---- Constructor boundaries ----

        [Test]
        public void Ctor_NullRun_Rejected()
        {
            ConstructorInfo ctor = GetStoreType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRunType(), typeof(int), typeof(long) }, null);

            try
            {
                ctor.Invoke(new object[] { null, 4, 1024 });
                Assert.Fail("Expected ArgumentNullException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("run"));
            }
        }

        [Test]
        public void Ctor_EntryCountBoundaries_Rejected()
        {
            object run = MakeRun();
            ConstructorInfo ctor = GetStoreType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRunType(), typeof(int), typeof(long) }, null);

            foreach (int count in new[] { 0, -1, 100001, int.MaxValue })
            {
                try
                {
                    ctor.Invoke(new object[] { run, count, 1024 });
                    Assert.Fail("Expected ArgumentOutOfRangeException for count " + count + ".");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex.InnerException).ParamName, Is.EqualTo("maximumEntryCount"));
                }
            }
        }

        [Test]
        public void Ctor_ByteCountBoundaries_Rejected()
        {
            object run = MakeRun();
            ConstructorInfo ctor = GetStoreType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRunType(), typeof(int), typeof(long) }, null);

            foreach (long bytes in new[] { 0L, -1L, long.MinValue })
            {
                try
                {
                    ctor.Invoke(new object[] { run, 4, bytes });
                    Assert.Fail("Expected ArgumentOutOfRangeException for bytes " + bytes + ".");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex.InnerException).ParamName, Is.EqualTo("maximumTotalByteCount"));
                }
            }
        }

        [Test]
        public void Ctor_ValidBoundaries_Accepted()
        {
            object run = MakeRun();

            object minStore = CreateStore(run, 1, 1);
            Assert.That((int)GetProperty(minStore, "MaximumEntryCount"), Is.EqualTo(1));
            Assert.That((long)GetProperty(minStore, "MaximumTotalByteCount"), Is.EqualTo(1));
            ((IDisposable)minStore).Dispose();

            object maxStore = CreateStore(run, 100000, long.MaxValue);
            Assert.That((int)GetProperty(maxStore, "MaximumEntryCount"), Is.EqualTo(100000));
            ((IDisposable)maxStore).Dispose();
        }

        // ---- Registration ----

        [Test]
        public void TryRegister_Success_OwnershipCountByteCounters()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Assert.That(TryRegister(scope.Store, entry), Is.True);

                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));
                Assert.That((long)GetProperty(scope.Store, "TotalByteCount"), Is.EqualTo(16));
                Assert.That((long)GetProperty(scope.Store, "TotalAccepted"), Is.EqualTo(1));
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(0));

                object retrieved;
                Assert.That(TryGet(scope.Store, 7, out retrieved), Is.True);
                Assert.That(ReferenceEquals(retrieved, entry), Is.True);
            });
        }

        [Test]
        public void TryRegister_EntryCountFull_False_RejectedOnce()
        {
            StoreScope scope = NewScope(2, 1024);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);
                object entry2 = MakeEntryTracked(scope, 2, 16, 1);
                object entry3 = MakeEntryTracked(scope, 3, 16, 1);

                Assert.That(TryRegister(scope.Store, entry1), Is.True);
                Assert.That(TryRegister(scope.Store, entry2), Is.True);

                Assert.That(TryRegister(scope.Store, entry3), Is.False);
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(2));
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(1));
                Assert.That((long)GetProperty(scope.Store, "TotalAccepted"), Is.EqualTo(2));
            });
        }

        [Test]
        public void TryRegister_ByteCountFull_False()
        {
            StoreScope scope = NewScope(8, 20);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 1, 15, 1);
                object entry2 = MakeEntryTracked(scope, 2, 10, 1);

                Assert.That(TryRegister(scope.Store, entry1), Is.True);

                Assert.That(TryRegister(scope.Store, entry2), Is.False);
                Assert.That((long)GetProperty(scope.Store, "TotalByteCount"), Is.EqualTo(15));
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(1));
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryRegister_BothCapacityShort_RejectedOnce()
        {
            StoreScope scope = NewScope(1, 100);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 1, 9, 1);
                object entry2 = MakeEntryTracked(scope, 2, 100, 1);

                Assert.That(TryRegister(scope.Store, entry1), Is.True);

                // Count is full and the byte capacity would also reject, but a
                // single failed registration must count exactly one rejection.
                Assert.That(TryRegister(scope.Store, entry2), Is.False);
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryRegister_RejectedEntry_CallerDisposable()
        {
            StoreScope scope = NewScope(1, 9);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 1, 9, 1);
                object entry2 = MakeEntryTracked(scope, 2, 9, 1);

                Assert.That(TryRegister(scope.Store, entry1), Is.True);
                Assert.That(TryRegister(scope.Store, entry2), Is.False);

                // The rejected entry stays caller-owned and can be disposed.
                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.True);
                Assert.DoesNotThrow(() => ((IDisposable)entry2).Dispose());
                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.False);
            });
        }

        // ---- Invalid registrations ----

        [Test]
        public void TryRegister_RunMismatch_Throws()
        {
            StoreScope scope = NewScope(4, 1024, testRunId: 1);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 2); // different run

                Exception ex = TryRegisterException(scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("entry"));
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(0));
            });
        }

        [Test]
        public void TryRegister_DisposedEntry_Throws()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                ((IDisposable)entry).Dispose();

                Exception ex = TryRegisterException(scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("entry"));
            });
        }

        [Test]
        public void TryRegister_DuplicateIdSameEntry_Throws()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);

                Exception ex = TryRegisterException(scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("entry"));
            });
        }

        [Test]
        public void TryRegister_DuplicateIdDifferentEntry_Throws()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 7, 16, 1);
                object entry2 = MakeEntryTracked(scope, 7, 16, 1);

                Assert.That(TryRegister(scope.Store, entry1), Is.True);

                Exception ex = TryRegisterException(scope.Store, entry2);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryRegister_InvalidConditionBeforeCapacity_Throws()
        {
            StoreScope scope = NewScope(1, 1024);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);
                Assert.That(TryRegister(scope.Store, entry1), Is.True); // fills the store

                // A run mismatch must throw, not be reported as a capacity rejection.
                object entry2 = MakeEntryTracked(scope, 2, 16, 2);
                Exception ex = TryRegisterException(scope.Store, entry2);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(0));
            });
        }

        // ---- TryGet ----

        [Test]
        public void TryGet_Nonexistent_False()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry;
                Assert.That(TryGet(scope.Store, 999, out entry), Is.False);
                Assert.That(entry, Is.Null);
            });
        }

        [Test]
        public void TryGet_InvalidId_Throws()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                foreach (long id in new[] { 0L, -1L })
                {
                    Exception ex = TryGetException(scope.Store, id);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "id " + id);
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("captureFrameId"));
                }
            });
        }

        // ---- Rollback ----

        [Test]
        public void Rollback_Success_OwnershipReturned_ByteRestored_CapacityReused()
        {
            StoreScope scope = NewScope(1, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));

                object returned = RollbackRegistration(scope.Store, 7, entry);
                Assert.That(ReferenceEquals(returned, entry), Is.True);
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(0));
                Assert.That((long)GetProperty(scope.Store, "TotalByteCount"), Is.EqualTo(0));
                Assert.That((long)GetProperty(scope.Store, "TotalAccepted"), Is.EqualTo(1)); // cumulative, unchanged

                // The freed slot can be reused.
                object entry2 = MakeEntryTracked(scope, 8, 16, 1);
                Assert.That(TryRegister(scope.Store, entry2), Is.True);
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));
                Assert.That((long)GetProperty(scope.Store, "TotalAccepted"), Is.EqualTo(2));
            });
        }

        [Test]
        public void Rollback_Nonexistent_Throws_NoSideEffects()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);

                Exception ex = RollbackException(scope.Store, 999, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));
                Assert.That((long)GetProperty(scope.Store, "TotalByteCount"), Is.EqualTo(16));
            });
        }

        [Test]
        public void Rollback_DifferentEntry_Throws_NoSideEffects()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                object other = MakeEntryTracked(scope, 9, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);

                Exception ex = RollbackException(scope.Store, 7, other);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(1));
            });
        }

        [Test]
        public void Rollback_DoubleRollback_Throws()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);

                Assert.That(ReferenceEquals(RollbackRegistration(scope.Store, 7, entry), entry), Is.True);

                Exception ex = RollbackException(scope.Store, 7, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(0));
            });
        }

        // ---- Dispose ----

        [Test]
        public void Dispose_MultipleEntries_AllDisposed()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);
                object entry2 = MakeEntryTracked(scope, 2, 16, 1);
                Assert.That(TryRegister(scope.Store, entry1), Is.True);
                Assert.That(TryRegister(scope.Store, entry2), Is.True);

                ((IDisposable)scope.Store).Dispose();

                Assert.That((bool)GetProperty(scope.Store, "IsCreated"), Is.False);
                Assert.That((bool)GetProperty(entry1, "IsCreated"), Is.False);
                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.False);
            });
        }

        [Test]
        public void Dispose_Idempotent()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);

                Assert.DoesNotThrow(() => ((IDisposable)scope.Store).Dispose());
                Assert.DoesNotThrow(() => ((IDisposable)scope.Store).Dispose());
                Assert.That((bool)GetProperty(scope.Store, "IsCreated"), Is.False);
            });
        }

        [Test]
        public void StoreDisposed_ApiThrows()
        {
            StoreScope scope = NewScope(4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                Assert.That(TryRegister(scope.Store, entry), Is.True);
                ((IDisposable)scope.Store).Dispose();

                Assert.That((bool)GetProperty(scope.Store, "IsCreated"), Is.False);

                Assert.That(TryRegisterException(scope.Store, entry), Is.TypeOf<ObjectDisposedException>());
                Assert.That(TryGetException(scope.Store, 7), Is.TypeOf<ObjectDisposedException>());
                Assert.That(RollbackException(scope.Store, 7, entry), Is.TypeOf<ObjectDisposedException>());

                foreach (string prop in new[] { "Run", "MaximumEntryCount", "MaximumTotalByteCount", "Count", "TotalByteCount", "TotalAccepted", "TotalRejected" })
                {
                    try
                    {
                        GetProperty(scope.Store, prop);
                        Assert.Fail("Expected ObjectDisposedException for property " + prop + ".");
                    }
                    catch (TargetInvocationException ex)
                    {
                        Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>(), prop);
                    }
                }
            });
        }

        // ---- Type shape ----

        [Test]
        public void Store_InternalSealed_NoPublicCtor_NoClear_NoStaticState_NotMonoBehaviourOrScriptableObject()
        {
            Type type = GetStoreType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Store_NoVariableCollections_SingleEntryArray()
        {
            Type type = GetStoreType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(8)); // run, entries, maxBytes, count, totalBytes, accepted, rejected, disposed

            bool hasEntryArray = false;
            foreach (FieldInfo field in fields)
            {
                Type fieldType = field.FieldType;

                bool isList = fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>);
                bool isDictionary = fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>);
                Assert.That(isList, Is.False, "Store must not hold a List.");
                Assert.That(isDictionary, Is.False, "Store must not hold a Dictionary.");

                if (fieldType == GetEntryType().MakeArrayType())
                {
                    hasEntryArray = true;
                }
            }

            Assert.That(hasEntryArray, Is.True);
        }
    }
}
