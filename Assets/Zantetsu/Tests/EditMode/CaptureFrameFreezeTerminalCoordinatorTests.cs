using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameFreezeTerminalCoordinatorTests
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

        private static Type GetCheckpointType() => GetTypeFromAssembly("FreezeTerminalCheckpoint");

        private static Type GetBufferType() => GetTypeFromAssembly("FreezeTerminalTraceBuffer");

        private static Type GetBuilderType() => GetTypeFromAssembly("FreezeTerminalTraceBufferBuilder");

        private static Type GetCoordinatorType() => GetTypeFromAssembly("CaptureFrameFreezeTerminalCoordinator");

        private static Type GetReceiptType() => GetTypeFromAssembly("TraceRunSealReceipt");

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

        // ---- Registry / queue operations ----

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

        private static void CommitAndRegister(object queue, object registry, object run, long captureFrameId)
        {
            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, MakeRequest(captureFrameId));
            Commit(registry, reservation, draft);
            MethodInfo register = GetQueueType().GetMethod("RegisterPendingDraft", BindingFlags.NonPublic | BindingFlags.Instance);
            register.Invoke(queue, new object[] { draft });
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

        private static TraceEvent GetEvent(object buffer, int index)
        {
            MethodInfo method = GetBufferType().GetMethod("GetEvent", BindingFlags.Public | BindingFlags.Instance);
            return (TraceEvent)method.Invoke(buffer, new object[] { index });
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

        private static void SetRecorderField(TraceFlightRecorder recorder, string name, object value)
        {
            FieldInfo field = typeof(TraceFlightRecorder).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(recorder, value);
        }

        // ---- Recorder / logger / coordinator operations ----

        private static object Seal(TraceLogger logger, long testRunId, TraceFlightRecorder recorder)
        {
            MethodInfo method = typeof(TraceLogger).GetMethod("SealAndDrainRunForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            object receipt = method.Invoke(logger, new object[] { testRunId, recorder });
            Assert.That(receipt, Is.Not.Null);
            return receipt;
        }

        private static void Begin(TraceFlightRecorder recorder, object receipt)
        {
            MethodInfo method = typeof(TraceFlightRecorder).GetMethod("BeginFreezeTerminalAppend", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            method.Invoke(recorder, new object[] { receipt, true });
        }

        private static object CreateReceipt(TraceLogger issuedBy, TraceFlightRecorder issuedTo, long testRunId)
        {
            ConstructorInfo ctor = GetReceiptType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(TraceFlightRecorder), typeof(long), typeof(int), typeof(int), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { issuedBy, issuedTo, testRunId, 0, 0, 0, 0 });
        }

        private static object CreateCoordinator(TraceFlightRecorder recorder, object builder)
        {
            ConstructorInfo ctor = GetCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceFlightRecorder), GetBuilderType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { recorder, builder });
        }

        private static object Complete(object coordinator, object receipt, object set, object checkpoint, bool captureAdmissionStopped)
        {
            MethodInfo method = GetCoordinatorType().GetMethod("Complete", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(coordinator, new object[] { receipt, set, checkpoint, captureAdmissionStopped });
        }

        private static Exception CompleteException(object coordinator, object receipt, object set, object checkpoint, bool captureAdmissionStopped)
        {
            try
            {
                Complete(coordinator, receipt, set, checkpoint, captureAdmissionStopped);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static Exception CompleteOffThread(object coordinator, object receipt, object set, object checkpoint, bool captureAdmissionStopped)
        {
            Exception captured = null;
            Thread thread = new Thread(() =>
            {
                captured = CompleteException(coordinator, receipt, set, checkpoint, captureAdmissionStopped);
            });
            thread.Start();
            thread.Join();
            return captured;
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
            public int MaxDraftPerRun = 8;
            public long TestRunId = 1;
            public int HistoryCapacity = 8;
            public int PostRollCapacity = 16;
            public int FreezeTerminalTraceReserve = 8;

            public TraceLogger Logger;
            public TraceFlightRecorder Recorder;
            public object Run;
            public object Registry;
            public object Queue;
            public object Store;
            public object Builder;
            public object Coordinator;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static Scope NewScope(int maxDraftPerRun = 8, long testRunId = 1, int historyCapacity = 8, int postRollCapacity = 16, int freezeTerminalTraceReserve = 8)
        {
            Scope scope = new Scope();
            scope.MaxDraftPerRun = maxDraftPerRun;
            scope.TestRunId = testRunId;
            scope.HistoryCapacity = historyCapacity;
            scope.PostRollCapacity = postRollCapacity;
            scope.FreezeTerminalTraceReserve = freezeTerminalTraceReserve;
            return scope;
        }

        private static void BuildScope(Scope scope)
        {
            scope.Logger = CreateCaptureLogger(scope.HistoryCapacity, scope.TestRunId);
            scope.Recorder = CreateRecorder(scope.Logger, scope.PostRollCapacity, scope.FreezeTerminalTraceReserve);
            scope.Run = MakeRun(scope.TestRunId);
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Queue = CreateQueue(scope.Registry, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Store = CreateStore(scope.Run, scope.MaxDraftPerRun, 4096);
            scope.Builder = CreateBuilder(scope.Registry);
            scope.Coordinator = CreateCoordinator(scope.Recorder, scope.Builder);
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

        private static object FreezePending(Scope scope, long[] captureFrameIds)
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

            object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
            return ForceDrop(scope.Registry, scope.Queue, snapshot);
        }

        // ---- Constructor ----

        [Test]
        public void Ctor_NullRecorder_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                try
                {
                    CreateCoordinator(null, scope.Builder);
                    Assert.Fail("Expected ArgumentNullException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                    Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("recorder"));
                }
            });
        }

        [Test]
        public void Ctor_NullBuilder_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                try
                {
                    CreateCoordinator(scope.Recorder, null);
                    Assert.Fail("Expected ArgumentNullException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                    Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("bufferBuilder"));
                }
            });
        }

        [Test]
        public void Ctor_LegacyLoggerRecorder_Rejected()
        {
            using (TraceLogger legacy = new TraceLogger(8))
            {
                TraceFlightRecorder recorder = CreateRecorder(legacy, 4, 2);
                Scope scope = NewScope();
                RunBody(scope, () =>
                {
                    try
                    {
                        CreateCoordinator(recorder, scope.Builder);
                        Assert.Fail("Expected ArgumentException.");
                    }
                    catch (TargetInvocationException ex)
                    {
                        Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                        Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("recorder"));
                    }
                });
            }
        }

        [Test]
        public void Ctor_BuilderRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object otherRun = MakeRun(testRunId: 99);
                object otherRegistry = CreateRegistry(otherRun, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object otherBuilder = CreateBuilder(otherRegistry);

                try
                {
                    CreateCoordinator(scope.Recorder, otherBuilder);
                    Assert.Fail("Expected ArgumentException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("bufferBuilder"));
                }
            });
        }

        // ---- Complete pre-validation ----

        [Test]
        public void Complete_NullReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);
                Exception ex = CompleteException(scope.Coordinator, null, set, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Complete_NullSet_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object receipt = CreateReceipt(scope.Logger, scope.Recorder, scope.TestRunId);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);
                Exception ex = CompleteException(scope.Coordinator, receipt, null, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void Complete_ArmedAndFrozenState_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);
                object forged = CreateReceipt(scope.Logger, scope.Recorder, scope.TestRunId);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Armed));
                Assert.That(CompleteException(scope.Coordinator, forged, set, checkpoint, true), Is.TypeOf<InvalidOperationException>());

                SetRecorderField(scope.Recorder, "_state", TraceFlightRecorderState.Frozen);
                Assert.That(CompleteException(scope.Coordinator, forged, set, checkpoint, true), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Complete_ForeignLoggerReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                TraceLogger otherLogger = CreateCaptureLogger(8, scope.TestRunId);
                try
                {
                    object foreign = CreateReceipt(otherLogger, scope.Recorder, scope.TestRunId);

                    Exception ex = CompleteException(scope.Coordinator, foreign, set, checkpoint, true);
                    Assert.That(ex, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
                }
                finally
                {
                    otherLogger.Dispose();
                }
            });
        }

        [Test]
        public void Complete_ForeignRecorderReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                TraceFlightRecorder otherRecorder = CreateRecorder(scope.Logger, 4, 2);
                object foreign = CreateReceipt(scope.Logger, otherRecorder, scope.TestRunId);

                Exception ex = CompleteException(scope.Coordinator, foreign, set, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Complete_ForgedReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                object forged = CreateReceipt(scope.Logger, scope.Recorder, scope.TestRunId); // not the issued instance

                Exception ex = CompleteException(scope.Coordinator, forged, set, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Complete_SetRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                // Forge a set with the wrong run id (same registry) so the
                // receipt-vs-set run ID mismatch fires before the identity check.
                object mismatchedSet = CreateSetRaw(scope.Registry, 777, new long[0]);

                Exception ex = CompleteException(scope.Coordinator, receipt, mismatchedSet, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Complete_SetNotCanonical_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                object otherRun = MakeRun(scope.TestRunId);
                object otherRegistry = CreateRegistry(otherRun, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object forgedSet = CreateSetRaw(otherRegistry, scope.TestRunId, new long[0]);

                Exception ex = CompleteException(scope.Coordinator, receipt, forgedSet, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("forcedDropFrameIds"));
            });
        }

        [Test]
        public void Complete_CheckpointRunIdMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, 777);

                Exception ex = CompleteException(scope.Coordinator, receipt, set, checkpoint, true);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("checkpoint"));
            });
        }

        [Test]
        public void Complete_CapturingPostRoll_AdmissionNotStopped_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[0]);
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                int capturedBefore = scope.Recorder.CapturedCount;
                Exception ex = CompleteException(scope.Coordinator, receipt, set, checkpoint, false);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("captureAdmissionStopped"));
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
            });
        }

        [Test]
        public void Complete_BuildFailure_LeavesCapturingPostRoll_NoBeginNoAppend()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[] { 100 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                // Corrupt the forced-drop entry so the buffer build fails.
                SetEntryEnumField(scope.Registry, 0, "DropReason", 6);

                int capturedBefore = scope.Recorder.CapturedCount;
                int postRollBefore = scope.Recorder.CapturedPostRollCount;
                int overflowBefore = scope.Recorder.TraceCaptureOverflowCount;

                Exception ex = CompleteException(scope.Coordinator, receipt, set, checkpoint, true);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll)); // Begin was not called
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore));
                Assert.That(scope.Recorder.TraceCaptureOverflowCount, Is.EqualTo(overflowBefore));
            });
        }

        // ---- Off-thread main-thread rejection ----

        [Test]
        public void Complete_OffThread_CapturingPostRoll_Rejected_StateAndRegistryUnchanged()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[] { 100 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                int capturedBefore = scope.Recorder.CapturedCount;
                int postRollBefore = scope.Recorder.CapturedPostRollCount;
                int overflowBefore = scope.Recorder.TraceCaptureOverflowCount;
                int entryCountBefore = (int)GetProperty(scope.Registry, "EntryCount");
                int pendingBefore = (int)GetProperty(scope.Registry, "PendingCount");

                Exception ex = CompleteOffThread(scope.Coordinator, receipt, set, checkpoint, true);

                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore));
                Assert.That(scope.Recorder.TraceCaptureOverflowCount, Is.EqualTo(overflowBefore));
                Assert.That(ReferenceEquals(GetProperty(scope.Registry, "IssuedForcedDropFrameIdSet"), set), Is.True);
                Assert.That((int)GetProperty(scope.Registry, "EntryCount"), Is.EqualTo(entryCountBefore));
                Assert.That((int)GetProperty(scope.Registry, "PendingCount"), Is.EqualTo(pendingBefore));
            });
        }

        [Test]
        public void Complete_OffThread_AwaitingFreezeTerminal_Rejected_StateAndRegistryUnchanged()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                Begin(scope.Recorder, receipt); // now AwaitingFreezeTerminal

                object set = FreezePending(scope, new long[] { 100 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                int capturedBefore = scope.Recorder.CapturedCount;
                int postRollBefore = scope.Recorder.CapturedPostRollCount;
                int overflowBefore = scope.Recorder.TraceCaptureOverflowCount;
                int entryCountBefore = (int)GetProperty(scope.Registry, "EntryCount");
                int pendingBefore = (int)GetProperty(scope.Registry, "PendingCount");

                Exception ex = CompleteOffThread(scope.Coordinator, receipt, set, checkpoint, true);

                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore));
                Assert.That(scope.Recorder.TraceCaptureOverflowCount, Is.EqualTo(overflowBefore));
                Assert.That(ReferenceEquals(GetProperty(scope.Registry, "IssuedForcedDropFrameIdSet"), set), Is.True);
                Assert.That((int)GetProperty(scope.Registry, "EntryCount"), Is.EqualTo(entryCountBefore));
                Assert.That((int)GetProperty(scope.Registry, "PendingCount"), Is.EqualTo(pendingBefore));
            });
        }

        [Test]
        public void Complete_OffThread_CapturingPostRoll_MainThreadPrecedesBuilderValidation()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[] { 100 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                // Builder validation would fail on the main thread; off-thread
                // must reject with the main-thread error before the builder runs.
                SetEntryEnumField(scope.Registry, 0, "DropReason", 6);

                Exception ex = CompleteOffThread(scope.Coordinator, receipt, set, checkpoint, true);

                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                StringAssert.Contains("constructed the capture logger", ex.Message);
                StringAssert.DoesNotContain("freeze-drain", ex.Message);
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
            });
        }

        [Test]
        public void Complete_OffThread_AwaitingFreezeTerminal_MainThreadPrecedesBuilderValidation()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                Begin(scope.Recorder, receipt); // now AwaitingFreezeTerminal

                object set = FreezePending(scope, new long[] { 100 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                // Builder validation would fail on the main thread; off-thread
                // must reject with the main-thread error before the builder runs.
                SetEntryEnumField(scope.Registry, 0, "DropReason", 6);

                Exception ex = CompleteOffThread(scope.Coordinator, receipt, set, checkpoint, true);

                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                StringAssert.Contains("constructed the capture logger", ex.Message);
                StringAssert.DoesNotContain("freeze-drain", ex.Message);
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));
            });
        }

        // ---- Success ----

        [Test]
        public void Complete_Success_ForcedDropZeroAndMultiple()
        {
            foreach (long[] ids in new[] { new long[0], new long[] { 1, 2, 3 } })
            {
                Scope scope = NewScope();
                RunBody(scope, () =>
                {
                    Assert.That(scope.Recorder.TryTrigger(), Is.True);
                    object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                    object set = FreezePending(scope, ids);
                    object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                    object buffer = Complete(scope.Coordinator, receipt, set, checkpoint, true);

                    Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                    Assert.That(buffer, Is.Not.Null);
                    Assert.That((int)GetProperty(buffer, "Count"), Is.EqualTo(ids.Length + 1));
                });
            }
        }

        [Test]
        public void Complete_Success_ReturnedBufferMatchesSnapshotTail()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[] { 10, 20 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                int capturedBefore = scope.Recorder.CapturedCount;
                object buffer = Complete(scope.Coordinator, receipt, set, checkpoint, true);

                int bufferCount = (int)GetProperty(buffer, "Count");
                TraceCaptureSnapshot snapshot = scope.Recorder.CreateFrozenSnapshot();

                for (int i = 0; i < bufferCount; i++)
                {
                    TraceEvent fromBuffer = GetEvent(buffer, i);
                    TraceEvent fromSnapshot = snapshot.GetEvent(capturedBefore + i);
                    Assert.That(fromBuffer.CaptureFrameId, Is.EqualTo(fromSnapshot.CaptureFrameId));
                    Assert.That(fromBuffer.EventType, Is.EqualTo(fromSnapshot.EventType));
                }

                Assert.That(snapshot.EventCount, Is.EqualTo(capturedBefore + bufferCount));
            });
        }

        // ---- Append failure + retry ----

        [Test]
        public void Complete_AppendFailure_LeavesAwaitingFreezeTerminal_RetrySucceeds()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                Begin(scope.Recorder, receipt); // now AwaitingFreezeTerminal

                object set = FreezePending(scope, new long[] { 100 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                // Corrupt a recorder counter so Append fails its invariant check.
                SetRecorderField(scope.Recorder, "_capturedPostRollCount", scope.Recorder.CapturedPostRollCount + 1);

                int capturedBefore = scope.Recorder.CapturedCount;
                Exception ex = CompleteException(scope.Coordinator, receipt, set, checkpoint, true);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));

                // Fix the cause and retry with the same receipt/set/checkpoint.
                SetRecorderField(scope.Recorder, "_capturedPostRollCount", scope.Recorder.CapturedPostRollCount - 1);
                object buffer = Complete(scope.Coordinator, receipt, set, checkpoint, true);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(buffer, Is.Not.Null);
                Assert.That((int)GetProperty(buffer, "Count"), Is.EqualTo(2));
            });
        }

        [Test]
        public void Complete_AwaitingRetry_DoesNotReBeginOrReSeal()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                Begin(scope.Recorder, receipt);

                // The issued receipt must remain the same throughout.
                object issued = GetProperty(scope.Logger, "IssuedSealReceipt");

                object set = FreezePending(scope, new long[] { 5 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                object buffer = Complete(scope.Coordinator, receipt, set, checkpoint, true);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(ReferenceEquals(GetProperty(scope.Logger, "IssuedSealReceipt"), issued), Is.True); // no re-seal
            });
        }

        [Test]
        public void Complete_SecondCall_NoDoubleAppend()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                object set = FreezePending(scope, new long[] { 1 });
                object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);

                object buffer = Complete(scope.Coordinator, receipt, set, checkpoint, true);
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));

                int countAfter = scope.Recorder.CapturedCount;
                Assert.That(CompleteException(scope.Coordinator, receipt, set, checkpoint, true), Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(countAfter));
            });
        }

        // ---- Type contracts ----

        [Test]
        public void Coordinator_HoldsOnlyRecorderAndBuilder_SealedNotDisposable()
        {
            Type type = GetCoordinatorType();
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType == typeof(TraceFlightRecorder) || field.FieldType == GetBuilderType(), Is.True, "Unexpected field type: " + field.FieldType.Name);
            }
        }

        // ---- Set raw-construction helper ----

        private static object CreateSetRaw(object registry, long testRunId, long[] ids)
        {
            ConstructorInfo ctor = GetTypeFromAssembly("ForcedDropFrameIdSet").GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), typeof(long), typeof(long[]) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { registry, testRunId, ids });
        }
    }
}
