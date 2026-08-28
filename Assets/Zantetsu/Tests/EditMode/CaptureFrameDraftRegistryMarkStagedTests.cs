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
    public class CaptureFrameDraftRegistryMarkStagedTests
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

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetReservationType() => GetTypeFromAssembly("CaptureFrameDraftReservation");

        private static Type GetRejectKindType() => GetTypeFromAssembly("CaptureFrameAdmissionRejectKind");

        private static Type GetStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

        private static Type GetEmissionStateType() => GetTypeFromAssembly("DraftDropTraceEmissionState");

        private static Type GetStoreType() => GetTypeFromAssembly("CaptureFramePngStagingStore");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

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

        private static object MakeRun(long testRunId = 1, int captureProfileId = 5)
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
            return ctor.Invoke(new object[] { context, 100, captureProfileId });
        }

        private static CaptureTraceProfile MakeProfile(int captureProfileId = 5, int maxInFlight = 2, int maxDraftPerRun = 4)
        {
            return new CaptureTraceProfile(captureProfileId, 4096, maxInFlight, maxDraftPerRun);
        }

        private static object CreateRegistry(object run, CaptureTraceProfile profile)
        {
            ConstructorInfo ctor = GetRegistryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRunType(), typeof(CaptureTraceProfile) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftRegistry constructor not found.");
            return ctor.Invoke(new object[] { run, profile });
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId = 1, CaptureEye eye = CaptureEye.Left)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, eye, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static object MakeDraft(object run, CaptureFrameRequest request, int commitPathId = 1)
        {
            ConstructorInfo ctor = GetDraftType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetRunType(),
                    typeof(CaptureFrameRequest).MakeByRefType(),
                    typeof(CaptureFrameTiming).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(int)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraft constructor not found.");
            return ctor.Invoke(new object[]
            {
                run, request,
                new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L),
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity),
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity),
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity),
                commitPathId
            });
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

        // ---- Registry operation helpers ----

        private static bool TryReserve(object registry, out object reservation, out object rejectKind)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryReserve", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { null, null };
            bool ok = (bool)method.Invoke(registry, args);
            reservation = args[0];
            rejectKind = args[1];
            return ok;
        }

        private static void Commit(object registry, object reservation, object draft)
        {
            MethodInfo method = GetRegistryType().GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { reservation, draft });
        }

        private static object CommitDraft(object registry, object run, CaptureFrameRequest request)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, request);
            Commit(registry, reservation, draft);
            return draft;
        }

        private static int Count(object registry, string name)
        {
            return (int)GetProperty(registry, name);
        }

        private static int GetSlotState(object registry, int slotIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotState", BindingFlags.NonPublic | BindingFlags.Instance);
            Array states = (Array)field.GetValue(registry);
            return (int)states.GetValue(slotIndex);
        }

        private static void SetSlotState(object registry, int slotIndex, int state)
        {
            FieldInfo field = GetRegistryType().GetField("_slotState", BindingFlags.NonPublic | BindingFlags.Instance);
            Array states = (Array)field.GetValue(registry);
            states.SetValue(Enum.ToObject(field.FieldType.GetElementType(), state), slotIndex);
        }

        private static int GetSlotEntryIndex(object registry, int slotIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotEntryIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            int[] indices = (int[])field.GetValue(registry);
            return indices[slotIndex];
        }

        private static void SetSlotEntryIndex(object registry, int slotIndex, int entryIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotEntryIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            int[] indices = (int[])field.GetValue(registry);
            indices[slotIndex] = entryIndex;
        }

        private static void SetPendingCount(object registry, int value)
        {
            FieldInfo field = GetRegistryType().GetField("_pendingCount", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(registry, value);
        }

        private static object GetEntryField(object registry, int entryIndex, string fieldName)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(entryIndex);
            FieldInfo field = entry.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Entry." + fieldName + " field not found.");
            return field.GetValue(entry);
        }

        private static void SetEntryField(object registry, int entryIndex, string fieldName, object value)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(entryIndex);
            FieldInfo field = entry.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(entry, value);
            entries.SetValue(entry, entryIndex);
        }

        // ---- Store operation helpers ----

        private static bool TryRegister(object store, object entry)
        {
            MethodInfo method = GetStoreType().GetMethod("TryRegister", BindingFlags.NonPublic | BindingFlags.Instance);
            return (bool)method.Invoke(store, new object[] { entry });
        }

        private static bool TryGet(object store, long captureFrameId, out object entry)
        {
            MethodInfo method = GetStoreType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { captureFrameId, null };
            bool ok = (bool)method.Invoke(store, args);
            entry = args[1];
            return ok;
        }

        private static object RollbackRegistration(object store, long captureFrameId, object expectedEntry)
        {
            MethodInfo method = GetStoreType().GetMethod("RollbackRegistration", BindingFlags.NonPublic | BindingFlags.Instance);
            return method.Invoke(store, new object[] { captureFrameId, expectedEntry });
        }

        // ---- TryMarkStaged invoke helpers ----

        private static bool TryMarkStaged(object registry, CaptureFrameRequest request, object store, object entry)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryMarkStaged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryMarkStaged method not found.");
            return (bool)method.Invoke(registry, new object[] { request, store, entry });
        }

        private static Exception TryMarkStagedException(object registry, CaptureFrameRequest request, object store, object entry)
        {
            try
            {
                TryMarkStaged(registry, request, store, entry);
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

        private sealed class MarkStagedScope
        {
            public object Run;
            public object Registry;
            public object Store;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static MarkStagedScope NewScope(int maxInFlight, int maxDraftPerRun, int storeMaxEntries, long storeMaxBytes, long testRunId = 1)
        {
            MarkStagedScope scope = new MarkStagedScope();
            scope.Run = MakeRun(testRunId, captureProfileId: 5);
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, maxInFlight, maxDraftPerRun));
            scope.Store = CreateStore(scope.Run, storeMaxEntries, storeMaxBytes);
            return scope;
        }

        private static object MakeEntryTracked(MarkStagedScope scope, long captureFrameId, int pngLength, long testRunId)
        {
            object entry = MakeEntry(captureFrameId, testRunId, pngLength);
            try
            {
                scope.AllEntries.Add(entry);
            }
            catch
            {
                ((IDisposable)entry).Dispose();
                throw;
            }

            return entry;
        }

        private static Exception[] CleanupScope(MarkStagedScope scope)
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

        private static void RunBody(MarkStagedScope scope, Action body)
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

        // ---- Null / invalid arguments ----

        [Test]
        public void TryMarkStaged_NullStore_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Exception ex = TryMarkStagedException(scope.Registry, request, null, entry);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("stagingStore"));
            });
        }

        [Test]
        public void TryMarkStaged_NullEntry_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("stagingEntry"));
            });
        }

        [Test]
        public void TryMarkStaged_InvalidRequest_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Exception ex = TryMarkStagedException(scope.Registry, default, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
            });
        }

        [Test]
        public void TryMarkStaged_DisposedStore_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                ((IDisposable)scope.Store).Dispose();

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
            });
        }

        [Test]
        public void TryMarkStaged_DisposedEntry_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);
                ((IDisposable)entry).Dispose();

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
            });
        }

        [Test]
        public void TryMarkStaged_RunReferenceMismatch_Rejected()
        {
            // Same test run ID but a different run instance.
            object run = MakeRun(1, captureProfileId: 5);
            object registry = CreateRegistry(run, MakeProfile(5, 2, 4));
            object otherRun = MakeRun(1, captureProfileId: 5);
            object store = CreateStore(otherRun, 4, 1024);

            MarkStagedScope scope = new MarkStagedScope { Run = run, Registry = registry, Store = store };

            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(registry, run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Exception ex = TryMarkStagedException(registry, request, store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingStore"));
            });
        }

        [Test]
        public void TryMarkStaged_EntryTestRunIdMismatch_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7, testRunId: 1);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 2); // different run

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingEntry"));
            });
        }

        [Test]
        public void TryMarkStaged_EntryCaptureFrameIdMismatch_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 8, 16, 1); // different frame ID

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingEntry"));
            });
        }

        // ---- Registry lookup rejections ----

        [Test]
        public void TryMarkStaged_RegistryNonexistent_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Exception ex = TryMarkStagedException(scope.Registry, MakeRequest(7), scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void TryMarkStaged_RequestMismatch_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                // Same capture frame ID but a different request (different eye).
                CaptureFrameRequest different = MakeRequest(7, eye: CaptureEye.Right);
                Exception ex = TryMarkStagedException(scope.Registry, different, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        // ---- Status / reason / emission rejections ----

        [Test]
        public void TryMarkStaged_AlreadyStaged_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetEntryField(scope.Registry, 0, "Status", Enum.ToObject(GetStatusType(), 1)); // Staged

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryMarkStaged_AlreadyDropped_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetEntryField(scope.Registry, 0, "Status", Enum.ToObject(GetStatusType(), 2)); // Dropped

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void TryMarkStaged_DropReasonCorrupt_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetEntryField(scope.Registry, 0, "DropReason", (CaptureFrameDropReason)6);

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void TryMarkStaged_EmissionStateCorrupt_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetEntryField(scope.Registry, 0, "EmissionState", Enum.ToObject(GetEmissionStateType(), 1));

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        // ---- Pending slot invariant rejections ----

        [Test]
        public void TryMarkStaged_NoPendingSlot_Rejected_NoSideEffects()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetSlotState(scope.Registry, 0, 0); // Free: no occupied slot

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryMarkStaged_MultipleSlots_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetSlotState(scope.Registry, 1, 2); // Occupied
                SetSlotEntryIndex(scope.Registry, 1, 0); // pointing at the same entry

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void TryMarkStaged_PendingCountZero_Rejected()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                SetPendingCount(scope.Registry, 0);

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entry);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        // ---- Success ----

        [Test]
        public void TryMarkStaged_Success_RegistryState()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                object draft = CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                Assert.That((int)GetEntryField(scope.Registry, 0, "Status"), Is.EqualTo(1)); // Staged
                Assert.That((int)GetEntryField(scope.Registry, 0, "DropReason"), Is.EqualTo(0)); // None
                Assert.That((int)GetEntryField(scope.Registry, 0, "EmissionState"), Is.EqualTo(0)); // None
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
                Assert.That(GetSlotState(scope.Registry, 0), Is.EqualTo(0)); // Free
                Assert.That(GetSlotEntryIndex(scope.Registry, 0), Is.EqualTo(-1));
            });
        }

        [Test]
        public void TryMarkStaged_Success_StoreHoldsSameEntry()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                object stored;
                Assert.That(TryGet(scope.Store, 7, out stored), Is.True);
                Assert.That(ReferenceEquals(stored, entry), Is.True);
                Assert.That((long)GetProperty(stored, "CaptureFrameId"), Is.EqualTo(7));
                Assert.That((int)GetProperty(stored, "ByteCount"), Is.EqualTo(16));
                Assert.That((string)GetProperty(stored, "ContentSha256"), Is.EqualTo(KnownPngSha256));
            });
        }

        [Test]
        public void TryMarkStaged_Success_SlotReusedByNextDraft()
        {
            MarkStagedScope scope = NewScope(1, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest first = MakeRequest(1);
                CommitDraft(scope.Registry, scope.Run, first);
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, first, scope.Store, entry1), Is.True);
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));

                CaptureFrameRequest second = MakeRequest(2);
                CommitDraft(scope.Registry, scope.Run, second);
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
                Assert.That(GetSlotState(scope.Registry, 0), Is.EqualTo(2)); // Occupied again
                Assert.That(GetSlotEntryIndex(scope.Registry, 0), Is.EqualTo(1));
            });
        }

        // ---- Store capacity shortage ----

        [Test]
        public void TryMarkStaged_EntryCountFull_False()
        {
            MarkStagedScope scope = NewScope(2, 4, 1, 1024); // store holds one entry
            RunBody(scope, () =>
            {
                CaptureFrameRequest first = MakeRequest(1);
                CommitDraft(scope.Registry, scope.Run, first);
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);
                Assert.That(TryMarkStaged(scope.Registry, first, scope.Store, entry1), Is.True);

                CaptureFrameRequest second = MakeRequest(2);
                CommitDraft(scope.Registry, scope.Run, second);
                object entry2 = MakeEntryTracked(scope, 2, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, second, scope.Store, entry2), Is.False);

                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(1));
                // Draft stays pending; slot not released.
                Assert.That((int)GetEntryField(scope.Registry, 1, "Status"), Is.EqualTo(0)); // Pending
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryMarkStaged_ByteCountFull_False()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 20);
            RunBody(scope, () =>
            {
                CaptureFrameRequest first = MakeRequest(1);
                CommitDraft(scope.Registry, scope.Run, first);
                object entry1 = MakeEntryTracked(scope, 1, 15, 1);
                Assert.That(TryMarkStaged(scope.Registry, first, scope.Store, entry1), Is.True);

                CaptureFrameRequest second = MakeRequest(2);
                CommitDraft(scope.Registry, scope.Run, second);
                object entry2 = MakeEntryTracked(scope, 2, 10, 1);

                Assert.That(TryMarkStaged(scope.Registry, second, scope.Store, entry2), Is.False);

                Assert.That((long)GetProperty(scope.Store, "TotalByteCount"), Is.EqualTo(15));
                Assert.That((int)GetEntryField(scope.Registry, 1, "Status"), Is.EqualTo(0)); // Pending
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void TryMarkStaged_False_RegistryUnchanged_CallerOwnership()
        {
            MarkStagedScope scope = NewScope(2, 4, 1, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest first = MakeRequest(1);
                CommitDraft(scope.Registry, scope.Run, first);
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);
                Assert.That(TryMarkStaged(scope.Registry, first, scope.Store, entry1), Is.True);

                CaptureFrameRequest second = MakeRequest(2);
                CommitDraft(scope.Registry, scope.Run, second);
                object entry2 = MakeEntryTracked(scope, 2, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, second, scope.Store, entry2), Is.False);

                // Registry completely unchanged for the second draft.
                Assert.That((int)GetEntryField(scope.Registry, 1, "Status"), Is.EqualTo(0)); // Pending
                Assert.That((int)GetEntryField(scope.Registry, 1, "DropReason"), Is.EqualTo(0));
                Assert.That((int)GetEntryField(scope.Registry, 1, "EmissionState"), Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(2));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
                Assert.That(GetSlotState(scope.Registry, 0), Is.EqualTo(2)); // Occupied
                Assert.That(GetSlotEntryIndex(scope.Registry, 0), Is.EqualTo(1));

                // The rejected entry is still caller-owned.
                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.True);
            });
        }

        [Test]
        public void TryMarkStaged_RetryAfterCapacityFreed_Succeeds()
        {
            MarkStagedScope scope = NewScope(2, 4, 1, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest first = MakeRequest(1);
                CommitDraft(scope.Registry, scope.Run, first);
                object entry1 = MakeEntryTracked(scope, 1, 16, 1);
                Assert.That(TryMarkStaged(scope.Registry, first, scope.Store, entry1), Is.True);

                CaptureFrameRequest second = MakeRequest(2);
                CommitDraft(scope.Registry, scope.Run, second);
                object entry2 = MakeEntryTracked(scope, 2, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, second, scope.Store, entry2), Is.False);

                // Free store capacity (test-only) and retry the same draft/entry.
                Assert.That(ReferenceEquals(RollbackRegistration(scope.Store, 1, entry1), entry1), Is.True);

                Assert.That(TryMarkStaged(scope.Registry, second, scope.Store, entry2), Is.True);
                Assert.That((int)GetEntryField(scope.Registry, 1, "Status"), Is.EqualTo(1)); // Staged
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
            });
        }

        // ---- Store exception leaves registry unchanged ----

        [Test]
        public void TryMarkStaged_StoreDuplicate_RegistryUnchanged()
        {
            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);

                object entryA = MakeEntryTracked(scope, 7, 16, 1);
                object entryA2 = MakeEntryTracked(scope, 7, 16, 1);

                // Pre-register a different entry instance with the same ID.
                Assert.That(TryRegister(scope.Store, entryA), Is.True);

                Exception ex = TryMarkStagedException(scope.Registry, request, scope.Store, entryA2);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());

                // Registry unchanged.
                Assert.That((int)GetEntryField(scope.Registry, 0, "Status"), Is.EqualTo(0)); // Pending
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        // ---- No trace / no extra dependencies ----

        [Test]
        public void TryMarkStaged_NoLoggerOrExtraDependency_NoTrace()
        {
            Type registryType = GetRegistryType();

            // The registry structurally cannot trace: it holds no logger or observer.
            foreach (FieldInfo field in registryType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(typeof(TraceLogger).IsAssignableFrom(field.FieldType), Is.False, "Registry must not hold a TraceLogger.");
                Assert.That(typeof(CaptureFrameTraceObserver).IsAssignableFrom(field.FieldType), Is.False, "Registry must not hold an observer.");
            }

            MarkStagedScope scope = NewScope(2, 4, 4, 1024);
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 7, 16, 1);

                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
            });
        }
    }
}
