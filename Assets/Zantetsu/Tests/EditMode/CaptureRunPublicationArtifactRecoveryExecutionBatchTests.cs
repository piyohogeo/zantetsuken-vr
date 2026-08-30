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
    public class CaptureRunPublicationArtifactRecoveryExecutionBatchTests
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
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            CapturePublicationPlan plan = null,
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

        private static CaptureRunPublicationPathSet GetPublicationPaths(CaptureRunPublicationArtifactRecoveryActionPlan plan)
        {
            return plan.Decision.PublicationDecision.Snapshot.Operation.PublicationPaths;
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
            CapturePublicationPlan plan = MakePlan();
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
            CapturePublicationPlan plan = MakePlan();
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

        private static CaptureRunPublicationArtifactRecoveryExecutionBatch ForgeBatch(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryPreparedStep[] preparedSteps)
        {
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = (CaptureRunPublicationArtifactRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryExecutionBatch));
            SetField(batch, "_actionPlan", actionPlan);
            SetField(batch, "_preparedSteps", preparedSteps);
            return batch;
        }

        private static CaptureRunPublicationArtifactRecoveryPreparedStep ForgePreparedStep(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            CaptureRunPublicationArtifactPublishOperation publishOperation,
            CaptureRunCaptureIndexCommitOperation commitOperation)
        {
            CaptureRunPublicationArtifactRecoveryPreparedStep step = (CaptureRunPublicationArtifactRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryPreparedStep));
            SetField(step, "_actionPlan", actionPlan);
            SetField(step, "_stepIndex", stepIndex);
            SetField(step, "_publishOperation", publishOperation);
            SetField(step, "_captureIndexCommitOperation", commitOperation);
            return step;
        }

        private static CaptureRunPublicationArtifactPublishOperation ForgePublishOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            CaptureRunPublicationArtifactPathSet artifactPaths)
        {
            CaptureRunPublicationArtifactPublishOperation operation = (CaptureRunPublicationArtifactPublishOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactPublishOperation));
            SetField(operation, "_actionPlan", actionPlan);
            SetField(operation, "_stepIndex", stepIndex);
            SetField(operation, "_artifactPaths", artifactPaths);
            return operation;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactRecoveryExecutionBatchTests).Assembly.Location);
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
            public int Calls;

            public CaptureRunPublicationArtifactPublishReceipt Publish(CaptureRunPublicationArtifactPublishOperation operation)
            {
                Calls++;
                return new CaptureRunPublicationArtifactPublishReceipt(this, operation);
            }
        }

        private sealed class FakeCommitter : ICaptureRunCaptureIndexCommitter
        {
            public int Calls;

            public CaptureRunCaptureIndexCommitReceipt Commit(CaptureRunCaptureIndexCommitOperation operation)
            {
                Calls++;
                return new CaptureRunCaptureIndexCommitReceipt(this, operation);
            }
        }

        // ---- Build / rejection ----

        [Test]
        public void Builder_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(null));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Builder_InvalidPlan_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = (CaptureRunPublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_AllDispositions_Build()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan[] plans = new[]
            {
                BuildOrphanedPreTracePlan(),
                BuildPublishPngSidecarPlan(out _),
                BuildCommitPlan(out _, out _),
                BuildCaptureCompletePlan(),
                BuildArtifactSourceMissingPlan(),
                BuildPublishedArtifactMissingPlan(),
                BuildRunRootCollisionPlan()
            };

            foreach (CaptureRunPublicationArtifactRecoveryActionPlan plan in plans)
            {
                CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);
                Assert.That(batch.IsValid, Is.True);
                Assert.That(batch.Count, Is.EqualTo(plan.Count));
                Assert.That(batch.Disposition, Is.EqualTo(plan.Disposition));
            }
        }

        [Test]
        public void Batch_GetStep_FixedOrderSameReference_AndIndexOutOfRange()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            for (int i = 0; i < batch.Count; i++)
            {
                Assert.That(batch.GetStep(i).StepIndex, Is.EqualTo(i));
                Assert.That(batch.GetStep(i).Step, Is.SameAs(plan.GetStep(i)));
            }

            foreach (int bad in new[] { -1, batch.Count, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetStep(bad));
                Assert.That(ex.ParamName, Is.EqualTo("index"));
            }
        }

        // ---- Per-disposition materialization ----

        [Test]
        public void Batch_PublishMissingArtifacts_PlanOrderPngThenSidecarThenReinspect()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(batch.Count, Is.EqualTo(3));

            Assert.That(batch.GetStep(0).Action, Is.EqualTo(PublishArtifact));
            Assert.That(batch.GetStep(0).Step.EntryIndex, Is.EqualTo(0));
            Assert.That(batch.GetStep(0).Step.ArtifactKind, Is.EqualTo(Png));
            Assert.That(batch.GetStep(0).PublishOperation, Is.Not.Null);
            Assert.That(batch.GetStep(0).CaptureIndexCommitOperation, Is.Null);

            Assert.That(batch.GetStep(1).Action, Is.EqualTo(PublishArtifact));
            Assert.That(batch.GetStep(1).Step.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(batch.GetStep(1).PublishOperation, Is.Not.Null);
            Assert.That(batch.GetStep(1).CaptureIndexCommitOperation, Is.Null);

            Assert.That(batch.GetStep(2).Action, Is.EqualTo(ReinspectArtifacts));
            Assert.That(batch.GetStep(2).PublishOperation, Is.Null);
            Assert.That(batch.GetStep(2).CaptureIndexCommitOperation, Is.Null);
        }

        [Test]
        public void Batch_CommitCaptureIndex_SingleStepCommitOperationOnly()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(batch.Count, Is.EqualTo(1));
            Assert.That(batch.GetStep(0).Action, Is.EqualTo(CommitCaptureIndex));
            Assert.That(batch.GetStep(0).PublishOperation, Is.Null);
            Assert.That(batch.GetStep(0).CaptureIndexCommitOperation, Is.Not.Null);

            CaptureRunCaptureIndexCommitOperation expected = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);
            CaptureRunCaptureIndexCommitOperation actual = batch.GetStep(0).CaptureIndexCommitOperation;

            Assert.That(actual.Mode, Is.EqualTo(expected.Mode));
            Assert.That(actual.TemporaryPath, Is.EqualTo(expected.TemporaryPath));
            Assert.That(actual.FinalPath, Is.EqualTo(expected.FinalPath));
            Assert.That(actual.GetCanonicalBytes(), Is.EqualTo(expected.GetCanonicalBytes()));
        }

        [Test]
        public void Batch_RoutingStopDispositions_NoOperations()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan[] plans = new[]
            {
                BuildOrphanedPreTracePlan(),
                BuildCaptureCompletePlan(),
                BuildArtifactSourceMissingPlan(),
                BuildPublishedArtifactMissingPlan(),
                BuildRunRootCollisionPlan()
            };

            foreach (CaptureRunPublicationArtifactRecoveryActionPlan plan in plans)
            {
                CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);
                Assert.That(batch.Count, Is.EqualTo(1));
                Assert.That(batch.GetStep(0).PublishOperation, Is.Null);
                Assert.That(batch.GetStep(0).CaptureIndexCommitOperation, Is.Null);
            }
        }

        [Test]
        public void Batch_ActionPlanStepOperation_ReferenceEquals()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(batch.ActionPlan, Is.SameAs(plan));
            Assert.That(batch.Decision, Is.SameAs(plan.Decision));
            Assert.That(batch.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(batch.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(batch.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(batch.LockLease, Is.SameAs(operation.LockLease));

            Assert.That(batch.GetStep(0).Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(batch.GetStep(0).PublishOperation.ActionPlan, Is.SameAs(plan));
            Assert.That(batch.GetStep(0).PublishOperation.Step, Is.SameAs(plan.GetStep(0)));
        }

        // ---- Side-effect boundary ----

        [Test]
        public void Batch_Build_PublisherAndCommitterNotInvoked()
        {
            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(publisher.Calls, Is.EqualTo(0));
            Assert.That(committer.Calls, Is.EqualTo(0));
            Assert.That(batch.IsValid, Is.True);
        }

        [Test]
        public void Batch_Build_LeaseNotDisposed()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(operation.LockLease.IsCreated, Is.True);
            Assert.That(batch.LockLease.IsCreated, Is.True);
        }

        [Test]
        public void Batch_Build_InputGraphUnchanged()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out CaptureRunPublicationArtifactInspectionOperation operation);
            string idBefore = plan.RunInitializationId;

            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.RunInitializationId, Is.EqualTo(idBefore));
            Assert.That(operation.LockLease.IsCreated, Is.True);
            Assert.That(batch.IsValid, Is.True);
        }

        // ---- Token ----

        [Test]
        public void PreparedStep_CrossToken_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan planA = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryActionPlan planB = BuildPublishPngSidecarPlan(out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunPublicationArtifactRecoveryPreparedStep(planB, tokenA, 0, null, null));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void Batch_LeaseReleased_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out CaptureRunPublicationArtifactInspectionOperation operation, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(batch.IsValid, Is.True);

            operation.LockLease.Dispose();

            Assert.That(batch.IsValid, Is.False);
        }

        // ---- Forge defense ----

        [Test]
        public void Batch_ForgedFields_IsValidFalse_NoException()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out CaptureRunPublicationArtifactInspectionOperation operation);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);
            Assert.That(batch.IsValid, Is.True);

            // Null array.
            Assert.That(ForgeBatch(plan, null).IsValid, Is.False);

            // Array length mismatch.
            CaptureRunPublicationArtifactRecoveryPreparedStep[] tooShort = new CaptureRunPublicationArtifactRecoveryPreparedStep[1];
            tooShort[0] = batch.GetStep(0);
            Assert.That(ForgeBatch(plan, tooShort).IsValid, Is.False);

            // Null element.
            CaptureRunPublicationArtifactRecoveryPreparedStep[] nullElement = new CaptureRunPublicationArtifactRecoveryPreparedStep[3];
            nullElement[0] = batch.GetStep(0);
            nullElement[1] = null;
            nullElement[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, nullElement).IsValid, Is.False);

            // Order swap.
            CaptureRunPublicationArtifactRecoveryPreparedStep[] swapped = new CaptureRunPublicationArtifactRecoveryPreparedStep[3];
            swapped[0] = batch.GetStep(1);
            swapped[1] = batch.GetStep(0);
            swapped[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, swapped).IsValid, Is.False);

            // Duplicate prepared step.
            CaptureRunPublicationArtifactRecoveryPreparedStep[] duplicated = new CaptureRunPublicationArtifactRecoveryPreparedStep[3];
            duplicated[0] = batch.GetStep(0);
            duplicated[1] = batch.GetStep(0);
            duplicated[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, duplicated).IsValid, Is.False);

            // Foreign action plan.
            CaptureRunPublicationArtifactRecoveryActionPlan foreignPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryPreparedStep[] foreignSteps = new CaptureRunPublicationArtifactRecoveryPreparedStep[3];
            foreignSteps[0] = batch.GetStep(0);
            foreignSteps[1] = batch.GetStep(1);
            foreignSteps[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(foreignPlan, foreignSteps).IsValid, Is.False);

            // Foreign publish operation (wrong artifact path set).
            CaptureRunPublicationArtifactPathSet foreignPaths = MakeOperation().GetArtifactPaths(0);
            CaptureRunPublicationArtifactPublishOperation forgedPublish = ForgePublishOperation(plan, 0, foreignPaths);
            CaptureRunPublicationArtifactRecoveryPreparedStep[] forgedPublishArr = new CaptureRunPublicationArtifactRecoveryPreparedStep[3];
            forgedPublishArr[0] = ForgePreparedStep(plan, 0, forgedPublish, null);
            forgedPublishArr[1] = batch.GetStep(1);
            forgedPublishArr[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, forgedPublishArr).IsValid, Is.False);

            // Publish/commit operation mix-up: a commit operation in a publish step.
            CaptureRunPublicationArtifactRecoveryActionPlan commitPlan = BuildCommitPlan(out _, out _);
            CaptureRunCaptureIndexCommitOperation commitOperation = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(commitPlan).GetStep(0).CaptureIndexCommitOperation;
            CaptureRunPublicationArtifactRecoveryPreparedStep[] mixedArr = new CaptureRunPublicationArtifactRecoveryPreparedStep[3];
            mixedArr[0] = ForgePreparedStep(plan, 0, null, commitOperation);
            mixedArr[1] = batch.GetStep(1);
            mixedArr[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, mixedArr).IsValid, Is.False);
        }

        [Test]
        public void Batch_CommitOperationBytesCorrupted_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);
            Assert.That(batch.IsValid, Is.True);

            CaptureRunCaptureIndexCommitOperation commitOperation = batch.GetStep(0).CaptureIndexCommitOperation;
            SetField(commitOperation, "_canonicalBytes", new byte[] { 1, 2, 3 });

            Assert.That(batch.IsValid, Is.False);
        }

        // ---- Sharing ----

        [Test]
        public void Batch_TwoBuilds_NonSharedBatchStepsOperations_SharedActionPlan()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan(out _);

            CaptureRunPublicationArtifactRecoveryExecutionBatch first = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);
            CaptureRunPublicationArtifactRecoveryExecutionBatch second = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(ReferenceEquals(first.GetStep(0), second.GetStep(0)), Is.False);
            Assert.That(ReferenceEquals(first.GetStep(0).PublishOperation, second.GetStep(0).PublishOperation), Is.False);
            Assert.That(first.ActionPlan, Is.SameAs(second.ActionPlan));
            Assert.That(first.ActionPlan, Is.SameAs(plan));
        }

        // ---- Shape ----

        [Test]
        public void PreparedStep_FieldShape_FourFields()
        {
            FieldInfo[] fields = typeof(CaptureRunPublicationArtifactRecoveryPreparedStep).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationArtifactRecoveryActionPlan)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(int)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationArtifactPublishOperation)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunCaptureIndexCommitOperation)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Batch_FieldShape_TwoFields_NoStaticState()
        {
            FieldInfo[] fields = typeof(CaptureRunPublicationArtifactRecoveryExecutionBatch).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationArtifactRecoveryActionPlan)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationArtifactRecoveryPreparedStep[])));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(typeof(CaptureRunPublicationArtifactRecoveryExecutionBatch).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
            Assert.That(typeof(CaptureRunPublicationArtifactRecoveryPreparedStep).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Types_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            foreach (Type type in new[] { typeof(CaptureRunPublicationArtifactRecoveryPreparedStep), typeof(CaptureRunPublicationArtifactRecoveryExecutionBatch) })
            {
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
        }

        [Test]
        public void Builder_IsStaticWithNoState()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        // ---- Linearity / source ----

        [Test]
        public void Batch_LargePlan_LinearBuild()
        {
            int count = 500;
            CapturePublicationPlan planEntries = MakePlan(entries: MakeEntries(count));
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(plan: planEntries, maximumEntryCount: count);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation, operation.GetArtifactPaths(i),
                    EvMatchesExpected, PngBytes, EvMatchesExpected, SidecarBytes,
                    EvAbsent, 0, EvAbsent, 0);
            }

            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPlan(operation, entries);
            CaptureRunPublicationArtifactRecoveryExecutionBatch batch = CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.Build(plan);

            Assert.That(batch.Count, Is.EqualTo(2 * count + 1));
            Assert.That(batch.GetStep(0).Action, Is.EqualTo(PublishArtifact));
            Assert.That(batch.GetStep(0).Step.EntryIndex, Is.EqualTo(0));
            Assert.That(batch.GetStep(2 * count - 1).Step.EntryIndex, Is.EqualTo(count - 1));
            Assert.That(batch.GetStep(2 * count).Action, Is.EqualTo(ReinspectArtifacts));
            Assert.That(batch.IsValid, Is.True);
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string preparedSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryPreparedStep.cs"));
            string batchSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionBatch.cs"));
            string builderSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionBatchBuilder.cs"));

            foreach (string source in new[] { preparedSource, batchSource, builderSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("Deserialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("TraceLogger"));
                Assert.That(source, Does.Not.Contain("TraceRunManifest"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("Guid"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("GetCanonicalBytes"));
                Assert.That(source, Does.Not.Contain("Publisher"));
                Assert.That(source, Does.Not.Contain("Committer"));
                Assert.That(source, Does.Not.Contain("HashSet"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
            }
        }

        [Test]
        public void Source_FullPlanValidationOnce_AndNoPartialPublish()
        {
            string batchSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryExecutionBatch.cs"));

            Assert.That(batchSource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(batchSource, Does.Contain("AcquireValidationToken"));

            int loopIndex = batchSource.IndexOf("for (int i = 0; i < count; i++)", StringComparison.Ordinal);
            Assert.That(loopIndex, Is.GreaterThan(0));

            // The constructor's materialization loop must not re-validate the
            // whole plan; it uses the already-issued token through the
            // index-local factory paths.
            int fieldAssignIndex = batchSource.IndexOf("_actionPlan = actionPlan;", StringComparison.Ordinal);
            Assert.That(fieldAssignIndex, Is.GreaterThan(loopIndex));

            string loopBody = batchSource.Substring(loopIndex, fieldAssignIndex - loopIndex);
            Assert.That(loopBody, Does.Not.Contain("AcquireValidationToken"));
            Assert.That(loopBody, Does.Not.Contain("actionPlan.IsValid"));

            // The step array is a local filled by the loop; the field is only
            // assigned after the loop, so a mid-construction failure never
            // publishes a partial batch.
            Assert.That(batchSource, Does.Contain("_preparedSteps = preparedSteps;"));
            int assignIndex = batchSource.IndexOf("_preparedSteps = preparedSteps;", StringComparison.Ordinal);
            Assert.That(assignIndex, Is.GreaterThan(loopIndex));
        }
    }
}
