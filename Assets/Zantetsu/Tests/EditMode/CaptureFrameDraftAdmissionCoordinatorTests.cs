using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftAdmissionCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetCoordinatorType() => GetTypeFromAssembly("CaptureFrameDraftAdmissionCoordinator");

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureFrameDraftFactory");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

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

        private static TraceLogger CreateCaptureLogger(int historyCapacity, long testRunId)
        {
            ConstructorInfo ctor = typeof(TraceLogger).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null, "Capture logger constructor not found.");
            return (TraceLogger)ctor.Invoke(new object[] { historyCapacity, testRunId });
        }

        // ---- Input factories ----

        private static TraceRunContext MakeTraceRunContext(long testRunId = 1)
        {
            return new TraceRunContext(
                testRunId, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", 12345, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));
        }

        private static object MakeRun(long testRunId = 1, long testCaseId = 100, int captureProfileId = 5)
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(TraceRunContext), typeof(long), typeof(int) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");
            return ctor.Invoke(new object[] { MakeTraceRunContext(testRunId: testRunId), testCaseId, captureProfileId });
        }

        private static CaptureTraceProfile MakeProfile(int captureProfileId = 5, int maxInFlight = 2, int maxDraftPerRun = 10)
        {
            return new CaptureTraceProfile(captureProfileId, 4096, maxInFlight, maxDraftPerRun);
        }

        private static CaptureFrameIdSequence MakeSequenceAt(long lastIssued)
        {
            ConstructorInfo ctor = typeof(CaptureFrameIdSequence).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameIdSequence)ctor.Invoke(new object[] { lastIssued });
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
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
            return ctor.Invoke(new object[] { run, request, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), commitPathId });
        }

        private static object CreateFactory(object run, CaptureFrameIdSequence sequence)
        {
            ConstructorInfo ctor = GetFactoryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetRunType(),
                    typeof(CaptureFrameIdSequence),
                    typeof(CaptureSource),
                    typeof(CaptureEye),
                    typeof(CaptureImageRect).MakeByRefType(),
                    typeof(int),
                    typeof(CapturePixelFormat)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftFactory constructor not found.");
            return ctor.Invoke(new object[] { run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32 });
        }

        private static object CreateRegistry(object run, CaptureTraceProfile profile)
        {
            ConstructorInfo ctor = GetRegistryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRunType(), typeof(CaptureTraceProfile) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftRegistry constructor not found.");
            return ctor.Invoke(new object[] { run, profile });
        }

        private static object CreateCoordinator(object factory, object registry, CaptureFrameTraceObserver observer)
        {
            ConstructorInfo ctor = GetCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetFactoryType(), GetRegistryType(), typeof(CaptureFrameTraceObserver) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftAdmissionCoordinator constructor not found.");
            return ctor.Invoke(new object[] { factory, registry, observer });
        }

        // ---- Registry operation helpers (for direct state setup) ----

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

        private static void RegistryCancel(object registry, object reservation)
        {
            MethodInfo method = GetRegistryType().GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { reservation });
        }

        private static int Count(object registry, string name)
        {
            return (int)GetProperty(registry, name);
        }

        // ---- Coordinator invoke helpers ----

        private sealed class AdmitResult
        {
            public bool Ok;
            public object Draft;
            public Exception Exception;
        }

        private static AdmitResult InvokeAdmit(
            object coordinator,
            CaptureFrameTiming timing,
            CapturePoseSample head,
            CapturePoseSample left,
            CapturePoseSample right,
            int commitPathId)
        {
            AdmitResult result = new AdmitResult();
            MethodInfo method = GetCoordinatorType().GetMethod("TryAdmit", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryAdmit method not found.");

            object[] args = new object[]
            {
                1000L, 200L, 300L, 4, 500L,
                600L, 700L, 800L, 9u, 1000L,
                timing, head, left, right, commitPathId,
                null
            };

            try
            {
                result.Ok = (bool)method.Invoke(coordinator, args);
                result.Draft = args[15];
            }
            catch (Exception ex)
            {
                result.Draft = args[15];
                result.Exception = Unwrap(ex);
            }

            return result;
        }

        private static AdmitResult InvokeAdmitSimple(object coordinator, int commitPathId = 1)
        {
            return InvokeAdmit(coordinator, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), commitPathId);
        }

        private static long DraftId(object draft)
        {
            return (long)GetProperty(draft, "CaptureFrameId");
        }

        // ---- Constructor contracts ----

        [Test]
        public void Constructor_ThreeNullDependencies_Rejected()
        {
            object run = MakeRun();
            CaptureTraceProfile profile = MakeProfile(captureProfileId: 5);
            object factory = CreateFactory(run, new CaptureFrameIdSequence());
            object registry = CreateRegistry(run, profile);

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                ConstructorInfo ctor = GetCoordinatorType().GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { GetFactoryType(), GetRegistryType(), typeof(CaptureFrameTraceObserver) },
                    null);

                AssertParamName(ctor, new object[] { null, registry, observer }, typeof(ArgumentNullException), "draftFactory");
                AssertParamName(ctor, new object[] { factory, null, observer }, typeof(ArgumentNullException), "draftRegistry");
                AssertParamName(ctor, new object[] { factory, registry, null }, typeof(ArgumentNullException), "traceObserver");
            }
        }

        private static void AssertParamName(ConstructorInfo ctor, object[] args, Type exceptionType, string paramName)
        {
            try
            {
                ctor.Invoke(args);
                Assert.Fail("Expected " + exceptionType.Name + ".");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException;
                Assert.That(inner, Is.TypeOf(exceptionType));
                if (exceptionType == typeof(ArgumentNullException))
                {
                    Assert.That(((ArgumentNullException)inner).ParamName, Is.EqualTo(paramName));
                }
                else
                {
                    Assert.That(((ArgumentException)inner).ParamName, Is.EqualTo(paramName));
                }
            }
        }

        [Test]
        public void Constructor_RunReferenceMismatch_Rejected()
        {
            object runA = MakeRun(testRunId: 1);
            object runB = MakeRun(testRunId: 1); // equal value, different reference
            CaptureTraceProfile profile = MakeProfile(captureProfileId: 5);

            object factory = CreateFactory(runA, new CaptureFrameIdSequence());
            object registry = CreateRegistry(runB, profile);

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                ConstructorInfo ctor = GetCoordinatorType().GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { GetFactoryType(), GetRegistryType(), typeof(CaptureFrameTraceObserver) },
                    null);

                try
                {
                    ctor.Invoke(new object[] { factory, registry, observer });
                    Assert.Fail("Expected ArgumentException.");
                }
                catch (TargetInvocationException ex)
                {
                    Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                    Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("draftRegistry"));
                }
            }
        }

        [Test]
        public void Factory_HasRunGetter()
        {
            object run = MakeRun();
            object factory = CreateFactory(run, new CaptureFrameIdSequence());

            Assert.That(ReferenceEquals(GetProperty(factory, "Run"), run), Is.True);
        }

        // ---- Success path ----

        [Test]
        public void TryAdmit_Success_TrueSameDraftPending()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                AdmitResult result = InvokeAdmitSimple(coordinator, commitPathId: 42);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Ok, Is.True);
                Assert.That(result.Draft, Is.Not.Null);
                Assert.That(DraftId(result.Draft), Is.EqualTo(1L));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void TryAdmit_SuccessIdsOneThenTwo()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                Assert.That(DraftId(InvokeAdmitSimple(coordinator).Draft), Is.EqualTo(1L));
                Assert.That(DraftId(InvokeAdmitSimple(coordinator).Draft), Is.EqualTo(2L));
            }
        }

        [Test]
        public void TryAdmit_Success_NoAdmissionTrace()
        {
            object run = MakeRun();
            object factory = CreateFactory(run, new CaptureFrameIdSequence());
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                AdmitResult result = InvokeAdmitSimple(coordinator);
                Assert.That(result.Ok, Is.True);

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        // ---- Capacity rejection path ----

        [Test]
        public void TryAdmit_PendingFull_FalseNullIdNotConsumed()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                // Occupy the only pending slot without committing.
                object reservation, rejectKind;
                Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);

                AdmitResult result = InvokeAdmitSimple(coordinator);

                Assert.That(result.Ok, Is.False);
                Assert.That(result.Draft, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(0));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
            }
        }

        [Test]
        public void TryAdmit_EntryFull_FalseNullIdNotConsumed()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 1));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                // Fill the single entry and pending slot via one successful admission.
                Assert.That(InvokeAdmitSimple(coordinator).Ok, Is.True);
                Assert.That(sequence.LastIssued, Is.EqualTo(1));

                AdmitResult result = InvokeAdmitSimple(coordinator);

                Assert.That(result.Ok, Is.False);
                Assert.That(result.Draft, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void TryAdmit_BothFull_RunEntryLimitEvent()
        {
            object run = MakeRun();
            object factory = CreateFactory(run, new CaptureFrameIdSequence());
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 1));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                Assert.That(InvokeAdmitSimple(coordinator).Ok, Is.True);

                AdmitResult result = InvokeAdmitSimple(coordinator);
                Assert.That(result.Ok, Is.False);

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameAdmissionRejected));
                Assert.That(logger.GetHistoryEvent(0).Value0, Is.EqualTo(2.0)); // RunEntryLimit
            }
        }

        [Test]
        public void TryAdmit_AdmissionEvent_CorrelationIdZeroRunTestRunId()
        {
            object run = MakeRun(testRunId: 77);
            object factory = CreateFactory(run, new CaptureFrameIdSequence());
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                object reservation, rejectKind;
                Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);

                AdmitResult result = InvokeAdmitSimple(coordinator);
                Assert.That(result.Ok, Is.False);

                logger.Drain();
                TraceEvent e = logger.GetHistoryEvent(0);

                Assert.That(e.CaptureFrameId, Is.EqualTo(0));
                Assert.That(e.TestRunId, Is.EqualTo(77L));
                Assert.That(e.Timestamp, Is.EqualTo(1000L));
                Assert.That(e.FrameId, Is.EqualTo(200L));
                Assert.That(e.FixedStepId, Is.EqualTo(300L));
                Assert.That(e.ThreadId, Is.EqualTo(4));
                Assert.That(e.OpenXRFrameId, Is.EqualTo(500L));
                Assert.That(e.SlashId, Is.EqualTo(600L));
                Assert.That(e.FrontEdgeId, Is.EqualTo(700L));
                Assert.That(e.ObjectId, Is.EqualTo(800L));
                Assert.That(e.ObjectGeneration, Is.EqualTo(9u));
                Assert.That(e.TaskId, Is.EqualTo(1000L));
            }
        }

        [Test]
        public void TryAdmit_AfterCapacityFreed_SucceedsWithIdOne()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                object reservation, rejectKind;
                Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);
                Assert.That(InvokeAdmitSimple(coordinator).Ok, Is.False);
                Assert.That(sequence.LastIssued, Is.EqualTo(0));

                RegistryCancel(registry, reservation);

                AdmitResult result = InvokeAdmitSimple(coordinator);
                Assert.That(result.Ok, Is.True);
                Assert.That(DraftId(result.Draft), Is.EqualTo(1L));
            }
        }

        // ---- Admission trace failure paths ----

        [Test]
        public void TryAdmit_DisposedLogger_RejectionTraceFails_IdNotConsumedRegistryUnchanged()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));

            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            object coordinator = CreateCoordinator(factory, registry, observer);

            object reservation, rejectKind;
            Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);

            logger.Dispose();

            AdmitResult result = InvokeAdmitSimple(coordinator);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Exception, Is.TypeOf<ObjectDisposedException>());
            Assert.That(result.Draft, Is.Null);
            Assert.That(sequence.LastIssued, Is.EqualTo(0));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
        }

        [Test]
        public void TryAdmit_CaptureRunLoggerMatchingRun_AdmissionTraceSucceeds()
        {
            object run = MakeRun(testRunId: 1);
            object factory = CreateFactory(run, new CaptureFrameIdSequence());
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));

            using (TraceLogger logger = CreateCaptureLogger(8, 1))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                object reservation, rejectKind;
                Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);

                AdmitResult result = InvokeAdmitSimple(coordinator);
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Exception, Is.Null);

                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameAdmissionRejected));
            }
        }

        [Test]
        public void TryAdmit_CaptureRunLoggerMismatch_ExceptionNotConverted()
        {
            object run = MakeRun(testRunId: 1);
            object factory = CreateFactory(run, new CaptureFrameIdSequence());
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));

            using (TraceLogger logger = CreateCaptureLogger(8, 2))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                object reservation, rejectKind;
                Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);

                AdmitResult result = InvokeAdmitSimple(coordinator);
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
            }
        }

        // ---- Reservation cleanup on failure ----

        [Test]
        public void TryAdmit_FactoryInvalidTiming_CancelsReservation_IdConsumed()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                AdmitResult result = InvokeAdmit(coordinator, default(CaptureFrameTiming), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);

                Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void TryAdmit_FactoryIdExhausted_CancelsReservation()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = MakeSequenceAt(long.MaxValue);
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                AdmitResult result = InvokeAdmitSimple(coordinator);

                Assert.That(result.Exception, Is.TypeOf<OverflowException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(long.MaxValue));
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void TryAdmit_CommitFailure_CancelsReservation_EntryPendingUnchanged()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            // Pre-commit a high-ID entry directly so the factory's first ID (1)
            // is out of order and the commit fails.
            object preReservation, preRejectKind;
            Assert.That(RegistryTryReserve(registry, out preReservation, out preRejectKind), Is.True);
            RegistryCommit(registry, preReservation, MakeDraft(run, MakeRequest(5, testRunId: 1)));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                AdmitResult result = InvokeAdmitSimple(coordinator);

                Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(sequence.LastIssued, Is.EqualTo(1));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void TryAdmit_AfterFailure_NextSuccessDoesNotReuseConsumedId()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                // First attempt fails after the factory consumed ID 1.
                AdmitResult failed = InvokeAdmit(coordinator, default(CaptureFrameTiming), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
                Assert.That(failed.Exception, Is.TypeOf<ArgumentException>());

                // The next success must not reuse ID 1.
                AdmitResult success = InvokeAdmitSimple(coordinator);
                Assert.That(success.Ok, Is.True);
                Assert.That(DraftId(success.Draft), Is.EqualTo(2L));
            }
        }

        [Test]
        public void TryAdmit_AfterFailure_SameSlotReusable()
        {
            object run = MakeRun();
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = CreateFactory(run, sequence);
            object registry = CreateRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 10));

            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object coordinator = CreateCoordinator(factory, registry, observer);

                AdmitResult failed = InvokeAdmit(coordinator, default(CaptureFrameTiming), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
                Assert.That(failed.Exception, Is.TypeOf<ArgumentException>());
                Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));

                AdmitResult success = InvokeAdmitSimple(coordinator);
                Assert.That(success.Ok, Is.True);
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
            }
        }

        // ---- Type shape ----

        [Test]
        public void Coordinator_HoldsOnlyThreeDependencies()
        {
            Type type = GetCoordinatorType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));

            bool hasFactory = false;
            bool hasRegistry = false;
            bool hasObserver = false;
            foreach (FieldInfo field in fields)
            {
                hasFactory |= field.FieldType == GetFactoryType();
                hasRegistry |= field.FieldType == GetRegistryType();
                hasObserver |= field.FieldType == typeof(CaptureFrameTraceObserver);

                Assert.That(field.FieldType, Is.Not.EqualTo(GetDraftType()), "Coordinator must not hold a draft.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameIdSequence)), "Coordinator must not hold an ID sequence.");
                Assert.That(field.FieldType, Is.Not.EqualTo(GetRunType()), "Coordinator must not hold the run.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceLogger)), "Coordinator must not hold a logger.");
            }

            Assert.That(hasFactory, Is.True);
            Assert.That(hasRegistry, Is.True);
            Assert.That(hasObserver, Is.True);
        }

        [Test]
        public void Coordinator_IsInternalSealedNotDisposableMonoBehaviourScriptableObject_NoStaticState()
        {
            Type type = GetCoordinatorType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }
    }
}
