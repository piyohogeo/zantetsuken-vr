using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class ForcedDropFrameIdSetTests
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

        private static Type GetQueueType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntentQueue");

        private static Type GetSnapshotType() => GetTypeFromAssembly("TerminalIntentOwnershipSnapshot");

        private static Type GetSetType() => GetTypeFromAssembly("ForcedDropFrameIdSet");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

        private static Type GetStoreType() => GetTypeFromAssembly("CaptureFramePngStagingStore");

        private static Type GetIntentType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntent");

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
            Assert.That(ctor, Is.Not.Null);

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
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { run, profile });
        }

        private static object CreateQueue(object registry, CaptureTraceProfile profile)
        {
            ConstructorInfo ctor = GetQueueType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), typeof(CaptureTraceProfile) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { registry, profile });
        }

        private static object CreateStore(object run, int maximumEntryCount, long maximumTotalByteCount)
        {
            ConstructorInfo ctor = GetStoreType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRunType(), typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { run, maximumEntryCount, maximumTotalByteCount });
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
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
            Assert.That(ctor, Is.Not.Null);
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
            Assert.That(ctor, Is.Not.Null);
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

        // ---- Registry / queue / store operation helpers ----

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

        private static object CommitDraft(object registry, object run, long captureFrameId)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, MakeRequest(captureFrameId));
            Commit(registry, reservation, draft);
            return draft;
        }

        private static void CommitAndRegister(object queue, object registry, object run, long captureFrameId)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, MakeRequest(captureFrameId));
            Commit(registry, reservation, draft);
            MethodInfo register = GetQueueType().GetMethod("RegisterPendingDraft", BindingFlags.NonPublic | BindingFlags.Instance);
            register.Invoke(queue, new object[] { draft });
        }

        private static void MarkDropped(object registry, CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetRegistryType().GetMethod("MarkDropped", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { request, reason });
        }

        private static bool TryMarkStaged(object registry, CaptureFrameRequest request, object store, object entry)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryMarkStaged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(registry, new object[] { request, store, entry });
        }

        private static int EnqueueTerminalIntent(object queue, object intent)
        {
            MethodInfo method = GetQueueType().GetMethod("EnqueueTerminalIntent", BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)method.Invoke(queue, new object[] { intent });
        }

        private static object CreateDropIntent(CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetIntentType().GetMethod("CreateDrop", BindingFlags.NonPublic | BindingFlags.Static);
            return method.Invoke(null, new object[] { request, reason });
        }

        private static bool TryDequeue(object queue, out object intent)
        {
            MethodInfo method = GetQueueType().GetMethod("TryDequeue", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { null };
            bool ok = (bool)method.Invoke(queue, args);
            intent = args[0];
            return ok;
        }

        private static void BeginProducerDrain(object queue)
        {
            MethodInfo method = GetQueueType().GetMethod("BeginProducerDrain", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(queue, null);
        }

        private static void CloseAfterProducerJoin(object queue)
        {
            MethodInfo method = GetQueueType().GetMethod("CloseAfterProducerJoin", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(queue, null);
        }

        private static object CreateOwnershipSnapshot(object queue, int producerRetainedPrivateBufferCount)
        {
            MethodInfo method = GetQueueType().GetMethod("CreateOwnershipSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(queue, new object[] { producerRetainedPrivateBufferCount });
        }

        private static object GetIssuedSnapshot(object queue)
        {
            PropertyInfo prop = GetQueueType().GetProperty("IssuedOwnershipSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null);
            return prop.GetValue(queue);
        }

        private static object ForceDrop(object registry, object queue, object snapshot)
        {
            MethodInfo method = GetRegistryType().GetMethod("ForceDropPendingForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(registry, new object[] { queue, snapshot });
        }

        private static Exception ForceDropException(object registry, object queue, object snapshot)
        {
            try
            {
                ForceDrop(registry, queue, snapshot);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object GetIssuedForcedDropSet(object registry)
        {
            PropertyInfo prop = GetRegistryType().GetProperty("IssuedForcedDropFrameIdSet", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null);
            return prop.GetValue(registry);
        }

        private static int Count(object registry, string name) => (int)GetProperty(registry, name);

        // ---- Registry corruption helpers ----

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
            Assert.That(field, Is.Not.Null, "Entry." + fieldName + " field not found.");
            field.SetValue(entry, value);
            entries.SetValue(entry, entryIndex);
        }

        private static void SetEntryEnumField(object registry, int entryIndex, string fieldName, int enumValue)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(entryIndex);
            FieldInfo field = entry.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Entry." + fieldName + " field not found.");
            field.SetValue(entry, Enum.ToObject(field.FieldType, enumValue));
            entries.SetValue(entry, entryIndex);
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

        private static void SetCount(object registry, string fieldName, int value)
        {
            FieldInfo field = GetRegistryType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(registry, value);
        }

        private static string DescribeRegistry(object registry)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("E=").Append(Count(registry, "EntryCount"));
            sb.Append(" P=").Append(Count(registry, "PendingCount"));
            sb.Append(" R=").Append(Count(registry, "ReservationCount"));
            sb.Append(' ');

            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Array entries = (Array)entriesField.GetValue(registry);
            int entryCount = Count(registry, "EntryCount");
            for (int i = 0; i < entryCount; i++)
            {
                object e = entries.GetValue(i);
                Type et = e.GetType();
                object draft = et.GetField("Draft", BindingFlags.Public | BindingFlags.Instance).GetValue(e);
                long id = draft == null ? -999 : (long)GetProperty(draft, "CaptureFrameId");
                int status = (int)et.GetField("Status", BindingFlags.Public | BindingFlags.Instance).GetValue(e);
                int reason = (int)et.GetField("DropReason", BindingFlags.Public | BindingFlags.Instance).GetValue(e);
                int emission = (int)et.GetField("EmissionState", BindingFlags.Public | BindingFlags.Instance).GetValue(e);
                sb.Append('{').Append(id).Append('/').Append(status).Append('/').Append(reason).Append('/').Append(emission).Append('}');
            }

            FieldInfo slotStateField = GetRegistryType().GetField("_slotState", BindingFlags.NonPublic | BindingFlags.Instance);
            Array slotState = (Array)slotStateField.GetValue(registry);
            FieldInfo slotIndexField = GetRegistryType().GetField("_slotEntryIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            Array slotIndex = (Array)slotIndexField.GetValue(registry);
            for (int s = 0; s < slotState.Length; s++)
            {
                sb.Append('[').Append((int)slotState.GetValue(s)).Append('/').Append((int)slotIndex.GetValue(s)).Append(']');
            }

            return sb.ToString();
        }

        // ---- Snapshot / set construction helpers ----

        private static object CreateSnapshotRaw(object queue, long testRunId, int queueCount, int accepted, int processed, int queueOwned, int producerRetained)
        {
            ConstructorInfo ctor = GetSnapshotType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetQueueType(), typeof(long), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { queue, testRunId, queueCount, accepted, processed, queueOwned, producerRetained });
        }

        private static object CreateSetRaw(object registry, long testRunId, long[] ids)
        {
            ConstructorInfo ctor = GetSetType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), typeof(long), typeof(long[]) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { registry, testRunId, ids });
        }

        private static Exception SetCtorException(object registry, long testRunId, long[] ids)
        {
            try
            {
                CreateSetRaw(registry, testRunId, ids);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static long SetGetCaptureFrameId(object set, int index)
        {
            MethodInfo m = GetSetType().GetMethod("GetCaptureFrameId", BindingFlags.Public | BindingFlags.Instance);
            return (long)m.Invoke(set, new object[] { index });
        }

        private static Exception SetGetCaptureFrameIdException(object set, int index)
        {
            try
            {
                SetGetCaptureFrameId(set, index);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static bool SetContains(object set, long id)
        {
            MethodInfo m = GetSetType().GetMethod("Contains", BindingFlags.Public | BindingFlags.Instance);
            return (bool)m.Invoke(set, new object[] { id });
        }

        private static Exception SetContainsException(object set, long id)
        {
            try
            {
                SetContains(set, id);
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

        private sealed class Scope
        {
            public int MaxDraftPerRun;
            public long TestRunId;

            public object Run;
            public object Registry;
            public object Queue;
            public object Store;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static Scope NewScope(int maxDraftPerRun = 8, long testRunId = 1)
        {
            Scope scope = new Scope();
            scope.MaxDraftPerRun = maxDraftPerRun;
            scope.TestRunId = testRunId;
            return scope;
        }

        private static void BuildScope(Scope scope)
        {
            scope.Run = MakeRun(scope.TestRunId, captureProfileId: 5);
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Queue = CreateQueue(scope.Registry, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Store = CreateStore(scope.Run, scope.MaxDraftPerRun, 4096);
        }

        private static object MakeEntryTracked(Scope scope, long captureFrameId, long testRunId, int pngLength)
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

        private static Exception[] CleanupScope(Scope scope)
        {
            Exception[] errors = null;

            try
            {
                if (scope.Queue != null && (bool)GetProperty(scope.Queue, "IsCreated"))
                {
                    ((IDisposable)scope.Queue).Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

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

        private static void RunBody(Scope scope, Action body)
        {
            ExceptionDispatchInfo bodyException = null;
            try
            {
                BuildScope(scope);
                body();
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] errors = CleanupScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        /// <summary>
        /// Commits and registers each pending draft, drains one intent per draft,
        /// closes the queue, and returns its issued ownership snapshot.
        /// </summary>
        private static object SetupForFreeze(Scope scope, long[] captureFrameIds)
        {
            for (int i = 0; i < captureFrameIds.Length; i++)
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, captureFrameIds[i]);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(captureFrameIds[i]), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
            }

            BeginProducerDrain(scope.Queue);
            CloseAfterProducerJoin(scope.Queue);

            for (int i = 0; i < captureFrameIds.Length; i++)
            {
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);
            }

            return CreateOwnershipSnapshot(scope.Queue, 0);
        }

        private static void AssertFreezeRejectedUnchanged(Scope scope, object snapshot, Action corrupt)
        {
            corrupt();
            string before = DescribeRegistry(scope.Registry);
            Exception ex = ForceDropException(scope.Registry, scope.Queue, snapshot);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(GetIssuedForcedDropSet(scope.Registry), Is.Null);
            Assert.That(DescribeRegistry(scope.Registry), Is.EqualTo(before));
        }

        // ---- ForcedDropFrameIdSet contracts ----

        [Test]
        public void Set_Empty_IsValid()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[0]);
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That(set, Is.Not.Null);
                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(0));
                Assert.That((long)GetProperty(set, "TestRunId"), Is.EqualTo(1));
                Assert.That((bool)GetProperty(set, "IsValid"), Is.True);
                Assert.That(ReferenceEquals(GetProperty(set, "IssuedBy"), scope.Registry), Is.True);
            });
        }

        [Test]
        public void Set_SingleAndMultiple_Ascending_Contains()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 3, 5, 7 });
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(3));
                Assert.That(SetGetCaptureFrameId(set, 0), Is.EqualTo(3));
                Assert.That(SetGetCaptureFrameId(set, 1), Is.EqualTo(5));
                Assert.That(SetGetCaptureFrameId(set, 2), Is.EqualTo(7));

                Assert.That(SetContains(set, 3), Is.True);
                Assert.That(SetContains(set, 5), Is.True);
                Assert.That(SetContains(set, 7), Is.True);
                Assert.That(SetContains(set, 4), Is.False);
                Assert.That((bool)GetProperty(set, "IsValid"), Is.True);
            });
        }

        [Test]
        public void Set_GetCaptureFrameId_OutOfRange_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                foreach (int index in new[] { -1, 1, 5 })
                {
                    Exception ex = SetGetCaptureFrameIdException(set, index);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("index"));
                }
            });
        }

        [Test]
        public void Set_Contains_NonPositive_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                foreach (long id in new[] { 0L, -1L })
                {
                    Exception ex = SetContainsException(set, id);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("captureFrameId"));
                }
            });
        }

        [Test]
        public void Set_NoPublicConstructorNoArrayExposure_NotDisposable()
        {
            Type type = GetSetType();
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
                Assert.That(prop.PropertyType.IsArray, Is.False, prop.Name + " must not expose an array.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType.IsArray, Is.False, method.Name + " must not expose an array.");
            }
        }

        [Test]
        public void SetCtor_IssuedByNull_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SetCtorException(null, 1, new long[0]);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("issuedBy"));
            });
        }

        [Test]
        public void SetCtor_TestRunIdNonPositive_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                foreach (long testRunId in new[] { 0L, -1L })
                {
                    Exception ex = SetCtorException(scope.Registry, testRunId, new long[0]);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("testRunId"));
                }
            });
        }

        [Test]
        public void SetCtor_IdsNull_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SetCtorException(scope.Registry, 1, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("captureFrameIds"));
            });
        }

        [Test]
        public void SetCtor_NonPositiveId_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SetCtorException(scope.Registry, 1, new long[] { 0 });
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("captureFrameIds"));
            });
        }

        [Test]
        public void SetCtor_NonIncreasingIds_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                foreach (long[] ids in new[] { new long[] { 2, 1 }, new long[] { 1, 1 }, new long[] { 1, 2, 2 } })
                {
                    Exception ex = SetCtorException(scope.Registry, 1, ids);
                    Assert.That(ex, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("captureFrameIds"));
                }
            });
        }

        // ---- ForceDrop dependency / snapshot validation ----

        [Test]
        public void ForceDrop_NullQueue_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[0]);
                Exception ex = ForceDropException(scope.Registry, null, snapshot);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("intentQueue"));
            });
        }

        [Test]
        public void ForceDrop_NullSnapshot_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = ForceDropException(scope.Registry, scope.Queue, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("ownershipSnapshot"));
            });
        }

        [Test]
        public void ForceDrop_QueueBoundToOtherRegistry_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRegistry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object otherQueue = CreateQueue(otherRegistry, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));

                object forged = CreateSnapshotRaw(scope.Queue, 1, 0, 0, 0, 0, 0); // non-null so step 3 is reached

                Exception ex = ForceDropException(scope.Registry, otherQueue, forged);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("intentQueue"));
            });
        }

        [Test]
        public void ForceDrop_ForgedSnapshotWrongIssuer_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherQueue = CreateQueue(scope.Registry, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object forged = CreateSnapshotRaw(otherQueue, 1, 0, 0, 0, 0, 0);

                Exception ex = ForceDropException(scope.Registry, scope.Queue, forged);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("ownershipSnapshot"));
            });
        }

        [Test]
        public void ForceDrop_ForgedSnapshotNotIssued_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object forged = CreateSnapshotRaw(scope.Queue, 1, 0, 0, 0, 0, 0); // valid but never issued by the queue

                Exception ex = ForceDropException(scope.Registry, scope.Queue, forged);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("ownershipSnapshot"));
            });
        }

        [Test]
        public void ForceDrop_SnapshotTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[0]);

                FieldInfo field = GetSnapshotType().GetField("_testRunId", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(field, Is.Not.Null);
                field.SetValue(snapshot, 99L);

                Exception ex = ForceDropException(scope.Registry, scope.Queue, snapshot);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("ownershipSnapshot"));
            });
        }

        // ---- ForceDrop success ----

        [Test]
        public void ForceDrop_NoPending_EmptySet()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                // One staged and one normally dropped draft; no pending remain.
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                CommitDraft(scope.Registry, scope.Run, 1);
                Assert.That(TryMarkStaged(scope.Registry, MakeRequest(1), scope.Store, entry), Is.True);

                CommitDraft(scope.Registry, scope.Run, 2);
                MarkDropped(scope.Registry, MakeRequest(2), CaptureFrameDropReason.PngEncodeFailed);

                object snapshot = SetupForFreeze(scope, new long[0]);
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(0));
                Assert.That((bool)GetProperty(set, "IsValid"), Is.True);
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
            });
        }

        [Test]
        public void ForceDrop_SinglePending_Reason9()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 7 });
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(1));
                Assert.That(SetGetCaptureFrameId(set, 0), Is.EqualTo(7));

                // Draft 7 is now Dropped with reason 9 and emission None.
                Assert.That((int)GetEntryField(scope.Registry, 0, "Status"), Is.EqualTo(2)); // Dropped
                Assert.That((int)GetEntryField(scope.Registry, 0, "DropReason"), Is.EqualTo(9)); // FreezeDrainTimeout
                Assert.That((int)GetEntryField(scope.Registry, 0, "EmissionState"), Is.EqualTo(0)); // None

                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void ForceDrop_MaxPending_AllReason9_AllSlotsFreed()
        {
            Scope scope = NewScope(maxDraftPerRun: 8);
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(8));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(8));

                for (int i = 0; i < 8; i++)
                {
                    Assert.That((int)GetEntryField(scope.Registry, i, "Status"), Is.EqualTo(2)); // Dropped
                    Assert.That((int)GetEntryField(scope.Registry, i, "DropReason"), Is.EqualTo(9));
                    Assert.That(SetGetCaptureFrameId(set, i), Is.EqualTo(i + 1));
                }
            });
        }

        [Test]
        public void ForceDrop_MixedStagedDropped_ExistingUnchanged()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                // Draft 1 staged, draft 2 normally dropped, draft 3 pending.
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                CommitDraft(scope.Registry, scope.Run, 1);
                Assert.That(TryMarkStaged(scope.Registry, MakeRequest(1), scope.Store, entry), Is.True);

                CommitDraft(scope.Registry, scope.Run, 2);
                MarkDropped(scope.Registry, MakeRequest(2), CaptureFrameDropReason.PngEncodeFailed);

                object snapshot = SetupForFreeze(scope, new long[] { 3 });
                string beforeEntries = DescribeRegistry(scope.Registry);

                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                // Staged entry stays staged (reason None, emission None).
                Assert.That((int)GetEntryField(scope.Registry, 0, "Status"), Is.EqualTo(1)); // Staged
                Assert.That((int)GetEntryField(scope.Registry, 0, "DropReason"), Is.EqualTo(0));
                Assert.That((int)GetEntryField(scope.Registry, 0, "EmissionState"), Is.EqualTo(0));

                // Normally dropped entry stays dropped with reason 6, emission Pending.
                Assert.That((int)GetEntryField(scope.Registry, 1, "Status"), Is.EqualTo(2)); // Dropped
                Assert.That((int)GetEntryField(scope.Registry, 1, "DropReason"), Is.EqualTo(6));
                Assert.That((int)GetEntryField(scope.Registry, 1, "EmissionState"), Is.EqualTo(1)); // Pending

                // The pending entry became reason 9.
                Assert.That((int)GetEntryField(scope.Registry, 2, "DropReason"), Is.EqualTo(9));
                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(1));
                Assert.That(SetGetCaptureFrameId(set, 0), Is.EqualTo(3));
            });
        }

        [Test]
        public void ForceDrop_SecondCall_SameInstance()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1, 2 });
                object first = ForceDrop(scope.Registry, scope.Queue, snapshot);
                object second = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That(ReferenceEquals(first, second), Is.True);
                Assert.That(ReferenceEquals(GetIssuedForcedDropSet(scope.Registry), first), Is.True);
            });
        }

        [Test]
        public void TryConsumeDropTrace_Reason9_False()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 5 });
                ForceDrop(scope.Registry, scope.Queue, snapshot);

                MethodInfo method = GetRegistryType().GetMethod("TryConsumeDropTrace", BindingFlags.NonPublic | BindingFlags.Instance);
                object[] args = new object[] { 5L, null };
                Assert.That((bool)method.Invoke(scope.Registry, args), Is.False);
            });
        }

        [Test]
        public void ForceDrop_DoesNotEnqueueTrace()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                using (TraceLogger logger = new TraceLogger(16))
                {
                    object snapshot = SetupForFreeze(scope, new long[] { 1 });
                    ForceDrop(scope.Registry, scope.Queue, snapshot);

                    Assert.That(logger.Drain(), Is.EqualTo(0));
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                    Assert.That(logger.TotalWritten, Is.EqualTo(0));
                }
            });
        }

        // ---- Precondition violations: each rejects and leaves state unchanged ----

        [Test]
        public void ForceDrop_ReservationRemaining_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitDraft(scope.Registry, scope.Run, 1);
                object reservation, rejectKind;
                Assert.That(TryReserve(scope.Registry, out reservation, out rejectKind), Is.True); // outstanding reservation

                object snapshot = SetupForFreeze(scope, new long[0]);
                AssertFreezeRejectedUnchanged(scope, snapshot, () => { });
            });
        }

        [Test]
        public void ForceDrop_PendingCountMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetCount(scope.Registry, "_pendingCount", 0));
            });
        }

        [Test]
        public void ForceDrop_PendingNoSlot_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () =>
                {
                    SetSlotState(scope.Registry, 0, 0); // Free
                    SetSlotEntryIndex(scope.Registry, 0, -1);
                });
            });
        }

        [Test]
        public void ForceDrop_PendingMultipleSlots_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1, 2 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () =>
                {
                    // Slot 1 now also points at entry 0 (draft 1).
                    SetSlotState(scope.Registry, 1, 2); // Occupied
                    SetSlotEntryIndex(scope.Registry, 1, 0);
                });
            });
        }

        [Test]
        public void ForceDrop_OccupiedSlotOutOfRangeIndex_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetSlotEntryIndex(scope.Registry, 0, 999));
            });
        }

        [Test]
        public void ForceDrop_ReservedSlotRemaining_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetSlotState(scope.Registry, 1, 1)); // Reserved
            });
        }

        [Test]
        public void ForceDrop_UndefinedSlotState_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetSlotState(scope.Registry, 1, 99)); // undefined state
            });
        }

        [Test]
        public void ForceDrop_FreeSlotStaleEntryIndex_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetSlotEntryIndex(scope.Registry, 1, 5)); // Free slot with stale index
            });
        }

        [Test]
        public void ForceDrop_PendingDropReasonCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "DropReason", 6));
            });
        }

        [Test]
        public void ForceDrop_PendingEmissionCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "EmissionState", 1));
            });
        }

        [Test]
        public void ForceDrop_StagedDropReasonCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                CommitDraft(scope.Registry, scope.Run, 1);
                Assert.That(TryMarkStaged(scope.Registry, MakeRequest(1), scope.Store, entry), Is.True);

                object snapshot = SetupForFreeze(scope, new long[0]);
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "DropReason", 6));
            });
        }

        [Test]
        public void ForceDrop_StagedEmissionCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                CommitDraft(scope.Registry, scope.Run, 1);
                Assert.That(TryMarkStaged(scope.Registry, MakeRequest(1), scope.Store, entry), Is.True);

                object snapshot = SetupForFreeze(scope, new long[0]);
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "EmissionState", 1));
            });
        }

        [Test]
        public void ForceDrop_DroppedReasonCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitDraft(scope.Registry, scope.Run, 1);
                MarkDropped(scope.Registry, MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed);

                object snapshot = SetupForFreeze(scope, new long[0]);
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "DropReason", 9));
            });
        }

        [Test]
        public void ForceDrop_DroppedEmissionCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitDraft(scope.Registry, scope.Run, 1);
                MarkDropped(scope.Registry, MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed);

                object snapshot = SetupForFreeze(scope, new long[0]);
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "EmissionState", 0));
            });
        }

        [Test]
        public void ForceDrop_UndefinedStatus_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });
                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryEnumField(scope.Registry, 0, "Status", 99));
            });
        }

        [Test]
        public void ForceDrop_EntryTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });

                object otherRun = MakeRun(testRunId: 2, captureProfileId: 5);
                object otherDraft = MakeDraft(otherRun, MakeRequest(5, testRunId: 2));

                AssertFreezeRejectedUnchanged(scope, snapshot, () => SetEntryField(scope.Registry, 0, "Draft", otherDraft));
            });
        }

        [Test]
        public void ForceDrop_NonPositiveCaptureFrameId_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1 });

                AssertFreezeRejectedUnchanged(scope, snapshot, () =>
                {
                    object forged = FormatterServices.GetUninitializedObject(GetDraftType());
                    FieldInfo requestField = GetDraftType().GetField("<Request>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.That(requestField, Is.Not.Null);
                    requestField.SetValue(forged, MakeRequest(0, testRunId: 1)); // TestRunId 1, CaptureFrameId 0
                    SetEntryField(scope.Registry, 0, "Draft", forged);
                });
            });
        }

        [Test]
        public void ForceDrop_CaptureFrameIdNotIncreasing_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object snapshot = SetupForFreeze(scope, new long[] { 1, 2 });

                object draft1 = GetEntryField(scope.Registry, 0, "Draft");
                object draft2 = GetEntryField(scope.Registry, 1, "Draft");

                AssertFreezeRejectedUnchanged(scope, snapshot, () =>
                {
                    // Swap the drafts so IDs are [2, 1] instead of [1, 2].
                    SetEntryField(scope.Registry, 0, "Draft", draft2);
                    SetEntryField(scope.Registry, 1, "Draft", draft1);
                });
            });
        }

        // ---- Type contracts ----

        [Test]
        public void Registry_HoldsNoQueueSnapshotLoggerObserver_NotDisposable()
        {
            Type type = GetRegistryType();
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(GetQueueType()), field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(GetSnapshotType()), field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)), field.Name);
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameTraceObserver)), field.Name);
            }

            FieldInfo setField = type.GetField("_issuedForcedDropFrameIdSet", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(setField, Is.Not.Null);
            Assert.That(setField.FieldType, Is.EqualTo(GetSetType()));
        }

        [Test]
        public void ForceDrop_LargeCapacity_AllPending()
        {
            const int Capacity = 512;
            Scope scope = NewScope(maxDraftPerRun: Capacity);
            RunBody(scope, () =>
            {
                long[] ids = new long[Capacity];
                for (int i = 0; i < Capacity; i++)
                {
                    ids[i] = i + 1;
                }

                object snapshot = SetupForFreeze(scope, ids);
                object set = ForceDrop(scope.Registry, scope.Queue, snapshot);

                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(Capacity));
                Assert.That(SetGetCaptureFrameId(set, 0), Is.EqualTo(1));
                Assert.That(SetGetCaptureFrameId(set, Capacity - 1), Is.EqualTo(Capacity));
                Assert.That((bool)GetProperty(set, "IsValid"), Is.True);
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
            });
        }
    }
}
