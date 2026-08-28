using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class FreezeTerminalTraceBufferBuilderTests
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

        private static Type GetCheckpointType() => GetTypeFromAssembly("FreezeTerminalCheckpoint");

        private static Type GetBufferType() => GetTypeFromAssembly("FreezeTerminalTraceBuffer");

        private static Type GetBuilderType() => GetTypeFromAssembly("FreezeTerminalTraceBufferBuilder");

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

        private static bool IsNegativeZero(double value) => BitConverter.DoubleToInt64Bits(value) == long.MinValue;

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

        private static CaptureFrameRequest MakeDistinctRequest(long captureFrameId, long testRunId)
        {
            // Twelve distinct, non-zero correlation values.
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                2, 3, 4, 5, captureFrameId, 6, testRunId, 7, 8, 9, 10u, 11);
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

        private static object MakeCheckpoint(long timestamp, long frameId, long fixedStepId, int threadId, long testRunId)
        {
            ConstructorInfo ctor = GetCheckpointType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(long), typeof(long), typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { timestamp, frameId, fixedStepId, threadId, testRunId });
        }

        private static object MakeEntry(long captureFrameId, long testRunId, int pngLength)
        {
            ConstructorInfo ctor = GetEntryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(long), typeof(NativeArray<byte>), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null);

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

        // ---- Registry / queue / builder operation helpers ----

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

        private static void RegisterPendingDraft(object queue, object draft)
        {
            MethodInfo method = GetQueueType().GetMethod("RegisterPendingDraft", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(queue, new object[] { draft });
        }

        private static void CommitAndRegister(object queue, object registry, object run, CaptureFrameRequest request)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, request);
            Commit(registry, reservation, draft);
            RegisterPendingDraft(queue, draft);
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

        private static object CreateOwnershipSnapshot(object queue, int producerRetained)
        {
            MethodInfo method = GetQueueType().GetMethod("CreateOwnershipSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(queue, new object[] { producerRetained });
        }

        private static object ForceDrop(object registry, object queue, object snapshot)
        {
            MethodInfo method = GetRegistryType().GetMethod("ForceDropPendingForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(registry, new object[] { queue, snapshot });
        }

        private static object CreateBuilder(object registry)
        {
            ConstructorInfo ctor = GetBuilderType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { registry });
        }

        private static object Build(object builder, object set, object checkpoint)
        {
            MethodInfo method = GetBuilderType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(builder, new object[] { set, checkpoint });
        }

        private static Exception BuildException(object builder, object set, object checkpoint)
        {
            try
            {
                Build(builder, set, checkpoint);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static TraceEvent GetEvent(object buffer, int index)
        {
            MethodInfo method = GetBufferType().GetMethod("GetEvent", BindingFlags.Public | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (TraceEvent)method.Invoke(buffer, new object[] { index });
        }

        private static Exception GetEventException(object buffer, int index)
        {
            try
            {
                GetEvent(buffer, index);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
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

        private static string DescribeRegistry(object registry)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("E=").Append((int)GetProperty(registry, "EntryCount"));
            sb.Append(" P=").Append((int)GetProperty(registry, "PendingCount"));
            sb.Append(" R=").Append((int)GetProperty(registry, "ReservationCount"));
            sb.Append(' ');

            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Array entries = (Array)entriesField.GetValue(registry);
            int entryCount = (int)GetProperty(registry, "EntryCount");
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

            return sb.ToString();
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

        // ---- Setup helpers ----

        /// <summary>
        /// Freezes the given pending draft IDs (via force-drop) and returns the
        /// issued forced-drop set.
        /// </summary>
        private static object SetupFrozen(Scope scope, long[] pendingIds)
        {
            for (int i = 0; i < pendingIds.Length; i++)
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, MakeRequest(pendingIds[i]));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(pendingIds[i]), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
            }

            BeginProducerDrain(scope.Queue);
            CloseAfterProducerJoin(scope.Queue);

            for (int i = 0; i < pendingIds.Length; i++)
            {
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);
            }

            object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
            return ForceDrop(scope.Registry, scope.Queue, snapshot);
        }

        /// <summary>
        /// Freezes one pending draft whose request carries twelve distinct
        /// non-zero correlation values, and returns its forced-drop set.
        /// </summary>
        private static object SetupDistinctFrozen(Scope scope, long captureFrameId)
        {
            CaptureFrameRequest request = MakeDistinctRequest(captureFrameId, scope.TestRunId);
            CommitAndRegister(scope.Queue, scope.Registry, scope.Run, request);
            Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(request, CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

            BeginProducerDrain(scope.Queue);
            CloseAfterProducerJoin(scope.Queue);
            object dequeued;
            Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);

            object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
            return ForceDrop(scope.Registry, scope.Queue, snapshot);
        }

        private static void AssertForcedDropEvent(TraceEvent e, long captureFrameId)
        {
            // Context fields (distinct values from MakeDistinctRequest).
            Assert.That(e.Timestamp, Is.EqualTo(2));
            Assert.That(e.FrameId, Is.EqualTo(3));
            Assert.That(e.FixedStepId, Is.EqualTo(4));
            Assert.That(e.ThreadId, Is.EqualTo(5));
            Assert.That(e.SlashId, Is.EqualTo(7));
            Assert.That(e.SlashGeneration, Is.EqualTo(0u));
            Assert.That(e.FrontEdgeId, Is.EqualTo(8));
            Assert.That(e.ObjectId, Is.EqualTo(9));
            Assert.That(e.ObjectGeneration, Is.EqualTo(10u));
            Assert.That(e.MobId, Is.EqualTo(0L));
            Assert.That(e.PlanGeneration, Is.EqualTo(0u));
            Assert.That(e.TaskId, Is.EqualTo(11));
            Assert.That(e.CaptureFrameId, Is.EqualTo(captureFrameId));
            Assert.That(e.OpenXRFrameId, Is.EqualTo(6));
            Assert.That(e.TestRunId, Is.EqualTo(1));

            Assert.That(e.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
            Assert.That(e.TaskType, Is.EqualTo(TraceTaskType.None));
            Assert.That(e.FromState, Is.EqualTo(0)); // Pending
            Assert.That(e.ToState, Is.EqualTo(2)); // Dropped
            Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
            Assert.That(e.Value0, Is.EqualTo(0.0));
            Assert.That(e.Value1, Is.EqualTo(9.0)); // FreezeDrainTimeout
        }

        private static void AssertEventsIdentical(TraceEvent a, TraceEvent b)
        {
            Assert.That(a.Timestamp, Is.EqualTo(b.Timestamp));
            Assert.That(a.FrameId, Is.EqualTo(b.FrameId));
            Assert.That(a.FixedStepId, Is.EqualTo(b.FixedStepId));
            Assert.That(a.ThreadId, Is.EqualTo(b.ThreadId));
            Assert.That(a.SlashId, Is.EqualTo(b.SlashId));
            Assert.That(a.SlashGeneration, Is.EqualTo(b.SlashGeneration));
            Assert.That(a.FrontEdgeId, Is.EqualTo(b.FrontEdgeId));
            Assert.That(a.ObjectId, Is.EqualTo(b.ObjectId));
            Assert.That(a.ObjectGeneration, Is.EqualTo(b.ObjectGeneration));
            Assert.That(a.MobId, Is.EqualTo(b.MobId));
            Assert.That(a.PlanGeneration, Is.EqualTo(b.PlanGeneration));
            Assert.That(a.TaskId, Is.EqualTo(b.TaskId));
            Assert.That(a.CaptureFrameId, Is.EqualTo(b.CaptureFrameId));
            Assert.That(a.OpenXRFrameId, Is.EqualTo(b.OpenXRFrameId));
            Assert.That(a.TestRunId, Is.EqualTo(b.TestRunId));
            Assert.That(a.EventType, Is.EqualTo(b.EventType));
            Assert.That(a.TaskType, Is.EqualTo(b.TaskType));
            Assert.That(a.FromState, Is.EqualTo(b.FromState));
            Assert.That(a.ToState, Is.EqualTo(b.ToState));
            Assert.That(a.Reason, Is.EqualTo(b.Reason));
            Assert.That(BitConverter.DoubleToInt64Bits(a.Value0), Is.EqualTo(BitConverter.DoubleToInt64Bits(b.Value0)));
            Assert.That(BitConverter.DoubleToInt64Bits(a.Value1), Is.EqualTo(BitConverter.DoubleToInt64Bits(b.Value1)));
        }

        // ---- Builder dependency / set / checkpoint validation ----

        [Test]
        public void Builder_NullRegistry_Rejected()
        {
            ConstructorInfo ctor = GetBuilderType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRegistryType() }, null);
            try
            {
                ctor.Invoke(new object[] { null });
                Assert.Fail("Expected ArgumentNullException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("draftRegistry"));
            }
        }

        [Test]
        public void Build_NullSet_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                Exception ex = BuildException(builder, null, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void Build_OtherRegistrySet_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRegistry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object forged = CreateSetRaw(otherRegistry, 1, new long[0]);
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                Exception ex = BuildException(builder, forged, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void Build_ForgedNotIssuedSet_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object forged = CreateSetRaw(scope.Registry, 1, new long[0]);
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                Exception ex = BuildException(builder, forged, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void Build_InvalidCheckpoint_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[0]);
                object builder = CreateBuilder(scope.Registry);

                // default checkpoint is invalid (thread/run IDs zero)
                object defaultCheckpoint = Activator.CreateInstance(GetCheckpointType());

                Exception ex = BuildException(builder, set, defaultCheckpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("checkpoint"));
            });
        }

        [Test]
        public void Build_CheckpointTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[0]);
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 42); // wrong run id

                Exception ex = BuildException(builder, set, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("checkpoint"));
            });
        }

        // ---- Direct buffer constructor validation (empty set exercises it) ----

        [Test]
        public void BufferCtor_EmptySetFromOtherRegistry_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRegistry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object forged = CreateSetRaw(otherRegistry, 1, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                Exception ex = BufferCtorException(scope.Registry, forged, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void BufferCtor_EmptySetForgedNotIssued_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object forged = CreateSetRaw(scope.Registry, 1, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                Exception ex = BufferCtorException(scope.Registry, forged, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void BufferCtor_EmptySetCheckpointTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 42);

                Exception ex = BufferCtorException(scope.Registry, set, checkpoint);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("checkpoint"));
            });
        }

        [Test]
        public void BufferCtor_EmptySetValid_Succeeds()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = CreateBufferDirect(scope.Registry, set, checkpoint);

                Assert.That((int)GetProperty(buffer, "Count"), Is.EqualTo(1));
                Assert.That((int)GetProperty(buffer, "ForcedDropCount"), Is.EqualTo(0));
                Assert.That(GetEvent(buffer, 0).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
            });
        }

        // ---- Forced-drop zero: ring only ----

        [Test]
        public void Build_ForcedDropZero_RingOnly()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[0]);
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);

                Assert.That((int)GetProperty(buffer, "Count"), Is.EqualTo(1));
                Assert.That((int)GetProperty(buffer, "ForcedDropCount"), Is.EqualTo(0));
                Assert.That((long)GetProperty(buffer, "TestRunId"), Is.EqualTo(1));

                TraceEvent ring = GetEvent(buffer, 0);
                Assert.That(ring.EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
                Assert.That(ring.Value0, Is.EqualTo(0.0)); // zero forced drops
            });
        }

        [Test]
        public void Build_ForcedDropOneAndMultiple_CountMatches()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object set1 = SetupFrozen(scope, new long[] { 10 });
                object buffer1 = Build(builder, set1, checkpoint);
                Assert.That((int)GetProperty(buffer1, "Count"), Is.EqualTo(2));
                Assert.That((int)GetProperty(buffer1, "ForcedDropCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void Build_ForcedDropMultiple_StrictlyIncreasingOrder()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 10, 20, 30 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);

                Assert.That((int)GetProperty(buffer, "Count"), Is.EqualTo(4));
                Assert.That((int)GetProperty(buffer, "ForcedDropCount"), Is.EqualTo(3));

                // Drop events are in the set's strictly increasing order.
                Assert.That(GetEvent(buffer, 0).CaptureFrameId, Is.EqualTo(10));
                Assert.That(GetEvent(buffer, 1).CaptureFrameId, Is.EqualTo(20));
                Assert.That(GetEvent(buffer, 2).CaptureFrameId, Is.EqualTo(30));
                Assert.That(GetEvent(buffer, 3).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
            });
        }

        [Test]
        public void Build_ForcedDropEvent_22Fields()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupDistinctFrozen(scope, 100);
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);

                TraceEvent drop = GetEvent(buffer, 0);
                AssertForcedDropEvent(drop, 100);

                Assert.That(IsNegativeZero(drop.Value0), Is.False); // +0.0
                Assert.That(BitConverter.DoubleToInt64Bits(drop.Value0), Is.EqualTo(0L)); // positive zero bits
            });
        }

        [Test]
        public void Build_ForcedDrop_OnlyGenerationMobPlanZero()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupDistinctFrozen(scope, 100);
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);
                TraceEvent drop = GetEvent(buffer, 0);

                Assert.That(drop.SlashGeneration, Is.EqualTo(0u));
                Assert.That(drop.MobId, Is.EqualTo(0L));
                Assert.That(drop.PlanGeneration, Is.EqualTo(0u));

                // All context-derived fields are non-zero (distinct values).
                Assert.That(drop.Timestamp, Is.Not.EqualTo(0));
                Assert.That(drop.FrameId, Is.Not.EqualTo(0));
                Assert.That(drop.FixedStepId, Is.Not.EqualTo(0));
                Assert.That(drop.ThreadId, Is.Not.EqualTo(0));
                Assert.That(drop.SlashId, Is.Not.EqualTo(0));
                Assert.That(drop.FrontEdgeId, Is.Not.EqualTo(0));
                Assert.That(drop.ObjectId, Is.Not.EqualTo(0));
                Assert.That(drop.ObjectGeneration, Is.Not.EqualTo(0u));
                Assert.That(drop.TaskId, Is.Not.EqualTo(0));
                Assert.That(drop.CaptureFrameId, Is.Not.EqualTo(0));
                Assert.That(drop.OpenXRFrameId, Is.Not.EqualTo(0));
                Assert.That(drop.TestRunId, Is.Not.EqualTo(0));
            });
        }

        [Test]
        public void Build_RingEvent_22Fields()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1, 2, 3 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);
                TraceEvent ring = GetEvent(buffer, 3); // last

                Assert.That(ring.Timestamp, Is.EqualTo(200));
                Assert.That(ring.FrameId, Is.EqualTo(201));
                Assert.That(ring.FixedStepId, Is.EqualTo(202));
                Assert.That(ring.ThreadId, Is.EqualTo(203));
                Assert.That(ring.SlashId, Is.EqualTo(0));
                Assert.That(ring.SlashGeneration, Is.EqualTo(0u));
                Assert.That(ring.FrontEdgeId, Is.EqualTo(0));
                Assert.That(ring.ObjectId, Is.EqualTo(0));
                Assert.That(ring.ObjectGeneration, Is.EqualTo(0u));
                Assert.That(ring.MobId, Is.EqualTo(0L));
                Assert.That(ring.PlanGeneration, Is.EqualTo(0u));
                Assert.That(ring.TaskId, Is.EqualTo(0));
                Assert.That(ring.CaptureFrameId, Is.EqualTo(0));
                Assert.That(ring.OpenXRFrameId, Is.EqualTo(0));
                Assert.That(ring.TestRunId, Is.EqualTo(1));

                Assert.That(ring.EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
                Assert.That(ring.TaskType, Is.EqualTo(TraceTaskType.None));
                Assert.That(ring.FromState, Is.EqualTo(3)); // AwaitingFreezeTerminal
                Assert.That(ring.ToState, Is.EqualTo(2)); // Frozen
                Assert.That(ring.Reason, Is.EqualTo(TraceReason.None));
                Assert.That(ring.Value0, Is.EqualTo(3.0)); // forcedDropCount
                Assert.That(ring.Value1, Is.EqualTo(0.0));
                Assert.That(IsNegativeZero(ring.Value1), Is.False);
            });
        }

        [Test]
        public void Build_RingAtTailOnly()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1, 2, 3 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);
                int count = (int)GetProperty(buffer, "Count");

                for (int i = 0; i < count - 1; i++)
                {
                    Assert.That(GetEvent(buffer, i).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                }

                Assert.That(GetEvent(buffer, count - 1).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
            });
        }

        [Test]
        public void Build_Value0Value1_NotNegativeZero()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1, 2 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer = Build(builder, set, checkpoint);
                int count = (int)GetProperty(buffer, "Count");

                for (int i = 0; i < count; i++)
                {
                    TraceEvent e = GetEvent(buffer, i);
                    Assert.That(IsNegativeZero(e.Value0), Is.False, "Value0 at " + i);
                    Assert.That(IsNegativeZero(e.Value1), Is.False, "Value1 at " + i);
                }
            });
        }

        [Test]
        public void GetEvent_OutOfRange_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);
                object buffer = Build(builder, set, checkpoint);

                int count = (int)GetProperty(buffer, "Count");
                foreach (int index in new[] { -1, count, count + 5 })
                {
                    Exception ex = GetEventException(buffer, index);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("index"));
                }
            });
        }

        [Test]
        public void GetEvent_ReturnsValueCopy_NoArrayExposure()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);
                object buffer = Build(builder, set, checkpoint);

                TraceEvent original = GetEvent(buffer, 0);
                TraceEvent modified = original;
                modified.CaptureFrameId = 999999;
                modified.EventType = TraceEventType.CaptureRingFrozen;

                Assert.That(GetEvent(buffer, 0).CaptureFrameId, Is.EqualTo(original.CaptureFrameId));
                Assert.That(GetEvent(buffer, 0).EventType, Is.EqualTo(original.EventType));
            });
        }

        [Test]
        public void Buffer_IsSoleArrayAllocator_NoExternalArrayParameter()
        {
            ConstructorInfo[] ctors = GetBufferType().GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(ctors.Length, Is.EqualTo(1));

            ParameterInfo[] parameters = ctors[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(3));
            foreach (ParameterInfo parameter in parameters)
            {
                Assert.That(parameter.ParameterType.IsArray, Is.False, parameter.Name + " must not be an array parameter.");
            }
        }

        [Test]
        public void Build_Twice_Identical()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1, 2, 3 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object buffer1 = Build(builder, set, checkpoint);
                object buffer2 = Build(builder, set, checkpoint);

                Assert.That(ReferenceEquals(buffer1, buffer2), Is.False);

                int count = (int)GetProperty(buffer1, "Count");
                Assert.That((int)GetProperty(buffer2, "Count"), Is.EqualTo(count));
                for (int i = 0; i < count; i++)
                {
                    AssertEventsIdentical(GetEvent(buffer1, i), GetEvent(buffer2, i));
                }
            });
        }

        // ---- Entry corruption: build must reject ----

        [Test]
        public void Build_EntryStatusCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                SetEntryEnumField(scope.Registry, 0, "Status", 1); // Staged
                Assert.That(BuildException(builder, set, checkpoint), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Build_EntryReasonCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                SetEntryEnumField(scope.Registry, 0, "DropReason", 6);
                Assert.That(BuildException(builder, set, checkpoint), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Build_EntryEmissionCorrupt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                SetEntryEnumField(scope.Registry, 0, "EmissionState", 1);
                Assert.That(BuildException(builder, set, checkpoint), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Build_EntryTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 100 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object otherRun = MakeRun(testRunId: 2, captureProfileId: 5);
                object otherDraft = MakeDraft(otherRun, MakeRequest(100, testRunId: 2));
                SetEntryField(scope.Registry, 0, "Draft", otherDraft);

                Assert.That(BuildException(builder, set, checkpoint), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Build_EntryCaptureFrameIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 100 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                object wrongDraft = MakeDraft(scope.Run, MakeRequest(999, testRunId: 1));
                SetEntryField(scope.Registry, 0, "Draft", wrongDraft);

                Assert.That(BuildException(builder, set, checkpoint), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Build_LeavesRegistrySetCheckpointUnchanged()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = SetupFrozen(scope, new long[] { 1, 2 });
                object builder = CreateBuilder(scope.Registry);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 1);

                string before = DescribeRegistry(scope.Registry);

                object buffer = Build(builder, set, checkpoint);

                Assert.That(DescribeRegistry(scope.Registry), Is.EqualTo(before));
                Assert.That((int)GetProperty(set, "Count"), Is.EqualTo(2));
                Assert.That(ReferenceEquals(GetProperty(set, "IssuedBy"), scope.Registry), Is.True);
                Assert.That((bool)GetProperty(checkpoint, "IsValid"), Is.True);
            });
        }

        // ---- Type contracts ----

        [Test]
        public void Builder_HoldsOnlyRegistry()
        {
            FieldInfo[] fields = GetBuilderType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(GetRegistryType()));
        }

        [Test]
        public void Buffer_HoldsOnlySetCheckpointArrayAndPrimitives()
        {
            FieldInfo[] fields = GetBufferType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(6));

            foreach (FieldInfo field in fields)
            {
                Assert.That(
                    field.FieldType == GetSetType()
                    || field.FieldType == GetCheckpointType()
                    || field.FieldType == typeof(TraceEvent[])
                    || field.FieldType == typeof(long)
                    || field.FieldType == typeof(int),
                    Is.True,
                    "Unexpected buffer field type: " + field.FieldType.Name);
            }
        }

        [Test]
        public void Types_NotDisposable_NoStaticMutableState()
        {
            foreach (Type type in new[] { GetBufferType(), GetBuilderType() })
            {
                Assert.That(type.IsSealed, Is.True);
                Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
                Assert.That(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), Is.Empty);
            }
        }

        // ---- Set raw-construction helper (for forged-set tests) ----

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

        private static object CreateBufferDirect(object registry, object set, object checkpoint)
        {
            ConstructorInfo ctor = GetBufferType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), GetSetType(), GetCheckpointType().MakeByRefType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { registry, set, checkpoint });
        }

        private static Exception BufferCtorException(object registry, object set, object checkpoint)
        {
            try
            {
                CreateBufferDirect(registry, set, checkpoint);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }
    }
}
