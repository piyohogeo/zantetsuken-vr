using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftTerminalCoordinatorTests
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

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

        private static Type GetStoreType() => GetTypeFromAssembly("CaptureFramePngStagingStore");

        private static Type GetIntentType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntent");

        private static Type GetCoordinatorType() => GetTypeFromAssembly("CaptureFrameDraftTerminalCoordinator");

        private static Type GetProcessingStatusType() => GetTypeFromAssembly("CaptureFrameDraftTerminalProcessingStatus");

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

        private static object CreateCoordinator(object queue, object registry, object store, CaptureFrameTraceObserver observer)
        {
            ConstructorInfo ctor = GetCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetQueueType(), GetRegistryType(), GetStoreType(), typeof(CaptureFrameTraceObserver) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { queue, registry, store, observer });
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

        private static void ClearEntryPngBytes(object entry)
        {
            FieldInfo field = GetEntryType().GetField("_pngBytes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(entry, default(NativeArray<byte>));
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

        private static object CommitDraft(object registry, object run, CaptureFrameRequest request)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, request);
            Commit(registry, reservation, draft);
            return draft;
        }

        private static void RegisterPendingDraft(object queue, object draft)
        {
            MethodInfo method = GetQueueType().GetMethod("RegisterPendingDraft", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(queue, new object[] { draft });
        }

        private static void CommitAndRegister(object queue, object registry, object run, long captureFrameId)
        {
            CaptureFrameRequest request = MakeRequest(captureFrameId);
            object draft = CommitDraft(registry, run, request);
            RegisterPendingDraft(queue, draft);
        }

        private static int EnqueueTerminalIntent(object queue, object intent)
        {
            MethodInfo method = GetQueueType().GetMethod("EnqueueTerminalIntent", BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)method.Invoke(queue, new object[] { intent });
        }

        private static object CreateStageIntent(CaptureFrameRequest request, object entry)
        {
            MethodInfo method = GetIntentType().GetMethod("CreateStage", BindingFlags.NonPublic | BindingFlags.Static);
            return method.Invoke(null, new object[] { request, entry });
        }

        private static object CreateDropIntent(CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetIntentType().GetMethod("CreateDrop", BindingFlags.NonPublic | BindingFlags.Static);
            return method.Invoke(null, new object[] { request, reason });
        }

        private static bool TryMarkStaged(object registry, CaptureFrameRequest request, object store, object entry)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryMarkStaged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(registry, new object[] { request, store, entry });
        }

        private static int GetRegistryStatus(object registry, CaptureFrameRequest request)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { request, null, null };
            Assert.That((bool)method.Invoke(registry, args), Is.True);
            return (int)args[2];
        }

        private static object StoreTryGet(object store, long captureFrameId)
        {
            MethodInfo method = GetStoreType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { captureFrameId, null };
            bool ok = (bool)method.Invoke(store, args);
            return ok ? args[1] : null;
        }

        private static int ProcessNext(object coordinator)
        {
            MethodInfo method = GetCoordinatorType().GetMethod("ProcessNext", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (int)method.Invoke(coordinator, null);
        }

        private static Exception ProcessNextException(object coordinator)
        {
            try
            {
                ProcessNext(coordinator);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Logger helpers ----

        private static TraceLogger CreateCaptureLogger(int historyCapacity, long testRunId)
        {
            ConstructorInfo ctor = typeof(TraceLogger).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return (TraceLogger)ctor.Invoke(new object[] { historyCapacity, testRunId });
        }

        private static TraceFlightRecorder CreateRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            ConstructorInfo ctor = typeof(TraceFlightRecorder).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return (TraceFlightRecorder)ctor.Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
        }

        private static void Seal(TraceLogger logger, long testRunId, TraceFlightRecorder recorder)
        {
            MethodInfo method = typeof(TraceLogger).GetMethod("SealAndDrainRunForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            object receipt = method.Invoke(logger, new object[] { testRunId, recorder });
            Assert.That(receipt, Is.Not.Null);
        }

        private static int GetLoggerCount(TraceLogger logger, string name)
        {
            PropertyInfo prop = typeof(TraceLogger).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null);
            return (int)prop.GetValue(logger);
        }

        private static int Count(object registry, string name) => (int)GetProperty(registry, name);

        private static object GetEntryField(object registry, int entryIndex, string fieldName)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(entryIndex);
            FieldInfo field = entry.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Entry." + fieldName + " field not found.");
            return field.GetValue(entry);
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
            public int QueueMaxInFlight;
            public int MaxDraftPerRun;
            public int StoreMaxEntryCount;
            public long StoreMaxTotalByteCount;
            public long TestRunId;

            public object Run;
            public object Registry;
            public object Store;
            public object Queue;
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public object Coordinator;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static Scope NewScope(
            int queueMaxInFlight = 2,
            int maxDraftPerRun = 8,
            int storeMaxEntryCount = 8,
            long storeMaxTotalByteCount = 4096,
            long testRunId = 1)
        {
            Scope scope = new Scope();
            scope.QueueMaxInFlight = queueMaxInFlight;
            scope.MaxDraftPerRun = maxDraftPerRun;
            scope.StoreMaxEntryCount = storeMaxEntryCount;
            scope.StoreMaxTotalByteCount = storeMaxTotalByteCount;
            scope.TestRunId = testRunId;
            return scope;
        }

        private static void BuildScope(Scope scope)
        {
            scope.Run = MakeRun(scope.TestRunId, captureProfileId: 5);
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Queue = CreateQueue(scope.Registry, MakeProfile(5, scope.QueueMaxInFlight, scope.MaxDraftPerRun));
            scope.Store = CreateStore(scope.Run, scope.StoreMaxEntryCount, scope.StoreMaxTotalByteCount);
            if (scope.Logger == null)
            {
                scope.Logger = new TraceLogger(16);
            }

            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.Coordinator = CreateCoordinator(scope.Queue, scope.Registry, scope.Store, scope.Observer);
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

        private static object MakePoisonedEntryTracked(Scope scope, long captureFrameId, long testRunId, int pngLength)
        {
            ConstructorInfo ctor = GetEntryCtor();

            byte[] data = new byte[pngLength];
            for (int i = 0; i < pngLength; i++)
            {
                data[i] = (byte)i;
            }

            NativeArray<byte> png = new NativeArray<byte>(data, Allocator.Persistent);
            object entry;
            try
            {
                entry = ctor.Invoke(new object[] { testRunId, captureFrameId, png, KnownPngSha256 });
            }
            catch
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }

                throw;
            }

            // Free our own copy of the struct so the entry's internal copy becomes
            // a dangling view: IsCreated stays true but the safety handle is
            // released, so the entry's Dispose throws until the view is cleared.
            png.Dispose();

            try
            {
                scope.AllEntries.Add(entry);
            }
            catch
            {
                ClearEntryPngBytes(entry);
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

            try
            {
                if (scope.Logger != null)
                {
                    scope.Logger.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
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

        // ---- Status enum contracts ----

        [Test]
        public void Status_UnderlyingTypeIsInt()
        {
            Assert.That(Enum.GetUnderlyingType(GetProcessingStatusType()), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void Status_NamesAndValues_MatchExactly()
        {
            Type type = GetProcessingStatusType();
            Assert.That(Enum.GetName(type, 0), Is.EqualTo("None"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("Staged"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("Dropped"));
            Assert.That(Enum.GetName(type, 3), Is.EqualTo("DiscardedAlreadyTerminal"));
        }

        [Test]
        public void Status_NoAliasesOrGaps()
        {
            Type type = GetProcessingStatusType();
            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(4));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(4));
            for (int i = 0; i <= 3; i++)
            {
                Assert.That(Enum.IsDefined(type, i), Is.True);
            }

            Assert.That(Enum.IsDefined(type, 4), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Constructor ----

        [Test]
        public void Ctor_NullIntentQueue_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                try
                {
                    CreateCoordinator(null, scope.Registry, scope.Store, scope.Observer);
                    Assert.Fail("Expected ArgumentNullException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                    Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("intentQueue"));
                }
            });
        }

        [Test]
        public void Ctor_NullDraftRegistry_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                try
                {
                    CreateCoordinator(scope.Queue, null, scope.Store, scope.Observer);
                    Assert.Fail("Expected ArgumentNullException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                    Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("draftRegistry"));
                }
            });
        }

        [Test]
        public void Ctor_NullStagingStore_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                try
                {
                    CreateCoordinator(scope.Queue, scope.Registry, null, scope.Observer);
                    Assert.Fail("Expected ArgumentNullException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                    Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("stagingStore"));
                }
            });
        }

        [Test]
        public void Ctor_NullTraceObserver_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                try
                {
                    CreateCoordinator(scope.Queue, scope.Registry, scope.Store, null);
                    Assert.Fail("Expected ArgumentNullException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                    Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("traceObserver"));
                }
            });
        }

        [Test]
        public void Ctor_QueueRegistryMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRegistry = CreateRegistry(scope.Run, MakeProfile(5, 8, 8));
                try
                {
                    CreateCoordinator(scope.Queue, otherRegistry, scope.Store, scope.Observer);
                    Assert.Fail("Expected ArgumentException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("draftRegistry"));
                }
            });
        }

        [Test]
        public void Ctor_StoreRunMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRun = MakeRun(testRunId: 2, captureProfileId: 5);
                object otherStore = CreateStore(otherRun, 8, 4096);
                try
                {
                    CreateCoordinator(scope.Queue, scope.Registry, otherStore, scope.Observer);
                    Assert.Fail("Expected ArgumentException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("stagingStore"));
                }
                finally
                {
                    ((IDisposable)otherStore).Dispose();
                }
            });
        }

        [Test]
        public void Coordinator_HasOnlyFourDependencies_NotIDisposable()
        {
            Type type = GetCoordinatorType();
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));

            foreach (FieldInfo field in fields)
            {
                Assert.That(
                    field.FieldType == GetQueueType()
                    || field.FieldType == GetRegistryType()
                    || field.FieldType == GetStoreType()
                    || field.FieldType == typeof(CaptureFrameTraceObserver),
                    Is.True,
                    "Unexpected field type: " + field.FieldType.Name);
            }
        }

        // ---- Empty queue ----

        [Test]
        public void ProcessNext_EmptyQueue_None_NoDependencyTouch()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(0)); // None

                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(0));
                Assert.That((long)GetProperty(scope.Store, "TotalAccepted"), Is.EqualTo(0));
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0));
            });
        }

        // ---- Drop intent winner ----

        [Test]
        public void ProcessNext_DropReason6_7_8_EachSucceeds()
        {
            foreach (int reason in new[] { 6, 7, 8 })
            {
                Scope scope = NewScope();
                RunBody(scope, () =>
                {
                    CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                    Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), (CaptureFrameDropReason)reason)), Is.EqualTo(0));

                    Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped

                    Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(2)); // Dropped
                    scope.Logger.Drain();
                    Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                    Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo((double)reason));
                });
            }
        }

        [Test]
        public void ProcessNext_DropSuccess_RegistryDroppedSlotFreedMirrorTerminal_TraceOnce()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped

                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(2)); // Dropped
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0)); // slot freed

                // Mirror is terminal: another intent for the same draft is rejected.
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(2)); // DraftAlreadyTerminal

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.TotalWritten, Is.EqualTo(1));
            });
        }

        // ---- Stage intent winner ----

        [Test]
        public void ProcessNext_StageSuccess_StoreOwnsSameEntry_RegistryStaged_SlotFreed()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(1)); // Staged

                object stored = StoreTryGet(scope.Store, 1);
                Assert.That(stored, Is.Not.Null);
                Assert.That(ReferenceEquals(stored, entry), Is.True); // store owns the same entry
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(1)); // Staged
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0)); // slot freed
            });
        }

        [Test]
        public void ProcessNext_StageSuccess_CoordinatorDoesNotDisposeEntry()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(1)); // Staged

                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True); // coordinator did not dispose it
            });
        }

        // ---- Store capacity shortage ----

        [Test]
        public void ProcessNext_StoreCountShortage_EntryDisposedThenReason7Drop()
        {
            Scope scope = NewScope(storeMaxEntryCount: 1, storeMaxTotalByteCount: 4096);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 2);

                // Fill the single store slot with draft 1's entry directly.
                object entry1 = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(TryMarkStaged(scope.Registry, MakeRequest(1), scope.Store, entry1), Is.True);

                // Draft 2's stage intent cannot register: count capacity is full.
                object entry2 = MakeEntryTracked(scope, 2, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(2), entry2)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped

                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.False); // disposed
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(2)), Is.EqualTo(2)); // Dropped
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(1));
                scope.Logger.Drain();
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo(7.0)); // reason 7
            });
        }

        [Test]
        public void ProcessNext_StoreByteShortage_EntryDisposedThenReason7Drop()
        {
            Scope scope = NewScope(storeMaxEntryCount: 8, storeMaxTotalByteCount: 8);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);

                object entry = MakeEntryTracked(scope, 1, 1, 16); // 16 bytes > 8 byte budget
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped

                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.False); // disposed
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(2)); // Dropped
                Assert.That((long)GetProperty(scope.Store, "TotalRejected"), Is.EqualTo(1));
                scope.Logger.Drain();
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo(7.0)); // reason 7
            });
        }

        // ---- Loser intents ----

        [Test]
        public void ProcessNext_StageWinnerThenDropLoser()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);

                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(1)); // Staged (winner)
                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(3)); // DiscardedAlreadyTerminal (loser)

                // Loser did not touch the winner's store entry or the registry state.
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True);
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(1)); // Staged
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0)); // no drop trace
            });
        }

        [Test]
        public void ProcessNext_DropWinnerThenStageLoser()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);

                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped (winner)
                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(3)); // DiscardedAlreadyTerminal (loser)

                // Loser's private entry was disposed; the store never took it.
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.False);
                Assert.That((int)GetProperty(scope.Store, "Count"), Is.EqualTo(0));
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(2)); // Dropped
                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1)); // only the winner's trace
            });
        }

        [Test]
        public void ProcessNext_StageVsStage_OnlyLaterEntryDisposed()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);

                object entry1 = MakeEntryTracked(scope, 1, 1, 16);
                object entry2 = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry1)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry2)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(1)); // Staged (entry1)
                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(3)); // loser (entry2)

                Assert.That((bool)GetProperty(entry1, "IsCreated"), Is.True); // store-owned, untouched
                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.False); // loser disposed
                Assert.That(ReferenceEquals(StoreTryGet(scope.Store, 1), entry1), Is.True);
            });
        }

        [Test]
        public void ProcessNext_DropVsDrop_OnlyFirstTrace()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngStagingStoreFull)), Is.EqualTo(0));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped (winner)
                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(3)); // loser

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo(6.0)); // first reason only
            });
        }

        // ---- Stage exception safety ----

        [Test]
        public void ProcessNext_StageEntryDisposed_OriginalExceptionMaintained_DraftPending()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                ((IDisposable)entry).Dispose(); // poison the stage before processing

                Exception ex = ProcessNextException(scope.Coordinator);
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(0)); // still Pending
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1)); // slot not freed
            });
        }

        [Test]
        public void ProcessNext_StoreDisposed_OriginalExceptionMaintained_EntryRecovered()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                ((IDisposable)scope.Store).Dispose(); // poison the store before processing

                Exception ex = ProcessNextException(scope.Coordinator);
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(0)); // still Pending
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.False); // coordinator recovered it
            });
        }

        [Test]
        public void ProcessNext_EntryCleanupFails_AggregateExceptionOrder()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakePoisonedEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                ((IDisposable)scope.Store).Dispose(); // TryMarkStaged throws; then entry cleanup also throws

                Exception ex = ProcessNextException(scope.Coordinator);
                Assert.That(ex, Is.TypeOf<AggregateException>());
                AggregateException agg = (AggregateException)ex;
                Assert.That(agg.InnerExceptions.Count, Is.EqualTo(2));
                Assert.That(agg.InnerExceptions[0], Is.TypeOf<ObjectDisposedException>()); // original store exception
                Assert.That(agg.InnerExceptions[1], Is.Not.Null); // cleanup exception

                // Resolve the poisoned entry so cleanup can dispose it.
                ClearEntryPngBytes(entry);
            });
        }

        // ---- Drop trace failure semantics ----

        [Test]
        public void ProcessNext_DisposedLogger_DropMirrorEmissionMaintained_NoReissue()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                scope.Logger.Dispose();

                Exception ex = ProcessNextException(scope.Coordinator);
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());

                // Drop state, freed slot, mirror terminal, and emission are all maintained.
                Assert.That(GetRegistryStatus(scope.Registry, MakeRequest(1)), Is.EqualTo(2)); // Dropped
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(0));
                Assert.That((int)GetEntryField(scope.Registry, 0, "EmissionState"), Is.EqualTo(2)); // Attempted
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(2)); // DraftAlreadyTerminal

                // No re-issue on a second attempt.
                MethodInfo record = typeof(CaptureFrameTraceObserver).GetMethod("RecordDraftDropped", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That((bool)record.Invoke(scope.Observer, new object[] { scope.Registry, 1L }), Is.False);
            });
        }

        [Test]
        public void ProcessNext_SealedLogger_PostSealAttemptOnce_NoReissue()
        {
            Scope scope = NewScope();
            scope.Logger = CreateCaptureLogger(16, 1); // bound to the draft's testRunId

            RunBody(scope, () =>
            {
                TraceFlightRecorder recorder = CreateRecorder(scope.Logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);
                Seal(scope.Logger, 1, recorder);

                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                // The sealed gate rejects silently; the drop still completes.
                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped

                Assert.That(GetLoggerCount(scope.Logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(1));
                Assert.That((int)GetEntryField(scope.Registry, 0, "EmissionState"), Is.EqualTo(2)); // Attempted
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0));

                // No re-issue.
                MethodInfo record = typeof(CaptureFrameTraceObserver).GetMethod("RecordDraftDropped", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That((bool)record.Invoke(scope.Observer, new object[] { scope.Registry, 1L }), Is.False);
                Assert.That(GetLoggerCount(scope.Logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(1));
            });
        }

        // ---- One intent per call / FIFO ----

        [Test]
        public void ProcessNext_OneIntentPerCall_FIFO()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 2);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(2), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));

                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(2));

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped (draft 1)
                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(1)); // only one dequeued

                Assert.That(ProcessNext(scope.Coordinator), Is.EqualTo(2)); // Dropped (draft 2)

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(2));
                Assert.That(scope.Logger.GetHistoryEvent(0).CaptureFrameId, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(1).CaptureFrameId, Is.EqualTo(2));
            });
        }
    }
}
