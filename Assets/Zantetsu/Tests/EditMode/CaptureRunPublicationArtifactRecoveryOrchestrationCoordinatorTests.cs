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
    public class CaptureRunPublicationArtifactRecoveryOrchestrationCoordinatorTests
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

        private static CaptureRunPublicationArtifactEntryObservation MakePublishPngSidecarObservation(
            CaptureRunPublicationArtifactInspectionOperation operation)
        {
            return MakeEntryObservation(
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
        }

        private static FakeArtifactInspector BuildPublishPngSidecarScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation();
            return MakePublishPngSidecarInspectorFor(operation, log);
        }

        private static FakeArtifactInspector MakePublishPngSidecarInspectorFor(
            CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            return MakeArtifactInspector(operation, new[] { MakePublishPngSidecarObservation(operation) }, EvMatchesExpected, 100, log);
        }

        private static FakeArtifactInspector BuildCommitScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation();
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
            return MakeArtifactInspector(operation, new[] { observation }, EvMatchesExpected, 100, log);
        }

        private static FakeArtifactInspector BuildOrphanedPreTraceScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation();
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(operation, operation.GetArtifactPaths(0));
            return MakeArtifactInspector(operation, new[] { observation }, EvAbsent, 0, log);
        }

        private static FakeArtifactInspector BuildCaptureCompleteScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            operation = MakeOperation(captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan), plan: plan);
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
            return MakeArtifactInspector(operation, new[] { observation }, EvMatchesExpected, 100, log);
        }

        private static FakeArtifactInspector BuildArtifactSourceMissingScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation();
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
            return MakeArtifactInspector(operation, new[] { observation }, EvMatchesExpected, 100, log);
        }

        private static FakeArtifactInspector BuildPublishedArtifactMissingScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            operation = MakeOperation(captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan), plan: plan);
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
            return MakeArtifactInspector(operation, new[] { observation }, EvMatchesExpected, 100, log);
        }

        private static FakeArtifactInspector BuildRunRootCollisionScenario(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation();
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
            return MakeArtifactInspector(operation, new[] { observation }, EvMismatch, 100, log);
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

        private static CaptureRunPublicationArtifactRecoveryExecutionResult ForgeExecutionResult(
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator issuedBy,
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch,
            CaptureRunPublicationArtifactRecoveryCompletedStep[] completedSteps)
        {
            CaptureRunPublicationArtifactRecoveryExecutionResult forged =
                (CaptureRunPublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryExecutionResult));
            SetField(forged, "_issuedBy", issuedBy);
            SetField(forged, "_batch", batch);
            SetField(forged, "_completedSteps", completedSteps);
            return forged;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactRecoveryOrchestrationCoordinatorTests).Assembly.Location);
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

            public int Calls { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunPublicationArtifactInspectionSnapshot Snapshot { get; set; }

            public Func<CaptureRunPublicationArtifactInspectionOperation, CaptureRunPublicationArtifactInspectionSnapshot> SnapshotFactory { get; set; }

            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                Calls++;
                _log?.Add("inspect");
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (SnapshotFactory != null)
                {
                    return SnapshotFactory(operation);
                }

                return Snapshot;
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

        // ---- Constructor contracts ----

        [Test]
        public void Coordinator_Constructor_NullDependencies_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator(null, execCoord));
            Assert.That(ex1.ParamName, Is.EqualTo("inspector"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator(new FakeArtifactInspector(), null));
            Assert.That(ex2.ParamName, Is.EqualTo("executionCoordinator"));
        }

        [Test]
        public void Result_Constructor_NullArguments_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator());
            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult = orchestrator.Execute(operation).ExecutionResult;

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationResult(null, executionResult));
            Assert.That(ex1.ParamName, Is.EqualTo("issuedBy"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationResult(orchestrator, null));
            Assert.That(ex2.ParamName, Is.EqualTo("executionResult"));
        }

        // ---- Operation rejection ----

        [Test]
        public void Execute_NullOperation_Rejected_InspectorNotContacted()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out _, log);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => orchestrator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(inspector.Calls, Is.EqualTo(0));
            Assert.That(log, Is.Empty);
        }

        [Test]
        public void Execute_InvalidOperation_Rejected_InspectorNotContacted()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out _, log);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunPublicationArtifactInspectionOperation invalid =
                (CaptureRunPublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => orchestrator.Execute(invalid));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(inspector.Calls, Is.EqualTo(0));
            Assert.That(log, Is.Empty);
        }

        // ---- Snapshot verification ----

        [Test]
        public void Execute_InspectorNullSnapshot_Rejected()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            inspector.SnapshotFactory = _ => null;
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            Assert.Throws<InvalidOperationException>(() => orchestrator.Execute(operation));
        }

        [Test]
        public void Execute_ForeignIssuerSnapshot_Rejected()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            FakeArtifactInspector foreign = new FakeArtifactInspector();
            inspector.SnapshotFactory = op => MakeArtifactSnapshot(foreign, op, EvMatchesExpected, 100, new[]
            {
                MakeEntryObservation(op, op.GetArtifactPaths(0),
                    stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                    stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                    finalPngStatus: EvAbsent, finalPngCount: 0,
                    finalSidecarStatus: EvAbsent, finalSidecarCount: 0)
            });
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            Assert.Throws<InvalidOperationException>(() => orchestrator.Execute(operation));
        }

        [Test]
        public void Execute_ForeignOperationSnapshot_Rejected()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            CaptureRunPublicationArtifactInspectionOperation other = MakeOperation();
            inspector.SnapshotFactory = _ => MakeArtifactSnapshot(inspector, other, EvMatchesExpected, 100, new[]
            {
                MakeEntryObservation(other, other.GetArtifactPaths(0),
                    stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                    stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                    finalPngStatus: EvAbsent, finalPngCount: 0,
                    finalSidecarStatus: EvAbsent, finalSidecarCount: 0)
            });
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            Assert.Throws<InvalidOperationException>(() => orchestrator.Execute(operation));
        }

        [Test]
        public void Execute_InspectorException_PropagatesIdentical_NoRetry()
        {
            IOException exception = new IOException("inspect boom");
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            inspector.ExceptionToThrow = exception;
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            IOException ex = Assert.Throws<IOException>(() => orchestrator.Execute(operation));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }), "No retry and no backend contact after an inspector exception.");
        }

        [Test]
        public void Execute_PublishException_PropagatesIdentical_NoRetry_NoReinspect()
        {
            IOException exception = new IOException("publish boom");
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            FakePublisher publisher = new FakePublisher(log) { ExceptionToThrow = exception };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator execCoord =
                new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(publisher, new FakeCommitter(log));
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, execCoord);

            IOException ex = Assert.Throws<IOException>(() => orchestrator.Execute(operation));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(inspector.Calls, Is.EqualTo(1), "No automatic re-inspection after a publish failure.");
            Assert.That(log, Is.EqualTo(new[] { "inspect", "publish:0:Png" }));
        }

        [Test]
        public void Execute_CommitException_PropagatesIdentical_NoRetry_NoReinspect()
        {
            IOException exception = new IOException("commit boom");
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildCommitScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            FakeCommitter committer = new FakeCommitter(log) { ExceptionToThrow = exception };
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator execCoord =
                new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(new FakePublisher(log), committer);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, execCoord);

            IOException ex = Assert.Throws<IOException>(() => orchestrator.Execute(operation));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(inspector.Calls, Is.EqualTo(1), "No automatic re-inspection after a commit failure.");
            Assert.That(log.Count, Is.EqualTo(2));
            Assert.That(log[0], Is.EqualTo("inspect"));
            Assert.That(log[1], Does.StartWith("commit:"));
        }

        // ---- End-to-end dispositions ----

        [Test]
        public void Execute_Publish_InspectorFirst_EachStepOnce()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                inspector, new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(publisher, committer));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = orchestrator.Execute(operation);

            Assert.That(log, Is.EqualTo(new[]
            {
                "inspect",
                "publish:0:Png",
                "publish:0:Sidecar"
            }));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(publisher.Calls, Is.EqualTo(2));
            Assert.That(committer.Calls, Is.EqualTo(0));
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_Commit_CalledOnce()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildCommitScenario(out CaptureRunPublicationArtifactInspectionOperation operation, log);
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                inspector, new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(publisher, committer));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = orchestrator.Execute(operation);

            Assert.That(committer.Calls, Is.EqualTo(1));
            Assert.That(publisher.Calls, Is.EqualTo(0));
            Assert.That(log.Count, Is.EqualTo(2));
            Assert.That(log[0], Is.EqualTo("inspect"));
            Assert.That(log[1], Does.StartWith("commit:"));
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_StopDispositions_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector orphaned = BuildOrphanedPreTraceScenario(out CaptureRunPublicationArtifactInspectionOperation orphanedOp, log);
            FakeArtifactInspector captureComplete = BuildCaptureCompleteScenario(out CaptureRunPublicationArtifactInspectionOperation captureCompleteOp, log);
            FakeArtifactInspector artifactSourceMissing = BuildArtifactSourceMissingScenario(out CaptureRunPublicationArtifactInspectionOperation sourceMissingOp, log);
            FakeArtifactInspector publishedMissing = BuildPublishedArtifactMissingScenario(out CaptureRunPublicationArtifactInspectionOperation publishedMissingOp, log);
            FakeArtifactInspector collision = BuildRunRootCollisionScenario(out CaptureRunPublicationArtifactInspectionOperation collisionOp, log);

            AssertStopStatus(orphaned, orphanedOp, CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace, log);
            AssertStopStatus(captureComplete, captureCompleteOp, CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired, log);
            AssertStopStatus(artifactSourceMissing, sourceMissingOp, CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing, log);
            AssertStopStatus(publishedMissing, publishedMissingOp, CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing, log);
            AssertStopStatus(collision, collisionOp, CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision, log);
        }

        private static void AssertStopStatus(
            FakeArtifactInspector inspector,
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactRecoveryExecutionStatus expectedStatus,
            List<string> log)
        {
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                inspector, new CaptureRunPublicationArtifactRecoveryExecutionCoordinator(publisher, committer));

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = orchestrator.Execute(operation);

            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(publisher.Calls, Is.EqualTo(0));
            Assert.That(committer.Calls, Is.EqualTo(0));
        }

        // ---- Result forwarding ----

        [Test]
        public void Result_Forwarding_ReferenceIdentity()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, execCoord);

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = orchestrator.Execute(operation);
            CaptureRunPublicationArtifactRecoveryExecutionResult executionResult = result.ExecutionResult;

            Assert.That(result.IssuedBy, Is.SameAs(orchestrator));
            Assert.That(result.ExecutionResult, Is.SameAs(executionResult));
            Assert.That(result.Batch, Is.SameAs(executionResult.Batch));
            Assert.That(result.ActionPlan, Is.SameAs(executionResult.Batch.ActionPlan));
            Assert.That(result.Decision, Is.SameAs(executionResult.Batch.ActionPlan.Decision));
            Assert.That(result.InspectionSnapshot, Is.SameAs(executionResult.Batch.ActionPlan.Decision.Snapshot));
            Assert.That(result.Status, Is.EqualTo(executionResult.Status));
            Assert.That(result.Disposition, Is.EqualTo(executionResult.Disposition));
            Assert.That(result.RootLayout, Is.SameAs(executionResult.RootLayout));
            Assert.That(result.LockLease, Is.SameAs(executionResult.LockLease));
            Assert.That(result.TestRunId, Is.EqualTo(executionResult.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(executionResult.RunInitializationId));
            Assert.That(result.InspectionSnapshot.Operation, Is.SameAs(operation));
            Assert.That(result.InspectionSnapshot.IssuedBy, Is.SameAs(inspector));
            Assert.That(result.InspectionSnapshot.Operation.LockLease, Is.SameAs(result.LockLease));
            Assert.That(result.InspectionSnapshot.Operation.RootLayout, Is.SameAs(result.RootLayout));
            Assert.That(result.IsValid, Is.True);
        }

        // ---- Result direct-constructor defense ----

        [Test]
        public void Result_DirectConstructor_ForeignInspector_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            FakeArtifactInspector inspectorA = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operationA);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator coordinatorA = MakeOrchestrator(inspectorA, execCoord);

            FakeArtifactInspector inspectorB = MakePublishPngSidecarInspectorFor(operationA);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator coordinatorB = MakeOrchestrator(inspectorB, execCoord);

            CaptureRunPublicationArtifactRecoveryExecutionResult resultB = coordinatorB.Execute(operationA).ExecutionResult;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationResult(coordinatorA, resultB));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignExecutionCoordinator_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator coordinatorA = MakeOrchestrator(inspector, MakeExecutionCoordinator());

            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator coordinatorB = MakeOrchestrator(inspector, MakeExecutionCoordinator());

            CaptureRunPublicationArtifactRecoveryExecutionResult resultB = coordinatorB.Execute(operation).ExecutionResult;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationResult(coordinatorA, resultB));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignBatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, execCoord);
            CaptureRunPublicationArtifactRecoveryExecutionResult good = orchestrator.Execute(operation).ExecutionResult;

            FakeArtifactInspector commitInspector = BuildCommitScenario(out CaptureRunPublicationArtifactInspectionOperation commitOperation);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator commitOrchestrator = MakeOrchestrator(commitInspector, execCoord);
            CaptureRunPublicationArtifactRecoveryExecutionBatch otherBatch = commitOrchestrator.Execute(commitOperation).Batch;

            CaptureRunPublicationArtifactRecoveryCompletedStep[] steps = new CaptureRunPublicationArtifactRecoveryCompletedStep[good.Count];
            for (int i = 0; i < good.Count; i++)
            {
                steps[i] = good.GetCompletedStep(i);
            }

            CaptureRunPublicationArtifactRecoveryExecutionResult forged = ForgeExecutionResult(good.IssuedBy, otherBatch, steps);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactRecoveryOrchestrationResult(orchestrator, forged));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        // ---- Lease release / forged values ----

        [Test]
        public void Result_LeaseExpired_IsValidFalse()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator());

            CaptureRunPublicationArtifactRecoveryOrchestrationResult result = orchestrator.Execute(operation);

            Assert.That(result.IsValid, Is.True);

            result.LockLease.Dispose();

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void ForgedValues_IsValidFalse_WithoutException()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator());
            CaptureRunPublicationArtifactRecoveryExecutionResult good = orchestrator.Execute(operation).ExecutionResult;

            // null execution result
            CaptureRunPublicationArtifactRecoveryOrchestrationResult nullExec =
                (CaptureRunPublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryOrchestrationResult));
            SetField(nullExec, "_issuedBy", orchestrator);
            SetField(nullExec, "_executionResult", null);
            Assert.That(nullExec.IsValid, Is.False);

            // null issuer
            CaptureRunPublicationArtifactRecoveryOrchestrationResult nullIssuer =
                (CaptureRunPublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryOrchestrationResult));
            SetField(nullIssuer, "_issuedBy", null);
            SetField(nullIssuer, "_executionResult", good);
            Assert.That(nullIssuer.IsValid, Is.False);

            // forged foreign inspector on the nested snapshot
            CaptureRunPublicationArtifactRecoveryOrchestrationResult valid = new CaptureRunPublicationArtifactRecoveryOrchestrationResult(orchestrator, good);
            SetField(valid.InspectionSnapshot, "_issuedBy", new FakeArtifactInspector());
            Assert.That(valid.IsValid, Is.False);

            // forged execution result issued by a foreign execution coordinator
            CaptureRunPublicationArtifactRecoveryExecutionResult foreignResult = ForgeExecutionResult(
                MakeExecutionCoordinator(), good.Batch,
                Enumerable.Range(0, good.Count).Select(i => good.GetCompletedStep(i)).ToArray());
            CaptureRunPublicationArtifactRecoveryOrchestrationResult forgedIssuer =
                (CaptureRunPublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationArtifactRecoveryOrchestrationResult));
            SetField(forgedIssuer, "_issuedBy", orchestrator);
            SetField(forgedIssuer, "_executionResult", foreignResult);
            Assert.That(forgedIssuer.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void OrchestrationCoordinator_Shape()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            Assert.That(instanceFields.All(f => f.IsInitOnly), Is.True);

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "The coordinator must not hold mutable static state.");
        }

        [Test]
        public void OrchestrationResult_Shape()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryOrchestrationResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            Assert.That(instanceFields.All(f => f.IsInitOnly), Is.True);

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "The result must not hold mutable static state.");

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(
                properties.Any(p => p.PropertyType != typeof(string) && (p.PropertyType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))),
                Is.False,
                "The result must not expose arrays or mutable collections.");
            Assert.That(properties.All(p => p.CanWrite == false), Is.True, "The result must not expose setters.");
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryOrchestrationResult.cs"
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
        public void Source_NoRedundantExecutionResultValidation()
        {
            string resultSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryOrchestrationResult.cs"));

            int correlatedIndex = resultSource.IndexOf("private static bool IsCorrelated(", StringComparison.Ordinal);
            int statusIndex = resultSource.IndexOf("private static bool StatusMatchesDisposition(", StringComparison.Ordinal);
            Assert.That(correlatedIndex, Is.GreaterThan(0));
            Assert.That(statusIndex, Is.GreaterThan(correlatedIndex));

            string correlatedBody = resultSource.Substring(correlatedIndex, statusIndex - correlatedIndex);
            Assert.That(correlatedBody, Does.Not.Contain("executionResult.IsValid"));
            Assert.That(correlatedBody, Does.Not.Contain("batch.IsValid"));
            Assert.That(correlatedBody, Does.Not.Contain("plan.IsValid"));
            Assert.That(correlatedBody, Does.Not.Contain("decision.IsValid"));
            Assert.That(correlatedBody, Does.Not.Contain("snapshot.IsValid"));
            Assert.That(correlatedBody, Does.Not.Contain("operation.IsValid"));
            Assert.That(correlatedBody, Does.Not.Contain("TryValidate"));
            Assert.That(correlatedBody, Does.Contain("token.IsIssuedFor"));

            // The direct constructor performs exactly one full validation; the
            // trusted constructor reuses the token without re-validating.
            int directIndex = resultSource.IndexOf("executionResult)", StringComparison.Ordinal);
            int trustedIndex = resultSource.IndexOf("ValidationToken token)", StringComparison.Ordinal);
            Assert.That(directIndex, Is.GreaterThan(0));
            Assert.That(trustedIndex, Is.GreaterThan(directIndex));

            string directBody = resultSource.Substring(directIndex, trustedIndex - directIndex);
            Assert.That(directBody, Does.Contain("TryValidate"));

            int firstPropertyIndex = resultSource.IndexOf(
                "internal CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator IssuedBy",
                StringComparison.Ordinal);
            Assert.That(firstPropertyIndex, Is.GreaterThan(trustedIndex));

            string trustedBody = resultSource.Substring(trustedIndex, firstPropertyIndex - trustedIndex);
            Assert.That(trustedBody, Does.Not.Contain("TryValidate"));
            Assert.That(trustedBody, Does.Not.Contain(".IsValid"));

            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryOrchestrationCoordinator.cs"));

            Assert.That(coordinatorSource, Does.Contain("TryValidate"));
            Assert.That(coordinatorSource, Does.Not.Contain("executionResult.IsValid"));
        }
    }
}
