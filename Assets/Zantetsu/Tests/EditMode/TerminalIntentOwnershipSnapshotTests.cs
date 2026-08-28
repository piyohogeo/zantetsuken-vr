using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class TerminalIntentOwnershipSnapshotTests
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

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

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

        // ---- Registry / queue operation helpers ----

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

        private static Exception CreateOwnershipSnapshotException(object queue, int producerRetainedPrivateBufferCount)
        {
            try
            {
                CreateOwnershipSnapshot(queue, producerRetainedPrivateBufferCount);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object GetIssuedSnapshot(object queue)
        {
            PropertyInfo prop = GetQueueType().GetProperty("IssuedOwnershipSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null);
            return prop.GetValue(queue);
        }

        private static object GetQueueField(object queue, string name)
        {
            FieldInfo field = GetQueueType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, name + " field not found.");
            return field.GetValue(queue);
        }

        private static void SetQueueField(object queue, string name, object value)
        {
            FieldInfo field = GetQueueType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, name + " field not found.");
            field.SetValue(queue, value);
        }

        private static string DescribeMirror(object queue)
        {
            FieldInfo field = GetQueueType().GetField("_mirror", BindingFlags.NonPublic | BindingFlags.Instance);
            Array mirror = (Array)field.GetValue(queue);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < mirror.Length; i++)
            {
                object entry = mirror.GetValue(i);
                Type entryType = entry.GetType();
                bool occupied = (bool)entryType.GetField("Occupied", BindingFlags.Public | BindingFlags.Instance).GetValue(entry);
                if (occupied)
                {
                    bool isTerminal = (bool)entryType.GetField("IsTerminal", BindingFlags.Public | BindingFlags.Instance).GetValue(entry);
                    int accepted = (int)entryType.GetField("AcceptedCount", BindingFlags.Public | BindingFlags.Instance).GetValue(entry);
                    int outstanding = (int)entryType.GetField("OutstandingCount", BindingFlags.Public | BindingFlags.Instance).GetValue(entry);
                    sb.Append(i).Append(':').Append(isTerminal ? 'T' : 'F').Append('/').Append(accepted).Append('/').Append(outstanding).Append(';');
                }
            }

            return sb.ToString();
        }

        // ---- Snapshot direct-construction helpers ----

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

        private static Exception SnapshotCtorException(object queue, long testRunId, int queueCount, int accepted, int processed, int queueOwned, int producerRetained)
        {
            try
            {
                CreateSnapshotRaw(queue, testRunId, queueCount, accepted, processed, queueOwned, producerRetained);
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
            public int QueueMaxInFlight;
            public int MaxDraftPerRun;
            public long TestRunId;

            public object Run;
            public object Registry;
            public object Queue;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static Scope NewScope(int queueMaxInFlight = 2, int maxDraftPerRun = 8, long testRunId = 1)
        {
            Scope scope = new Scope();
            scope.QueueMaxInFlight = queueMaxInFlight;
            scope.MaxDraftPerRun = maxDraftPerRun;
            scope.TestRunId = testRunId;
            return scope;
        }

        private static void BuildScope(Scope scope)
        {
            scope.Run = MakeRun(scope.TestRunId, captureProfileId: 5);
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, scope.MaxDraftPerRun, scope.MaxDraftPerRun));
            scope.Queue = CreateQueue(scope.Registry, MakeProfile(5, scope.QueueMaxInFlight, scope.MaxDraftPerRun));
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
        /// Commits two drafts, enqueues one drop and one stage intent, closes the
        /// queue after producer join, and drains it. The stage entry is returned
        /// caller-owned.
        /// </summary>
        private static object SetupDrainedClosedQueue(Scope scope)
        {
            CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
            CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 2);

            object entry = MakeEntryTracked(scope, 2, 1, 16);
            Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
            Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(2), entry)), Is.EqualTo(0));

            BeginProducerDrain(scope.Queue);
            CloseAfterProducerJoin(scope.Queue);

            object dequeued;
            Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);
            Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);

            return entry;
        }

        // ---- Snapshot contracts ----

        [Test]
        public void Snapshot_AllValuesAndIsValid()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = SetupDrainedClosedQueue(scope);
                ((IDisposable)entry).Dispose();

                object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
                Assert.That(snapshot, Is.Not.Null);

                Assert.That((long)GetProperty(snapshot, "TestRunId"), Is.EqualTo(1));
                Assert.That((int)GetProperty(snapshot, "QueueCount"), Is.EqualTo(0));
                Assert.That((int)GetProperty(snapshot, "RunAcceptedIntentCount"), Is.EqualTo(2));
                Assert.That((int)GetProperty(snapshot, "RunProcessedIntentCount"), Is.EqualTo(2));
                Assert.That((int)GetProperty(snapshot, "QueueOwnedPrivateBufferCount"), Is.EqualTo(0));
                Assert.That((int)GetProperty(snapshot, "ProducerRetainedPrivateBufferCount"), Is.EqualTo(0));
                Assert.That((bool)GetProperty(snapshot, "IsValid"), Is.True);
            });
        }

        [Test]
        public void Snapshot_NoPublicConstructorOrSetters()
        {
            Type type = GetSnapshotType();
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Snapshot_IssuedByReferencesQueue()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = SetupDrainedClosedQueue(scope);
                ((IDisposable)entry).Dispose();

                object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
                Assert.That(ReferenceEquals(GetProperty(snapshot, "IssuedBy"), scope.Queue), Is.True);
            });
        }

        [Test]
        public void Snapshot_NotIDisposable_NoStaticMutableState()
        {
            Type type = GetSnapshotType();
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic), Is.Empty);
        }

        [Test]
        public void Snapshot_HoldsOnlyPrimitivesAndQueue()
        {
            FieldInfo[] fields = GetSnapshotType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(7));

            foreach (FieldInfo field in fields)
            {
                Assert.That(
                    field.FieldType == GetQueueType() || field.FieldType == typeof(long) || field.FieldType == typeof(int),
                    Is.True,
                    "Unexpected snapshot field type: " + field.FieldType.Name);
            }
        }

        // ---- Snapshot constructor direct contracts ----

        [Test]
        public void SnapshotCtor_IssuedByNull_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SnapshotCtorException(null, 1, 0, 0, 0, 0, 0);
                Assert.That(ex, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("issuedBy"));
            });
        }

        [Test]
        public void SnapshotCtor_TestRunIdNonPositive_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                foreach (long testRunId in new[] { 0L, -1L })
                {
                    Exception ex = SnapshotCtorException(scope.Queue, testRunId, 0, 0, 0, 0, 0);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("testRunId"));
                }
            });
        }

        [Test]
        public void SnapshotCtor_NegativeCounts_Rejected_ParamName()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex;

                ex = SnapshotCtorException(scope.Queue, 1, -1, 0, 0, 0, 0);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("queueCount"));

                ex = SnapshotCtorException(scope.Queue, 1, 0, -1, 0, 0, 0);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("runAcceptedIntentCount"));

                ex = SnapshotCtorException(scope.Queue, 1, 0, 0, -1, 0, 0);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("runProcessedIntentCount"));

                ex = SnapshotCtorException(scope.Queue, 1, 0, 0, 0, -1, 0);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("queueOwnedPrivateBufferCount"));

                ex = SnapshotCtorException(scope.Queue, 1, 0, 0, 0, 0, -1);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("producerRetainedPrivateBufferCount"));
            });
        }

        [Test]
        public void SnapshotCtor_NonZeroQueueCount_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SnapshotCtorException(scope.Queue, 1, 1, 0, 0, 0, 0);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("queueCount"));
            });
        }

        [Test]
        public void SnapshotCtor_AcceptedProcessedMismatch_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SnapshotCtorException(scope.Queue, 1, 0, 1, 0, 0, 0);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("runProcessedIntentCount"));
            });
        }

        [Test]
        public void SnapshotCtor_NonZeroQueueOwned_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SnapshotCtorException(scope.Queue, 1, 0, 0, 0, 1, 0);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("queueOwnedPrivateBufferCount"));
            });
        }

        [Test]
        public void SnapshotCtor_NonZeroProducerRetained_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = SnapshotCtorException(scope.Queue, 1, 0, 0, 0, 0, 1);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("producerRetainedPrivateBufferCount"));
            });
        }

        // ---- CreateOwnershipSnapshot contracts ----

        [Test]
        public void CreateOwnershipSnapshot_InitialState_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_ProducerDraining_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                BeginProducerDrain(scope.Queue);
                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_ClosedButNonEmpty_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_StageIntentStillQueued_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_NegativeProducerRetained_ArgumentOutOfRange()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                Exception ex = CreateOwnershipSnapshotException(scope.Queue, -1);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("producerRetainedPrivateBufferCount"));
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_PositiveProducerRetained_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 1), Is.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_AcceptedProcessedMismatch_Rejected_NoChange()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);

                SetQueueField(scope.Queue, "_runAcceptedIntentCount", 2); // accepted 2 vs processed 1

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<InvalidOperationException>());
                Assert.That(GetIssuedSnapshot(scope.Queue), Is.Null);
                Assert.That((int)GetQueueField(scope.Queue, "_runAcceptedIntentCount"), Is.EqualTo(2)); // unchanged
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_QueueOwnedBufferNonZero_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);

                SetQueueField(scope.Queue, "_queueOwnedPrivateBufferCount", 1);

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<InvalidOperationException>());
                Assert.That(GetIssuedSnapshot(scope.Queue), Is.Null);
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_StageIntentDequeuedProducerRetainedOne_Rejected()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True); // entry now caller-owned

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 1), Is.TypeOf<InvalidOperationException>());
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True); // still caller-owned
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_Success_AllZero()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = SetupDrainedClosedQueue(scope);
                ((IDisposable)entry).Dispose();

                object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
                Assert.That(snapshot, Is.Not.Null);
                Assert.That((bool)GetProperty(snapshot, "IsValid"), Is.True);
                Assert.That(ReferenceEquals(GetIssuedSnapshot(scope.Queue), snapshot), Is.True);
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_SecondCall_SameInstance()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = SetupDrainedClosedQueue(scope);
                ((IDisposable)entry).Dispose();

                object first = CreateOwnershipSnapshot(scope.Queue, 0);
                object second = CreateOwnershipSnapshot(scope.Queue, 0);

                Assert.That(ReferenceEquals(first, second), Is.True);
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_CountersMirrorEntryOwnershipUnchanged()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                CommitAndRegister(scope.Queue, scope.Registry, scope.Run, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True); // entry caller-owned, not disposed

                string mirrorBefore = DescribeMirror(scope.Queue);
                int acceptedBefore = (int)GetProperty(scope.Queue, "RunAcceptedIntentCount");
                int processedBefore = (int)GetProperty(scope.Queue, "RunProcessedIntentCount");
                int queueOwnedBefore = (int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount");
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True);

                object snapshot = CreateOwnershipSnapshot(scope.Queue, 0);
                Assert.That(snapshot, Is.Not.Null);

                Assert.That(DescribeMirror(scope.Queue), Is.EqualTo(mirrorBefore));
                Assert.That((int)GetProperty(scope.Queue, "RunAcceptedIntentCount"), Is.EqualTo(acceptedBefore));
                Assert.That((int)GetProperty(scope.Queue, "RunProcessedIntentCount"), Is.EqualTo(processedBefore));
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(queueOwnedBefore));
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True); // snapshot did not dispose the caller-owned entry
            });
        }

        [Test]
        public void CreateOwnershipSnapshot_AfterDisposeStarted_ObjectDisposed()
        {
            Scope scope = NewScope();
            RunBody(scope, () =>
            {
                object entry = SetupDrainedClosedQueue(scope);
                ((IDisposable)entry).Dispose();

                ((IDisposable)scope.Queue).Dispose();

                Assert.That(CreateOwnershipSnapshotException(scope.Queue, 0), Is.TypeOf<ObjectDisposedException>());
            });
        }
    }
}
