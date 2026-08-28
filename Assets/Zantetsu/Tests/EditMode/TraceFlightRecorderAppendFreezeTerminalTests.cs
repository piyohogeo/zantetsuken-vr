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
    public class TraceFlightRecorderAppendFreezeTerminalTests
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

        private static Type GetSetType() => GetTypeFromAssembly("ForcedDropFrameIdSet");

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

        private static Exception AppendException(TraceFlightRecorder recorder, object buffer)
        {
            try
            {
                Append(recorder, buffer);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static void SetRecorderField(TraceFlightRecorder recorder, string name, object value)
        {
            FieldInfo field = typeof(TraceFlightRecorder).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(recorder, value);
        }

        private static void EnqueueIntoLoggerQueue(TraceLogger logger, TraceEvent e)
        {
            FieldInfo field = typeof(TraceLogger).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance);
            NativeQueue<TraceEvent> queue = (NativeQueue<TraceEvent>)field.GetValue(logger);
            queue.Enqueue(e);
        }

        private static TraceEvent Event(long tag, long testRunId)
        {
            return new TraceEvent { Timestamp = tag, TestRunId = testRunId };
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

        private static void SetupAwaitingFreezeTerminal(Scope scope)
        {
            Assert.That(scope.Recorder.TryTrigger(), Is.True);
            object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
            Begin(scope.Recorder, receipt);
            Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));
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

        private static object BuildTerminalBuffer(Scope scope, long[] captureFrameIds)
        {
            object set = FreezePending(scope, captureFrameIds);
            object builder = CreateBuilder(scope.Registry);
            object checkpoint = MakeCheckpoint(200, 201, 202, 203, scope.TestRunId);
            return BuildBuffer(builder, set, checkpoint);
        }

        private static object SetupReadyBuffer(Scope scope, long[] captureFrameIds)
        {
            SetupAwaitingFreezeTerminal(scope);
            return BuildTerminalBuffer(scope, captureFrameIds);
        }

        // ---- Null / thread / state rejection ----

        [Test]
        public void Append_NullBuffer_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupAwaitingFreezeTerminal(scope);
                Exception ex = AppendException(scope.Recorder, null);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        [Test]
        public void Append_OffThread_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                SetupAwaitingFreezeTerminal(scope);
                object buffer = BuildTerminalBuffer(scope, new long[0]);

                Exception captured = null;
                Thread thread = new Thread(() =>
                {
                    captured = AppendException(scope.Recorder, buffer);
                });
                thread.Start();
                thread.Join();

                Assert.That(captured, Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));
            });
        }

        [Test]
        public void Append_NotAwaitingFreezeTerminal_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = BuildTerminalBuffer(scope, new long[0]);

                // Armed.
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Armed));
                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());

                // CapturingPostRoll.
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());

                // Frozen.
                SetRecorderField(scope.Recorder, "_state", TraceFlightRecorderState.Frozen);
                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Append_NoReserve_Rejected()
        {
            Scope scope = NewScope(freezeTerminalTraceReserve: 0);
            RunBody(scope, () =>
            {
                object buffer = BuildTerminalBuffer(scope, new long[0]);
                SetRecorderField(scope.Recorder, "_state", TraceFlightRecorderState.AwaitingFreezeTerminal);

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Append_LoggerNotSealed_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = BuildTerminalBuffer(scope, new long[0]);
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                SetRecorderField(scope.Recorder, "_state", TraceFlightRecorderState.AwaitingFreezeTerminal);

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Append_QueueNonEmpty_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = BuildTerminalBuffer(scope, new long[0]);
                SetupAwaitingFreezeTerminal(scope);
                EnqueueIntoLoggerQueue(scope.Logger, Event(777, scope.TestRunId));

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void Append_RunIdMismatch_Rejected()
        {
            Scope scope = NewScope(testRunId: 42);
            RunBody(scope, () =>
            {
                SetupAwaitingFreezeTerminal(scope); // logger bound to 42

                // Build a buffer bound to run 1.
                object otherRun = MakeRun(testRunId: 1);
                object otherRegistry = CreateRegistry(otherRun, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
                object otherQueue = CreateQueue(otherRegistry, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));

                object reservation, rejectKind;
                Assert.That(TryReserve(otherRegistry, out reservation, out rejectKind), Is.True);
                object draft = MakeDraft(otherRun, MakeRequest(100, testRunId: 1));
                Commit(otherRegistry, reservation, draft);
                MethodInfo register = GetQueueType().GetMethod("RegisterPendingDraft", BindingFlags.NonPublic | BindingFlags.Instance);
                register.Invoke(otherQueue, new object[] { draft });

                Assert.That(EnqueueTerminalIntent(otherQueue, CreateDropIntent(MakeRequest(100, 1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
                BeginProducerDrain(otherQueue);
                CloseAfterProducerJoin(otherQueue);
                object dequeued;
                Assert.That(TryDequeue(otherQueue, out dequeued), Is.True);
                object snapshot = CreateOwnershipSnapshot(otherQueue, 0);
                object otherSet = ForceDrop(otherRegistry, otherQueue, snapshot);

                object otherBuilder = CreateBuilder(otherRegistry);
                object buffer = BuildBuffer(otherBuilder, otherSet, MakeCheckpoint(200, 201, 202, 203, 1));

                Exception ex = AppendException(scope.Recorder, buffer);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("terminalBuffer"));
            });
        }

        // ---- Buffer structure rejection ----

        [Test]
        public void Append_BufferForcedDropCountMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 100 });
                SetBufferField(buffer, "_forcedDropCount", 999);

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<ArgumentException>());
            });
        }

        [Test]
        public void Append_BufferCountMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 100 });
                SetBufferField(buffer, "_count", 999);

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<ArgumentException>());
            });
        }

        [Test]
        public void Append_ReserveInsufficient_Rejected()
        {
            Scope scope = NewScope(freezeTerminalTraceReserve: 2, postRollCapacity: 4);
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 1, 2, 3 }); // Count 4 > reserve 2

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<ArgumentException>());
            });
        }

        [Test]
        public void Append_CaptureCounterInvariantViolation_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[0]);
                SetRecorderField(scope.Recorder, "_capturedPostRollCount", scope.Recorder.CapturedPostRollCount + 1);

                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());
            });
        }

        // ---- Success ----

        [Test]
        public void Append_ForcedDropZero_RingOnly_Succeeds()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[0]);

                int capturedBefore = scope.Recorder.CapturedCount;
                int postRollBefore = scope.Recorder.CapturedPostRollCount;

                Append(scope.Recorder, buffer);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore + 1));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore + 1));
                Assert.That(scope.Recorder.GetCapturedEvent(capturedBefore).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
            });
        }

        [Test]
        public void Append_ForcedDropMultiple_OrderSucceeds()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 10, 20, 30 });

                int capturedBefore = scope.Recorder.CapturedCount;
                Append(scope.Recorder, buffer);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(4)); // 3 drops + 1 ring
                Assert.That(scope.Recorder.GetCapturedEvent(capturedBefore).CaptureFrameId, Is.EqualTo(10));
                Assert.That(scope.Recorder.GetCapturedEvent(capturedBefore + 1).CaptureFrameId, Is.EqualTo(20));
                Assert.That(scope.Recorder.GetCapturedEvent(capturedBefore + 2).CaptureFrameId, Is.EqualTo(30));
                Assert.That(scope.Recorder.GetCapturedEvent(capturedBefore + 3).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
            });
        }

        [Test]
        public void Append_NormalRegionEmptyAndFull_Succeeds()
        {
            Scope scope = NewScope(postRollCapacity: 8, freezeTerminalTraceReserve: 4); // normal region 4
            RunBody(scope, () =>
            {
                // Fill the normal post-roll region first.
                Assert.That(scope.Recorder.TryTrigger(), Is.True);
                for (int i = 1; i <= scope.Recorder.NormalPostRollCapacity; i++)
                {
                    scope.Logger.Enqueue(Event(i, scope.TestRunId));
                }

                object receipt = Seal(scope.Logger, scope.TestRunId, scope.Recorder);
                Begin(scope.Recorder, receipt);
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(scope.Recorder.NormalPostRollCapacity));

                int capturedBefore = scope.Recorder.CapturedCount;
                object buffer = BuildTerminalBuffer(scope, new long[] { 1, 2 });
                Append(scope.Recorder, buffer);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(capturedBefore + 3));
                Assert.That(scope.Recorder.GetCapturedEvent(capturedBefore + 2).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
            });
        }

        [Test]
        public void Append_Success_CountersAndSnapshotMatch_NoDependencyChange()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 5, 7 });

                int triggerBefore = scope.Recorder.TriggerHistoryCount;
                int postRollBefore = scope.Recorder.CapturedPostRollCount;
                int overflowBefore = scope.Recorder.TraceCaptureOverflowCount;

                Append(scope.Recorder, buffer);

                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(scope.Recorder.TriggerHistoryCount, Is.EqualTo(triggerBefore)); // unchanged
                Assert.That(scope.Recorder.CapturedPostRollCount, Is.EqualTo(postRollBefore + 3));
                Assert.That(scope.Recorder.TraceCaptureOverflowCount, Is.EqualTo(overflowBefore)); // unchanged

                TraceCaptureSnapshot snapshot = scope.Recorder.CreateFrozenSnapshot();
                Assert.That(snapshot.EventCount, Is.EqualTo(scope.Recorder.CapturedCount));
                Assert.That(snapshot.GetEvent(snapshot.EventCount - 1).EventType, Is.EqualTo(TraceEventType.CaptureRingFrozen));
                Assert.That(snapshot.GetEvent(snapshot.EventCount - 3).CaptureFrameId, Is.EqualTo(5));
                Assert.That(snapshot.GetEvent(snapshot.EventCount - 2).CaptureFrameId, Is.EqualTo(7));

                Assert.That((int)GetProperty(buffer, "Count"), Is.EqualTo(3));
                Assert.That(scope.Logger.IsCreated, Is.True);
            });
        }

        [Test]
        public void Append_SecondCall_NoDoubleAppend()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 1 });
                Append(scope.Recorder, buffer);

                int countAfter = scope.Recorder.CapturedCount;
                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.Recorder.CapturedCount, Is.EqualTo(countAfter));
            });
        }

        // ---- Event verification rejection with restore + retry ----

        private static void AssertDropFieldDiffRejectedThenRestoreSucceeds(Action<TraceEvent[]> mutate)
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 100 });
                TraceEvent[] events = GetBufferEvents(buffer);
                TraceEvent original = events[0];

                mutate(events);
                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<ArgumentException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));

                events[0] = original;
                Append(scope.Recorder, buffer);
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            });
        }

        [Test]
        public void Append_DropEvent22Field_SingleDiff_RejectedThenRestoreSucceeds()
        {
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Timestamp = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].FrameId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].FixedStepId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].ThreadId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].SlashId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].SlashGeneration = 1; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].FrontEdgeId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].ObjectId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].ObjectGeneration = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].MobId = 1; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].PlanGeneration = 1; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].TaskId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].CaptureFrameId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].OpenXRFrameId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].TestRunId = 999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].EventType = TraceEventType.CaptureRingFrozen; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].TaskType = (TraceTaskType)999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].FromState = 9; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].ToState = 9; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Reason = (TraceReason)999; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Value0 = 1.0; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Value1 = 1.0; });
        }

        private static void AssertRingFieldDiffRejectedThenRestoreSucceeds(Action<TraceEvent[]> mutate)
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object buffer = SetupReadyBuffer(scope, new long[] { 100 });
                TraceEvent[] events = GetBufferEvents(buffer);
                TraceEvent original = events[1];

                mutate(events);
                Assert.That(AppendException(scope.Recorder, buffer), Is.TypeOf<ArgumentException>());
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.AwaitingFreezeTerminal));

                events[1] = original;
                Append(scope.Recorder, buffer);
                Assert.That(scope.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            });
        }

        [Test]
        public void Append_RingEvent22Field_SingleDiff_RejectedThenRestoreSucceeds()
        {
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].Timestamp = 999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].FrameId = 999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].FixedStepId = 999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].ThreadId = 999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].SlashId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].SlashGeneration = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].FrontEdgeId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].ObjectId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].ObjectGeneration = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].MobId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].PlanGeneration = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].TaskId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].CaptureFrameId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].OpenXRFrameId = 1; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].TestRunId = 999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].EventType = TraceEventType.CaptureFrameDropped; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].TaskType = (TraceTaskType)999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].FromState = 9; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].ToState = 9; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].Reason = (TraceReason)999; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].Value0 = 999.0; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].Value1 = 1.0; });
        }

        [Test]
        public void Append_DropIdMissingDuplicateOrder_RejectedThenRestoreSucceeds()
        {
            // Missing ID.
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].CaptureFrameId = 999; });

            // Duplicate: two drops would share an ID.
            Scope scopeDup = NewScope();
            RunBody(scopeDup, () =>
            {
                object buffer = SetupReadyBuffer(scopeDup, new long[] { 10, 20 });
                TraceEvent[] events = GetBufferEvents(buffer);
                TraceEvent original = events[1];
                events[1].CaptureFrameId = 10; // duplicate of first drop
                Assert.That(AppendException(scopeDup.Recorder, buffer), Is.TypeOf<ArgumentException>());
                events[1] = original;
                Append(scopeDup.Recorder, buffer);
                Assert.That(scopeDup.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            });

            // Order violation: swap the two drop IDs.
            Scope scopeOrder = NewScope();
            RunBody(scopeOrder, () =>
            {
                object buffer = SetupReadyBuffer(scopeOrder, new long[] { 10, 20 });
                TraceEvent[] events = GetBufferEvents(buffer);
                TraceEvent original0 = events[0];
                TraceEvent original1 = events[1];
                events[0].CaptureFrameId = 20;
                events[1].CaptureFrameId = 10;
                Assert.That(AppendException(scopeOrder.Recorder, buffer), Is.TypeOf<ArgumentException>());
                events[0] = original0;
                events[1] = original1;
                Append(scopeOrder.Recorder, buffer);
                Assert.That(scopeOrder.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            });
        }

        [Test]
        public void Append_ValueNaNInfinityNegativeZero_Rejected()
        {
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Value0 = double.NaN; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Value1 = double.PositiveInfinity; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Value1 = double.NegativeInfinity; });
            AssertDropFieldDiffRejectedThenRestoreSucceeds(e => { e[0].Value0 = -0.0; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].Value0 = double.NaN; });
            AssertRingFieldDiffRejectedThenRestoreSucceeds(e => { e[1].Value1 = -0.0; });
        }

        [Test]
        public void Append_RingMissingMiddleMultiple_Rejected()
        {
            // Ring missing (turned into a drop): last event is no longer ring.
            Scope scopeMissing = NewScope();
            RunBody(scopeMissing, () =>
            {
                object buffer = SetupReadyBuffer(scopeMissing, new long[] { 10 });
                TraceEvent[] events = GetBufferEvents(buffer);
                TraceEvent original = events[1];
                events[1].EventType = TraceEventType.CaptureFrameDropped;
                Assert.That(AppendException(scopeMissing.Recorder, buffer), Is.TypeOf<ArgumentException>());
                events[1] = original;
                Append(scopeMissing.Recorder, buffer);
                Assert.That(scopeMissing.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            });

            // Ring in the middle: a drop slot becomes ring.
            Scope scopeMiddle = NewScope();
            RunBody(scopeMiddle, () =>
            {
                object buffer = SetupReadyBuffer(scopeMiddle, new long[] { 10, 20 });
                TraceEvent[] events = GetBufferEvents(buffer);
                TraceEvent original = events[0];
                events[0].EventType = TraceEventType.CaptureRingFrozen;
                Assert.That(AppendException(scopeMiddle.Recorder, buffer), Is.TypeOf<ArgumentException>());
                events[0] = original;
                Append(scopeMiddle.Recorder, buffer);
                Assert.That(scopeMiddle.Recorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
            });
        }
    }
}
