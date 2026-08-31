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
            public int CallCount { get; private set; }

            public CaptureRunPublicationCaptureCompleteCleanupOperation LastOperation { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunPublicationCaptureCompleteCleanupOperation, CaptureRunPublicationCaptureCompleteCleanupReceipt> ReceiptOverride { get; set; }

            public CaptureRunPublicationCaptureCompleteCleanupReceipt Execute(CaptureRunPublicationCaptureCompleteCleanupOperation operation)
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
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
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

        // ---- Forge helpers ----

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
    }
}
