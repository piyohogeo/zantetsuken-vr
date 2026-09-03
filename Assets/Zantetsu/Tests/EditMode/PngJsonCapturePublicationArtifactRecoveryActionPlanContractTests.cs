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
    public class PngJsonCapturePublicationArtifactRecoveryActionPlanContractTests
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

        private static CaptureRunPublicationArtifactRecoveryDisposition OrphanedPreTrace => CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishMissingArtifacts => CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;

        private static CaptureRunPublicationArtifactRecoveryDisposition CommitCaptureIndex => CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryDisposition CaptureComplete => CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;

        private static CaptureRunPublicationArtifactRecoveryDisposition ArtifactSourceMissing => CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishedArtifactMissing => CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition RunRootCollision => CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        private static CaptureRunPublicationArtifactRecoveryAction ReinspectArtifacts => CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts;

        private static CaptureRunPublicationArtifactRecoveryAction CommitCaptureIndexAction => CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryAction ContinueCaptureCompleteCleanup => CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup;

        private static CaptureRunPublicationArtifactRecoveryAction StopOrphanedPreTrace => CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryAction StopArtifactSourceMissing => CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryAction StopPublishedArtifactMissing => CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryAction StopRunRootCollision => CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision;

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

        private static CaptureRunPublicationArtifactKind None => CaptureRunPublicationArtifactKind.None;

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
            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
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

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeFreshAuthority(params long[] frameIds)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(MakeSeed(frameIds, out _));
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

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotArray(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar,
            long maximumPngByteCount = 1000)
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, maximumPngByteCount);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            Assert.That(operation.EntryCount, Is.EqualTo(stagingPng.Length));

            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[stagingPng.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(token, operation, i, stagingPng[i], stagingSidecar[i], finalPng[i], finalSidecar[i]);
            }

            return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                new FakeArtifactInspector(), operation, traceStatus, traceCount, entries);
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotSingle(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar,
            long maximumPngByteCount = 1000)
        {
            return MakeSnapshotArray(
                authority,
                traceStatus,
                traceCount,
                new[] { stagingPng },
                new[] { stagingSidecar },
                new[] { finalPng },
                new[] { finalSidecar },
                maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactRecoveryDecision ClassifyDecision(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
        {
            return PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(snapshot);
        }

        private static PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPlan(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
        {
            return PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(ClassifyDecision(snapshot));
        }

        private static void AssertStep(
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan,
            int index,
            CaptureRunPublicationArtifactRecoveryAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind kind)
        {
            CaptureRunPublicationArtifactRecoveryStep step = plan.GetStep(index);
            Assert.That(step, Is.Not.Null);
            Assert.That(step.Matches(action, entryIndex, kind), Is.True);
        }

        private static void AssertSingleRouting(
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan,
            CaptureRunPublicationArtifactRecoveryAction action)
        {
            Assert.That(plan.Count, Is.EqualTo(1));
            AssertStep(plan, 0, action, -1, None);
        }

        // ---- Factory rejection ----

        [Test]
        public void Create_NullDecision_ThrowsArgumentNullException()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(null));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Create_InvalidDecision_ThrowsArgumentException()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(
                MakeSnapshotSingle(MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected));
            SetField(decision, "_snapshot", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Create_UndefinedDisposition_ThrowsArgumentException()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(
                MakeSnapshotSingle(MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected));
            SetField(decision, "_disposition", CaptureRunPublicationArtifactRecoveryDisposition.None);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Create_PublishMissingArtifacts_ZeroPublishable_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);
            Assert.That(decision.Disposition, Is.EqualTo(CommitCaptureIndex));

            SetField(decision, "_disposition", PublishMissingArtifacts);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        // ---- Step table ----

        [Test]
        public void Recovery_StepTable_AllSevenDispositions()
        {
            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent)), StopOrphanedPreTrace);

            PngJsonCapturePublicationArtifactRecoveryActionPlan publish = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
            Assert.That(publish.Count, Is.EqualTo(2));
            AssertStep(publish, 0, PublishArtifact, 0, Png);
            AssertStep(publish, 1, ReinspectArtifacts, -1, None);

            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected)), CommitCaptureIndexAction);

            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(indexAuthoritative: true), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected)), ContinueCaptureCompleteCleanup);

            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected)), StopArtifactSourceMissing);

            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(indexAuthoritative: true), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected)), StopPublishedArtifactMissing);

            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMismatch, 1, EvAbsent, EvAbsent, EvAbsent, EvAbsent)), StopRunRootCollision);
        }

        [Test]
        public void Fresh_StepTable_SampleDispositions()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan publish = BuildPlan(MakeSnapshotSingle(
                MakeFreshAuthority(10), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
            Assert.That(publish.Disposition, Is.EqualTo(PublishMissingArtifacts));
            Assert.That(publish.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));
            Assert.That(publish.Count, Is.EqualTo(2));
            AssertStep(publish, 0, PublishArtifact, 0, Png);
            AssertStep(publish, 1, ReinspectArtifacts, -1, None);

            AssertSingleRouting(BuildPlan(MakeSnapshotSingle(
                MakeFreshAuthority(10), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent)), StopOrphanedPreTrace);
        }

        [Test]
        public void Publish_PngOnly()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));

            Assert.That(plan.Count, Is.EqualTo(2));
            AssertStep(plan, 0, PublishArtifact, 0, Png);
            AssertStep(plan, 1, ReinspectArtifacts, -1, None);
        }

        [Test]
        public void Publish_SidecarOnly()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvMatchesExpected, EvMatchesExpected, EvAbsent));

            Assert.That(plan.Count, Is.EqualTo(2));
            AssertStep(plan, 0, PublishArtifact, 0, Sidecar);
            AssertStep(plan, 1, ReinspectArtifacts, -1, None);
        }

        [Test]
        public void Publish_BothPngThenSidecar()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent));

            Assert.That(plan.Count, Is.EqualTo(3));
            AssertStep(plan, 0, PublishArtifact, 0, Png);
            AssertStep(plan, 1, PublishArtifact, 0, Sidecar);
            AssertStep(plan, 2, ReinspectArtifacts, -1, None);
        }

        [Test]
        public void Publish_EntryAscendingPngThenSidecar()
        {
            PngJsonCapturePublicationPlan planEntries = MakePlan(1, new[] { MakeEntry(10), MakeEntry(20) });
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotArray(
                MakeRecoveryAuthority(planEntries),
                EvMatchesExpected,
                1,
                new[] { EvMatchesExpected, EvMatchesExpected },
                new[] { EvMatchesExpected, EvMatchesExpected },
                new[] { EvAbsent, EvAbsent },
                new[] { EvAbsent, EvAbsent }));

            Assert.That(plan.Count, Is.EqualTo(5));
            AssertStep(plan, 0, PublishArtifact, 0, Png);
            AssertStep(plan, 1, PublishArtifact, 0, Sidecar);
            AssertStep(plan, 2, PublishArtifact, 1, Png);
            AssertStep(plan, 3, PublishArtifact, 1, Sidecar);
            AssertStep(plan, 4, ReinspectArtifacts, -1, None);
        }

        [Test]
        public void ZeroEntry_StopAndCommitDispositions()
        {
            PngJsonCapturePublicationPlan emptyPlan = MakePlan(1, new PngJsonCapturePublicationPlanEntry[0]);

            AssertSingleRouting(BuildPlan(MakeSnapshotArray(
                MakeRecoveryAuthority(emptyPlan), EvAbsent, 0,
                new CaptureRunPublicationEvidenceStatus[0], new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0], new CaptureRunPublicationEvidenceStatus[0])), StopOrphanedPreTrace);

            AssertSingleRouting(BuildPlan(MakeSnapshotArray(
                MakeRecoveryAuthority(emptyPlan), EvMatchesExpected, 1,
                new CaptureRunPublicationEvidenceStatus[0], new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0], new CaptureRunPublicationEvidenceStatus[0])), CommitCaptureIndexAction);

            AssertSingleRouting(BuildPlan(MakeSnapshotArray(
                MakeRecoveryAuthority(emptyPlan, indexAuthoritative: true), EvMatchesExpected, 1,
                new CaptureRunPublicationEvidenceStatus[0], new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0], new CaptureRunPublicationEvidenceStatus[0])), ContinueCaptureCompleteCleanup);
        }

        // ---- Forwarding ----

        [Test]
        public void Plan_Forwarding_ExactGraph()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision);

            Assert.That(ReferenceEquals(plan.Decision, decision), Is.True);
            Assert.That(ReferenceEquals(plan.Authority, authority), Is.True);
            Assert.That(plan.AuthorityKind, Is.EqualTo(authority.Kind));
            Assert.That(ReferenceEquals(plan.AuthoritativePlan, snapshot.Plan), Is.True);
            Assert.That(ReferenceEquals(plan.RootLayout, snapshot.RootLayout), Is.True);
            Assert.That(plan.LockIdentityEvidence, Is.SameAs(snapshot.LockIdentityEvidence));
            Assert.That(plan.TestRunId, Is.EqualTo(snapshot.TestRunId));
            Assert.That(plan.RunInitializationId, Is.EqualTo(snapshot.RunInitializationId));
            Assert.That(plan.RunManifestContentSha256, Is.EqualTo(snapshot.RunManifestContentSha256));
            Assert.That(plan.Disposition, Is.EqualTo(PublishMissingArtifacts));
        }

        // ---- Validity under corruption ----

        [Test]
        public void Plan_ReflectionCorrupt_DecisionDisposition_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
            Assert.That(plan.IsValid, Is.True);

            SetField(plan.Decision, "_disposition", RunRootCollision);

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_ReflectionCorrupt_SnapshotEntry_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            Assert.That(plan.IsValid, Is.True);

            SetField(snapshot.GetEntry(0), "_finalPngStatus", EvMismatch);

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_ReflectionCorrupt_StepArray_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
            Assert.That(plan.IsValid, Is.True);

            SetField(plan, "_steps", null);

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void Plan_OwnerReleased_PlanAndTokenFailClosed()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(plan.IsValid, Is.True);
            Assert.That(token.IsIssuedFor(plan), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(plan.IsValid, Is.False);
            Assert.That(token.IsIssuedFor(plan), Is.False);
        }

        [Test]
        public void Token_SameValueDecisionSubstitution_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            PngJsonCapturePublicationArtifactRecoveryDecision equivalent = ClassifyDecision(snapshot);
            Assert.That(ReferenceEquals(decision, equivalent), Is.False);

            SetField(plan, "_decision", equivalent);

            Assert.That(token.IsIssuedFor(plan), Is.False);
        }

        [Test]
        public void Token_SameValueStepSubstitution_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(token.IsIssuedFor(plan), Is.True);

            CaptureRunPublicationArtifactRecoveryStep[] steps = (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            Assert.That(steps.Length, Is.EqualTo(2));
            steps[0] = new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, Png);

            Assert.That(token.IsIssuedFor(plan), Is.False);
        }

        [Test]
        public void Token_DecisionDispositionChangedAfterIssuance_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(plan.IsValid, Is.True);
            Assert.That(token.IsIssuedFor(plan), Is.True);

            SetField(decision, "_disposition", RunRootCollision);

            Assert.That(plan.IsValid, Is.False);
            Assert.That(token.IsIssuedFor(plan), Is.False);
        }

        [Test]
        public void Token_DecisionSnapshotSwappedAfterIssuance_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot publishSnapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactInspectionSnapshot commitSnapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);

            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(publishSnapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(plan.IsValid, Is.True);
            Assert.That(token.IsIssuedFor(plan), Is.True);

            // Swap the decision's snapshot for another snapshot of the same
            // owner that classifies to a different disposition.
            SetField(decision, "_snapshot", commitSnapshot);

            Assert.That(plan.IsValid, Is.False);
            Assert.That(token.IsIssuedFor(plan), Is.False);
        }

        [Test]
        public void Token_OperationAuthorityNulledAfterIssuance_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(decision);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(plan.IsValid, Is.True);
            Assert.That(token.IsIssuedFor(plan), Is.True);

            SetField(plan.Decision.Snapshot.Operation, "_authority", null);

            Assert.That(plan.IsValid, Is.False);
            Assert.That(token.IsIssuedFor(plan), Is.False);
        }

        // ---- Builder ----

        [Test]
        public void Builder_DelegatesAndDoesNotRevalidateOrMutate()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);

            PngJsonCapturePublicationArtifactRecoveryActionPlan plan =
                PngJsonCapturePublicationArtifactRecoveryActionPlanBuilder.Build(decision);

            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(ReferenceEquals(plan.Decision, decision), Is.True);
            Assert.That(decision.IsValid, Is.True);

            string builderSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryActionPlanBuilder.cs");
            Assert.That(builderSource, Does.Contain("PngJsonCapturePublicationArtifactRecoveryActionPlan.Create"));
            Assert.That(builderSource, Does.Not.Contain("TryValidate"));
            Assert.That(builderSource, Does.Not.Contain(".IsValid"));
        }

        // ---- Scale ----

        [Test]
        public void Plan_ThousandEntries_LinearConstruction()
        {
            long[] frameIds = new long[1000];
            for (int i = 0; i < frameIds.Length; i++)
            {
                frameIds[i] = i + 1;
            }

            CaptureRunPublicationEvidenceStatus[] stagingPng = new CaptureRunPublicationEvidenceStatus[1000];
            CaptureRunPublicationEvidenceStatus[] stagingSidecar = new CaptureRunPublicationEvidenceStatus[1000];
            CaptureRunPublicationEvidenceStatus[] finalPng = new CaptureRunPublicationEvidenceStatus[1000];
            CaptureRunPublicationEvidenceStatus[] finalSidecar = new CaptureRunPublicationEvidenceStatus[1000];
            for (int i = 0; i < 1000; i++)
            {
                stagingPng[i] = EvMatchesExpected;
                stagingSidecar[i] = EvAbsent;
                finalPng[i] = EvAbsent;
                finalSidecar[i] = EvMatchesExpected;
            }

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeFreshAuthority(frameIds), EvMatchesExpected, 1, stagingPng, stagingSidecar, finalPng, finalSidecar, maximumPngByteCount: 2000);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            Assert.That(plan.Disposition, Is.EqualTo(PublishMissingArtifacts));
            Assert.That(plan.Count, Is.EqualTo(1001));

            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(token.IsIssuedFor(plan), Is.True);

            for (int i = 0; i < 1000; i++)
            {
                AssertStep(plan, i, PublishArtifact, i, Png);
            }

            AssertStep(plan, 1000, ReinspectArtifacts, -1, None);
        }

        // ---- Shape and isolation ----

        [Test]
        public void Plan_TypeShape_NotDisposable_NoPublicConstructor()
        {
            Type planType = typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan);
            Assert.That(planType.IsPublic, Is.False);
            Assert.That(planType.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(planType), Is.False);
            Assert.That(planType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            Type builderType = typeof(PngJsonCapturePublicationArtifactRecoveryActionPlanBuilder);
            Assert.That(builderType.IsAbstract, Is.True);
            Assert.That(builderType.IsSealed, Is.True);
        }

        [Test]
        public void Plan_Source_DoesNotExposeLeaseOrTouchBackends()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryActionPlan.cs");

            Assert.That(source, Does.Not.Contain("CaptureRunLockLease"));
            Assert.That(source, Does.Not.Contain("OwnershipLease"));
            Assert.That(source, Does.Not.Contain("CaptureRunLockHandle"));
            Assert.That(source, Does.Not.Contain(".Dispose()"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("Notifier"));
            Assert.That(source, Does.Not.Contain("Backend"));
            Assert.That(source, Does.Not.Contain("Logger"));

            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain(".Where("));
            Assert.That(source, Does.Not.Contain(".Select("));
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("ToArray"));
            Assert.That(source, Does.Not.Contain("Array.Copy"));
        }
    }
}
