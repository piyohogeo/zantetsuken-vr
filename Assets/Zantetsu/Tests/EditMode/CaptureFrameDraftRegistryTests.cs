using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftRegistryTests
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

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetReservationType() => GetTypeFromAssembly("CaptureFrameDraftReservation");

        private static Type GetRejectKindType() => GetTypeFromAssembly("CaptureFrameAdmissionRejectKind");

        private static Type GetStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, target.GetType().Name + "." + name + " field not found.");
            return field.GetValue(target);
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

        private static TraceRunContext MakeTraceRunContext(
            long testRunId = 1,
            string buildId = "build-1",
            string sceneId = "scene-1",
            long randomSeed = 12345)
        {
            return new TraceRunContext(
                testRunId,
                1000,
                buildId,
                "6000.3.22f1",
                ValidSha256,
                sceneId,
                randomSeed,
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

        private static CaptureTraceProfile MakeProfile(int captureProfileId = 5, int maxInFlight = 2, int maxDraftPerRun = 2)
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

        private static object MakeReservation(Guid ownerId, long generation, int slotIndex)
        {
            ConstructorInfo ctor = GetReservationType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(Guid), typeof(long), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftReservation constructor not found.");
            return ctor.Invoke(new object[] { ownerId, generation, slotIndex });
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

        private static Exception CommitException(object registry, object reservation, object draft)
        {
            try
            {
                Commit(registry, reservation, draft);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static void Cancel(object registry, object reservation)
        {
            MethodInfo method = GetRegistryType().GetMethod("Cancel", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "Cancel method not found.");
            method.Invoke(registry, new object[] { reservation });
        }

        private static Exception CancelException(object registry, object reservation)
        {
            try
            {
                Cancel(registry, reservation);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
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

        private static void SetSlotGeneration(object registry, int slotIndex, long generation)
        {
            FieldInfo field = GetRegistryType().GetField("_slotGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_slotGeneration field not found.");
            long[] generations = (long[])field.GetValue(registry);
            generations[slotIndex] = generation;
        }

        private static long GetSlotGeneration(object registry, int slotIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotGeneration", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_slotGeneration field not found.");
            long[] generations = (long[])field.GetValue(registry);
            return generations[slotIndex];
        }

        private static int GetSlotState(object registry, int slotIndex)
        {
            FieldInfo field = GetRegistryType().GetField("_slotState", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "_slotState field not found.");
            Array states = (Array)field.GetValue(registry);
            return (int)states.GetValue(slotIndex);
        }

        private static Exception TryReserveException(object registry)
        {
            try
            {
                object reservation;
                object rejectKind;
                TryReserve(registry, out reservation, out rejectKind);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Enum contracts ----

        [Test]
        public void RejectKind_UnderlyingTypeIsInt()
        {
            Assert.That(Enum.GetUnderlyingType(GetRejectKindType()), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void RejectKind_NamesAndValues_MatchExactly()
        {
            Type type = GetRejectKindType();

            Assert.That(Enum.GetName(type, 0), Is.EqualTo("None"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("PendingLimit"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("RunEntryLimit"));
        }

        [Test]
        public void RejectKind_HasNoAliasesOrGaps()
        {
            Type type = GetRejectKindType();

            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(3));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(3));

            for (int i = 0; i <= 2; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 3), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Constructor contracts ----

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            ConstructorInfo ctor = GetRegistryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRunType(), typeof(CaptureTraceProfile) },
                null);

            try
            {
                ctor.Invoke(new object[] { null, MakeProfile() });
                Assert.Fail("Expected ArgumentNullException for null run.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("run"));
            }

            try
            {
                ctor.Invoke(new object[] { MakeRun(), null });
                Assert.Fail("Expected ArgumentNullException for null profile.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo("profile"));
            }
        }

        [Test]
        public void Constructor_ProfileIdMismatch_Rejected()
        {
            object run = MakeRun(captureProfileId: 5);
            CaptureTraceProfile profile = MakeProfile(captureProfileId: 6);

            ConstructorInfo ctor = GetRegistryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRunType(), typeof(CaptureTraceProfile) },
                null);

            try
            {
                ctor.Invoke(new object[] { run, profile });
                Assert.Fail("Expected ArgumentException for profile ID mismatch.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex.InnerException).ParamName, Is.EqualTo("profile"));
            }
        }

        [Test]
        public void Constructor_SizesCapacitiesFromProfile()
        {
            object run = MakeRun(captureProfileId: 5);
            CaptureTraceProfile profile = MakeProfile(captureProfileId: 5, maxInFlight: 7, maxDraftPerRun: 100);
            object registry = MakeRegistry(run, profile);

            Assert.That(Count(registry, "EntryCapacity"), Is.EqualTo(100));
            Assert.That(Count(registry, "PendingCapacity"), Is.EqualTo(7));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            Assert.That(ReferenceEquals(GetProperty(registry, "Run"), run), Is.True);
        }

        // ---- Reservation contracts ----

        [Test]
        public void TryReserve_Success_SetsCountsAndValidReservation()
        {
            object registry = MakeRegistry();

            object reservation;
            object rejectKind;
            bool ok = TryReserve(registry, out reservation, out rejectKind);

            Assert.That(ok, Is.True);
            Assert.That((int)rejectKind, Is.EqualTo(0));
            Assert.That((bool)GetProperty(reservation, "IsValid"), Is.True);
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));
        }

        [Test]
        public void TryReserve_PendingExhausted_ReturnsPendingLimit()
        {
            object registry = MakeRegistry(profile: MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object r1, k1, r2, k2;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);

            object r3, k3;
            bool ok = TryReserve(registry, out r3, out k3);

            Assert.That(ok, Is.False);
            Assert.That((int)k3, Is.EqualTo(1));
            Assert.That((bool)GetProperty(r3, "IsValid"), Is.False);
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(2));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
        }

        [Test]
        public void TryReserve_EntryExhausted_ReturnsRunEntryLimit()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 2));

            object r1, k1, r2, k2;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Commit(registry, r1, MakeDraft(run, MakeRequest(1)));
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Commit(registry, r2, MakeDraft(run, MakeRequest(2)));

            object r3, k3;
            bool ok = TryReserve(registry, out r3, out k3);

            Assert.That(ok, Is.False);
            Assert.That((int)k3, Is.EqualTo(2));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(2));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
        }

        [Test]
        public void TryReserve_ReservationCountsTowardCapacity()
        {
            object registry = MakeRegistry(profile: MakeProfile(maxInFlight: 1, maxDraftPerRun: 2));

            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);

            // A single uncommitted reservation already exhausts the pending pool
            // even though the entry store still has room.
            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.False);
            Assert.That((int)k2, Is.EqualTo(1));
        }

        [Test]
        public void Cancel_FreesPendingSlotForReservation()
        {
            object registry = MakeRegistry(profile: MakeProfile(maxInFlight: 1, maxDraftPerRun: 2));

            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));

            Cancel(registry, r1);
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(0));

            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
        }

        [Test]
        public void TryReserve_SkipsExhaustedSlotAndUsesAnotherFreeSlot()
        {
            object registry = MakeRegistry(profile: MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));
            SetSlotGeneration(registry, 0, long.MaxValue);

            object reservation;
            object rejectKind;
            bool ok = TryReserve(registry, out reservation, out rejectKind);

            Assert.That(ok, Is.True);
            Assert.That((int)rejectKind, Is.EqualTo(0));
            Assert.That((bool)GetProperty(reservation, "IsValid"), Is.True);
            Assert.That((int)GetField(reservation, "PendingSlotIndex"), Is.EqualTo(1));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
        }

        [Test]
        public void TryReserve_AllFreeSlotsExhausted_ThrowsOverflowWithoutMutation()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            // Commit one entry into slot 0, then free slot 1 and exhaust it.
            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            CaptureFrameRequest request = MakeRequest(1);
            Commit(registry, r1, MakeDraft(run, request));

            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Assert.That((int)GetField(r2, "PendingSlotIndex"), Is.EqualTo(1));
            Cancel(registry, r2);

            SetSlotGeneration(registry, 1, long.MaxValue);

            Exception ex = TryReserveException(registry);
            Assert.That(ex, Is.TypeOf<OverflowException>());

            // Counts are unchanged.
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));

            // The exhausted free slot stays free and unmodified.
            Assert.That(GetSlotState(registry, 1), Is.EqualTo(0));
            Assert.That(GetSlotGeneration(registry, 1), Is.EqualTo(long.MaxValue));

            // The existing entry is untouched and still retrievable.
            object found;
            object status;
            Assert.That(TryGet(registry, request, out found, out status), Is.True);
        }

        // ---- Commit contracts ----

        [Test]
        public void Commit_Success_ReturnsSameDraftAndPendingStatus()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            CaptureFrameRequest request = MakeRequest(10);
            object draft = MakeDraft(run, request);

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            Commit(registry, reservation, draft);

            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));

            object found;
            object status;
            bool ok = TryGet(registry, request, out found, out status);
            Assert.That(ok, Is.True);
            Assert.That(ReferenceEquals(found, draft), Is.True);
            Assert.That((int)status, Is.EqualTo(0)); // Pending
        }

        [Test]
        public void Commit_EntriesAreAppendOnlyAndRetained()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Commit(registry, r1, MakeDraft(run, MakeRequest(1)));

            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Commit(registry, r2, MakeDraft(run, MakeRequest(2)));

            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(2));
            Assert.That(Count(registry, "PendingCount"), Is.EqualTo(2));

            object found;
            object status;
            Assert.That(TryGet(registry, MakeRequest(1), out found, out status), Is.True);
            Assert.That(TryGet(registry, MakeRequest(2), out found, out status), Is.True);
        }

        [Test]
        public void Commit_AllowsIdGaps_MaintainsStrictAscendingOrder()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Commit(registry, r1, MakeDraft(run, MakeRequest(1)));

            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Commit(registry, r2, MakeDraft(run, MakeRequest(5)));

            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(2));
        }

        [Test]
        public void Commit_DuplicateCaptureFrameId_RejectedUnchanged()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Commit(registry, r1, MakeDraft(run, MakeRequest(5)));

            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Exception ex = CommitException(registry, r2, MakeDraft(run, MakeRequest(5)));

            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
        }

        [Test]
        public void Commit_OutOfOrderCaptureFrameId_RejectedUnchanged()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object r1, k1;
            Assert.That(TryReserve(registry, out r1, out k1), Is.True);
            Commit(registry, r1, MakeDraft(run, MakeRequest(5)));

            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Exception ex = CommitException(registry, r2, MakeDraft(run, MakeRequest(3)));

            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
        }

        [Test]
        public void Commit_DraftFromDifferentRun_Rejected()
        {
            object run = MakeRun(captureProfileId: 5);
            object otherRun = MakeRun(captureProfileId: 6);
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);

            Exception ex = CommitException(registry, reservation, MakeDraft(otherRun, MakeRequest(1, testRunId: 1)));

            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));
        }

        [Test]
        public void Commit_InvalidOwner_Rejected()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run);

            object forged = MakeReservation(Guid.NewGuid(), 1L, 0);
            Exception ex = CommitException(registry, forged, MakeDraft(run, MakeRequest(1)));

            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
        }

        [Test]
        public void Commit_DefaultReservation_Rejected()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run);

            Exception ex = CommitException(registry, MakeReservation(Guid.Empty, 0L, 0), MakeDraft(run, MakeRequest(1)));

            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Commit_StaleAndDoubleCommit_Rejected()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);

            object draft = MakeDraft(run, MakeRequest(1));
            Commit(registry, reservation, draft);

            // Double commit.
            Assert.That(CommitException(registry, reservation, draft), Is.TypeOf<InvalidOperationException>());

            // Stale: cancel then commit again with the same reservation copy.
            object r2, k2;
            Assert.That(TryReserve(registry, out r2, out k2), Is.True);
            Cancel(registry, r2);
            Assert.That(CommitException(registry, r2, MakeDraft(run, MakeRequest(2))), Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Cancel_DoubleCancel_Rejected()
        {
            object registry = MakeRegistry();

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            Cancel(registry, reservation);
            Assert.That(CancelException(registry, reservation), Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Commit_FailureLeavesReservationValidForExplicitCancel()
        {
            object run = MakeRun(captureProfileId: 5);
            object otherRun = MakeRun(captureProfileId: 6);
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);

            Exception ex = CommitException(registry, reservation, MakeDraft(otherRun, MakeRequest(1, testRunId: 1)));
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(1));

            // The reservation remains valid and can be explicitly cancelled.
            Cancel(registry, reservation);
            Assert.That(Count(registry, "ReservationCount"), Is.EqualTo(0));
            Assert.That(Count(registry, "EntryCount"), Is.EqualTo(0));
        }

        [Test]
        public void Commit_NullDraft_Rejected()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run);

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);

            Exception ex = CommitException(registry, reservation, null);
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("draft"));
        }

        // ---- TryGet contracts ----

        [Test]
        public void TryGet_NotPresent_ReturnsFalseWithNull()
        {
            object registry = MakeRegistry();

            object draft;
            object status;
            bool ok = TryGet(registry, MakeRequest(99), out draft, out status);

            Assert.That(ok, Is.False);
            Assert.That(draft, Is.Null);
            Assert.That((int)status, Is.EqualTo(0));
        }

        [Test]
        public void TryGet_RequestMismatch_Rejected()
        {
            object run = MakeRun();
            object registry = MakeRegistry(run, MakeProfile(maxInFlight: 2, maxDraftPerRun: 10));

            CaptureFrameRequest request = MakeRequest(10);
            object draft = MakeDraft(run, request);

            object reservation, rejectKind;
            Assert.That(TryReserve(registry, out reservation, out rejectKind), Is.True);
            Commit(registry, reservation, draft);

            // Same ID, different source.
            CaptureFrameRequest other = MakeRequest(10, source: CaptureSource.OpenXRProjection);

            object found;
            object status;
            try
            {
                TryGet(registry, other, out found, out status);
                Assert.Fail("Expected InvalidOperationException.");
            }
            catch (Exception ex)
            {
                Assert.That(Unwrap(ex), Is.TypeOf<InvalidOperationException>());
            }
        }

        [Test]
        public void TryGet_InvalidRequest_Rejected()
        {
            object registry = MakeRegistry();

            object draft;
            object status;
            try
            {
                TryGet(registry, default(CaptureFrameRequest), out draft, out status);
                Assert.Fail("Expected ArgumentException.");
            }
            catch (Exception ex)
            {
                Assert.That(Unwrap(ex), Is.TypeOf<ArgumentException>());
            }
        }

        // ---- Type shape contracts ----

        [Test]
        public void Type_HasNoTerminalRemoveOrClearApis()
        {
            Type type = GetRegistryType();

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.Name, Is.Not.EqualTo("Clear"), "Registry must not expose Clear.");
                Assert.That(method.Name, Is.Not.EqualTo("Remove"), "Registry must not expose Remove.");
                Assert.That(method.Name, Is.Not.EqualTo("Stage"), "Registry must not expose Stage.");
                Assert.That(method.Name, Is.Not.EqualTo("Drop"), "Registry must not expose Drop.");
            }

            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Type_FixedArraysAreReadonlyAndSizedToCapacities()
        {
            object run = MakeRun(captureProfileId: 5);
            CaptureTraceProfile profile = MakeProfile(captureProfileId: 5, maxInFlight: 3, maxDraftPerRun: 7);
            object registry = MakeRegistry(run, profile);

            int entryCapacity = Count(registry, "EntryCapacity");
            int pendingCapacity = Count(registry, "PendingCapacity");

            foreach (FieldInfo field in GetRegistryType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType.IsArray)
                {
                    Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                    Array array = (Array)field.GetValue(registry);
                    Assert.That(array.Length, Is.EqualTo(field.Name == "_entries" ? entryCapacity : pendingCapacity),
                        field.Name + " must be sized to its capacity and never resized.");
                }
            }
        }

        [Test]
        public void Type_HasNoCollectionOrStaticMutableState()
        {
            Type type = GetRegistryType();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string fullName = field.FieldType.FullName ?? field.FieldType.Name;
                Assert.That(fullName.IndexOf("List`", StringComparison.Ordinal), Is.LessThan(0), field.Name + " must not be a List.");
                Assert.That(fullName.IndexOf("Dictionary`", StringComparison.Ordinal), Is.LessThan(0), field.Name + " must not be a Dictionary.");
                Assert.That(fullName.IndexOf("Collection", StringComparison.Ordinal), Is.LessThan(0), field.Name + " must not be a mutable collection.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Type_HoldsNoSequenceOrFactory()
        {
            Type type = GetRegistryType();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameIdSequence)), field.Name + " must not hold an ID sequence.");
                Assert.That(field.FieldType, Is.Not.EqualTo(GetTypeFromAssembly("CaptureFrameDraftFactory")), field.Name + " must not hold a draft factory.");
            }
        }

        [Test]
        public void Type_IsInternalSealedNotDisposableMonoBehaviourOrScriptableObject()
        {
            Type type = GetRegistryType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False);
        }
    }
}
