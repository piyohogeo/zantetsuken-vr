using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftRecordFinalizerTests
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

        private static Type GetQueueType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntentQueue");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

        private static Type GetStoreType() => GetTypeFromAssembly("CaptureFramePngStagingStore");

        private static Type GetStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

        private static Type GetEmissionStateType() => GetTypeFromAssembly("DraftDropTraceEmissionState");

        private static Type GetSetType() => GetTypeFromAssembly("ForcedDropFrameIdSet");

        private static Type GetFinalizerType() => GetTypeFromAssembly("CaptureFrameDraftRecordFinalizer");

        private static Type GetFinalizationType() => GetTypeFromAssembly("CaptureFrameDraftRecordFinalization");

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

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureFrameDraftRecordFinalizerTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
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

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId = 1, CaptureEye eye = CaptureEye.Left)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, eye, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static object MakeDraftWithPoses(
            object run,
            CaptureFrameRequest request,
            int commitPathId,
            CapturePoseSample headPose,
            CapturePoseSample leftControllerPose,
            CapturePoseSample rightControllerPose)
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
                headPose,
                leftControllerPose,
                rightControllerPose,
                commitPathId
            });
        }

        private static object MakeDraft(object run, CaptureFrameRequest request, int commitPathId = 1)
        {
            return MakeDraftWithPoses(
                run, request, commitPathId,
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity),
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity),
                new CapturePoseSample(new Vector3(0f, 0f, 0f), Quaternion.identity));
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

        private static TraceRunManifest MakeManifest(long testRunId = 1, string buildId = "build-1", string sceneId = "scene-1", long randomSeed = 12345)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId, 1000, buildId, "6000.3.22f1", ValidSha256, sceneId, randomSeed, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));

            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                logger.Enqueue(new TraceEvent { Timestamp = 1, EventType = TraceEventType.None });
                Assert.That(recorder.TryTrigger(), Is.True);
                return TraceRunManifest.Create(recorder.CreateFrozenSnapshot(), context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static CaptureRunReference MakeReference(
            long testRunId = 1,
            long testCaseId = 100,
            int captureProfileId = 5,
            string buildId = "build-1",
            string sceneId = "scene-1",
            long randomSeed = 12345)
        {
            TraceRunManifest manifest = MakeManifest(testRunId, buildId, sceneId, randomSeed);
            return new CaptureRunReference(manifest, testCaseId, captureProfileId, TraceRunManifestCodec.ComputeContentSha256(manifest));
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

        private static object CommitDraft(object registry, object run, CaptureFrameRequest request, int commitPathId = 1)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, request, commitPathId);
            Commit(registry, reservation, draft);
            return draft;
        }

        private static bool TryMarkStaged(object registry, CaptureFrameRequest request, object store, object entry)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryMarkStaged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(registry, new object[] { request, store, entry });
        }

        private static void MarkDropped(object registry, CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetRegistryType().GetMethod("MarkDropped", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { request, reason });
        }

        private static bool TryConsumeDropTrace(object registry, long captureFrameId)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryConsumeDropTrace", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { captureFrameId, null };
            return (bool)method.Invoke(registry, args);
        }

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

        private static object ForceDrop(object registry, object queue, object snapshot)
        {
            MethodInfo method = GetRegistryType().GetMethod("ForceDropPendingForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(registry, new object[] { queue, snapshot });
        }

        private static object GetIssuedForcedDropSet(object registry)
        {
            return GetProperty(registry, "IssuedForcedDropFrameIdSet");
        }

        private static int Count(object registry, string name) => (int)GetProperty(registry, name);

        // ---- Corruption helpers ----

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

        private static void SetCountField(object registry, string fieldName, int value)
        {
            FieldInfo field = GetRegistryType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(registry, value);
        }

        private static void SetStoreIntField(object store, string fieldName, int value)
        {
            FieldInfo field = GetStoreType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(store, value);
        }

        private static void SetStoreLongField(object store, string fieldName, long value)
        {
            FieldInfo field = GetStoreType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(store, value);
        }

        private static void SetIssuedForcedDropSet(object registry, object set)
        {
            FieldInfo field = GetRegistryType().GetField("_issuedForcedDropFrameIdSet", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(registry, set);
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

        private static void SetDraftCommitPathId(object draft, int value)
        {
            FieldInfo field = GetDraftType().GetField("<CommitPathId>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "CommitPathId backing field not found.");
            field.SetValue(draft, value);
        }

        private static void SetEntryLongField(object entry, string fieldName, long value)
        {
            FieldInfo field = GetEntryType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(entry, value);
        }

        // ---- Finalizer / finalization helpers ----

        private static object CreateFinalizer(object registry, object store)
        {
            ConstructorInfo ctor = GetFinalizerType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), GetStoreType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { registry, store });
        }

        private static Exception CreateFinalizerException(object registry, object store)
        {
            try
            {
                CreateFinalizer(registry, store);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object CreateFinalization(object finalizer, CaptureRunReference finalRun)
        {
            MethodInfo method = GetFinalizerType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(finalizer, new object[] { finalRun });
        }

        private static Exception CreateFinalizationException(object finalizer, CaptureRunReference finalRun)
        {
            try
            {
                CreateFinalization(finalizer, finalRun);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static CaptureRunReference GetRun(object finalization) => (CaptureRunReference)GetProperty(finalization, "Run");

        private static int GetRecordCount(object finalization) => (int)GetProperty(finalization, "RecordCount");

        private static int GetDroppedCount(object finalization) => (int)GetProperty(finalization, "DroppedCount");

        private static CaptureFrameRecord GetRecord(object finalization, int index)
        {
            MethodInfo method = GetFinalizationType().GetMethod("GetRecord", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (CaptureFrameRecord)method.Invoke(finalization, new object[] { index });
        }

        private static Exception GetRecordException(object finalization, int index)
        {
            try
            {
                GetRecord(finalization, index);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object GetStagingEntry(object finalization, int index)
        {
            MethodInfo method = GetFinalizationType().GetMethod("GetStagingEntry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(finalization, new object[] { index });
        }

        private static Exception GetStagingEntryException(object finalization, int index)
        {
            try
            {
                GetStagingEntry(finalization, index);
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
            public object Finalizer;
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
            scope.Finalizer = CreateFinalizer(scope.Registry, scope.Store);
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

        // ---- Lifecycle helpers ----

        private static object StageDraft(Scope scope, long captureFrameId, int pngLength = 16, int commitPathId = 1)
        {
            CaptureFrameRequest request = MakeRequest(captureFrameId, scope.TestRunId);
            object draft = CommitDraft(scope.Registry, scope.Run, request, commitPathId);
            object entry = MakeEntryTracked(scope, captureFrameId, scope.TestRunId, pngLength);
            Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);
            return draft;
        }

        private static void DropDraftNormal(Scope scope, long captureFrameId, CaptureFrameDropReason reason)
        {
            CaptureFrameRequest request = MakeRequest(captureFrameId, scope.TestRunId);
            CommitDraft(scope.Registry, scope.Run, request);
            MarkDropped(scope.Registry, request, reason);
            Assert.That(TryConsumeDropTrace(scope.Registry, captureFrameId), Is.True);
        }

        private static void FreezeRemaining(Scope scope)
        {
            BeginProducerDrain(scope.Queue);
            CloseAfterProducerJoin(scope.Queue);
            object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
            ForceDrop(scope.Registry, scope.Queue, snapshot);
        }

        // ---- Field comparison helpers ----

        private static bool RequestsIdentical(CaptureFrameRequest a, CaptureFrameRequest b)
        {
            return a.TraceContext.Timestamp == b.TraceContext.Timestamp
                && a.TraceContext.UnityFrameId == b.TraceContext.UnityFrameId
                && a.TraceContext.FixedStepId == b.TraceContext.FixedStepId
                && a.TraceContext.ThreadId == b.TraceContext.ThreadId
                && a.TraceContext.CaptureFrameId == b.TraceContext.CaptureFrameId
                && a.TraceContext.OpenXRFrameId == b.TraceContext.OpenXRFrameId
                && a.TraceContext.TestRunId == b.TraceContext.TestRunId
                && a.TraceContext.SlashId == b.TraceContext.SlashId
                && a.TraceContext.FrontEdgeId == b.TraceContext.FrontEdgeId
                && a.TraceContext.ObjectId == b.TraceContext.ObjectId
                && a.TraceContext.ObjectGeneration == b.TraceContext.ObjectGeneration
                && a.TraceContext.TaskId == b.TraceContext.TaskId
                && a.Source == b.Source
                && a.Eye == b.Eye
                && a.ImageRect.X == b.ImageRect.X
                && a.ImageRect.Y == b.ImageRect.Y
                && a.ImageRect.Width == b.ImageRect.Width
                && a.ImageRect.Height == b.ImageRect.Height
                && a.ArrayIndex == b.ArrayIndex
                && a.PixelLayout.Format == b.PixelLayout.Format;
        }

        private static bool TimingIdentical(CaptureFrameTiming a, CaptureFrameTiming b)
        {
            return a.PredictedDisplayTimeSeconds == b.PredictedDisplayTimeSeconds
                && a.PredictedDisplayPeriodSeconds == b.PredictedDisplayPeriodSeconds
                && a.ShouldRender == b.ShouldRender
                && a.AppGpuTimeMilliseconds == b.AppGpuTimeMilliseconds
                && a.CompositorGpuTimeMilliseconds == b.CompositorGpuTimeMilliseconds
                && a.DroppedFrameCount == b.DroppedFrameCount;
        }

        private static bool PoseIdentical(CapturePoseSample a, CapturePoseSample b)
        {
            return a.IsAvailable == b.IsAvailable
                && a.Position.x == b.Position.x
                && a.Position.y == b.Position.y
                && a.Position.z == b.Position.z
                && a.Rotation.x == b.Rotation.x
                && a.Rotation.y == b.Rotation.y
                && a.Rotation.z == b.Rotation.z
                && a.Rotation.w == b.Rotation.w;
        }

        // ---- Tests ----

        [Test]
        public void FinalizerCtor_NullRegistry_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = CreateFinalizerException(null, scope.Store);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("draftRegistry"));
            });
        }

        [Test]
        public void FinalizerCtor_NullStore_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = CreateFinalizerException(scope.Registry, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("stagingStore"));
            });
        }

        [Test]
        public void FinalizerCtor_StoreRunMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRun = MakeRun(scope.TestRunId, captureProfileId: 5);
                object otherStore = CreateStore(otherRun, scope.MaxDraftPerRun, 4096);
                try
                {
                    Exception ex = CreateFinalizerException(scope.Registry, otherStore);
                    Assert.That(ex, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingStore"));
                }
                finally
                {
                    ((IDisposable)otherStore).Dispose();
                }
            });
        }

        [Test]
        public void Create_NullFinalRun_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = CreateFinalizationException(scope.Finalizer, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_DisposedStore_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                ((IDisposable)scope.Store).Dispose();

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
            });
        }

        [Test]
        public void Create_ReservationRemaining_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);
                SetCountField(scope.Registry, "_reservationCount", 1);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_PendingRemaining_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);
                SetCountField(scope.Registry, "_pendingCount", 1);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_ForcedDropSetNotIssued_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_ForcedDropSetForgedIssuer_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                object otherRegistry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object forged = CreateSetRaw(otherRegistry, scope.TestRunId, new long[0]);
                SetIssuedForcedDropSet(scope.Registry, forged);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_ForcedDropSetWrongTestRunId_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                object forged = CreateSetRaw(scope.Registry, scope.TestRunId + 1, new long[0]);
                SetIssuedForcedDropSet(scope.Registry, forged);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_FinalRunTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference(testRunId: scope.TestRunId + 1));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_FinalRunTestCaseIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference(testCaseId: 101));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_FinalRunBuildIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference(buildId: "build-2"));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_FinalRunSceneIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference(sceneId: "scene-2"));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_FinalRunRandomSeedMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference(randomSeed: 54321));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_FinalRunCaptureProfileIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference(captureProfileId: 6));
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalRun"));
            });
        }

        [Test]
        public void Create_Empty_EmptyResult()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                FreezeRemaining(scope);
                CaptureRunReference finalRun = MakeReference();

                object finalization = CreateFinalization(scope.Finalizer, finalRun);

                Assert.That(ReferenceEquals(GetRun(finalization), finalRun), Is.True);
                Assert.That(GetRecordCount(finalization), Is.EqualTo(0));
                Assert.That(GetDroppedCount(finalization), Is.EqualTo(0));
            });
        }

        [Test]
        public void Create_StagedMultiple_AscendingIds()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                StageDraft(scope, 20);
                StageDraft(scope, 30);
                FreezeRemaining(scope);

                object finalization = CreateFinalization(scope.Finalizer, MakeReference());

                Assert.That(GetRecordCount(finalization), Is.EqualTo(3));
                Assert.That(GetDroppedCount(finalization), Is.EqualTo(0));
                Assert.That(GetRecord(finalization, 0).CaptureFrameId, Is.EqualTo(10));
                Assert.That(GetRecord(finalization, 1).CaptureFrameId, Is.EqualTo(20));
                Assert.That(GetRecord(finalization, 2).CaptureFrameId, Is.EqualTo(30));
            });
        }

        [Test]
        public void Create_StagedAndDropped_RecordsStagedOnly()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                DropDraftNormal(scope, 20, CaptureFrameDropReason.PngEncodeFailed);
                StageDraft(scope, 30);
                DropDraftNormal(scope, 40, CaptureFrameDropReason.CaptureCancelled);
                // 50 stays pending and is force-dropped with reason 9.
                CommitDraft(scope.Registry, scope.Run, MakeRequest(50, scope.TestRunId));
                FreezeRemaining(scope);

                object finalization = CreateFinalization(scope.Finalizer, MakeReference());

                Assert.That(GetRecordCount(finalization), Is.EqualTo(2));
                Assert.That(GetDroppedCount(finalization), Is.EqualTo(3));
                Assert.That(GetRecord(finalization, 0).CaptureFrameId, Is.EqualTo(10));
                Assert.That(GetRecord(finalization, 1).CaptureFrameId, Is.EqualTo(30));
            });
        }

        [Test]
        public void Create_DroppedReasons6To9_Finalize()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                DropDraftNormal(scope, 20, CaptureFrameDropReason.PngEncodeFailed);
                DropDraftNormal(scope, 30, CaptureFrameDropReason.PngStagingStoreFull);
                DropDraftNormal(scope, 40, CaptureFrameDropReason.CaptureCancelled);
                CommitDraft(scope.Registry, scope.Run, MakeRequest(50, scope.TestRunId));
                FreezeRemaining(scope); // 50 -> FreezeDrainTimeout

                object finalization = CreateFinalization(scope.Finalizer, MakeReference());

                Assert.That(GetRecordCount(finalization), Is.EqualTo(1));
                Assert.That(GetDroppedCount(finalization), Is.EqualTo(4));
                Assert.That(GetRecord(finalization, 0).CaptureFrameId, Is.EqualTo(10));
            });
        }

        [Test]
        public void Create_RecordFieldsMatchDraft()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object draft10 = StageDraft(scope, 10, commitPathId: 11);
                object draft20 = StageDraft(scope, 20, commitPathId: 22);
                FreezeRemaining(scope);

                CaptureRunReference finalRun = MakeReference();
                object finalization = CreateFinalization(scope.Finalizer, finalRun);

                Assert.That(GetRecordCount(finalization), Is.EqualTo(2));

                CaptureFrameRecord record0 = GetRecord(finalization, 0);
                CaptureFrameRecord record1 = GetRecord(finalization, 1);

                Assert.That(record0.CaptureFrameId, Is.EqualTo(10));
                Assert.That(record0.CommitPathId, Is.EqualTo(11));
                Assert.That(ReferenceEquals(record0.Run, finalRun), Is.True);

                Assert.That(record1.CaptureFrameId, Is.EqualTo(20));
                Assert.That(record1.CommitPathId, Is.EqualTo(22));
                Assert.That(ReferenceEquals(record1.Run, finalRun), Is.True);

                CaptureFrameRequest draft10Request = (CaptureFrameRequest)GetProperty(draft10, "Request");
                CaptureFrameTiming draft10Timing = (CaptureFrameTiming)GetProperty(draft10, "Timing");
                CapturePoseSample draft10Head = (CapturePoseSample)GetProperty(draft10, "HeadPose");
                CapturePoseSample draft10Left = (CapturePoseSample)GetProperty(draft10, "LeftControllerPose");
                CapturePoseSample draft10Right = (CapturePoseSample)GetProperty(draft10, "RightControllerPose");

                Assert.That(RequestsIdentical(record0.Request, draft10Request), Is.True);
                Assert.That(TimingIdentical(record0.Timing, draft10Timing), Is.True);
                Assert.That(PoseIdentical(record0.HeadPose, draft10Head), Is.True);
                Assert.That(PoseIdentical(record0.LeftControllerPose, draft10Left), Is.True);
                Assert.That(PoseIdentical(record0.RightControllerPose, draft10Right), Is.True);
            });
        }

        [Test]
        public void Create_UnavailablePose_NotCompleted()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(10, scope.TestRunId);
                object reservation, rejectKind;
                Assert.That(TryReserve(scope.Registry, out reservation, out rejectKind), Is.True);
                object draft = MakeDraftWithPoses(
                    scope.Run, request, 1,
                    CapturePoseSample.Unavailable,
                    new CapturePoseSample(new Vector3(1f, 2f, 3f), Quaternion.identity),
                    new CapturePoseSample(new Vector3(4f, 5f, 6f), Quaternion.identity));
                Commit(scope.Registry, reservation, draft);
                object entry = MakeEntryTracked(scope, 10, scope.TestRunId, 16);
                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                FreezeRemaining(scope);

                object finalization = CreateFinalization(scope.Finalizer, MakeReference());

                Assert.That(GetRecordCount(finalization), Is.EqualTo(1));
                CaptureFrameRecord record = GetRecord(finalization, 0);
                Assert.That(record.HeadPose.IsAvailable, Is.False);
                Assert.That(record.HeadPose.Position, Is.EqualTo(Vector3.zero));
                Assert.That(record.LeftControllerPose.IsAvailable, Is.True);
                Assert.That(record.LeftControllerPose.Position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            });
        }

        [Test]
        public void Create_StagedWithoutPng_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(10, scope.TestRunId);
                object draft = CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 10, scope.TestRunId, 16);
                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                // Remove the entry while leaving the draft Staged.
                RollbackRegistration(scope.Store, 10, entry);

                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_DroppedWithPng_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                DropDraftNormal(scope, 10, CaptureFrameDropReason.PngEncodeFailed);
                object extra = MakeEntryTracked(scope, 10, scope.TestRunId, 16);
                Assert.That(TryRegister(scope.Store, extra), Is.True);

                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_StoreExtraEntry_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                object extra = MakeEntryTracked(scope, 999, scope.TestRunId, 16);
                Assert.That(TryRegister(scope.Store, extra), Is.True);

                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_EntryTestRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(10, scope.TestRunId);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 10, scope.TestRunId, 16);
                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                // Corrupt the entry's run identity after registration.
                SetEntryLongField(entry, "_testRunId", scope.TestRunId + 1);

                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_EntryCaptureFrameIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(10, scope.TestRunId);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 10, scope.TestRunId, 16);
                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                // Corrupt the entry's frame identity so the store lookup misses.
                SetEntryLongField(entry, "_captureFrameId", 11);

                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_DisposedStagingEntry_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CaptureFrameRequest request = MakeRequest(10, scope.TestRunId);
                CommitDraft(scope.Registry, scope.Run, request);
                object entry = MakeEntryTracked(scope, 10, scope.TestRunId, 16);
                Assert.That(TryMarkStaged(scope.Registry, request, scope.Store, entry), Is.True);

                ((IDisposable)entry).Dispose();

                FreezeRemaining(scope);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_UndefinedStatus_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);
                SetEntryEnumField(scope.Registry, 0, "Status", 99);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_StagedWithDropReason_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);
                SetEntryEnumField(scope.Registry, 0, "DropReason", 6);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_StagedWithEmission_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);
                SetEntryEnumField(scope.Registry, 0, "EmissionState", 1);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_DroppedInvalidReason_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                DropDraftNormal(scope, 10, CaptureFrameDropReason.PngEncodeFailed);
                FreezeRemaining(scope);
                SetEntryEnumField(scope.Registry, 0, "DropReason", 5);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_NormalDropEmissionPending_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                DropDraftNormal(scope, 10, CaptureFrameDropReason.PngEncodeFailed);
                FreezeRemaining(scope);
                SetEntryEnumField(scope.Registry, 0, "EmissionState", 1); // Pending

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_FreezeDropEmissionAttempted_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitDraft(scope.Registry, scope.Run, MakeRequest(10, scope.TestRunId));
                FreezeRemaining(scope); // 10 -> FreezeDrainTimeout, emission None
                SetEntryEnumField(scope.Registry, 0, "EmissionState", 2); // Attempted

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_StoreCountMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);
                SetStoreIntField(scope.Store, "_count", 2);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_TotalByteCountMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);
                SetStoreLongField(scope.Store, "_totalByteCount", 1);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_ValidationFailureDoesNotReachRecordCtor()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);

                // Corrupt the store count so validation fails before promotion,
                // and corrupt the draft so the record constructor would throw if
                // it were ever reached.
                SetStoreIntField(scope.Store, "_count", 2);
                SetDraftCommitPathId(GetEntryField(scope.Registry, 0, "Draft"), 0);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_MidPromotionRecordCtorFailure_InputsUnchanged()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10, commitPathId: 1);
                object draft20 = StageDraft(scope, 20, commitPathId: 2);
                FreezeRemaining(scope);

                int entryCountBefore = Count(scope.Registry, "EntryCount");
                int storeCountBefore = (int)GetProperty(scope.Store, "Count");

                // The second record construction will fail.
                SetDraftCommitPathId(draft20, 0);

                Exception ex = CreateFinalizationException(scope.Finalizer, MakeReference());
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());

                // Registry, store, and entries are untouched.
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(entryCountBefore));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "ReservationCount"), Is.EqualTo(0));
                Assert.That((int)GetEntryField(scope.Registry, 0, "Status"), Is.EqualTo(1)); // Staged
                Assert.That((int)GetEntryField(scope.Registry, 1, "Status"), Is.EqualTo(1)); // Staged
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(storeCountBefore));
                Assert.That((bool)GetProperty(scope.Store, "IsCreated"), Is.True);
            });
        }

        [Test]
        public void Create_Repeated_DeterministicResults()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10, commitPathId: 1);
                DropDraftNormal(scope, 20, CaptureFrameDropReason.PngEncodeFailed);
                StageDraft(scope, 30, commitPathId: 3);
                FreezeRemaining(scope);

                CaptureRunReference finalRun = MakeReference();
                object first = CreateFinalization(scope.Finalizer, finalRun);
                object second = CreateFinalization(scope.Finalizer, finalRun);

                Assert.That(GetRecordCount(first), Is.EqualTo(GetRecordCount(second)));
                Assert.That(GetDroppedCount(first), Is.EqualTo(GetDroppedCount(second)));

                for (int i = 0; i < GetRecordCount(first); i++)
                {
                    CaptureFrameRecord a = GetRecord(first, i);
                    CaptureFrameRecord b = GetRecord(second, i);
                    Assert.That(a.CaptureFrameId, Is.EqualTo(b.CaptureFrameId));
                    Assert.That(a.CommitPathId, Is.EqualTo(b.CommitPathId));
                    Assert.That(RequestsIdentical(a.Request, b.Request), Is.True);
                    Assert.That(TimingIdentical(a.Timing, b.Timing), Is.True);
                    Assert.That(PoseIdentical(a.HeadPose, b.HeadPose), Is.True);
                    Assert.That(PoseIdentical(a.LeftControllerPose, b.LeftControllerPose), Is.True);
                    Assert.That(PoseIdentical(a.RightControllerPose, b.RightControllerPose), Is.True);
                    Assert.That(ReferenceEquals(a.Run, b.Run), Is.True);
                }

                // The staging entry references are identical across runs.
                for (int i = 0; i < GetRecordCount(first); i++)
                {
                    Assert.That(ReferenceEquals(GetStagingEntry(first, i), GetStagingEntry(second, i)), Is.True);
                }
            });
        }

        [Test]
        public void Finalization_IndexOutOfRange_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                StageDraft(scope, 10);
                FreezeRemaining(scope);

                object finalization = CreateFinalization(scope.Finalizer, MakeReference());

                foreach (int index in new[] { -1, 1, 5 })
                {
                    Exception recordEx = GetRecordException(finalization, index);
                    Assert.That(recordEx, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)recordEx).ParamName, Is.EqualTo("index"));

                    Exception entryEx = GetStagingEntryException(finalization, index);
                    Assert.That(entryEx, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)entryEx).ParamName, Is.EqualTo("index"));
                }
            });
        }

        [Test]
        public void Finalization_NoArrayExposure_NoPublicSetters_NotDisposable()
        {
            Type type = GetFinalizationType();
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

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
        public void Finalizer_HoldsOnlyRegistryAndStore_NotDisposable()
        {
            Type type = GetFinalizerType();
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));

            bool hasRegistry = false;
            bool hasStore = false;
            foreach (FieldInfo field in fields)
            {
                hasRegistry |= field.FieldType == GetRegistryType();
                hasStore |= field.FieldType == GetStoreType();
            }

            Assert.That(hasRegistry, Is.True, "Finalizer must hold the draft registry.");
            Assert.That(hasStore, Is.True, "Finalizer must hold the staging store.");
        }

        [Test]
        public void Finalizer_NoDisposeClearRollbackTraceLoggerFilesystem()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureFrameDraftRecordFinalizer.cs"));

            Assert.That(source, Does.Not.Contain("System.IO"));
            Assert.That(source, Does.Not.Contain(".Dispose("));
            Assert.That(source, Does.Not.Contain(".Clear("));
            Assert.That(source, Does.Not.Contain("Rollback"));
            Assert.That(source, Does.Not.Contain("TraceObserver"));
            Assert.That(source, Does.Not.Contain("TraceLogger"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
        }
    }
}
