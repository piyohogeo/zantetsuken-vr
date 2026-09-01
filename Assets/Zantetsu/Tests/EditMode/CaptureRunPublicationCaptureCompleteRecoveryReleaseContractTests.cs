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
    public class CaptureRunPublicationCaptureCompleteRecoveryReleaseContractTests
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

        private static PngJsonCapturePublicationPlanEntry MakeEntry(long captureFrameId)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new PngJsonCapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                PngBytes,
                SidecarBytes,
                StagingHash,
                StagingHash);
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

        private static CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(plan: plan);

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

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, new[] { observation }, EvMatchesExpected, 100);
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
                    BuildCommitResult()));
        }

        private static CaptureRunInitializationOpenOutcome GetProvenanceOpenOutcome(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult)
        {
            return notificationResult.CleanupResult.OrchestrationResult.InspectionSnapshot.Decision.Snapshot.Operation.OpenOutcome;
        }

        private static CaptureRunPublicationCaptureCompleteLifecycleEvidence MakeRecoveryEvidence()
        {
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult = MakeNotificationResult(commitRoute: true);
            CaptureRunInitializationOpenOutcome outcome = GetProvenanceOpenOutcome(notificationResult);
            return CaptureRunPublicationCaptureCompleteLifecycleEvidence.FromRecovery(notificationResult, outcome);
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation MakeReleaseOperation()
        {
            return CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(MakeRecoveryEvidence());
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation MakeReleaseOperationWithThrowingFirstHandle()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunLockLease lease = operation.LockLease;
            SetField(lease, "_firstHandle", new ThrowingOnceHandle(lease.PathSet.FirstLockPath));
            return operation;
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt ReleaseSuccessfully(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            operation.OpenOutcome.Dispose();
            return new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseContractTests).Assembly.Location);
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

        private sealed class ThrowingOnceHandle : ICaptureRunLockHandle
        {
            private int _calls;

            public ThrowingOnceHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
                if (_calls++ == 0)
                {
                    throw new InvalidOperationException("First release fails.");
                }
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
            public CaptureRunPublicationCaptureCompleteCleanupReceipt Execute(CaptureRunPublicationCaptureCompleteCleanupOperation operation)
            {
                return new CaptureRunPublicationCaptureCompleteCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeNotificationNotifier : ICaptureRunPublicationCaptureCompleteNotifier
        {
            public CaptureRunPublicationCaptureCompleteNotificationReceipt Notify(CaptureRunPublicationCaptureCompleteNotificationOperation operation)
            {
                return new CaptureRunPublicationCaptureCompleteNotificationReceipt(this, operation);
            }
        }

        private sealed class FakeReleaser : ICaptureRunPublicationCaptureCompleteRecoveryReleaser
        {
            public CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Release(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                throw new InvalidOperationException("Not used in contract tests.");
            }
        }

        // ---- Operation: construction ----

        [Test]
        public void Operation_NullEvidence_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(null));

            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_InvalidEvidence_Rejected()
        {
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            evidence.LockLease.Dispose();
            Assert.That(evidence.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_ReleasedOutcome_Rejected()
        {
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            evidence.OpenOutcome.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_NonRecoveryKind_Rejected()
        {
            // Forge a fresh-shaped evidence (freeze receipt set) so its Kind is
            // FreshSession, not RecoveryOpenOutcome.
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            SetField(evidence, "_openOutcome", null);
            SetField(evidence, "_freezeReceipt", FormatterServices.GetUninitializedObject(
                typeof(CaptureEvidenceRunFreezeReceipt)));
            Assert.That(evidence.Kind, Is.EqualTo(CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.FreshSession));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_ExactProvenanceOpenOutcome_Rejected()
        {
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            CaptureRunInitializationOpenOutcome provenance = evidence.OpenOutcome;

            // Same orchestration result and lease, different instance.
            CaptureRunInitializationOpenOutcome forged = ForgeOutcome(
                provenance.OrchestrationResult, provenance.OrchestrationResult.LockLease);
            Assert.That(ReferenceEquals(forged, provenance), Is.False);
            Assert.That(forged.IsValid, Is.True);

            SetField(evidence, "_openOutcome", forged);

            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence));
        }

        [Test]
        public void Operation_ConstructionDoesNotDispose()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();

            Assert.That(operation.OpenOutcome.IsCreated, Is.True);
            Assert.That(operation.LockLease.IsCreated, Is.True);
            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.CanRelease, Is.True);
        }

        [Test]
        public void Operation_ForwardsAllValues()
        {
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation =
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence);

            Assert.That(ReferenceEquals(operation.LifecycleEvidence, evidence), Is.True);
            Assert.That(ReferenceEquals(operation.NotificationResult, evidence.NotificationResult), Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, evidence.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(operation.LockLease, evidence.LockLease), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, evidence.RootLayout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(evidence.TestRunId));
            Assert.That(operation.RunInitializationId, Is.EqualTo(evidence.RunInitializationId));
            Assert.That(operation.RunManifestContentSha256, Is.EqualTo(evidence.RunManifestContentSha256));
            Assert.That(operation.CaptureIndexPath, Is.EqualTo(evidence.CaptureIndexPath));
        }

        // ---- Operation: exception-safe predicates ----

        [Test]
        public void Operation_UninitializedConvergesFalse()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation));

            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.CanRelease, Is.False);
        }

        [Test]
        public void Operation_FieldCorruptionConvergesFalse()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            Assert.That(operation.IsValid, Is.True);

            SetField(operation, "_lifecycleEvidence", null);
            Assert.That(operation.IsValid, Is.False);

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation2 = MakeReleaseOperation();
            SetField(operation2, "_openOutcome", null);
            Assert.That(operation2.IsValid, Is.False);
            Assert.That(operation2.CanRelease, Is.False);

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation3 = MakeReleaseOperation();
            SetField(operation3, "_lockLease", null);
            Assert.That(operation3.IsValid, Is.False);

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation4 = MakeReleaseOperation();
            SetField(operation4, "_issuanceProof", null);
            Assert.That(operation4.CanRelease, Is.False);
        }

        // ---- Operation: type shape ----

        [Test]
        public void Operation_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        // ---- Releaser interface ----

        [Test]
        public void Releaser_InterfaceShape()
        {
            Type type = typeof(ICaptureRunPublicationCaptureCompleteRecoveryReleaser);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsInterface, Is.True);

            MethodInfo[] methods = type.GetMethods();
            Assert.That(methods.Length, Is.EqualTo(1));

            MethodInfo method = methods[0];
            Assert.That(method.Name, Is.EqualTo("Release"));
            Assert.That(method.ReturnType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt)));

            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation)));
        }

        // ---- Receipt: construction boundary ----

        [Test]
        public void Receipt_BeforeRelease_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            FakeReleaser releaser = new FakeReleaser();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_AfterRelease_ConstructsAndStaysValid()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            FakeReleaser releaser = new FakeReleaser();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = ReleaseSuccessfully(releaser, operation);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(ReferenceEquals(receipt.IssuedBy, releaser), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(operation.OpenOutcome.IsCreated, Is.False);
            Assert.That(operation.LockLease.IsCreated, Is.False);

            // The lease release invalidates the evidence and notification
            // result, but the receipt must remain valid.
            Assert.That(operation.LifecycleEvidence.IsValid, Is.False);
            Assert.That(operation.NotificationResult.IsValid, Is.False);
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_NullAndForeignRejection()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            FakeReleaser releaser = new FakeReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = ReleaseSuccessfully(releaser, operation);

            Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(null, operation));
            Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, null));

            // IsIssuedFor reference identity.
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(null, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(new FakeReleaser(), operation), Is.False);
            Assert.That(receipt.IsIssuedFor(releaser, MakeReleaseOperation()), Is.False);
        }

        [Test]
        public void Receipt_ForwardsAllValues()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            FakeReleaser releaser = new FakeReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = ReleaseSuccessfully(releaser, operation);

            Assert.That(ReferenceEquals(receipt.LifecycleEvidence, operation.LifecycleEvidence), Is.True);
            Assert.That(ReferenceEquals(receipt.NotificationResult, operation.NotificationResult), Is.True);
            Assert.That(ReferenceEquals(receipt.OpenOutcome, operation.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(receipt.LockLease, operation.LockLease), Is.True);
            Assert.That(ReferenceEquals(receipt.RootLayout, operation.RootLayout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(receipt.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(receipt.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
            Assert.That(receipt.CaptureIndexPath, Is.EqualTo(operation.CaptureIndexPath));
        }

        [Test]
        public void Receipt_UninitializedAndFieldCorruptionConvergesFalse()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt));

            Assert.That(receipt.IsValid, Is.False);

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            FakeReleaser releaser = new FakeReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt valid = ReleaseSuccessfully(releaser, operation);
            Assert.That(valid.IsValid, Is.True);

            SetField(valid, "_operation", null);
            Assert.That(valid.IsValid, Is.False);
        }

        [Test]
        public void Receipt_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        // ---- Cross-operation proof substitution ----

        [Test]
        public void Receipt_CrossOperationProofSubstitution_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation first = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation second = MakeReleaseOperation();

            // Swap second's proof to first's proof: the proof no longer binds.
            SetField(second, "_issuanceProof", GetField(first, "_issuanceProof"));
            Assert.That(second.IsValid, Is.False);
            Assert.That(second.CanRelease, Is.False);
        }

        [Test]
        public void Operation_SameEvidenceProofSwap_Rejected()
        {
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation a =
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation b =
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence);

            // Same evidence (same outcome and lease), different operations.
            Assert.That(a.IsValid, Is.True);
            Assert.That(b.IsValid, Is.True);

            SetField(b, "_issuanceProof", GetField(a, "_issuanceProof"));
            Assert.That(b.IsValid, Is.False);
            Assert.That(b.CanRelease, Is.False);
        }

        [Test]
        public void Operation_PostIssuanceReferenceSwap_RejectedByCanReleaseAndReceipt()
        {
            // Evidence reference swap after issuance.
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            SetField(operation, "_lifecycleEvidence", MakeRecoveryEvidence());
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.CanRelease, Is.False);

            // Notification reference swap after issuance: CanRelease false and
            // the receipt rejects even after the outcome is released.
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation2 = MakeReleaseOperation();
            SetField(operation2, "_notificationResult", MakeNotificationResult(commitRoute: true));
            Assert.That(operation2.IsValid, Is.False);
            Assert.That(operation2.CanRelease, Is.False);

            operation2.OpenOutcome.Dispose();
            FakeReleaser releaser = new FakeReleaser();
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation2));
        }

        [Test]
        public void Operation_ProofGetterAbsent()
        {
            Assert.That(
                typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation).GetProperty(
                    "Proof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(
                typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation).GetProperty(
                    "IssuanceProof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
        }

        [Test]
        public void IssuanceProof_MintRequiresOperationNotReferences()
        {
            Type proofType = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation).GetNestedType(
                "IssuanceProof", BindingFlags.NonPublic);
            Assert.That(proofType, Is.Not.Null);
            Assert.That(proofType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            MethodInfo acquire = proofType.GetMethod("Acquire", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(acquire, Is.Not.Null);
            ParameterInfo[] parameters = acquire.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation)));
        }

        // ---- Partial release failure and retry ----

        [Test]
        public void Release_PartialFailure_ReceiptUnavailableButRetryable()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperationWithThrowingFirstHandle();

            // First attempt partially fails.
            Assert.Throws<AggregateException>(() => operation.OpenOutcome.Dispose());

            // Partial-failure state: outcome live, lease dead.
            Assert.That(operation.OpenOutcome.IsCreated, Is.True);
            Assert.That(operation.LockLease.IsCreated, Is.False);
            Assert.That(operation.IsValid, Is.False);

            // Retryable: the exact outcome is still created.
            Assert.That(operation.CanRelease, Is.True);

            // Receipt cannot be constructed yet.
            FakeReleaser releaser = new FakeReleaser();
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation));
        }

        [Test]
        public void Release_SecondDispose_CompletesAndReceiptConstructible()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperationWithThrowingFirstHandle();

            Assert.Throws<AggregateException>(() => operation.OpenOutcome.Dispose());
            operation.OpenOutcome.Dispose();

            Assert.That(operation.OpenOutcome.IsCreated, Is.False);
            Assert.That(operation.LockLease.IsCreated, Is.False);
            Assert.That(operation.CanRelease, Is.False);

            FakeReleaser releaser = new FakeReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt =
                new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation);
            Assert.That(receipt.IsValid, Is.True);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunPublicationCaptureCompleteRecoveryReleaser.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteNotifier"));
                Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteCleanupBackend"));
                Assert.That(source, Does.Not.Contain("CaptureFrameDraftRegistry"));
                Assert.That(source, Does.Not.Contain("CaptureArtifactRegistry"));
                Assert.That(source, Does.Not.Contain("using UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("using System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
            }

            string[] noDisposePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt.cs"
            };

            foreach (string relativePath in noDisposePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));
                Assert.That(source, Does.Not.Contain(".Dispose()"));
            }
        }
    }
}
