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
    public class CaptureRunPublicationCaptureCompleteCleanupContractTests
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

        private static CaptureRunPublicationFramesObservationStatus FramesDirectory => CaptureRunPublicationFramesObservationStatus.Directory;

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

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
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

            FakeInitInspector inspector = new FakeInitInspector(staging, final);
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

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            PngJsonCapturePublicationPlan plan = null,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
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

        private static CaptureRunPublicationArtifactRecoveryExecutionCoordinator MakeExecutionCoordinator(List<string> log = null)
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

        private static CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            int entryCount = 1,
            CaptureRunPublicationEvidenceStatus stagingStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            PngJsonCapturePublicationPlan plan = null)
        {
            plan = plan ?? MakePlan(entries: MakeEntries(entryCount));
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                plan: plan,
                publicationPlanTemporary: publicationPlanTemporary,
                publicationPlan: publicationPlan ?? MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary: captureIndexTemporary,
                stagingFramesStatus: stagingFramesStatus,
                maximumEntryCount: entryCount);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation,
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

        private static CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            int entryCount = 1,
            CaptureRunPublicationEvidenceStatus stagingStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            PngJsonCapturePublicationPlan plan = null)
        {
            plan = plan ?? MakePlan(entries: MakeEntries(entryCount));
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                plan: plan,
                publicationPlanTemporary: publicationPlanTemporary,
                publicationPlan: publicationPlan ?? MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary: captureIndexTemporary,
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan),
                stagingFramesStatus: stagingFramesStatus,
                maximumEntryCount: entryCount);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation,
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

        private static CaptureRunPublicationCaptureCompleteCleanupActionPlan BuildPlan(bool commitRoute)
        {
            return CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(
                commitRoute ? BuildCommitResult() : BuildCaptureCompleteResult());
        }

        private static CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildArtifactResult(
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

        private static CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator MakeCleanupOrchestrator(
            FakePublicationCleanupBackend backend = null)
        {
            return new CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator(
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(
                    backend ?? new FakePublicationCleanupBackend()));
        }

        private static CaptureRunPublicationCaptureCompleteCleanupExecutionResult ForgeExecutionResult(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator issuedBy,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps)
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult forged =
                (CaptureRunPublicationCaptureCompleteCleanupExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionResult));
            SetField(forged, "_issuedBy", issuedBy);
            SetField(forged, "_batch", batch);
            SetField(forged, "_completedSteps", completedSteps);
            return forged;
        }

        private static CaptureRunPublicationPathSet GetPublicationPaths(CaptureRunPublicationCaptureCompleteCleanupActionPlan plan)
        {
            return plan.OrchestrationResult.InspectionSnapshot.Decision.Snapshot.Operation.PublicationPaths;
        }

        private static CaptureRunPublicationCaptureCompleteCleanupOperation MakeOp(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan,
            int stepIndex)
        {
            return new CaptureRunPublicationCaptureCompleteCleanupOperation(
                plan,
                GetPublicationPaths(plan),
                new CaptureRunMarkerPathSet(plan.RootLayout),
                stepIndex);
        }

        private static CaptureRunPublicationCaptureCompleteCleanupOperation MakeOp(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            return new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, stepIndex);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationCaptureCompleteCleanupContractTests).Assembly.Location);
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

        private sealed class FakePublicationCleanupBackend : ICaptureRunPublicationCaptureCompleteCleanupBackend
        {
            private readonly List<string> _log;

            public FakePublicationCleanupBackend(List<string> log = null)
            {
                _log = log;
            }

            public int CallCount { get; private set; }

            public CaptureRunPublicationCaptureCompleteCleanupOperation LastOperation { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public Action ExecuteMutator { get; set; }

            public Func<CaptureRunPublicationCaptureCompleteCleanupOperation, CaptureRunPublicationCaptureCompleteCleanupReceipt> ReceiptOverride { get; set; }

            public CaptureRunPublicationCaptureCompleteCleanupReceipt Execute(CaptureRunPublicationCaptureCompleteCleanupOperation operation)
            {
                CallCount++;
                LastOperation = operation;
                _log?.Add("cleanup:" + operation.StepIndex + ":" + operation.Action);

                ExecuteMutator?.Invoke();

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ReceiptOverride != null)
                {
                    return ReceiptOverride(operation);
                }

                return new CaptureRunPublicationCaptureCompleteCleanupReceipt(this, operation);
            }
        }

        // ---- Null rejection ----

        [Test]
        public void Operation_NullActionPlan_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    null,
                    new CaptureRunPublicationPathSet(layout),
                    new CaptureRunMarkerPathSet(layout),
                    0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_NullPublicationPaths_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, null, new CaptureRunMarkerPathSet(plan.RootLayout), 0));

            Assert.That(ex.ParamName, Is.EqualTo("publicationPaths"));
        }

        [Test]
        public void Operation_NullMarkerPaths_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), null, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        // ---- Plan and path set rejection ----

        [Test]
        public void Operation_InvalidActionPlan_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                (CaptureRunPublicationCaptureCompleteCleanupActionPlan)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan));

            CaptureRunRootLayout layout = MakeLayout();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, new CaptureRunPublicationPathSet(layout), new CaptureRunMarkerPathSet(layout), 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_ForeignPublicationPaths_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet foreign = new CaptureRunPublicationPathSet(MakeLayout(2));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, foreign, new CaptureRunMarkerPathSet(plan.RootLayout), 0));

            Assert.That(ex.ParamName, Is.EqualTo("publicationPaths"));
        }

        [Test]
        public void Operation_ForeignMarkerPaths_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunMarkerPathSet foreign = new CaptureRunMarkerPathSet(MakeLayout(2));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), foreign, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void Operation_EqualButDifferentPublicationPathsInstance_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet differentInstance = new CaptureRunPublicationPathSet(plan.RootLayout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, differentInstance, new CaptureRunMarkerPathSet(plan.RootLayout), 0));

            Assert.That(ex.ParamName, Is.EqualTo("publicationPaths"));
        }

        [Test]
        public void Operation_StepIndexOutOfRange_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            foreach (int index in new[] { -1, plan.Count, plan.Count + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => MakeOp(plan, index));

                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Operation_NullStep_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupStep[] steps = new CaptureRunPublicationCaptureCompleteCleanupStep[plan.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = plan.GetStep(i);
            }

            steps[0] = null;
            SetField(plan, "_steps", steps);

            Assert.Throws<ArgumentException>(() => MakeOp(plan, 0));
        }

        [Test]
        public void Operation_SwappedStep_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupStep[] steps = new CaptureRunPublicationCaptureCompleteCleanupStep[plan.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = plan.GetStep(i);
            }

            CaptureRunPublicationCaptureCompleteCleanupStep tmp = steps[0];
            steps[0] = steps[1];
            steps[1] = tmp;
            SetField(plan, "_steps", steps);

            Assert.Throws<ArgumentException>(() => MakeOp(plan, 0));
        }

        [Test]
        public void Operation_CaptureCompleteReady_Rejected()
        {
            // Commit route step 7 is CaptureCompleteReady (routing step).
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            Assert.That(plan.GetStep(7).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeOp(plan, 7));

            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Operation_UndefinedAction_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            SetField(plan.GetStep(0), "_action", (CaptureRunPublicationCaptureCompleteCleanupAction)999);

            Assert.Throws<ArgumentException>(() => MakeOp(plan, 0));
        }

        [Test]
        public void Operation_ReleasedLease_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            plan.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeOp(plan, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        // ---- Target path mapping ----

        [Test]
        public void TargetPath_CommitRoute_FixedPaths()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(
                plan: planValue,
                publicationPlanTemporary: MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue));
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationArtifactPathSet paths0 = plan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);

            Assert.That(MakeOp(plan, 0).TargetPath, Is.EqualTo(publicationPaths.PublicationPlanTemporaryPath));
            Assert.That(MakeOp(plan, 1).TargetPath, Is.EqualTo(paths0.StagingPngPath));
            Assert.That(MakeOp(plan, 2).TargetPath, Is.EqualTo(paths0.StagingSidecarPath));
            Assert.That(MakeOp(plan, 3).TargetPath, Is.EqualTo(publicationPaths.StagingFramesRoot));
            Assert.That(MakeOp(plan, 4).TargetPath, Is.EqualTo(publicationPaths.PublicationPlanPath));
            Assert.That(MakeOp(plan, 5).TargetPath, Is.EqualTo(markerPaths.StagingReadyPath));
            Assert.That(MakeOp(plan, 6).TargetPath, Is.EqualTo(markerPaths.StagingInitializationPath));
            Assert.That(MakeOp(plan, 7).TargetPath, Is.EqualTo(plan.RootLayout.StagingRunRoot));
        }

        [Test]
        public void TargetPath_CaptureIndexTemporary_FixedPath()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCaptureCompleteResult(
                plan: planValue,
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, planValue));
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);

            Assert.That(MakeOp(plan, 0).TargetPath, Is.EqualTo(publicationPaths.CaptureIndexTemporaryPath));
        }

        [Test]
        public void NoFinalPathsAreTargets()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            HashSet<string> finalPaths = new HashSet<string>(StringComparer.Ordinal)
            {
                publicationPaths.FinalFramesRoot,
                publicationPaths.CaptureIndexTemporaryPath,
                publicationPaths.CaptureIndexPath,
                plan.RootLayout.FinalRunRoot,
                markerPaths.FinalInitializationTemporaryPath,
                markerPaths.FinalInitializationPath,
                markerPaths.FinalReadyTemporaryPath,
                markerPaths.FinalReadyPath
            };

            for (int i = 0; i < plan.Count; i++)
            {
                if (plan.GetStep(i).Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    continue;
                }

                CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, i);
                Assert.That(finalPaths, Does.Not.Contain(op.TargetPath), "Step " + i + " must not target a final path.");
            }
        }

        // ---- Artifact forwarding ----

        [Test]
        public void Artifact_PngSidecar_PathsCountHash()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationArtifactPathSet paths0 = plan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);

            CaptureRunPublicationCaptureCompleteCleanupOperation png = MakeOp(plan, 0);
            Assert.That(png.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            Assert.That(png.EntryIndex, Is.EqualTo(0));
            Assert.That(png.ArtifactKind, Is.EqualTo(Png));
            Assert.That(png.ArtifactPaths, Is.SameAs(paths0));
            Assert.That(png.TargetPath, Is.EqualTo(paths0.StagingPngPath));
            Assert.That(png.ExpectedByteCount, Is.EqualTo(paths0.Entry.PngByteLength));
            Assert.That(png.ExpectedContentSha256, Is.EqualTo(paths0.Entry.PngContentSha256));

            CaptureRunPublicationCaptureCompleteCleanupOperation sidecar = MakeOp(plan, 1);
            Assert.That(sidecar.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(sidecar.ArtifactPaths, Is.SameAs(paths0));
            Assert.That(sidecar.TargetPath, Is.EqualTo(paths0.StagingSidecarPath));
            Assert.That(sidecar.ExpectedByteCount, Is.EqualTo(paths0.Entry.SidecarByteLength));
            Assert.That(sidecar.ExpectedContentSha256, Is.EqualTo(paths0.Entry.SidecarContentSha256));
        }

        [Test]
        public void NonArtifact_NullArtifactPaths_ZeroCount_NullHash()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            for (int i = 2; i <= 6; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, i);

                Assert.That(op.ArtifactKind, Is.EqualTo(CaptureRunPublicationArtifactKind.None));
                Assert.That(op.EntryIndex, Is.EqualTo(-1));
                Assert.That(op.ArtifactPaths, Is.Null);
                Assert.That(op.ExpectedByteCount, Is.EqualTo(0));
                Assert.That(op.ExpectedContentSha256, Is.Null);
            }
        }

        // ---- Forwarding ----

        [Test]
        public void Operation_ForwardsPlanIdentityAndRun()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, publicationPaths, markerPaths, 0);

            Assert.That(op.ActionPlan, Is.SameAs(plan));
            Assert.That(op.PublicationPaths, Is.SameAs(publicationPaths));
            Assert.That(op.MarkerPaths, Is.SameAs(markerPaths));
            Assert.That(op.StepIndex, Is.EqualTo(0));
            Assert.That(op.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(op.AuthoritativePlan, Is.SameAs(plan.AuthoritativePlan));
            Assert.That(op.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(op.LockLease, Is.SameAs(plan.LockLease));
            Assert.That(op.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(op.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(op.IsValid, Is.True);
        }

        // ---- IsValid exception safety ----

        [Test]
        public void Operation_Uninitialized_IsValidFalse()
        {
            CaptureRunPublicationCaptureCompleteCleanupOperation empty =
                (CaptureRunPublicationCaptureCompleteCleanupOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupOperation));

            Assert.That(empty.IsValid, Is.False);
        }

        [Test]
        public void Operation_ReleasedLease_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            Assert.That(op.IsValid, Is.True);

            plan.LockLease.Dispose();

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void Operation_ForeignArtifactPathSet_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);
            Assert.That(op.IsValid, Is.True);

            CaptureRunPublicationCaptureCompleteCleanupActionPlan foreignPlan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCommitResult());
            CaptureRunPublicationArtifactPathSet foreign = foreignPlan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);

            CaptureRunPublicationArtifactInspectionOperation inspection = plan.OrchestrationResult.InspectionSnapshot.Operation;
            CaptureRunPublicationArtifactPathSet[] forged = new CaptureRunPublicationArtifactPathSet[inspection.EntryCount];
            for (int i = 0; i < forged.Length; i++)
            {
                forged[i] = inspection.GetArtifactPaths(i);
            }

            forged[0] = foreign;
            SetField(inspection, "_artifactPaths", forged);

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void Operation_StagingObservationCorrupted_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);
            Assert.That(op.IsValid, Is.True);

            CaptureRunPublicationArtifactEntryObservation observation = plan.OrchestrationResult.InspectionSnapshot.GetEntry(0);
            SetField(observation, "_stagingPngStatus", CaptureRunPublicationEvidenceStatus.Absent);

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void Operation_FinalObservationCorrupted_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);
            Assert.That(op.IsValid, Is.True);

            CaptureRunPublicationArtifactEntryObservation observation = plan.OrchestrationResult.InspectionSnapshot.GetEntry(0);
            SetField(observation, "_finalPngStatus", CaptureRunPublicationEvidenceStatus.Absent);

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void Operation_CorruptedPublicationPathSet_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet source = GetPublicationPaths(plan);
            CaptureRunPublicationPathSet forged = ForgePublicationPathSet(source, "_publicationPlanPath", source.CaptureIndexPath);

            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);
            SetField(op, "_publicationPaths", forged);

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void Operation_CorruptedMarkerPathSet_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunMarkerPathSet forged = ForgeMarkerPathSet(source, "_stagingReadyPath", source.StagingInitializationPath);

            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);
            SetField(op, "_markerPaths", forged);

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void CrossToken_Observation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan planA = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan planB =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCommitResult());

            CaptureRunPublicationArtifactEntryObservation observationA = planA.OrchestrationResult.InspectionSnapshot.GetEntry(0);
            CaptureRunPublicationArtifactInspectionOperation operationB = planB.OrchestrationResult.InspectionSnapshot.Operation;
            CaptureRunPublicationArtifactInspectionOperation.ValidationToken tokenB = operationB.AcquireValidationToken();

            Assert.That(observationA.IsValidIndexLocal(tokenB), Is.False);
        }

        // ---- Receipt ----

        [Test]
        public void Receipt_NullIssuer_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupReceipt(null, op));

            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void Receipt_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupReceipt(new FakePublicationCleanupBackend(), null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_InvalidOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            plan.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupReceipt(new FakePublicationCleanupBackend(), op));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_HoldsReferencesAndForwards()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = new CaptureRunPublicationCaptureCompleteCleanupReceipt(backend, op);

            Assert.That(receipt.IssuedBy, Is.SameAs(backend));
            Assert.That(receipt.Operation, Is.SameAs(op));
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.ActionPlan, Is.SameAs(plan));
            Assert.That(receipt.StepIndex, Is.EqualTo(0));
            Assert.That(receipt.Step, Is.SameAs(op.Step));
            Assert.That(receipt.Action, Is.EqualTo(op.Action));
            Assert.That(receipt.EntryIndex, Is.EqualTo(op.EntryIndex));
            Assert.That(receipt.ArtifactKind, Is.EqualTo(op.ArtifactKind));
            Assert.That(receipt.TargetPath, Is.EqualTo(op.TargetPath));
            Assert.That(receipt.RootLayout, Is.SameAs(op.RootLayout));
            Assert.That(receipt.LockLease, Is.SameAs(op.LockLease));
            Assert.That(receipt.TestRunId, Is.EqualTo(op.TestRunId));
            Assert.That(receipt.RunInitializationId, Is.EqualTo(op.RunInitializationId));
        }

        [Test]
        public void Receipt_IsIssuedFor_TrueForMatchingOnly()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(op);

            Assert.That(receipt.IsIssuedFor(backend, op), Is.True);
            Assert.That(receipt.IsIssuedFor(new FakePublicationCleanupBackend(), op), Is.False);
            Assert.That(receipt.IsIssuedFor(backend, MakeOp(plan, 1)), Is.False);
        }

        [Test]
        public void Receipt_Uninitialized_IsValidFalse()
        {
            CaptureRunPublicationCaptureCompleteCleanupReceipt empty =
                (CaptureRunPublicationCaptureCompleteCleanupReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupReceipt));

            Assert.That(empty.IsValid, Is.False);
            Assert.That(empty.IsIssuedFor(new FakePublicationCleanupBackend(), null), Is.False);
        }

        // ---- Fake backend ----

        [Test]
        public void FakeBackend_ReturnsReceiptForSameOperation()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(op);

            Assert.That(backend.CallCount, Is.EqualTo(1));
            Assert.That(backend.LastOperation, Is.SameAs(op));
            Assert.That(receipt.IssuedBy, Is.SameAs(backend));
            Assert.That(receipt.Operation, Is.SameAs(op));
        }

        [Test]
        public void FakeBackend_ForeignIssuerReceipt_Detected()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            FakePublicationCleanupBackend foreign = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            backend.ReceiptOverride = _ => new CaptureRunPublicationCaptureCompleteCleanupReceipt(foreign, _);
            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(op);

            Assert.That(receipt.IsIssuedFor(backend, op), Is.False);
        }

        [Test]
        public void FakeBackend_DifferentOperationReceipt_Detected()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op1 = MakeOp(plan, 0);
            CaptureRunPublicationCaptureCompleteCleanupOperation op2 = MakeOp(plan, 1);

            backend.ReceiptOverride = _ => new CaptureRunPublicationCaptureCompleteCleanupReceipt(backend, op2);
            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(op1);

            Assert.That(receipt.IsIssuedFor(backend, op1), Is.False);
        }

        [Test]
        public void FakeBackend_NullReceipt_Detected()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            backend.ReceiptOverride = _ => null;
            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(op);

            Assert.That(receipt, Is.Null, "A backend must never return a null receipt.");
        }

        [Test]
        public void FakeBackend_Exception_NotTransformedOrRetried()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend { ExceptionToThrow = new IOException("boom") };
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, 0);

            IOException ex = Assert.Throws<IOException>(() => backend.Execute(op));

            Assert.That(ex.Message, Is.EqualTo("boom"));
            Assert.That(backend.CallCount, Is.EqualTo(1));
        }

        // ---- Large plan ----

        [Test]
        public void LargePlan_OperationConstruction()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 500);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            // The last staging step is the sidecar of the last entry.
            int lastStagingStep = 500 * 2 - 1;
            CaptureRunPublicationCaptureCompleteCleanupOperation op = MakeOp(plan, lastStagingStep);

            Assert.That(op.IsValid, Is.True);
            Assert.That(op.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            Assert.That(op.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(op.EntryIndex, Is.EqualTo(499));
        }

        // ---- Plan validation token ----

        [Test]
        public void PlanToken_Acquire_IssuedForPlanOnly()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);

            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(token.IsIssuedFor(plan), Is.True);
            Assert.That(token.IsIssuedFor(other), Is.False);
            Assert.That(token.IsIssuedFor(null), Is.False);
            Assert.That(token.InspectionToken, Is.Not.Null);
            Assert.That(token.ExecutionResultToken, Is.Not.Null);
        }

        [Test]
        public void TrustedConstructor_BuildsAllSteps_SharedToken()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 500);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            int built = 0;
            for (int i = 0; i < plan.Count; i++)
            {
                if (plan.GetStep(i).Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    continue;
                }

                CaptureRunPublicationCaptureCompleteCleanupOperation op =
                    new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, i, token);

                Assert.That(op.StepIndex, Is.EqualTo(i));
                built++;
            }

            Assert.That(built, Is.EqualTo(500 * 2 + 5));

            // Spot-check full re-validation on a few operations only, so the
            // shared-token batch path stays linear in the total step count.
            Assert.That(new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 999, token).IsValid, Is.True);
            Assert.That(new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 1004, token).IsValid, Is.True);
        }

        [Test]
        public void TrustedConstructor_NullToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, null));

            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void TrustedConstructor_CrossToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken foreign = other.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, foreign));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void TrustedConstructor_StaleToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            plan.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void TrustedConstructor_StepSwapAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupStep[] steps = new CaptureRunPublicationCaptureCompleteCleanupStep[plan.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = plan.GetStep(i);
            }

            steps[0] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.None);
            SetField(plan, "_steps", steps);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void TrustedConstructor_StepReorderAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupStep[] steps = new CaptureRunPublicationCaptureCompleteCleanupStep[plan.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = plan.GetStep(i);
            }

            CaptureRunPublicationCaptureCompleteCleanupStep tmp = steps[0];
            steps[0] = steps[1];
            steps[1] = tmp;
            SetField(plan, "_steps", steps);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void TrustedConstructor_NullStepsAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(plan, "_steps", null);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void TrustedConstructor_NullSnapshotEntriesAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(plan.OrchestrationResult.InspectionSnapshot, "_entries", null);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void TrustedConstructor_NullArtifactPathsAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(plan.OrchestrationResult.InspectionSnapshot.Operation, "_artifactPaths", null);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void Token_HasNoStepArrayExposure()
        {
            Type tokenType = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken);

            Assert.That(
                tokenType.GetProperty("Steps", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(
                tokenType.GetField("Steps", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(
                tokenType.GetMethod("get_Steps", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);

            foreach (PropertyInfo property in tokenType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(property.PropertyType.IsArray, Is.False, "The token must not expose any array property.");
            }

            foreach (FieldInfo field in tokenType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType.IsArray)
                {
                    Assert.That(field.IsPrivate, Is.True, "Any array field on the token must be private (a defensive snapshot).");
                }
            }
        }

        [Test]
        public void TrustedConstructor_InPlaceStepReplacementAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            // Replace only the element inside the same array object; the array
            // reference itself is unchanged, so an array-level identity check
            // would not notice, but the per-index snapshot must.
            CaptureRunPublicationCaptureCompleteCleanupStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupStep[])GetField(plan, "_steps");
            steps[0] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.None);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void Token_HasNoUnvalidatedMintApi()
        {
            Type planTokenType = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken);
            Type inspectionTokenType = typeof(CaptureRunPublicationArtifactInspectionOperation.ValidationToken);

            Assert.That(planTokenType.GetMethod("AcquireTrusted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Null);
            Assert.That(inspectionTokenType.GetMethod("AcquireTrusted", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Null);
            Assert.That(inspectionTokenType.GetMethod("TryAcquireViaProof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Null);

            // The two-step proof mint must not exist; the inspection token's
            // only non-validating mint is the proof-gated TryAcquireFromValidatedPlan.
            Assert.That(
                typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan).GetNestedType("ValidationProof", BindingFlags.Public | BindingFlags.NonPublic),
                Is.Null);

            Assert.That(planTokenType.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
            Assert.That(inspectionTokenType.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
            Assert.That(inspectionTokenType.GetMethod("TryAcquireFromValidatedPlan", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
        }

        [Test]
        public void InspectionToken_MintRejectsCorruptedOperation()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationArtifactInspectionOperation operation = plan.OrchestrationResult.InspectionSnapshot.Operation;

            // Corrupt the operation; the full plan validation must fail, so the
            // atomic mint path issues no inspection token.
            SetField(operation, "_artifactPaths", null);

            Assert.That(plan.TryValidate(out _), Is.False);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void InspectionToken_NonValidatedMintRejectsNullProof()
        {
            Assert.That(
                CaptureRunPublicationArtifactInspectionOperation.ValidationToken.TryAcquireFromValidatedPlan(null, out _),
                Is.False);
        }

        [Test]
        public void TrustedConstructor_StepFieldMutationAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            // Mutate the same step instance's three fields into a different
            // valid combination; the reference is unchanged, so only a value
            // snapshot can detect this. Step 0 is a staging-artifact step, so
            // a publication-plan deletion is a valid but different combination.
            SetField(plan.GetStep(0), "_action", CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan);
            SetField(plan.GetStep(0), "_entryIndex", -1);
            SetField(plan.GetStep(0), "_artifactKind", CaptureRunPublicationArtifactKind.None);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void TrustedConstructor_NullIssuedStepsAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            // Forge the token's snapshot array away; the index-local predicate
            // must fail closed instead of leaking a NullReferenceException.
            SetField(token, "_issuedSteps", null);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), 0, token));
        }

        [Test]
        public void TrustedConstructor_CorruptedOrchestrationResultInternalAfterToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            // Corrupt the orchestration result's internal execution result
            // reference after issuance; the index-local check must fail closed
            // without leaking a NullReferenceException.
            SetField(plan.OrchestrationResult, "_executionResult", null);

            Assert.That(plan.IsValidIndexLocal(token, 0), Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Source_NoUnconditionalCatchInPredicates()
        {
            string operationSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOperation.cs"));
            string receiptSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupReceipt.cs"));

            Assert.That(receiptSource, Does.Not.Contain("catch"));
            Assert.That(operationSource, Does.Not.Contain("catch {"));
            Assert.That(operationSource, Does.Not.Contain("catch\n"));
        }

        // ---- Shape ----

        [Test]
        public void Operation_Shape_FourReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));

            int planFields = 0;
            int publicationPathFields = 0;
            int markerPathFields = 0;
            int intFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan)) planFields++;
                else if (field.FieldType == typeof(CaptureRunPublicationPathSet)) publicationPathFields++;
                else if (field.FieldType == typeof(CaptureRunMarkerPathSet)) markerPathFields++;
                else if (field.FieldType == typeof(int)) intFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(planFields, Is.EqualTo(1));
            Assert.That(publicationPathFields, Is.EqualTo(1));
            Assert.That(markerPathFields, Is.EqualTo(1));
            Assert.That(intFields, Is.EqualTo(1));
        }

        [Test]
        public void Receipt_Shape_TwoReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));

            int backendFields = 0;
            int operationFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(ICaptureRunPublicationCaptureCompleteCleanupBackend)) backendFields++;
                else if (field.FieldType == typeof(CaptureRunPublicationCaptureCompleteCleanupOperation)) operationFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(backendFields, Is.EqualTo(1));
            Assert.That(operationFields, Is.EqualTo(1));
        }

        [Test]
        public void Backend_IsInterface_SingleMethod()
        {
            Type type = typeof(ICaptureRunPublicationCaptureCompleteCleanupBackend);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Has.Length.EqualTo(1));
        }

        [Test]
        public void NoMutableStaticState()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunPublicationCaptureCompleteCleanupOperation),
                typeof(CaptureRunPublicationCaptureCompleteCleanupReceipt)
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
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationCaptureCompleteCleanupBackend.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupReceipt.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("System.IO"));
            }
        }

        [Test]
        public void Source_OperationHasNoBackendReference()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOperation.cs"));

            Assert.That(source, Does.Not.Contain("Backend"));
        }

        [Test]
        public void BackendXml_MentionsContractKeywords()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationCaptureCompleteCleanupBackend.cs"));

            Assert.That(source, Does.Contain("no-follow"));
            Assert.That(source, Does.Contain("non-recursively"));
            Assert.That(source, Does.Contain("durably flushes"));
            Assert.That(source, Does.Contain("re-inspects"));
            Assert.That(source, Does.Contain("issued by this backend"));
            Assert.That(source, Does.Contain("ReferenceEquals(receipt.IssuedBy, this)"));
            Assert.That(source, Does.Contain("foreign issuer"));
            Assert.That(source, Does.Contain("fail-closed"));
        }

        // ---- Execution batch: construction helpers ----

        private static CaptureRunPublicationCaptureCompleteCleanupActionPlan BuildCommitPlanWithPublicationPlanTemporary()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(
                plan: planValue,
                publicationPlanTemporary: MakeDoc(
                    CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue));
            return CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
        }

        private static CaptureRunPublicationCaptureCompleteCleanupActionPlan BuildCaptureCompletePlanWithCaptureIndexTemporary()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCaptureCompleteResult(
                plan: planValue,
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, planValue));
            return CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
        }

        private static CaptureRunPublicationCaptureCompleteCleanupExecutionBatch BuildBatch(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan)
        {
            return CaptureRunPublicationCaptureCompleteCleanupExecutionBatchBuilder.Build(plan);
        }

        // ---- Execution batch: route order ----

        [Test]
        public void Batch_CommitRoute_StepsMatchPlanOrder()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            CaptureRunPublicationCaptureCompleteCleanupAction[] expected =
            {
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady
            };

            Assert.That(batch.Count, Is.EqualTo(expected.Length));
            Assert.That(batch.IsValid, Is.True);

            for (int i = 0; i < expected.Length; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                Assert.That(prepared.Action, Is.EqualTo(expected[i]), "step " + i);
                Assert.That(prepared.StepIndex, Is.EqualTo(i));
                Assert.That(prepared.Action, Is.EqualTo(plan.GetStep(i).Action));
                Assert.That(prepared.ActionPlan, Is.SameAs(plan));
            }
        }

        [Test]
        public void Batch_CaptureCompleteRoute_StepsMatchPlanOrder()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCaptureCompletePlanWithCaptureIndexTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            CaptureRunPublicationCaptureCompleteCleanupAction[] expected =
            {
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady
            };

            Assert.That(batch.Count, Is.EqualTo(expected.Length));
            Assert.That(batch.IsValid, Is.True);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(batch.GetStep(i).Action, Is.EqualTo(expected[i]), "step " + i);
                Assert.That(batch.GetStep(i).StepIndex, Is.EqualTo(i));
            }
        }

        [Test]
        public void Batch_DefaultRoutes_StepsMatchPlanOrder()
        {
            CaptureRunPublicationCaptureCompleteCleanupAction[] expected =
            {
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady
            };

            foreach (bool commitRoute in new[] { true, false })
            {
                CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildPlan(commitRoute));

                Assert.That(batch.Count, Is.EqualTo(expected.Length), "commitRoute=" + commitRoute);
                Assert.That(batch.IsValid, Is.True);

                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.That(batch.GetStep(i).Action, Is.EqualTo(expected[i]), "step " + i);
                }
            }
        }

        [Test]
        public void Batch_NoArtifactCleanup_StepsMatchPlanOrder()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(
                plan: planValue,
                stagingStatus: CaptureRunPublicationEvidenceStatus.Absent,
                publicationPlanTemporary: MakeDoc(
                    CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue));
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            CaptureRunPublicationCaptureCompleteCleanupAction[] expected =
            {
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady
            };

            Assert.That(batch.Count, Is.EqualTo(expected.Length));
            Assert.That(batch.IsValid, Is.True);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(batch.GetStep(i).Action, Is.EqualTo(expected[i]), "step " + i);
            }
        }

        [Test]
        public void PreparedStep_ExclusiveOperationPerAction()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan commit = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupActionPlan captureComplete = BuildCaptureCompletePlanWithCaptureIndexTemporary();

            HashSet<CaptureRunPublicationCaptureCompleteCleanupAction> sideEffecting =
                new HashSet<CaptureRunPublicationCaptureCompleteCleanupAction>();
            int readySteps = 0;
            int operations = 0;

            foreach (CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch in new[]
            {
                BuildBatch(commit),
                BuildBatch(captureComplete)
            })
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                    if (prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                    {
                        readySteps++;
                        Assert.That(prepared.CleanupOperation, Is.Null);
                    }
                    else
                    {
                        operations++;
                        Assert.That(prepared.CleanupOperation, Is.Not.Null, "step " + i);
                        Assert.That(prepared.CleanupOperation.Action, Is.EqualTo(prepared.Action), "step " + i);
                        sideEffecting.Add(prepared.Action);
                    }
                }
            }

            Assert.That(readySteps, Is.EqualTo(2));
            Assert.That(operations, Is.EqualTo(16));
            Assert.That(sideEffecting.Count, Is.EqualTo(8));

            foreach (CaptureRunPublicationCaptureCompleteCleanupAction action in new[]
            {
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker,
                CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot
            })
            {
                Assert.That(sideEffecting, Does.Contain(action));
            }
        }

        [Test]
        public void Batch_AllSideEffectOperations_FullyCorrelated()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            CaptureRunPublicationPathSet publicationPaths = batch.GetStep(0).PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = batch.GetStep(0).MarkerPaths;

            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                CaptureRunPublicationCaptureCompleteCleanupStep step = plan.GetStep(i);

                Assert.That(prepared.ActionPlan, Is.SameAs(plan));
                Assert.That(prepared.PublicationPaths, Is.SameAs(publicationPaths));
                Assert.That(prepared.MarkerPaths, Is.SameAs(markerPaths));
                Assert.That(prepared.StepIndex, Is.EqualTo(i));
                Assert.That(prepared.Action, Is.EqualTo(step.Action));
                Assert.That(prepared.Step.EntryIndex, Is.EqualTo(step.EntryIndex));
                Assert.That(prepared.Step.ArtifactKind, Is.EqualTo(step.ArtifactKind));

                if (prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    Assert.That(prepared.CleanupOperation, Is.Null);
                }
                else
                {
                    CaptureRunPublicationCaptureCompleteCleanupOperation op = prepared.CleanupOperation;
                    Assert.That(op, Is.Not.Null);
                    Assert.That(op.ActionPlan, Is.SameAs(plan));
                    Assert.That(op.PublicationPaths, Is.SameAs(publicationPaths));
                    Assert.That(op.MarkerPaths, Is.SameAs(markerPaths));
                    Assert.That(op.StepIndex, Is.EqualTo(i));
                    Assert.That(op.Action, Is.EqualTo(step.Action));
                    Assert.That(op.EntryIndex, Is.EqualTo(step.EntryIndex));
                    Assert.That(op.ArtifactKind, Is.EqualTo(step.ArtifactKind));
                    Assert.That(op.TargetPath, Is.Not.Null.And.Not.Empty);
                }
            }
        }

        [Test]
        public void Batch_SharedPathSetsAcrossAllSteps()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            CaptureRunPublicationPathSet publicationPaths = batch.GetStep(0).PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = batch.GetStep(0).MarkerPaths;

            Assert.That(publicationPaths, Is.Not.Null);
            Assert.That(markerPaths, Is.Not.Null);
            Assert.That(publicationPaths.RootLayout, Is.SameAs(batch.RootLayout));
            Assert.That(markerPaths.RootLayout, Is.SameAs(batch.RootLayout));

            for (int i = 1; i < batch.Count; i++)
            {
                Assert.That(batch.GetStep(i).PublicationPaths, Is.SameAs(publicationPaths), "step " + i);
                Assert.That(batch.GetStep(i).MarkerPaths, Is.SameAs(markerPaths), "step " + i);
            }
        }

        // ---- Execution batch: rejection ----

        [Test]
        public void Builder_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationCaptureCompleteCleanupExecutionBatchBuilder.Build(null));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionBatch(null));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_InvalidPlan_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                (CaptureRunPublicationCaptureCompleteCleanupActionPlan)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionBatch(plan));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_ReleasedLease_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            plan.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionBatch(plan));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_GetStep_OutOfRange_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildPlan(commitRoute: true));

            foreach (int index in new[] { -1, batch.Count, batch.Count + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => batch.GetStep(index));

                Assert.That(ex.ParamName, Is.EqualTo("index"));
            }
        }

        [Test]
        public void PreparedStep_NullArguments_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    null, publicationPaths, markerPaths, 0, plan.AcquireValidationToken()));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));

            ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, null, markerPaths, 0, plan.AcquireValidationToken()));
            Assert.That(ex.ParamName, Is.EqualTo("publicationPaths"));

            ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, publicationPaths, null, 0, plan.AcquireValidationToken()));
            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));

            ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, publicationPaths, markerPaths, 0, null));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void PreparedStep_IndexOutOfRange_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            foreach (int index in new[] { -1, plan.Count, plan.Count + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                        plan, publicationPaths, markerPaths, index, plan.AcquireValidationToken()));

                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void PreparedStep_CrossToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken foreign = other.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan,
                    GetPublicationPaths(plan),
                    new CaptureRunMarkerPathSet(plan.RootLayout),
                    0,
                    foreign));

            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void PreparedStep_StaleToken_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            plan.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan,
                    GetPublicationPaths(plan),
                    new CaptureRunMarkerPathSet(plan.RootLayout),
                    0,
                    token));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        // ---- Execution batch: lease liveness and corruption ----

        [Test]
        public void Batch_LeaseReleased_InvalidatesAll()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            Assert.That(batch.IsValid, Is.True);
            Assert.That(batch.GetStep(0).IsValid, Is.True);
            Assert.That(batch.GetStep(batch.Count - 1).IsValid, Is.True);

            batch.LockLease.Dispose();

            Assert.That(batch.IsValid, Is.False);
            Assert.That(batch.GetStep(0).IsValid, Is.False);
            Assert.That(batch.GetStep(batch.Count - 1).IsValid, Is.False);
        }

        [Test]
        public void Batch_StepsArrayCorruption_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] original =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");

            // Null array.
            Assert.That(ForgeBatch(plan, null).IsValid, Is.False);

            // Null element.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] withNull =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            withNull[0] = null;
            Assert.That(ForgeBatch(plan, withNull).IsValid, Is.False);

            // Shorter array.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] shorter =
                new CaptureRunPublicationCaptureCompleteCleanupPreparedStep[original.Length - 1];
            Array.Copy(original, shorter, shorter.Length);
            Assert.That(ForgeBatch(plan, shorter).IsValid, Is.False);

            // Longer array.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] longer =
                new CaptureRunPublicationCaptureCompleteCleanupPreparedStep[original.Length + 1];
            Array.Copy(original, longer, original.Length);
            Assert.That(ForgeBatch(plan, longer).IsValid, Is.False);

            // Reordered elements.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] reordered =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            reordered[0] = original[1];
            reordered[1] = original[0];
            Assert.That(ForgeBatch(plan, reordered).IsValid, Is.False);

            // Element replaced with a foreign prepared step.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] replaced =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            replaced[0] = BuildBatch(BuildPlan(commitRoute: true)).GetStep(0);
            Assert.That(ForgeBatch(plan, replaced).IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_CorruptedIndex_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            // Step 0 holds step 1's operation: the index no longer correlates.
            CaptureRunPublicationCaptureCompleteCleanupOperation foreign =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 1, token);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep forged =
                ForgePreparedStep(plan, publicationPaths, markerPaths, 0, foreign);

            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_CorruptedPlanOrPathSetOrOperation_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);

            // Different plan reference.
            Assert.That(ForgePreparedStep(other, GetPublicationPaths(other), new CaptureRunMarkerPathSet(other.RootLayout), 0, op).IsValid, Is.False);

            // Different publication path set reference.
            CaptureRunPublicationPathSet foreignPaths = new CaptureRunPublicationPathSet(plan.RootLayout);
            Assert.That(ForgePreparedStep(plan, foreignPaths, markerPaths, 0, op).IsValid, Is.False);

            // Different marker path set reference.
            CaptureRunMarkerPathSet foreignMarkers = new CaptureRunMarkerPathSet(plan.RootLayout);
            Assert.That(ForgePreparedStep(plan, publicationPaths, foreignMarkers, 0, op).IsValid, Is.False);

            // Different operation instance for the same index (step 1's op).
            CaptureRunPublicationCaptureCompleteCleanupOperation foreignOp =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 1, token);
            Assert.That(ForgePreparedStep(plan, publicationPaths, markerPaths, 0, foreignOp).IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_RoutingStepWithInjectedOperation_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            int readyIndex = plan.Count - 1;
            Assert.That(plan.GetStep(readyIndex).Action,
                Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep forged =
                ForgePreparedStep(plan, publicationPaths, markerPaths, readyIndex, op);

            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_SideEffectStepMissingOperation_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep forged =
                ForgePreparedStep(plan, publicationPaths, markerPaths, 0, null);

            Assert.That(forged.IsValid, Is.False);
        }

        // ---- Execution batch: full token-gated correlation ----

        [Test]
        public void PreparedStep_SideEffect_ObservationCorruptedAfterToken_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);

            Assert.That(prepared.IsValidIndexLocal(token), Is.True);

            CaptureRunPublicationArtifactEntryObservation observation =
                plan.OrchestrationResult.InspectionSnapshot.GetEntry(0);
            SetField(observation, "_stagingPngStatus", CaptureRunPublicationEvidenceStatus.Absent);

            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
            Assert.That(prepared.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_SideEffect_PathSetCorruptedAfterToken_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);

            Assert.That(prepared.IsValidIndexLocal(token), Is.True);

            SetField(GetPublicationPaths(plan), "_publicationPlanPath", null);

            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
            Assert.That(prepared.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_SideEffect_OperationInternalSwapAfterToken_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared =
                ForgePreparedStep(plan, publicationPaths, markerPaths, 0, op);

            Assert.That(prepared.IsValidIndexLocal(token), Is.True);

            SetField(op, "_publicationPaths", new CaptureRunPublicationPathSet(plan.RootLayout));

            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
            Assert.That(prepared.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_Routing_ForeignPathSet_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            int readyIndex = plan.Count - 1;

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep forged =
                ForgePreparedStep(plan, new CaptureRunPublicationPathSet(plan.RootLayout), markerPaths, readyIndex, null);

            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_Routing_InvalidPathSet_IsInvalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            int readyIndex = plan.Count - 1;

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep forged =
                ForgePreparedStep(plan, publicationPaths, markerPaths, readyIndex, null);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(forged.IsValidIndexLocal(token), Is.True);

            SetField(publicationPaths, "_publicationPlanPath", null);

            Assert.That(forged.IsValidIndexLocal(token), Is.False);
            Assert.That(forged.IsValid, Is.False);
        }

        // ---- Execution batch: routing constructor rejection ----

        [Test]
        public void PreparedStep_Routing_ForeignPublicationPathSet_ConstructorRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            CaptureRunPublicationPathSet foreign = new CaptureRunPublicationPathSet(plan.RootLayout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, foreign, new CaptureRunMarkerPathSet(plan.RootLayout), readyIndex, token));

            Assert.That(ex.ParamName, Is.EqualTo("publicationPaths"));
        }

        [Test]
        public void PreparedStep_Routing_CorruptedPublicationPathSet_ConstructorRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            CaptureRunPublicationPathSet corrupted = GetPublicationPaths(plan);
            SetField(corrupted, "_publicationPlanPath", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, corrupted, new CaptureRunMarkerPathSet(plan.RootLayout), readyIndex, token));

            Assert.That(ex.ParamName, Is.EqualTo("publicationPaths"));
        }

        [Test]
        public void PreparedStep_Routing_ForeignMarkerPathSet_ConstructorRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            CaptureRunMarkerPathSet foreign = new CaptureRunMarkerPathSet(MakeLayout(2));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, GetPublicationPaths(plan), foreign, readyIndex, token));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void PreparedStep_Routing_CorruptedMarkerPathSet_ConstructorRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            CaptureRunMarkerPathSet corrupted = ForgeMarkerPathSet(
                new CaptureRunMarkerPathSet(plan.RootLayout), "_stagingReadyPath", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, GetPublicationPaths(plan), corrupted, readyIndex, token));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void PreparedStep_Routing_ReleasedLease_ConstructorRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            plan.LockLease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, GetPublicationPaths(plan), new CaptureRunMarkerPathSet(plan.RootLayout), readyIndex, token));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        // ---- Execution batch: execution result chain corruption ----

        [Test]
        public void PreparedStep_ExecutionResultBatchNullAfterToken_FailsClosed()
        {
            AssertExecutionResultChainCorruptionFailsClosed(
                plan => SetField(plan.OrchestrationResult.ExecutionResult, "_batch", null));
        }

        [Test]
        public void PreparedStep_ExecutionBatchActionPlanNullAfterToken_FailsClosed()
        {
            AssertExecutionResultChainCorruptionFailsClosed(
                plan => SetField(plan.OrchestrationResult.ExecutionResult.Batch, "_actionPlan", null));
        }

        [Test]
        public void PreparedStep_RecoveryActionPlanDecisionNullAfterToken_FailsClosed()
        {
            AssertExecutionResultChainCorruptionFailsClosed(
                plan => SetField(plan.OrchestrationResult.ExecutionResult.Batch.ActionPlan, "_decision", null));
        }

        private static void AssertExecutionResultChainCorruptionFailsClosed(
            Action<CaptureRunPublicationCaptureCompleteCleanupActionPlan> corrupt)
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);

            corrupt(plan);

            // Side-effecting prepared step and operation fail closed without
            // leaking a NullReferenceException.
            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
            Assert.That(op.IsValidIndexLocal(token), Is.False);

            // The routing constructor rejects with the action plan parameter.
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, publicationPaths, markerPaths, readyIndex, token));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_TryValidate_NullTokenOnEveryFailure()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] original =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");

            // Length mismatch (shorter).
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] shorter =
                new CaptureRunPublicationCaptureCompleteCleanupPreparedStep[original.Length - 1];
            Array.Copy(original, shorter, shorter.Length);
            AssertTryValidateFailsWithNullToken(ForgeBatch(plan, shorter));

            // Length mismatch (longer).
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] longer =
                new CaptureRunPublicationCaptureCompleteCleanupPreparedStep[original.Length + 1];
            Array.Copy(original, longer, original.Length);
            AssertTryValidateFailsWithNullToken(ForgeBatch(plan, longer));

            // Empty array.
            AssertTryValidateFailsWithNullToken(
                ForgeBatch(plan, new CaptureRunPublicationCaptureCompleteCleanupPreparedStep[0]));

            // Null element.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] withNull =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            withNull[0] = null;
            AssertTryValidateFailsWithNullToken(ForgeBatch(plan, withNull));

            // Reordered elements.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] reordered =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            reordered[0] = original[1];
            reordered[1] = original[0];
            AssertTryValidateFailsWithNullToken(ForgeBatch(plan, reordered));

            // First step with a null publication path set: the plan token was
            // already acquired, so the later path set check must null it.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] nullPaths =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            nullPaths[0] = ForgePreparedStep(plan, null, batch.GetStep(0).MarkerPaths, 0, null);
            AssertTryValidateFailsWithNullToken(ForgeBatch(plan, nullPaths));

            // First step with a null marker path set.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] nullMarkers =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            nullMarkers[0] = ForgePreparedStep(plan, batch.GetStep(0).PublicationPaths, null, 0, null);
            AssertTryValidateFailsWithNullToken(ForgeBatch(plan, nullMarkers));
        }

        private static void AssertTryValidateFailsWithNullToken(
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch candidate)
        {
            bool valid = candidate.TryValidate(
                out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token);

            Assert.That(valid, Is.False);
            Assert.That(token, Is.Null);
        }

        // ---- Execution batch: execution result success evidence ----

        [Test]
        public void PreparedStep_ExecutionResultEvidenceDestroyedAfterToken_FailsClosed()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();
            int readyIndex = plan.Count - 1;

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);

            Assert.That(prepared.IsValidIndexLocal(token), Is.True);
            Assert.That(op.IsValidIndexLocal(token), Is.True);

            SetField(plan.OrchestrationResult.ExecutionResult, "_completedSteps", null);

            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
            Assert.That(op.IsValidIndexLocal(token), Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    plan, publicationPaths, markerPaths, readyIndex, token));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        // ---- Execution batch: authoritative plan structure ----

        [Test]
        public void PreparedStep_AuthoritativePlanEntriesNullAfterToken_FailsClosed()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);

            Assert.That(op.IsValidIndexLocal(token), Is.True);

            SetField(plan.AuthoritativePlan, "_entries", null);

            Assert.That(op.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void PreparedStep_InspectionDecisionNullAfterToken_FailsClosed()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = GetPublicationPaths(plan);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupOperation op =
                new CaptureRunPublicationCaptureCompleteCleanupOperation(plan, publicationPaths, markerPaths, 0, token);

            Assert.That(op.IsValidIndexLocal(token), Is.True);

            SetField(plan.OrchestrationResult.InspectionSnapshot.Operation, "_decision", null);

            Assert.That(op.IsValidIndexLocal(token), Is.False);
        }

        // ---- Execution batch: batch token binding ----

        [Test]
        public void Batch_TryValidate_TokenBoundToExactBatch()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batchA = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batchB = BuildBatch(plan);

            Assert.That(
                batchA.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken tokenA),
                Is.True);

            Assert.That(tokenA.IsIssuedFor(batchA), Is.True);
            Assert.That(tokenA.IsIssuedFor(batchB), Is.False);
            Assert.That(tokenA.IsIssuedFor(null), Is.False);
            Assert.That(tokenA.ActionPlanToken, Is.Not.Null);
        }

        [Test]
        public void Batch_TryValidate_TokenDetectsArrayReplacement()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(batch), Is.True);

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] original =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] replacement =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone();
            SetField(batch, "_steps", replacement);

            Assert.That(token.IsIssuedFor(batch), Is.False);
        }

        [Test]
        public void Batch_TryValidate_TokenDetectsInPlaceElementReplacement()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(batch), Is.True);

            // Replace an element in place (same array reference): the token
            // must fail closed.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            steps[0] = steps[1];

            Assert.That(token.IsIssuedFor(batch), Is.False);
        }

        [Test]
        public void Batch_TryValidate_TokenDetectsNullPreparedStepActionPlan()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(batch), Is.True);

            // Forge the prepared step's plan link away; the per-step proof
            // comparison must fail closed instead of leaking a
            // NullReferenceException from resolving step.Action.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            SetField(steps[0], "_actionPlan", null);

            Assert.That(token.IsIssuedFor(batch), Is.False);
        }

        [Test]
        public void Batch_TryValidate_TokenDetectsNullPlanStepArray()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(batch), Is.True);

            // Forge the plan's step array away; the per-step proof comparison
            // must fail closed instead of throwing when resolving step.Action.
            SetField(plan, "_steps", null);

            Assert.That(token.IsIssuedFor(batch), Is.False);
        }

        [Test]
        public void Proof_IsUnobtainableExternally()
        {
            Type tokenType = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken);
            Type proofType = typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken.ValidatedPlanProof);

            // The two-step proof mint must not exist; the proof can only be
            // produced and consumed inside the atomic TryAcquire.
            Assert.That(
                tokenType.GetMethod("TryAcquireProof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                Is.Null);

            Assert.That(proofType.IsPublic, Is.False);

            // The proof has only a private constructor, so no assembly code can
            // instantiate it.
            ConstructorInfo[] constructors = proofType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors, Is.Not.Empty);
            foreach (ConstructorInfo constructor in constructors)
            {
                Assert.That(constructor.IsPublic, Is.False, "The proof constructor must not be public.");
                Assert.That(constructor.IsPrivate, Is.True, "The proof constructor must be private.");
            }

            // The atomic mint returns the plan token, never the proof, through
            // any out parameter.
            MethodInfo mint = proofType.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(mint, Is.Not.Null);
            Assert.That(
                mint.GetParameters().Any(parameter => parameter.ParameterType == proofType && parameter.IsOut),
                Is.False,
                "The atomic mint must not return the proof through an out parameter.");
        }

        [Test]
        public void CorruptedActionPlan_AtomicMintFailsClosed()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            // Destroy the recovery action plan's step array; the full cleanup
            // plan validation must fail, so the atomic mint path issues no
            // proof and no downstream token.
            SetField(plan.OrchestrationResult.Batch.ActionPlan, "_steps", null);

            Assert.That(plan.TryValidate(out _), Is.False);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void CorruptedInspectionOperation_AtomicMintFailsClosed()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationArtifactInspectionOperation operation = plan.OrchestrationResult.InspectionSnapshot.Operation;

            // Corrupt the inspection operation; the full plan validation must
            // fail, so the atomic mint path issues no inspection token.
            SetField(operation, "_artifactPaths", null);

            Assert.That(plan.TryValidate(out _), Is.False);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void ActionPlanToken_NonValidatedMintRejectsNullProof()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = plan.OrchestrationResult.Batch.ActionPlan;

            // A null proof is rejected; a real proof cannot be obtained outside
            // the atomic mint, so this is the only call a caller can make.
            Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken.AcquireFromValidatedPlan(actionPlan, null));
        }

        [Test]
        public void ExecutionResultToken_NonValidatedMintRejectsNullProof()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationArtifactRecoveryExecutionResult result = plan.OrchestrationResult.ExecutionResult;

            Assert.That(
                CaptureRunPublicationArtifactRecoveryExecutionResult.ValidationToken.TryAcquireFromValidatedResult(
                    result, null, out _),
                Is.False);
        }

        [Test]
        public void Batch_TryValidate_TokenDetectsSwappedPreparedStepActionPlan()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(batch), Is.True);

            // Swap the prepared step's plan link to a different but equally
            // shaped plan. The step index and action still match, but the
            // token must fail closed because the step no longer correlates to
            // the validated plan.
            CaptureRunPublicationCaptureCompleteCleanupActionPlan otherPlan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            SetField(steps[0], "_actionPlan", otherPlan);

            Assert.That(token.IsIssuedFor(batch), Is.False);
        }

        [Test]
        public void Batch_TryValidate_TokenDetectsSwappedPublicationPaths()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(batch), Is.True);

            // Replace the prepared step's publication path set with a
            // different instance; the token must fail closed.
            CaptureRunPublicationCaptureCompleteCleanupActionPlan otherPlan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationPathSet forged = GetPublicationPaths(otherPlan);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            SetField(steps[0], "_publicationPaths", forged);

            Assert.That(token.IsIssuedFor(batch), Is.False);
        }

        [Test]
        public void Batch_Token_TryGetIssuedStep_ExactIndexProof()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);

            Assert.That(
                token.TryGetIssuedStep(batch, 0, out CaptureRunPublicationCaptureCompleteCleanupPreparedStep step0),
                Is.True);
            Assert.That(ReferenceEquals(step0, batch.GetStep(0)), Is.True);

            Assert.That(token.TryGetIssuedStep(batch, -1, out _), Is.False);
            Assert.That(token.TryGetIssuedStep(batch, batch.Count, out _), Is.False);
            Assert.That(token.TryGetIssuedStep(null, 0, out _), Is.False);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch otherBatch = BuildBatch(plan);
            Assert.That(token.TryGetIssuedStep(otherBatch, 0, out _), Is.False);
        }

        [Test]
        public void Batch_Token_NullActionPlanTokenFailsClosedWithoutThrowing()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken token),
                Is.True);

            // Corrupt the token's action plan link after issuance; every
            // predicate must fail closed instead of leaking a
            // NullReferenceException.
            SetField(token, "_actionPlanToken", null);

            Assert.That(token.IsIssuedForExactBindings(batch), Is.False);
            Assert.That(token.IsIssuedFor(batch), Is.False);

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared;
            Assert.That(token.TryGetIssuedStep(batch, 0, out prepared), Is.False);
            Assert.That(prepared, Is.Null);

            // The result token delegates to its held batch token, so it must
            // also fail closed rather than propagate the exception.
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);
            Assert.That(
                result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken resultToken),
                Is.True);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken innerBatchToken =
                (CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken)GetField(resultToken, "_batchToken");
            SetField(innerBatchToken, "_actionPlanToken", null);

            Assert.That(resultToken.IsIssuedFor(result), Is.False);
        }

        // ---- Execution batch: shape and O(n) ----

        [Test]
        public void Batch_FieldsShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionBatch);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(property.PropertyType.IsArray, Is.False,
                    property.Name + " must not expose an array.");
            }
        }

        [Test]
        public void Batch_NoExternalArrayConstructor()
        {
            ConstructorInfo[] constructors = typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionBatch)
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(constructors, Has.Length.EqualTo(1));

            ParameterInfo[] parameters = constructors[0].GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].ParameterType,
                Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteCleanupActionPlan)));
        }

        [Test]
        public void PreparedStep_FieldsShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupPreparedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(5));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Builder_Shape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionBatchBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Batch_DoesNotDisposeLease()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            Assert.That(batch.LockLease.IsCreated, Is.True);
            Assert.That(batch.IsValid, Is.True);
            Assert.That(batch.LockLease.IsCreated, Is.True);
        }

        [Test]
        public void Batch_LargePlan_BuildsAndValidates()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(500));
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 500, plan: planValue);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan =
                CaptureRunPublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(batch.Count, Is.EqualTo(500 * 2 + 6));
            Assert.That(batch.IsValid, Is.True);

            Assert.That(batch.GetStep(0).Action,
                Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            Assert.That(batch.GetStep(batch.Count - 1).Action,
                Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
            Assert.That(batch.GetStep(batch.Count - 1).CleanupOperation, Is.Null);
        }

        [Test]
        public void Source_NoForbiddenDependenciesOrBackendCalls()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupPreparedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionBatchBuilder.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("System.IO"));
                Assert.That(source, Does.Not.Contain("Backend"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain(".Dispose("));
            }
        }

        [Test]
        public void Source_ExactLengthAllocation()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.cs"));

            Assert.That(
                CountOccurrences(source, "new CaptureRunPublicationCaptureCompleteCleanupPreparedStep["),
                Is.EqualTo(1));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        // ---- Execution coordinator / result / completed step ----

        [Test]
        public void ExecutionStatus_EnumShapeAndAppendOnly()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.None, Is.EqualTo(0));
            Assert.That((int)CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady, Is.EqualTo(1));

            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.cs"));
            Assert.That(source, Does.Contain("append-only"));
        }

        [Test]
        public void Coordinator_NullBackend_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("backend"));
        }

        [Test]
        public void Coordinator_NullBatch_RejectedWithoutBackendContact()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("batch"));
            Assert.That(backend.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Coordinator_InvalidBatch_RejectedWithoutBackendContact()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            SetField(batch, "_steps", null);

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(batch));

            Assert.That(ex.ParamName, Is.EqualTo("batch"));
            Assert.That(backend.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Coordinator_ExecutesAllSideEffectingActionsInOrder()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            List<string> log = new List<string>();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend(log);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            int sideEffectingCount = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch.GetStep(i).Action != CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    sideEffectingCount++;
                }
            }

            Assert.That(backend.CallCount, Is.EqualTo(sideEffectingCount));
            Assert.That(result.Count, Is.EqualTo(batch.Count));
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));

            // Every side-effecting step was executed exactly once in batch order,
            // and each completed step carries a receipt bound to its own operation.
            List<string> expectedLog = new List<string>();
            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep completed = result.GetStep(i);

                Assert.That(ReferenceEquals(completed.PreparedStep, prepared), Is.True, "step " + i);

                if (prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    Assert.That(completed.CleanupReceipt, Is.Null, "routing step " + i);
                }
                else
                {
                    Assert.That(completed.CleanupReceipt, Is.Not.Null, "side-effecting step " + i);
                    Assert.That(ReferenceEquals(completed.CleanupReceipt.IssuedBy, backend), Is.True);
                    Assert.That(
                        ReferenceEquals(completed.CleanupReceipt.Operation, prepared.CleanupOperation),
                        Is.True);
                    expectedLog.Add("cleanup:" + prepared.StepIndex + ":" + prepared.Action);
                }
            }

            Assert.That(log, Is.EqualTo(expectedLog));
        }

        [Test]
        public void Coordinator_RoutingStep_NoBackendContact()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(backend.CallCount, Is.EqualTo(batch.Count - 1));

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep last = result.GetStep(result.Count - 1);
            Assert.That(last.PreparedStep.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
            Assert.That(last.CleanupReceipt, Is.Null);
        }

        [Test]
        public void Coordinator_NullReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend
            {
                ReceiptOverride = op => null
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.CallCount, Is.EqualTo(1), "Execution must stop after the first rejected receipt.");
        }

        [Test]
        public void Coordinator_ForeignIssuerReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            FakePublicationCleanupBackend foreign = new FakePublicationCleanupBackend();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend
            {
                ReceiptOverride = op => new CaptureRunPublicationCaptureCompleteCleanupReceipt(foreign, op)
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Coordinator_DifferentOperationReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            CaptureRunPublicationCaptureCompleteCleanupOperation wrongOperation = MakeOp(plan, 1);
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            backend.ReceiptOverride = op => new CaptureRunPublicationCaptureCompleteCleanupReceipt(backend, wrongOperation);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Coordinator_ForwardingMismatchReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            // Step 1 is the PNG staging artifact and step 2 the sidecar: binding
            // a receipt to the sidecar operation while the step expects PNG makes
            // the forwarded artifact kind, entry index, and target path disagree.
            CaptureRunPublicationCaptureCompleteCleanupOperation sidecarOperation = MakeOp(plan, 2);
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            backend.ReceiptOverride = op => new CaptureRunPublicationCaptureCompleteCleanupReceipt(backend, sidecarOperation);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Coordinator_BackendException_PropagatesIdentical_NoRetry_NoSubsequentSteps()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            IOException exception = new IOException("cleanup failed");
            List<string> log = new List<string>();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend(log)
            {
                ExceptionToThrow = exception
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(log, Is.EqualTo(new[] { "cleanup:0:DeletePublicationPlanTemporary" }));
        }

        [Test]
        public void Coordinator_Failure_DoesNotDisposeLease()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend
            {
                ExceptionToThrow = new IOException("boom")
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<IOException>(() => coordinator.Execute(batch));
            Assert.That(batch.LockLease.IsCreated, Is.True);
        }

        [Test]
        public void Coordinator_Success_DoesNotDisposeLease()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.LockLease.IsCreated, Is.True);
        }

        [Test]
        public void Coordinator_StepsInPlaceReorderDuringFirstExecute_StopsBeforeSecondCall()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            backend.ExecuteMutator = () =>
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                    (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep swap = steps[1];
                steps[1] = steps[2];
                steps[2] = swap;
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.CallCount, Is.EqualTo(1), "Execution must stop before the second backend call.");
        }

        [Test]
        public void Coordinator_StepsArrayReplacementDuringFirstExecute_StopsBeforeSecondCall()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            backend.ExecuteMutator = () =>
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] original =
                    (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
                SetField(batch, "_steps", ((CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone()));
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.CallCount, Is.EqualTo(1), "Execution must stop before the second backend call.");
        }

        [Test]
        public void Coordinator_ForeignPreparedStepDuringFirstExecute_StopsBeforeSecondCall()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch otherBatch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            backend.ExecuteMutator = () =>
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                    (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
                steps[1] = otherBatch.GetStep(1);
            };
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.CallCount, Is.EqualTo(1), "Execution must stop before the second backend call.");
        }

        [Test]
        public void Coordinator_ActionPlanReplacementDuringFirstExecute_StopsBeforeSecondCall()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupActionPlan otherPlan =
                BuildCommitPlanWithPublicationPlanTemporary();

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            backend.ExecuteMutator = () => SetField(batch, "_actionPlan", otherPlan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.CallCount, Is.EqualTo(1), "Execution must stop before the second backend call.");
        }

        [Test]
        public void Result_ArrayDefensiveCopyAndNonExposure()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult first = coordinator.Execute(batch);
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] internalArray =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(first, "_completedSteps");

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] input =
                new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[first.Count];
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = internalArray[i];
            }

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult second =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionResult(coordinator, batch, input);

            input[0] = null;

            Assert.That(second.GetStep(0), Is.Not.Null, "The result must defensively copy the input array.");

            // The completed-step array is never exposed.
            foreach (PropertyInfo property in typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionResult)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(
                    property.PropertyType != typeof(CaptureRunPublicationCaptureCompleteCleanupCompletedStep[]),
                    "The completed-step array must not be exposed.");
            }
        }

        [Test]
        public void Result_IsValidFalseForNullReorderReplacement()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult good = coordinator.Execute(batch);

            // Missing trailing step.
            AssertCleanupResultRejected(coordinator, batch,
                new[] { good.GetStep(0), good.GetStep(1) });

            // Extra step.
            AssertCleanupResultRejected(coordinator, batch,
                new[] { good.GetStep(0), good.GetStep(1), good.GetStep(2), good.GetStep(3), good.GetStep(4), good.GetStep(5), good.GetStep(6), good.GetStep(7), good.GetStep(8), good.GetStep(0) });

            // Reordered step.
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] reordered = WithReplacedCleanup(good, 0, good.GetStep(1));
            reordered[1] = good.GetStep(0);
            AssertCleanupResultRejected(coordinator, batch, reordered);

            // Foreign prepared step from a different batch.
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch otherBatch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep foreign =
                coordinator.Execute(otherBatch).GetStep(0);
            AssertCleanupResultRejected(coordinator, batch, WithReplacedCleanup(good, 0, foreign));
        }

        [Test]
        public void Result_IsValidFalseForForeignIssuerReceipt()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult good = coordinator.Execute(batch);

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep original = good.GetStep(0);
            FakePublicationCleanupBackend foreign = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupReceipt foreignReceipt =
                new CaptureRunPublicationCaptureCompleteCleanupReceipt(foreign, original.CleanupReceipt.Operation);
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep forged = ForgeCleanupCompletedStep(original, foreignReceipt);

            AssertCleanupResultRejected(coordinator, batch, WithReplacedCleanup(good, 0, forged));
        }

        [Test]
        public void Result_IsValidFalseAfterLeaseRelease()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.IsValid, Is.True);

            result.LockLease.Dispose();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.TryValidate(out _), Is.False);
        }

        [Test]
        public void Result_CrossTokenSubstitutionRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult resultA =
                coordinator.Execute(BuildBatch(BuildCommitPlanWithPublicationPlanTemporary()));
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult resultB =
                coordinator.Execute(BuildBatch(BuildCommitPlanWithPublicationPlanTemporary()));

            Assert.That(resultA.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken tokenA), Is.True);
            Assert.That(resultB.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken tokenB), Is.True);

            Assert.That(tokenA.IsIssuedFor(resultA), Is.True);
            Assert.That(tokenA.IsIssuedFor(resultB), Is.False);
            Assert.That(tokenB.IsIssuedFor(resultB), Is.True);
            Assert.That(tokenB.IsIssuedFor(resultA), Is.False);
            Assert.That(tokenA.IsIssuedFor(null), Is.False);
        }

        [Test]
        public void Result_TokenDetectsInPlaceElementReplacement()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token), Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(result, "_completedSteps");
            steps[0] = steps[1];

            Assert.That(token.IsIssuedFor(result), Is.False);
        }

        [Test]
        public void ExecutionResult_Token_ExactBindings_BindingOnly()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(
                result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedForExactBindings(result), Is.True);
            Assert.That(token.IsIssuedForExactBindings(null), Is.False);

            // Cross-token substitution.
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult other =
                coordinator.Execute(BuildBatch(BuildCommitPlanWithPublicationPlanTemporary()));
            Assert.That(token.IsIssuedForExactBindings(other), Is.False);

            // Completed-step array swapped in place.
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] original =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(result, "_completedSteps");
            SetField(result, "_completedSteps", ((CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])original.Clone()));
            Assert.That(token.IsIssuedForExactBindings(result), Is.False);

            // Null array.
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result2 =
                coordinator.Execute(BuildBatch(BuildCommitPlanWithPublicationPlanTemporary()));
            Assert.That(
                result2.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token2),
                Is.True);
            SetField(result2, "_completedSteps", null);
            Assert.That(token2.IsIssuedForExactBindings(result2), Is.False);
        }

        [Test]
        public void Result_TrustedCtorRejectsForeignBatchToken()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch otherBatch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());

            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult good = coordinator.Execute(batch);
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completed =
                new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[good.Count];
            for (int i = 0; i < good.Count; i++)
            {
                completed[i] = good.GetStep(i);
            }

            Assert.That(
                otherBatch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken foreignToken),
                Is.True);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionResult(coordinator, batch, completed, foreignToken));

            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        [Test]
        public void Result_TrustedCtorRejectsBatchStepsReorderedAfterToken()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult good = coordinator.Execute(batch);
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completed =
                new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[good.Count];
            for (int i = 0; i < good.Count; i++)
            {
                completed[i] = good.GetStep(i);
            }

            Assert.That(
                batch.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionBatch.ValidationToken batchToken),
                Is.True);

            // Reorder the batch's prepared steps after the token was minted.
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] preparedSteps =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep preparedSwap = preparedSteps[1];
            preparedSteps[1] = preparedSteps[2];
            preparedSteps[2] = preparedSwap;

            // Mirror the reorder in the completed steps so a correlation that
            // only compared the current array contents would accept them.
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep completedSwap = completed[1];
            completed[1] = completed[2];
            completed[2] = completedSwap;

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionResult(coordinator, batch, completed, batchToken));
        }

        [Test]
        public void Result_TokenDetectsBatchActionPlanReplacement()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);
            Assert.That(
                result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);

            CaptureRunPublicationCaptureCompleteCleanupActionPlan otherPlan = BuildCommitPlanWithPublicationPlanTemporary();
            SetField(result.Batch, "_actionPlan", otherPlan);

            Assert.That(token.IsIssuedFor(result), Is.False);
        }

        [Test]
        public void Result_TokenDetectsBatchStepsArrayReplacement()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);
            Assert.That(
                result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] original =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(result.Batch, "_steps");
            SetField(result.Batch, "_steps", ((CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])original.Clone()));

            Assert.That(token.IsIssuedFor(result), Is.False);
        }

        [Test]
        public void Result_TokenDetectsBatchStepsInPlaceReorder()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);
            Assert.That(
                result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token),
                Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(result.Batch, "_steps");
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep swap = steps[1];
            steps[1] = steps[2];
            steps[2] = swap;

            Assert.That(token.IsIssuedFor(result), Is.False);
        }

        [Test]
        public void Result_TokenFailsClosedOnCorruptedBatchWithoutThrowing()
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());

            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);
            Assert.That(
                result.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken token),
                Is.True);

            SetField(result.Batch, "_steps", null);
            Assert.That(token.IsIssuedFor(result), Is.False);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch secondBatch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult second = coordinator.Execute(secondBatch);
            Assert.That(
                second.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken secondToken),
                Is.True);

            SetField(second.Batch, "_actionPlan", null);
            Assert.That(secondToken.IsIssuedFor(second), Is.False);
        }

        [Test]
        public void CompletedStep_ReceiptExclusivityTable()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            for (int i = 0; i < result.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupCompletedStep completed = result.GetStep(i);
                CaptureRunPublicationCaptureCompleteCleanupAction action = completed.PreparedStep.Action;

                if (action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    Assert.That(completed.CleanupReceipt, Is.Null, "routing step must hold no receipt");
                }
                else
                {
                    Assert.That(completed.CleanupReceipt, Is.Not.Null, "side-effecting step must hold a receipt");
                    Assert.That(
                        ReferenceEquals(completed.CleanupReceipt.Operation, completed.PreparedStep.CleanupOperation),
                        Is.True);
                }
            }
        }

        [Test]
        public void CompletedStep_RoutingStepReceiptRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep routingStep = batch.GetStep(batch.Count - 1);
            Assert.That(routingStep.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));

            CaptureRunPublicationCaptureCompleteCleanupReceipt strayReceipt =
                new CaptureRunPublicationCaptureCompleteCleanupReceipt(
                    new FakePublicationCleanupBackend(), batch.GetStep(0).CleanupOperation);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupCompletedStep(routingStep, strayReceipt, token));
        }

        [Test]
        public void CompletedStep_SideEffectingStepNullReceiptRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep sideEffectingStep = batch.GetStep(0);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupCompletedStep(sideEffectingStep, null, token));
        }

        [Test]
        public void CompletedStep_ForeignOperationReceiptRejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupActionPlan plan = BuildCommitPlanWithPublicationPlanTemporary();
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep sideEffectingStep = batch.GetStep(0);
            CaptureRunPublicationCaptureCompleteCleanupReceipt wrongReceipt =
                new CaptureRunPublicationCaptureCompleteCleanupReceipt(
                    new FakePublicationCleanupBackend(), batch.GetStep(1).CleanupOperation);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupCompletedStep(sideEffectingStep, wrongReceipt, token));
        }

        [Test]
        public void Source_ExecutionTypesNoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupCompletedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionResult.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Notification"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("using System.Linq"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("Thread"));
                Assert.That(source, Does.Not.Contain(".Dispose()"));
            }
        }

        [Test]
        public void Source_LoopNoFullValidationNoBackendRevalidation()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator.cs"));

            int loopIndex = coordinatorSource.IndexOf("for (int i = 0; i < count; i++)", StringComparison.Ordinal);
            Assert.That(loopIndex, Is.GreaterThan(0));

            int resultIndex = coordinatorSource.IndexOf("return new CaptureRunPublicationCaptureCompleteCleanupExecutionResult", StringComparison.Ordinal);
            Assert.That(resultIndex, Is.GreaterThan(loopIndex));

            string loopBody = coordinatorSource.Substring(loopIndex, resultIndex - loopIndex);
            Assert.That(loopBody, Does.Not.Contain("batch.IsValid"));
            Assert.That(loopBody, Does.Not.Contain(".IsValid"));
            Assert.That(loopBody, Does.Not.Contain("TryValidate"));
            Assert.That(loopBody, Does.Not.Contain("AcquireValidationToken"));
            Assert.That(loopBody, Does.Contain("VerifyReceipt"));
            Assert.That(coordinatorSource, Does.Contain("IsIssuedForIndexLocal"));
        }

        [Test]
        public void Source_BatchValidatedExactlyOnce()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator.cs"));

            Assert.That(CountOccurrences(coordinatorSource, "batch.TryValidate"), Is.EqualTo(1));
            Assert.That(coordinatorSource, Does.Not.Contain("batch.IsValid"));
            Assert.That(coordinatorSource, Does.Not.Contain("AcquireValidationToken"));
        }

        [Test]
        public void Source_ResultUsesExactBindingsNotFullBatchValidation()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionResult.cs"));

            // The result must never call the full-sequence batch validation;
            // correlation uses the O(1) exact-bindings predicate plus per-index
            // exact-step proofs instead.
            Assert.That(CountOccurrences(source, "batchToken.IsIssuedFor("), Is.EqualTo(0));
            Assert.That(source, Does.Contain("IsIssuedForExactBindings"));
        }

        [Test]
        public void Source_OrchestrationTypesNoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Notification"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("using System.Linq"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("Thread"));
                Assert.That(source, Does.Not.Contain(".Dispose()"));
            }
        }

        [Test]
        public void Source_OrchestrationNoDuplicateFullValidation()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator.cs"));
            string resultSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult.cs"));

            // The coordinator never re-validates the recovery result, action
            // plan, or batch on the success path; the builders are the single
            // full-validation boundary.
            Assert.That(coordinatorSource, Does.Not.Contain("recoveryResult.IsValid"));
            Assert.That(coordinatorSource, Does.Not.Contain("actionPlan.IsValid"));
            Assert.That(coordinatorSource, Does.Not.Contain("batch.IsValid"));
            Assert.That(coordinatorSource, Does.Not.Contain("AcquireValidationToken"));

            // No TryValidate in the coordinator: the atomic result constructor
            // owns the single full validation, so no token ever leaves it.
            Assert.That(CountOccurrences(coordinatorSource, "TryValidate"), Is.EqualTo(0));

            // The result runs TryValidate exactly three times: once in the
            // direct constructor, once in the atomic constructor, and once in
            // IsValid. No constructor accepts an externally supplied token.
            Assert.That(CountOccurrences(resultSource, "TryValidate"), Is.EqualTo(3));
            Assert.That(resultSource, Does.Contain("IsCorrelated"));

            // The correlation predicate must use the O(1) exact-bindings
            // predicate, not the full-step walk, because TryValidate has
            // already fully validated the completed-step sequence.
            Assert.That(CountOccurrences(resultSource, "token.IsIssuedFor("), Is.EqualTo(0));
            Assert.That(resultSource, Does.Contain("IsIssuedForExactBindings"));
        }

        [Test]
        public void Source_ForwardingComparisons()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator.cs"));

            Assert.That(coordinatorSource, Does.Contain("receipt.Action != prepared.Action"));
            Assert.That(coordinatorSource, Does.Contain("receipt.StepIndex != prepared.StepIndex"));
            Assert.That(coordinatorSource, Does.Contain("receipt.EntryIndex != operation.EntryIndex"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ArtifactKind != operation.ArtifactKind"));
            Assert.That(coordinatorSource, Does.Contain("receipt.TargetPath"));
            Assert.That(coordinatorSource, Does.Contain("receipt.ActionPlan"));
            Assert.That(coordinatorSource, Does.Contain("receipt.RootLayout"));
            Assert.That(coordinatorSource, Does.Contain("receipt.LockLease"));
            Assert.That(coordinatorSource, Does.Contain("receipt.TestRunId"));
            Assert.That(coordinatorSource, Does.Contain("receipt.RunInitializationId"));
        }

        [Test]
        public void Coordinator_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].IsPrivate, Is.True);
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(ICaptureRunPublicationCaptureCompleteCleanupBackend)));
        }

        [Test]
        public void Result_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        [Test]
        public void CompletedStep_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupCompletedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        // ---- Cleanup orchestration: end-to-end ----

        [Test]
        public void Orchestration_CommitCaptureIndexRoute_ConnectsEndToEnd()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result = coordinator.Execute(recovery);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));
            Assert.That(result.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex));
            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.OrchestrationResult, recovery), Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, recovery.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockLease, recovery.LockLease), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(recovery.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(recovery.RunInitializationId));

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = result.Batch;
            int sideEffecting = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch.GetStep(i).Action != CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    sideEffecting++;
                }
            }

            Assert.That(backend.CallCount, Is.EqualTo(sideEffecting), "Every side-effecting step executes once.");
            Assert.That(result.ExecutionResult.Count, Is.EqualTo(batch.Count));
        }

        [Test]
        public void Orchestration_CaptureCompleteRoute_ConnectsEndToEnd()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCaptureCompleteResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result = coordinator.Execute(recovery);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));
            Assert.That(result.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete));
            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.OrchestrationResult, recovery), Is.True);
        }

        [Test]
        public void Orchestration_NullRecoveryResult_Rejected()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(backend.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Orchestration_NonTargetDispositions_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult publish = BuildArtifactResult(
                true, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            CaptureRunPublicationArtifactRecoveryOrchestrationResult orphaned = BuildArtifactResult(
                true, EvAbsent, EvAbsent, EvAbsent, EvAbsent, EvAbsent);
            CaptureRunPublicationArtifactRecoveryOrchestrationResult sourceMissing = BuildArtifactResult(
                true, EvAbsent, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected);
            CaptureRunPublicationArtifactRecoveryOrchestrationResult publishedMissing = BuildArtifactResult(
                false, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            CaptureRunPublicationArtifactRecoveryOrchestrationResult collision = BuildArtifactResult(
                true, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMismatch);

            foreach (CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery in new[]
            {
                publish, orphaned, sourceMissing, publishedMissing, collision
            })
            {
                FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
                CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

                Assert.Throws<ArgumentException>(() => coordinator.Execute(recovery));
                Assert.That(backend.CallCount, Is.EqualTo(0), "No backend contact for a non-target disposition.");
            }
        }

        [Test]
        public void Orchestration_InvalidRecoveryResult_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();

            CaptureRunPublicationArtifactRecoveryOrchestrationResult forged =
                (CaptureRunPublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryOrchestrationResult));
            SetField(forged, "_issuedBy", recovery.IssuedBy);
            SetField(forged, "_executionResult", null);

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            Assert.Throws<ArgumentException>(() => coordinator.Execute(forged));
            Assert.That(backend.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Orchestration_ReleasedLease_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            recovery.LockLease.Dispose();

            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            Assert.Throws<ArgumentException>(() => coordinator.Execute(recovery));
            Assert.That(backend.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Orchestration_BackendException_PropagatesIdentical()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            IOException exception = new IOException("cleanup failed");
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend { ExceptionToThrow = exception };
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(recovery));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(backend.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Orchestration_Failure_NoRetryNoDispose()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            List<string> log = new List<string>();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend(log)
            {
                ExceptionToThrow = new IOException("boom")
            };
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            Assert.Throws<IOException>(() => coordinator.Execute(recovery));

            Assert.That(backend.CallCount, Is.EqualTo(1), "No retry.");
            Assert.That(log.Count, Is.EqualTo(1), "No subsequent backend contact.");
            Assert.That(recovery.LockLease.IsCreated, Is.True, "The lease stays owned by the caller.");
        }

        // ---- Cleanup orchestration result: construction and correlation ----

        [Test]
        public void OrchestrationResult_NullIssuedBy_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult good = coordinator.Execute(recovery);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(null, good.ExecutionResult));
            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void OrchestrationResult_NullExecutionResult_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(coordinator, null));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void OrchestrationResult_ForeignIssuer_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult good = coordinator.Execute(recovery);

            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator foreignExecution =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult forged =
                ForgeExecutionResult(foreignExecution, good.Batch,
                    (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(good.ExecutionResult, "_completedSteps"));

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(coordinator, forged));
        }

        [Test]
        public void OrchestrationResult_ForeignBatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult good = coordinator.Execute(recovery);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch otherBatch =
                BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult forged =
                ForgeExecutionResult(coordinator.ExecutionCoordinator, otherBatch,
                    (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(good.ExecutionResult, "_completedSteps"));

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(coordinator, forged));
        }

        [Test]
        public void OrchestrationResult_ForeignActionPlan_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult good = coordinator.Execute(recovery);

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch forgedBatch = ForgeBatch(
                BuildCommitPlanWithPublicationPlanTemporary(),
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep[])GetField(good.Batch, "_steps"));
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult forged =
                ForgeExecutionResult(coordinator.ExecutionCoordinator, forgedBatch,
                    (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(good.ExecutionResult, "_completedSteps"));

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(coordinator, forged));
        }

        [Test]
        public void Orchestration_AtomicCtor_RejectsReplacedCompletedStepElement()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator execution =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator(execution);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult = execution.Execute(batch);

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(executionResult, "_completedSteps");
            steps[0] = steps[1];

            Assert.Throws<InvalidOperationException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(
                    coordinator, executionResult, batch, batch.ActionPlan, batch.ActionPlan.OrchestrationResult));
        }

        [Test]
        public void Orchestration_AtomicCtor_RejectsReplacedReceipt()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator execution =
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                new CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator(execution);
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(BuildCommitPlanWithPublicationPlanTemporary());
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult = execution.Execute(batch);

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep original = executionResult.GetStep(0);
            CaptureRunPublicationCaptureCompleteCleanupReceipt foreignReceipt =
                new CaptureRunPublicationCaptureCompleteCleanupReceipt(
                    new FakePublicationCleanupBackend(), original.CleanupReceipt.Operation);
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep forged =
                ForgeCleanupCompletedStep(original, foreignReceipt);

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(executionResult, "_completedSteps");
            steps[0] = forged;

            Assert.Throws<InvalidOperationException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult(
                    coordinator, executionResult, batch, batch.ActionPlan, batch.ActionPlan.OrchestrationResult));
        }

        [Test]
        public void OrchestrationResult_NoExternalTokenConstructor()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult);

            foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                bool acceptsToken = ctor.GetParameters().Any(
                    parameter => parameter.ParameterType == typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionResult.ValidationToken));
                Assert.That(acceptsToken, Is.False, "No constructor may accept an externally supplied token.");
            }
        }

        [Test]
        public void OrchestrationResult_ForwardingAndFieldShape()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result = coordinator.Execute(recovery);

            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Batch, result.ExecutionResult.Batch), Is.True);
            Assert.That(ReferenceEquals(result.ActionPlan, result.ExecutionResult.ActionPlan), Is.True);
            Assert.That(ReferenceEquals(result.OrchestrationResult, recovery), Is.True);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));
            Assert.That(result.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex));
            Assert.That(ReferenceEquals(result.RootLayout, recovery.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockLease, recovery.LockLease), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(recovery.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(recovery.RunInitializationId));

            Type type = typeof(CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }

            Type coordinatorType = typeof(CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator);
            Assert.That(coordinatorType.IsPublic, Is.False);
            Assert.That(coordinatorType.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(coordinatorType), Is.False);
            Assert.That(coordinatorType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] coordinatorFields = coordinatorType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(coordinatorFields.Length, Is.EqualTo(1));
            Assert.That(coordinatorFields[0].IsInitOnly, Is.True);
            Assert.That(coordinatorFields[0].IsPrivate, Is.True);
            Assert.That(coordinatorFields[0].FieldType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator)));
        }

        [Test]
        public void OrchestrationResult_CorruptedFailsClosedWithoutThrowing()
        {
            FakePublicationCleanupBackend backend = new FakePublicationCleanupBackend();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(backend);

            // Completed step array null.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result1 = coordinator.Execute(BuildCommitResult());
            Assert.That(result1.IsValid, Is.True);
            SetField(result1.ExecutionResult, "_completedSteps", null);
            Assert.That(result1.IsValid, Is.False);

            // Lease released.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result2 = coordinator.Execute(BuildCommitResult());
            Assert.That(result2.IsValid, Is.True);
            result2.LockLease.Dispose();
            Assert.That(result2.IsValid, Is.False);

            // Foreign execution coordinator.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result3 = coordinator.Execute(BuildCommitResult());
            Assert.That(result3.IsValid, Is.True);
            SetField(result3.ExecutionResult, "_issuedBy",
                new CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator(new FakePublicationCleanupBackend()));
            Assert.That(result3.IsValid, Is.False);

            // Batch swapped.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result4 = coordinator.Execute(BuildCommitResult());
            Assert.That(result4.IsValid, Is.True);
            SetField(result4.ExecutionResult, "_batch", BuildBatch(BuildCommitPlanWithPublicationPlanTemporary()));
            Assert.That(result4.IsValid, Is.False);

            // Action plan swapped on the batch.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result5 = coordinator.Execute(BuildCommitResult());
            Assert.That(result5.IsValid, Is.True);
            SetField(result5.Batch, "_actionPlan", BuildCommitPlanWithPublicationPlanTemporary());
            Assert.That(result5.IsValid, Is.False);

            // Nested orchestration result corrupted (recovery execution result null).
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult result6 = coordinator.Execute(BuildCommitResult());
            Assert.That(result6.IsValid, Is.True);
            SetField(result6.OrchestrationResult, "_executionResult", null);
            Assert.That(result6.IsValid, Is.False);
        }

        private static CaptureRunPublicationCaptureCompleteCleanupCompletedStep ForgeCleanupCompletedStep(
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep template,
            CaptureRunPublicationCaptureCompleteCleanupReceipt receipt)
        {
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep forged =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupCompletedStep));
            SetField(forged, "_preparedStep", template.PreparedStep);
            SetField(forged, "_cleanupReceipt", receipt);
            return forged;
        }

        private static CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] WithReplacedCleanup(
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult result,
            int index,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep replacement)
        {
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps =
                new CaptureRunPublicationCaptureCompleteCleanupCompletedStep[result.Count];
            for (int i = 0; i < result.Count; i++)
            {
                steps[i] = i == index ? replacement : result.GetStep(i);
            }

            return steps;
        }

        private static void AssertCleanupResultRejected(
            CaptureRunPublicationCaptureCompleteCleanupExecutionCoordinator coordinator,
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] completedSteps)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteCleanupExecutionResult(coordinator, batch, completedSteps));

            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        // ---- Forge helpers ----

        private static CaptureRunPublicationCaptureCompleteCleanupExecutionBatch ForgeBatch(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps)
        {
            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch =
                (CaptureRunPublicationCaptureCompleteCleanupExecutionBatch)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupExecutionBatch));
            SetField(batch, "_actionPlan", actionPlan);
            SetField(batch, "_steps", steps);
            return batch;
        }

        private static CaptureRunPublicationCaptureCompleteCleanupPreparedStep ForgePreparedStep(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunPublicationCaptureCompleteCleanupOperation cleanupOperation)
        {
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared =
                (CaptureRunPublicationCaptureCompleteCleanupPreparedStep)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupPreparedStep));
            SetField(prepared, "_actionPlan", actionPlan);
            SetField(prepared, "_publicationPaths", publicationPaths);
            SetField(prepared, "_markerPaths", markerPaths);
            SetField(prepared, "_stepIndex", stepIndex);
            SetField(prepared, "_cleanupOperation", cleanupOperation);
            return prepared;
        }

        private static CaptureRunPublicationPathSet ForgePublicationPathSet(CaptureRunPublicationPathSet source, string fieldName, string corruptedValue)
        {
            CaptureRunPublicationPathSet forged = (CaptureRunPublicationPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationPathSet));
            SetField(forged, "_rootLayout", source.RootLayout);
            SetField(forged, "_stagingFramesRoot", source.StagingFramesRoot);
            SetField(forged, "_publicationPlanTemporaryPath", source.PublicationPlanTemporaryPath);
            SetField(forged, "_publicationPlanPath", source.PublicationPlanPath);
            SetField(forged, "_finalFramesRoot", source.FinalFramesRoot);
            SetField(forged, "_captureIndexTemporaryPath", source.CaptureIndexTemporaryPath);
            SetField(forged, "_captureIndexPath", source.CaptureIndexPath);
            SetField(forged, fieldName, corruptedValue);
            return forged;
        }

        private static CaptureRunMarkerPathSet ForgeMarkerPathSet(CaptureRunMarkerPathSet source, string fieldName, string corruptedValue)
        {
            CaptureRunMarkerPathSet forged = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));
            SetField(forged, "_rootLayout", source.RootLayout);
            SetField(forged, "_stagingInitializationTemporaryPath", source.StagingInitializationTemporaryPath);
            SetField(forged, "_stagingInitializationPath", source.StagingInitializationPath);
            SetField(forged, "_stagingReadyTemporaryPath", source.StagingReadyTemporaryPath);
            SetField(forged, "_stagingReadyPath", source.StagingReadyPath);
            SetField(forged, "_finalInitializationTemporaryPath", source.FinalInitializationTemporaryPath);
            SetField(forged, "_finalInitializationPath", source.FinalInitializationPath);
            SetField(forged, "_finalReadyTemporaryPath", source.FinalReadyTemporaryPath);
            SetField(forged, "_finalReadyPath", source.FinalReadyPath);
            SetField(forged, fieldName, corruptedValue);
            return forged;
        }

        // ---- Capture-complete notification operation / notifier / receipt ----

        private sealed class FakeNotificationNotifier : ICaptureRunPublicationCaptureCompleteNotifier
        {
            public int CallCount { get; private set; }

            public CaptureRunPublicationCaptureCompleteNotificationOperation LastOperation { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunPublicationCaptureCompleteNotificationOperation, CaptureRunPublicationCaptureCompleteNotificationReceipt> ReceiptOverride { get; set; }

            public CaptureRunPublicationCaptureCompleteNotificationReceipt Notify(CaptureRunPublicationCaptureCompleteNotificationOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ReceiptOverride != null)
                {
                    return ReceiptOverride(operation);
                }

                return new CaptureRunPublicationCaptureCompleteNotificationReceipt(this, operation);
            }
        }

        private static CaptureRunPublicationCaptureCompleteNotificationOperation MakeNotificationOperation(bool commitRoute)
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                coordinator.Execute(commitRoute ? BuildCommitResult() : BuildCaptureCompleteResult());
            return new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup);
        }

        private static CaptureRunPublicationCaptureCompleteNotificationCoordinator MakeNotificationCoordinator(
            FakeNotificationNotifier notifier = null)
        {
            return new CaptureRunPublicationCaptureCompleteNotificationCoordinator(
                notifier ?? new FakeNotificationNotifier());
        }

        private static CaptureRunPublicationCaptureCompleteNotificationResult MakeNotificationResult(bool commitRoute)
        {
            return MakeNotificationCoordinator(new FakeNotificationNotifier()).Execute(
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(
                    commitRoute ? BuildCommitResult() : BuildCaptureCompleteResult()));
        }

        private static CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof MakeProof(
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator)
        {
            return coordinator.Execute(
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult())).Proof;
        }

        [Test]
        public void Notification_CommitCaptureIndexRoute_Constructs()
        {
            CaptureRunPublicationCaptureCompleteNotificationOperation operation = MakeNotificationOperation(commitRoute: true);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));
            Assert.That(operation.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex));
        }

        [Test]
        public void Notification_CaptureCompleteRoute_Constructs()
        {
            CaptureRunPublicationCaptureCompleteNotificationOperation operation = MakeNotificationOperation(commitRoute: false);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));
            Assert.That(operation.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete));
        }

        [Test]
        public void Notification_ForwardsAllValues()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(recovery);
            CaptureRunPublicationCaptureCompleteNotificationOperation operation = new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup);

            Assert.That(ReferenceEquals(operation.CleanupResult, cleanup), Is.True);
            Assert.That(ReferenceEquals(operation.ExecutionResult, cleanup.ExecutionResult), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, cleanup.RootLayout), Is.True);
            Assert.That(ReferenceEquals(operation.LockLease, cleanup.LockLease), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(cleanup.TestRunId));
            Assert.That(operation.RunInitializationId, Is.EqualTo(cleanup.RunInitializationId));
            Assert.That(operation.RunManifestContentSha256, Is.EqualTo(cleanup.ActionPlan.AuthoritativePlan.RunManifestContentSha256));
            Assert.That(operation.CaptureIndexPath, Is.EqualTo(GetPublicationPaths(cleanup.ActionPlan).CaptureIndexPath));
            Assert.That(operation.Disposition, Is.EqualTo(cleanup.Disposition));
            Assert.That(operation.Status, Is.EqualTo(cleanup.Status));
        }

        [Test]
        public void Notification_StableIdentityFourElements()
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = BuildCommitResult();
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator = MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(recovery);
            CaptureRunPublicationCaptureCompleteNotificationOperation operation = new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup);

            Assert.That(operation.TestRunId, Is.EqualTo(recovery.TestRunId));
            Assert.That(operation.RunInitializationId, Is.EqualTo(recovery.RunInitializationId));
            Assert.That(operation.RunManifestContentSha256, Is.EqualTo(recovery.Decision.AuthoritativePlan.RunManifestContentSha256));
            Assert.That(operation.CaptureIndexPath, Is.EqualTo(GetPublicationPaths(cleanup.ActionPlan).CaptureIndexPath));

            // Identity string comparisons are ordinal.
            Assert.That(string.Equals(operation.RunInitializationId, recovery.RunInitializationId, StringComparison.Ordinal), Is.True);
            Assert.That(string.Equals(operation.RunManifestContentSha256, recovery.Decision.AuthoritativePlan.RunManifestContentSha256, StringComparison.Ordinal), Is.True);
            Assert.That(string.Equals(operation.CaptureIndexPath, GetPublicationPaths(cleanup.ActionPlan).CaptureIndexPath, StringComparison.Ordinal), Is.True);
        }

        [Test]
        public void Notification_NullResult_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(null));

            Assert.That(ex.ParamName, Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void Notification_InvalidResult_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(BuildCommitResult());

            SetField(cleanup.ExecutionResult, "_completedSteps", null);
            Assert.That(cleanup.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup));

            Assert.That(ex.ParamName, Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void Notification_DispositionContradiction_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(BuildCommitResult());

            CaptureRunPublicationArtifactRecoveryDecision decision = cleanup.OrchestrationResult.Decision;
            SetField(decision, "_disposition", CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision);
            Assert.That(cleanup.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup));

            Assert.That(ex.ParamName, Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void Notification_ForeignRootLayoutLockLeasePublicationPathSet_Rejected()
        {
            // Foreign root layout: swap the cleanup execution batch.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator c1 =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult r1 = c1.Execute(BuildCommitResult());
            SetField(r1.ExecutionResult, "_batch", BuildBatch(BuildCommitPlanWithPublicationPlanTemporary()));
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(r1));

            // Foreign lock lease: release the lease.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator c2 =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult r2 = c2.Execute(BuildCommitResult());
            r2.LockLease.Dispose();
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(r2));

            // Foreign publication path set: swap the recovery operation's path set to another layout.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator c3 =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult r3 = c3.Execute(BuildCommitResult());
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation =
                r3.OrchestrationResult.InspectionSnapshot.Decision.Snapshot.Operation;
            SetField(recoveryOperation, "_publicationPaths", new CaptureRunPublicationPathSet(MakeLayout(999)));
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(r3));
        }

        [Test]
        public void Notification_LeaseExpired_Invalid()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(BuildCommitResult());
            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup);

            Assert.That(operation.IsValid, Is.True);

            cleanup.LockLease.Dispose();
            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Notification_PlanCorruption_Rejected()
        {
            // Test run id corrupted.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator c1 =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult r1 = c1.Execute(BuildCommitResult());
            SetField(r1.ActionPlan.AuthoritativePlan, "_testRunId", 999L);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(r1));

            // Run initialization id corrupted.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator c2 =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult r2 = c2.Execute(BuildCommitResult());
            SetField(r2.ActionPlan.AuthoritativePlan, "_runInitializationId", "ffffffffffffffffffffffffffffffff");
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(r2));

            // Manifest hash corrupted.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator c3 =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult r3 = c3.Execute(BuildCommitResult());
            SetField(r3.ActionPlan.AuthoritativePlan, "_runManifestContentSha256", "broken");
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(r3));
        }

        [Test]
        public void Notification_CaptureIndexPathReplaced_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(BuildCommitResult());
            CaptureRunPublicationPathSet pathSet = GetPublicationPaths(cleanup.ActionPlan);

            SetField(pathSet, "_captureIndexPath", pathSet.CaptureIndexTemporaryPath);

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup));
        }

        [Test]
        public void Notification_CompletedStepReceiptCorruption_Rejected()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(BuildCommitResult());

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep original = cleanup.ExecutionResult.GetStep(0);
            CaptureRunPublicationCaptureCompleteCleanupReceipt foreign =
                new CaptureRunPublicationCaptureCompleteCleanupReceipt(
                    new FakePublicationCleanupBackend(), original.CleanupReceipt.Operation);
            CaptureRunPublicationCaptureCompleteCleanupCompletedStep forged = ForgeCleanupCompletedStep(original, foreign);

            CaptureRunPublicationCaptureCompleteCleanupCompletedStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupCompletedStep[])GetField(cleanup.ExecutionResult, "_completedSteps");
            steps[0] = forged;

            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup));
        }

        [Test]
        public void Notification_UninitializedOperationAndReceipt_Invalid()
        {
            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                (CaptureRunPublicationCaptureCompleteNotificationOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteNotificationOperation));
            Assert.That(operation.IsValid, Is.False);

            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt =
                (CaptureRunPublicationCaptureCompleteNotificationReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteNotificationReceipt));
            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void NotificationReceipt_NullArgsForeignNotifierForeignOperation_Rejected()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationOperation operation = MakeNotificationOperation(commitRoute: true);
            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt =
                new CaptureRunPublicationCaptureCompleteNotificationReceipt(notifier, operation);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(notifier, operation), Is.True);

            // Null arguments.
            Assert.That(receipt.IsIssuedFor(null, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(notifier, null), Is.False);

            // Foreign notifier.
            Assert.That(receipt.IsIssuedFor(new FakeNotificationNotifier(), operation), Is.False);

            // Different operation.
            CaptureRunPublicationCaptureCompleteNotificationOperation other = MakeNotificationOperation(commitRoute: false);
            Assert.That(receipt.IsIssuedFor(notifier, other), Is.False);

            // Null-argument construction rejection.
            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationReceipt(null, operation));
            Assert.That(ex1.ParamName, Is.EqualTo("issuedBy"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationReceipt(notifier, null));
            Assert.That(ex2.ParamName, Is.EqualTo("operation"));

            // Invalid operation rejection.
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator coordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup = coordinator.Execute(BuildCommitResult());
            CaptureRunPublicationCaptureCompleteNotificationOperation leaseOperation =
                new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup);
            cleanup.LockLease.Dispose();
            Assert.That(leaseOperation.IsValid, Is.False);

            ArgumentException ex3 = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationReceipt(notifier, leaseOperation));
            Assert.That(ex3.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void NotificationReceipt_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteNotificationReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        [Test]
        public void NotificationOperation_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteNotificationOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].IsPrivate, Is.True);
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult)));
        }

        [Test]
        public void Notifier_InterfaceSingleMethod()
        {
            Type type = typeof(ICaptureRunPublicationCaptureCompleteNotifier);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsInterface, Is.True);

            MethodInfo[] methods = type.GetMethods();
            Assert.That(methods.Length, Is.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Notify"));
            Assert.That(methods[0].ReturnType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteNotificationReceipt)));

            ParameterInfo[] parameters = methods[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteNotificationOperation)));
        }

        [Test]
        public void Source_NotificationXmlContract()
        {
            string notifierSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationCaptureCompleteNotifier.cs"));

            Assert.That(notifierSource, Does.Contain("idempotent"));
            Assert.That(notifierSource, Does.Contain("durably"));
            Assert.That(notifierSource, Does.Contain("hard failure"));
            Assert.That(notifierSource, Does.Contain("no internal retry"));
            Assert.That(notifierSource, Does.Contain("never mutates, retains, or disposes"));
            Assert.That(notifierSource, Does.Contain("identity conflict"));
        }

        [Test]
        public void Source_NotificationTypesNoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationCaptureCompleteNotifier.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationReceipt.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteCleanupBackend"));
                Assert.That(source, Does.Not.Contain(".Dispose()"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("using System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));

                // The only static members are pure predicate helpers, never mutable state.
                Assert.That(
                    CountOccurrences(source, "static"),
                    Is.EqualTo(CountOccurrences(source, "static bool")));
            }
        }

        [Test]
        public void Source_NotificationNoDuplicateFullValidation()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationOperation.cs"));

            // The cleanup orchestration result is fully validated exactly once
            // in the constructor and once in IsValid; the correlation predicate
            // itself never re-runs the full result, batch, plan, or
            // authoritative-plan validation.
            Assert.That(CountOccurrences(source, "cleanupResult.IsValid"), Is.EqualTo(2));
            Assert.That(source, Does.Not.Contain("executionResult.IsValid"));
            Assert.That(source, Does.Not.Contain("batch.IsValid"));
            Assert.That(source, Does.Not.Contain("actionPlan.IsValid"));
            Assert.That(source, Does.Not.Contain("plan.IsValid"));
            Assert.That(source, Does.Not.Contain("TryValidate"));
            Assert.That(source, Does.Not.Contain("AcquireValidationToken"));
            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("Array.Copy"));
        }

        // ---- Capture-complete notification coordinator / result ----

        [Test]
        public void NotificationCoordinator_NullCleanupResult_Rejected()
        {
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(new FakeNotificationNotifier());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => coordinator.Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void NotificationCoordinator_InvalidCleanupResult_NotifierNotContacted()
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationCoordinator cleanupCoordinator =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend());
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                cleanupCoordinator.Execute(BuildCommitResult());
            SetField(cleanup.ExecutionResult, "_completedSteps", null);
            Assert.That(cleanup.IsValid, Is.False);

            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);

            Assert.Throws<ArgumentException>(() => coordinator.Execute(cleanup));
            Assert.That(notifier.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void NotificationCoordinator_OperationBuiltOnceSameReferencePassed()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult());

            CaptureRunPublicationCaptureCompleteNotificationResult result = coordinator.Execute(cleanup);

            Assert.That(notifier.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(notifier.LastOperation, result.Operation), Is.True);
            Assert.That(ReferenceEquals(notifier.LastOperation.CleanupResult, cleanup), Is.True);
        }

        [Test]
        public void NotificationCoordinator_NotifierCalledExactlyOnce()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult());

            coordinator.Execute(cleanup);

            Assert.That(notifier.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void NotificationCoordinator_NotifierExceptionPropagatesSameInstance()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            InvalidOperationException expected = new InvalidOperationException("boom");
            notifier.ExceptionToThrow = expected;
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult());

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => coordinator.Execute(cleanup));

            Assert.That(ReferenceEquals(thrown, expected), Is.True);
        }

        [Test]
        public void NotificationResult_NullReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(new FakeNotificationNotifier());
            CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof = MakeProof(coordinator);
            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                MakeNotificationOperation(commitRoute: true);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationResult(coordinator, proof, operation, null));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void NotificationResult_ForeignNotifierReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(new FakeNotificationNotifier());
            CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof = MakeProof(coordinator);
            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                MakeNotificationOperation(commitRoute: true);

            CaptureRunPublicationCaptureCompleteNotificationReceipt foreignReceipt =
                new CaptureRunPublicationCaptureCompleteNotificationReceipt(new FakeNotificationNotifier(), operation);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationResult(coordinator, proof, operation, foreignReceipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void NotificationResult_DifferentOperationReceipt_Rejected()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof = MakeProof(coordinator);
            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                MakeNotificationOperation(commitRoute: true);
            CaptureRunPublicationCaptureCompleteNotificationOperation other =
                MakeNotificationOperation(commitRoute: false);

            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt =
                new CaptureRunPublicationCaptureCompleteNotificationReceipt(notifier, other);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationResult(coordinator, proof, operation, receipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void NotificationResult_InvalidReceipt_Rejected()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof = MakeProof(coordinator);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult());
            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanup);
            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt =
                new CaptureRunPublicationCaptureCompleteNotificationReceipt(notifier, operation);

            cleanup.LockLease.Dispose();
            Assert.That(operation.IsValid, Is.False);
            Assert.That(receipt.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationResult(coordinator, proof, operation, receipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void NotificationResult_ForwardsAllValues()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinator =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult());
            CaptureRunPublicationCaptureCompleteNotificationResult result = coordinator.Execute(cleanup);

            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Notifier, notifier), Is.True);
            Assert.That(ReferenceEquals(result.Receipt.IssuedBy, notifier), Is.True);
            Assert.That(ReferenceEquals(result.Receipt.Operation, result.Operation), Is.True);
            Assert.That(ReferenceEquals(result.CleanupResult, cleanup), Is.True);
            Assert.That(ReferenceEquals(result.ExecutionResult, cleanup.ExecutionResult), Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, cleanup.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockLease, cleanup.LockLease), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(cleanup.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(cleanup.RunInitializationId));
            Assert.That(result.RunManifestContentSha256, Is.EqualTo(cleanup.ActionPlan.AuthoritativePlan.RunManifestContentSha256));
            Assert.That(result.CaptureIndexPath, Is.EqualTo(GetPublicationPaths(cleanup.ActionPlan).CaptureIndexPath));
            Assert.That(result.Disposition, Is.EqualTo(cleanup.Disposition));
            Assert.That(result.Status, Is.EqualTo(cleanup.Status));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void NotificationResult_BothDispositionsAccepted()
        {
            CaptureRunPublicationCaptureCompleteNotificationResult commit = MakeNotificationResult(commitRoute: true);
            Assert.That(commit.IsValid, Is.True);
            Assert.That(commit.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex));

            CaptureRunPublicationCaptureCompleteNotificationResult complete = MakeNotificationResult(commitRoute: false);
            Assert.That(complete.IsValid, Is.True);
            Assert.That(complete.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete));
        }

        [Test]
        public void NotificationResult_ReceiptOperationReplaced_Invalid()
        {
            CaptureRunPublicationCaptureCompleteNotificationResult result = MakeNotificationResult(commitRoute: true);
            Assert.That(result.IsValid, Is.True);

            CaptureRunPublicationCaptureCompleteNotificationOperation other =
                MakeNotificationOperation(commitRoute: false);
            SetField(result.Receipt, "_operation", other);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void NotificationResult_CorruptedGraphConvergesFalse()
        {
            // Cleanup result corrupted inside the operation.
            CaptureRunPublicationCaptureCompleteNotificationResult r1 = MakeNotificationResult(commitRoute: true);
            Assert.That(r1.IsValid, Is.True);
            SetField(r1.Operation, "_cleanupResult", null);
            Assert.That(r1.IsValid, Is.False);

            // Plan corrupted.
            CaptureRunPublicationCaptureCompleteNotificationResult r2 = MakeNotificationResult(commitRoute: true);
            Assert.That(r2.IsValid, Is.True);
            SetField(r2.CleanupResult.ActionPlan.AuthoritativePlan, "_runManifestContentSha256", "broken");
            Assert.That(r2.IsValid, Is.False);

            // Path set corrupted.
            CaptureRunPublicationCaptureCompleteNotificationResult r3 = MakeNotificationResult(commitRoute: true);
            Assert.That(r3.IsValid, Is.True);
            CaptureRunPublicationPathSet pathSet = GetPublicationPaths(r3.CleanupResult.ActionPlan);
            SetField(pathSet, "_captureIndexPath", pathSet.CaptureIndexTemporaryPath);
            Assert.That(r3.IsValid, Is.False);
        }

        [Test]
        public void NotificationResult_LeaseExpired_Invalid()
        {
            CaptureRunPublicationCaptureCompleteNotificationResult result = MakeNotificationResult(commitRoute: true);
            Assert.That(result.IsValid, Is.True);

            result.LockLease.Dispose();
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void NotificationResult_CrossCoordinator_Rejected()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinatorA =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinatorB =
                MakeNotificationCoordinator(notifier);

            CaptureRunPublicationCaptureCompleteNotificationResult result = coordinatorA.Execute(
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult()));

            // Coordinator B shares the same notifier but must still be rejected.
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationResult(
                    coordinatorB, result.Proof, result.Operation, result.Receipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void NotificationResult_CrossProofSubstitutionRejected()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinatorA =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinatorB =
                MakeNotificationCoordinator(notifier);

            CaptureRunPublicationCaptureCompleteNotificationResult resultB = coordinatorB.Execute(
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult()));

            // Coordinator A cannot adopt coordinator B's proof.
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteNotificationResult(
                    coordinatorA, resultB.Proof, resultB.Operation, resultB.Receipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void NotificationResult_IssuedByReplacedSameNotifier_Invalid()
        {
            FakeNotificationNotifier notifier = new FakeNotificationNotifier();
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinatorA =
                MakeNotificationCoordinator(notifier);
            CaptureRunPublicationCaptureCompleteNotificationCoordinator coordinatorB =
                MakeNotificationCoordinator(notifier);

            CaptureRunPublicationCaptureCompleteNotificationResult result = coordinatorA.Execute(
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(BuildCommitResult()));
            Assert.That(result.IsValid, Is.True);

            SetField(result, "_issuedBy", coordinatorB);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void NotificationResult_IssuanceProofNotExternallyMintable()
        {
            Type proofType = typeof(CaptureRunPublicationCaptureCompleteNotificationCoordinator.IssuanceProof);

            Assert.That(proofType.IsPublic, Is.False);
            Assert.That(proofType.IsNested, Is.True);
            Assert.That(proofType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            ConstructorInfo[] constructors = proofType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
        }

        [Test]
        public void NotificationResult_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteNotificationResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        [Test]
        public void NotificationCoordinator_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteNotificationCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].IsPrivate, Is.True);
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(ICaptureRunPublicationCaptureCompleteNotifier)));
        }

        [Test]
        public void Source_NotificationCoordinatorResultNoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationResult.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteCleanupBackend"));
                Assert.That(source, Does.Not.Contain(".Dispose()"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("using System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
            }
        }

        [Test]
        public void Source_NotificationCoordinatorNoPrevalidationNoRetryNoCatch()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationCoordinator.cs"));

            // The coordinator never pre-validates the cleanup result; the
            // operation constructor is the single pre-notification validation
            // boundary.
            Assert.That(source, Does.Not.Contain("cleanupResult.IsValid"));
            Assert.That(CountOccurrences(source, "new CaptureRunPublicationCaptureCompleteNotificationOperation("), Is.EqualTo(1));
            Assert.That(CountOccurrences(source, "_notifier.Notify"), Is.EqualTo(1));

            // No retry loops and no exception transformation.
            Assert.That(source, Does.Not.Contain("for ("));
            Assert.That(source, Does.Not.Contain("while ("));
            Assert.That(source, Does.Not.Contain("foreach"));
            Assert.That(source, Does.Not.Contain("catch"));
        }

        [Test]
        public void Source_NotificationResultNoDuplicateFullValidation()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteNotificationResult.cs"));

            // The single post-notification full validation is aggregated into
            // the IsIssuedFor path; the predicate never calls operation.IsValid
            // or receipt.IsValid separately.
            Assert.That(source, Does.Not.Contain("operation.IsValid"));
            Assert.That(source, Does.Not.Contain("receipt.IsValid"));
            Assert.That(CountOccurrences(source, "IsIssuedFor("), Is.EqualTo(1));
        }
    }
}
