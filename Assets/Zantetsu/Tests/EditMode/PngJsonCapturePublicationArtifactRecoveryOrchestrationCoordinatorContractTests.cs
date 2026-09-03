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
    public class PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinatorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        private static CaptureRunPublicationArtifactRecoveryAction ReinspectArtifacts => CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts;

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus ReinspectionRequired => CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus CaptureCompleteCleanupRequired => CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus OrphanedPreTraceStatus => CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus ArtifactSourceMissingStatus => CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus PublishedArtifactMissingStatus => CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus RunRootCollisionStatus => CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision;

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

        // ---- General helpers ----

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

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
        }

        private static long Probe(CaptureRunPublicationEvidenceStatus status, long expectedByteLength, long limit)
        {
            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
                    return expectedByteLength;

                case CaptureRunPublicationEvidenceStatus.Mismatch:
                    return 1;

                case CaptureRunPublicationEvidenceStatus.Invalid:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.LimitExceeded:
                    return checked(limit + 1);

                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
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

        private sealed class FakeArtifactInspector : IPngJsonCapturePublicationArtifactInspector
        {
            private readonly List<string> _log;

            public FakeArtifactInspector(List<string> log = null)
            {
                _log = log;
            }

            public int Calls { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public PngJsonCapturePublicationArtifactInspectionSnapshot Snapshot { get; set; }

            public Func<PngJsonCapturePublicationArtifactInspectionOperation, PngJsonCapturePublicationArtifactInspectionSnapshot> SnapshotFactory { get; set; }

            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
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

        private sealed class FakePublisher : IPngJsonCapturePublicationArtifactPublisher
        {
            private readonly List<string> _log;

            public FakePublisher(List<string> log = null)
            {
                _log = log;
            }

            public int Calls;

            public Exception ExceptionToThrow { get; set; }

            public PngJsonCapturePublicationArtifactPublishReceipt Publish(
                PngJsonCapturePublicationArtifactPublishOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                Calls++;
                _log?.Add("publish:" + operation.EntryIndex + ":" + operation.ArtifactKind);
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return PngJsonCapturePublicationArtifactPublishReceipt.Create(this, operation, token);
            }
        }

        private sealed class FakeCommitter : IPngJsonCaptureRunCaptureIndexCommitter
        {
            private readonly List<string> _log;

            public FakeCommitter(List<string> log = null)
            {
                _log = log;
            }

            public int Calls;

            public Exception ExceptionToThrow { get; set; }

            public PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
                PngJsonCaptureRunCaptureIndexCommitOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                Calls++;
                _log?.Add("commit:" + operation.Mode);
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return PngJsonCaptureRunCaptureIndexCommitReceipt.Create(this, operation, token);
            }
        }

        // ---- Lease ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        // ---- Fresh seed graph forging ----

        private CaptureRunInitializationSession MakeLifecycleSession(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator execution = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationExecutionReceipt receipt = execution.Execute(batch);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);

            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
            return issue.Session;
        }

        private static CaptureFrameDraftRegistry ForgeDraftRegistry(long testRunId)
        {
            CaptureFrameDraftRegistry registry =
                (CaptureFrameDraftRegistry)FormatterServices.GetUninitializedObject(typeof(CaptureFrameDraftRegistry));
            CaptureDraftRunContext run =
                (CaptureDraftRunContext)FormatterServices.GetUninitializedObject(typeof(CaptureDraftRunContext));
            SetField(run, "<TestRunId>k__BackingField", testRunId);
            SetField(registry, "_run", run);
            SetField(registry, "_pendingCount", 0);
            SetField(registry, "_reservationCount", 0);
            return registry;
        }

        private static CaptureArtifactRegistry ForgeArtifactRegistry()
        {
            CaptureArtifactRegistry registry =
                (CaptureArtifactRegistry)FormatterServices.GetUninitializedObject(typeof(CaptureArtifactRegistry));
            SetField(registry, "_reservedArtifactCount", 0);
            return registry;
        }

        private static CaptureEvidenceRunFreezeReceipt ForgeFreezeReceipt(
            CaptureRunInitializationSession session,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            CaptureFrameDraftRegistry drafts,
            CaptureArtifactRegistry artifacts)
        {
            long testRunId = session.TestRunId;

            CaptureEvidenceDraftCoordinator evidence =
                (CaptureEvidenceDraftCoordinator)FormatterServices.GetUninitializedObject(typeof(CaptureEvidenceDraftCoordinator));
            SetField(evidence, "_drafts", drafts);
            SetField(evidence, "_artifacts", artifacts);
            SetField(evidence, "_drainStarted", true);
            SetField(evidence, "_queuedCancelled", true);
            SetField(evidence, "_joined", true);
            SetField(evidence, "_occupied", new bool[0]);

            TraceLogger logger = (TraceLogger)FormatterServices.GetUninitializedObject(typeof(TraceLogger));
            SetField(logger, "_testRunId", testRunId);

            TraceFlightRecorder recorder = (TraceFlightRecorder)FormatterServices.GetUninitializedObject(typeof(TraceFlightRecorder));
            SetField(recorder, "_state", TraceFlightRecorderState.Frozen);
            SetField(recorder, "_logger", logger);

            FreezeTerminalTraceBufferBuilder bufferBuilder =
                (FreezeTerminalTraceBufferBuilder)FormatterServices.GetUninitializedObject(typeof(FreezeTerminalTraceBufferBuilder));
            SetField(bufferBuilder, "_draftRegistry", drafts);

            CaptureFrameFreezeTerminalCoordinator issuedBy =
                (CaptureFrameFreezeTerminalCoordinator)FormatterServices.GetUninitializedObject(typeof(CaptureFrameFreezeTerminalCoordinator));
            SetField(issuedBy, "_recorder", recorder);
            SetField(issuedBy, "_bufferBuilder", bufferBuilder);

            FreezeTerminalTraceBuffer terminalBuffer =
                (FreezeTerminalTraceBuffer)FormatterServices.GetUninitializedObject(typeof(FreezeTerminalTraceBuffer));
            SetField(terminalBuffer, "_testRunId", testRunId);

            CaptureEvidenceRunFreezeReceipt receipt =
                (CaptureEvidenceRunFreezeReceipt)FormatterServices.GetUninitializedObject(typeof(CaptureEvidenceRunFreezeReceipt));
            SetField(receipt, "_issuedBy", issuedBy);
            SetField(receipt, "_evidence", evidence);
            SetField(receipt, "_runSession", session);
            SetField(receipt, "_lockIdentityEvidence", lockIdentityEvidence);
            SetField(receipt, "_terminalBuffer", terminalBuffer);
            return receipt;
        }

        private CaptureEvidenceRunFreezeReceipt MakeValidFreezeReceipt(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationSession session = MakeLifecycleSession(layout, disposeLog, out owner, out CaptureRunLockIdentityEvidence identity);
            CaptureFrameDraftRegistry drafts = ForgeDraftRegistry(layout.TestRunId);
            CaptureArtifactRegistry artifacts = ForgeArtifactRegistry();
            return ForgeFreezeReceipt(session, identity, drafts, artifacts);
        }

        private static CaptureArtifactFileStore ForgeStore(CaptureRunRootLayout layout)
        {
            CaptureArtifactFileStore store =
                (CaptureArtifactFileStore)FormatterServices.GetUninitializedObject(typeof(CaptureArtifactFileStore));
            SetField(store, "_rootLayout", layout);
            SetField(store, "_publicationPlanPath", Path.Combine(layout.StagingRunRoot, "publication.plan"));
            return store;
        }

        private static CaptureEvidenceRunPublicationCoordinator ForgeCoordinator(CaptureArtifactFileStore store)
        {
            CaptureEvidenceRunPublicationCoordinator coordinator =
                (CaptureEvidenceRunPublicationCoordinator)FormatterServices.GetUninitializedObject(
                    typeof(CaptureEvidenceRunPublicationCoordinator));
            SetField(coordinator, "_store", store);
            SetField(coordinator, "_freshPublicationGate", new object());
            SetField(coordinator, "_recoveryReceiptAuthority", new object());
            return coordinator;
        }

        private static CaptureEvidenceRunPublicationCoordinator.IssuanceProof MintProof(
            CaptureEvidenceRunPublicationCoordinator coordinator,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CapturePublicationPlanWriteReceipt writeReceipt)
        {
            return new CaptureEvidenceRunPublicationCoordinator.IssuanceProof(
                coordinator,
                GetField(coordinator, "_freshPublicationGate"),
                freezeReceipt,
                writeReceipt,
                freezeReceipt.Drafts,
                freezeReceipt.Artifacts,
                freezeReceipt.LockIdentityEvidence);
        }

        private static CaptureArtifactDescriptor MakeImageDescriptor(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureArtifactDescriptor(
                "frame/" + idStr + "/image",
                CaptureArtifactKind.FrameImage,
                "image/png",
                1,
                "frames/" + idStr + ".png.stage",
                "frames/" + idStr + ".png",
                100 + id,
                HashA);
        }

        private static CaptureArtifactDescriptor MakeMetadataDescriptor(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureArtifactDescriptor(
                "frame/" + idStr + "/metadata",
                CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json",
                2,
                "frames/" + idStr + ".json.stage",
                "frames/" + idStr + ".json",
                200 + id,
                HashB);
        }

        private static CaptureFrameEvidenceEntry MakeFrameEvidence(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureFrameEvidenceEntry(
                id,
                new[] { "frame/" + idStr + "/image", "frame/" + idStr + "/metadata" });
        }

        private static CapturePublicationPlan MakeGenericPlan(long testRunId, long[] frameIds)
        {
            CaptureArtifactDescriptor[] descriptors = new CaptureArtifactDescriptor[frameIds.Length * 2];
            CaptureFrameEvidenceEntry[] evidence = new CaptureFrameEvidenceEntry[frameIds.Length];
            int d = 0;
            for (int i = 0; i < frameIds.Length; i++)
            {
                descriptors[d++] = MakeImageDescriptor(frameIds[i]);
                descriptors[d++] = MakeMetadataDescriptor(frameIds[i]);
                evidence[i] = MakeFrameEvidence(frameIds[i]);
            }

            Array.Sort(descriptors, (a, b) => string.CompareOrdinal(a.ArtifactId, b.ArtifactId));
            return new CapturePublicationPlan(testRunId, InitId, HashA, descriptors, evidence);
        }

        private CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(
            CapturePublicationPlan genericPlan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunRootLayout layout = MakeLayout(genericPlan.TestRunId);
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout, null, out owner);
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, genericPlan, store.PublicationPlanPath, 16);
            return CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator,
                MintProof(coordinator, freezeReceipt, writeReceipt),
                freezeReceipt,
                writeReceipt);
        }

        private PngJsonCaptureFrozenRunPublicationPlanBinding MakeSeedBinding(
            long[] frameIds,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CapturePublicationPlan genericPlan = MakeGenericPlan(3, frameIds);
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(genericPlan, out owner);
            return PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen);
        }

        private PngJsonCaptureFrozenRunArtifactInspectionSeed MakeSeed(
            long[] frameIds,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeSeedBinding(frameIds, out owner);
            return PngJsonCaptureFrozenRunArtifactInspectionSeedBuilder.Build(binding);
        }

        // ---- Recovery decision graph forging ----

        private static CaptureRunMarkerBinding MakeMarkerBinding(CaptureRunRootLayout layout)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
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
            return MakeRootObservation(role, true, Canonical, init, Canonical, ready);
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

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(null, out owner),
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

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation(1000, 4, 64, out owner);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(plan ?? MakePlan(), indexAuthoritative, captureIndexTemporary, out owner));
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan = null,
            bool indexAuthoritative = false,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null)
        {
            return MakeRecoveryAuthority(plan, indexAuthoritative, captureIndexTemporary, out _);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeRecoveryAuthority(null, false, null, out owner);
        }

        private static PngJsonCapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = 16,
            long sidecarByteLength = 32)
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
                HashA,
                HashA);
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId = 1,
            PngJsonCapturePublicationPlanEntry[] entries = null)
        {
            return new PngJsonCapturePublicationPlan(
                testRunId,
                InitId,
                HashA,
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

        private static PngJsonCapturePublicationArtifactInspectionOperation MakeOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount = 1000)
        {
            return PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactEntryObservation MakeIndexObservation(
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            int index,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar)
        {
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(index);
            PngJsonCapturePublicationPlanEntry entry = paths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, operation.MaximumSidecarByteCount);
            return PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                token, operation, paths,
                stagingPng, Probe(stagingPng, entry.PngByteLength, pngLimit),
                stagingSidecar, Probe(stagingSidecar, entry.SidecarByteLength, sidecarLimit),
                finalPng, Probe(finalPng, entry.PngByteLength, pngLimit),
                finalSidecar, Probe(finalSidecar, entry.SidecarByteLength, sidecarLimit));
        }

        // ---- Scenario helpers ----

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotFor(
            IPngJsonCapturePublicationArtifactInspector inspector,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount)
        {
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[stagingPng.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(token, operation, i, stagingPng[i], stagingSidecar[i], finalPng[i], finalSidecar[i]);
            }

            return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                inspector, operation, traceStatus, traceCount, entries);
        }

        private static FakeArtifactInspector MakeArtifactInspector(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100,
            List<string> log = null)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector(log);
            inspector.Snapshot = MakeSnapshotFor(
                inspector, operation, stagingPng, stagingSidecar, finalPng, finalSidecar, traceStatus, traceCount);
            return inspector;
        }

        private FakeArtifactInspector BuildPublishPngSidecarScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority());
            return MakeArtifactInspector(
                operation,
                new[] { EvMatchesExpected }, new[] { EvMatchesExpected }, new[] { EvAbsent }, new[] { EvAbsent },
                EvMatchesExpected, 100, log);
        }

        private FakeArtifactInspector BuildCommitScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority());
            return MakeArtifactInspector(
                operation,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvMatchesExpected }, new[] { EvMatchesExpected },
                EvMatchesExpected, 100, log);
        }

        private FakeArtifactInspector BuildOrphanedPreTraceScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority());
            return MakeArtifactInspector(
                operation,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent },
                EvAbsent, 0, log);
        }

        private FakeArtifactInspector BuildCaptureCompleteScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority(indexAuthoritative: true));
            return MakeArtifactInspector(
                operation,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvMatchesExpected }, new[] { EvMatchesExpected },
                EvMatchesExpected, 100, log);
        }

        private FakeArtifactInspector BuildArtifactSourceMissingScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority());
            return MakeArtifactInspector(
                operation,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvMatchesExpected },
                EvMatchesExpected, 100, log);
        }

        private FakeArtifactInspector BuildPublishedArtifactMissingScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority(indexAuthoritative: true));
            return MakeArtifactInspector(
                operation,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvMatchesExpected },
                EvMatchesExpected, 100, log);
        }

        private FakeArtifactInspector BuildRunRootCollisionScenario(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            List<string> log = null)
        {
            operation = MakeOperation(MakeRecoveryAuthority());
            return MakeArtifactInspector(
                operation,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent },
                EvMismatch, 100, log);
        }

        private static PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator MakeExecutionCoordinator(
            List<string> log = null)
        {
            return new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(
                new FakePublisher(log), new FakeCommitter(log));
        }

        private static PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator MakeOrchestrator(
            IPngJsonCapturePublicationArtifactInspector inspector,
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator executionCoordinator)
        {
            return new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, executionCoordinator);
        }

        private static PngJsonCapturePublicationArtifactRecoveryExecutionResult ForgeExecutionResult(
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator issuedBy,
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] completedSteps,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionResult forged =
                (PngJsonCapturePublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult));
            SetField(forged, "_issuedBy", issuedBy);
            SetField(forged, "_batch", batch);
            SetField(forged, "_completedSteps", completedSteps);
            SetField(forged, "_token", token);
            return forged;
        }

        // ---- Constructor contracts ----

        [Test]
        public void Coordinator_Constructor_NullDependencies_Rejected()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator();

            ArgumentNullException inspectorEx = Assert.Throws<ArgumentNullException>(
                () => new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(null, execution));
            Assert.That(inspectorEx.ParamName, Is.EqualTo("inspector"));

            ArgumentNullException executionEx = Assert.Throws<ArgumentNullException>(
                () => new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, null));
            Assert.That(executionEx.ParamName, Is.EqualTo("executionCoordinator"));
        }

        [Test]
        public void Result_Create_NullArguments_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);
            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult = result.ExecutionResult;
            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken token;
            executionResult.TryValidate(out token);

            Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(null, executionResult, token));
            Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(coordinator, null, token));
            Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(coordinator, executionResult, null));
        }

        // ---- Execute: null / invalid operation ----

        [Test]
        public void Execute_NullOperation_Rejected_InspectorNotContacted()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = new FakeArtifactInspector(log);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(inspector.Calls, Is.EqualTo(0));
            Assert.That(log, Is.Empty);
        }

        [Test]
        public void Execute_InvalidOperation_Rejected_InspectorNotContacted()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = new FakeArtifactInspector(log);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            PngJsonCapturePublicationArtifactInspectionOperation invalid =
                (PngJsonCapturePublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(invalid));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(inspector.Calls, Is.EqualTo(0));
            Assert.That(log, Is.Empty);
        }

        // ---- Execute: snapshot verification ----

        [Test]
        public void Execute_InspectorNullSnapshot_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());

            inspector.Snapshot = null;

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Execute_ForeignIssuerSnapshot_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());

            FakeArtifactInspector foreign = new FakeArtifactInspector();
            foreign.Snapshot = MakeSnapshotFor(
                foreign, operation,
                new[] { EvMatchesExpected }, new[] { EvMatchesExpected }, new[] { EvAbsent }, new[] { EvAbsent },
                EvMatchesExpected, 100);
            inspector.Snapshot = foreign.Snapshot;

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Execute_ForeignOperationSnapshot_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());

            PngJsonCapturePublicationArtifactInspectionOperation other = MakeOperation(MakeRecoveryAuthority());
            inspector.Snapshot = MakeSnapshotFor(
                inspector, other,
                new[] { EvMatchesExpected }, new[] { EvMatchesExpected }, new[] { EvAbsent }, new[] { EvAbsent },
                EvMatchesExpected, 100);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        // ---- Execute: exception propagation ----

        [Test]
        public void Execute_InspectorException_PropagatesIdentical_NoRetry()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation, log);
            IOException exception = new IOException("inspect failed");
            inspector.ExceptionToThrow = exception;
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(operation));
            Assert.That(ex, Is.SameAs(exception));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }));
        }

        [Test]
        public void Execute_PublishException_PropagatesIdentical_NoRetry_NoReinspect()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation, log);
            FakePublisher publisher = new FakePublisher(log) { ExceptionToThrow = new IOException("publish failed") };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution =
                new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, new FakeCommitter(log));
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);

            Assert.Throws<IOException>(() => coordinator.Execute(operation));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "inspect", "publish:0:Png" }));
        }

        [Test]
        public void Execute_CommitException_PropagatesIdentical_NoRetry_NoReinspect()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildCommitScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation, log);
            FakeCommitter committer = new FakeCommitter(log) { ExceptionToThrow = new IOException("commit failed") };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution =
                new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(new FakePublisher(log), committer);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);

            Assert.Throws<IOException>(() => coordinator.Execute(operation));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "inspect", "commit:CreateTemporaryAndCommit" }));
        }

        // ---- Execute: call order ----

        [Test]
        public void Execute_Publish_InspectorFirst_EachStepOnce()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation, log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator(log);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            Assert.That(log, Is.EqualTo(new[] { "inspect", "publish:0:Png", "publish:0:Sidecar" }));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(ReinspectionRequired));
        }

        [Test]
        public void Execute_Commit_CalledOnce()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildCommitScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation, log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator(log);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            Assert.That(log, Is.EqualTo(new[] { "inspect", "commit:CreateTemporaryAndCommit" }));
            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(CaptureCompleteCleanupRequired));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_StopDispositions_NoBackendCalls()
        {
            List<string> log = new List<string>();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator(log);

            FakeArtifactInspector orphanedInspector = BuildOrphanedPreTraceScenario(out PngJsonCapturePublicationArtifactInspectionOperation orphanedOp, log);
            FakeArtifactInspector captureCompleteInspector = BuildCaptureCompleteScenario(out PngJsonCapturePublicationArtifactInspectionOperation ccOp, log);
            FakeArtifactInspector sourceMissingInspector = BuildArtifactSourceMissingScenario(out PngJsonCapturePublicationArtifactInspectionOperation smOp, log);
            FakeArtifactInspector publishedMissingInspector = BuildPublishedArtifactMissingScenario(out PngJsonCapturePublicationArtifactInspectionOperation pmOp, log);
            FakeArtifactInspector collisionInspector = BuildRunRootCollisionScenario(out PngJsonCapturePublicationArtifactInspectionOperation rcOp, log);

            MakeOrchestrator(orphanedInspector, execution).Execute(orphanedOp);
            MakeOrchestrator(captureCompleteInspector, execution).Execute(ccOp);
            MakeOrchestrator(sourceMissingInspector, execution).Execute(smOp);
            MakeOrchestrator(publishedMissingInspector, execution).Execute(pmOp);
            MakeOrchestrator(collisionInspector, execution).Execute(rcOp);

            Assert.That(log.Count, Is.EqualTo(5));
            foreach (string entry in log)
            {
                Assert.That(entry, Is.EqualTo("inspect"));
            }
        }

        // ---- Status mapping ----

        [Test]
        public void Execute_StatusMapping_AllDispositions()
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator();

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult publishResult = MakeOrchestrator(
                BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation publishOp), execution).Execute(publishOp);
            Assert.That(publishResult.Status, Is.EqualTo(ReinspectionRequired));

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult commitResult = MakeOrchestrator(
                BuildCommitScenario(out PngJsonCapturePublicationArtifactInspectionOperation commitOp), execution).Execute(commitOp);
            Assert.That(commitResult.Status, Is.EqualTo(CaptureCompleteCleanupRequired));

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult captureCompleteResult = MakeOrchestrator(
                BuildCaptureCompleteScenario(out PngJsonCapturePublicationArtifactInspectionOperation ccOp), execution).Execute(ccOp);
            Assert.That(captureCompleteResult.Status, Is.EqualTo(CaptureCompleteCleanupRequired));

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult orphanedResult = MakeOrchestrator(
                BuildOrphanedPreTraceScenario(out PngJsonCapturePublicationArtifactInspectionOperation orphanedOp), execution).Execute(orphanedOp);
            Assert.That(orphanedResult.Status, Is.EqualTo(OrphanedPreTraceStatus));

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult sourceMissingResult = MakeOrchestrator(
                BuildArtifactSourceMissingScenario(out PngJsonCapturePublicationArtifactInspectionOperation smOp), execution).Execute(smOp);
            Assert.That(sourceMissingResult.Status, Is.EqualTo(ArtifactSourceMissingStatus));

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult publishedMissingResult = MakeOrchestrator(
                BuildPublishedArtifactMissingScenario(out PngJsonCapturePublicationArtifactInspectionOperation pmOp), execution).Execute(pmOp);
            Assert.That(publishedMissingResult.Status, Is.EqualTo(PublishedArtifactMissingStatus));

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult collisionResult = MakeOrchestrator(
                BuildRunRootCollisionScenario(out PngJsonCapturePublicationArtifactInspectionOperation rcOp), execution).Execute(rcOp);
            Assert.That(collisionResult.Status, Is.EqualTo(RunRootCollisionStatus));
        }

        [Test]
        public void Execute_ReinspectionRequired_InspectorOnce()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(ReinspectionRequired));
        }

        [Test]
        public void Execute_CaptureCompleteCleanupRequired_NoCleanupContact()
        {
            List<string> log = new List<string>();
            FakeArtifactInspector inspector = BuildCaptureCompleteScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation, log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator(log);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            Assert.That(inspector.Calls, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(CaptureCompleteCleanupRequired));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }), "CaptureComplete must not contact publisher or committer.");
        }

        // ---- Result forwarding / correlation ----

        [Test]
        public void Result_Forwarding_ReferenceIdentity()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator();
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            Assert.That(result.IssuedBy, Is.SameAs(coordinator));
            Assert.That(result.ExecutionResult, Is.Not.Null);
            Assert.That(result.ExecutionResult.IssuedBy, Is.SameAs(execution));
            Assert.That(result.InspectionSnapshot, Is.SameAs(inspector.Snapshot));
            Assert.That(result.Decision, Is.SameAs(result.ActionPlan.Decision));
            Assert.That(result.Batch, Is.SameAs(result.ExecutionResult.Batch));
            Assert.That(result.Authority, Is.SameAs(operation.Authority));
            Assert.That(result.AuthorityKind, Is.EqualTo(operation.AuthorityKind));
            Assert.That(result.AuthoritativePlan, Is.SameAs(operation.Plan));
            Assert.That(result.Status, Is.EqualTo(ReinspectionRequired));
            Assert.That(result.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts));
            Assert.That(result.RootLayout, Is.SameAs(operation.RootLayout));
            Assert.That(result.LockIdentityEvidence, Is.SameAs(operation.LockIdentityEvidence));
            Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(result.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_Create_ForeignExecutionCoordinator_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator executionA = MakeExecutionCoordinator();
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, executionA);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult good = coordinator.Execute(operation);

            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator executionB = MakeExecutionCoordinator();
            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResultB = executionB.Execute(good.ExecutionResult.Batch);
            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken tokenB;
            executionResultB.TryValidate(out tokenB);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(coordinator, executionResultB, tokenB));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_Create_CrossTokenSubstitution_Rejected()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution = MakeExecutionCoordinator();
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator = MakeOrchestrator(inspector, execution);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult good = coordinator.Execute(operation);

            FakeArtifactInspector otherInspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation otherOp);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult other =
                MakeOrchestrator(otherInspector, MakeExecutionCoordinator()).Execute(otherOp);
            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken otherToken;
            other.ExecutionResult.TryValidate(out otherToken);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(
                    coordinator, good.ExecutionResult, otherToken));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_CorruptedExecutionResult_IsValidFalse()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);
            Assert.That(result.IsValid, Is.True);

            // Null out the execution result's completed-step array: the held
            // proof's O(1) exact binding must detect the swap and report false
            // without throwing.
            SetField(result.ExecutionResult, "_completedSteps", null);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_Create_Rejected_AfterExecutionResultCorruption()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken token =
                (PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken)GetField(result, "_token");

            // Corrupt the execution result after issuance; Create must reject
            // because the proof's O(1) exact binding no longer matches.
            SetField(result.ExecutionResult, "_completedSteps", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.Create(
                    coordinator, result.ExecutionResult, token));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        // ---- Owner / forge ----

        [Test]
        public void Result_OwnerExpired_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);
            FakeArtifactInspector inspector = MakeArtifactInspector(
                operation,
                new[] { EvMatchesExpected }, new[] { EvMatchesExpected }, new[] { EvAbsent }, new[] { EvAbsent },
                EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);
            Assert.That(result.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void ForgedValues_IsValidFalse_WithoutException()
        {
            FakeArtifactInspector inspector = BuildPublishPngSidecarScenario(out PngJsonCapturePublicationArtifactInspectionOperation operation);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator coordinator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = coordinator.Execute(operation);

            // Foreign inspector in the snapshot graph.
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult foreignInspector =
                (PngJsonCapturePublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryOrchestrationResult));
            SetField(foreignInspector, "_issuedBy", coordinator);
            SetField(foreignInspector, "_executionResult", result.ExecutionResult);
            SetField(foreignInspector, "_token", GetField(result, "_token"));
            FakeArtifactInspector foreign = new FakeArtifactInspector();
            SetField(result.ExecutionResult.Batch.ActionPlan.Decision.Snapshot, "_issuedBy", foreign);
            Assert.That(foreignInspector.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void OrchestrationCoordinator_Shape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void OrchestrationResult_Shape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryOrchestrationResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Shape_NoLeaseExposure()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator),
                typeof(PngJsonCapturePublicationArtifactRecoveryOrchestrationResult)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(byte[])
                        || field.FieldType == typeof(Stream)
                        || field.FieldType == typeof(ICaptureRunLockHandle),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, owner, bytes, stream, or handle.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || prop.PropertyType == typeof(byte[])
                        || prop.PropertyType == typeof(Stream)
                        || prop.PropertyType == typeof(ICaptureRunLockHandle),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a lease, owner, token, bytes, stream, or handle.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || method.ReturnType == typeof(byte[])
                        || method.ReturnType == typeof(Stream)
                        || method.ReturnType == typeof(ICaptureRunLockHandle),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a lease, owner, token, bytes, stream, or handle.");
                }
            }
        }

        // ---- Source ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = ReadSource(relativePath);

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("SerializeCanonical"));
                Assert.That(source, Does.Not.Contain("GetCanonicalBytes"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }
        }

        [Test]
        public void Source_NoRedundantExecutionResultValidation()
        {
            string coordinatorSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator.cs");
            string resultSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.cs");

            // The coordinator validates the execution result exactly once via
            // TryValidate; the result factory must only confirm the O(1) proof
            // binding and never re-validate.
            Assert.That(coordinatorSource, Does.Contain("executionResult.TryValidate(out token)"));
            Assert.That(resultSource, Does.Contain("token.IsIssuedFor(executionResult)"));
            Assert.That(resultSource, Does.Not.Contain("executionResult.TryValidate"));
            Assert.That(resultSource, Does.Not.Contain("SerializeCanonical"));
        }
    }
}
