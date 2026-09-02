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
            bool throwingFirstHandle,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog, throwingFirstHandle);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            return owner;
        }

        private CaptureRunInitializationOpenOutcome MakeOutcome(
            List<string> disposeLog,
            bool throwingFirstHandle,
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

            owner = MakeOwner(layout, disposeLog, throwingFirstHandle, out identity);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(List<string> disposeLog = null)
        {
            return MakePublicationRecoveryOutcome(disposeLog, false, out _);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakePublicationRecoveryOutcome(disposeLog, false, out owner);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOutcome(disposeLog, throwingFirstHandle, out owner, out _);
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
            return MakeOperation(disposeLog, plan, publicationPlanTemporary, publicationPlan, captureIndexTemporary, captureIndex, stagingFramesStatus, maximumEntryCount, false, out _);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOperation(null, null, null, null, null, null, CaptureRunPublicationFramesObservationStatus.Directory, 4, false, out owner);
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
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog, throwingFirstHandle, out owner);
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

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            List<string> disposeLog = null,
            bool throwingFirstHandle = false)
        {
            return BuildCommitResult(disposeLog, throwingFirstHandle, out _);
        }

        private CaptureRunPublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                disposeLog, plan, null, null, null, null, CaptureRunPublicationFramesObservationStatus.Directory, 4, throwingFirstHandle, out owner);

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

        private CaptureRunPublicationCaptureCompleteNotificationResult MakeNotificationResult(
            bool commitRoute,
            List<string> disposeLog = null,
            bool throwingFirstHandle = false)
        {
            return MakeNotificationResult(commitRoute, disposeLog, throwingFirstHandle, out _);
        }

        private CaptureRunPublicationCaptureCompleteNotificationResult MakeNotificationResult(
            bool commitRoute,
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeNotificationCoordinator(new FakeNotificationNotifier()).Execute(
                MakeCleanupOrchestrator(new FakePublicationCleanupBackend()).Execute(
                    BuildCommitResult(disposeLog, throwingFirstHandle, out owner)));
        }

        private static CaptureRunInitializationOpenOutcome GetProvenanceOpenOutcome(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult)
        {
            return notificationResult.CleanupResult.OrchestrationResult.InspectionSnapshot.Decision.Snapshot.Operation.OpenOutcome;
        }

        private CaptureRunPublicationCaptureCompleteLifecycleEvidence MakeRecoveryEvidence(
            List<string> disposeLog = null,
            bool throwingFirstHandle = false)
        {
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult = MakeNotificationResult(
                commitRoute: true,
                disposeLog: disposeLog,
                throwingFirstHandle: throwingFirstHandle,
                out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationOpenOutcome outcome = GetProvenanceOpenOutcome(notificationResult);
            return CaptureRunPublicationCaptureCompleteLifecycleEvidence.FromRecovery(notificationResult, outcome, owner);
        }

        private CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation MakeReleaseOperation(
            List<string> disposeLog = null,
            bool throwingFirstHandle = false)
        {
            return CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(
                MakeRecoveryEvidence(disposeLog, throwingFirstHandle));
        }

        private CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation MakeReleaseOperationWithThrowingFirstHandle(List<string> disposeLog = null)
        {
            // The throwing first handle is embedded in the raw lock lease before
            // it is transferred to the ownership lease, so the owner, identity
            // evidence, notification result, lifecycle evidence, and release
            // operation are all minted from the exact issued owner.
            return MakeReleaseOperation(disposeLog, throwingFirstHandle: true);
        }

        private CaptureRunInitializationSessionOwnershipLease MakeForeignOwner(CaptureRunRootLayout layout)
        {
            CaptureRunLockLease lease = MakeLease(layout);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            return owner;
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaser MakeReleaser()
        {
            return new CaptureRunPublicationCaptureCompleteRecoveryReleaser();
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt ReleaseSuccessfully(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            operation.OwnershipLease.Dispose();
            return new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation);
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator MakeCoordinator(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = null)
        {
            return new CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator(
                releaser ?? MakeReleaser());
        }

        private CaptureRunPublicationCaptureCompleteRecoveryReleaseResult MakeResult()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            return MakeCoordinator().Execute(operation);
        }

        private static CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt ForgeReceipt(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt));
            SetField(receipt, "_issuedBy", releaser);
            SetField(receipt, "_operation", operation);
            return receipt;
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

        private static void AssertForbiddenDependencies(string source)
        {
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("CaptureFrameDraftRegistry"));
            Assert.That(source, Does.Not.Contain("CaptureArtifactRegistry"));
            Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteNotifier"));
            Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteCleanupBackend"));
            Assert.That(source, Does.Not.Contain("Trace"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("Thread"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("using UnityEngine"));
            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain("catch"));
            Assert.That(source, Does.Not.Contain("for ("));
            Assert.That(source, Does.Not.Contain("while ("));
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

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null, bool throwingFirstHandle = false)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            ICaptureRunLockHandle first = throwingFirstHandle
                ? new ThrowingOnceHandle(pathSet.FirstLockPath)
                : (ICaptureRunLockHandle)new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
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

        private sealed class CountingReleaser : ICaptureRunPublicationCaptureCompleteRecoveryReleaser
        {
            public int ReleaseCallCount { get; private set; }

            public CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation LastOperation { get; private set; }

            public CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Release(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                ReleaseCallCount++;
                LastOperation = operation;
                operation.OwnershipLease.Dispose();
                return new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(this, operation);
            }
        }

        private sealed class ThrowingReleaser : ICaptureRunPublicationCaptureCompleteRecoveryReleaser
        {
            private readonly Exception _exception;

            public int ReleaseCallCount { get; private set; }

            public ThrowingReleaser(Exception exception)
            {
                _exception = exception;
            }

            public CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Release(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                ReleaseCallCount++;
                throw _exception;
            }
        }

        private sealed class ReturningReleaser : ICaptureRunPublicationCaptureCompleteRecoveryReleaser
        {
            public CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Receipt { get; set; }

            public int ReleaseCallCount { get; private set; }

            public ReturningReleaser(CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = null)
            {
                Receipt = receipt;
            }

            public CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Release(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                ReleaseCallCount++;
                return Receipt;
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
            evidence.OwnershipLease.Dispose();
            Assert.That(evidence.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_ReleasedOutcome_Rejected()
        {
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            evidence.OwnershipLease.Dispose();

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
                provenance.OrchestrationResult, provenance.OrchestrationResult.LockIdentityEvidence);
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

            Assert.That(operation.OpenOutcome.IsValid, Is.True);
            Assert.That(operation.OwnershipLease.IsCreated, Is.True);
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
            Assert.That(ReferenceEquals(operation.OwnershipLease, evidence.OwnershipLease), Is.True);
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
            SetField(operation3, "_ownershipLease", null);
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
            Assert.That(operation.OpenOutcome.IsValid, Is.False);
            Assert.That(operation.OwnershipLease.IsCreated, Is.False);

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
            Assert.That(ReferenceEquals(receipt.OwnershipLease, operation.OwnershipLease), Is.True);
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

            operation2.OwnershipLease.Dispose();
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
        public void IssuanceProof_NoProofReturningMintApi()
        {
            Type proofType = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation).GetNestedType(
                "IssuanceProof", BindingFlags.NonPublic);
            Assert.That(proofType, Is.Not.Null);

            // Proof constructor is private.
            Assert.That(proofType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(proofType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Length, Is.EqualTo(1));

            // No static method returns the proof type: the only static factory
            // returns the operation, never the proof.
            foreach (MethodInfo method in proofType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(proofType), method.Name + " must not return the proof.");
            }

            // The atomic factory exists and returns only the operation.
            MethodInfo mint = proofType.GetMethod("Mint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(mint, Is.Not.Null);
            Assert.That(mint.ReturnType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation)));
            ParameterInfo[] parameters = mint.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteLifecycleEvidence)));
        }

        [Test]
        public void From_AtomicFactoryRejectsCorruptedEvidence()
        {
            // Corrupted outcome (foreign instance, same result and lease).
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence();
            CaptureRunInitializationOpenOutcome provenance = evidence.OpenOutcome;
            SetField(evidence, "_openOutcome", ForgeOutcome(
                provenance.OrchestrationResult, provenance.OrchestrationResult.LockIdentityEvidence));
            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence));

            // Corrupted notification (owner released).
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence2 = MakeRecoveryEvidence();
            evidence2.OwnershipLease.Dispose();
            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence2));

            // Corrupted owner (outcome released).
            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence3 = MakeRecoveryEvidence();
            evidence3.OwnershipLease.Dispose();
            Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.From(evidence3));
        }

        // ---- Partial release failure and retry ----

        [Test]
        public void Release_PartialFailure_ReceiptUnavailableButRetryable()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperationWithThrowingFirstHandle();

            // First attempt partially fails.
            Assert.Throws<AggregateException>(() => operation.OwnershipLease.Dispose());

            // Partial-failure state: the ownership lease is no longer live.
            Assert.That(operation.OwnershipLease.IsCreated, Is.False);
            Assert.That(operation.IsValid, Is.False);

            // Retryable: the exact outcome is still held.
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

            Assert.Throws<AggregateException>(() => operation.OwnershipLease.Dispose());
            operation.OwnershipLease.Dispose();

            Assert.That(operation.OwnershipLease.IsCreated, Is.False);
            Assert.That(operation.CanRelease, Is.False);

            FakeReleaser releaser = new FakeReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt =
                new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation);
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void OwnershipLease_ReleaseStateLifecycle()
        {
            // Initial: both handles held.
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunInitializationSessionOwnershipLease owner = operation.OwnershipLease;
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.CanRelease, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // Partial failure: the first handle throws on the first disposal.
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation partial = MakeReleaseOperationWithThrowingFirstHandle();
            CaptureRunInitializationSessionOwnershipLease partialOwner = partial.OwnershipLease;
            Assert.Throws<AggregateException>(() => partialOwner.Dispose());
            Assert.That(partialOwner.IsCreated, Is.False);
            Assert.That(partialOwner.CanRelease, Is.True);
            Assert.That(partialOwner.IsReleaseComplete, Is.False);

            // Complete: the retry succeeds and both handles are released.
            partialOwner.Dispose();
            Assert.That(partialOwner.IsCreated, Is.False);
            Assert.That(partialOwner.CanRelease, Is.False);
            Assert.That(partialOwner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void OwnershipLease_DisposedFlagAloneIsNotReleaseComplete()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunInitializationSessionOwnershipLease owner = operation.OwnershipLease;

            // Tamper only the disposal flag; the raw lease handles are still
            // held, so release must not count as complete.
            SetField(owner, "_disposed", true);

            Assert.That(owner.IsCreated, Is.False);
            Assert.That(owner.CanRelease, Is.False);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // The release operation can no longer release, and no receipt can
            // be fabricated from the tampered owner.
            Assert.That(operation.CanRelease, Is.False);

            FakeReleaser releaser = new FakeReleaser();
            Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(releaser, operation));
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

        // ---- Standard releaser ----

        [Test]
        public void Releaser_NullOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => releaser.Release(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Releaser_InvalidOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation uninitialized =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation));
            Assert.Throws<ArgumentException>(() => releaser.Release(uninitialized));

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation proofCorrupted = MakeReleaseOperation();
            SetField(proofCorrupted, "_issuanceProof", null);
            ArgumentException ex = Assert.Throws<ArgumentException>(() => releaser.Release(proofCorrupted));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Releaser_PreCallReferenceSwap_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation evidenceSwap = MakeReleaseOperation();
            SetField(evidenceSwap, "_lifecycleEvidence", MakeRecoveryEvidence());
            Assert.Throws<ArgumentException>(() => releaser.Release(evidenceSwap));

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation notificationSwap = MakeReleaseOperation();
            SetField(notificationSwap, "_notificationResult", MakeNotificationResult(commitRoute: true));
            Assert.Throws<ArgumentException>(() => releaser.Release(notificationSwap));

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation outcomeSwap = MakeReleaseOperation();
            SetField(outcomeSwap, "_openOutcome", MakeReleaseOperation().OpenOutcome);
            Assert.Throws<ArgumentException>(() => releaser.Release(outcomeSwap));

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation leaseSwap = MakeReleaseOperation();
            CaptureRunInitializationSessionOwnershipLease foreignOwner = MakeForeignOwner(MakeLayout());
            SetField(leaseSwap, "_ownershipLease", foreignOwner);
            Assert.Throws<ArgumentException>(() => releaser.Release(leaseSwap));
        }

        [Test]
        public void Releaser_NormalRelease_DisposesOnceAndReturnsReceipt()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = releaser.Release(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(operation.OwnershipLease.IsCreated, Is.False);
            Assert.That(ReferenceEquals(receipt.IssuedBy, releaser), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        [Test]
        public void Releaser_AlreadyReleased_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            releaser.Release(operation);
            Assert.Throws<ArgumentException>(() => releaser.Release(operation));
        }

        [Test]
        public void Releaser_LockHandlesReleasedInReverseOrder()
        {
            List<string> log = new List<string>();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation(log);
            CaptureRunLockPathSet pathSet = operation.OwnershipLease.LockPathSet;
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            releaser.Release(operation);

            Assert.That(log.Count, Is.EqualTo(2));
            Assert.That(log[0], Is.EqualTo(pathSet.SecondLockPath));
            Assert.That(log[1], Is.EqualTo(pathSet.FirstLockPath));
        }

        [Test]
        public void Releaser_PartialFailure_PropagatesAndRetries()
        {
            List<string> log = new List<string>();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperationWithThrowingFirstHandle(log);
            CaptureRunLockPathSet pathSet = operation.OwnershipLease.LockPathSet;
            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();

            // First attempt: the aggregate propagates unchanged (single inner failure).
            AggregateException first = Assert.Throws<AggregateException>(() => releaser.Release(operation));
            Assert.That(first.InnerExceptions.Count, Is.EqualTo(1));
            Assert.That(first.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());

            // No receipt; the already-released second handle was not re-disposed.
            Assert.That(log.Count, Is.EqualTo(1));
            Assert.That(log[0], Is.EqualTo(pathSet.SecondLockPath));

            // Retryable through the same operation and the exact outcome.
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.OwnershipLease.IsCreated, Is.False);

            // Second attempt retries only the failed handle and succeeds.
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = releaser.Release(operation);
            Assert.That(receipt, Is.Not.Null);
            Assert.That(operation.OwnershipLease.IsCreated, Is.False);
            Assert.That(log.Count, Is.EqualTo(1), "The released handle must not be re-disposed.");

            // Third attempt rejects as fully released.
            Assert.Throws<ArgumentException>(() => releaser.Release(operation));
        }

        [Test]
        public void Releaser_OwnershipLeaseSwap_FailsClosed()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();

            // Swap the operation's ownership lease to a foreign, still-created
            // owner; the issuance proof no longer binds.
            CaptureRunInitializationSessionOwnershipLease foreignOwner = MakeForeignOwner(MakeLayout());
            SetField(operation, "_ownershipLease", foreignOwner);
            Assert.That(operation.CanRelease, Is.False);

            CaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();
            Assert.Throws<ArgumentException>(() => releaser.Release(operation));
        }

        [Test]
        public void Releaser_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaser);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(ICaptureRunPublicationCaptureCompleteRecoveryReleaser).IsAssignableFrom(type), Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            // No instance fields, no static mutable fields.
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            // Internal constructor only.
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Length, Is.EqualTo(1));

            // Exactly one declared interface method.
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(methods.Length, Is.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Release"));
        }

        [Test]
        public void Releaser_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaser.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("CaptureFrameDraftRegistry"));
            Assert.That(source, Does.Not.Contain("CaptureArtifactRegistry"));
            Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteNotifier"));
            Assert.That(source, Does.Not.Contain("ICaptureRunPublicationCaptureCompleteCleanupBackend"));
            Assert.That(source, Does.Not.Contain("Trace"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("Thread"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("using UnityEngine"));
            Assert.That(source, Does.Not.Contain("using System.Linq"));

            // Exactly one ownership-lease disposal call.
            Assert.That(source, Does.Contain("ownershipLease.Dispose()"));
            Assert.That(CountOccurrences(source, ".Dispose()"), Is.EqualTo(1));

            // No catch and no retry loop.
            Assert.That(source, Does.Not.Contain("catch"));
            Assert.That(source, Does.Not.Contain("for ("));
            Assert.That(source, Does.Not.Contain("while ("));
        }

        // ---- Coordinator ----

        [Test]
        public void Coordinator_NullReleaser_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("releaser"));
        }

        [Test]
        public void Coordinator_NullOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Coordinator_UninitializedOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Coordinator_ProofCorruptedOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            SetField(operation, "_issuanceProof", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Coordinator_FullyReleasedOperation_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            operation.OwnershipLease.Dispose();

            Assert.That(operation.CanRelease, Is.False);
            Assert.Throws<ArgumentException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Coordinator_CallsReleaserExactlyOnceWithExactOperation()
        {
            CountingReleaser releaser = new CountingReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = coordinator.Execute(operation);

            Assert.That(releaser.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(releaser.LastOperation, operation), Is.True);
            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void Coordinator_ReleaserException_PropagatesSameInstance()
        {
            InvalidOperationException expected = new InvalidOperationException("boom");
            ThrowingReleaser releaser = new ThrowingReleaser(expected);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
            Assert.That(ReferenceEquals(actual, expected), Is.True);
        }

        [Test]
        public void Coordinator_ReleaserException_NoResultAndNoRetry()
        {
            ThrowingReleaser releaser = new ThrowingReleaser(new InvalidOperationException("boom"));
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));

            Assert.That(releaser.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.OwnershipLease.IsCreated, Is.True);
        }

        [Test]
        public void Coordinator_PartialFailure_SecondAttemptSucceeds()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperationWithThrowingFirstHandle();

            Assert.Throws<AggregateException>(() => coordinator.Execute(operation));

            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.CanRelease, Is.True);

            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = coordinator.Execute(operation);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsValid, Is.True);
            Assert.That(operation.OwnershipLease.IsCreated, Is.False);

            Assert.Throws<ArgumentException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Coordinator_NullReceipt_Rejected()
        {
            ReturningReleaser releaser = new ReturningReleaser(null);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
            Assert.That(operation.OwnershipLease.IsCreated, Is.True);
        }

        [Test]
        public void Coordinator_ForeignIssuerReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            ReturningReleaser releaser = new ReturningReleaser();
            releaser.Receipt = ForgeReceipt(MakeReleaser(), operation);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Coordinator_WrongOperationReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation other = MakeReleaseOperation();
            ReturningReleaser releaser = new ReturningReleaser();
            releaser.Receipt = ForgeReceipt(releaser, other);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Coordinator_NotReleasedReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            ReturningReleaser releaser = new ReturningReleaser();
            releaser.Receipt = ForgeReceipt(releaser, operation);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Coordinator_DependencyViolation_NoFallback()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            ReturningReleaser releaser = new ReturningReleaser();
            releaser.Receipt = ForgeReceipt(MakeReleaser(), operation);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator(releaser);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));

            Assert.That(releaser.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.OwnershipLease.IsCreated, Is.True);
        }

        // ---- Result ----

        [Test]
        public void Result_NormalConstruction_ForwardsAllValues()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = coordinator.Execute(operation);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.RecoveryOwnerReleased));

            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Releaser, coordinator.Releaser), Is.True);
            Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            Assert.That(result.Receipt, Is.Not.Null);
            Assert.That(ReferenceEquals(result.Receipt.IssuedBy, coordinator.Releaser), Is.True);
            Assert.That(ReferenceEquals(result.LifecycleEvidence, operation.LifecycleEvidence), Is.True);
            Assert.That(ReferenceEquals(result.NotificationResult, operation.NotificationResult), Is.True);
            Assert.That(ReferenceEquals(result.OpenOutcome, operation.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(result.OwnershipLease, operation.OwnershipLease), Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(result.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
            Assert.That(result.CaptureIndexPath, Is.EqualTo(operation.CaptureIndexPath));

            Assert.That(operation.OwnershipLease.IsCreated, Is.False);
        }

        [Test]
        public void Result_UpstreamInvalidStillValid()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = MakeCoordinator();

            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = coordinator.Execute(operation);

            Assert.That(operation.LifecycleEvidence.IsValid, Is.False);
            Assert.That(operation.NotificationResult.IsValid, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.CanRelease, Is.False);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.RecoveryOwnerReleased));
        }

        [Test]
        public void Result_Uninitialized_ConvergesNone()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseResult));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_Create_NullArguments_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult sample = MakeResult();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = sample.IssuedBy;
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof proof =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof)GetField(sample, "_proof");
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = sample.Operation;
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = sample.Receipt;

            ArgumentNullException ex0 = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.Create(null, proof, operation, receipt));
            Assert.That(ex0.ParamName, Is.EqualTo("issuedBy"));

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.Create(coordinator, null, operation, receipt));
            Assert.That(ex1.ParamName, Is.EqualTo("proof"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.Create(coordinator, proof, null, receipt));
            Assert.That(ex2.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Result_Create_NullReceipt_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult sample = MakeResult();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = sample.IssuedBy;
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof proof =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof)GetField(sample, "_proof");
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = sample.Operation;

            // A null receipt is a dependency violation, not a structural null.
            Assert.Throws<InvalidOperationException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.Create(coordinator, proof, operation, null));
        }

        [Test]
        public void Result_Create_CorrelationMismatch_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult sample = MakeResult();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator coordinator = sample.IssuedBy;
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof proof =
                (CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.IssuanceProof)GetField(sample, "_proof");
            CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt receipt = sample.Receipt;

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation other = MakeReleaseOperation();
            Assert.Throws<InvalidOperationException>(
                () => CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.Create(coordinator, proof, other, receipt));
        }

        [Test]
        public void Result_IssuedBySubstitution_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = MakeResult();
            SetField(result, "_issuedBy", MakeCoordinator());
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_SharedReleaserDifferentCoordinator_Rejected()
        {
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser = MakeReleaser();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator first = MakeCoordinator(releaser);
            CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator second = MakeCoordinator(releaser);

            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation = MakeReleaseOperation();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = first.Execute(operation);

            SetField(result, "_issuedBy", second);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_CrossProofSubstitution_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult first = MakeResult();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult second = MakeResult();

            SetField(first, "_proof", GetField(second, "_proof"));
            Assert.That(first.IsValid, Is.False);
            Assert.That(first.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_CrossOperationSubstitution_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = MakeResult();
            SetField(result, "_operation", MakeReleaseOperation());
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_CrossReceiptSubstitution_Rejected()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult first = MakeResult();
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult second = MakeResult();

            SetField(first, "_receipt", GetField(second, "_receipt"));
            Assert.That(first.IsValid, Is.False);
            Assert.That(first.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_ReceiptInternalCorruption_False()
        {
            CaptureRunPublicationCaptureCompleteRecoveryReleaseResult result = MakeResult();
            SetField(result.Receipt, "_operation", MakeReleaseOperation());
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None));
        }

        [Test]
        public void Result_ProofGetterAbsent()
        {
            Assert.That(
                typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseResult).GetProperty(
                    "Proof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(
                typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseResult).GetProperty(
                    "IssuanceProof", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
        }

        [Test]
        public void Coordinator_ProofCannotBeExternallyMinted()
        {
            Type proofType = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator).GetNestedType(
                "IssuanceProof", BindingFlags.NonPublic);
            Assert.That(proofType, Is.Not.Null);

            Assert.That(proofType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(proofType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Length, Is.EqualTo(1));

            foreach (MethodInfo method in proofType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(proofType), method.Name + " must not return the proof.");
            }

            Type coordinatorType = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator);
            foreach (MethodInfo method in coordinatorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.IsPrivate)
                {
                    continue;
                }

                Assert.That(method.ReturnType, Is.Not.EqualTo(proofType), method.Name + " must not return the proof.");
            }
        }

        // ---- Status enum ----

        [Test]
        public void Status_EnumExplicitValues()
        {
            Assert.That(
                Enum.GetUnderlyingType(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus)),
                Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.None, Is.EqualTo(0));
            Assert.That((int)CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus.RecoveryOwnerReleased, Is.EqualTo(1));
        }

        // ---- Type shape ----

        [Test]
        public void Coordinator_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(fields.Any(f => f.FieldType == typeof(ICaptureRunPublicationCaptureCompleteRecoveryReleaser)), Is.True);
            Assert.That(fields.Any(f => f.FieldType == typeof(object)), Is.True);

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Result_TypeShape()
        {
            Type type = typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields, Is.Not.Empty);
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsValueType, Is.False, field.Name + " must be a reference field.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            // One private assignment constructor, no public constructor.
            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            // The atomic factory is the single validation-and-assignment path.
            MethodInfo create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(create, Is.Not.Null);
            Assert.That(create.ReturnType, Is.EqualTo(typeof(CaptureRunPublicationCaptureCompleteRecoveryReleaseResult)));
        }

        // ---- Source inspection ----

        [Test]
        public void Coordinator_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.cs"));

            AssertForbiddenDependencies(source);

            Assert.That(CountOccurrences(source, ".Release("), Is.EqualTo(1));
            Assert.That(source, Does.Not.Contain(".IsValid"));
            Assert.That(source, Does.Not.Contain(".Dispose()"));
        }

        [Test]
        public void Coordinator_Source_SingleCorrelationPath()
        {
            string coordinatorSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseCoordinator.cs"));
            string resultSource = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.cs"));

            // The coordinator never runs the correlation predicate itself; it
            // delegates to the atomic factory exactly once.
            Assert.That(coordinatorSource, Does.Not.Contain("IsCorrelated"));
            Assert.That(CountOccurrences(coordinatorSource, ".Create("), Is.EqualTo(1));

            // The factory is the sole predicate invocation on the construction
            // path. The only other occurrence is the IsValid property, which is
            // not part of construction; the private assignment constructor adds
            // none.
            Assert.That(CountOccurrences(resultSource, "IsCorrelated("), Is.EqualTo(3));
        }

        [Test]
        public void Result_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationCaptureCompleteRecoveryReleaseResult.cs"));

            AssertForbiddenDependencies(source);
            Assert.That(source, Does.Not.Contain(".IsValid"));
            Assert.That(source, Does.Not.Contain(".Dispose()"));
        }
    }
}
