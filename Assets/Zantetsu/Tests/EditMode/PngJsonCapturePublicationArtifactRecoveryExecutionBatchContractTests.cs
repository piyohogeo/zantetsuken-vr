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
    public class PngJsonCapturePublicationArtifactRecoveryExecutionBatchContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private const string ManifestHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

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

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

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

        // ---- Disposition-specific plans ----

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildOrphanedPreTracePlan()
        {
            return BuildPlan(MakeSnapshotSingle(MakeRecoveryAuthority(), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPublishPngPlan()
        {
            return BuildPlan(MakeSnapshotSingle(MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPublishPngSidecarPlan()
        {
            return BuildPlan(MakeSnapshotSingle(MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildRecoveryCommitPlan()
        {
            return BuildRecoveryCommitPlan(out _);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildRecoveryCommitPlan(
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            authority = MakeRecoveryAuthority();
            return BuildCommitPlan(authority);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildFreshCommitPlan()
        {
            return BuildFreshCommitPlan(out _);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildFreshCommitPlan(
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            authority = MakeFreshAuthority(10);
            return BuildCommitPlan(authority);
        }

        private static PngJsonCapturePublicationArtifactRecoveryActionPlan BuildCommitPlan(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            return BuildPlan(MakeCommitSnapshot(authority));
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeCommitSnapshot(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            return MakeSnapshotSingle(authority, EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildCaptureCompletePlan()
        {
            return BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(indexAuthoritative: true), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildArtifactSourceMissingPlan()
        {
            return BuildPlan(MakeSnapshotSingle(MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPublishedArtifactMissingPlan()
        {
            return BuildPlan(MakeSnapshotSingle(
                MakeRecoveryAuthority(indexAuthoritative: true), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildRunRootCollisionPlan()
        {
            return BuildPlan(MakeSnapshotSingle(MakeRecoveryAuthority(), EvMismatch, 1, EvAbsent, EvAbsent, EvAbsent, EvAbsent));
        }

        // ---- Forge helpers ----

        private static PngJsonCapturePublicationArtifactRecoveryPreparedStep ForgePreparedStep(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            PngJsonCapturePublicationArtifactPublishOperation publishOperation,
            PngJsonCaptureRunCaptureIndexCommitOperation captureIndexCommitOperation)
        {
            PngJsonCapturePublicationArtifactRecoveryPreparedStep step = (PngJsonCapturePublicationArtifactRecoveryPreparedStep)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactRecoveryPreparedStep));
            SetField(step, "_actionPlan", actionPlan);
            SetField(step, "_stepIndex", stepIndex);
            SetField(step, "_publishOperation", publishOperation);
            SetField(step, "_captureIndexCommitOperation", captureIndexCommitOperation);
            return step;
        }

        private static PngJsonCapturePublicationArtifactRecoveryExecutionBatch ForgeBatch(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] preparedSteps)
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = (PngJsonCapturePublicationArtifactRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch));
            SetField(batch, "_actionPlan", actionPlan);
            SetField(batch, "_preparedSteps", preparedSteps);
            return batch;
        }

        private static PngJsonCapturePublicationArtifactPublishOperation ForgePublishOperation(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet)
        {
            PngJsonCapturePublicationArtifactPublishOperation operation = (PngJsonCapturePublicationArtifactPublishOperation)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactPublishOperation));
            SetField(operation, "_actionPlan", actionPlan);
            SetField(operation, "_stepIndex", stepIndex);
            SetField(operation, "_artifactPaths", pathSet);
            return operation;
        }

        // ---- Builder / factory rejection ----

        [Test]
        public void Builder_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder.Build(null));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Builder_InvalidPlan_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = (PngJsonCapturePublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder.Build(plan));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Batch_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(null));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        // ---- All dispositions ----

        [Test]
        public void Batch_AllDispositions_Build()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan[] plans = new[]
            {
                BuildOrphanedPreTracePlan(),
                BuildPublishPngSidecarPlan(),
                BuildRecoveryCommitPlan(),
                BuildCaptureCompletePlan(),
                BuildArtifactSourceMissingPlan(),
                BuildPublishedArtifactMissingPlan(),
                BuildRunRootCollisionPlan()
            };

            foreach (PngJsonCapturePublicationArtifactRecoveryActionPlan plan in plans)
            {
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
                Assert.That(batch.IsValid, Is.True);
                Assert.That(batch.Count, Is.EqualTo(plan.Count));
                Assert.That(batch.Disposition, Is.EqualTo(plan.Disposition));
            }
        }

        [Test]
        public void Batch_GetStep_FixedOrderSameReference_AndIndexOutOfRange()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

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

        [Test]
        public void Batch_PublishMissingArtifacts_PlanOrderPngThenSidecarThenReinspect()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

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
        public void Batch_CommitCaptureIndex_SingleStepCommitOperationOnly_Recovery()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(batch.Count, Is.EqualTo(1));
            Assert.That(batch.GetStep(0).Action, Is.EqualTo(CommitCaptureIndexAction));
            Assert.That(batch.GetStep(0).PublishOperation, Is.Null);
            Assert.That(batch.GetStep(0).CaptureIndexCommitOperation, Is.Not.Null);

            PngJsonCaptureRunCaptureIndexCommitOperation expected =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);
            PngJsonCaptureRunCaptureIndexCommitOperation actual = batch.GetStep(0).CaptureIndexCommitOperation;

            Assert.That(actual.Mode, Is.EqualTo(expected.Mode));
            Assert.That(actual.GetCanonicalBytes(), Is.EqualTo(expected.GetCanonicalBytes()));
        }

        [Test]
        public void Batch_CommitCaptureIndex_SingleStepCommitOperationOnly_Fresh()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildFreshCommitPlan(out PngJsonCapturePublicationArtifactInspectionAuthority authority);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(authority.Kind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));
            Assert.That(batch.Count, Is.EqualTo(1));
            Assert.That(batch.GetStep(0).Action, Is.EqualTo(CommitCaptureIndexAction));
            Assert.That(batch.GetStep(0).PublishOperation, Is.Null);
            Assert.That(batch.GetStep(0).CaptureIndexCommitOperation, Is.Not.Null);
            Assert.That(batch.IsValid, Is.True);
        }

        [Test]
        public void Batch_RoutingStopDispositions_NoOperations()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan[] plans = new[]
            {
                BuildOrphanedPreTracePlan(),
                BuildCaptureCompletePlan(),
                BuildArtifactSourceMissingPlan(),
                BuildPublishedArtifactMissingPlan(),
                BuildRunRootCollisionPlan()
            };

            foreach (PngJsonCapturePublicationArtifactRecoveryActionPlan plan in plans)
            {
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
                Assert.That(batch.Count, Is.EqualTo(1));
                Assert.That(batch.GetStep(0).PublishOperation, Is.Null);
                Assert.That(batch.GetStep(0).CaptureIndexCommitOperation, Is.Null);
            }
        }

        [Test]
        public void Batch_ActionPlanStepOperation_ReferenceEquals()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(batch.ActionPlan, Is.SameAs(plan));
            Assert.That(batch.Decision, Is.SameAs(plan.Decision));
            Assert.That(batch.Authority, Is.SameAs(plan.Authority));
            Assert.That(batch.AuthorityKind, Is.EqualTo(plan.AuthorityKind));
            Assert.That(batch.AuthoritativePlan, Is.SameAs(plan.AuthoritativePlan));
            Assert.That(batch.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(batch.LockIdentityEvidence, Is.SameAs(plan.LockIdentityEvidence));
            Assert.That(batch.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(batch.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(batch.RunManifestContentSha256, Is.EqualTo(plan.RunManifestContentSha256));

            Assert.That(batch.GetStep(0).Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(batch.GetStep(0).ActionPlan, Is.SameAs(plan));
            Assert.That(batch.GetStep(0).PublishOperation.ActionPlan, Is.SameAs(plan));
            Assert.That(batch.GetStep(0).PublishOperation.Step, Is.SameAs(plan.GetStep(0)));
        }

        [Test]
        public void Batch_Build_OwnerNotDisposed()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(owner.IsCreated, Is.True);
            Assert.That(batch.LockIdentityEvidence.IsIssuedFor(owner), Is.True);
        }

        [Test]
        public void Batch_Build_InputGraphUnchanged()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            string idBefore = plan.RunInitializationId;

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.RunInitializationId, Is.EqualTo(idBefore));
            Assert.That(batch.IsValid, Is.True);
        }

        // ---- Token ----

        [Test]
        public void PreparedStep_NullToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(plan, null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void PreparedStep_CrossPlanToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan planA = BuildPublishPngPlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan planB = BuildPublishPngPlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(planB, tokenA, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void PreparedStep_StaleToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            owner.Dispose();
            _owners.Remove(owner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(plan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void PreparedStep_StepIndexOutOfRange_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan();

            foreach (int bad in new[] { -1, plan.Count, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(plan, plan.AcquireValidationToken(), bad));
                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        // ---- Owner release ----

        [Test]
        public void Batch_OwnerReleased_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(batch.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(batch.IsValid, Is.False);
            Assert.That(batch.TryValidate(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token), Is.False);
            Assert.That(token, Is.Null);
        }

        // ---- Forge defense ----

        [Test]
        public void Batch_ForgedFields_IsValidFalse_NoException()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
            Assert.That(batch.IsValid, Is.True);

            // Null array.
            Assert.That(ForgeBatch(plan, null).IsValid, Is.False);

            // Array length mismatch.
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] tooShort = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[1];
            tooShort[0] = batch.GetStep(0);
            Assert.That(ForgeBatch(plan, tooShort).IsValid, Is.False);

            // Null element.
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] nullElement = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[3];
            nullElement[0] = batch.GetStep(0);
            nullElement[1] = null;
            nullElement[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, nullElement).IsValid, Is.False);

            // Order swap.
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] swapped = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[3];
            swapped[0] = batch.GetStep(1);
            swapped[1] = batch.GetStep(0);
            swapped[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, swapped).IsValid, Is.False);

            // Duplicate prepared step.
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] duplicated = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[3];
            duplicated[0] = batch.GetStep(0);
            duplicated[1] = batch.GetStep(0);
            duplicated[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, duplicated).IsValid, Is.False);

            // Foreign action plan with the same step count.
            PngJsonCapturePublicationArtifactRecoveryActionPlan foreignPlan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] foreignSteps = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[3];
            foreignSteps[0] = batch.GetStep(0);
            foreignSteps[1] = batch.GetStep(1);
            foreignSteps[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(foreignPlan, foreignSteps).IsValid, Is.False);

            // Foreign publish operation (wrong artifact path set).
            PngJsonCapturePublicationArtifactInspectionPathSet foreignPaths = MakeOperation(MakeRecoveryAuthority()).GetArtifactPaths(0);
            PngJsonCapturePublicationArtifactPublishOperation forgedPublish = ForgePublishOperation(plan, 0, foreignPaths);
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] forgedPublishArr = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[3];
            forgedPublishArr[0] = ForgePreparedStep(plan, 0, forgedPublish, null);
            forgedPublishArr[1] = batch.GetStep(1);
            forgedPublishArr[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, forgedPublishArr).IsValid, Is.False);

            // Publish/commit operation mix-up: a commit operation in a publish step.
            PngJsonCapturePublicationArtifactRecoveryActionPlan commitPlan = BuildRecoveryCommitPlan();
            PngJsonCaptureRunCaptureIndexCommitOperation commitOperation =
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(commitPlan).GetStep(0).CaptureIndexCommitOperation;
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] mixedArr = new PngJsonCapturePublicationArtifactRecoveryPreparedStep[3];
            mixedArr[0] = ForgePreparedStep(plan, 0, null, commitOperation);
            mixedArr[1] = batch.GetStep(1);
            mixedArr[2] = batch.GetStep(2);
            Assert.That(ForgeBatch(plan, mixedArr).IsValid, Is.False);
        }

        [Test]
        public void PreparedStep_PublishOperationCrossStepSubstituted_IsValidIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep =
                PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(plan, token, 0);

            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);

            // Step 1's publish operation (sidecar) in step 0 (png) is rejected.
            PngJsonCapturePublicationArtifactPublishOperation step1Operation =
                PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 1);
            SetField(preparedStep, "_publishOperation", step1Operation);

            Assert.That(preparedStep.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void PreparedStep_PublishOperationSameValueDifferentInstance_StillValid()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep =
                PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(plan, token, 0);

            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);

            // A fresh instance built for the same step is equivalent.
            PngJsonCapturePublicationArtifactPublishOperation other =
                PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0);
            Assert.That(ReferenceEquals(preparedStep.PublishOperation, other), Is.False);
            SetField(preparedStep, "_publishOperation", other);

            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);
        }

        [Test]
        public void PreparedStep_CommitOperationSameValueDifferentInstance_StillValid()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep =
                PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(plan, token, 0);

            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);

            // A fresh instance built for the same step is equivalent.
            PngJsonCaptureRunCaptureIndexCommitOperation other =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);
            Assert.That(ReferenceEquals(preparedStep.CaptureIndexCommitOperation, other), Is.False);
            SetField(preparedStep, "_captureIndexCommitOperation", other);

            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);
        }

        [Test]
        public void Batch_CommitOperationBytesCorrupted_FullValidationFalse_IndexLocalTrue()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
            PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep = batch.GetStep(0);
            PngJsonCaptureRunCaptureIndexCommitOperation commitOperation = preparedStep.CaptureIndexCommitOperation;

            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);
            Assert.That(batch.IsValid, Is.True);

            SetField(commitOperation, "_canonicalBytes", new byte[] { 1, 2, 3 });

            // Pure index-local validation does not re-serialize the canonical bytes.
            Assert.That(preparedStep.IsValidIndexLocal(token), Is.True);

            // Full batch validation re-serializes and rejects the corruption.
            Assert.That(batch.IsValid, Is.False);
        }

        // ---- Sharing ----

        [Test]
        public void Batch_TwoBuilds_NonSharedBatchStepsOperations_SharedActionPlan()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch first = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch second = PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

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
            FieldInfo[] fields = typeof(PngJsonCapturePublicationArtifactRecoveryPreparedStep).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(int)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCapturePublicationArtifactPublishOperation)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCaptureRunCaptureIndexCommitOperation)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Batch_FieldShape_TwoFields_NoStaticState()
        {
            FieldInfo[] fields = typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryPreparedStep[])));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
            Assert.That(typeof(PngJsonCapturePublicationArtifactRecoveryPreparedStep).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Types_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            foreach (Type type in new[] { typeof(PngJsonCapturePublicationArtifactRecoveryPreparedStep), typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch) })
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
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Shape_NoLeaseTokenOrBytesExposure()
        {
            foreach (Type type in new[] { typeof(PngJsonCapturePublicationArtifactRecoveryPreparedStep), typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch), typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || field.FieldType == typeof(byte[]),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, token, or byte array.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || prop.PropertyType == typeof(byte[]),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a lease, token, or byte array.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || method.ReturnType == typeof(byte[]),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a lease, token, or byte array.");
                }
            }
        }

        // ---- Scale / source ----

        [Test]
        public void Batch_ThousandEntryPublish_LinearBuild()
        {
            const int count = 1000;

            long[] frameIds = new long[count];
            CaptureRunPublicationEvidenceStatus[] stagingPng = new CaptureRunPublicationEvidenceStatus[count];
            CaptureRunPublicationEvidenceStatus[] stagingSidecar = new CaptureRunPublicationEvidenceStatus[count];
            CaptureRunPublicationEvidenceStatus[] finalPng = new CaptureRunPublicationEvidenceStatus[count];
            CaptureRunPublicationEvidenceStatus[] finalSidecar = new CaptureRunPublicationEvidenceStatus[count];
            for (int i = 0; i < count; i++)
            {
                frameIds[i] = i + 1;
                stagingPng[i] = EvMatchesExpected;
                stagingSidecar[i] = EvAbsent;
                finalPng[i] = EvAbsent;
                finalSidecar[i] = EvMatchesExpected;
            }

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeFreshAuthority(frameIds), EvMatchesExpected, 1, stagingPng, stagingSidecar, finalPng, finalSidecar, maximumPngByteCount: 2000);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            // The plan token is acquired exactly once inside the batch factory,
            // before the materialization loop.
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch =
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);

            Assert.That(batch.Count, Is.EqualTo(count + 1));
            Assert.That(batch.GetStep(0).Action, Is.EqualTo(PublishArtifact));
            Assert.That(batch.GetStep(0).Step.EntryIndex, Is.EqualTo(0));
            Assert.That(batch.GetStep(count - 1).Step.EntryIndex, Is.EqualTo(count - 1));
            Assert.That(batch.GetStep(count).Action, Is.EqualTo(ReinspectArtifacts));
            Assert.That(batch.IsValid, Is.True);
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string preparedSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryPreparedStep.cs");
            string batchSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionBatch.cs");
            string builderSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder.cs");

            foreach (string source in new[] { preparedSource, batchSource, builderSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("SerializeCanonical"));
                Assert.That(source, Does.Not.Contain("GetCanonicalBytes"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Notifier"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Backend"));
                Assert.That(source, Does.Not.Contain("Coordinator"));
                Assert.That(source, Does.Not.Contain("Receipt"));
                Assert.That(source, Does.Not.Contain("Publisher"));
                Assert.That(source, Does.Not.Contain("Committer"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
                Assert.That(source, Does.Not.Contain("HashSet"));
            }
        }

        [Test]
        public void Source_BatchSingleTokenBeforeLoop_NoPerStepRevalidation()
        {
            string batchSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionBatch.cs");

            // One acquisition in Create, one in TryValidate — each before its own loop.
            int acquireCount = batchSource.Split(new[] { "TryAcquireValidationToken" }, StringSplitOptions.None).Length - 1;
            Assert.That(acquireCount, Is.EqualTo(2));

            int createAcquireIndex = batchSource.IndexOf("TryAcquireValidationToken", StringComparison.Ordinal);
            int loopIndex = batchSource.IndexOf("for (int i = 0; i < count; i++)", StringComparison.Ordinal);
            Assert.That(createAcquireIndex, Is.GreaterThan(0));
            Assert.That(loopIndex, Is.GreaterThan(createAcquireIndex));

            int returnIndex = batchSource.IndexOf("return new PngJsonCapturePublicationArtifactRecoveryExecutionBatch(", StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(loopIndex));

            string loopBody = batchSource.Substring(loopIndex, returnIndex - loopIndex);
            Assert.That(loopBody, Does.Not.Contain("TryAcquireValidationToken"));
            Assert.That(loopBody, Does.Not.Contain("actionPlan.IsValid"));
        }

        [Test]
        public void Source_PreparedStepCommitFullUsesTokenGatedHelper()
        {
            string preparedSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryPreparedStep.cs");

            int commitCase = preparedSource.LastIndexOf("case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:", StringComparison.Ordinal);
            Assert.That(commitCase, Is.GreaterThan(0));

            int nextCase = preparedSource.IndexOf("case ", commitCase + 1, StringComparison.Ordinal);
            Assert.That(nextCase, Is.GreaterThan(commitCase));

            string commitCaseBody = preparedSource.Substring(commitCase, nextCase - commitCase);
            Assert.That(commitCaseBody, Does.Contain("captureIndexCommitOperation.IsValidWithToken(token)"));
            Assert.That(commitCaseBody, Does.Not.Contain("captureIndexCommitOperation.IsValid;"));
        }

        [Test]
        public void Source_BuilderDelegates_NoDuplication()
        {
            string builderSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionBatchBuilder.cs");

            Assert.That(builderSource, Does.Contain("PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create"));
            Assert.That(builderSource, Does.Not.Contain("TryAcquireValidationToken"));
            Assert.That(builderSource, Does.Not.Contain("CreateIndexLocal"));
            Assert.That(builderSource, Does.Not.Contain("for ("));
            Assert.That(builderSource, Does.Not.Contain("SerializeCanonical"));
        }
    }
}
