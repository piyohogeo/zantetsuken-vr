using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftTerminalIntentQueueTests
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

        private static Type GetQueueStateType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntentQueueState");

        private static Type GetStatusType() => GetTypeFromAssembly("TerminalIntentEnqueueStatus");

        private static Type GetIntentType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntent");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

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

        // ---- Registry helpers ----

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

        private static void MarkDropped(object registry, CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetRegistryType().GetMethod("MarkDropped", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { request, reason });
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

        private static bool TryMarkStaged(object registry, CaptureFrameRequest request, object store, object entry)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryMarkStaged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(registry, new object[] { request, store, entry });
        }

        // ---- Queue operation helpers ----

        private static void RegisterPendingDraft(object queue, object draft)
        {
            MethodInfo method = GetQueueType().GetMethod("RegisterPendingDraft", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(queue, new object[] { draft });
        }

        private static void MarkDraftTerminal(object queue, CaptureFrameRequest request)
        {
            MethodInfo method = GetQueueType().GetMethod("MarkDraftTerminal", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(queue, new object[] { request });
        }

        private static int EnqueueTerminalIntent(object queue, object intent)
        {
            MethodInfo method = GetQueueType().GetMethod("EnqueueTerminalIntent", BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)method.Invoke(queue, new object[] { intent });
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

        private static long GetIntentCaptureFrameId(object intent)
        {
            object request = GetProperty(intent, "Request");
            Assert.That(request, Is.InstanceOf<CaptureFrameRequest>());
            return ((CaptureFrameRequest)request).TraceContext.CaptureFrameId;
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

        private sealed class QueueScope
        {
            public object Run;
            public object Registry;
            public object Queue;
            public readonly List<object> AllEntries = new List<object>();
        }

        private static QueueScope NewScope(int queueMaxInFlight, int maxDraftPerRun, long testRunId = 1)
        {
            QueueScope scope = new QueueScope();
            scope.Run = MakeRun(testRunId, captureProfileId: 5);
            // The registry is only a fixture here: give it enough pending slots
            // (up to maxDraftPerRun) so many drafts can be registered while the
            // queue capacity stays pinned to 2 * queueMaxInFlight.
            scope.Registry = CreateRegistry(scope.Run, MakeProfile(5, maxDraftPerRun, maxDraftPerRun));
            scope.Queue = CreateQueue(scope.Registry, MakeProfile(5, queueMaxInFlight, maxDraftPerRun));
            return scope;
        }

        private static object MakeEntryTracked(QueueScope scope, long captureFrameId, long testRunId, int pngLength)
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

        private static object CommitAndRegister(QueueScope scope, long captureFrameId)
        {
            CaptureFrameRequest request = MakeRequest(captureFrameId);
            object draft = CommitDraft(scope.Registry, scope.Run, request);
            RegisterPendingDraft(scope.Queue, draft);
            return draft;
        }

        private static object MakePoisonedEntryTracked(QueueScope scope, long captureFrameId, long testRunId, int pngLength)
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

        private static void ClearEntryPngBytes(object entry)
        {
            FieldInfo field = GetEntryType().GetField("_pngBytes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(entry, default(NativeArray<byte>));
        }

        private static void AssertObjectDisposed(Action action)
        {
            try
            {
                action();
                Assert.Fail("Expected ObjectDisposedException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>());
            }
        }

        private static Exception[] CleanupScope(QueueScope scope)
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

        private static void RunBody(QueueScope scope, Action body)
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

        // ---- State enum contracts ----

        [Test]
        public void State_UnderlyingTypeIsInt()
        {
            Assert.That(Enum.GetUnderlyingType(GetQueueStateType()), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void State_NamesAndValues_MatchExactly()
        {
            Type type = GetQueueStateType();
            Assert.That(Enum.GetName(type, 0), Is.EqualTo("Accepting"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("ProducerDraining"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("Closed"));
        }

        [Test]
        public void State_NoAliasesOrGaps()
        {
            Type type = GetQueueStateType();
            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(3));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(3));
            for (int i = 0; i <= 2; i++)
            {
                Assert.That(Enum.IsDefined(type, i), Is.True);
            }

            Assert.That(Enum.IsDefined(type, 3), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Constructor ----

        [Test]
        public void Ctor_NullRegistry_Rejected()
        {
            ConstructorInfo ctor = GetQueueType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRegistryType(), typeof(CaptureTraceProfile) }, null);

            try
            {
                ctor.Invoke(new object[] { null, MakeProfile() });
                Assert.Fail("Expected ArgumentNullException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("draftRegistry"));
            }
        }

        [Test]
        public void Ctor_NullProfile_Rejected()
        {
            object registry = CreateRegistry(MakeRun(), MakeProfile());
            ConstructorInfo ctor = GetQueueType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRegistryType(), typeof(CaptureTraceProfile) }, null);

            try
            {
                ctor.Invoke(new object[] { registry, null });
                Assert.Fail("Expected ArgumentNullException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("profile"));
            }
        }

        [Test]
        public void Ctor_ProfileIdMismatch_Rejected()
        {
            object registry = CreateRegistry(MakeRun(captureProfileId: 5), MakeProfile(captureProfileId: 5));
            ConstructorInfo ctor = GetQueueType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRegistryType(), typeof(CaptureTraceProfile) }, null);

            try
            {
                ctor.Invoke(new object[] { registry, MakeProfile(captureProfileId: 6) });
                Assert.Fail("Expected ArgumentException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("profile"));
            }
        }

        [Test]
        public void Ctor_CapacityIsTwiceMaxInFlight()
        {
            QueueScope scope = NewScope(3, 8);
            Assert.That((int)GetProperty(scope.Queue, "Capacity"), Is.EqualTo(6));
            Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(0));
            Assert.That((bool)GetProperty(scope.Queue, "IsCreated"), Is.True);
            ((IDisposable)scope.Queue).Dispose();
        }

        [Test]
        public void Ctor_Overflow_RejectedBeforeAllocation()
        {
            object registry = CreateRegistry(MakeRun(captureProfileId: 5), MakeProfile(captureProfileId: 5));

            CaptureTraceProfile corrupt = (CaptureTraceProfile)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(CaptureTraceProfile));
            typeof(CaptureTraceProfile).GetField("_captureProfileId", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(corrupt, 5);
            typeof(CaptureTraceProfile).GetField("_maxInFlightDraftCount", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(corrupt, int.MaxValue);

            ConstructorInfo ctor = GetQueueType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRegistryType(), typeof(CaptureTraceProfile) }, null);

            try
            {
                ctor.Invoke(new object[] { registry, corrupt });
                Assert.Fail("Expected overflow rejection.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex.InnerException).ParamName, Is.EqualTo("profile"));
            }
        }

        // ---- Mirror ----

        [Test]
        public void RegisterPendingDraft_RegistersAndMatches()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                object intent = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);

                Assert.That(EnqueueTerminalIntent(scope.Queue, intent), Is.EqualTo(0)); // Accepted
            });
        }

        [Test]
        public void RegisterPendingDraft_Duplicate_Throws()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                object draft = CommitAndRegister(scope, 1);

                try
                {
                    RegisterPendingDraft(scope.Queue, draft);
                    Assert.Fail("Expected ArgumentException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                }
            });
        }

        [Test]
        public void RegisterPendingDraft_NotInRegistry_Throws()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                object draft = MakeDraft(scope.Run, MakeRequest(1));

                try
                {
                    RegisterPendingDraft(scope.Queue, draft);
                    Assert.Fail("Expected InvalidOperationException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
                }
            });
        }

        [Test]
        public void RegisterPendingDraft_DroppedDraft_Rejected()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                object draft = CommitDraft(scope.Registry, scope.Run, MakeRequest(1));
                MarkDropped(scope.Registry, MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);

                try
                {
                    RegisterPendingDraft(scope.Queue, draft);
                    Assert.Fail("Expected InvalidOperationException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
                }
            });
        }

        [Test]
        public void RegisterPendingDraft_StagedDraft_Rejected()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                object draft = CommitDraft(scope.Registry, scope.Run, MakeRequest(1));
                object store = CreateStore(scope.Run, 4, 4096);
                try
                {
                    object entry = MakeEntryTracked(scope, 1, 1, 16);
                    Assert.That(TryMarkStaged(scope.Registry, MakeRequest(1), store, entry), Is.True);

                    try
                    {
                        RegisterPendingDraft(scope.Queue, draft);
                        Assert.Fail("Expected InvalidOperationException.");
                    }
                    catch (TargetInvocationException ex)
                    {
                        Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());
                    }
                }
                finally
                {
                    ((IDisposable)store).Dispose();
                }
            });
        }

        // ---- Enqueue: InvalidIntent ----

        [Test]
        public void Enqueue_NullIntent_InvalidIntent()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                Assert.That(EnqueueTerminalIntent(scope.Queue, null), Is.EqualTo(5)); // InvalidIntent
                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(0));
            });
        }

        [Test]
        public void Enqueue_UnknownDraftId_InvalidIntent()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                object intent = CreateDropIntent(MakeRequest(99), CaptureFrameDropReason.CaptureCancelled);
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent), Is.EqualTo(5)); // InvalidIntent
            });
        }

        [Test]
        public void Enqueue_RequestMismatch_InvalidIntent()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);

                // Same capture frame ID but different request (different eye).
                CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, 1, 30, 1, 5, 6, 7, 8u, 9);
                CaptureFrameRequest different = new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Right, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
                object intent = CreateDropIntent(different, CaptureFrameDropReason.CaptureCancelled);

                Assert.That(EnqueueTerminalIntent(scope.Queue, intent), Is.EqualTo(5)); // InvalidIntent
            });
        }

        [Test]
        public void Enqueue_DisposedEntry_InvalidIntent()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                object intent = CreateStageIntent(MakeRequest(1), entry);
                ((IDisposable)entry).Dispose();

                Assert.That(EnqueueTerminalIntent(scope.Queue, intent), Is.EqualTo(5)); // InvalidIntent
            });
        }

        // ---- Enqueue: priority ----

        [Test]
        public void Enqueue_Closed_ValidIntent_RunNotAccepting_InvalidIntentFirst()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);

                // Invalid intent wins over RunNotAccepting.
                Assert.That(EnqueueTerminalIntent(scope.Queue, null), Is.EqualTo(5)); // InvalidIntent

                // Valid intent in Closed state.
                object intent = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent), Is.EqualTo(4)); // RunNotAccepting
            });
        }

        [Test]
        public void Enqueue_TerminalDraft_DraftAlreadyTerminal_EvenWhenFull()
        {
            QueueScope scope = NewScope(1, 8); // capacity 2
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                CommitAndRegister(scope, 2);

                object intent1 = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);
                object intent2 = CreateDropIntent(MakeRequest(2), CaptureFrameDropReason.CaptureCancelled);
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent1), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent2), Is.EqualTo(0)); // queue now full

                // Make draft 1 terminal, then try to enqueue another intent for it.
                MarkDropped(scope.Registry, MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);
                MarkDraftTerminal(scope.Queue, MakeRequest(1));

                object intent3 = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent3), Is.EqualTo(2)); // DraftAlreadyTerminal
            });
        }

        [Test]
        public void Enqueue_ThirdIntent_IntentLimitExceeded_EvenWhenFull()
        {
            QueueScope scope = NewScope(1, 8); // capacity 2
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);

                object intent1 = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed);
                object intent2 = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngStagingStoreFull);
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent1), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent2), Is.EqualTo(0)); // queue now full

                object intent3 = CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled);
                Assert.That(EnqueueTerminalIntent(scope.Queue, intent3), Is.EqualTo(3)); // IntentLimitExceeded
            });
        }

        [Test]
        public void Enqueue_QueueFullOnly_Backpressured()
        {
            QueueScope scope = NewScope(1, 8); // capacity 2
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                CommitAndRegister(scope, 2);
                CommitAndRegister(scope, 3);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(2), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0)); // full

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(3), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(1)); // Backpressured
                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(2));
                Assert.That((int)GetProperty(scope.Queue, "RunAcceptedIntentCount"), Is.EqualTo(2));
            });
        }

        [Test]
        public void Enqueue_BackpressuredThenDrain_RetrySucceeds()
        {
            QueueScope scope = NewScope(1, 8); // capacity 2
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                CommitAndRegister(scope, 2);
                CommitAndRegister(scope, 3);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(2), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));

                object retry = CreateDropIntent(MakeRequest(3), CaptureFrameDropReason.CaptureCancelled);
                Assert.That(EnqueueTerminalIntent(scope.Queue, retry), Is.EqualTo(1)); // Backpressured

                // Drain one, then retry the same intent.
                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);
                Assert.That(EnqueueTerminalIntent(scope.Queue, retry), Is.EqualTo(0)); // Accepted on retry
            });
        }

        // ---- Accepted / FIFO / counters ----

        [Test]
        public void Enqueue_Accepted_TransfersStageEntryOwnership()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                object intent = CreateStageIntent(MakeRequest(1), entry);

                Assert.That(EnqueueTerminalIntent(scope.Queue, intent), Is.EqualTo(0));
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(1));

                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True);
                Assert.That(ReferenceEquals(dequeued, intent), Is.True);
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(0));
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True); // not disposed by queue
            });
        }

        [Test]
        public void Enqueue_FIFO()
        {
            QueueScope scope = NewScope(2, 8);
            RunBody(scope, () =>
            {
                for (long id = 1; id <= 4; id++)
                {
                    CommitAndRegister(scope, id);
                }

                for (long id = 1; id <= 4; id++)
                {
                    Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(id), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));
                }

                for (long id = 1; id <= 4; id++)
                {
                    object intent;
                    Assert.That(TryDequeue(scope.Queue, out intent), Is.True);
                    Assert.That(GetIntentCaptureFrameId(intent), Is.EqualTo(id));
                }

                object empty;
                Assert.That(TryDequeue(scope.Queue, out empty), Is.False);
            });
        }

        [Test]
        public void Dequeue_OutstandingDecreases_AcceptedTotalDoesNot()
        {
            QueueScope scope = NewScope(1, 8);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngStagingStoreFull)), Is.EqualTo(0));
                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(2));

                object first;
                Assert.That(TryDequeue(scope.Queue, out first), Is.True);
                Assert.That((int)GetProperty(scope.Queue, "RunProcessedIntentCount"), Is.EqualTo(1));

                // Accepted total stays 2 even after processing.
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(3)); // IntentLimitExceeded
            });
        }

        [Test]
        public void Enqueue_AfterTwoProcessed_ThirdPermanentlyLimitExceeded()
        {
            QueueScope scope = NewScope(1, 8);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngEncodeFailed)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.PngStagingStoreFull)), Is.EqualTo(0));

                object first, second;
                Assert.That(TryDequeue(scope.Queue, out first), Is.True);
                Assert.That(TryDequeue(scope.Queue, out second), Is.True);
                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(0));

                // Even with an empty queue, the accepted total is 2.
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(3)); // IntentLimitExceeded
            });
        }

        [Test]
        public void Enqueue_StageDropMixed_Counters()
        {
            QueueScope scope = NewScope(2, 8);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                CommitAndRegister(scope, 2);
                CommitAndRegister(scope, 3);

                object entry1 = MakeEntryTracked(scope, 1, 1, 16);
                object entry3 = MakeEntryTracked(scope, 3, 1, 16);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry1)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(2), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(3), entry3)), Is.EqualTo(0));

                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(3));
                Assert.That((int)GetProperty(scope.Queue, "RunAcceptedIntentCount"), Is.EqualTo(3));
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(2)); // two stage entries

                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True); // stage 1
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(1));
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True); // drop 2
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(1));
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True); // stage 3
                Assert.That((int)GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"), Is.EqualTo(0));
                Assert.That((int)GetProperty(scope.Queue, "RunProcessedIntentCount"), Is.EqualTo(3));
            });
        }

        // ---- Lifecycle ----

        [Test]
        public void Lifecycle_NormalTransition()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                Assert.That((int)GetProperty(scope.Queue, "State"), Is.EqualTo(0)); // Accepting
                BeginProducerDrain(scope.Queue);
                Assert.That((int)GetProperty(scope.Queue, "State"), Is.EqualTo(1)); // ProducerDraining
                CloseAfterProducerJoin(scope.Queue);
                Assert.That((int)GetProperty(scope.Queue, "State"), Is.EqualTo(2)); // Closed
            });
        }

        [Test]
        public void Lifecycle_OrderViolation_Throws()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                try { CloseAfterProducerJoin(scope.Queue); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>()); }

                BeginProducerDrain(scope.Queue);
                try { BeginProducerDrain(scope.Queue); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>()); }

                CloseAfterProducerJoin(scope.Queue);
                try { CloseAfterProducerJoin(scope.Queue); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>()); }
            });
        }

        [Test]
        public void Lifecycle_Closed_DequeueStillWorks()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)), Is.EqualTo(0));

                BeginProducerDrain(scope.Queue);
                CloseAfterProducerJoin(scope.Queue);

                object intent;
                Assert.That(TryDequeue(scope.Queue, out intent), Is.True);
                Assert.That(GetIntentCaptureFrameId(intent), Is.EqualTo(1));
            });
        }

        // ---- Dispose ----

        [Test]
        public void Dispose_OnlyFreesQueueOwnedEntries()
        {
            QueueScope scope = NewScope(1, 8); // capacity 2
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                CommitAndRegister(scope, 2);
                CommitAndRegister(scope, 3);

                object entry1 = MakeEntryTracked(scope, 1, 1, 16);
                object entry2 = MakeEntryTracked(scope, 2, 1, 16);
                object entry3 = MakeEntryTracked(scope, 3, 1, 16);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry1)), Is.EqualTo(0)); // queue owns entry1
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(2), entry2)), Is.EqualTo(0)); // queue owns entry2
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(3), entry3)), Is.EqualTo(1)); // backpressured, caller owns entry3

                object dequeued;
                Assert.That(TryDequeue(scope.Queue, out dequeued), Is.True); // entry1 now caller-owned

                ((IDisposable)scope.Queue).Dispose();

                Assert.That((bool)GetProperty(entry2, "IsCreated"), Is.False); // queue-owned, disposed
                Assert.That((bool)GetProperty(entry1, "IsCreated"), Is.True); // dequeued, caller-owned
                Assert.That((bool)GetProperty(entry3, "IsCreated"), Is.True); // backpressured, caller-owned
            });
        }

        [Test]
        public void Dispose_Idempotent()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                object entry = MakeEntryTracked(scope, 1, 1, 16);
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entry)), Is.EqualTo(0));

                Assert.DoesNotThrow(() => ((IDisposable)scope.Queue).Dispose());
                Assert.DoesNotThrow(() => ((IDisposable)scope.Queue).Dispose());
                Assert.That((bool)GetProperty(scope.Queue, "IsCreated"), Is.False);
            });
        }

        [Test]
        public void Disposed_ApiThrows()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                ((IDisposable)scope.Queue).Dispose();

                Assert.That((bool)GetProperty(scope.Queue, "IsCreated"), Is.False);

                foreach (string prop in new[] { "State", "Capacity", "Count", "RunAcceptedIntentCount", "RunProcessedIntentCount", "QueueOwnedPrivateBufferCount" })
                {
                    try { GetProperty(scope.Queue, prop); Assert.Fail(); }
                    catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>(), prop); }
                }

                object intent;
                try { TryDequeue(scope.Queue, out intent); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>()); }

                // Enqueue is a producer API: it must throw ObjectDisposedException
                // rather than returning a terminal status.
                try { EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>()); }

                try { RegisterPendingDraft(scope.Queue, MakeDraft(scope.Run, MakeRequest(1))); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>()); }

                try { MarkDraftTerminal(scope.Queue, MakeRequest(1)); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>()); }

                try { BeginProducerDrain(scope.Queue); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>()); }

                try { CloseAfterProducerJoin(scope.Queue); Assert.Fail(); }
                catch (TargetInvocationException ex) { Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>()); }
            });
        }

        [Test]
        public void Enqueue_AfterDispose_NullIntent_ThrowsObjectDisposedNotInvalid()
        {
            QueueScope scope = NewScope(2, 4);
            RunBody(scope, () =>
            {
                ((IDisposable)scope.Queue).Dispose();

                // Disposed check precedes validation: even a null (invalid)
                // intent yields ObjectDisposedException, not InvalidIntent.
                try
                {
                    EnqueueTerminalIntent(scope.Queue, null);
                    Assert.Fail("Expected ObjectDisposedException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ObjectDisposedException>());
                }
            });
        }

        [Test]
        public void Dispose_PartialFailure_NormalApisFailClosed_RetrySucceeds()
        {
            QueueScope scope = NewScope(2, 8);
            RunBody(scope, () =>
            {
                CommitAndRegister(scope, 1);
                CommitAndRegister(scope, 2);

                object entryA = MakeEntryTracked(scope, 1, 1, 16);
                object entryB = MakePoisonedEntryTracked(scope, 2, 1, 16);

                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(1), entryA)), Is.EqualTo(0));
                Assert.That(EnqueueTerminalIntent(scope.Queue, CreateStageIntent(MakeRequest(2), entryB)), Is.EqualTo(0));

                // First Dispose: entry A succeeds (its slot is cleared), entry B fails.
                Assert.That(Assert.Catch(() => ((IDisposable)scope.Queue).Dispose()), Is.Not.Null);

                // Not fully disposed yet, but every normal API now fails closed.
                Assert.That((bool)GetProperty(scope.Queue, "IsCreated"), Is.False);
                AssertObjectDisposed(() => GetProperty(scope.Queue, "State"));
                AssertObjectDisposed(() => GetProperty(scope.Queue, "Count"));
                AssertObjectDisposed(() => GetProperty(scope.Queue, "RunAcceptedIntentCount"));
                AssertObjectDisposed(() => GetProperty(scope.Queue, "RunProcessedIntentCount"));
                AssertObjectDisposed(() => GetProperty(scope.Queue, "QueueOwnedPrivateBufferCount"));
                AssertObjectDisposed(() => EnqueueTerminalIntent(scope.Queue, CreateDropIntent(MakeRequest(1), CaptureFrameDropReason.CaptureCancelled)));
                AssertObjectDisposed(() => { object d; TryDequeue(scope.Queue, out d); });

                // Resolve the transient failure: the poisoned entry's allocation is
                // already freed, so clearing its dangling view lets Dispose succeed.
                ClearEntryPngBytes(entryB);

                // Re-Dispose completes and only then is the queue fully disposed.
                Assert.DoesNotThrow(() => ((IDisposable)scope.Queue).Dispose());
                Assert.That((bool)GetProperty(scope.Queue, "IsCreated"), Is.False);
            });
        }

        // ---- MPSC concurrency ----

        [Test]
        public void Enqueue_Mpsc_ConcurrentProducers_NoLossOrDuplication()
        {
            const int ProducerCount = 4;
            const int IntentsPerProducer = 4;
            const int TotalIntents = ProducerCount * IntentsPerProducer;

            QueueScope scope = NewScope(8, 32); // capacity 16, mirror 32
            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                for (long id = 1; id <= TotalIntents; id++)
                {
                    CommitAndRegister(scope, id);
                }

                object[] intents = new object[TotalIntents];
                for (int i = 0; i < TotalIntents; i++)
                {
                    intents[i] = CreateDropIntent(MakeRequest(i + 1), CaptureFrameDropReason.CaptureCancelled);
                }

                using (Barrier barrier = new Barrier(ProducerCount))
                {
                    Thread[] threads = new Thread[ProducerCount];
                    for (int t = 0; t < ProducerCount; t++)
                    {
                        int threadIndex = t;
                        threads[t] = new Thread(() =>
                        {
                            barrier.SignalAndWait();
                            for (int i = 0; i < IntentsPerProducer; i++)
                            {
                                int intentIndex = threadIndex * IntentsPerProducer + i;
                                EnqueueTerminalIntent(scope.Queue, intents[intentIndex]);
                            }
                        });
                        threads[t].Start();
                    }

                    foreach (Thread thread in threads)
                    {
                        thread.Join();
                    }
                }

                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(TotalIntents));
                Assert.That((int)GetProperty(scope.Queue, "RunAcceptedIntentCount"), Is.EqualTo(TotalIntents));

                HashSet<long> seen = new HashSet<long>();
                for (int i = 0; i < TotalIntents; i++)
                {
                    object intent;
                    Assert.That(TryDequeue(scope.Queue, out intent), Is.True);
                    long id = GetIntentCaptureFrameId(intent);
                    Assert.That(seen.Add(id), Is.True, "duplicate or missing intent id " + id);
                }

                Assert.That(seen.Count, Is.EqualTo(TotalIntents));
                Assert.That((int)GetProperty(scope.Queue, "Count"), Is.EqualTo(0));
                Assert.That((int)GetProperty(scope.Queue, "RunProcessedIntentCount"), Is.EqualTo(TotalIntents));

                object empty;
                Assert.That(TryDequeue(scope.Queue, out empty), Is.False);
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            errors = CleanupScope(scope);
            ThrowCleanupAndBody(body, errors);
        }
    }
}
