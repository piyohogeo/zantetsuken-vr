using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationRecoveryInspectionTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;

            public FakeHandle(string lockPath, bool isCreated = true, List<string> disposeLog = null)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
                _disposeLog?.Add(LockPath);
            }
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            public int CallCount { get; private set; }

            public CaptureRunInitializationRecoveryInspectionOperation LastOperation { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this,
                    operation,
                    MakeStagingObservation(),
                    MakeFinalObservation());
            }
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog, out FakeHandle first, out FakeHandle second)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunLockIdentityEvidence MakeIdentity(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog, out _, out _);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            return CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
        }

        private static CaptureRunInitializationMarker MakeInitMarker(CaptureRunRootRole role, long testRunId = 1)
        {
            return new CaptureRunInitializationMarker(testRunId, InitId, role, StagingHash, FinalHash);
        }

        private static CaptureRunReadyMarker MakeReadyMarker()
        {
            return new CaptureRunReadyMarker(1, InitId, StagingHash, FinalHash);
        }

        private static CaptureRunInitializationRootObservation MakeStagingObservation()
        {
            return new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging,
                true,
                false,
                CaptureRunMarkerObservationStatus.Canonical,
                MakeInitMarker(CaptureRunRootRole.Staging),
                false,
                CaptureRunMarkerObservationStatus.Canonical,
                MakeReadyMarker(),
                false,
                false,
                false);
        }

        private static CaptureRunInitializationRootObservation MakeFinalObservation()
        {
            return new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Final,
                true,
                false,
                CaptureRunMarkerObservationStatus.Canonical,
                MakeInitMarker(CaptureRunRootRole.Final),
                false,
                CaptureRunMarkerObservationStatus.Canonical,
                MakeReadyMarker(),
                false,
                false,
                false);
        }

        private static CaptureRunInitializationRootObservation MakeForgedInconsistentStaging()
        {
            CaptureRunInitializationRootObservation observation = (CaptureRunInitializationRootObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRootObservation));
            SetField(observation, "_rootRole", CaptureRunRootRole.Staging);
            SetField(observation, "_rootExists", true);
            SetField(observation, "_initializationStatus", CaptureRunMarkerObservationStatus.Canonical);
            return observation;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryInspectionTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
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

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }

        // ---- Enum ----

        [Test]
        public void Enum_Contract()
        {
            Type type = typeof(CaptureRunMarkerObservationStatus);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            string[] names = Enum.GetNames(type);
            Assert.That(names, Is.EqualTo(new[] { "Absent", "Canonical", "Invalid" }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(3));
            Assert.That((int)values.GetValue(0), Is.EqualTo(0));
            Assert.That((int)values.GetValue(1), Is.EqualTo(1));
            Assert.That((int)values.GetValue(2), Is.EqualTo(2));

            Assert.That((int)values.GetValue(0), Is.Not.EqualTo((int)values.GetValue(1)));
            Assert.That((int)values.GetValue(1), Is.Not.EqualTo((int)values.GetValue(2)));
        }

        // ---- Root observation ----

        [Test]
        public void Observation_AbsentRoot_HoldsNothing()
        {
            CaptureRunInitializationRootObservation observation = new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging,
                false,
                false,
                CaptureRunMarkerObservationStatus.Absent,
                null,
                false,
                CaptureRunMarkerObservationStatus.Absent,
                null,
                false,
                false,
                false);

            Assert.That(observation.RootExists, Is.False);
            Assert.That(observation.HasInitializationTemporary, Is.False);
            Assert.That(observation.InitializationStatus, Is.EqualTo(CaptureRunMarkerObservationStatus.Absent));
            Assert.That(observation.InitializationMarker, Is.Null);
            Assert.That(observation.HasReadyTemporary, Is.False);
            Assert.That(observation.ReadyStatus, Is.EqualTo(CaptureRunMarkerObservationStatus.Absent));
            Assert.That(observation.ReadyMarker, Is.Null);
            Assert.That(observation.HasNonMarkerEntries, Is.False);
            Assert.That(observation.HasUnknownEntries, Is.False);
            Assert.That(observation.RootEntryLimitExceeded, Is.False);
        }

        [Test]
        public void Observation_Canonical_And_Invalid_Accepted()
        {
            CaptureRunInitializationRootObservation canonical = new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, true, true,
                CaptureRunMarkerObservationStatus.Canonical, MakeInitMarker(CaptureRunRootRole.Staging),
                true, CaptureRunMarkerObservationStatus.Canonical, MakeReadyMarker(),
                false, false, false);

            Assert.That(canonical.InitializationMarker, Is.Not.Null);
            Assert.That(canonical.ReadyMarker, Is.Not.Null);

            CaptureRunInitializationRootObservation invalid = new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Final, true, false,
                CaptureRunMarkerObservationStatus.Invalid, null,
                false, CaptureRunMarkerObservationStatus.Invalid, null,
                true, true, true);

            Assert.That(invalid.InitializationMarker, Is.Null);
            Assert.That(invalid.ReadyMarker, Is.Null);
            Assert.That(invalid.HasNonMarkerEntries, Is.True);
            Assert.That(invalid.HasUnknownEntries, Is.True);
            Assert.That(invalid.RootEntryLimitExceeded, Is.True);
        }

        [Test]
        public void Observation_CanonicalWithMismatchedValues_AcceptedAsFact()
        {
            CaptureRunInitializationMarker mismatched = MakeInitMarker(CaptureRunRootRole.Staging, testRunId: 999);

            CaptureRunInitializationRootObservation observation = new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, true, false,
                CaptureRunMarkerObservationStatus.Canonical, mismatched,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false);

            Assert.That(observation.InitializationMarker, Is.SameAs(mismatched));
        }

        [Test]
        public void Observation_InvalidRole_Rejected()
        {
            foreach (CaptureRunRootRole role in new[]
            {
                CaptureRunRootRole.None,
                (CaptureRunRootRole)(-1),
                (CaptureRunRootRole)3,
                (CaptureRunRootRole)int.MaxValue
            })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunInitializationRootObservation(
                    role, true, false,
                    CaptureRunMarkerObservationStatus.Absent, null,
                    false, CaptureRunMarkerObservationStatus.Absent, null,
                    false, false, false));

                Assert.That(ex.ParamName, Is.EqualTo("rootRole"));
            }
        }

        [Test]
        public void Observation_UndefinedStatus_Rejected()
        {
            CaptureRunMarkerObservationStatus undefined = (CaptureRunMarkerObservationStatus)3;

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, true, false,
                undefined, null,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));

            Assert.That(ex.ParamName, Is.EqualTo("initializationStatus"));
        }

        [Test]
        public void Observation_MissingRoot_WithContent_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, false, true,
                CaptureRunMarkerObservationStatus.Absent, null,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, false, false,
                CaptureRunMarkerObservationStatus.Canonical, null,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, false, false,
                CaptureRunMarkerObservationStatus.Absent, MakeInitMarker(CaptureRunRootRole.Staging),
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, false, false,
                CaptureRunMarkerObservationStatus.Absent, null,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, true));
        }

        [Test]
        public void Observation_CanonicalWithNullMarker_Rejected()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, true, false,
                CaptureRunMarkerObservationStatus.Canonical, null,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));

            Assert.That(ex.ParamName, Is.EqualTo("initializationMarker"));
        }

        [Test]
        public void Observation_AbsentOrInvalidWithMarker_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, true, false,
                CaptureRunMarkerObservationStatus.Absent, MakeInitMarker(CaptureRunRootRole.Staging),
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, true, false,
                CaptureRunMarkerObservationStatus.Invalid, MakeInitMarker(CaptureRunRootRole.Staging),
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false));
        }

        [Test]
        public void Observation_Fields_AreElevenReadonly()
        {
            Type type = typeof(CaptureRunInitializationRootObservation);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(11));

            int roleFields = 0;
            int statusFields = 0;
            int initMarkerFields = 0;
            int readyMarkerFields = 0;
            int boolFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunRootRole)) roleFields++;
                else if (field.FieldType == typeof(CaptureRunMarkerObservationStatus)) statusFields++;
                else if (field.FieldType == typeof(CaptureRunInitializationMarker)) initMarkerFields++;
                else if (field.FieldType == typeof(CaptureRunReadyMarker)) readyMarkerFields++;
                else if (field.FieldType == typeof(bool)) boolFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(roleFields, Is.EqualTo(1));
            Assert.That(statusFields, Is.EqualTo(2));
            Assert.That(initMarkerFields, Is.EqualTo(1));
            Assert.That(readyMarkerFields, Is.EqualTo(1));
            Assert.That(boolFields, Is.EqualTo(6));
        }

        [Test]
        public void Observation_IsValid_ValidCases_True()
        {
            Assert.That(MakeStagingObservation().IsValid, Is.True);
            Assert.That(MakeFinalObservation().IsValid, Is.True);

            CaptureRunInitializationRootObservation absent = new CaptureRunInitializationRootObservation(
                CaptureRunRootRole.Staging, false, false,
                CaptureRunMarkerObservationStatus.Absent, null,
                false, CaptureRunMarkerObservationStatus.Absent, null,
                false, false, false);

            Assert.That(absent.IsValid, Is.True);
        }

        [Test]
        public void Observation_Uninitialized_IsInvalid()
        {
            CaptureRunInitializationRootObservation observation = (CaptureRunInitializationRootObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRootObservation));

            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void Observation_ForgedInconsistent_IsInvalid()
        {
            Assert.That(MakeForgedInconsistentStaging().IsValid, Is.False);
        }

        // ---- Inspection operation ----

        [Test]
        public void Operation_NullRootLayout_Rejected()
        {
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(MakeLayout(), null, out owner);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryInspectionOperation(null, identity, 4));

            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
            owner.Dispose();
        }

        [Test]
        public void Operation_NullIdentity_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryInspectionOperation(MakeLayout(), null, 4));

            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
        }

        [Test]
        public void Operation_DisposedOwner_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            owner.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4));

            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
        }

        [Test]
        public void Operation_ForeignRootLayout_Rejected()
        {
            CaptureRunRootLayout layoutA = MakeLayout(1);
            CaptureRunRootLayout layoutB = MakeLayout(2);
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layoutA, null, out owner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryInspectionOperation(layoutB, identity, 4));

            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
            owner.Dispose();
        }

        [Test]
        public void Operation_NonPositiveMaxEntryCount_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);

            foreach (int count in new[] { 0, -1, int.MinValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, count));

                Assert.That(ex.ParamName, Is.EqualTo("maximumRootEntryCount"));
            }
            owner.Dispose();
        }

        [Test]
        public void Operation_MaxEntryCountBoundary_Accepted()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);

            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(
                layout, identity, CaptureRunInitializationRecoveryInspectionOperation.MaximumAllowedRootEntryCount);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.MaximumRootEntryCount, Is.EqualTo(CaptureRunInitializationRecoveryInspectionOperation.MaximumAllowedRootEntryCount));
            Assert.That(operation.ProbeCount, Is.EqualTo(CaptureRunInitializationRecoveryInspectionOperation.MaximumAllowedRootEntryCount + 1));
            owner.Dispose();
        }

        [Test]
        public void Operation_MaxEntryCountPlusOne_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new CaptureRunInitializationRecoveryInspectionOperation(
                    layout, identity, CaptureRunInitializationRecoveryInspectionOperation.MaximumAllowedRootEntryCount + 1));

            Assert.That(ex.ParamName, Is.EqualTo("maximumRootEntryCount"));
            owner.Dispose();
        }

        [Test]
        public void Operation_IntMaxValue_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, int.MaxValue));

            Assert.That(ex.ParamName, Is.EqualTo("maximumRootEntryCount"));
            owner.Dispose();
        }

        [Test]
        public void Operation_ProbeCount_OverflowWhenForged()
        {
            CaptureRunInitializationRecoveryInspectionOperation operation = (CaptureRunInitializationRecoveryInspectionOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryInspectionOperation));
            SetField(operation, "_maximumRootEntryCount", int.MaxValue);

            Assert.Throws<OverflowException>(() =>
            {
                int probeCount = operation.ProbeCount;
            });
        }

        [Test]
        public void Operation_IsValid_BeforeAndAfterOwnerRelease()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            Assert.That(operation.IsValid, Is.True);

            owner.Dispose();

            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Operation_HoldsByReference_And_DoesNotDisposeOwner()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, disposeLog, out owner);

            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            Assert.That(operation.RootLayout, Is.SameAs(layout));
            Assert.That(operation.LockIdentityEvidence, Is.SameAs(identity));
            Assert.That(operation.MaximumRootEntryCount, Is.EqualTo(4));
            Assert.That(disposeLog, Is.Empty, "The operation must not dispose the owner.");
            owner.Dispose();
        }

        [Test]
        public void Operation_Uninitialized_IsInvalid()
        {
            CaptureRunInitializationRecoveryInspectionOperation operation = (CaptureRunInitializationRecoveryInspectionOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryInspectionOperation));

            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Operation_Fields_AreThreeReadonly()
        {
            Type type = typeof(CaptureRunInitializationRecoveryInspectionOperation);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(3));

            int layoutFields = 0;
            int identityFields = 0;
            int intFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunRootLayout)) layoutFields++;
                else if (field.FieldType == typeof(CaptureRunLockIdentityEvidence)) identityFields++;
                else if (field.FieldType == typeof(int)) intFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(layoutFields, Is.EqualTo(1));
            Assert.That(identityFields, Is.EqualTo(1));
            Assert.That(intFields, Is.EqualTo(1));
        }

        // ---- Snapshot ----

        [Test]
        public void Snapshot_NullArgs_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                null, operation, MakeStagingObservation(), MakeFinalObservation()));
            Assert.That(ex1.ParamName, Is.EqualTo("issuedBy"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), null, MakeStagingObservation(), MakeFinalObservation()));
            Assert.That(ex2.ParamName, Is.EqualTo("operation"));

            ArgumentNullException ex3 = Assert.Throws<ArgumentNullException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), operation, null, MakeFinalObservation()));
            Assert.That(ex3.ParamName, Is.EqualTo("staging"));

            ArgumentNullException ex4 = Assert.Throws<ArgumentNullException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), operation, MakeStagingObservation(), null));
            Assert.That(ex4.ParamName, Is.EqualTo("final"));
            owner.Dispose();
        }

        [Test]
        public void Snapshot_InvalidOperation_Rejected()
        {
            CaptureRunInitializationRecoveryInspectionOperation operation = (CaptureRunInitializationRecoveryInspectionOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), operation, MakeStagingObservation(), MakeFinalObservation()));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Snapshot_RoleSwap_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), operation, MakeFinalObservation(), MakeStagingObservation()));

            Assert.That(ex.ParamName, Is.EqualTo("staging"));
            owner.Dispose();
        }

        [Test]
        public void Snapshot_HoldsByReference_And_IsValid()
        {
            FakeInspector inspector = new FakeInspector();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRootObservation staging = MakeStagingObservation();
            CaptureRunInitializationRootObservation final = MakeFinalObservation();

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = new CaptureRunInitializationRecoveryInspectionSnapshot(inspector, operation, staging, final);

            Assert.That(snapshot.IssuedBy, Is.SameAs(inspector));
            Assert.That(snapshot.Operation, Is.SameAs(operation));
            Assert.That(snapshot.Staging, Is.SameAs(staging));
            Assert.That(snapshot.Final, Is.SameAs(final));
            Assert.That(snapshot.IsValid, Is.True);
            owner.Dispose();
        }

        [Test]
        public void Snapshot_Uninitialized_IsInvalid()
        {
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = (CaptureRunInitializationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryInspectionSnapshot));

            Assert.That(snapshot.IsValid, Is.False);
        }

        [Test]
        public void Snapshot_ForgedInconsistentStaging_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), operation, MakeForgedInconsistentStaging(), MakeFinalObservation()));

            Assert.That(ex.ParamName, Is.EqualTo("staging"));
            owner.Dispose();
        }

        [Test]
        public void Snapshot_IsValid_False_WhenForgedObservationInconsistent()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = (CaptureRunInitializationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryInspectionSnapshot));
            SetField(snapshot, "_issuedBy", new FakeInspector());
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_staging", MakeForgedInconsistentStaging());
            SetField(snapshot, "_final", MakeFinalObservation());

            Assert.That(snapshot.IsValid, Is.False);
            owner.Dispose();
        }

        [Test]
        public void Snapshot_Fields_AreFourReadonly()
        {
            Type type = typeof(CaptureRunInitializationRecoveryInspectionSnapshot);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(4));

            int inspectorFields = 0;
            int operationFields = 0;
            int observationFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(ICaptureRunInitializationRecoveryInspector)) inspectorFields++;
                else if (field.FieldType == typeof(CaptureRunInitializationRecoveryInspectionOperation)) operationFields++;
                else if (field.FieldType == typeof(CaptureRunInitializationRootObservation)) observationFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(inspectorFields, Is.EqualTo(1));
            Assert.That(operationFields, Is.EqualTo(1));
            Assert.That(observationFields, Is.EqualTo(2));
        }

        [Test]
        public void Snapshot_DoesNotDisposeOwner()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, disposeLog, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = new CaptureRunInitializationRecoveryInspectionSnapshot(
                new FakeInspector(), operation, MakeStagingObservation(), MakeFinalObservation());

            Assert.That(disposeLog, Is.Empty, "The snapshot must not dispose the owner.");
            Assert.That(snapshot.Operation.LockIdentityEvidence, Is.SameAs(identity));
            owner.Dispose();
        }

        // ---- Inspector boundary ----

        [Test]
        public void FakeInspector_ReturnsSnapshotForSameOperation()
        {
            FakeInspector inspector = new FakeInspector();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = inspector.Inspect(operation);

            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot.IssuedBy, Is.SameAs(inspector));
            Assert.That(snapshot.Operation, Is.SameAs(operation));
            Assert.That(inspector.LastOperation, Is.SameAs(operation));
            Assert.That(inspector.CallCount, Is.EqualTo(1));
            owner.Dispose();
        }

        [Test]
        public void FakeInspector_Exceptions_NotTransformedOrRetried()
        {
            FakeInspector inspector = new FakeInspector { ExceptionToThrow = new IOException("boom") };
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner;
            CaptureRunLockIdentityEvidence identity = MakeIdentity(layout, null, out owner);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);

            IOException ex = Assert.Throws<IOException>(() => inspector.Inspect(operation));

            Assert.That(ex.Message, Is.EqualTo("boom"));
            Assert.That(inspector.CallCount, Is.EqualTo(1));
            Assert.That(inspector.LastOperation, Is.SameAs(operation));
            owner.Dispose();
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter_Sealed_NotDisposable_NotUnityObject()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunInitializationRootObservation),
                typeof(CaptureRunInitializationRecoveryInspectionOperation),
                typeof(CaptureRunInitializationRecoveryInspectionSnapshot)
            })
            {
                Assert.That(type.IsPublic, Is.False, type.Name);
                Assert.That(type.IsSealed, Is.True, type.Name);
                Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.Name);
                Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False, type.Name);

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
                }
            }

            Type inspectorType = typeof(ICaptureRunInitializationRecoveryInspector);
            Assert.That(inspectorType.IsInterface, Is.True);
            Assert.That(inspectorType.IsPublic, Is.False);
            Assert.That(inspectorType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Has.Length.EqualTo(1));
        }

        [Test]
        public void NoMutableStaticState()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunInitializationRootObservation),
                typeof(CaptureRunInitializationRecoveryInspectionOperation),
                typeof(CaptureRunInitializationRecoveryInspectionSnapshot)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
                }
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerObservationStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRootObservation.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryInspectionOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryInspectionSnapshot.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunInitializationRecoveryInspector.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Trace"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }
        }
    }
}
