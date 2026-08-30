using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunPublicationRecoveryInspectionTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string OtherInitId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlanTemporary => CaptureRunPublicationDocumentKind.PublicationPlanTemporary;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationDocumentObservationStatus DocLimitExceeded => CaptureRunPublicationDocumentObservationStatus.LimitExceeded;

        // ---- Helpers ----

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

        private static CaptureRunMarkerBinding MakeBinding(CaptureRunRootLayout layout)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        private static CaptureRunInitializationRootObservation MakeObservation(
            CaptureRunRootRole role,
            bool rootExists,
            CaptureRunMarkerObservationStatus initStatus,
            CaptureRunInitializationMarker initMarker,
            CaptureRunMarkerObservationStatus readyStatus,
            CaptureRunReadyMarker readyMarker,
            bool hasNonMarker = false,
            bool hasUnknown = false,
            bool hasInitTmp = false,
            bool hasReadyTmp = false)
        {
            return new CaptureRunInitializationRootObservation(
                role,
                rootExists,
                hasInitTmp,
                initStatus,
                initMarker,
                hasReadyTmp,
                readyStatus,
                readyMarker,
                hasNonMarker,
                hasUnknown,
                false);
        }

        private static CaptureRunInitializationRootObservation MakeAbsent(CaptureRunRootRole role)
        {
            return MakeObservation(role, false, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(CaptureRunRootRole role, CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CapturePublicationPlanEntry MakeEntry(long captureFrameId)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new CapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                16,
                32,
                StagingHash,
                StagingHash);
        }

        private static CapturePublicationPlan MakePlan(long testRunId = 1, string initId = InitId)
        {
            return new CapturePublicationPlan(testRunId, initId, StagingHash, new[] { MakeEntry(10) });
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            CapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockLease lease)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_lockLease", lease);
            return outcome;
        }

        private static CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(List<string> disposeLog = null)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, lease, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, lease);
        }

        private static CaptureRunInitializationOpenOutcome MakeRunRootCollisionOutcome(List<string> disposeLog = null)
        {
            CaptureRunRootLayout layout = MakeLayout();

            FakeInspector inspector = new FakeInspector(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final));
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, lease, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, lease);
        }

        private static CaptureRunPublicationRecoveryInspectionOperation MakeOperation(
            int maximumPlanBytes = 16,
            int maximumEntryCount = 4,
            int maximumPathBytes = 64)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(),
                maximumPlanBytes,
                maximumEntryCount,
                maximumPathBytes);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeValidSnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation)
        {
            return MakeSnapshot(issuedBy, operation);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeSnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            CaptureRunPublicationFramesObservationStatus finalFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            bool stagingHasUnexpectedEntries = false,
            bool finalHasUnexpectedEntries = false,
            bool stagingRootEntryLimitExceeded = false,
            bool finalRootEntryLimitExceeded = false)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                stagingFramesStatus,
                finalFramesStatus,
                stagingHasUnexpectedEntries,
                finalHasUnexpectedEntries,
                stagingRootEntryLimitExceeded,
                finalRootEntryLimitExceeded);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot ForgeSnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null)
        {
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = (CaptureRunPublicationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionSnapshot));
            SetField(snapshot, "_issuedBy", issuedBy);
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_publicationPlanTemporary", publicationPlanTemporary ?? MakeDoc(PublicationPlanTemporary, DocAbsent));
            SetField(snapshot, "_publicationPlan", publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent));
            SetField(snapshot, "_captureIndexTemporary", captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent));
            SetField(snapshot, "_captureIndex", captureIndex ?? MakeDoc(CaptureIndex, DocAbsent));
            SetField(snapshot, "_stagingFramesStatus", CaptureRunPublicationFramesObservationStatus.Directory);
            SetField(snapshot, "_finalFramesStatus", CaptureRunPublicationFramesObservationStatus.Directory);
            return snapshot;
        }

        private static void AssertEnumContract(Type type, string[] expectedNames)
        {
            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetNames(type), Is.EqualTo(expectedNames));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(expectedNames.Length));
            for (int i = 0; i < expectedNames.Length; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationRecoveryInspectionTests).Assembly.Location);
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

        // ---- Fakes ----

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

            public void Dispose()
            {
                _disposeLog?.Add(LockPath);
            }
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            public FakeInspector(CaptureRunInitializationRootObservation staging, CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakePublicationInspector : ICaptureRunPublicationRecoveryInspector
        {
            public int InspectCount { get; private set; }

            public CaptureRunPublicationRecoveryInspectionOperation LastOperation { get; private set; }

            public CaptureRunPublicationRecoveryInspectionSnapshot SnapshotToReturn { get; set; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunPublicationRecoveryInspectionSnapshot Inspect(CaptureRunPublicationRecoveryInspectionOperation operation)
            {
                InspectCount++;
                LastOperation = operation;
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return SnapshotToReturn;
            }
        }

        // ---- Enums ----

        [Test]
        public void Enum_Contracts()
        {
            AssertEnumContract(typeof(CaptureRunPublicationDocumentKind),
                new[] { "None", "PublicationPlanTemporary", "PublicationPlan", "CaptureIndexTemporary", "CaptureIndex" });
            AssertEnumContract(typeof(CaptureRunPublicationDocumentObservationStatus),
                new[] { "Absent", "Canonical", "Invalid", "LimitExceeded" });
            AssertEnumContract(typeof(CaptureRunPublicationFramesObservationStatus),
                new[] { "Absent", "Directory", "Invalid" });
        }

        // ---- Document observation ----

        [Test]
        public void Observation_AllNormalCombinations()
        {
            CapturePublicationPlan plan = MakePlan();

            foreach (CaptureRunPublicationDocumentKind kind in new[]
            {
                PublicationPlanTemporary,
                PublicationPlan,
                CaptureIndexTemporary,
                CaptureIndex
            })
            {
                Assert.That(MakeDoc(kind, DocAbsent, 0, null).IsValid, Is.True, kind + " absent");
                Assert.That(MakeDoc(kind, DocCanonical, 1, plan).IsValid, Is.True, kind + " canonical");
                Assert.That(MakeDoc(kind, DocInvalid, 0, null).IsValid, Is.True, kind + " invalid zero");
                Assert.That(MakeDoc(kind, DocInvalid, 5, null).IsValid, Is.True, kind + " invalid positive");
                Assert.That(MakeDoc(kind, DocLimitExceeded, 1, null).IsValid, Is.True, kind + " limit exceeded");
            }
        }

        [Test]
        public void Observation_UndefinedKind_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeDoc(CaptureRunPublicationDocumentKind.None, DocAbsent));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeDoc((CaptureRunPublicationDocumentKind)99, DocAbsent));
        }

        [Test]
        public void Observation_UndefinedStatus_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeDoc(PublicationPlan, (CaptureRunPublicationDocumentObservationStatus)99));
        }

        [Test]
        public void Observation_Contradictions_Rejected()
        {
            CapturePublicationPlan plan = MakePlan();

            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocAbsent, 1, null));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocAbsent, 0, plan));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocCanonical, 0, plan));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocCanonical, 1, null));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocInvalid, -1, null));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocInvalid, 0, plan));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocLimitExceeded, 0, null));
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocLimitExceeded, 1, plan));
        }

        [Test]
        public void Observation_Uninitialized_IsInvalid()
        {
            CaptureRunPublicationDocumentObservation observation = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationDocumentObservation));
            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void Observation_ForgedInconsistent_IsValidFalse_NoException()
        {
            CaptureRunPublicationDocumentObservation canonicalNullPlan = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationDocumentObservation));
            SetField(canonicalNullPlan, "_kind", PublicationPlan);
            SetField(canonicalNullPlan, "_status", DocCanonical);
            SetField(canonicalNullPlan, "_probedByteCount", 1);
            Assert.That(canonicalNullPlan.IsValid, Is.False);

            CaptureRunPublicationDocumentObservation absentNonzero = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationDocumentObservation));
            SetField(absentNonzero, "_kind", PublicationPlan);
            SetField(absentNonzero, "_status", DocAbsent);
            SetField(absentNonzero, "_probedByteCount", 5);
            Assert.That(absentNonzero.IsValid, Is.False);

            CaptureRunPublicationDocumentObservation undefinedKind = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationDocumentObservation));
            SetField(undefinedKind, "_kind", CaptureRunPublicationDocumentKind.None);
            SetField(undefinedKind, "_status", DocAbsent);
            Assert.That(undefinedKind.IsValid, Is.False);
        }

        [Test]
        public void PlanAndEntry_Uninitialized_IsInvalid()
        {
            CapturePublicationPlan plan = (CapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(CapturePublicationPlan));
            CapturePublicationPlanEntry entry = (CapturePublicationPlanEntry)FormatterServices.GetUninitializedObject(typeof(CapturePublicationPlanEntry));

            Assert.That(plan.IsValid, Is.False);
            Assert.That(entry.IsValid, Is.False);
        }

        [Test]
        public void Observation_CanonicalInvalidPlan_Rejected()
        {
            CapturePublicationPlan plan = (CapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(CapturePublicationPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocCanonical, 1, plan));
            Assert.That(ex.ParamName, Is.EqualTo("plan"));
        }

        // ---- Inspection operation ----

        [Test]
        public void Operation_NullOutcome_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationRecoveryInspectionOperation(null, 16, 4, 64));
            Assert.That(ex.ParamName, Is.EqualTo("openOutcome"));
        }

        [Test]
        public void Operation_DisposedOutcome_Rejected()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();
            outcome.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64));
            Assert.That(ex.ParamName, Is.EqualTo("openOutcome"));
        }

        [Test]
        public void Operation_RunRootCollisionOutcome_Rejected()
        {
            CaptureRunInitializationOpenOutcome outcome = MakeRunRootCollisionOutcome();
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.RunRootCollision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64));
            Assert.That(ex.ParamName, Is.EqualTo("openOutcome"));
        }

        [Test]
        public void Operation_PublicationOutcome_Accepted()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64);

            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void Operation_LimitBoundaries_Accepted()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();

            CaptureRunPublicationRecoveryInspectionOperation min = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1, 0, 1);
            Assert.That(min.IsValid, Is.True);

            CaptureRunPublicationRecoveryInspectionOperation max = new CaptureRunPublicationRecoveryInspectionOperation(
                outcome,
                CaptureRunPublicationRecoveryInspectionOperation.MaximumAllowedPlanBytes,
                CaptureRunPublicationRecoveryInspectionOperation.MaximumAllowedEntryCount,
                CaptureRunPublicationRecoveryInspectionOperation.MaximumAllowedPathBytes);
            Assert.That(max.IsValid, Is.True);
        }

        [Test]
        public void Operation_LimitOutOfRange_Rejected_WithParamName()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();

            foreach (int badPlanBytes in new[] { 0, -1, CaptureRunPublicationRecoveryInspectionOperation.MaximumAllowedPlanBytes + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunPublicationRecoveryInspectionOperation(outcome, badPlanBytes, 4, 64));
                Assert.That(ex.ParamName, Is.EqualTo("maximumPlanBytes"));
            }

            foreach (int badEntryCount in new[] { -1, CaptureRunPublicationRecoveryInspectionOperation.MaximumAllowedEntryCount + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, badEntryCount, 64));
                Assert.That(ex.ParamName, Is.EqualTo("maximumEntryCount"));
            }

            foreach (int badPathBytes in new[] { 0, -1, CaptureRunPublicationRecoveryInspectionOperation.MaximumAllowedPathBytes + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, badPathBytes));
                Assert.That(ex.ParamName, Is.EqualTo("maximumPathBytes"));
            }
        }

        [Test]
        public void Operation_ForwardsAndHoldsByReference()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64);

            Assert.That(operation.OpenOutcome, Is.SameAs(outcome));
            Assert.That(operation.RootLayout, Is.SameAs(outcome.RootLayout));
            Assert.That(operation.LockLease, Is.SameAs(outcome.OrchestrationResult.LockLease));
            Assert.That(operation.TestRunId, Is.EqualTo(outcome.TestRunId));
            Assert.That(operation.RunInitializationId, Is.EqualTo(outcome.RunInitializationId));
            Assert.That(operation.MaximumRootEntryCount, Is.EqualTo(4));
            Assert.That(operation.RootEntryProbeCount, Is.EqualTo(5));
            Assert.That(operation.PublicationPaths.RootLayout, Is.SameAs(outcome.RootLayout));
            Assert.That(operation.MaximumPlanBytes, Is.EqualTo(16));
            Assert.That(operation.MaximumEntryCount, Is.EqualTo(4));
            Assert.That(operation.MaximumPathBytes, Is.EqualTo(64));
        }

        [Test]
        public void Operation_DoesNotDisposeLease()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(disposeLog, Is.Empty, "The operation must not dispose the lease.");
        }

        [Test]
        public void Operation_IsValid_False_AfterOutcomeDispose()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64);

            Assert.That(operation.IsValid, Is.True);

            outcome.Dispose();

            Assert.That(operation.IsValid, Is.False);
            Assert.That(disposeLog, Is.Not.Empty);
        }

        [Test]
        public void Operation_Uninitialized_IsInvalid()
        {
            CaptureRunPublicationRecoveryInspectionOperation operation = (CaptureRunPublicationRecoveryInspectionOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionOperation));

            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Operation_Fields_AreFiveReadonly()
        {
            Type type = typeof(CaptureRunPublicationRecoveryInspectionOperation);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(5));

            int outcomeFields = 0;
            int pathSetFields = 0;
            int intFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationOpenOutcome)) outcomeFields++;
                else if (field.FieldType == typeof(CaptureRunPublicationPathSet)) pathSetFields++;
                else if (field.FieldType == typeof(int)) intFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(outcomeFields, Is.EqualTo(1));
            Assert.That(pathSetFields, Is.EqualTo(1));
            Assert.That(intFields, Is.EqualTo(3));
        }

        // ---- Snapshot ----

        [Test]
        public void Snapshot_NullArgs_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            Assert.That(Assert.Throws<ArgumentNullException>(() => MakeSnapshot(null, operation)).ParamName, Is.EqualTo("issuedBy"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => MakeSnapshot(inspector, null)).ParamName, Is.EqualTo("operation"));

            Assert.Throws<ArgumentNullException>(() => new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector, operation, null, MakeDoc(PublicationPlan, DocAbsent), MakeDoc(CaptureIndexTemporary, DocAbsent), MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory, CaptureRunPublicationFramesObservationStatus.Directory, false, false, false, false));
            Assert.Throws<ArgumentNullException>(() => new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector, operation, MakeDoc(PublicationPlanTemporary, DocAbsent), null, MakeDoc(CaptureIndexTemporary, DocAbsent), MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory, CaptureRunPublicationFramesObservationStatus.Directory, false, false, false, false));
            Assert.Throws<ArgumentNullException>(() => new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector, operation, MakeDoc(PublicationPlanTemporary, DocAbsent), MakeDoc(PublicationPlan, DocAbsent), null, MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory, CaptureRunPublicationFramesObservationStatus.Directory, false, false, false, false));
            Assert.Throws<ArgumentNullException>(() => new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector, operation, MakeDoc(PublicationPlanTemporary, DocAbsent), MakeDoc(PublicationPlan, DocAbsent), MakeDoc(CaptureIndexTemporary, DocAbsent), null,
                CaptureRunPublicationFramesObservationStatus.Directory, CaptureRunPublicationFramesObservationStatus.Directory, false, false, false, false));
        }

        [Test]
        public void Snapshot_InvalidOperation_Rejected()
        {
            CaptureRunPublicationRecoveryInspectionOperation operation = (CaptureRunPublicationRecoveryInspectionOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => MakeSnapshot(new FakePublicationInspector(), operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Snapshot_KindOrderMismatch_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeSnapshot(
                inspector,
                operation,
                publicationPlan: MakeDoc(CaptureIndex, DocAbsent)));

            Assert.That(ex.ParamName, Is.EqualTo("publicationPlan"));
        }

        [Test]
        public void Snapshot_FramesUndefined_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeSnapshot(
                inspector,
                operation,
                stagingFramesStatus: (CaptureRunPublicationFramesObservationStatus)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeSnapshot(
                inspector,
                operation,
                finalFramesStatus: (CaptureRunPublicationFramesObservationStatus)99));
        }

        [Test]
        public void Snapshot_HoldsByReference_And_IsValid()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CaptureRunPublicationDocumentObservation plan = MakeDoc(PublicationPlan, DocCanonical, 10, MakePlan());

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(inspector, operation, publicationPlan: plan);

            Assert.That(snapshot.IssuedBy, Is.SameAs(inspector));
            Assert.That(snapshot.Operation, Is.SameAs(operation));
            Assert.That(snapshot.PublicationPlan, Is.SameAs(plan));
            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void Snapshot_CanonicalByteUpperBound()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(maximumPlanBytes: 16);

            CaptureRunPublicationDocumentObservation ok = MakeDoc(PublicationPlan, DocCanonical, 16, MakePlan());
            Assert.That(MakeSnapshot(inspector, operation, publicationPlan: ok).IsValid, Is.True);

            CaptureRunPublicationDocumentObservation over = MakeDoc(PublicationPlan, DocCanonical, 17, MakePlan());
            Assert.Throws<ArgumentException>(() => MakeSnapshot(inspector, operation, publicationPlan: over));
        }

        [Test]
        public void Snapshot_InvalidByteUpperBound()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(maximumPlanBytes: 16);

            CaptureRunPublicationDocumentObservation ok = MakeDoc(PublicationPlan, DocInvalid, 16, null);
            Assert.That(MakeSnapshot(inspector, operation, publicationPlan: ok).IsValid, Is.True);

            CaptureRunPublicationDocumentObservation over = MakeDoc(PublicationPlan, DocInvalid, 17, null);
            Assert.Throws<ArgumentException>(() => MakeSnapshot(inspector, operation, publicationPlan: over));
        }

        [Test]
        public void Snapshot_LimitExceeded_ExactlyMaxPlusOne()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(maximumPlanBytes: 16);

            CaptureRunPublicationDocumentObservation ok = MakeDoc(PublicationPlan, DocLimitExceeded, 17, null);
            Assert.That(MakeSnapshot(inspector, operation, publicationPlan: ok).IsValid, Is.True);

            Assert.Throws<ArgumentException>(() => MakeSnapshot(
                inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocLimitExceeded, 16, null)));
            Assert.Throws<ArgumentException>(() => MakeSnapshot(
                inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocLimitExceeded, 18, null)));
        }

        [Test]
        public void Snapshot_RootEntryFlags_AcceptedAsFacts()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector,
                operation,
                MakeDoc(PublicationPlanTemporary, DocAbsent),
                MakeDoc(PublicationPlan, DocAbsent),
                MakeDoc(CaptureIndexTemporary, DocAbsent),
                MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Invalid,
                CaptureRunPublicationFramesObservationStatus.Invalid,
                true,
                true,
                true,
                true);

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.StagingHasUnexpectedEntries, Is.True);
            Assert.That(snapshot.FinalHasUnexpectedEntries, Is.True);
            Assert.That(snapshot.StagingRootEntryLimitExceeded, Is.True);
            Assert.That(snapshot.FinalRootEntryLimitExceeded, Is.True);
        }

        [Test]
        public void Snapshot_CanonicalDifferentTestRunId_Accepted()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CaptureRunPublicationDocumentObservation plan = MakeDoc(PublicationPlan, DocCanonical, 10, MakePlan(testRunId: 2));
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(inspector, operation, publicationPlan: plan);

            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void Snapshot_CanonicalDifferentInitId_Accepted()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CaptureRunPublicationDocumentObservation plan = MakeDoc(PublicationPlan, DocCanonical, 10, MakePlan(initId: OtherInitId));
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(inspector, operation, publicationPlan: plan);

            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void Snapshot_PlanIndexMismatch_NotClassified()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CaptureRunPublicationDocumentObservation plan = MakeDoc(PublicationPlan, DocCanonical, 10, MakePlan(testRunId: 1));
            CaptureRunPublicationDocumentObservation index = MakeDoc(CaptureIndex, DocCanonical, 10, MakePlan(testRunId: 2, initId: OtherInitId));

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(inspector, operation, publicationPlan: plan, captureIndex: index);

            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void Snapshot_Uninitialized_IsInvalid()
        {
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = (CaptureRunPublicationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionSnapshot));

            Assert.That(snapshot.IsValid, Is.False);
        }

        [Test]
        public void Snapshot_ForgedInconsistentObservation_IsValidFalse_NoException()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CaptureRunPublicationDocumentObservation forgedDoc = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationDocumentObservation));
            SetField(forgedDoc, "_kind", PublicationPlan);
            SetField(forgedDoc, "_status", DocCanonical);
            SetField(forgedDoc, "_probedByteCount", 1);

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = (CaptureRunPublicationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionSnapshot));
            SetField(snapshot, "_issuedBy", inspector);
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_publicationPlanTemporary", MakeDoc(PublicationPlanTemporary, DocAbsent));
            SetField(snapshot, "_publicationPlan", forgedDoc);
            SetField(snapshot, "_captureIndexTemporary", MakeDoc(CaptureIndexTemporary, DocAbsent));
            SetField(snapshot, "_captureIndex", MakeDoc(CaptureIndex, DocAbsent));
            SetField(snapshot, "_stagingFramesStatus", CaptureRunPublicationFramesObservationStatus.Directory);
            SetField(snapshot, "_finalFramesStatus", CaptureRunPublicationFramesObservationStatus.Directory);

            Assert.That(snapshot.IsValid, Is.False);
        }

        [Test]
        public void Snapshot_IsValid_False_WhenOperationDisposedOutcome()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 16, 4, 64);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeValidSnapshot(inspector, operation);

            Assert.That(snapshot.IsValid, Is.True);

            outcome.Dispose();

            Assert.That(snapshot.IsValid, Is.False);
        }

        // ---- Snapshot plan limits ----

        [Test]
        public void Snapshot_UninitializedPlan_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CapturePublicationPlan plan = (CapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(CapturePublicationPlan));
            Assert.That(plan.IsValid, Is.False);

            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocCanonical, 10, plan));
        }

        [Test]
        public void Snapshot_EntryArrayCorruption_Rejected()
        {
            CapturePublicationPlan plan = (CapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(CapturePublicationPlan));
            SetField(plan, "_testRunId", 1L);
            SetField(plan, "_runInitializationId", InitId);
            SetField(plan, "_runManifestContentSha256", StagingHash);
            SetField(plan, "_entries", null);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocCanonical, 10, plan));
        }

        [Test]
        public void Snapshot_EntryCountLimitExceeded_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(maximumEntryCount: 2);

            CapturePublicationPlan plan = new CapturePublicationPlan(1, InitId, StagingHash, new[] { MakeEntry(1), MakeEntry(2), MakeEntry(3) });
            Assert.That(plan.IsValid, Is.True);

            CaptureRunPublicationDocumentObservation observation = MakeDoc(PublicationPlan, DocCanonical, 10, plan);
            Assert.That(observation.IsValid, Is.True);

            Assert.Throws<ArgumentException>(() => MakeSnapshot(inspector, operation, publicationPlan: observation));
            Assert.That(ForgeSnapshot(inspector, operation, publicationPlan: observation).IsValid, Is.False);
        }

        [Test]
        public void Snapshot_PlanPathByteLimit_Exceeded_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(maximumPathBytes: 16);

            CapturePublicationPlan plan = new CapturePublicationPlan(1, InitId, StagingHash, new[] { MakeEntry(999999999999999999L) });
            Assert.That(plan.IsValid, Is.True);

            CaptureRunPublicationDocumentObservation observation = MakeDoc(PublicationPlan, DocCanonical, 100, plan);
            Assert.That(observation.IsValid, Is.True);

            Assert.Throws<ArgumentException>(() => MakeSnapshot(inspector, operation, publicationPlan: observation));
            Assert.That(ForgeSnapshot(inspector, operation, publicationPlan: observation).IsValid, Is.False);
        }

        [Test]
        public void Snapshot_EachPathTypeOverLimit_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(maximumPathBytes: 16);

            string[] pathFields =
            {
                "_pngStagingRelativePath",
                "_sidecarStagingRelativePath",
                "_pngFinalRelativePath",
                "_sidecarFinalRelativePath"
            };

            foreach (string pathField in pathFields)
            {
                CapturePublicationPlanEntry entry = MakeEntry(10);
                SetField(entry, pathField, "frames/very-long-path-exceeding-limit/" + pathField + ".png");
                Assert.That(entry.IsValid, Is.False, pathField);

                CapturePublicationPlan plan = (CapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(CapturePublicationPlan));
                SetField(plan, "_testRunId", 1L);
                SetField(plan, "_runInitializationId", InitId);
                SetField(plan, "_runManifestContentSha256", StagingHash);
                SetField(plan, "_entries", new[] { entry });
                Assert.That(plan.IsValid, Is.False, pathField);

                Assert.Throws<ArgumentException>(() => MakeDoc(PublicationPlan, DocCanonical, 100, plan), pathField);

                CaptureRunPublicationDocumentObservation forgedObservation = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationDocumentObservation));
                SetField(forgedObservation, "_kind", PublicationPlan);
                SetField(forgedObservation, "_status", DocCanonical);
                SetField(forgedObservation, "_probedByteCount", 100);
                SetField(forgedObservation, "_plan", plan);
                Assert.That(forgedObservation.IsValid, Is.False, pathField);

                Assert.That(ForgeSnapshot(inspector, operation, publicationPlan: forgedObservation).IsValid, Is.False, pathField);
            }
        }

        // ---- Inspector interface ----

        [Test]
        public void Inspector_InterfaceSignature()
        {
            Type type = typeof(ICaptureRunPublicationRecoveryInspector);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);

            MethodInfo[] methods = type.GetMethods();
            Assert.That(methods.Length, Is.EqualTo(1));

            MethodInfo inspect = methods[0];
            Assert.That(inspect.Name, Is.EqualTo("Inspect"));
            Assert.That(inspect.ReturnType, Is.EqualTo(typeof(CaptureRunPublicationRecoveryInspectionSnapshot)));

            ParameterInfo[] parameters = inspect.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationRecoveryInspectionOperation)));
            Assert.That(parameters[0].Name, Is.EqualTo("operation"));
        }

        [Test]
        public void Inspector_XmlContract()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationRecoveryInspector.cs"));

            Assert.That(source, Does.Contain("ArgumentNullException"));
            Assert.That(source, Does.Contain("ArgumentException"));
            Assert.That(source, Does.Contain("DeserializeCanonical"));
            Assert.That(source, Does.Contain("retry"));
            Assert.That(source, Does.Contain("RootEntryProbeCount"));
        }

        // ---- Shape ----

        [Test]
        public void Shape_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            foreach (Type type in ContractTypes())
            {
                Assert.That(type.IsPublic, Is.False, type.Name);
                Assert.That(type.IsSealed, Is.True, type.Name);
                Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.Name);

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(prop.CanWrite, Is.False, type.Name + "." + prop.Name + " must be get-only.");
                }
            }
        }

        [Test]
        public void Shape_FieldsReadonly_NoArraysOrCollections()
        {
            foreach (Type type in ContractTypes())
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " must be readonly.");
                    Assert.That(typeof(System.Collections.ICollection).IsAssignableFrom(field.FieldType), Is.False,
                        type.Name + "." + field.Name + " must not be an array or mutable collection.");
                }
            }
        }

        [Test]
        public void Shape_NoMutableStaticState()
        {
            foreach (Type type in ContractTypes())
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, type.Name + "." + field.Name + " must be readonly or const.");
                }
            }
        }

        [Test]
        public void Source_NoFileDirectoryFileStreamPInvoke()
        {
            string[] files =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationDocumentKind.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationDocumentObservationStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationFramesObservationStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationDocumentObservation.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationRecoveryInspectionOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationRecoveryInspector.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationRecoveryInspectionSnapshot.cs"
            };

            foreach (string file in files)
            {
                string source = File.ReadAllText(LocateSource(file));
                Assert.That(source, Does.Not.Contain("File."), file);
                Assert.That(source, Does.Not.Contain("Directory."), file);
                Assert.That(source, Does.Not.Contain("FileStream"), file);
                Assert.That(source, Does.Not.Contain("DllImport"), file);
            }
        }

        private static Type[] ContractTypes()
        {
            return new[]
            {
                typeof(CaptureRunPublicationDocumentObservation),
                typeof(CaptureRunPublicationRecoveryInspectionOperation),
                typeof(CaptureRunPublicationRecoveryInspectionSnapshot)
            };
        }
    }
}
