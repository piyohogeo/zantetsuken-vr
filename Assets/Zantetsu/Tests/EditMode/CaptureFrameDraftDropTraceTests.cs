using System;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftDropTraceTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers for internal types ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetPayloadType() => GetTypeFromAssembly("CaptureFrameDraftDropTracePayload");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetReservationType() => GetTypeFromAssembly("CaptureFrameDraftReservation");

        private static Type GetRejectKindType() => GetTypeFromAssembly("CaptureFrameAdmissionRejectKind");

        private static Type GetStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

        private static Type GetEmissionStateType() => GetTypeFromAssembly("DraftDropTraceEmissionState");

        private static Type GetDraftTraceContextType() => GetTypeFromAssembly("CaptureFrameDraftTraceContext");

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

        private static TraceRunContext MakeTraceRunContext(long testRunId = 1)
        {
            return new TraceRunContext(
                testRunId,
                1000,
                "build-1",
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));
        }

        private static object MakeRun(long testRunId = 1, long testCaseId = 100, int captureProfileId = 5)
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceRunContext), typeof(long), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");
            return ctor.Invoke(new object[] { MakeTraceRunContext(testRunId: testRunId), testCaseId, captureProfileId });
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
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftRegistry constructor not found.");
            return ctor.Invoke(new object[] { run, profile });
        }

        private static object MakeRegistry(object run = null, CaptureTraceProfile profile = null)
        {
            return CreateRegistry(run ?? MakeRun(), profile ?? MakeProfile());
        }

        private static CaptureFrameRequest MakeRequest(
            long captureFrameId,
            long testRunId = 1,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left,
            CaptureImageRect? imageRect = null,
            int arrayIndex = 0)
        {
            CaptureImageRect rect = imageRect ?? new CaptureImageRect(0, 0, 2, 2);

            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1,
                20,
                3,
                4,
                captureFrameId,
                30,
                testRunId,
                5,
                6,
                7,
                8u,
                9);

            return new CaptureFrameRequest(context, source, eye, rect, arrayIndex, CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRequest MakeRequestDifferent(long captureFrameId)
        {
            return MakeRequest(captureFrameId, eye: CaptureEye.Right);
        }

        private static CaptureFrameRequest MakeRequestWithContext(CaptureFrameTraceContext context)
        {
            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
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

        private static object MakeDraftTraceContext(CaptureFrameTraceContext context)
        {
            ConstructorInfo ctor = GetDraftTraceContextType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(CaptureFrameTraceContext).MakeByRefType() },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftTraceContext constructor not found.");
            return ctor.Invoke(new object[] { context });
        }

        // ---- Registry operation helpers ----

        private static bool TryReserve(object registry, out object reservation, out object rejectKind)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryReserve", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryReserve method not found.");
            object[] args = new object[] { null, null };
            bool ok = (bool)method.Invoke(registry, args);
            reservation = args[0];
            rejectKind = args[1];
            return ok;
        }

        private static void Commit(object registry, object reservation, object draft)
        {
            MethodInfo method = GetRegistryType().GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "Commit method not found.");
            method.Invoke(registry, new object[] { reservation, draft });
        }

        private static object CommitDraft(object registry, object run, CaptureFrameRequest request)
        {
            object reservation;
            object rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            object draft = MakeDraft(run, request);
            Commit(registry, reservation, draft);
            return draft;
        }

        private static bool TryGet(object registry, CaptureFrameRequest request, out object draft, out object status)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryGet method not found.");
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

        private static int GetSlotState(object registry, int slotIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotState", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_slotState field not found.");
            Array states = (Array)field.GetValue(registry);
            return (int)states.GetValue(slotIndex);
        }

        private static int GetSlotEntryIndex(object registry, int slotIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotEntryIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_slotEntryIndex field not found.");
            int[] indices = (int[])field.GetValue(registry);
            return indices[slotIndex];
        }

        private static object GetEntryField(object registry, int entryIndex, string fieldName)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(entriesField, Is.Not.Null, "_entries field not found.");
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(entryIndex);
            FieldInfo field = entry.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Entry." + fieldName + " field not found.");
            return field.GetValue(entry);
        }

        private static void SetEntryField(object registry, int entryIndex, string fieldName, object value)
        {
            FieldInfo entriesField = GetRegistryType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(entriesField, Is.Not.Null, "_entries field not found.");
            Array entries = (Array)entriesField.GetValue(registry);
            object entry = entries.GetValue(entryIndex);
            FieldInfo field = entry.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "Entry." + fieldName + " field not found.");
            field.SetValue(entry, value);
            entries.SetValue(entry, entryIndex);
        }

        // ---- MarkDropped helpers ----

        private static void MarkDropped(object registry, CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetRegistryType().GetMethod("MarkDropped", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "MarkDropped method not found.");
            method.Invoke(registry, new object[] { request, reason });
        }

        private static Exception MarkDroppedException(object registry, CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            try
            {
                MarkDropped(registry, request, reason);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- TryConsumeDropTrace helpers ----

        private static bool TryConsumeDropTrace(object registry, long captureFrameId, out object payload)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryConsumeDropTrace", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TryConsumeDropTrace method not found.");
            object[] args = new object[] { captureFrameId, null };
            bool ok = (bool)method.Invoke(registry, args);
            payload = args[1];
            return ok;
        }

        private static Exception TryConsumeDropTraceException(object registry, long captureFrameId)
        {
            try
            {
                object payload;
                TryConsumeDropTrace(registry, captureFrameId, out payload);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Payload helpers ----

        private static object CreatePayload(object traceContext, CaptureFrameDropReason reason)
        {
            ConstructorInfo ctor = GetPayloadType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetDraftTraceContextType().MakeByRefType(), typeof(CaptureFrameDropReason) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftDropTracePayload constructor not found.");
            return ctor.Invoke(new object[] { traceContext, reason });
        }

        private static Exception CreatePayloadException(object traceContext, CaptureFrameDropReason reason)
        {
            try
            {
                CreatePayload(traceContext, reason);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Observer helpers ----

        private static TraceLogger CreateCaptureLogger(int historyCapacity, long testRunId)
        {
            ConstructorInfo ctor = typeof(TraceLogger).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null, "Capture logger constructor not found.");
            return (TraceLogger)ctor.Invoke(new object[] { historyCapacity, testRunId });
        }

        private static TraceFlightRecorder CreateRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            ConstructorInfo ctor = typeof(TraceFlightRecorder).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceLogger), typeof(int), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "Internal recorder constructor not found.");
            return (TraceFlightRecorder)ctor.Invoke(new object[] { logger, postRollCapacity, freezeTerminalTraceReserve });
        }

        private static void Seal(TraceLogger logger, long testRunId, TraceFlightRecorder recorder)
        {
            MethodInfo method = typeof(TraceLogger).GetMethod("SealAndDrainRunForFreeze", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "SealAndDrainRunForFreeze method not found.");
            object receipt = method.Invoke(logger, new object[] { testRunId, recorder });
            Assert.That(receipt, Is.Not.Null, "Seal returned no receipt.");
        }

        private static int GetCount(TraceLogger logger, string name)
        {
            PropertyInfo prop = typeof(TraceLogger).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, name + " property not found.");
            return (int)prop.GetValue(logger);
        }

        private static bool RecordDraftDropped(CaptureFrameTraceObserver observer, object registry, long captureFrameId)
        {
            MethodInfo method = typeof(CaptureFrameTraceObserver).GetMethod(
                "RecordDraftDropped", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "RecordDraftDropped method not found.");
            return (bool)method.Invoke(observer, new object[] { registry, captureFrameId });
        }

        private static Exception RecordDraftDroppedException(CaptureFrameTraceObserver observer, object registry, long captureFrameId)
        {
            try
            {
                RecordDraftDropped(observer, registry, captureFrameId);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static void AssertDraftDropEvent(TraceEvent e, CaptureFrameTraceContext c, int expectedReason)
        {
            Assert.That(e.EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
            Assert.That(e.TaskType, Is.EqualTo(TraceTaskType.None));
            Assert.That(e.FromState, Is.EqualTo(0)); // Pending
            Assert.That(e.ToState, Is.EqualTo(2)); // Dropped
            Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
            Assert.That(e.Value0, Is.EqualTo(0.0));
            Assert.That(e.Value1, Is.EqualTo((double)expectedReason));

            // 12 correlation fields transcribed exactly.
            Assert.That(e.Timestamp, Is.EqualTo(c.Timestamp));
            Assert.That(e.FrameId, Is.EqualTo(c.UnityFrameId));
            Assert.That(e.FixedStepId, Is.EqualTo(c.FixedStepId));
            Assert.That(e.ThreadId, Is.EqualTo(c.ThreadId));
            Assert.That(e.CaptureFrameId, Is.EqualTo(c.CaptureFrameId));
            Assert.That(e.OpenXRFrameId, Is.EqualTo(c.OpenXRFrameId));
            Assert.That(e.TestRunId, Is.EqualTo(c.TestRunId));
            Assert.That(e.SlashId, Is.EqualTo(c.SlashId));
            Assert.That(e.FrontEdgeId, Is.EqualTo(c.FrontEdgeId));
            Assert.That(e.ObjectId, Is.EqualTo(c.ObjectId));
            Assert.That(e.ObjectGeneration, Is.EqualTo(c.ObjectGeneration));
            Assert.That(e.TaskId, Is.EqualTo(c.TaskId));

            // Not present in the draft context: stay zero.
            Assert.That(e.SlashGeneration, Is.EqualTo(0u));
            Assert.That(e.MobId, Is.EqualTo(0L));
            Assert.That(e.PlanGeneration, Is.EqualTo(0u));
        }

        // ---- Payload shape contracts ----

        [Test]
        public void Payload_InternalReadonlyStruct_NoPublicCtor_NotDisposable_NoStaticState()
        {
            Type type = GetPayloadType();

            Assert.That(type.IsValueType, Is.True);
            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Payload_Default_Invalid()
        {
            object def = Activator.CreateInstance(GetPayloadType());
            Assert.That((bool)GetProperty(def, "IsValid"), Is.False);
        }

        [Test]
        public void Payload_Reasons6To8_Valid()
        {
            object traceContext = MakeDraftTraceContext(MakeRequest(7).TraceContext);

            foreach (int reason in new[] { 6, 7, 8 })
            {
                object payload = CreatePayload(traceContext, (CaptureFrameDropReason)reason);
                Assert.That((bool)GetProperty(payload, "IsValid"), Is.True, "Reason " + reason + " should be valid.");
                Assert.That((int)GetProperty(payload, "Reason"), Is.EqualTo(reason));
            }
        }

        [Test]
        public void Payload_InvalidReason_Rejected()
        {
            object traceContext = MakeDraftTraceContext(MakeRequest(7).TraceContext);

            foreach (int reason in new[] { 0, 1, 2, 3, 4, 5, 9, -1, 10, int.MaxValue })
            {
                Exception ex = CreatePayloadException(traceContext, (CaptureFrameDropReason)reason);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "Reason " + reason + " must be rejected.");
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("reason"));
            }
        }

        [Test]
        public void Payload_NonPositiveIds_Rejected()
        {
            CaptureFrameTraceContext zeroId = new CaptureFrameTraceContext(1, 20, 3, 4, 0, 30, 1, 5, 6, 7, 8u, 9);
            object tc0 = MakeDraftTraceContext(zeroId);
            Exception ex0 = CreatePayloadException(tc0, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex0, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex0).ParamName, Is.EqualTo("traceContext"));

            CaptureFrameTraceContext zeroRun = new CaptureFrameTraceContext(1, 20, 3, 4, 7, 30, 0, 5, 6, 7, 8u, 9);
            object tc1 = MakeDraftTraceContext(zeroRun);
            Exception ex1 = CreatePayloadException(tc1, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex1, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex1).ParamName, Is.EqualTo("traceContext"));
        }

        // ---- MarkDropped contracts ----

        [Test]
        public void MarkDropped_PngEncodeFailed_TransitionsAndFreesSlot()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 4));

            CaptureFrameRequest request = MakeRequest(7);
            object draft = CommitDraft(registry, run, request);

            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
            Assert.That(GetSlotState(registry, 0), Is.EqualTo(2)); // Occupied
            Assert.That(GetSlotEntryIndex(registry, 0), Is.EqualTo(0));

            MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

            Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(2)); // Dropped
            Assert.That((int)GetEntryField(registry, 0, "DropReason"), Is.EqualTo(6));
            Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(1)); // Pending

            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
            Assert.That(GetSlotState(registry, 0), Is.EqualTo(0)); // Free
            Assert.That(GetSlotEntryIndex(registry, 0), Is.EqualTo(-1));

            // The entry and its draft reference remain.
            object outDraft;
            object status;
            Assert.That(TryGet(registry, request, out outDraft, out status), Is.True);
            Assert.That(ReferenceEquals(outDraft, draft), Is.True);
            Assert.That((int)status, Is.EqualTo(2));
        }

        [Test]
        public void MarkDropped_Reasons6To8_EachTransitions()
        {
            foreach (int reason in new[] { 6, 7, 8 })
            {
                object run = MakeRun(captureProfileId: 5);
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 2));
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(registry, run, request);

                MarkDropped(registry, request, (CaptureFrameDropReason)reason);

                Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(2));
                Assert.That((int)GetEntryField(registry, 0, "DropReason"), Is.EqualTo(reason));
                Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(1));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            }
        }

        [Test]
        public void MarkDropped_FreesSlotForNextAdmission()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 4));

            CaptureFrameRequest first = MakeRequest(1);
            CommitDraft(registry, run, first);
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));

            MarkDropped(registry, first, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));

            CaptureFrameRequest second = MakeRequest(2);
            CommitDraft(registry, run, second);
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(2));
            Assert.That(GetSlotState(registry, 0), Is.EqualTo(2)); // Occupied again
            Assert.That(GetSlotEntryIndex(registry, 0), Is.EqualTo(1)); // points at entry 1
        }

        [Test]
        public void MarkDropped_InvalidRequest_Rejected()
        {
            object registry = MakeRegistry();

            Exception ex = MarkDroppedException(registry, default, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
        }

        [Test]
        public void MarkDropped_Unregistered_Rejected_NoSideEffects()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));

            Exception ex = MarkDroppedException(registry, MakeRequest(7), CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
        }

        [Test]
        public void MarkDropped_RequestMismatch_Rejected_NoSideEffects()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CommitDraft(registry, run, MakeRequest(7));

            Exception ex = MarkDroppedException(registry, MakeRequestDifferent(7), CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(0)); // still Pending
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
        }

        [Test]
        public void MarkDropped_DoubleDrop_Rejected()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);

            MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

            Exception ex = MarkDroppedException(registry, request, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(2));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
        }

        [Test]
        public void MarkDropped_StagedEntry_Rejected()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);

            SetEntryField(registry, 0, "Status", Enum.ToObject(GetStatusType(), 1)); // Staged

            Exception ex = MarkDroppedException(registry, request, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void MarkDropped_InvalidReasons_Rejected_WithParamName()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);

            foreach (int reason in new[] { 0, 1, 2, 3, 4, 5, 9, -1, 10, int.MaxValue })
            {
                Exception ex = MarkDroppedException(registry, request, (CaptureFrameDropReason)reason);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "Reason " + reason + " must be rejected.");
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("reason"));
            }

            Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(0));
            Assert.That((int)GetEntryField(registry, 0, "DropReason"), Is.EqualTo(0));
            Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
        }

        // ---- TryConsumeDropTrace contracts ----

        [Test]
        public void TryConsumeDropTrace_NonPositiveId_Rejected()
        {
            object registry = MakeRegistry();

            Exception ex = TryConsumeDropTraceException(registry, 0);
            Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("captureFrameId"));
        }

        [Test]
        public void TryConsumeDropTrace_Success_PendingToAttempted()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);
            MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

            Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(1)); // Pending

            object payload;
            bool ok = TryConsumeDropTrace(registry, 7, out payload);

            Assert.That(ok, Is.True);
            Assert.That(payload, Is.Not.Null);
            Assert.That((bool)GetProperty(payload, "IsValid"), Is.True);
            Assert.That((int)GetProperty(payload, "Reason"), Is.EqualTo(6));
            Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(2)); // Attempted
        }

        [Test]
        public void TryConsumeDropTrace_SecondCall_False_DefaultPayload_StateUnchanged()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);
            MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

            object first;
            Assert.That(TryConsumeDropTrace(registry, 7, out first), Is.True);

            object second;
            bool ok2 = TryConsumeDropTrace(registry, 7, out second);

            Assert.That(ok2, Is.False);
            Assert.That((bool)GetProperty(second, "IsValid"), Is.False);
            Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(2)); // unchanged
        }

        [Test]
        public void TryConsumeDropTrace_NonDroppedOrMissing_ReturnsFalse()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 2, maxDraftPerRun: 4));

            // Missing ID.
            object p0;
            Assert.That(TryConsumeDropTrace(registry, 999, out p0), Is.False);
            Assert.That((bool)GetProperty(p0, "IsValid"), Is.False);

            // Pending entry (not dropped).
            CaptureFrameRequest pendingRequest = MakeRequest(1);
            CommitDraft(registry, run, pendingRequest);
            object p1;
            Assert.That(TryConsumeDropTrace(registry, 1, out p1), Is.False);

            // Staged entry (not dropped).
            SetEntryField(registry, 0, "Status", Enum.ToObject(GetStatusType(), 1));
            object p2;
            Assert.That(TryConsumeDropTrace(registry, 1, out p2), Is.False);

            // Dropped with the freeze reason (9): never consumable.
            CaptureFrameRequest freezeRequest = MakeRequest(2);
            CommitDraft(registry, run, freezeRequest);
            SetEntryField(registry, 1, "Status", Enum.ToObject(GetStatusType(), 2));
            SetEntryField(registry, 1, "DropReason", (CaptureFrameDropReason)9);
            SetEntryField(registry, 1, "EmissionState", Enum.ToObject(GetEmissionStateType(), 1));
            object p3;
            Assert.That(TryConsumeDropTrace(registry, 2, out p3), Is.False);
        }

        // ---- RecordDraftDropped contracts ----

        [Test]
        public void RecordDraftDropped_GeneratesExactlyOneEvent()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                object run = MakeRun(captureProfileId: 5);
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
                CaptureFrameRequest request = MakeRequest(7);
                CommitDraft(registry, run, request);
                MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                Assert.That(RecordDraftDropped(observer, registry, 7), Is.True);
                Assert.That(logger.Drain(), Is.EqualTo(1));

                Assert.That(logger.HistoryCount, Is.EqualTo(1));
                Assert.That(logger.TotalWritten, Is.EqualTo(1));
            }
        }

        [Test]
        public void RecordDraftDropped_Reasons6To8_EachPayload()
        {
            foreach (int reason in new[] { 6, 7, 8 })
            {
                using (TraceLogger logger = new TraceLogger(8))
                {
                    object run = MakeRun(captureProfileId: 5);
                    object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5, maxInFlight: 1, maxDraftPerRun: 4));
                    CaptureFrameRequest request = MakeRequest(7);
                    CommitDraft(registry, run, request);
                    MarkDropped(registry, request, (CaptureFrameDropReason)reason);

                    CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                    Assert.That(RecordDraftDropped(observer, registry, 7), Is.True);
                    logger.Drain();

                    AssertDraftDropEvent(logger.GetHistoryEvent(0), request.TraceContext, reason);
                }
            }
        }

        [Test]
        public void RecordDraftDropped_CorrelationValuesTranscribed()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                object run = MakeRun(captureProfileId: 5);
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));

                CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                    111, 222, 333, 44, 55, 666, 1, 777, 888, 999, 1010u, 1212);
                CaptureFrameRequest request = MakeRequestWithContext(context);
                CommitDraft(registry, run, request);
                MarkDropped(registry, request, CaptureFrameDropReason.CaptureCancelled);

                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                Assert.That(RecordDraftDropped(observer, registry, 55), Is.True);
                logger.Drain();

                AssertDraftDropEvent(logger.GetHistoryEvent(0), context, 8);
            }
        }

        [Test]
        public void RecordDraftDropped_NoConsumableTrace_TouchesNoLogger()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                object run = MakeRun(captureProfileId: 5);
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));

                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                // Missing ID and a still-pending entry both return false without
                // touching the logger.
                Assert.That(RecordDraftDropped(observer, registry, 999), Is.False);
                Assert.That(RecordDraftDropped(observer, registry, 1), Is.False);

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));
            }
        }

        [Test]
        public void RecordDraftDropped_DisposedLogger_DraftDroppedSlotFreedEmissionAttempted_NoReenqueue()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);
            MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            logger.Dispose();

            Exception ex = RecordDraftDroppedException(observer, registry, 7);
            Assert.That(ex, Is.TypeOf<ObjectDisposedException>());

            Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(2)); // still Dropped
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0)); // slot freed
            Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(2)); // Attempted

            // Second attempt does not re-enqueue.
            Assert.That(RecordDraftDropped(observer, registry, 7), Is.False);
        }

        [Test]
        public void RecordDraftDropped_TestRunIdMismatch_Throws_DraftDroppedSlotFreedEmissionAttempted_NoReenqueue()
        {
            TraceLogger logger = CreateCaptureLogger(8, 42);
            try
            {
                object run = MakeRun(captureProfileId: 5); // testRunId = 1
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
                CaptureFrameRequest request = MakeRequest(7); // testRunId = 1
                CommitDraft(registry, run, request);
                MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Exception ex = RecordDraftDroppedException(observer, registry, 7);
                Assert.That(ex, Is.TypeOf<ArgumentException>());

                Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(2));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
                Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(2));

                Assert.That(RecordDraftDropped(observer, registry, 7), Is.False);
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void RecordDraftDropped_SealedLogger_PostSealAttemptIncrementsOnce_NoReenqueue()
        {
            TraceLogger logger = CreateCaptureLogger(8, 1); // bound to the draft's testRunId
            try
            {
                TraceFlightRecorder recorder = CreateRecorder(logger, 10, 1);
                Assert.That(recorder.TryTrigger(), Is.True);
                Seal(logger, 1, recorder); // actually seals the capture run

                object run = MakeRun(captureProfileId: 5); // testRunId = 1
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
                CaptureFrameRequest request = MakeRequest(7); // testRunId = 1
                CommitDraft(registry, run, request);
                MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                // The sealed gate rejects the enqueue silently (no exception), so
                // the attempt is counted exactly once and the event is dropped.
                Assert.That(RecordDraftDropped(observer, registry, 7), Is.True);
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(1));
                Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(2)); // Attempted

                // The rejected event never entered the queue or history.
                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
                Assert.That(logger.TotalWritten, Is.EqualTo(0));

                // Re-calling consumes nothing and does not attempt another enqueue.
                Assert.That(RecordDraftDropped(observer, registry, 7), Is.False);
                Assert.That(GetCount(logger, "PostSealTraceEnqueueAttemptCount"), Is.EqualTo(1));
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void RecordDraftDropped_NativeQueueWriteFailure_OriginalException_FailureCountOne_NoRetry()
        {
            TraceLogger logger = CreateCaptureLogger(8, 1); // bound to the draft's testRunId
            try
            {
                object run = MakeRun(captureProfileId: 5); // testRunId = 1
                object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
                CaptureFrameRequest request = MakeRequest(7); // testRunId = 1
                CommitDraft(registry, run, request);
                MarkDropped(registry, request, CaptureFrameDropReason.PngEncodeFailed);

                // Force the native queue write to fail before the observer runs.
                FieldInfo queueField = typeof(TraceLogger).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(queueField, Is.Not.Null, "_queue field not found.");
                NativeQueue<TraceEvent> queue = (NativeQueue<TraceEvent>)queueField.GetValue(logger);
                queue.Dispose();
                queueField.SetValue(logger, queue);

                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);

                Exception ex = RecordDraftDroppedException(observer, registry, 7);
                // The observer neither translates nor wraps the native queue's
                // use-after-dispose exception.
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());

                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(1));

                Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(2)); // still Dropped
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0)); // slot freed
                Assert.That((int)GetEntryField(registry, 0, "EmissionState"), Is.EqualTo(2)); // Attempted

                // No retry: the second call consumes nothing and the failure
                // count does not change.
                Assert.That(RecordDraftDropped(observer, registry, 7), Is.False);
                Assert.That(GetCount(logger, "TraceEnqueueFailureCount"), Is.EqualTo(1));
            }
            finally
            {
                logger.Dispose();
            }
        }

        // ---- Freeze reason isolation ----

        [Test]
        public void FreezeReason9_NotReachableViaNormalDropPath()
        {
            object run = MakeRun(captureProfileId: 5);
            object registry = MakeRegistry(run, MakeProfile(captureProfileId: 5));
            CaptureFrameRequest request = MakeRequest(7);
            CommitDraft(registry, run, request);

            Exception markEx = MarkDroppedException(registry, request, (CaptureFrameDropReason)9);
            Assert.That(markEx, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)markEx).ParamName, Is.EqualTo("reason"));
            Assert.That((int)GetEntryField(registry, 0, "Status"), Is.EqualTo(0)); // unchanged

            Exception payloadEx = CreatePayloadException(MakeDraftTraceContext(request.TraceContext), (CaptureFrameDropReason)9);
            Assert.That(payloadEx, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)payloadEx).ParamName, Is.EqualTo("reason"));
        }

        // ---- Existing contract preservation ----

        [Test]
        public void LegacyRecordDropped_StillRejectsReasons6To9()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, 55, 30, 1, 5, 6, 7, 8u, 9);

                foreach (int reason in new[] { 6, 7, 8, 9 })
                {
                    ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                        () => observer.RecordDropped(context, (CaptureFrameDropReason)reason));
                    Assert.That(ex.ParamName, Is.EqualTo("reason"));
                }

                Assert.That(logger.Drain(), Is.EqualTo(0));
                Assert.That(logger.HistoryCount, Is.EqualTo(0));
            }
        }

        // ---- Type shape: no new public API, no disposal, no static state ----

        [Test]
        public void Types_NoNewPublicApi_NotDisposable_NoStaticState()
        {
            Type registryType = GetRegistryType();
            Assert.That(registryType.GetMethod("MarkDropped", BindingFlags.Public | BindingFlags.Instance), Is.Null);
            Assert.That(registryType.GetMethod("TryConsumeDropTrace", BindingFlags.Public | BindingFlags.Instance), Is.Null);
            Assert.That(typeof(IDisposable).IsAssignableFrom(registryType), Is.False);
            Assert.That(registryType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            Type payloadType = GetPayloadType();
            Assert.That(payloadType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(payloadType), Is.False);
            Assert.That(payloadType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            Assert.That(typeof(CaptureFrameTraceObserver).GetMethod("RecordDraftDropped", BindingFlags.Public | BindingFlags.Instance), Is.Null);
            Assert.That(typeof(CaptureFrameTraceObserver).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }
    }
}
