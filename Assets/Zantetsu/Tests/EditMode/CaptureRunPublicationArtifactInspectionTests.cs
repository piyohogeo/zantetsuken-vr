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
    public class CaptureRunPublicationArtifactInspectionTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationEvidenceStatus EvInvalid => CaptureRunPublicationEvidenceStatus.Invalid;

        private static CaptureRunPublicationEvidenceStatus EvLimitExceeded => CaptureRunPublicationEvidenceStatus.LimitExceeded;

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
                role, rootExists, hasInitTmp, initStatus, initMarker,
                hasReadyTmp, readyStatus, readyMarker, hasNonMarker, hasUnknown, false);
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

        private static CapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = 16,
            long sidecarByteLength = 32,
            string pngHash = null,
            string sidecarHash = null)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new CapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                pngByteLength,
                sidecarByteLength,
                pngHash ?? StagingHash,
                sidecarHash ?? StagingHash);
        }

        private static CapturePublicationPlan MakePlan(
            long testRunId = 1,
            string initId = null,
            string manifestHash = null,
            CapturePublicationPlanEntry[] entries = null)
        {
            return new CapturePublicationPlan(
                testRunId,
                initId ?? InitId,
                manifestHash ?? StagingHash,
                entries ?? new[] { MakeEntry(10) });
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

        private static CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryOperation(
            int maximumPlanBytes = 1000,
            int maximumEntryCount = 4,
            int maximumPathBytes = 64)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(),
                maximumPlanBytes,
                maximumEntryCount,
                maximumPathBytes);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeRecoverySnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private static CaptureRunPublicationRecoveryDecision MakeDecision(
            CapturePublicationPlan plan = null,
            bool indexAuthoritative = false)
        {
            plan = plan ?? MakePlan();
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private static CaptureRunPublicationArtifactInspectionOperation MakeArtifactOperation(
            CaptureRunPublicationRecoveryDecision decision = null,
            long maximumPngByteCount = 1000)
        {
            return new CaptureRunPublicationArtifactInspectionOperation(decision ?? MakeDecision(), maximumPngByteCount);
        }

        private static CaptureRunPublicationArtifactEntryObservation MakeEntryObservation(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingPngCount = 0,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingSidecarCount = 0,
            CaptureRunPublicationEvidenceStatus finalPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalPngCount = 0,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalSidecarCount = 0)
        {
            return new CaptureRunPublicationArtifactEntryObservation(
                operation,
                artifactPaths,
                stagingPngStatus,
                stagingPngCount,
                stagingSidecarStatus,
                stagingSidecarCount,
                finalPngStatus,
                finalPngCount,
                finalSidecarStatus,
                finalSidecarCount);
        }

        private static CaptureRunPublicationArtifactInspectionSnapshot MakeArtifactSnapshot(
            ICaptureRunPublicationArtifactInspector issuedBy,
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long traceCount = 0,
            CaptureRunPublicationArtifactEntryObservation[] entries = null)
        {
            if (entries == null)
            {
                entries = new CaptureRunPublicationArtifactEntryObservation[operation.EntryCount];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = MakeEntryObservation(operation, operation.GetArtifactPaths(i));
                }
            }

            return new CaptureRunPublicationArtifactInspectionSnapshot(issuedBy, operation, traceStatus, traceCount, entries);
        }

        private static void AssertStatusCombos(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactPathSet paths,
            long limit,
            Func<CaptureRunPublicationEvidenceStatus, long, CaptureRunPublicationArtifactEntryObservation> build)
        {
            Assert.That(build(EvAbsent, 0).IsValid, Is.True);
            Assert.That(build(EvMatchesExpected, limit).IsValid, Is.True);
            Assert.That(build(EvMismatch, 1).IsValid, Is.True);
            Assert.That(build(EvInvalid, 0).IsValid, Is.True);
            Assert.That(build(EvLimitExceeded, limit + 1).IsValid, Is.True);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactInspectionTests).Assembly.Location);
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
            public CaptureRunPublicationRecoveryInspectionSnapshot Inspect(CaptureRunPublicationRecoveryInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        private sealed class FakeArtifactInspector : ICaptureRunPublicationArtifactInspector
        {
            public int InspectCount { get; private set; }

            public CaptureRunPublicationArtifactInspectionOperation LastOperation { get; private set; }

            public CaptureRunPublicationArtifactInspectionSnapshot SnapshotToReturn { get; set; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
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

        // ---- Enum ----

        [Test]
        public void Enum_Contract()
        {
            AssertEnumContract(typeof(CaptureRunPublicationEvidenceStatus),
                new[] { "None", "Absent", "MatchesExpected", "Mismatch", "Invalid", "LimitExceeded" });
        }

        // ---- Operation ----

        [Test]
        public void Operation_NullDecision_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactInspectionOperation(null, 1000));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Operation_InvalidDecision_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = (CaptureRunPublicationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryDecision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactInspectionOperation(decision, 1000));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Operation_NoAuthoritativeDocument_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = MakeRecoveryOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeRecoverySnapshot(inspector, recoveryOperation);
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.NoAuthoritativeDocument));
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionOperation(decision, 1000));
        }

        [Test]
        public void Operation_RunRootCollision_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = MakeRecoveryOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector,
                recoveryOperation,
                MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                MakeDoc(PublicationPlan, DocAbsent),
                MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, true, false);
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.RunRootCollision));
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionOperation(decision, 1000));
        }

        [Test]
        public void Operation_MaxPngByteCountBoundaries()
        {
            CapturePublicationPlan tinyPlan = MakePlan(entries: new[] { MakeEntry(10, pngByteLength: 1) });
            CaptureRunPublicationRecoveryDecision tinyDecision = MakeDecision(tinyPlan);
            Assert.That(new CaptureRunPublicationArtifactInspectionOperation(tinyDecision, 1).IsValid, Is.True);

            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            Assert.That(new CaptureRunPublicationArtifactInspectionOperation(
                decision, CaptureRunPublicationArtifactInspectionOperation.MaximumAllowedPngByteCount).IsValid, Is.True);

            foreach (long bad in new[] { 0L, -1L, CaptureRunPublicationArtifactInspectionOperation.MaximumAllowedPngByteCount + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunPublicationArtifactInspectionOperation(decision, bad));
                Assert.That(ex.ParamName, Is.EqualTo("maximumPngByteCount"));
            }
        }

        [Test]
        public void Operation_EntryPngLengthExceedsMax_Rejected()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10, pngByteLength: 2000) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);

            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionOperation(decision, 1000));
        }

        [Test]
        public void Operation_EntrySidecarLengthExceedsMax_Rejected()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10, sidecarByteLength: 70000) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);

            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionOperation(decision, 1000));
        }

        [Test]
        public void Operation_ForwardsAndLeaseNotOwned()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            CapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeRecoverySnapshot(
                inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1234);

            Assert.That(operation.Decision, Is.SameAs(decision));
            Assert.That(operation.Plan, Is.SameAs(plan));
            Assert.That(operation.EntryCount, Is.EqualTo(1));
            Assert.That(operation.MaximumPngByteCount, Is.EqualTo(1234));
            Assert.That(operation.MaximumSidecarByteCount, Is.EqualTo(CaptureFramePngArtifactCodec.MaximumCanonicalByteCount));
            Assert.That(operation.MaximumTraceManifestByteCount, Is.EqualTo(TraceRunManifestCodec.MaximumCanonicalByteCount));
            Assert.That(operation.RootLayout, Is.SameAs(decision.RootLayout));
            Assert.That(operation.LockLease, Is.SameAs(recoveryOperation.LockLease));
            Assert.That(operation.TestRunId, Is.EqualTo(1));
            Assert.That(operation.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(operation.RunManifestContentSha256, Is.EqualTo(StagingHash));
            Assert.That(disposeLog, Is.Empty, "The operation must not dispose the lease.");
        }

        [Test]
        public void GetArtifactPaths_AllIndices_AndOutOfRange()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2), MakeEntry(3) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);

            Assert.That(operation.EntryCount, Is.EqualTo(3));
            Assert.That(operation.GetArtifactPaths(0).EntryIndex, Is.EqualTo(0));
            Assert.That(operation.GetArtifactPaths(1).EntryIndex, Is.EqualTo(1));
            Assert.That(operation.GetArtifactPaths(2).EntryIndex, Is.EqualTo(2));

            foreach (int bad in new[] { -1, 3, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => operation.GetArtifactPaths(bad));
                Assert.That(ex.ParamName, Is.EqualTo("index"));
            }
        }

        [Test]
        public void Operation_LargePlan_LinearConstruction()
        {
            // Building or validating one entry must never rescan the whole plan;
            // with 10000 entries a quadratic path would take minutes, so the
            // elapsed bound below pins the full-graph validation to a single
            // pass per boundary.
            const int entryCount = 10000;
            CapturePublicationPlanEntry[] entries = new CapturePublicationPlanEntry[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeEntry(i + 1);
            }

            CapturePublicationPlan plan = MakePlan(entries: entries);

            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(), 1000, entryCount, 64);
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = MakeRecoverySnapshot(
                inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1024);
            bool valid = operation.IsValid;
            stopwatch.Stop();

            Assert.That(valid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(entryCount));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)),
                "Constructing and validating the operation must not rescan the whole plan per entry.");
        }

        [Test]
        public void Operation_LeaseRelease_IsValidFalse()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeRecoverySnapshot(
                inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan()));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);

            Assert.That(operation.IsValid, Is.True);

            outcome.Dispose();

            Assert.That(operation.IsValid, Is.False);
        }

        // ---- Entry observation ----

        [Test]
        public void Observation_AllFileStatusCombinations()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths = operation.GetArtifactPaths(0);

            AssertStatusCombos(operation, paths, 16, (s, c) => MakeEntryObservation(operation, paths, stagingPngStatus: s, stagingPngCount: c));
            AssertStatusCombos(operation, paths, 32, (s, c) => MakeEntryObservation(operation, paths, stagingSidecarStatus: s, stagingSidecarCount: c));
            AssertStatusCombos(operation, paths, 16, (s, c) => MakeEntryObservation(operation, paths, finalPngStatus: s, finalPngCount: c));
            AssertStatusCombos(operation, paths, 32, (s, c) => MakeEntryObservation(operation, paths, finalSidecarStatus: s, finalSidecarCount: c));
        }

        [Test]
        public void Observation_NoneOrUndefinedStatus_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths = operation.GetArtifactPaths(0);

            Assert.Throws<ArgumentOutOfRangeException>(() => MakeEntryObservation(
                operation, paths, stagingPngStatus: CaptureRunPublicationEvidenceStatus.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeEntryObservation(
                operation, paths, stagingPngStatus: (CaptureRunPublicationEvidenceStatus)99));
        }

        [Test]
        public void Observation_StatusCountContradiction_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths = operation.GetArtifactPaths(0);

            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvAbsent, stagingPngCount: 1));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvMatchesExpected, stagingPngCount: 0));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvMatchesExpected, stagingPngCount: 15));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvMatchesExpected, stagingPngCount: 17));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvMismatch, stagingPngCount: 17));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvInvalid, stagingPngCount: -1));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvInvalid, stagingPngCount: 17));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvLimitExceeded, stagingPngCount: 16));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvLimitExceeded, stagingPngCount: 18));
        }

        [Test]
        public void Observation_LimitExceeded_ExactlyLimitPlusOne()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths = operation.GetArtifactPaths(0);

            Assert.That(MakeEntryObservation(operation, paths, stagingSidecarStatus: EvLimitExceeded, stagingSidecarCount: 33).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingSidecarStatus: EvLimitExceeded, stagingSidecarCount: 32));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingSidecarStatus: EvLimitExceeded, stagingSidecarCount: 34));
        }

        [Test]
        public void Observation_MatchesExpected_ExactlyExpectedLength()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths = operation.GetArtifactPaths(0);

            // PNG expected byte length is 16; sidecar expected byte length is 32.
            Assert.That(MakeEntryObservation(operation, paths, stagingPngStatus: EvMatchesExpected, stagingPngCount: 16).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvMatchesExpected, stagingPngCount: 15));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingPngStatus: EvMatchesExpected, stagingPngCount: 17));

            Assert.That(MakeEntryObservation(operation, paths, stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: 32).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: 31));
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: 33));

            Assert.That(MakeEntryObservation(operation, paths, finalPngStatus: EvMatchesExpected, finalPngCount: 16).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, finalPngStatus: EvMatchesExpected, finalPngCount: 15));

            Assert.That(MakeEntryObservation(operation, paths, finalSidecarStatus: EvMatchesExpected, finalSidecarCount: 32).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths, finalSidecarStatus: EvMatchesExpected, finalSidecarCount: 31));
        }

        [Test]
        public void Observation_ForeignDecisionPathSet_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);

            CaptureRunPublicationRecoveryDecision otherDecision = MakeDecision();
            CaptureRunPublicationArtifactPathSet foreign = new CaptureRunPublicationArtifactPathSet(otherDecision, 0);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, foreign));
            Assert.That(ex.ParamName, Is.EqualTo("artifactPaths"));
        }

        [Test]
        public void Observation_ForgedIndexMismatch_IsValidFalse()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, operation.GetArtifactPaths(0));
            Assert.That(observation.IsValid, Is.True);

            SetField(observation.ArtifactPaths, "_entryIndex", 1);
            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void Observation_ForgedStatusOrCount_IsValidFalse_NoException()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, paths);
            Assert.That(observation.IsValid, Is.True);

            SetField(observation, "_stagingPngStatus", CaptureRunPublicationEvidenceStatus.MatchesExpected);
            SetField(observation, "_stagingPngProbedByteCount", 0L);
            Assert.That(observation.IsValid, Is.False);

            CaptureRunPublicationArtifactEntryObservation forged = MakeEntryObservation(operation, paths);
            SetField(forged, "_stagingPngStatus", (CaptureRunPublicationEvidenceStatus)99);
            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void Observation_OtherIndexPathSetMissing_Invalid_NoException()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths0 = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, paths0);
            Assert.That(observation.IsValid, Is.True);

            // Forge: the other index's path set is missing, so the operation is
            // no longer fully valid even though entry 0 is index-locally valid.
            SetField(operation, "_artifactPaths", new CaptureRunPublicationArtifactPathSet[] { paths0, null });
            Assert.That(operation.IsValid, Is.False);
            Assert.That(observation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths0));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Observation_PathSetArrayLengthMismatch_Invalid_NoException()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths0 = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, paths0);
            Assert.That(observation.IsValid, Is.True);

            // Forge: the path set array is shorter than the plan entry count.
            SetField(operation, "_artifactPaths", new CaptureRunPublicationArtifactPathSet[] { paths0 });
            Assert.That(operation.IsValid, Is.False);
            Assert.That(observation.IsValid, Is.False);

            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths0));
        }

        [Test]
        public void Observation_DecisionDispositionCorrupted_Invalid_NoException()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths0 = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, paths0);
            Assert.That(observation.IsValid, Is.True);

            // Forge: the decision's disposition no longer names an authoritative document.
            SetField(decision, "_disposition", CaptureRunPublicationRecoveryDisposition.NoAuthoritativeDocument);
            Assert.That(decision.IsValid, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(observation.IsValid, Is.False);

            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths0));
        }

        [Test]
        public void Observation_PlanEntriesNull_Invalid_NoException()
        {
            CapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactPathSet paths0 = operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, paths0);
            Assert.That(observation.IsValid, Is.True);

            // Forge: the authoritative plan loses its entry array entirely.
            SetField(plan, "_entries", null);
            Assert.That(plan.IsValid, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(observation.IsValid, Is.False);

            Assert.Throws<ArgumentException>(() => MakeEntryObservation(operation, paths0));
        }

        // ---- Snapshot ----

        [Test]
        public void Snapshot_NullArgs_Rejected()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();

            Assert.That(Assert.Throws<ArgumentNullException>(() => MakeArtifactSnapshot(null, operation)).ParamName, Is.EqualTo("issuedBy"));

            Assert.That(Assert.Throws<ArgumentNullException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, null, EvAbsent, 0, new CaptureRunPublicationArtifactEntryObservation[0])).ParamName, Is.EqualTo("operation"));

            Assert.That(Assert.Throws<ArgumentNullException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, null)).ParamName, Is.EqualTo("entries"));
        }

        [Test]
        public void Snapshot_InvalidOperation_Rejected()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = (CaptureRunPublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                new FakeArtifactInspector(), operation, EvAbsent, 0, new CaptureRunPublicationArtifactEntryObservation[0]));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Snapshot_EntryCountMismatch_Rejected()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();

            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, new CaptureRunPublicationArtifactEntryObservation[0]));
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, new CaptureRunPublicationArtifactEntryObservation[2]));
        }

        [Test]
        public void Snapshot_NullElement_Rejected()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();

            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, new CaptureRunPublicationArtifactEntryObservation[] { null }));
        }

        [Test]
        public void Snapshot_OrderSwapAndDuplicateIndex_Rejected()
        {
            CapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            FakeArtifactInspector inspector = new FakeArtifactInspector();

            CaptureRunPublicationArtifactEntryObservation e0 = MakeEntryObservation(operation, operation.GetArtifactPaths(0));
            CaptureRunPublicationArtifactEntryObservation e1 = MakeEntryObservation(operation, operation.GetArtifactPaths(1));

            // Swapped order: position 0 holds entry 1 observation.
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, new[] { e1, e0 }));

            // Duplicate index: both positions hold entry 0 observation.
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, new[] { e0, e0 }));
        }

        [Test]
        public void Snapshot_TraceStatusCountBoundary()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();
            long traceLimit = TraceRunManifestCodec.MaximumCanonicalByteCount;

            Assert.That(MakeArtifactSnapshot(inspector, operation, EvAbsent, 0).IsValid, Is.True);
            Assert.That(MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, traceLimit).IsValid, Is.True);
            Assert.That(MakeArtifactSnapshot(inspector, operation, EvLimitExceeded, traceLimit + 1).IsValid, Is.True);

            Assert.Throws<ArgumentException>(() => MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, traceLimit + 1));
            Assert.Throws<ArgumentException>(() => MakeArtifactSnapshot(inspector, operation, EvLimitExceeded, traceLimit));
        }

        [Test]
        public void Snapshot_DefensiveCopy_ArrayNotExposed()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();

            CaptureRunPublicationArtifactEntryObservation[] entries =
            {
                MakeEntryObservation(operation, operation.GetArtifactPaths(0), stagingPngStatus: EvMatchesExpected, stagingPngCount: 16)
            };

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = new CaptureRunPublicationArtifactInspectionSnapshot(
                inspector, operation, EvAbsent, 0, entries);

            entries[0] = null;

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(snapshot.GetEntry(0).StagingPngStatus, Is.EqualTo(EvMatchesExpected));

            // No property exposes the underlying array.
            foreach (PropertyInfo prop in typeof(CaptureRunPublicationArtifactInspectionSnapshot).GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.PropertyType.IsArray, Is.False, prop.Name + " must not expose an array.");
            }
        }

        [Test]
        public void Snapshot_ForwardsOperationDecisionPlan()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation);

            Assert.That(snapshot.IssuedBy, Is.SameAs(inspector));
            Assert.That(snapshot.Operation, Is.SameAs(operation));
            Assert.That(snapshot.Decision, Is.SameAs(decision));
            Assert.That(snapshot.Plan, Is.SameAs(decision.AuthoritativePlan));
        }

        [Test]
        public void Snapshot_GetEntryOutOfRange_Rejected()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation);

            foreach (int bad in new[] { -1, 1, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetEntry(bad));
                Assert.That(ex.ParamName, Is.EqualTo("index"));
            }
        }

        [Test]
        public void Snapshot_ForgedCorruption_IsValidFalse_NoException()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeArtifactOperation();
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation);
            Assert.That(snapshot.IsValid, Is.True);

            // Forge a null entry element.
            CaptureRunPublicationArtifactInspectionSnapshot forged = (CaptureRunPublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionSnapshot));
            SetField(forged, "_issuedBy", inspector);
            SetField(forged, "_operation", operation);
            SetField(forged, "_traceManifestStatus", EvAbsent);
            SetField(forged, "_traceManifestProbedByteCount", 0L);
            SetField(forged, "_entries", new CaptureRunPublicationArtifactEntryObservation[] { null });
            Assert.That(forged.IsValid, Is.False);

            // Forge the trace status to undefined.
            CaptureRunPublicationArtifactInspectionSnapshot forgedTrace = (CaptureRunPublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionSnapshot));
            SetField(forgedTrace, "_issuedBy", inspector);
            SetField(forgedTrace, "_operation", operation);
            SetField(forgedTrace, "_traceManifestStatus", (CaptureRunPublicationEvidenceStatus)99);
            SetField(forgedTrace, "_traceManifestProbedByteCount", 0L);
            SetField(forgedTrace, "_entries", new[] { MakeEntryObservation(operation, operation.GetArtifactPaths(0)) });
            Assert.That(forgedTrace.IsValid, Is.False);
        }

        [Test]
        public void Snapshot_LeaseRelease_IsValidFalse()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = MakeRecoverySnapshot(
                inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan()));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(new FakeArtifactInspector(), operation);

            Assert.That(snapshot.IsValid, Is.True);

            outcome.Dispose();

            Assert.That(snapshot.IsValid, Is.False);
        }

        [Test]
        public void Inspection_DoesNotMutateOrDisposeInputs()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            CapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = MakeRecoverySnapshot(
                inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);
            CaptureRunPublicationArtifactInspectionOperation operation = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(new FakeArtifactInspector(), operation);

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(outcome.IsCreated, Is.True);
            Assert.That(disposeLog, Is.Empty, "Inspection must not dispose the lease.");
        }

        // ---- Inspector interface ----

        [Test]
        public void Inspector_InterfaceSignature()
        {
            Type type = typeof(ICaptureRunPublicationArtifactInspector);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);

            MethodInfo[] methods = type.GetMethods();
            Assert.That(methods.Length, Is.EqualTo(1));

            MethodInfo inspect = methods[0];
            Assert.That(inspect.Name, Is.EqualTo("Inspect"));
            Assert.That(inspect.ReturnType, Is.EqualTo(typeof(CaptureRunPublicationArtifactInspectionSnapshot)));

            ParameterInfo[] parameters = inspect.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationArtifactInspectionOperation)));
            Assert.That(parameters[0].Name, Is.EqualTo("operation"));
        }

        [Test]
        public void Inspector_XmlContract()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationArtifactInspector.cs"));

            Assert.That(source, Does.Contain("ArgumentNullException"));
            Assert.That(source, Does.Contain("ArgumentException"));
            Assert.That(source, Does.Contain("no-follow"));
            Assert.That(source, Does.Contain("SHA-256"));
            Assert.That(source, Does.Contain("retry"));
        }

        // ---- Shape ----

        [Test]
        public void Shape_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type[] types =
            {
                typeof(CaptureRunPublicationArtifactInspectionOperation),
                typeof(CaptureRunPublicationArtifactEntryObservation),
                typeof(CaptureRunPublicationArtifactInspectionSnapshot)
            };

            foreach (Type type in types)
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
        public void Shape_FieldsReadonly_NoStaticMutableState()
        {
            Type[] types =
            {
                typeof(CaptureRunPublicationArtifactInspectionOperation),
                typeof(CaptureRunPublicationArtifactEntryObservation),
                typeof(CaptureRunPublicationArtifactInspectionSnapshot)
            };

            foreach (Type type in types)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " must be readonly.");
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, type.Name + "." + field.Name + " must be readonly or const.");
                }
            }
        }

        [Test]
        public void Shape_EntryObservation_NoCollections()
        {
            foreach (FieldInfo field in typeof(CaptureRunPublicationArtifactEntryObservation).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(typeof(System.Collections.ICollection).IsAssignableFrom(field.FieldType), Is.False,
                    field.Name + " must not be an array or mutable collection.");
            }
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] codeFiles =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationEvidenceStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactInspectionOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactEntryObservation.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactInspectionSnapshot.cs"
            };

            string[] codeForbidden =
            {
                "File.", "Directory.", "FileStream", "DllImport", "UnityEngine", "System.Linq",
                "Serialize", "Deserialize", "SHA", "Random", "DateTime", "List<", "HashSet", "Dictionary"
            };

            foreach (string file in codeFiles)
            {
                string source = File.ReadAllText(LocateSource(file));
                foreach (string term in codeForbidden)
                {
                    Assert.That(source, Does.Not.Contain(term), file + " must not contain " + term);
                }
            }

            string interfaceSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationArtifactInspector.cs"));
            Assert.That(interfaceSource, Does.Not.Contain("File."));
            Assert.That(interfaceSource, Does.Not.Contain("Directory."));
            Assert.That(interfaceSource, Does.Not.Contain("FileStream"));
            Assert.That(interfaceSource, Does.Not.Contain("DllImport"));
            Assert.That(interfaceSource, Does.Not.Contain("UnityEngine"));
            Assert.That(interfaceSource, Does.Not.Contain("System.Linq"));
        }
    }
}
