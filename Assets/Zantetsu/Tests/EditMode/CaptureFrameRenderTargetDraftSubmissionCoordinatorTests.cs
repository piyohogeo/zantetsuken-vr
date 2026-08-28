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
    public class CaptureFrameRenderTargetDraftSubmissionCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetSubmissionCoordinatorType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftSubmissionCoordinator");

        private static Type GetAdmissionCoordinatorType() => GetTypeFromAssembly("CaptureFrameDraftAdmissionCoordinator");

        private static Type GetSchedulerType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftScheduler");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureFrameDraftFactory");

        private static Type GetStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

        private static Type GetSubmissionStatusType() => GetTypeFromAssembly("CaptureFrameDraftSubmissionStatus");

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

        private static CaptureFrameIdSequence MakeSequence(long? startAt = null)
        {
            if (startAt == null)
            {
                return new CaptureFrameIdSequence();
            }

            ConstructorInfo ctor = typeof(CaptureFrameIdSequence).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameIdSequence)ctor.Invoke(new object[] { startAt.Value });
        }

        private static object CreateFactory(object run, CaptureFrameIdSequence sequence)
        {
            ConstructorInfo ctor = GetFactoryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetRunType(), typeof(CaptureFrameIdSequence), typeof(CaptureSource), typeof(CaptureEye),
                    typeof(CaptureImageRect).MakeByRefType(), typeof(int), typeof(CapturePixelFormat)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftFactory constructor not found.");
            return ctor.Invoke(new object[] { run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32 });
        }

        private static object CreateAdmissionCoordinator(object factory, object registry, CaptureFrameTraceObserver observer)
        {
            ConstructorInfo ctor = GetAdmissionCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetFactoryType(), GetRegistryType(), typeof(CaptureFrameTraceObserver) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftAdmissionCoordinator constructor not found.");
            return ctor.Invoke(new object[] { factory, registry, observer });
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

        private static object CreateSubmissionCoordinator(object admissionCoordinator, object scheduler)
        {
            ConstructorInfo ctor = GetSubmissionCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetAdmissionCoordinatorType(), GetSchedulerType() }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameRenderTargetDraftSubmissionCoordinator constructor not found.");
            return ctor.Invoke(new object[] { admissionCoordinator, scheduler });
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

        // ---- Registry / scheduler operation helpers ----

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

        private static int Count(object registry, string name)
        {
            return (int)GetProperty(registry, name);
        }

        private static void PreCommitDraft(object registry, object run, long captureFrameId)
        {
            object reservation, rejectKind;
            Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);
            RegistryCommit(registry, reservation, MakeDraft(run, MakeRequest(captureFrameId)));
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

        private sealed class SubmissionScope
        {
            public int PoolCapacity;
            public int LeaseCapacity;
            public int QueueCapacity;
            public int MaxInFlight;
            public int MaxDraftPerRun;
            public long? SequenceStart;
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public CaptureFrameRequestQueue RequestQueue;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public CaptureFrameIdSequence Sequence;
            public object Run;
            public object Registry;
            public object Factory;
            public object AdmissionCoordinator;
            public object Scheduler;
            public object SubmissionCoordinator;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<CaptureFrameRequest> Registered = new List<CaptureFrameRequest>();
        }

        private static SubmissionScope NewScope(int poolCapacity, int leaseCapacity, int queueCapacity, int maxInFlight = 2, int maxDraftPerRun = 10, long? sequenceStart = null)
        {
            SubmissionScope scope = new SubmissionScope();
            scope.PoolCapacity = poolCapacity;
            scope.LeaseCapacity = leaseCapacity;
            scope.QueueCapacity = queueCapacity;
            scope.MaxInFlight = maxInFlight;
            scope.MaxDraftPerRun = maxDraftPerRun;
            scope.SequenceStart = sequenceStart;
            return scope;
        }

        private static void BuildScope(SubmissionScope scope)
        {
            scope.Logger = new TraceLogger(8);
            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.RequestQueue = new CaptureFrameRequestQueue(scope.QueueCapacity);
            scope.Pool = new CaptureFrameRenderTargetPool(scope.PoolCapacity, MakeFrameProfile());
            scope.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(scope.LeaseCapacity, scope.Pool);
            scope.Sequence = MakeSequence(scope.SequenceStart);
            scope.Run = MakeRun(captureProfileId: 1);
            scope.Registry = CreateRegistry(scope.Run, MakeTraceProfile(captureProfileId: 1, maxInFlight: scope.MaxInFlight, maxDraftPerRun: scope.MaxDraftPerRun));
            scope.Factory = CreateFactory(scope.Run, scope.Sequence);
            scope.AdmissionCoordinator = CreateAdmissionCoordinator(scope.Factory, scope.Registry, scope.Observer);
            scope.Scheduler = CreateScheduler(scope.Registry, scope.RequestQueue, scope.LeaseRegistry, scope.Observer);
            scope.SubmissionCoordinator = CreateSubmissionCoordinator(scope.AdmissionCoordinator, scope.Scheduler);
        }

        private static CaptureFrameRenderTargetLease Rent(SubmissionScope scope)
        {
            bool rented = scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease);
            if (rented)
            {
                scope.Held.Add(lease);
            }

            Assert.That(rented, Is.True);
            return lease;
        }

        private static void TrackScheduled(SubmissionScope scope, CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
        {
            scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex);
            scope.Registered.Add(request);
        }

        private static CaptureFrameRequest TrackSubmissionOwnership(SubmissionScope scope, SubmitResult result, CaptureFrameRenderTargetLease lease)
        {
            if (result.Draft == null)
            {
                return default;
            }

            CaptureFrameRequest request = (CaptureFrameRequest)GetProperty(result.Draft, "Request");

            // Ownership truth comes from the lease registry, not the status.
            if (scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease))
            {
                // Track the request so cleanup recovers the actual registry lease.
                scope.Registered.Add(request);

                // Release the caller's input lease only when it is the exact
                // lease the registry holds; otherwise keep it in Held so it is
                // returned separately and the mismatch is reported below.
                if (LeasesIdentical(registeredLease, lease))
                {
                    scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex);
                }
            }

            return request;
        }

        private static bool LeasesIdentical(CaptureFrameRenderTargetLease a, CaptureFrameRenderTargetLease b)
        {
            if (a.SlotIndex != b.SlotIndex || a.IsValid != b.IsValid)
            {
                return false;
            }

            FieldInfo ownerTokenField = typeof(CaptureFrameRenderTargetLease).GetField("_ownerToken", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo generationField = typeof(CaptureFrameRenderTargetLease).GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance);
            return (Guid)ownerTokenField.GetValue(a) == (Guid)ownerTokenField.GetValue(b)
                && (long)generationField.GetValue(a) == (long)generationField.GetValue(b);
        }

        private static void AssertScheduledLeaseIdentical(SubmissionScope scope, CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
        {
            Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True, "Scheduled draft must have a registered lease.");
            Assert.That(LeasesIdentical(registeredLease, lease), Is.True, "Registered lease must match the input lease.");
        }

        private static void AssertNoLeaseRegistered(SubmissionScope scope, CaptureFrameRequest request)
        {
            Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.False, "Backpressured or rolled-back draft must not have a registered lease.");
        }

        private static Exception[] CleanupSubmissionScope(SubmissionScope scope)
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

        private static void RunSubmissionBody(SubmissionScope scope, Action body)
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

            Exception[] errors = CleanupSubmissionScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        // ---- Submission invoke helpers ----

        private sealed class SubmitResult
        {
            public int Status = -1;
            public object Draft;
            public Exception Exception;
        }

        private static SubmitResult InvokeSubmit(object coordinator, CaptureFrameTiming timing, CaptureFrameRenderTargetLease lease, int commitPathId = 1)
        {
            SubmitResult result = new SubmitResult();
            MethodInfo method = GetSubmissionCoordinatorType().GetMethod("TrySubmit", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TrySubmit method not found.");

            object[] args = new object[]
            {
                1000L, 200L, 300L, 4, 500L,
                600L, 700L, 800L, 9u, 1000L,
                timing, MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f),
                commitPathId, lease, null
            };

            try
            {
                object status = method.Invoke(coordinator, args);
                result.Status = (int)status;
                result.Draft = args[16];
            }
            catch (Exception ex)
            {
                result.Status = -1;
                result.Draft = args[16];
                result.Exception = Unwrap(ex);
            }

            return result;
        }

        private static SubmitResult InvokeSubmitSimple(object coordinator, CaptureFrameRenderTargetLease lease)
        {
            return InvokeSubmit(coordinator, MakeTiming(), lease);
        }

        private static bool InvokeSchedulerTrySchedule(object scheduler, object draft, CaptureFrameRenderTargetLease lease)
        {
            MethodInfo method = GetSchedulerType().GetMethod("TrySchedule", BindingFlags.NonPublic | BindingFlags.Instance);
            return (bool)method.Invoke(scheduler, new object[] { draft, lease });
        }

        // ---- Status enum contracts ----

        [Test]
        public void StatusEnum_UnderlyingTypeIsInt()
        {
            Assert.That(Enum.GetUnderlyingType(GetSubmissionStatusType()), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void StatusEnum_NamesAndValues_MatchExactly()
        {
            Type type = GetSubmissionStatusType();

            Assert.That(Enum.GetName(type, 0), Is.EqualTo("None"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("AdmissionRejected"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("Scheduled"));
            Assert.That(Enum.GetName(type, 3), Is.EqualTo("SchedulingBackpressured"));
        }

        [Test]
        public void StatusEnum_HasNoAliasesOrGaps()
        {
            Type type = GetSubmissionStatusType();

            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(4));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(4));

            for (int i = 0; i <= 3; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 4), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Constructor contracts ----

        [Test]
        public void Constructor_TwoNullDependencies_Rejected()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                ConstructorInfo ctor = GetSubmissionCoordinatorType().GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetAdmissionCoordinatorType(), GetSchedulerType() }, null);

                AssertNullParam(ctor, new object[] { null, scope.Scheduler }, "admissionCoordinator");
                AssertNullParam(ctor, new object[] { scope.AdmissionCoordinator, null }, "draftScheduler");
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

        [Test]
        public void Constructor_DifferentRegistry_Rejected()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                object otherRun = MakeRun(captureProfileId: 1);
                object otherRegistry = CreateRegistry(otherRun, MakeTraceProfile(captureProfileId: 1));
                object otherScheduler = CreateScheduler(otherRegistry, scope.RequestQueue, scope.LeaseRegistry, scope.Observer);

                ConstructorInfo ctor = GetSubmissionCoordinatorType().GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetAdmissionCoordinatorType(), GetSchedulerType() }, null);

                try
                {
                    ctor.Invoke(new object[] { scope.AdmissionCoordinator, otherScheduler });
                    Assert.Fail("Expected ArgumentException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("draftScheduler"));
                }
            });
        }

        // ---- Admission rejected ----

        [Test]
        public void AdmissionRejected_OutNull_IdNotConsumed_SchedulerUntouched()
        {
            SubmissionScope scope = NewScope(2, 2, 2, maxInFlight: 1, maxDraftPerRun: 1);
            RunSubmissionBody(scope, () =>
            {
                PreCommitDraft(scope.Registry, scope.Run, 5);
                CaptureFrameRenderTargetLease lease = Rent(scope);

                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(1)); // AdmissionRejected
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
            });
        }

        [Test]
        public void AdmissionRejected_DoesNotValidateLease()
        {
            SubmissionScope scope = NewScope(2, 2, 2, maxInFlight: 1, maxDraftPerRun: 1);
            RunSubmissionBody(scope, () =>
            {
                PreCommitDraft(scope.Registry, scope.Run, 5);

                // A default (invalid) lease must not be validated on admission rejection.
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, default(CaptureFrameRenderTargetLease));

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(1)); // AdmissionRejected
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            });
        }

        // ---- Scheduled ----

        [Test]
        public void Scheduled_OutDraftSameAsRegistryDraft()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                // Track lease ownership immediately, before any assertion.
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(2)); // Scheduled
                Assert.That(result.Draft, Is.Not.Null);

                object registered;
                object status;
                Assert.That(RegistryTryGet(scope.Registry, request, out registered, out status), Is.True);
                Assert.That(ReferenceEquals(registered, result.Draft), Is.True);
                Assert.That((int)status, Is.EqualTo(0)); // Pending

                AssertScheduledLeaseIdentical(scope, request, lease);
            });
        }

        [Test]
        public void Scheduled_QueueRequestAndLeaseIdentical()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Status, Is.EqualTo(2)); // Scheduled
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.TryPeek(out CaptureFrameRequest head), Is.True);
                Assert.That(head.TraceContext.CaptureFrameId, Is.EqualTo(request.TraceContext.CaptureFrameId));
                Assert.That(head.TraceContext.TestRunId, Is.EqualTo(request.TraceContext.TestRunId));

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                Assert.That(LeasesIdentical(registeredLease, lease), Is.True);
            });
        }

        // ---- Scheduling backpressure ----

        [Test]
        public void LeaseRegistryFull_SchedulingBackpressured()
        {
            SubmissionScope scope = NewScope(2, 1, 2);
            RunSubmissionBody(scope, () =>
            {
                // Occupy the single lease registry slot with a different frame.
                CaptureFrameRenderTargetLease otherLease = Rent(scope);
                Assert.That(scope.LeaseRegistry.TryRegister(MakeRequest(99), otherLease), Is.True);
                TrackScheduled(scope, MakeRequest(99), otherLease);

                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(3)); // SchedulingBackpressured
                Assert.That(result.Draft, Is.Not.Null);
                AssertNoLeaseRegistered(scope, request);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void RequestQueueFull_SchedulingBackpressured()
        {
            SubmissionScope scope = NewScope(2, 2, 1);
            RunSubmissionBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(99)), Is.True);

                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(3)); // SchedulingBackpressured
                Assert.That(result.Draft, Is.Not.Null);
                AssertNoLeaseRegistered(scope, request);
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
                Assert.That(scope.RequestQueue.TotalRejected, Is.EqualTo(1));
            });
        }

        [Test]
        public void Backpressure_LeaseReturnableToPool()
        {
            SubmissionScope scope = NewScope(2, 2, 1);
            RunSubmissionBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(99)), Is.True);

                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Status, Is.EqualTo(3)); // SchedulingBackpressured
                AssertNoLeaseRegistered(scope, request);

                // The caller still owns the lease and can return it to the pool.
                bool stillHeld = scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex) > 0;
                if (stillHeld)
                {
                    scope.Pool.Return(lease);
                }

                Assert.That(stillHeld, Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void Backpressure_RetrySameDraftAndLeaseViaScheduler_Succeeds()
        {
            SubmissionScope scope = NewScope(2, 2, 1);
            RunSubmissionBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(99)), Is.True);

                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                // Track ownership immediately: backpressure keeps the lease caller-owned.
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Status, Is.EqualTo(3)); // SchedulingBackpressured
                Assert.That(result.Draft, Is.Not.Null);
                AssertNoLeaseRegistered(scope, request);

                // Free the queue and retry the SAME draft and lease directly via
                // the scheduler, never via a new admission.
                Assert.That(scope.RequestQueue.TryDequeue(out CaptureFrameRequest dequeued), Is.True);

                bool retryScheduled = InvokeSchedulerTrySchedule(scope.Scheduler, result.Draft, lease);
                if (retryScheduled)
                {
                    TrackScheduled(scope, request, lease);
                }

                Assert.That(retryScheduled, Is.True);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
            });
        }

        // ---- Admission exception paths ----

        [Test]
        public void AdmissionInvalidTiming_OutNull_SchedulerUntouched()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmit(scope.SubmissionCoordinator, default(CaptureFrameTiming), lease);

                Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "ReservationCount"), Is.EqualTo(0));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1)); // ID consumed by failed admission
            });
        }

        [Test]
        public void AdmissionIdExhausted_OutNull_ReservationReleased()
        {
            SubmissionScope scope = NewScope(2, 2, 2, sequenceStart: long.MaxValue);
            RunSubmissionBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                Assert.That(result.Exception, Is.TypeOf<OverflowException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "ReservationCount"), Is.EqualTo(0));
            });
        }

        [Test]
        public void AdmissionRejectedTraceFails_OutNull_SchedulerUntouched()
        {
            SubmissionScope scope = NewScope(2, 2, 2, maxInFlight: 1, maxDraftPerRun: 1);
            RunSubmissionBody(scope, () =>
            {
                PreCommitDraft(scope.Registry, scope.Run, 5);
                CaptureFrameRenderTargetLease lease = Rent(scope);

                scope.Logger.Dispose();

                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                Assert.That(result.Exception, Is.TypeOf<ObjectDisposedException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
            });
        }

        // ---- Scheduler exception paths ----

        [Test]
        public void StaleLease_SchedulerException_OutDraftNonNull()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                CaptureFrameRenderTargetLease stale = default;
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, stale);

                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, stale);

                Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(result.Draft, Is.Not.Null);
                AssertNoLeaseRegistered(scope, request);
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        [Test]
        public void DisposedLogger_QueuedTraceFails_OutDraftNonNull_PendingAndLeaseReturnable()
        {
            SubmissionScope scope = NewScope(2, 2, 2);
            RunSubmissionBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                scope.Logger.Dispose();

                // Admission succeeds without touching the logger, then the
                // scheduler's queued trace fails on the disposed logger.
                SubmitResult result = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                // Track ownership immediately (rollback success → caller-owned).
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result, lease);

                Assert.That(result.Exception, Is.TypeOf<ObjectDisposedException>());
                Assert.That(result.Draft, Is.Not.Null);

                // Draft remains Pending in the registry.
                object registered;
                object status;
                Assert.That(RegistryTryGet(scope.Registry, request, out registered, out status), Is.True);
                Assert.That(ReferenceEquals(registered, result.Draft), Is.True);
                Assert.That((int)status, Is.EqualTo(0));
                AssertNoLeaseRegistered(scope, request);

                // The scheduler rolled back the lease; the caller can return it.
                bool stillHeld = scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex) > 0;
                if (stillHeld)
                {
                    scope.Pool.Return(lease);
                }

                Assert.That(stillHeld, Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        // ---- Ownership / type shape ----

        [Test]
        public void NoIdReuse_AcrossPaths()
        {
            SubmissionScope scope = NewScope(2, 2, 1);
            RunSubmissionBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(99)), Is.True);

                CaptureFrameRenderTargetLease lease = Rent(scope);
                SubmitResult backpressured = InvokeSubmitSimple(scope.SubmissionCoordinator, lease);

                // Track ownership immediately (backpressure keeps the lease caller-owned).
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, backpressured, lease);

                Assert.That(backpressured.Status, Is.EqualTo(3));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));

                // Direct retry reuses the same ID.
                Assert.That(scope.RequestQueue.TryDequeue(out CaptureFrameRequest ignored), Is.True);
                bool retryScheduled = InvokeSchedulerTrySchedule(scope.Scheduler, backpressured.Draft, lease);
                if (retryScheduled)
                {
                    TrackScheduled(scope, request, lease);
                }

                Assert.That(retryScheduled, Is.True);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));

                // Free the queue again for a fresh admission.
                Assert.That(scope.RequestQueue.TryDequeue(out CaptureFrameRequest ignored2), Is.True);

                // A fresh admission advances to the next ID.
                CaptureFrameRenderTargetLease secondLease = Rent(scope);
                SubmitResult second = InvokeSubmitSimple(scope.SubmissionCoordinator, secondLease);
                TrackSubmissionOwnership(scope, second, secondLease);
                Assert.That(second.Status, Is.EqualTo(2));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(2));
            });
        }

        [Test]
        public void Coordinator_HoldsOnlyTwoDependencies()
        {
            Type type = GetSubmissionCoordinatorType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));

            bool hasAdmission = false;
            bool hasScheduler = false;
            foreach (FieldInfo field in fields)
            {
                hasAdmission |= field.FieldType == GetAdmissionCoordinatorType();
                hasScheduler |= field.FieldType == GetSchedulerType();

                Assert.That(field.FieldType, Is.Not.EqualTo(GetDraftType()), "Coordinator must not hold a draft.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRenderTargetLease)), "Coordinator must not hold a lease.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRenderTargetPool)), "Coordinator must not hold the pool.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRequestQueue)), "Coordinator must not hold the queue.");
                Assert.That(field.FieldType, Is.Not.EqualTo(GetRegistryType()), "Coordinator must not hold the registry.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)), "Coordinator must not hold a logger.");
            }

            Assert.That(hasAdmission, Is.True);
            Assert.That(hasScheduler, Is.True);
        }

        [Test]
        public void Coordinator_IsInternalSealedNotDisposableMonoBehaviourScriptableObject_NoStaticState()
        {
            Type type = GetSubmissionCoordinatorType();

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
        public void GpuIntegration_SubmitSchedulePumpCompleteRemoveReturn_DraftPending()
        {
            CaptureFrameProfile frameProfile = MakeFrameProfile();

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
                object registry = CreateRegistry(run, MakeTraceProfile(captureProfileId: 1));
                CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
                object factory = CreateFactory(run, sequence);

                requestQueue = new CaptureFrameRequestQueue(1);
                pool = new CaptureFrameRenderTargetPool(1, frameProfile);
                bufferPool = new CaptureFrameReadbackBufferPool(1, 16);
                dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
                leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
                CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

                logger = new TraceLogger(8);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object admissionCoordinator = CreateAdmissionCoordinator(factory, registry, observer);
                object scheduler = CreateScheduler(registry, requestQueue, leaseRegistry, observer);
                object coordinator = CreateSubmissionCoordinator(admissionCoordinator, scheduler);

                bool rented = pool.TryRent(out lease);
                if (rented)
                {
                    leaseHeld = true;
                }

                Assert.That(rented, Is.True);

                SubmitResult result = InvokeSubmitSimple(coordinator, lease);

                // Track ownership immediately, before any assertion, using the
                // lease registry as the source of truth.
                request = result.Draft != null ? (CaptureFrameRequest)GetProperty(result.Draft, "Request") : default;
                if (result.Draft != null && leaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease))
                {
                    registered = true;
                    if (LeasesIdentical(registeredLease, lease))
                    {
                        leaseHeld = false;
                    }
                }

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(2)); // Scheduled
                Assert.That(result.Draft, Is.Not.Null);

                Assert.That(leaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease scheduledLease), Is.True);
                Assert.That(LeasesIdentical(scheduledLease, lease), Is.True);

                Assert.That(pump.TryStartNext(), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult readbackResult), Is.True);
                dispatcher.Release(readbackResult);

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

                object registeredDraft;
                object status;
                Assert.That(RegistryTryGet(registry, request, out registeredDraft, out status), Is.True);
                Assert.That(ReferenceEquals(registeredDraft, result.Draft), Is.True);
                Assert.That((int)status, Is.EqualTo(0)); // still Pending
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            bool gpuSafe = true;

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

            try { if (dispatcher != null && dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool != null && bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (pool != null && pool.IsCreated) { pool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (logger != null && logger.IsCreated) { logger.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
