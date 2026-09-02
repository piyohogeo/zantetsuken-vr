using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunPublicationCaptureCompleteCleanupActionPlanTests
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

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationDocumentObservationStatus DocLimitExceeded => CaptureRunPublicationDocumentObservationStatus.LimitExceeded;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationFramesObservationStatus FramesDirectory => CaptureRunPublicationFramesObservationStatus.Directory;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }

            _owners.Clear();
        }

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

        private static PngJsonCapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = PngBytes,
            long sidecarByteLength = SidecarBytes,
            string pngHash = null,
            string sidecarHash = null)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new PngJsonCapturePublicationPlanEntry(
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

        private static PngJsonCapturePublicationPlanEntry[] MakeEntries(int count)
        {
            PngJsonCapturePublicationPlanEntry[] entries = new PngJsonCapturePublicationPlanEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntry(i + 1);
            }

            return entries;
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId = 1,
            string initId = null,
            string manifestHash = null,
            PngJsonCapturePublicationPlanEntry[] entries = null)
        {
            return new PngJsonCapturePublicationPlan(
                testRunId,
                initId ?? InitId,
                manifestHash ?? StagingHash,
                entries ?? new[] { MakeEntry(10) });
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            PngJsonCapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationSessionOwnershipLease MakeOwner(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            return owner;
        }

        private CaptureRunInitializationOpenOutcome MakeOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInitInspector inspector = new FakeInitInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            owner = MakeOwner(layout, disposeLog, out identity);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(List<string> disposeLog = null)
        {
            return MakeOutcome(disposeLog, out _, out _);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOutcome(disposeLog, out owner, out _);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeRecoverySnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                stagingFramesStatus,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            PngJsonCapturePublicationPlan plan = null,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            int maximumEntryCount = 4)
        {
            return MakeOperation(disposeLog, plan, publicationPlanTemporary, publicationPlan, captureIndexTemporary, captureIndex, stagingFramesStatus, maximumEntryCount, out _);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOperation(null, null, null, null, null, null, CaptureRunPublicationFramesObservationStatus.Directory, 4, out owner);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog,
            PngJsonCapturePublicationPlan plan,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            CaptureRunPublicationDocumentObservation publicationPlan,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationDocumentObservation captureIndex,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus,
            int maximumEntryCount,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog, out owner);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(
                outcome, 1000, maximumEntryCount, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            plan = plan ?? MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = MakeRecoverySnapshot(
                inspector,
                recoveryOperation,
                publicationPlanTemporary: publicationPlanTemporary,
                publicationPlan: publicationPlan ?? MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary: captureIndexTemporary,
                captureIndex: captureIndex,
                stagingFramesStatus: stagingFramesStatus);
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

        private static CaptureRunPublicationArtifactEntryObservation MakeEntryObservationIndexLocal(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactInspectionOperation.ValidationToken token,
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
                token,
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

        private static FakeArtifactInspector MakeArtifactInspector(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100,
            List<string> log = null)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector(log);
            inspector.Snapshot = MakeArtifactSnapshot(inspector, operation, traceStatus, traceCount, entries);
            return inspector;
        }

        private static CaptureRunPublicationArtifactRecoveryExecutionCoordinator MakeExecutionCoordinator(
            List<string> log = null)
        {
            return new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(
                new FakePublisher(log), new FakeCommitter(log));
        }

        private static CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator MakeOrchestrator(
            ICaptureRunPublicationArtifactInspector inspector,
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator executionCoordinator)
        {
            return new CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator(inspector, executionCoordinator);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildArtifactResult(
            bool commitRoute,
            CaptureRunPublicationEvidenceStatus stagingPngStatus,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus,
            CaptureRunPublicationEvidenceStatus finalPngStatus,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus,
            CaptureRunPublicationEvidenceStatus traceStatus)
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = commitRoute
                ? MakeOperation(plan: plan)
                : MakeOperation(plan: plan, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan));

            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: stagingPngStatus,
                stagingPngCount: stagingPngStatus == EvMatchesExpected ? PngBytes : 0,
                stagingSidecarStatus: stagingSidecarStatus,
                stagingSidecarCount: stagingSidecarStatus == EvMatchesExpected ? SidecarBytes : 0,
                finalPngStatus: finalPngStatus,
                finalPngCount: finalPngStatus == EvMatchesExpected ? PngBytes : 0,
                finalSidecarStatus: finalSidecarStatus,
                finalSidecarCount: finalSidecarStatus == EvMatchesExpected ? SidecarBytes : 0);

            FakeArtifactInspector inspector = MakeArtifactInspector(
                operation, new[] { observation }, traceStatus, traceStatus == EvAbsent ? 0 : 100);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            return orchestrator.Execute(operation);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            int entryCount = 1,
            CaptureRunPublicationEvidenceStatus stagingStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            PngJsonCapturePublicationPlan plan = null)
        {
            return BuildCommitResult(null, entryCount, stagingStatus, publicationPlanTemporary, publicationPlan, captureIndexTemporary, stagingFramesStatus, plan, out _);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return BuildCommitResult(null, 1, CaptureRunPublicationEvidenceStatus.MatchesExpected, null, null, null, CaptureRunPublicationFramesObservationStatus.Directory, null, out owner);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return BuildCommitResult(disposeLog, 1, CaptureRunPublicationEvidenceStatus.MatchesExpected, null, null, null, CaptureRunPublicationFramesObservationStatus.Directory, null, out owner);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            List<string> disposeLog,
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            CaptureRunPublicationDocumentObservation publicationPlan,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus,
            PngJsonCapturePublicationPlan plan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            plan = plan ?? MakePlan(entries: MakeEntries(entryCount));
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                disposeLog,
                plan,
                publicationPlanTemporary,
                publicationPlan ?? MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary,
                null,
                stagingFramesStatus,
                entryCount,
                out owner);

            CaptureRunPublicationArtifactInspectionOperation.ValidationToken token = operation.AcquireValidationToken();

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeEntryObservationIndexLocal(
                    operation,
                    token,
                    operation.GetArtifactPaths(i),
                    stagingPngStatus: stagingStatus,
                    stagingPngCount: stagingStatus == EvMatchesExpected ? PngBytes : 0,
                    stagingSidecarStatus: stagingStatus,
                    stagingSidecarCount: stagingStatus == EvMatchesExpected ? SidecarBytes : 0,
                    finalPngStatus: EvMatchesExpected,
                    finalPngCount: PngBytes,
                    finalSidecarStatus: EvMatchesExpected,
                    finalSidecarCount: SidecarBytes);
            }

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            return orchestrator.Execute(operation);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            int entryCount = 1,
            CaptureRunPublicationEvidenceStatus stagingStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            PngJsonCapturePublicationPlan plan = null)
        {
            return BuildCaptureCompleteResult(null, entryCount, stagingStatus, publicationPlanTemporary, publicationPlan, captureIndexTemporary, stagingFramesStatus, plan, out _);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return BuildCaptureCompleteResult(null, 1, CaptureRunPublicationEvidenceStatus.MatchesExpected, null, null, null, CaptureRunPublicationFramesObservationStatus.Directory, null, out owner);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            List<string> disposeLog,
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            CaptureRunPublicationDocumentObservation publicationPlan,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus,
            PngJsonCapturePublicationPlan plan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            plan = plan ?? MakePlan(entries: MakeEntries(entryCount));
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                disposeLog,
                plan,
                publicationPlanTemporary,
                publicationPlan ?? MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary,
                MakeDoc(CaptureIndex, DocCanonical, 100, plan),
                stagingFramesStatus,
                entryCount,
                out owner);

            CaptureRunPublicationArtifactInspectionOperation.ValidationToken token = operation.AcquireValidationToken();

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeEntryObservationIndexLocal(
                    operation,
                    token,
                    operation.GetArtifactPaths(i),
                    stagingPngStatus: stagingStatus,
                    stagingPngCount: stagingStatus == EvMatchesExpected ? PngBytes : 0,
                    stagingSidecarStatus: stagingStatus,
                    stagingSidecarCount: stagingStatus == EvMatchesExpected ? SidecarBytes : 0,
                    finalPngStatus: EvMatchesExpected,
                    finalPngCount: PngBytes,
                    finalSidecarStatus: EvMatchesExpected,
                    finalSidecarCount: SidecarBytes);
            }

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            return orchestrator.Execute(operation);
        }

        private CaptureRunPublicationCaptureCompleteCleanupActionPlan BuildPlan(bool commitRoute)
        {
            return BuildPlan(commitRoute, out _);
        }

        private CaptureRunPublicationCaptureCompleteCleanupActionPlan BuildPlan(
            bool commitRoute,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(
                commitRoute ? BuildCommitResult(out owner) : BuildCaptureCompleteResult(out owner));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlanTests).Assembly.Location);
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

        private sealed class FakeInitInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            public FakeInitInspector(CaptureRunInitializationRootObservation staging, CaptureRunInitializationRootObservation final)
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
            private readonly List<string> _log;

            public FakeArtifactInspector(List<string> log = null)
            {
                _log = log;
            }

            public CaptureRunPublicationArtifactInspectionSnapshot Snapshot { get; set; }

            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                _log?.Add("inspect");
                return Snapshot;
            }
        }

        private sealed class FakePublisher : ICaptureRunPublicationArtifactPublisher
        {
            private readonly List<string> _log;

            public FakePublisher(List<string> log = null) { _log = log; }

            public CaptureRunPublicationArtifactPublishReceipt Publish(CaptureRunPublicationArtifactPublishOperation operation)
            {
                _log?.Add("publish:" + operation.EntryIndex + ":" + operation.ArtifactKind);
                return new CaptureRunPublicationArtifactPublishReceipt(this, operation);
            }
        }

        private sealed class FakeCommitter : ICaptureRunCaptureIndexCommitter
        {
            private readonly List<string> _log;

            public FakeCommitter(List<string> log = null) { _log = log; }

            public CaptureRunCaptureIndexCommitReceipt Commit(CaptureRunCaptureIndexCommitOperation operation)
            {
                _log?.Add("commit:" + operation.Mode);
                return new CaptureRunCaptureIndexCommitReceipt(this, operation);
            }
        }

        // ---- Action enum contract ----

        [Test]
        public void Action_Enum_Contract()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupAction);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            Assert.That(Enum.GetNames(type), Is.EqualTo(new[]
            {
                "None",
                "DeletePublicationPlanTemporary",
                "DeleteCaptureIndexTemporary",
                "DeleteStagingArtifact",
                "RemoveStagingFramesRoot",
                "DeletePublicationPlan",
                "DeleteStagingReadyMarker",
                "DeleteStagingInitializationMarker",
                "RemoveStagingRunRoot",
                "CaptureCompleteReady"
            }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(10));
            for (int i = 0; i < 10; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        // ---- Step shape and contract ----

        [Test]
        public void Step_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Step_Constructor_RejectsNoneUndefinedContradictory()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CaptureRunPublicationCaptureCompleteCleanupStep(CaptureRunPublicationCaptureCompleteCleanupAction.None, -1, CaptureRunPublicationArtifactKind.None));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CaptureRunPublicationCaptureCompleteCleanupStep((CaptureRunPublicationCaptureCompleteCleanupAction)999, -1, CaptureRunPublicationArtifactKind.None));

            // DeleteStagingArtifact with negative index.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CaptureRunPublicationCaptureCompleteCleanupStep(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, -1, CaptureRunPublicationArtifactKind.Png));

            // DeleteStagingArtifact with kind None.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CaptureRunPublicationCaptureCompleteCleanupStep(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.None));

            // Non-staging action with non-negative index.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CaptureRunPublicationCaptureCompleteCleanupStep(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, 0, CaptureRunPublicationArtifactKind.None));

            // Non-staging action with kind Png.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new CaptureRunPublicationCaptureCompleteCleanupStep(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.Png));
        }

        [Test]
        public void Step_Matches_FullComparison()
        {
            CaptureRunPublicationCaptureCompleteCleanupStep step = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 3, CaptureRunPublicationArtifactKind.Sidecar);

            Assert.That(step.IsValid, Is.True);
            Assert.That(step.Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 3, CaptureRunPublicationArtifactKind.Sidecar), Is.True);
            Assert.That(step.Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 3, CaptureRunPublicationArtifactKind.Png), Is.False);
            Assert.That(step.Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 2, CaptureRunPublicationArtifactKind.Sidecar), Is.False);
            Assert.That(step.Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, 3, CaptureRunPublicationArtifactKind.Sidecar), Is.False);
        }

        // ---- Plan / Builder shape ----

        [Test]
        public void Plan_Shape_TwoReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Builder_Shape_StaticNoFields()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract && type.IsSealed, Is.True);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.That(fields, Is.Empty, "The builder must hold no fields.");
        }

        [Test]
        public void Shape_NoLeaseExposure()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan),
                typeof(CaptureRunPublicationCaptureCompleteCleanupStep),
                typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a raw or ownership lease.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a raw or ownership lease.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a raw or ownership lease.");
                }
            }
        }

        // ---- Builder rejection ----

        [Test]
        public void Builder_NullResult_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(null));
            Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
        }

        [Test]
        public void Builder_InvalidResult_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();

            CaptureRunPublicationArtifactRecoveryOrchestrationResult forged =
                (CaptureRunPublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryOrchestrationResult));
            SetField(forged, "_issuedBy", result.IssuedBy);
            SetField(forged, "_executionResult", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(forged));
            Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
        }

        [Test]
        public void Builder_Rejects_NonCleanupStatuses()
        {
            // ReinspectionRequired (publish).
            CaptureRunPublicationArtifactRecoveryOrchestrationResult publish = BuildArtifactResult(
                true, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            Assert.That(publish.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult orphaned = BuildArtifactResult(
                true, EvAbsent, EvAbsent, EvAbsent, EvAbsent, EvAbsent);
            Assert.That(orphaned.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult sourceMissing = BuildArtifactResult(
                true, EvAbsent, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected);
            Assert.That(sourceMissing.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult publishedMissing = BuildArtifactResult(
                false, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            Assert.That(publishedMissing.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult collision = BuildArtifactResult(
                true, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMismatch);
            Assert.That(collision.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision));

            foreach (CaptureRunPublicationArtifactRecoveryOrchestrationResult result in new[]
            {
                publish, orphaned, sourceMissing, publishedMissing, collision
            })
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
                Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
            }
        }

        // ---- Commit receipt proof ----

        [Test]
        public void CommitRoute_MissingReceipt_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            CaptureRunPublicationArtifactRecoveryCompletedStep step = result.ExecutionResult.GetCompletedStep(0);
            SetField(step, "_commitReceipt", null);

            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CommitRoute_ForeignIssuer_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            CaptureRunPublicationArtifactRecoveryCompletedStep step = result.ExecutionResult.GetCompletedStep(0);
            SetField(step.CommitReceipt, "_issuedBy", new FakeCommitter());

            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CommitRoute_DifferentOperation_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            CaptureRunPublicationArtifactRecoveryOrchestrationResult other = BuildCommitResult();

            CaptureRunPublicationArtifactRecoveryCompletedStep step = result.ExecutionResult.GetCompletedStep(0);
            CaptureRunCaptureIndexCommitOperation otherOperation = other.Batch.GetStep(0).CaptureIndexCommitOperation;
            SetField(step.CommitReceipt, "_operation", otherOperation);

            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CommitRoute_CorruptedReceipt_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            CaptureRunPublicationArtifactRecoveryCompletedStep step = result.ExecutionResult.GetCompletedStep(0);
            SetField(step.CommitReceipt, "_operation", null);

            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        // ---- CaptureComplete index proof ----

        [Test]
        public void CaptureComplete_MissingCanonicalIndex_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);
            Assert.That(plan.IsValid, Is.True);

            SetField(plan.OrchestrationResult.InspectionSnapshot.Decision.Snapshot, "_captureIndex", MakeDoc(CaptureIndex, DocAbsent));

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void CaptureComplete_DifferentPlan_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);
            Assert.That(plan.IsValid, Is.True);

            PngJsonCapturePublicationPlan other = MakePlan(testRunId: 1, entries: MakeEntries(2));
            SetField(plan.OrchestrationResult.InspectionSnapshot.Decision.Snapshot, "_captureIndex", MakeDoc(CaptureIndex, DocCanonical, 100, other));

            Assert.That(plan.IsValid, Is.False);
        }

        // ---- Step sequence ----

        [Test]
        public void CommitRoute_StepSequence()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            // 1 entry with staging MatchesExpected, publication.plan Canonical, staging frames Directory.
            // Expected: DeleteStagingArtifact(0,Png), DeleteStagingArtifact(0,Sidecar),
            //           RemoveStagingFramesRoot, DeletePublicationPlan,
            //           DeleteStagingReadyMarker, DeleteStagingInitializationMarker,
            //           RemoveStagingRunRoot, CaptureCompleteReady
            Assert.That(plan.Count, Is.EqualTo(8));

            Assert.That(plan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.Png), Is.True);
            Assert.That(plan.GetStep(1).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.Sidecar), Is.True);
            Assert.That(plan.GetStep(2).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(3).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(4).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(5).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(6).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(7).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady, -1, CaptureRunPublicationArtifactKind.None), Is.True);
        }

        [Test]
        public void CaptureCompleteRoute_StepSequence()
        {
            // publication.plan Canonical, capture index Canonical, index tmp Canonical (matching), plan tmp Canonical.
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCaptureCompleteResult(
                publicationPlanTemporary: MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, planValue),
                plan: planValue);

            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            // 1 entry (2 staging steps) + 2 temporary + frames + publication + 4 tail = 10.
            Assert.That(plan.Count, Is.EqualTo(10));

            Assert.That(plan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(1).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(2).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.Png), Is.True);
            Assert.That(plan.GetStep(3).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.Sidecar), Is.True);
            Assert.That(plan.GetStep(4).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(5).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(6).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(7).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(8).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(9).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady, -1, CaptureRunPublicationArtifactKind.None), Is.True);
        }

        [Test]
        public void TemporaryDocuments_AbsentGeneratesNoStep()
        {
            // Both tmp documents absent: no temporary steps.
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);

            for (int i = 0; i < plan.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = plan.GetStep(i);
                Assert.That(step.Action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary));
                Assert.That(step.Action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
            }
        }

        [Test]
        public void TemporaryDocuments_CanonicalGeneratesSteps()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));

            // Publication plan tmp canonical → DeletePublicationPlanTemporary at head.
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(
                publicationPlanTemporary: MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue),
                plan: planValue);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            Assert.That(plan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary, -1, CaptureRunPublicationArtifactKind.None), Is.True);

            // Capture index tmp canonical → DeleteCaptureIndexTemporary in the capture-complete route only.
            CaptureRunPublicationArtifactRecoveryOrchestrationResult complete = BuildCaptureCompleteResult(
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, planValue),
                plan: planValue);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan completePlan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(complete);
            Assert.That(completePlan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary, -1, CaptureRunPublicationArtifactKind.None), Is.True);

            // Commit route: a canonical index tmp must not produce a delete step.
            CaptureRunPublicationArtifactRecoveryOrchestrationResult committed = BuildCommitResult(
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, planValue),
                plan: planValue);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan committedPlan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(committed);
            for (int i = 0; i < committedPlan.Count; i++)
            {
                Assert.That(committedPlan.GetStep(i).Action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
            }
        }

        [Test]
        public void TemporaryDocuments_InvalidOrLimitExceeded_Rejected()
        {
            // Invalid publication plan temporary (an Invalid tmp is not a classifier collision).
            CaptureRunPublicationArtifactRecoveryOrchestrationResult invalidPlanTmp = BuildCommitResult(
                publicationPlanTemporary: MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocInvalid, 0));
            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(invalidPlanTmp));

            // Invalid capture index temporary (capture-complete route).
            CaptureRunPublicationArtifactRecoveryOrchestrationResult invalidIndexTmp = BuildCaptureCompleteResult(
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocInvalid, 0));
            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(invalidIndexTmp));

            // A limit-exceeded temporary document is a classifier collision and can never
            // reach the builder through the normal pipeline; forge it to exercise the
            // fail-closed path directly.
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);
            Assert.That(plan.IsValid, Is.True);

            SetField(plan.OrchestrationResult.InspectionSnapshot.Decision.Snapshot, "_publicationPlanTemporary",
                MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocLimitExceeded, 100));

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void StagingArtifact_AbsentGeneratesNoStep()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(stagingStatus: EvAbsent);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            for (int i = 0; i < plan.Count; i++)
            {
                Assert.That(plan.GetStep(i).Action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            }
        }

        [Test]
        public void StagingArtifact_MatchesExpectedGeneratesStep()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            Assert.That(plan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.Png), Is.True);
            Assert.That(plan.GetStep(1).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, CaptureRunPublicationArtifactKind.Sidecar), Is.True);
        }

        [Test]
        public void StagingArtifact_EntryAscendingPngBeforeSidecar()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 3);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            int position = 0;
            for (int entry = 0; entry < 3; entry++)
            {
                Assert.That(plan.GetStep(position++).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, entry, CaptureRunPublicationArtifactKind.Png), Is.True);
                Assert.That(plan.GetStep(position++).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, entry, CaptureRunPublicationArtifactKind.Sidecar), Is.True);
            }

            Assert.That(plan.GetStep(position).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
        }

        [Test]
        public void NoFinalArtifactOrFinalRootOrCaptureIndexDeletion()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);

            for (int i = 0; i < plan.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = plan.GetStep(i);
                Assert.That(step.IsValid, Is.True);

                // The cleanup action contract contains no final-artifact, final-root,
                // capture-index, or final-marker deletion action. The only per-entry
                // action is DeleteStagingArtifact, which targets staging Png/Sidecar.
                if (step.Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    Assert.That(
                        step.ArtifactKind == CaptureRunPublicationArtifactKind.Png
                        || step.ArtifactKind == CaptureRunPublicationArtifactKind.Sidecar,
                        Is.True);
                    Assert.That(step.EntryIndex, Is.GreaterThanOrEqualTo(0));
                }
                else
                {
                    Assert.That(step.EntryIndex, Is.EqualTo(-1));
                    Assert.That(step.ArtifactKind, Is.EqualTo(CaptureRunPublicationArtifactKind.None));
                }
            }
        }

        [Test]
        public void PublicationPlanAfterArtifactsBeforeMarkers()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 2);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            int lastArtifact = -1;
            int publicationIndex = -1;
            int firstMarker = -1;

            for (int i = 0; i < plan.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupStep step = plan.GetStep(i);
                if (step.Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    lastArtifact = i;
                }
                else if (step.Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan)
                {
                    publicationIndex = i;
                }
                else if (step.Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker)
                {
                    firstMarker = i;
                }
            }

            Assert.That(lastArtifact, Is.GreaterThanOrEqualTo(0));
            Assert.That(publicationIndex, Is.GreaterThan(lastArtifact));
            Assert.That(firstMarker, Is.GreaterThan(publicationIndex));
        }

        [Test]
        public void MarkerOrderReadyBeforeInit()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            int ready = -1;
            int init = -1;
            for (int i = 0; i < plan.Count; i++)
            {
                if (plan.GetStep(i).Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker)
                {
                    ready = i;
                }
                else if (plan.GetStep(i).Action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker)
                {
                    init = i;
                }
            }

            Assert.That(ready, Is.GreaterThanOrEqualTo(0));
            Assert.That(init, Is.GreaterThan(ready));
        }

        [Test]
        public void CaptureCompleteReadyAlwaysLast()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan commit = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan complete = BuildPlan(commitRoute: false);

            Assert.That(commit.GetStep(commit.Count - 1).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
            Assert.That(complete.GetStep(complete.Count - 1).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
        }

        // ---- Forged / corruption ----

        [Test]
        public void ForgedStepArray_NullStep_Reorder_IsValidFalse()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            Assert.That(plan.IsValid, Is.True);

            // Null step array.
            SetField(plan, "_steps", null);
            Assert.That(plan.IsValid, Is.False);

            // Null element.
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan2 = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupStep[] withNull = new CaptureRunPublicationCaptureCompleteCleanupStep[plan2.Count];
            for (int i = 0; i < withNull.Length; i++)
            {
                withNull[i] = plan2.GetStep(i);
            }

            withNull[0] = null;
            SetField(plan2, "_steps", withNull);
            Assert.That(plan2.IsValid, Is.False);

            // Reordered steps.
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan3 = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupStep[] reordered = new CaptureRunPublicationCaptureCompleteCleanupStep[plan3.Count];
            for (int i = 0; i < reordered.Length; i++)
            {
                reordered[i] = plan3.GetStep(i);
            }

            CaptureRunPublicationCaptureCompleteCleanupStep tmp = reordered[0];
            reordered[0] = reordered[1];
            reordered[1] = tmp;
            SetField(plan3, "_steps", reordered);
            Assert.That(plan3.IsValid, Is.False);
        }

        [Test]
        public void ObservationCorruption_IsValidFalse()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            Assert.That(plan.IsValid, Is.True);

            // Corrupt publication.plan from Canonical to Absent.
            SetField(plan.OrchestrationResult.InspectionSnapshot.Decision.Snapshot, "_publicationPlan", MakeDoc(PublicationPlan, DocAbsent));

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void OwnerExpiry_IsValidFalse()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(
                commitRoute: true,
                out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.IsValidIndexLocal(token, 0), Is.True);

            owner.Dispose();

            Assert.That(plan.IsValid, Is.False);
            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);
        }

        [Test]
        public void Plan_BuildAndValidate_DoesNotReleaseOwner()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(
                disposeLog,
                out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.IsValidIndexLocal(plan.AcquireValidationToken(), 0), Is.True);
            Assert.That(disposeLog, Is.Empty, "Plan construction and validation must not release the owner.");
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(plan.LockIdentityEvidence.IsIssuedFor(owner), Is.True);
        }

        // ---- Large plan ----

        [Test]
        public void LargePlan_LinearConstruction()
        {
            int count = 500;
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: count);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            // 500 entries * 2 staging steps + frames + publication + 4 tail = 1000 + 6 = 1006.
            Assert.That(plan.Count, Is.EqualTo(count * 2 + 6));
            Assert.That(plan.IsValid, Is.True);
        }

        // ---- Forwarding ----

        [Test]
        public void Plan_Forwarding()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            Assert.That(plan.OrchestrationResult, Is.SameAs(result));
            Assert.That(plan.AuthoritativePlan, Is.SameAs(result.Decision.AuthoritativePlan));
            Assert.That(plan.RootLayout, Is.SameAs(result.RootLayout));
            Assert.That(plan.LockIdentityEvidence, Is.SameAs(result.LockIdentityEvidence));
            Assert.That(plan.TestRunId, Is.EqualTo(result.TestRunId));
            Assert.That(plan.RunInitializationId, Is.EqualTo(result.RunInitializationId));
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupAction.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupActionPlan.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Dispose"));
                Assert.That(source, Does.Not.Contain("Backend"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Bootstrap"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("Enumerable."));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("Reinspect"));
            }
        }

        [Test]
        public void Source_ExactLengthAllocation_NoExternalArrayCtor()
        {
            string planSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupActionPlan.cs"));

            int allocations = CountOccurrences(planSource, "new CaptureRunPublicationCaptureCompleteCleanupStep[");
            Assert.That(allocations, Is.EqualTo(1), "The plan must allocate its step array exactly once.");

            // The constructor must not accept an array.
            Type planType = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan);
            ConstructorInfo[] ctors = planType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (ConstructorInfo ctor in ctors)
            {
                foreach (ParameterInfo parameter in ctor.GetParameters())
                {
                    Assert.That(parameter.ParameterType.IsArray, Is.False, "The plan constructor must not accept an array parameter.");
                }
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }
    }
}
