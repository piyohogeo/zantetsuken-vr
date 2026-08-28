using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRenderTargetDraftSchedulerTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetSchedulerType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftScheduler");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

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

        private static CaptureFrameProfile MakeFrameProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
        }

        private static CaptureTraceProfile MakeTraceProfile(int captureProfileId = 1, int maxInFlight = 2, int maxDraftPerRun = 10)
        {
            return new CaptureTraceProfile(captureProfileId, 4096, maxInFlight, maxDraftPerRun);
        }

        private static object MakeRun(int captureProfileId = 1)
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(TraceRunContext), typeof(long), typeof(int) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");

            TraceRunContext context = new TraceRunContext(
                1, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", 12345, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));
            return ctor.Invoke(new object[] { context, 100, captureProfileId });
        }

        private static object CreateRegistry(object run, CaptureTraceProfile traceProfile)
        {
            ConstructorInfo ctor = GetRegistryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRunType(), typeof(CaptureTraceProfile) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftRegistry constructor not found.");
            return ctor.Invoke(new object[] { run, traceProfile });
        }

        private static object CreateScheduler(object registry, CaptureFrameRequestQueue queue, CaptureFrameRenderTargetLeaseRegistry leaseRegistry, CaptureFrameTraceObserver observer)
        {
            ConstructorInfo ctor = GetSchedulerType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), typeof(CaptureFrameRequestQueue), typeof(CaptureFrameRenderTargetLeaseRegistry), typeof(CaptureFrameTraceObserver) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameRenderTargetDraftScheduler constructor not found.");
            return ctor.Invoke(new object[] { registry, queue, leaseRegistry, observer });
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
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraft constructor not found.");
            return ctor.Invoke(new object[] { run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), commitPathId });
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        // ---- Registry operation helpers ----

        private static bool RegistryTryReserve(object registry, out object reservation, out object rejectKind)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryReserve", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { null, null };
            bool ok = (bool)method.Invoke(registry, args);
            reservation = args[0];
            rejectKind = args[1];
            return ok;
        }

        private static void RegistryCommit(object registry, object reservation, object draft)
        {
            MethodInfo method = GetRegistryType().GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { reservation, draft });
        }

        private static bool RegistryTryGet(object registry, CaptureFrameRequest request, out object draft, out object status)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { request, null, null };
            bool ok = (bool)method.Invoke(registry, args);
            draft = args[1];
            status = args[2];
            return ok;
        }

        private static void SetEntryStatus(object registry, int index, int statusValue)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(entriesField, Is.Not.Null, "_entries field not found.");
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(index);
            FieldInfo statusField = entry.GetType().GetField("Status", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            statusField.SetValue(entry, Enum.ToObject(GetStatusType(), statusValue));
            entries.SetValue(entry, index);
        }

        private static int Count(object registry, string name)
        {
            return (int)GetProperty(registry, name);
        }

        private static void AssertRequestIdentical(CaptureFrameRequest a, CaptureFrameRequest b)
        {
            Assert.That(a.TraceContext.Timestamp, Is.EqualTo(b.TraceContext.Timestamp));
            Assert.That(a.TraceContext.UnityFrameId, Is.EqualTo(b.TraceContext.UnityFrameId));
            Assert.That(a.TraceContext.FixedStepId, Is.EqualTo(b.TraceContext.FixedStepId));
            Assert.That(a.TraceContext.ThreadId, Is.EqualTo(b.TraceContext.ThreadId));
            Assert.That(a.TraceContext.CaptureFrameId, Is.EqualTo(b.TraceContext.CaptureFrameId));
            Assert.That(a.TraceContext.OpenXRFrameId, Is.EqualTo(b.TraceContext.OpenXRFrameId));
            Assert.That(a.TraceContext.TestRunId, Is.EqualTo(b.TraceContext.TestRunId));
            Assert.That(a.TraceContext.SlashId, Is.EqualTo(b.TraceContext.SlashId));
            Assert.That(a.TraceContext.FrontEdgeId, Is.EqualTo(b.TraceContext.FrontEdgeId));
            Assert.That(a.TraceContext.ObjectId, Is.EqualTo(b.TraceContext.ObjectId));
            Assert.That(a.TraceContext.ObjectGeneration, Is.EqualTo(b.TraceContext.ObjectGeneration));
            Assert.That(a.TraceContext.TaskId, Is.EqualTo(b.TraceContext.TaskId));
            Assert.That(a.Source, Is.EqualTo(b.Source));
            Assert.That(a.Eye, Is.EqualTo(b.Eye));
            Assert.That(a.ImageRect.X, Is.EqualTo(b.ImageRect.X));
            Assert.That(a.ImageRect.Y, Is.EqualTo(b.ImageRect.Y));
            Assert.That(a.ImageRect.Width, Is.EqualTo(b.ImageRect.Width));
            Assert.That(a.ImageRect.Height, Is.EqualTo(b.ImageRect.Height));
            Assert.That(a.ArrayIndex, Is.EqualTo(b.ArrayIndex));
            Assert.That(a.PixelLayout.Format, Is.EqualTo(b.PixelLayout.Format));
            Assert.That(a.PixelLayout.Width, Is.EqualTo(b.PixelLayout.Width));
            Assert.That(a.PixelLayout.Height, Is.EqualTo(b.PixelLayout.Height));
        }

        private static void AssertLeaseIdentical(CaptureFrameRenderTargetLease a, CaptureFrameRenderTargetLease b)
        {
            Assert.That(a.SlotIndex, Is.EqualTo(b.SlotIndex));
            Assert.That(a.IsValid, Is.EqualTo(b.IsValid));

            FieldInfo ownerTokenField = typeof(CaptureFrameRenderTargetLease).GetField("_ownerToken", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo generationField = typeof(CaptureFrameRenderTargetLease).GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That((Guid)ownerTokenField.GetValue(a), Is.EqualTo((Guid)ownerTokenField.GetValue(b)));
            Assert.That((long)generationField.GetValue(a), Is.EqualTo((long)generationField.GetValue(b)));
        }

        // ---- Scheduler invoke helper ----

        private static bool InvokeTrySchedule(object scheduler, object draft, CaptureFrameRenderTargetLease lease)
        {
            MethodInfo method = GetSchedulerType().GetMethod("TrySchedule", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TrySchedule method not found.");
            return (bool)method.Invoke(scheduler, new object[] { draft, lease });
        }

        private static Exception TryScheduleException(object scheduler, object draft, CaptureFrameRenderTargetLease lease)
        {
            try
            {
                InvokeTrySchedule(scheduler, draft, lease);
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

        private sealed class DraftScope
        {
            public int PoolCapacity;
            public int LeaseCapacity;
            public int QueueCapacity;
            public int MaxInFlight;
            public int MaxDraftPerRun;
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public CaptureFrameRequestQueue RequestQueue;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public object Run;
            public object Registry;
            public object Scheduler;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<CaptureFrameRequest> Registered = new List<CaptureFrameRequest>();
        }

        private static DraftScope NewScope(int poolCapacity, int leaseCapacity, int queueCapacity, int maxInFlight = 2, int maxDraftPerRun = 10)
        {
            // No resources are created here: the scope is populated inside
            // RunDraftBody so that setup failures are also cleaned up.
            DraftScope scope = new DraftScope();
            scope.PoolCapacity = poolCapacity;
            scope.LeaseCapacity = leaseCapacity;
            scope.QueueCapacity = queueCapacity;
            scope.MaxInFlight = maxInFlight;
            scope.MaxDraftPerRun = maxDraftPerRun;
            return scope;
        }

        private static void BuildScope(DraftScope scope)
        {
            scope.Logger = new TraceLogger(8);
            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.RequestQueue = new CaptureFrameRequestQueue(scope.QueueCapacity);
            scope.Pool = new CaptureFrameRenderTargetPool(scope.PoolCapacity, MakeFrameProfile());
            scope.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(scope.LeaseCapacity, scope.Pool);
            scope.Run = MakeRun(captureProfileId: 1);
            scope.Registry = CreateRegistry(scope.Run, MakeTraceProfile(captureProfileId: 1, maxInFlight: scope.MaxInFlight, maxDraftPerRun: scope.MaxDraftPerRun));
            scope.Scheduler = CreateScheduler(scope.Registry, scope.RequestQueue, scope.LeaseRegistry, scope.Observer);
        }

        private static CaptureFrameRenderTargetLease Rent(DraftScope scope)
        {
            Assert.That(scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
            scope.Held.Add(lease);
            return lease;
        }

        private static void ReturnHeld(DraftScope scope, CaptureFrameRenderTargetLease lease)
        {
            scope.Pool.Return(lease);
            scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex);
        }

        private static object RegisterDraft(DraftScope scope, long captureFrameId)
        {
            CaptureFrameRequest request = MakeRequest(captureFrameId, testRunId: 1);
            object draft = MakeDraft(scope.Run, request);
            object reservation, rejectKind;
            Assert.That(RegistryTryReserve(scope.Registry, out reservation, out rejectKind), Is.True);
            RegistryCommit(scope.Registry, reservation, draft);
            return draft;
        }

        private static void TrackScheduled(DraftScope scope, CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
        {
            scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex);
            scope.Registered.Add(request);
        }

        private static Exception[] CleanupDraftScope(DraftScope scope)
        {
            Exception[] errors = null;

            for (int i = scope.Registered.Count - 1; i >= 0; i--)
            {
                CaptureFrameRequest request = scope.Registered[i];
                scope.Registered.RemoveAt(i);
                try
                {
                    if (scope.LeaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease lease))
                    {
                        scope.Pool.Return(lease);
                    }
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            for (int i = scope.Held.Count - 1; i >= 0; i--)
            {
                CaptureFrameRenderTargetLease lease = scope.Held[i];
                scope.Held.RemoveAt(i);
                try
                {
                    scope.Pool.Return(lease);
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            try { if (scope.Pool != null && scope.Pool.IsCreated) { scope.Pool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (scope.Logger != null && scope.Logger.IsCreated) { scope.Logger.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            return errors;
        }

        private static void RunDraftBody(DraftScope scope, Action body)
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

            Exception[] errors = CleanupDraftScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        // ---- Constructor contracts ----

        [Test]
        public void Constructor_FourNullDependencies_Rejected()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                ConstructorInfo ctor = GetSchedulerType().GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { GetRegistryType(), typeof(CaptureFrameRequestQueue), typeof(CaptureFrameRenderTargetLeaseRegistry), typeof(CaptureFrameTraceObserver) },
                    null);

                AssertNullParam(ctor, new object[] { null, scope.RequestQueue, scope.LeaseRegistry, scope.Observer }, "draftRegistry");
                AssertNullParam(ctor, new object[] { scope.Registry, null, scope.LeaseRegistry, scope.Observer }, "requestQueue");
                AssertNullParam(ctor, new object[] { scope.Registry, scope.RequestQueue, null, scope.Observer }, "leaseRegistry");
                AssertNullParam(ctor, new object[] { scope.Registry, scope.RequestQueue, scope.LeaseRegistry, null }, "traceObserver");
            });
        }

        private static void AssertNullParam(ConstructorInfo ctor, object[] args, string paramName)
        {
            try
            {
                ctor.Invoke(args);
                Assert.Fail("Expected ArgumentNullException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo(paramName));
            }
        }

        // ---- Draft validation stage ----

        [Test]
        public void NullDraft_Rejected_AllDependenciesUnchanged()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);

                Assert.That(TryScheduleException(scope.Scheduler, null, lease), Is.TypeOf<ArgumentNullException>());

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(0));
            });
        }

        [Test]
        public void UnregisteredDraft_Rejected_WithoutTouchingLeaseQueueTrace()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                object draft = MakeDraft(scope.Run, MakeRequest(1));

                Assert.That(TryScheduleException(scope.Scheduler, draft, lease), Is.TypeOf<InvalidOperationException>());

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void SameIdDifferentRequest_RejectedByRegistry()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                RegisterDraft(scope, 5);

                // Different request with the same capture frame ID.
                CaptureFrameTraceContext otherContext = new CaptureFrameTraceContext(1, 20, 3, 4, 5, 30, 1, 5, 6, 7, 8u, 9);
                CaptureFrameRequest otherRequest = new CaptureFrameRequest(otherContext, CaptureSource.OpenXRProjection, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
                object otherDraft = MakeDraft(scope.Run, otherRequest);

                Assert.That(TryScheduleException(scope.Scheduler, otherDraft, lease), Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void DifferentDraftInstance_Rejected()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                object registered = RegisterDraft(scope, 5);

                // A second instance with the identical request.
                object duplicate = MakeDraft(scope.Run, MakeRequest(5));

                Assert.That(TryScheduleException(scope.Scheduler, duplicate, lease), Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void NonPendingStatus_RejectedBeforeStart()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                object registered = RegisterDraft(scope, 5);

                // Force the registry entry to a non-pending status.
                SetEntryStatus(scope.Registry, 0, 1); // Staged

                Assert.That(TryScheduleException(scope.Scheduler, registered, lease), Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void ForeignPoolLease_RejectedByRegistry()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                CaptureFrameRenderTargetPool foreignPool = new CaptureFrameRenderTargetPool(1, MakeFrameProfile());
                try
                {
                    Assert.That(foreignPool.TryRent(out CaptureFrameRenderTargetLease foreignLease), Is.True);
                    try
                    {
                        Assert.That(TryScheduleException(scope.Scheduler, RegisterDraft(scope, 6), foreignLease), Is.TypeOf<InvalidOperationException>());
                        Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                    }
                    finally
                    {
                        foreignPool.Return(foreignLease);
                    }
                }
                finally
                {
                    foreignPool.Dispose();
                }
            });
        }

        // ---- Lease registry full ----

        [Test]
        public void LeaseRegistryFull_False_QueueTraceUntouched_DraftPending()
        {
            DraftScope scope = NewScope(2, 1, 2);
            RunDraftBody(scope, () =>
            {
                object draft = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease lease = Rent(scope);

                // Occupy the single lease registry slot with a different frame.
                CaptureFrameRequest otherRequest = MakeRequest(99, testRunId: 1);
                CaptureFrameRenderTargetLease otherLease = Rent(scope);
                Assert.That(scope.LeaseRegistry.TryRegister(otherRequest, otherLease), Is.True);
                TrackScheduled(scope, otherRequest, otherLease);

                bool result = InvokeTrySchedule(scope.Scheduler, draft, lease);
                Assert.That(result, Is.False);

                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(2));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));

                object found;
                object status;
                Assert.That(RegistryTryGet(scope.Registry, MakeRequest(5), out found, out status), Is.True);
                Assert.That((int)status, Is.EqualTo(0)); // still Pending
            });
        }

        // ---- Queue full (backpressure) ----

        [Test]
        public void QueueFull_False_TotalRejectedIncrements_NoDroppedTrace()
        {
            DraftScope scope = NewScope(2, 2, 1);
            RunDraftBody(scope, () =>
            {
                object first = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease firstLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, first, firstLease), Is.True);
                TrackScheduled(scope, MakeRequest(5), firstLease);

                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));

                object second = RegisterDraft(scope, 6);
                CaptureFrameRenderTargetLease secondLease = Rent(scope);
                bool result = InvokeTrySchedule(scope.Scheduler, second, secondLease);
                Assert.That(result, Is.False);

                Assert.That(scope.RequestQueue.TotalRejected, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1)); // second lease rolled back

                scope.Logger.Drain();
                for (int i = 0; i < scope.Logger.HistoryCount; i++)
                {
                    Assert.That(scope.Logger.GetHistoryEvent(i).EventType, Is.Not.EqualTo(TraceEventType.CaptureFrameDropped));
                }
            });
        }

        [Test]
        public void QueueFull_LeaseRolledBack_CallerCanReturnToPool()
        {
            DraftScope scope = NewScope(2, 2, 1);
            RunDraftBody(scope, () =>
            {
                object first = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease firstLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, first, firstLease), Is.True);
                TrackScheduled(scope, MakeRequest(5), firstLease);

                object second = RegisterDraft(scope, 6);
                CaptureFrameRenderTargetLease secondLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, second, secondLease), Is.False);

                // The caller still owns secondLease and can return it.
                ReturnHeld(scope, secondLease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void QueueFull_AfterFree_SameDraftAndLeaseRetrySucceeds()
        {
            DraftScope scope = NewScope(2, 2, 1);
            RunDraftBody(scope, () =>
            {
                object first = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease firstLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, first, firstLease), Is.True);
                TrackScheduled(scope, MakeRequest(5), firstLease);

                object second = RegisterDraft(scope, 6);
                CaptureFrameRenderTargetLease secondLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, second, secondLease), Is.False);

                // Free the queue and retry the same draft and lease.
                Assert.That(scope.RequestQueue.TryDequeue(out CaptureFrameRequest dequeued), Is.True);

                Assert.That(InvokeTrySchedule(scope.Scheduler, second, secondLease), Is.True);
                TrackScheduled(scope, MakeRequest(6), secondLease);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
            });
        }

        // ---- Success path ----

        [Test]
        public void Success_QueueHeadMatches_LeaseRegistered_DraftPending()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                object draft = RegisterDraft(scope, 5);
                CaptureFrameRequest request = MakeRequest(5);
                CaptureFrameRenderTargetLease lease = Rent(scope);

                Assert.That(InvokeTrySchedule(scope.Scheduler, draft, lease), Is.True);
                TrackScheduled(scope, request, lease);

                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.TryPeek(out CaptureFrameRequest head), Is.True);
                AssertRequestIdentical(head, request);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                AssertLeaseIdentical(registeredLease, lease);

                object found;
                object status;
                Assert.That(RegistryTryGet(scope.Registry, request, out found, out status), Is.True);
                Assert.That(ReferenceEquals(found, draft), Is.True);
                Assert.That((int)status, Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void Success_QueuedTraceExactlyOne()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                object draft = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease lease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, draft, lease), Is.True);
                TrackScheduled(scope, MakeRequest(5), lease);

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameQueued));
                Assert.That(scope.Logger.GetHistoryEvent(0).CaptureFrameId, Is.EqualTo(5L));
            });
        }

        [Test]
        public void FifoOrder_Preserved()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                object first = RegisterDraft(scope, 1);
                CaptureFrameRenderTargetLease firstLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, first, firstLease), Is.True);
                TrackScheduled(scope, MakeRequest(1), firstLease);

                object second = RegisterDraft(scope, 2);
                CaptureFrameRenderTargetLease secondLease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, second, secondLease), Is.True);
                TrackScheduled(scope, MakeRequest(2), secondLease);

                Assert.That(scope.RequestQueue.TryDequeue(out CaptureFrameRequest a), Is.True);
                Assert.That(scope.RequestQueue.TryDequeue(out CaptureFrameRequest b), Is.True);
                Assert.That(a.TraceContext.CaptureFrameId, Is.EqualTo(1L));
                Assert.That(b.TraceContext.CaptureFrameId, Is.EqualTo(2L));
            });
        }

        [Test]
        public void DuplicateSchedule_Rejected_NoQueueDuplicate()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                object draft = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease lease = Rent(scope);
                Assert.That(InvokeTrySchedule(scope.Scheduler, draft, lease), Is.True);
                TrackScheduled(scope, MakeRequest(5), lease);

                CaptureFrameRenderTargetLease secondLease = Rent(scope);
                Assert.That(TryScheduleException(scope.Scheduler, draft, secondLease), Is.TypeOf<InvalidOperationException>());
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
            });
        }

        // ---- Disposed logger rollback ----

        [Test]
        public void DisposedLogger_LeaseRolledBack_QueueUntouched_DraftPending()
        {
            DraftScope scope = NewScope(2, 2, 2);
            RunDraftBody(scope, () =>
            {
                object draft = RegisterDraft(scope, 5);
                CaptureFrameRenderTargetLease lease = Rent(scope);

                scope.Logger.Dispose();

                Assert.That(TryScheduleException(scope.Scheduler, draft, lease), Is.TypeOf<ObjectDisposedException>());
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));

                object found;
                object status;
                Assert.That(RegistryTryGet(scope.Registry, MakeRequest(5), out found, out status), Is.True);
                Assert.That((int)status, Is.EqualTo(0));
            });
        }

        // ---- Type shape ----

        [Test]
        public void Scheduler_HoldsOnlyFourDependencies()
        {
            Type type = GetSchedulerType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));

            bool hasRegistry = false;
            bool hasQueue = false;
            bool hasLeaseRegistry = false;
            bool hasObserver = false;
            foreach (FieldInfo field in fields)
            {
                hasRegistry |= field.FieldType == GetRegistryType();
                hasQueue |= field.FieldType == typeof(CaptureFrameRequestQueue);
                hasLeaseRegistry |= field.FieldType == typeof(CaptureFrameRenderTargetLeaseRegistry);
                hasObserver |= field.FieldType == typeof(CaptureFrameTraceObserver);

                Assert.That(field.FieldType, Is.Not.EqualTo(GetDraftType()), "Scheduler must not hold a draft.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRenderTargetPool)), "Scheduler must not hold the pool.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)), "Scheduler must not hold a logger.");
            }

            Assert.That(hasRegistry, Is.True);
            Assert.That(hasQueue, Is.True);
            Assert.That(hasLeaseRegistry, Is.True);
            Assert.That(hasObserver, Is.True);
        }

        [Test]
        public void Scheduler_IsInternalSealedNotDisposableMonoBehaviourScriptableObject_NoStaticState()
        {
            Type type = GetSchedulerType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        // ---- Real render texture integration ----

        [Test]
        public void GpuIntegration_RentSchedulePumpCompleteRemoveReturn()
        {
            CaptureFrameProfile frameProfile = MakeFrameProfile();
            CaptureTraceProfile traceProfile = MakeTraceProfile(captureProfileId: 1, maxInFlight: 2, maxDraftPerRun: 10);

            CaptureFrameRequestQueue requestQueue = null;
            CaptureFrameRenderTargetPool pool = null;
            CaptureFrameReadbackBufferPool bufferPool = null;
            UnityRenderTextureReadbackDispatcher dispatcher = null;
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry = null;
            TraceLogger logger = null;

            CaptureFrameRequest request = default;
            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                object run = MakeRun(captureProfileId: 1);
                object registry = CreateRegistry(run, traceProfile);

                requestQueue = new CaptureFrameRequestQueue(1);
                pool = new CaptureFrameRenderTargetPool(1, frameProfile);
                bufferPool = new CaptureFrameReadbackBufferPool(1, 16);
                dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
                leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
                CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

                logger = new TraceLogger(8);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object scheduler = CreateScheduler(registry, requestQueue, leaseRegistry, observer);

                request = MakeRequest(5);
                object draft = MakeDraft(run, request);
                object reservation, rejectKind;
                Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);
                RegistryCommit(registry, reservation, draft);

                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                Assert.That(InvokeTrySchedule(scheduler, draft, lease), Is.True);
                registered = true;
                leaseHeld = false;

                Assert.That(pump.TryStartNext(), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult result), Is.True);
                dispatcher.Release(result);

                Assert.That(leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removed), Is.True);
                registered = false;
                lease = removed;
                leaseHeld = true;

                pool.Return(lease);
                leaseHeld = false;

                Assert.That(requestQueue.Count, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            bool gpuSafe = true;

            // 1. WaitAllRequests and 2. collect/release any delivered result.
            try
            {
                AsyncGPUReadback.WaitAllRequests();
                if (dispatcher != null && dispatcher.IsCreated)
                {
                    while (dispatcher.TryCollect(out CaptureFrameReadbackResult extra))
                    {
                        dispatcher.Release(extra);
                    }
                }
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            // 3. Only once GPU-safe: remove the lease and return it to the pool.
            if (gpuSafe)
            {
                if (registered && leaseRegistry != null)
                {
                    registered = false;
                    try
                    {
                        if (leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
                        {
                            lease = removed;
                            leaseHeld = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                if (leaseHeld && pool != null)
                {
                    leaseHeld = false;
                    try { pool.Return(lease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }
            }

            // 4. Dispatcher, 5. Buffer Pool, 6. RenderTarget Pool, 7. Logger.
            try { if (dispatcher != null && dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool != null && bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (pool != null && pool.IsCreated) { pool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (logger != null && logger.IsCreated) { logger.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
