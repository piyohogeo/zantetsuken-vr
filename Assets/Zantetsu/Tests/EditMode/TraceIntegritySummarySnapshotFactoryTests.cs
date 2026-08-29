using System;
using System.Collections.Generic;
using System.IO;
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
    public class TraceIntegritySummarySnapshotFactoryTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FactorySourcePath = "Assets/Zantetsu/Runtime/Observability/TraceIntegritySummarySnapshotFactory.cs";

        private static string LocateFactorySource()
        {
            if (File.Exists(FactorySourcePath))
            {
                return FactorySourcePath;
            }

            string dir = Path.GetDirectoryName(typeof(TraceIntegritySummarySnapshotFactoryTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, FactorySourcePath);
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

            Assert.Fail("Factory source file not found.");
            return null;
        }

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetFactoryType() => GetTypeFromAssembly("TraceIntegritySummarySnapshotFactory");

        private static Type GetReceiptType() => GetTypeFromAssembly("TraceRunSealReceipt");

        private static Type GetQueueType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntentQueue");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetIntentType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntent");

        private static Type GetCheckpointType() => GetTypeFromAssembly("FreezeTerminalCheckpoint");

        private static Type GetBufferType() => GetTypeFromAssembly("FreezeTerminalTraceBuffer");

        private static Type GetBuilderType() => GetTypeFromAssembly("FreezeTerminalTraceBufferBuilder");

        private static Type GetStoreType() => GetTypeFromAssembly("CaptureFramePngStagingStore");

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

        private static bool IsPositiveZero(double value) => BitConverter.DoubleToInt64Bits(value) == 0L;

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

        private static TraceRunContext MakeRunContext(long testRunId)
        {
            return new TraceRunContext(
                testRunId, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", 12345, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));
        }

        private static object MakeRun(long testRunId = 1, int captureProfileId = 5)
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceRunContext), typeof(long), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null);
            TraceRunContext context = MakeRunContext(testRunId);
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

        // ---- Registry / queue / builder operations ----

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

        private static object BuildBuffer(object builder, object set, object checkpoint)
        {
            MethodInfo method = GetBuilderType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(builder, new object[] { set, checkpoint });
        }

        private static TraceEvent GetEvent(object buffer, int index)
        {
            MethodInfo method = GetBufferType().GetMethod("GetEvent", BindingFlags.Public | BindingFlags.Instance);
            return (TraceEvent)method.Invoke(buffer, new object[] { index });
        }

        private static TraceEvent[] GetBufferEvents(object buffer)
        {
            FieldInfo field = GetBufferType().GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            return (TraceEvent[])field.GetValue(buffer);
        }

        private static void SetBufferField(object buffer, string name, object value)
        {
            FieldInfo field = GetBufferType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(buffer, value);
        }

        // ---- Recorder / logger operation helpers ----

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

        private static void Append(TraceFlightRecorder recorder, object buffer)
        {
            MethodInfo method = typeof(TraceFlightRecorder).GetMethod("AppendFreezeTerminalEvents", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            method.Invoke(recorder, new object[] { buffer });
        }

        private static void SetRecorderField(TraceFlightRecorder recorder, string name, object value)
        {
            FieldInfo field = typeof(TraceFlightRecorder).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(recorder, value);
        }

        private static object CreateReceipt(TraceLogger issuedBy, TraceFlightRecorder issuedTo, long testRunId, int finalDrainedCount, int capturedPostRollCount, int traceCaptureOverflowCount, int sealedTraceEnqueueFailureCount)
        {
            ConstructorInfo ctor = GetReceiptType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(TraceFlightRecorder), typeof(long), typeof(int), typeof(int), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { issuedBy, issuedTo, testRunId, finalDrainedCount, capturedPostRollCount, traceCaptureOverflowCount, sealedTraceEnqueueFailureCount });
        }

        private static void SetReceiptField(object receipt, string name, int value)
        {
            FieldInfo field = GetReceiptType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Receipt field not found: " + name);
            field.SetValue(receipt, value);
        }

        private static void SetLoggerSealedFailures(TraceLogger logger, int value)
        {
            FieldInfo field = typeof(TraceLogger).GetField("_sealGate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            NativeArray<int> gate = (NativeArray<int>)field.GetValue(logger);
            gate[3] = value; // SlotSealedFailures
        }

        // ---- Factory invocation helpers ----

        private static TraceCaptureSnapshot CreateExport(
            TraceFlightRecorder recorder,
            TraceRunContext runContext,
            object sealReceipt,
            object terminalBuffer,
            uint priorBundlePublishFailureCount)
        {
            MethodInfo method = GetFactoryType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "Create method not found.");
            return (TraceCaptureSnapshot)method.Invoke(null, new object[] { recorder, runContext, sealReceipt, terminalBuffer, priorBundlePublishFailureCount });
        }

        private static Exception CreateExportException(
            TraceFlightRecorder recorder,
            TraceRunContext runContext,
            object sealReceipt,
            object terminalBuffer,
            uint priorBundlePublishFailureCount)
        {
            try
            {
                CreateExport(recorder, runContext, sealReceipt, terminalBuffer, priorBundlePublishFailureCount);
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
            public int MaxDraftPerRun = 8;
            public long TestRunId = 1;
            public int HistoryCapacity = 8;
            public int PostRollCapacity = 16;
            public int FreezeTerminalTraceReserve = 8;

            public TraceLogger Logger;
            public TraceFlightRecorder Recorder;
            public TraceRunContext RunContext;
            public object Run;
            public object Registry;
            public object Queue;
            public object Store;
            public object Receipt;
            public object TerminalBuffer;
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
            scope.RunContext = MakeRunContext(scope.TestRunId);
            scope.Run = MakeRun(scope.TestRunId);
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Queue = CreateQueue(scope.Registry, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Store = CreateStore(scope.Run, scope.MaxDraftPerRun, 4096);
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

        // ---- Setup helpers ----

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

        private static object BuildTerminalBuffer(Scope scope, long[] captureFrameIds)
        {
            object set = FreezePending(scope, captureFrameIds);
            object builder = CreateBuilder(scope.Registry);
            object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);
            return BuildBuffer(builder, set, checkpoint);
        }

        private static void SetupFrozen(Scope scope, long[] captureFrameIds)
        {
            Assert.That(scope.Recorder.TryTrigger(), Is.True);
            scope.Receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
            Begin(scope.Recorder, scope.Receipt);
            scope.TerminalBuffer = BuildTerminalBuffer(scope, captureFrameIds);
            Append(scope.Recorder, scope.TerminalBuffer);
            Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
        }

        // ---- Tests ----

        [Test]
        public void TraceEventType_AppendsIntegritySummaryAfterExistingValues()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(TraceEventType)), Is.EqualTo(typeof(int)));
            Assert.That((int)TraceEventType.None, Is.EqualTo(0));
            Assert.That((int)TraceEventType.CaptureFrameAdmissionRejected, Is.EqualTo(45));
            Assert.That((int)TraceEventType.TraceIntegritySummary, Is.EqualTo(46));

            for (int i = 0; i <= 46; i++)
            {
                Assert.That(Enum.GetName(typeof(TraceEventType), i), Is.Not.Null, "TraceEventType value " + i + " has no name.");
            }

            Assert.That(Enum.GetName(typeof(TraceEventType), 47), Is.Null);
        }

        [Test]
        public void TraceReason_AppendsWriteFailureAndOverflowReasons()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(TraceReason)), Is.EqualTo(typeof(int)));
            Assert.That((int)TraceReason.None, Is.EqualTo(0));
            Assert.That((int)TraceReason.TraceWriteFailureObserved, Is.EqualTo(1));
            Assert.That((int)TraceReason.TraceCaptureOverflowObserved, Is.EqualTo(2));
            Assert.That(Enum.GetName(typeof(TraceReason), 3), Is.Null);
        }

        [Test]
        public void TraceIntegrityState_IsAppendOnlyInt()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(TraceIntegrityState)), Is.EqualTo(typeof(int)));
            Assert.That((int)TraceIntegrityState.Complete, Is.EqualTo(0));
            Assert.That((int)TraceIntegrityState.Incomplete, Is.EqualTo(1));
            Assert.That(Enum.GetName(typeof(TraceIntegrityState), 2), Is.Null);
        }

        [Test]
        public void Create_NullRecorder_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);
                Exception ex = CreateExportException(null, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("recorder"));
            });
        }

        [Test]
        public void Create_NullRunContext_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);
                Exception ex = CreateExportException(scope.Recorder, null, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("runContext"));
            });
        }

        [Test]
        public void Create_NullReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, null, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Create_NullTerminalBuffer_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, null, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Create_OffThread_Rejected_BeforeSnapshotOrStateChange()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[] { 10, 20 });
                TraceCaptureSnapshot before = scope.Recorder.CreateFrozenSnapshot();
                int capturedBefore = scope.Recorder.CapturedCount;

                Exception captured = null;
                Thread thread = new Thread(() =>
                {
                    captured = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                });
                thread.Start();
                thread.Join();

                Assert.That(captured, Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(scope.Recorder.CreateFrozenSnapshot().EventCount, Is.EqualTo(before.EventCount));
            });
        }

        [Test]
        public void Create_LegacyLogger_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = BuildTerminalBuffer(scope, new long[0]);

                using (TraceLogger legacy = new TraceLogger(8))
                {
                    TraceFlightRecorder legacyRecorder = CreateRecorder(legacy, 4, 0);
                    Assert.That(legacyRecorder.TryTrigger(), Is.True);
                    Assert.That(legacyRecorder.Freeze(), Is.True);
                    object forged = CreateReceipt(legacy, legacyRecorder, scope.TestRunId, 0, 0, 0, 0);

                    Exception ex = CreateExportException(legacyRecorder, scope.RunContext, forged, buffer, 0u);
                    Assert.That(ex, Is.TypeOf<InvalidOperationException>());
                }
            });
        }

        [Test]
        public void Create_NotFrozen_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                scope.Receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                scope.TerminalBuffer = BuildTerminalBuffer(scope, new long[0]);

                // Still CapturingPostRoll (never began the freeze terminal append).
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.CapturingPostRoll));
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_NotSealed_Rejected()
        {
            Scope scope = NewScope(freezeTerminalTraceReserve: 0);
            RunBody(scope, () =>
            {
                scope.TerminalBuffer = BuildTerminalBuffer(scope, new long[0]);

                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                Assert.That(scope.Recorder.Freeze(), Is.True);
                object forged = CreateReceipt(scope.Logger, scope.Recorder, scope.TestRunId, 0, 0, 0, 0);

                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, forged, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Create_ForeignLoggerReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                TraceLogger other = CreateCaptureLogger(8, scope.TestRunId);
                try
                {
                    object foreign = CreateReceipt(other, scope.Recorder, scope.TestRunId, 0, 0, 0, 0);
                    Exception ex = CreateExportException(scope.Recorder, scope.RunContext, foreign, scope.TerminalBuffer, 0u);
                    Assert.That(ex, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
                }
                finally
                {
                    other.Dispose();
                }
            });
        }

        [Test]
        public void Create_ForeignRecorderReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                TraceFlightRecorder otherRecorder = CreateRecorder(scope.Logger, 4, 2);
                object foreign = CreateReceipt(scope.Logger, otherRecorder, scope.TestRunId, 0, 0, 0, 0);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, foreign, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Create_ForgedReceipt_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                object forged = CreateReceipt(scope.Logger, scope.Recorder, scope.TestRunId, 0, 0, 0, 0);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, forged, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Create_DifferentRunContext_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                TraceRunContext other = MakeRunContext(999);
                Exception ex = CreateExportException(scope.Recorder, other, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("runContext"));
            });
        }

        [Test]
        public void Create_DifferentTerminalBufferRun_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetBufferField(scope.TerminalBuffer, "_testRunId", 999L);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Create_ReceiptSealedFailureMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetReceiptField(scope.Receipt, "_sealedTraceEnqueueFailureCount", 5);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Create_ReceiptOverflowMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetReceiptField(scope.Receipt, "_traceCaptureOverflowCount", 5);
                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("sealReceipt"));
            });
        }

        [Test]
        public void Create_TailFieldDiff_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[] { 10, 20 });

                TraceEvent[] bufferEvents = GetBufferEvents(scope.TerminalBuffer);
                bufferEvents[0].Timestamp = 999999L;

                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Create_TailOrderViolation_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[] { 10, 20 });

                TraceEvent[] bufferEvents = GetBufferEvents(scope.TerminalBuffer);
                TraceEvent tmp = bufferEvents[0];
                bufferEvents[0] = bufferEvents[1];
                bufferEvents[1] = tmp;

                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Create_TailExtra_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetBufferField(scope.TerminalBuffer, "_count", 1000);

                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Create_TailMissing_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[] { 10, 20 });

                SetBufferField(scope.TerminalBuffer, "_count", 1);

                Exception ex = CreateExportException(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Create_CompleteSummary_AllFieldsAndCounts()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[] { 10, 20 });
                TraceCaptureSnapshot original = scope.Recorder.CreateFrozenSnapshot();

                TraceCaptureSnapshot export = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);

                Assert.That(export.EventCount, Is.EqualTo(original.EventCount + 1));
                Assert.That(export.TriggerHistoryCount, Is.EqualTo(original.TriggerHistoryCount));
                Assert.That(export.CapturedPostRollCount, Is.EqualTo(original.CapturedPostRollCount + 1));
                Assert.That(export.EventCount, Is.EqualTo(export.TriggerHistoryCount + export.CapturedPostRollCount));
                Assert.That(export.WasHistoryOverwrittenAtTrigger, Is.EqualTo(original.WasHistoryOverwrittenAtTrigger));

                // Original events are copied unchanged, in order.
                for (int i = 0; i < original.EventCount; i++)
                {
                    AssertIdentical(export.GetEvent(i), original.GetEvent(i));
                }

                // Summary is exactly one event at the tail.
                TraceEvent summary = export.GetEvent(export.EventCount - 1);
                TraceEvent last = original.GetEvent(original.EventCount - 1);

                Assert.That(summary.Timestamp, Is.EqualTo(last.Timestamp));
                Assert.That(summary.FrameId, Is.EqualTo(last.FrameId));
                Assert.That(summary.FixedStepId, Is.EqualTo(last.FixedStepId));
                Assert.That(summary.ThreadId, Is.EqualTo(203)); // checkpoint thread id
                Assert.That(summary.TestRunId, Is.EqualTo(scope.TestRunId));

                Assert.That(summary.SlashId, Is.EqualTo(0));
                Assert.That(summary.SlashGeneration, Is.EqualTo(0));
                Assert.That(summary.FrontEdgeId, Is.EqualTo(0));
                Assert.That(summary.ObjectId, Is.EqualTo(0));
                Assert.That(summary.ObjectGeneration, Is.EqualTo(0));
                Assert.That(summary.MobId, Is.EqualTo(0));
                Assert.That(summary.PlanGeneration, Is.EqualTo(0));
                Assert.That(summary.TaskId, Is.EqualTo(0));
                Assert.That(summary.CaptureFrameId, Is.EqualTo(0));
                Assert.That(summary.OpenXRFrameId, Is.EqualTo(0));

                Assert.That(summary.EventType, Is.EqualTo(TraceEventType.TraceIntegritySummary));
                Assert.That(summary.TaskType, Is.EqualTo(TraceTaskType.None));
                Assert.That(summary.FromState, Is.EqualTo((int)TraceIntegrityState.Complete));
                Assert.That(summary.ToState, Is.EqualTo(0));
                Assert.That(summary.Reason, Is.EqualTo(TraceReason.None));
                Assert.That(summary.Value0, Is.EqualTo(0.0));
                Assert.That(IsPositiveZero(summary.Value0), Is.True);
                Assert.That(summary.Value1, Is.EqualTo(0.0));
                Assert.That(IsPositiveZero(summary.Value1), Is.True);
            });
        }

        [Test]
        public void Create_SealedFailureOnly_IncompleteWriteFailure()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetLoggerSealedFailures(scope.Logger, 3);
                SetReceiptField(scope.Receipt, "_sealedTraceEnqueueFailureCount", 3);

                TraceCaptureSnapshot export = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                TraceEvent summary = export.GetEvent(export.EventCount - 1);

                Assert.That(summary.FromState, Is.EqualTo((int)TraceIntegrityState.Incomplete));
                Assert.That(summary.ToState, Is.EqualTo(0));
                Assert.That(summary.Reason, Is.EqualTo(TraceReason.TraceWriteFailureObserved));
                Assert.That(summary.Value0, Is.EqualTo(3.0));
            });
        }

        [Test]
        public void Create_OverflowOnly_IncompleteOverflow()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetRecorderField(scope.Recorder, "_traceCaptureOverflowCount", 5);
                SetReceiptField(scope.Receipt, "_traceCaptureOverflowCount", 5);

                TraceCaptureSnapshot export = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                TraceEvent summary = export.GetEvent(export.EventCount - 1);

                Assert.That(summary.FromState, Is.EqualTo((int)TraceIntegrityState.Incomplete));
                Assert.That(summary.ToState, Is.EqualTo(5));
                Assert.That(summary.Reason, Is.EqualTo(TraceReason.TraceCaptureOverflowObserved));
                Assert.That(summary.Value0, Is.EqualTo(0.0));
            });
        }

        [Test]
        public void Create_BothFailures_WriteFailureTakesPriority()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                SetLoggerSealedFailures(scope.Logger, 3);
                SetReceiptField(scope.Receipt, "_sealedTraceEnqueueFailureCount", 3);
                SetRecorderField(scope.Recorder, "_traceCaptureOverflowCount", 5);
                SetReceiptField(scope.Receipt, "_traceCaptureOverflowCount", 5);

                TraceCaptureSnapshot export = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 0u);
                TraceEvent summary = export.GetEvent(export.EventCount - 1);

                Assert.That(summary.FromState, Is.EqualTo((int)TraceIntegrityState.Incomplete));
                Assert.That(summary.ToState, Is.EqualTo(5));
                Assert.That(summary.Reason, Is.EqualTo(TraceReason.TraceWriteFailureObserved));
                Assert.That(summary.Value0, Is.EqualTo(3.0));
            });
        }

        [Test]
        public void Create_PriorPublishFailureOnly_StillComplete()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                TraceCaptureSnapshot export = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 7u);
                TraceEvent summary = export.GetEvent(export.EventCount - 1);

                Assert.That(summary.FromState, Is.EqualTo((int)TraceIntegrityState.Complete));
                Assert.That(summary.Reason, Is.EqualTo(TraceReason.None));
                Assert.That(summary.Value0, Is.EqualTo(0.0));
                Assert.That(summary.Value1, Is.EqualTo(7.0));
            });
        }

        [Test]
        public void Create_UintMaxPriorPublishCount_StoredExactly()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[0]);

                TraceCaptureSnapshot export = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, uint.MaxValue);
                TraceEvent summary = export.GetEvent(export.EventCount - 1);

                Assert.That(summary.Value1, Is.EqualTo(4294967295.0));
                Assert.That((uint)summary.Value1, Is.EqualTo(uint.MaxValue));
                Assert.That(summary.FromState, Is.EqualTo((int)TraceIntegrityState.Complete));
            });
        }

        [Test]
        public void Create_RepeatedCalls_RecorderUnchangedAndResultsIdentical()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupFrozen(scope, new long[] { 10, 20 });
                int capturedBefore = scope.Recorder.CapturedCount;
                int postRollBefore = scope.Recorder.CapturedPostRollCount;
                int overflowBefore = scope.Recorder.TraceCaptureOverflowCount;

                TraceCaptureSnapshot first = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 4u);
                TraceCaptureSnapshot second = CreateExport(scope.Recorder, scope.RunContext, scope.Receipt, scope.TerminalBuffer, 4u);

                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore));
                Assert.That(scope.Recorder.TraceCaptureOverflowCount, Is.EqualTo(overflowBefore));
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));

                Assert.That(second.EventCount, Is.EqualTo(first.EventCount));
                for (int i = 0; i < first.EventCount; i++)
                {
                    AssertIdentical(second.GetEvent(i), first.GetEvent(i));
                }
            });
        }

        [Test]
        public void Factory_IsStatelessAndOwnsNothing()
        {
            Type type = GetFactoryType();
            Assert.That(type.IsAbstract, Is.True, "Static factory must be abstract+sealed.");
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Factory_DoesNotEnqueueDrainOrSampleTimeOrThread()
        {
            string source = File.ReadAllText(LocateFactorySource());
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain(".Enqueue("));
            Assert.That(source, Does.Not.Contain(".Drain("));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Thread.CurrentThread"));
            Assert.That(source, Does.Not.Contain("UnityEngine.Time"));
        }

        private static void AssertIdentical(TraceEvent left, TraceEvent right)
        {
            Assert.That(left.Timestamp, Is.EqualTo(right.Timestamp));
            Assert.That(left.FrameId, Is.EqualTo(right.FrameId));
            Assert.That(left.FixedStepId, Is.EqualTo(right.FixedStepId));
            Assert.That(left.ThreadId, Is.EqualTo(right.ThreadId));
            Assert.That(left.SlashId, Is.EqualTo(right.SlashId));
            Assert.That(left.SlashGeneration, Is.EqualTo(right.SlashGeneration));
            Assert.That(left.FrontEdgeId, Is.EqualTo(right.FrontEdgeId));
            Assert.That(left.ObjectId, Is.EqualTo(right.ObjectId));
            Assert.That(left.ObjectGeneration, Is.EqualTo(right.ObjectGeneration));
            Assert.That(left.MobId, Is.EqualTo(right.MobId));
            Assert.That(left.PlanGeneration, Is.EqualTo(right.PlanGeneration));
            Assert.That(left.TaskId, Is.EqualTo(right.TaskId));
            Assert.That(left.CaptureFrameId, Is.EqualTo(right.CaptureFrameId));
            Assert.That(left.OpenXRFrameId, Is.EqualTo(right.OpenXRFrameId));
            Assert.That(left.TestRunId, Is.EqualTo(right.TestRunId));
            Assert.That(left.EventType, Is.EqualTo(right.EventType));
            Assert.That(left.TaskType, Is.EqualTo(right.TaskType));
            Assert.That(left.FromState, Is.EqualTo(right.FromState));
            Assert.That(left.ToState, Is.EqualTo(right.ToState));
            Assert.That(left.Reason, Is.EqualTo(right.Reason));
            Assert.That(BitConverter.DoubleToInt64Bits(left.Value0), Is.EqualTo(BitConverter.DoubleToInt64Bits(right.Value0)));
            Assert.That(BitConverter.DoubleToInt64Bits(left.Value1), Is.EqualTo(BitConverter.DoubleToInt64Bits(right.Value1)));
        }
    }
}
