using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunPublicationArtifactRecoveryExecutionCoordinatorTests
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

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        private static CaptureRunPublicationArtifactRecoveryAction CommitCaptureIndex => CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryAction ReinspectArtifacts => CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts;

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

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
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            PngJsonCapturePublicationPlan plan = null,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            int maximumEntryCount = 4)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
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
                captureIndex: captureIndex);
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
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            return CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(new FakeArtifactInspector(), operation, traceStatus, traceCount, entries)));
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishPlan(
            int entryCount,
            out CaptureRunPublicationArtifactInspectionOperation operation)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(entryCount));
            operation = MakeOperation(plan: plan, maximumEntryCount: entryCount);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation,
                    operation.GetArtifactPaths(i),
                    stagingPngStatus: EvMatchesExpected,
                    stagingPngCount: PngBytes,
                    stagingSidecarStatus: EvMatchesExpected,
                    stagingSidecarCount: SidecarBytes,
                    finalPngStatus: EvAbsent,
                    finalPngCount: 0,
                    finalSidecarStatus: EvAbsent,
                    finalSidecarCount: 0);
            }

            return BuildPlan(operation, entries);
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildCommitPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation)
        {
            operation = MakeOperation();
            observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected,
                finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishPngSidecarPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation)
        {
            operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent,
                finalPngCount: 0,
                finalSidecarStatus: EvAbsent,
                finalSidecarCount: 0);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildOrphanedPreTracePlan()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, operation.GetArtifactPaths(0));
            return BuildPlan(operation, new[] { observation }, CaptureRunPublicationEvidenceStatus.Absent, 0);
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildCaptureCompletePlan()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan), plan: plan);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected,
                finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildArtifactSourceMissingPlan()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvAbsent,
                stagingPngCount: 0,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent,
                finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishedArtifactMissingPlan()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan), plan: plan);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent,
                finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildRunRootCollisionPlan()
        {
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected,
                finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation }, EvMismatch, 100);
        }

        private static CaptureRunPublicationArtifactRecoveryExecutionBatch BuildBatch(
            CaptureRunPublicationArtifactRecoveryActionPlan plan)
        {
            return CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactRecoveryExecutionCoordinatorTests).Assembly.Location);
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

        private sealed class FakePublisher : ICaptureRunPublicationArtifactPublisher
        {
            private readonly List<string> _log;

            public FakePublisher(List<string> log = null) { _log = log; }

            public int Calls;

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunPublicationArtifactPublishOperation, CaptureRunPublicationArtifactPublishReceipt> ReceiptOverride { get; set; }

            public CaptureRunPublicationArtifactPublishReceipt Publish(CaptureRunPublicationArtifactPublishOperation operation)
            {
                Calls++;
                _log?.Add("publish:" + operation.EntryIndex + ":" + operation.ArtifactKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                if (ReceiptOverride != null) return ReceiptOverride(operation);
                return new CaptureRunPublicationArtifactPublishReceipt(this, operation);
            }
        }

        private sealed class FakeCommitter : ICaptureRunCaptureIndexCommitter
        {
            private readonly List<string> _log;

            public FakeCommitter(List<string> log = null) { _log = log; }

            public int Calls;

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunCaptureIndexCommitOperation, CaptureRunCaptureIndexCommitReceipt> ReceiptOverride { get; set; }

            public CaptureRunCaptureIndexCommitReceipt Commit(CaptureRunCaptureIndexCommitOperation operation)
            {
                Calls++;
                _log?.Add("commit:" + operation.Mode);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                if (ReceiptOverride != null) return ReceiptOverride(operation);
                return new CaptureRunCaptureIndexCommitReceipt(this, operation);
            }
        }

        private static CaptureRunPublicationArtifactRecoveryExecutionCoordinator MakeCoordinator(
            ICaptureRunPublicationArtifactPublisher publisher,
            ICaptureRunCaptureIndexCommitter committer)
        {
            return new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(publisher, committer);
        }

        private static CaptureRunPublicationArtifactRecoveryCompletedStep ForgeCompletedStep(
            CaptureRunPublicationArtifactRecoveryCompletedStep template,
            CaptureRunPublicationArtifactPublishReceipt publishReceipt,
            CaptureRunCaptureIndexCommitReceipt commitReceipt)
        {
            CaptureRunPublicationArtifactRecoveryCompletedStep forged =
                (CaptureRunPublicationArtifactRecoveryCompletedStep)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryCompletedStep));
            SetField(forged, "_preparedStep", template.PreparedStep);
            SetField(forged, "_publishReceipt", publishReceipt);
            SetField(forged, "_commitReceipt", commitReceipt);
            return forged;
        }

        private static CaptureRunPublicationArtifactRecoveryCompletedStep[] WithReplaced(
            CaptureRunPublicationArtifactRecoveryExecutionResult result,
            int index,
            CaptureRunPublicationArtifactRecoveryCompletedStep replacement)
        {
            CaptureRunPublicationArtifactRecoveryCompletedStep[] steps =
                new CaptureRunPublicationArtifactRecoveryCompletedStep[result.Count];
            for (int i = 0; i < result.Count; i++)
            {
                steps[i] = i == index ? replacement : result.GetCompletedStep(i);
            }

            return steps;
        }

        private static void AssertResultRejected(
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator,
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch,
            CaptureRunPublicationArtifactRecoveryCompletedStep[] completedSteps)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryExecutionResult(coordinator, batch, completedSteps));

            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        // ---- Constructor / shape ----

        [Test]
        public void Coordinator_NullDependencies_Rejected()
        {
            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();

            ArgumentNullException pubEx = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(null, committer));
            Assert.That(pubEx.ParamName, Is.EqualTo("publisher"));

            ArgumentNullException comEx = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(publisher, null));
            Assert.That(comEx.ParamName, Is.EqualTo("captureIndexCommitter"));
        }

        [Test]
        public void Coordinator_Shape_TwoReadonlyDeps_NotDisposable()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryExecutionCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void StatusEnum_Contract()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryExecutionStatus);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            string[] names = Enum.GetNames(type);
            Assert.That(names, Is.EqualTo(new[]
            {
                "None",
                "ReinspectionRequired",
                "CaptureCompleteCleanupRequired",
                "OrphanedPreTrace",
                "ArtifactSourceMissing",
                "PublishedArtifactMissing",
                "RunRootCollision"
            }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(7));
            for (int i = 0; i < 7; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        [Test]
        public void CompletedStep_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryCompletedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Result_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryExecutionResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        // ---- Status mapping ----

        [Test]
        public void Result_StatusMapping()
        {
            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            Assert.That(coordinator.Execute(BuildBatch(BuildPublishPngSidecarPlan(out _))).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired));
            Assert.That(coordinator.Execute(BuildBatch(BuildCommitPlan(out _, out _))).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired));
            Assert.That(coordinator.Execute(BuildBatch(BuildCaptureCompletePlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired));
            Assert.That(coordinator.Execute(BuildBatch(BuildOrphanedPreTracePlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace));
            Assert.That(coordinator.Execute(BuildBatch(BuildArtifactSourceMissingPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing));
            Assert.That(coordinator.Execute(BuildBatch(BuildPublishedArtifactMissingPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing));
            Assert.That(coordinator.Execute(BuildBatch(BuildRunRootCollisionPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision));
        }

        // ---- Execution order ----

        [Test]
        public void Execute_PublishMultiple_PlanOrderEntryOrderPngThenSidecar_OnceEach()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPlan(3, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(log, Is.EqualTo(new[]
            {
                "publish:0:Png",
                "publish:0:Sidecar",
                "publish:1:Png",
                "publish:1:Sidecar",
                "publish:2:Png",
                "publish:2:Sidecar"
            }));
            Assert.That(publisher.Calls, Is.EqualTo(6));
            Assert.That(committer.Calls, Is.EqualTo(0));

            Assert.That(result.Count, Is.EqualTo(7));
            for (int i = 0; i < 6; i++)
            {
                Assert.That(result.GetCompletedStep(i).PublishReceipt, Is.Not.Null);
                Assert.That(result.GetCompletedStep(i).CommitReceipt, Is.Null);
            }

            // Final step is the Reinspect routing step: no backend, no receipt.
            Assert.That(result.GetCompletedStep(6).PreparedStep.Action, Is.EqualTo(ReinspectArtifacts));
            Assert.That(result.GetCompletedStep(6).PublishReceipt, Is.Null);
            Assert.That(result.GetCompletedStep(6).CommitReceipt, Is.Null);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_Commit_CalledOnce()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(committer.Calls, Is.EqualTo(1));
            Assert.That(publisher.Calls, Is.EqualTo(0));
            Assert.That(log, Is.EqualTo(new[] { "commit:" + batch.GetStep(0).CaptureIndexCommitOperation.Mode }));
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.GetCompletedStep(0).CommitReceipt, Is.Not.Null);
            Assert.That(result.GetCompletedStep(0).PublishReceipt, Is.Null);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_RoutingStop_NoBackendCalls()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan[] plans =
            {
                BuildOrphanedPreTracePlan(),
                BuildCaptureCompletePlan(),
                BuildArtifactSourceMissingPlan(),
                BuildPublishedArtifactMissingPlan(),
                BuildRunRootCollisionPlan()
            };

            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            foreach (CaptureRunPublicationArtifactRecoveryActionPlan plan in plans)
            {
                CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(BuildBatch(plan));
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(result.GetCompletedStep(0).PublishReceipt, Is.Null);
                Assert.That(result.GetCompletedStep(0).CommitReceipt, Is.Null);
            }

            Assert.That(log, Is.Empty, "Routing and stop dispositions must never contact a backend.");
        }

        // ---- Receipt violations ----

        [Test]
        public void Execute_Publish_NullReceipt_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher publisher = new FakePublisher { ReceiptOverride = _ => null };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Publish_ForeignIssuer_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher foreign = new FakePublisher();
            FakePublisher publisher = new FakePublisher
            {
                ReceiptOverride = op => new CaptureRunPublicationArtifactPublishReceipt(foreign, op)
            };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Publish_DifferentOperation_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher publisher = new FakePublisher();
            CaptureRunPublicationArtifactPublishOperation wrongOperation = batch.GetStep(1).PublishOperation;
            publisher.ReceiptOverride = op => new CaptureRunPublicationArtifactPublishReceipt(publisher, wrongOperation);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Publish_ForwardingMismatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            // Forge a receipt bound to the sidecar operation while the step
            // expects the PNG operation: ArtifactKind and paths disagree.
            FakePublisher publisher = new FakePublisher();
            CaptureRunPublicationArtifactPublishOperation sidecarOperation = batch.GetStep(1).PublishOperation;
            publisher.ReceiptOverride = op => new CaptureRunPublicationArtifactPublishReceipt(publisher, sidecarOperation);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Commit_NullReceipt_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakeCommitter committer = new FakeCommitter { ReceiptOverride = _ => null };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Commit_ForeignIssuer_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakeCommitter foreign = new FakeCommitter();
            FakeCommitter committer = new FakeCommitter
            {
                ReceiptOverride = op => new CaptureRunCaptureIndexCommitReceipt(foreign, op)
            };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Commit_DifferentOperation_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakeCommitter committer = new FakeCommitter();
            CaptureRunCaptureIndexCommitOperation wrongOperation = CaptureRunCaptureIndexCommitOperationFactory.Create(
                BuildCommitPlan(out _, out _), 0);
            committer.ReceiptOverride = op => new CaptureRunCaptureIndexCommitReceipt(committer, wrongOperation);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        // ---- Exception propagation / no retry / no rollback ----

        [Test]
        public void Execute_PublisherException_PropagatesIdentical_NoRetry_NoSubsequentSteps()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPlan(2, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            IOException exception = new IOException("publish failed");
            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log) { ExceptionToThrow = exception };
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(log, Is.EqualTo(new[] { "publish:0:Png" }), "No retry and no subsequent steps after an exception.");
        }

        [Test]
        public void Execute_CommitterException_PropagatesIdentical()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            IOException exception = new IOException("commit failed");
            FakeCommitter committer = new FakeCommitter { ExceptionToThrow = exception };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));
            Assert.That(ex, Is.SameAs(exception));
        }

        [Test]
        public void Execute_PartialFailure_NoLeaseDispose()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            List<string> disposeLog = new List<string>();
            CaptureRunPublicationArtifactInspectionOperation op = MakeOperation(disposeLog);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                op, op.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0);
            CaptureRunPublicationArtifactRecoveryActionPlan publishPlan = BuildPlan(op, new[] { observation });
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(publishPlan);

            FakePublisher publisher = new FakePublisher { ExceptionToThrow = new IOException("boom") };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<IOException>(() => coordinator.Execute(batch));
            Assert.That(disposeLog, Is.Empty, "The coordinator must not dispose the lease on failure.");
        }

        [Test]
        public void Execute_PartialFailure_InputGraphUnchanged()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunCaptureIndexCommitOperation commitOperation = batch.GetStep(0).CaptureIndexCommitOperation;
            byte[] before = commitOperation.GetCanonicalBytes();

            FakeCommitter committer = new FakeCommitter { ExceptionToThrow = new IOException("boom") };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(plan.IsValid, Is.True);
            Assert.That(commitOperation.IsValid, Is.True);
            Assert.That(commitOperation.GetCanonicalBytes(), Is.EqualTo(before));
        }

        // ---- Invalid batch rejection ----

        [Test]
        public void Execute_NullBatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("batch"));
        }

        [Test]
        public void Execute_InvalidBatch_Rejected_NoBackendCalls()
        {
            List<string> log = new List<string>();
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakePublisher(log), new FakeCommitter(log));

            CaptureRunPublicationArtifactRecoveryExecutionBatch batch =
                (CaptureRunPublicationArtifactRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryExecutionBatch));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(batch));
            Assert.That(ex.ParamName, Is.EqualTo("batch"));
            Assert.That(log, Is.Empty);
        }

        // ---- Result correlation ----

        [Test]
        public void Result_CompletedSteps_CountOrderPreparedStepReference()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());

            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.Count, Is.EqualTo(batch.Count));
            Assert.That(result.Batch, Is.SameAs(batch));
            Assert.That(result.IssuedBy, Is.SameAs(coordinator));
            Assert.That(result.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(result.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(InitId));

            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunPublicationArtifactRecoveryCompletedStep completed = result.GetCompletedStep(i);
                Assert.That(completed.PreparedStep, Is.SameAs(batch.GetStep(i)));
                if (completed.PreparedStep.Action == PublishArtifact)
                {
                    Assert.That(completed.PublishReceipt.Operation, Is.SameAs(completed.PreparedStep.PublishOperation));
                }
                else
                {
                    Assert.That(completed.PublishReceipt, Is.Null);
                    Assert.That(completed.CommitReceipt, Is.Null);
                }
            }

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_ArrayDefensiveCopy_NotExposed()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());

            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(
                typeof(CaptureRunPublicationArtifactRecoveryExecutionResult)
                    .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunPublicationArtifactRecoveryCompletedStep[])),
                Is.False,
                "The completed-step array must not be exposed.");
        }

        // ---- Result direct-constructor defense ----

        [Test]
        public void Result_DirectConstructor_MissingExtraSwappedForeign_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            CaptureRunPublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);

            CaptureRunPublicationArtifactRecoveryCompletedStep step0 = good.GetCompletedStep(0);
            CaptureRunPublicationArtifactRecoveryCompletedStep step1 = good.GetCompletedStep(1);

            // missing
            AssertResultRejected(coordinator, batch, new[] { step0 });
            // extra
            AssertResultRejected(coordinator, batch, new[] { step0, step1, step0 });
            // swapped
            AssertResultRejected(coordinator, batch, new[] { step1, step0 });

            // foreign prepared step from a different plan
            CaptureRunPublicationArtifactRecoveryExecutionBatch otherBatch = BuildBatch(BuildPublishPngSidecarPlan(out _));
            CaptureRunPublicationArtifactRecoveryCompletedStep foreign = coordinator.Execute(otherBatch).GetCompletedStep(0);
            AssertResultRejected(coordinator, batch, new[] { foreign, step1 });
        }

        [Test]
        public void Result_IsValid_False_ForBrokenValues()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            CaptureRunPublicationArtifactRecoveryExecutionResult nullSteps =
                (CaptureRunPublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryExecutionResult));
            SetField(nullSteps, "_issuedBy", coordinator);
            SetField(nullSteps, "_batch", batch);
            SetField(nullSteps, "_completedSteps", null);
            Assert.That(nullSteps.IsValid, Is.False);

            CaptureRunPublicationArtifactRecoveryExecutionResult nullElement =
                (CaptureRunPublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryExecutionResult));
            SetField(nullElement, "_issuedBy", coordinator);
            SetField(nullElement, "_batch", batch);
            SetField(nullElement, "_completedSteps", new CaptureRunPublicationArtifactRecoveryCompletedStep[] { null, result.GetCompletedStep(1) });
            Assert.That(nullElement.IsValid, Is.False);
        }

        [Test]
        public void Result_DirectConstructor_ForeignIssuer_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            FakePublisher publisher = new FakePublisher();
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());
            CaptureRunPublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunPublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakePublisher foreign = new FakePublisher();
            CaptureRunPublicationArtifactPublishReceipt foreignReceipt =
                new CaptureRunPublicationArtifactPublishReceipt(foreign, original.PublishReceipt.Operation);
            CaptureRunPublicationArtifactRecoveryCompletedStep forged = ForgeCompletedStep(original, foreignReceipt, null);

            AssertResultRejected(coordinator, batch, WithReplaced(good, 0, forged));
        }

        [Test]
        public void Result_DirectConstructor_ForeignCommitterIssuer_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            FakeCommitter committer = new FakeCommitter();
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);
            CaptureRunPublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunPublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakeCommitter foreign = new FakeCommitter();
            CaptureRunCaptureIndexCommitReceipt foreignReceipt =
                new CaptureRunCaptureIndexCommitReceipt(foreign, original.CommitReceipt.Operation);
            CaptureRunPublicationArtifactRecoveryCompletedStep forged = ForgeCompletedStep(original, null, foreignReceipt);

            AssertResultRejected(coordinator, batch, WithReplaced(good, 0, forged));
        }

        // ---- Completed step direct-constructor defense ----

        [Test]
        public void CompletedStep_DirectConstructor_ReceiptKindOperationActionMismatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan publishPlan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch publishBatch = BuildBatch(publishPlan);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken publishToken = publishPlan.AcquireValidationToken();

            CaptureRunPublicationArtifactRecoveryPreparedStep publishStep = publishBatch.GetStep(0);
            CaptureRunPublicationArtifactPublishReceipt publishReceipt =
                new CaptureRunPublicationArtifactPublishReceipt(new FakePublisher(), publishStep.PublishOperation);

            // A publish step must not hold a commit receipt.
            CaptureRunCaptureIndexCommitReceipt strayCommitReceipt =
                new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), BuildBatch(BuildCommitPlan(out _, out _)).GetStep(0).CaptureIndexCommitOperation);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryCompletedStep(publishStep, publishReceipt, strayCommitReceipt, publishToken));

            // A routing step must not hold any receipt.
            CaptureRunPublicationArtifactRecoveryPreparedStep reinspectStep = publishBatch.GetStep(2);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryCompletedStep(reinspectStep, publishReceipt, null, publishToken));

            // A commit step must not hold a publish receipt.
            CaptureRunPublicationArtifactRecoveryActionPlan commitPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch commitBatch = BuildBatch(commitPlan);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken commitToken = commitPlan.AcquireValidationToken();
            CaptureRunPublicationArtifactRecoveryPreparedStep commitStep = commitBatch.GetStep(0);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryCompletedStep(commitStep, publishReceipt, null, commitToken));
        }

        [Test]
        public void CompletedStep_DirectConstructor_NullIssuerReceipt_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            CaptureRunPublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(0);

            CaptureRunPublicationArtifactPublishReceipt brokenReceipt =
                (CaptureRunPublicationArtifactPublishReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactPublishReceipt));
            SetField(brokenReceipt, "_operation", prepared.PublishOperation);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryCompletedStep(prepared, brokenReceipt, null, token));
        }

        [Test]
        public void CompletedStep_DirectConstructor_CrossToken_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryActionPlan other = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(0);
            CaptureRunPublicationArtifactPublishReceipt receipt =
                new CaptureRunPublicationArtifactPublishReceipt(new FakePublisher(), prepared.PublishOperation);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryCompletedStep(
                    prepared, receipt, null, other.AcquireValidationToken()));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void ForgedBrokenReceipt_IsValidFalse_WithoutException()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            CaptureRunPublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunPublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);

            CaptureRunPublicationArtifactPublishReceipt brokenReceipt =
                (CaptureRunPublicationArtifactPublishReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactPublishReceipt));
            SetField(brokenReceipt, "_operation", original.PublishReceipt.Operation);
            CaptureRunPublicationArtifactRecoveryCompletedStep brokenStep = ForgeCompletedStep(original, brokenReceipt, null);

            Assert.That(brokenStep.IsValid, Is.False);

            CaptureRunPublicationArtifactRecoveryExecutionResult brokenResult =
                (CaptureRunPublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryExecutionResult));
            SetField(brokenResult, "_issuedBy", coordinator);
            SetField(brokenResult, "_batch", batch);
            SetField(brokenResult, "_completedSteps", WithReplaced(good, 0, brokenStep));
            Assert.That(brokenResult.IsValid, Is.False);
        }

        [Test]
        public void Result_LeaseExpired_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.IsValid, Is.True);

            result.LockLease.Dispose();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.GetCompletedStep(0).IsValid, Is.False);
        }

        // ---- Linearity ----

        [Test]
        public void Execute_LargePublishBatch_LinearExecution()
        {
            int count = 500;
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPlan(count, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            CaptureRunPublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(publisher.Calls, Is.EqualTo(2 * count));
            Assert.That(committer.Calls, Is.EqualTo(0));
            Assert.That(result.Count, Is.EqualTo(2 * count + 1));
            Assert.That(result.IsValid, Is.True);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryCompletedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionResult.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionCoordinator.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("IdGenerator"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("TraceLogger"));
                Assert.That(source, Does.Not.Contain("TraceRunManifest"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }
        }

        [Test]
        public void Source_LoopNoFullValidationNoSerialize()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionCoordinator.cs"));

            int loopIndex = coordinatorSource.IndexOf("for (int i = 0; i < batch.Count; i++)", StringComparison.Ordinal);
            Assert.That(loopIndex, Is.GreaterThan(0));

            int resultIndex = coordinatorSource.IndexOf("return new CaptureRunPublicationArtifactRecoveryExecutionResult", StringComparison.Ordinal);
            Assert.That(resultIndex, Is.GreaterThan(loopIndex));

            string loopBody = coordinatorSource.Substring(loopIndex, resultIndex - loopIndex);
            Assert.That(loopBody, Does.Not.Contain("batch.IsValid"));
            Assert.That(loopBody, Does.Not.Contain(".IsValid"));
            Assert.That(loopBody, Does.Not.Contain("Serialize"));
            Assert.That(loopBody, Does.Not.Contain("GetCanonicalBytes"));
            Assert.That(loopBody, Does.Not.Contain("AcquireValidationToken"));

            Assert.That(coordinatorSource, Does.Contain("IsValidIndexLocal"));
        }

        [Test]
        public void Source_ForwardingComparisons()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionCoordinator.cs"));

            Assert.That(coordinatorSource, Does.Contain("receipt.EntryIndex != operation.EntryIndex"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ArtifactKind != operation.ArtifactKind"));
            Assert.That(coordinatorSource, Does.Contain("receipt.CaptureFrameId != operation.CaptureFrameId"));
            Assert.That(coordinatorSource, Does.Contain("receipt.SourcePath"));
            Assert.That(coordinatorSource, Does.Contain("receipt.DestinationPath"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ExpectedByteCount"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ExpectedContentSha256"));
            Assert.That(coordinatorSource, Does.Contain("receipt.RootLayout"));
            Assert.That(coordinatorSource, Does.Contain("receipt.TestRunId"));
            Assert.That(coordinatorSource, Does.Contain("receipt.RunInitializationId"));

            Assert.That(coordinatorSource, Does.Contain("receipt.Mode != operation.Mode"));
            Assert.That(coordinatorSource, Does.Contain("receipt.TemporaryPath"));
            Assert.That(coordinatorSource, Does.Contain("receipt.FinalPath"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ByteCount"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ActionPlan"));
        }
    }
}
