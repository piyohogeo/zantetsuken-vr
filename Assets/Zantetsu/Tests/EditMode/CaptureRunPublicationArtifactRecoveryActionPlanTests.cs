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
    public class CaptureRunPublicationArtifactRecoveryActionPlanTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const long PngBytes = 16;

        private const long SidecarBytes = 32;

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

        private static CaptureRunPublicationArtifactKind NoneKind => CaptureRunPublicationArtifactKind.None;

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

        private static CaptureRunPublicationArtifactRecoveryAction ActionNone => CaptureRunPublicationArtifactRecoveryAction.None;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        private static CaptureRunPublicationArtifactRecoveryAction ReinspectArtifacts => CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts;

        private static CaptureRunPublicationArtifactRecoveryAction CommitCaptureIndex => CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryAction ContinueCaptureCompleteCleanup => CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup;

        private static CaptureRunPublicationArtifactRecoveryAction StopOrphanedPreTrace => CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryAction StopArtifactSourceMissing => CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryAction StopPublishedArtifactMissing => CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryAction StopRunRootCollision => CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispOrphanedPreTrace => CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispPublishMissingArtifacts => CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispCommitCaptureIndex => CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispCaptureComplete => CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispArtifactSourceMissing => CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispPublishedArtifactMissing => CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition DispRunRootCollision => CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;

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

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(CaptureRunRootRole role, CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = PngBytes,
            long sidecarByteLength = SidecarBytes,
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

        private static CapturePublicationPlanEntry[] MakeEntries(int count)
        {
            CapturePublicationPlanEntry[] entries = new CapturePublicationPlanEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntry(i + 1);
            }

            return entries;
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

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            bool indexAuthoritative = false,
            CapturePublicationPlan plan = null,
            int maximumEntryCount = 4)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(
                outcome, 1000, maximumEntryCount, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            plan = plan ?? MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, recoveryOperation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, recoveryOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);
            return new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
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

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPlan(
            bool indexAuthoritative = false,
            CapturePublicationPlan plan = null,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long traceCount = 0,
            int maximumEntryCount = 4)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, indexAuthoritative, plan, maximumEntryCount);
            return CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(inspector, operation, traceStatus, traceCount, null)));
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPlan(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            return CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(new FakeArtifactInspector(), operation, traceStatus, traceCount, entries)));
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan ForgeActionPlan(
            CaptureRunPublicationArtifactRecoveryDecision decision,
            CaptureRunPublicationArtifactRecoveryStep[] steps)
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = (CaptureRunPublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryActionPlan));
            SetField(plan, "_decision", decision);
            SetField(plan, "_steps", steps);
            return plan;
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactRecoveryActionPlanTests).Assembly.Location);
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
            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        // ---- Enum ----

        [Test]
        public void ArtifactKind_Enum_Contract()
        {
            AssertEnumContract(typeof(CaptureRunPublicationArtifactKind),
                new[] { "None", "Png", "Sidecar" });
        }

        [Test]
        public void RecoveryAction_Enum_Contract()
        {
            AssertEnumContract(typeof(CaptureRunPublicationArtifactRecoveryAction),
                new[]
                {
                    "None", "PublishArtifact", "ReinspectArtifacts", "CommitCaptureIndex",
                    "ContinueCaptureCompleteCleanup", "StopOrphanedPreTrace",
                    "StopArtifactSourceMissing", "StopPublishedArtifactMissing", "StopRunRootCollision"
                });
        }

        // ---- Step ----

        [Test]
        public void Step_AllActionsValidCombinations()
        {
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, Png).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 3, Sidecar).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(ReinspectArtifacts, -1, NoneKind).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndex, -1, NoneKind).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(ContinueCaptureCompleteCleanup, -1, NoneKind).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(StopOrphanedPreTrace, -1, NoneKind).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(StopArtifactSourceMissing, -1, NoneKind).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(StopPublishedArtifactMissing, -1, NoneKind).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationArtifactRecoveryStep(StopRunRootCollision, -1, NoneKind).IsValid, Is.True);
        }

        [Test]
        public void Step_NoneAction_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunPublicationArtifactRecoveryStep(ActionNone, -1, NoneKind));
        }

        [Test]
        public void Step_UndefinedAction_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunPublicationArtifactRecoveryStep(
                (CaptureRunPublicationArtifactRecoveryAction)99, -1, NoneKind));
        }

        [Test]
        public void Step_UndefinedKind_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunPublicationArtifactRecoveryStep(
                PublishArtifact, 0, (CaptureRunPublicationArtifactKind)99));
        }

        [Test]
        public void Step_PublishNegativeIndex_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, -1, Png));
        }

        [Test]
        public void Step_PublishNoneKind_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, NoneKind));
        }

        [Test]
        public void Step_RoutingWithIndex_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactRecoveryStep(StopRunRootCollision, 0, NoneKind));
        }

        [Test]
        public void Step_RoutingWithKind_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndex, -1, Png));
        }

        [Test]
        public void Step_ForgedFields_IsValidAndMatchesFalse_NoException()
        {
            CaptureRunPublicationArtifactRecoveryStep valid = new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 2, Sidecar);
            Assert.That(valid.Matches(PublishArtifact, 2, Sidecar), Is.True);
            Assert.That(valid.Matches(PublishArtifact, 2, Png), Is.False);
            Assert.That(valid.Matches(PublishArtifact, 3, Sidecar), Is.False);

            CaptureRunPublicationArtifactRecoveryStep forged = (CaptureRunPublicationArtifactRecoveryStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryStep));
            SetField(forged, "_action", ActionNone);
            SetField(forged, "_entryIndex", -1);
            SetField(forged, "_artifactKind", NoneKind);
            Assert.That(forged.IsValid, Is.False);
            Assert.That(forged.Matches(StopRunRootCollision, -1, NoneKind), Is.False);

            SetField(forged, "_action", (CaptureRunPublicationArtifactRecoveryAction)99);
            Assert.That(forged.IsValid, Is.False);
            Assert.That(forged.Matches(StopRunRootCollision, -1, NoneKind), Is.False);
        }

        // ---- Step sequences ----

        [Test]
        public void Plan_OrphanedPreTrace_SingleStopStep()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(traceStatus: EvAbsent, traceCount: 0);

            Assert.That(plan.Disposition, Is.EqualTo(DispOrphanedPreTrace));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(StopOrphanedPreTrace, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_PublishMissingArtifacts_OrderAndCount()
        {
            CapturePublicationPlan planEntries = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, planEntries, 4);

            CaptureRunPublicationArtifactEntryObservation e0 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0);
            CaptureRunPublicationArtifactEntryObservation e1 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(1),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { e0, e1 });

            Assert.That(plan.Disposition, Is.EqualTo(DispPublishMissingArtifacts));
            Assert.That(plan.Count, Is.EqualTo(4));
            Assert.That(plan.GetStep(0).Matches(PublishArtifact, 0, Png), Is.True);
            Assert.That(plan.GetStep(1).Matches(PublishArtifact, 0, Sidecar), Is.True);
            Assert.That(plan.GetStep(2).Matches(PublishArtifact, 1, Png), Is.True);
            Assert.That(plan.GetStep(3).Matches(ReinspectArtifacts, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_PublishMissingArtifacts_SkipsMatchedFinal()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            Assert.That(plan.Disposition, Is.EqualTo(DispPublishMissingArtifacts));
            Assert.That(plan.Count, Is.EqualTo(2));
            Assert.That(plan.GetStep(0).Matches(PublishArtifact, 0, Sidecar), Is.True);
            Assert.That(plan.GetStep(1).Matches(ReinspectArtifacts, -1, NoneKind), Is.True);
        }

        [Test]
        public void Plan_PublishMissingArtifacts_StagingMatchFinalMatch_NoPublish()
        {
            // Both artifacts already final-matching, so nothing is publishable;
            // the classifier therefore never reaches PublishMissingArtifacts.
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            Assert.That(plan.Disposition, Is.EqualTo(DispCommitCaptureIndex));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(CommitCaptureIndex, -1, NoneKind), Is.True);
        }

        [Test]
        public void Plan_CommitCaptureIndex_SingleStep()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            Assert.That(plan.Disposition, Is.EqualTo(DispCommitCaptureIndex));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(CommitCaptureIndex, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_CaptureComplete_SingleCleanupStep()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, indexAuthoritative: true);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            Assert.That(plan.Disposition, Is.EqualTo(DispCaptureComplete));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(ContinueCaptureCompleteCleanup, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_ArtifactSourceMissing_SingleStopStep()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            Assert.That(plan.Disposition, Is.EqualTo(DispArtifactSourceMissing));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(StopArtifactSourceMissing, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_PublishedArtifactMissing_SingleStopStep()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, indexAuthoritative: true);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, new[] { observation });

            Assert.That(plan.Disposition, Is.EqualTo(DispPublishedArtifactMissing));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(StopPublishedArtifactMissing, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_RunRootCollision_SingleStopStep()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(traceStatus: EvMismatch, traceCount: 100);

            Assert.That(plan.Disposition, Is.EqualTo(DispRunRootCollision));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.GetStep(0).Matches(StopRunRootCollision, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        // ---- Rejection ----

        [Test]
        public void Builder_NullDecision_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(null));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Builder_InvalidDecision_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = (CaptureRunPublicationArtifactRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryDecision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(decision));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        // ---- Shape ----

        [Test]
        public void Plan_ConstructorTakesOnlyDecision()
        {
            ConstructorInfo[] constructors = typeof(CaptureRunPublicationArtifactRecoveryActionPlan).GetConstructors(
                BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(constructors.Length, Is.EqualTo(1));
            ParameterInfo[] parameters = constructors[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationArtifactRecoveryDecision)));
        }

        [Test]
        public void Plan_PrivateArrayNotExposed()
        {
            foreach (PropertyInfo prop in typeof(CaptureRunPublicationArtifactRecoveryActionPlan).GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.PropertyType.IsArray, Is.False, prop.Name + " must not expose an array.");
            }
        }

        [Test]
        public void Plan_GetStepOutOfRange_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(traceStatus: EvAbsent, traceCount: 0);

            foreach (int bad in new[] { -1, 1, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetStep(bad));
                Assert.That(ex.ParamName, Is.EqualTo("index"));
            }
        }

        [Test]
        public void Plan_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryActionPlan);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Plan_FieldShape_TwoReadonlyFields()
        {
            FieldInfo[] fields = typeof(CaptureRunPublicationArtifactRecoveryActionPlan).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Builder_IsStaticWithNoState()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryActionPlanBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        // ---- Forge defense ----

        [Test]
        public void Plan_ForgedStepsArrayNull_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = ForgeActionPlan(
                BuildPlan(traceStatus: EvAbsent, traceCount: 0).Decision, null);

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_ForgedNullStepElement_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = MakeCommitDecision();

            CaptureRunPublicationArtifactRecoveryActionPlan plan = ForgeActionPlan(
                decision, new CaptureRunPublicationArtifactRecoveryStep[] { null });

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_ForgedStepOrderSwap_IsValidFalse()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0);
            CaptureRunPublicationArtifactRecoveryActionPlan valid = BuildPlan(operation, new[] { observation });

            CaptureRunPublicationArtifactRecoveryActionPlan swapped = ForgeActionPlan(valid.Decision, new[]
            {
                new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, Sidecar),
                new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, Png),
                new CaptureRunPublicationArtifactRecoveryStep(ReinspectArtifacts, -1, NoneKind)
            });

            Assert.That(swapped.IsValid, Is.False);
        }

        [Test]
        public void Plan_ForgedStepActionSwap_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = MakeCommitDecision();

            CaptureRunPublicationArtifactRecoveryActionPlan plan = ForgeActionPlan(decision, new[]
            {
                new CaptureRunPublicationArtifactRecoveryStep(StopRunRootCollision, -1, NoneKind)
            });

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_ForgedStepCountMismatch_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = MakeCommitDecision();

            CaptureRunPublicationArtifactRecoveryActionPlan missing = ForgeActionPlan(
                decision, new CaptureRunPublicationArtifactRecoveryStep[0]);
            Assert.That(missing.IsValid, Is.False);

            CaptureRunPublicationArtifactRecoveryActionPlan extra = ForgeActionPlan(decision, new[]
            {
                new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndex, -1, NoneKind),
                new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndex, -1, NoneKind)
            });
            Assert.That(extra.IsValid, Is.False);
        }

        [Test]
        public void Plan_ForgedDecision_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryDecision orphanDecision = BuildPlan(traceStatus: EvAbsent, traceCount: 0).Decision;
            CaptureRunPublicationArtifactRecoveryActionPlan valid = CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(MakeCommitDecision());

            CaptureRunPublicationArtifactRecoveryActionPlan forged = ForgeActionPlan(orphanDecision, new[]
            {
                valid.GetStep(0)
            });

            Assert.That(forged.IsValid, Is.False);
        }

        private static CaptureRunPublicationArtifactRecoveryDecision MakeCommitDecision()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            return CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(new FakeArtifactInspector(), operation, EvMatchesExpected, 100, new[] { observation }));
        }

        // ---- Lease / mutation ----

        [Test]
        public void Plan_LeaseRelease_Invalid_NoException()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation, EvAbsent, 0, null);
            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot);
            CaptureRunPublicationArtifactRecoveryActionPlan plan = CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(decision);

            Assert.That(plan.IsValid, Is.True);

            operation.LockLease.Dispose();

            Assert.That(snapshot.IsValid, Is.False);
            Assert.That(decision.IsValid, Is.False);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_DoesNotMutateOrDisposeInputs()
        {
            List<string> disposeLog = new List<string>();
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(disposeLog);
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation, EvAbsent, 0, null);
            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot);

            CaptureRunPublicationArtifactRecoveryActionPlan plan = CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(decision);

            Assert.That(plan.Decision, Is.SameAs(decision));
            Assert.That(plan.Decision.Snapshot, Is.SameAs(snapshot));
            Assert.That(disposeLog, Is.Empty, "Plan construction must not dispose the lease.");
            Assert.That(operation.LockLease.IsCreated, Is.True);
            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        // ---- Linearity / source ----

        [Test]
        public void Plan_LargePlan_LinearAndCorrect()
        {
            int count = 1000;
            CapturePublicationPlan planEntries = MakePlan(entries: MakeEntries(count));
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, planEntries, count);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation, operation.GetArtifactPaths(i),
                    EvMatchesExpected, PngBytes, EvMatchesExpected, SidecarBytes,
                    EvAbsent, 0, EvAbsent, 0);
            }

            CaptureRunPublicationArtifactRecoveryActionPlan plan = CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, entries)));

            Assert.That(plan.Disposition, Is.EqualTo(DispPublishMissingArtifacts));
            Assert.That(plan.Count, Is.EqualTo(2 * count + 1));
            Assert.That(plan.GetStep(0).Matches(PublishArtifact, 0, Png), Is.True);
            Assert.That(plan.GetStep(1).Matches(PublishArtifact, 0, Sidecar), Is.True);
            Assert.That(plan.GetStep(2 * count).Matches(ReinspectArtifacts, -1, NoneKind), Is.True);
            Assert.That(plan.IsValid, Is.True);
        }

        [Test]
        public void Plan_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryActionPlan.cs"));

            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("ToArray"));
            Assert.That(source, Does.Not.Contain("Array.Copy"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Dictionary"));
            Assert.That(source, Does.Not.Contain("HashSet"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("SHA"));
            Assert.That(source, Does.Not.Contain("Serialize"));
            Assert.That(source, Does.Not.Contain("Deserialize"));
        }
    }
}
