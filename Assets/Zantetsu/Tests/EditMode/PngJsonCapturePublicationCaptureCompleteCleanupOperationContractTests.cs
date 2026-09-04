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
    public class PngJsonCapturePublicationCaptureCompleteCleanupOperationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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

        private static CaptureRunPublicationArtifactKind Png => CaptureRunPublicationArtifactKind.Png;

        private static CaptureRunPublicationArtifactKind Sidecar => CaptureRunPublicationArtifactKind.Sidecar;

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
            public PngJsonCapturePublicationArtifactInspectionSnapshot Snapshot { get; set; }

            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                return Snapshot;
            }
        }

        private sealed class FakePublisher : IPngJsonCapturePublicationArtifactPublisher
        {
            public PngJsonCapturePublicationArtifactPublishReceipt Publish(
                PngJsonCapturePublicationArtifactPublishOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return PngJsonCapturePublicationArtifactPublishReceipt.Create(this, operation, token);
            }
        }

        private sealed class FakeCommitter : IPngJsonCaptureRunCaptureIndexCommitter
        {
            public PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
                PngJsonCaptureRunCaptureIndexCommitOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
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

        // ---- Fresh seed graph ----

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
                HashA);
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

        // ---- Recovery decision graph ----

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
            CaptureRunPublicationDocumentObservation captureIndex = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                stagingFramesStatus,
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

        // ---- Plan / entry / doc ----

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

        // ---- Operation / observation / snapshot ----

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

        private static FakeArtifactInspector MakeArtifactInspector(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            inspector.Snapshot = PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                inspector, operation, traceStatus, traceCount, entries);
            return inspector;
        }

        // ---- Orchestration construction ----

        private static PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator MakeExecutionCoordinator()
        {
            return new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(
                new FakePublisher(), new FakeCommitter());
        }

        private static PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator MakeOrchestrator(
            IPngJsonCapturePublicationArtifactInspector inspector,
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator executionCoordinator)
        {
            return new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, executionCoordinator);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            int entryCount = 1,
            CaptureRunPublicationEvidenceStatus stagingStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected)
        {
            return BuildCommitResult(entryCount, stagingStatus, out _);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCommitResult(
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(entryCount));
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, false, null, out owner);
            return BuildCleanupResult(authority, entryCount, stagingStatus);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            int entryCount = 1,
            CaptureRunPublicationEvidenceStatus stagingStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected)
        {
            return BuildCaptureCompleteResult(entryCount, stagingStatus, out _);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCaptureCompleteResult(
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(entryCount));
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, true, null, out owner);
            return BuildCleanupResult(authority, entryCount, stagingStatus);
        }

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult BuildCleanupResult(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus)
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                entries[i] = MakeIndexObservation(
                    token, operation, i,
                    stagingStatus, stagingStatus, EvMatchesExpected, EvMatchesExpected);
            }

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                MakeOrchestrator(inspector, MakeExecutionCoordinator());
            return orchestrator.Execute(operation);
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupActionPlan BuildPlan(bool commitRoute)
        {
            return PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(
                commitRoute ? BuildCommitResult() : BuildCaptureCompleteResult());
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupOperation BuildOperation(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan,
            int stepIndex)
        {
            CaptureRunPublicationPathSet publicationPaths =
                plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            return PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(
                plan, publicationPaths, markerPaths, stepIndex);
        }

        // ---- Tests ----

        [Test]
        public void Operation_Shape_FiveReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(5));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Operation_NoLeaseProofStreamBytesExposure()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupOperation);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(
                    field.FieldType == typeof(CaptureRunLockLease)
                    || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                    || field.FieldType == typeof(Stream)
                    || field.FieldType == typeof(byte[]),
                    Is.False,
                    type.Name + "." + field.Name + " must not hold a lease, stream, or byte sequence.");
            }

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                    || prop.PropertyType == typeof(Stream)
                    || prop.PropertyType == typeof(byte[]),
                    Is.False,
                    type.Name + "." + prop.Name + " must not expose a lease, stream, or bytes.");
            }
        }

        [Test]
        public void Factory_Shape_StaticNoFields()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupOperationFactory);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract && type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Create_NullArguments_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(null, publicationPaths, markerPaths, 0));
            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, null, markerPaths, 0));
            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, null, 0));
        }

        [Test]
        public void Create_InvalidPlan_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            SetField(plan, "_steps", null);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0));
        }

        [Test]
        public void Create_OutOfRangeStep_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, plan.Count));
        }

        [Test]
        public void Create_CaptureCompleteReady_Rejected()
        {
            // The last step of every cleanup plan is CaptureCompleteReady.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            int last = plan.Count - 1;
            Assert.That(plan.GetStep(last).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));

            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, last));
        }

        [Test]
        public void TargetPath_Mapping_CommitRoute()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths =
                plan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);

            // Commit route: DeleteStagingArtifact(Png), DeleteStagingArtifact(Sidecar),
            // RemoveStagingFramesRoot, DeletePublicationPlan, 4 tail.
            Assert.That(plan.Count, Is.EqualTo(8));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation png =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(png.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            Assert.That(png.TargetPath, Is.EqualTo(artifactPaths.StagingPngPath));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation sidecar =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 1);
            Assert.That(sidecar.TargetPath, Is.EqualTo(artifactPaths.StagingSidecarPath));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation frames =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 2);
            Assert.That(frames.TargetPath, Is.EqualTo(publicationPaths.StagingFramesRoot));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation publication =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 3);
            Assert.That(publication.TargetPath, Is.EqualTo(publicationPaths.PublicationPlanPath));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation ready =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 4);
            Assert.That(ready.TargetPath, Is.EqualTo(markerPaths.StagingReadyPath));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation init =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 5);
            Assert.That(init.TargetPath, Is.EqualTo(markerPaths.StagingInitializationPath));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation root =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 6);
            Assert.That(root.TargetPath, Is.EqualTo(publicationPaths.RootLayout.StagingRunRoot));
        }

        [Test]
        public void TargetPath_Mapping_TemporaryDocuments()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(planValue, true, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, planValue), out _);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(token, operation, 0, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);

            // Make publication.plan.tmp canonical too.
            SetField(result.Authority.RecoveryDecision.Snapshot, "_publicationPlanTemporary",
                MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue));

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            CaptureRunPublicationPathSet publicationPaths = result.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            PngJsonCapturePublicationCaptureCompleteCleanupOperation planTmp =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(planTmp.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary));
            Assert.That(planTmp.TargetPath, Is.EqualTo(publicationPaths.PublicationPlanTemporaryPath));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation indexTmp =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 1);
            Assert.That(indexTmp.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
            Assert.That(indexTmp.TargetPath, Is.EqualTo(publicationPaths.CaptureIndexTemporaryPath));
        }

        [Test]
        public void ArtifactStep_PngSidecar_IndependentCorrelation()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths =
                plan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);

            PngJsonCapturePublicationCaptureCompleteCleanupOperation png =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(png.ArtifactKind, Is.EqualTo(Png));
            Assert.That(png.EntryIndex, Is.EqualTo(0));
            Assert.That(png.ArtifactPaths, Is.SameAs(artifactPaths));
            Assert.That(png.ExpectedByteCount, Is.EqualTo(artifactPaths.Entry.PngByteLength));
            Assert.That(png.ExpectedContentSha256, Is.EqualTo(artifactPaths.Entry.PngContentSha256));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation sidecar =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 1);
            Assert.That(sidecar.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(sidecar.ExpectedByteCount, Is.EqualTo(artifactPaths.Entry.SidecarByteLength));
            Assert.That(sidecar.ExpectedContentSha256, Is.EqualTo(artifactPaths.Entry.SidecarContentSha256));
            Assert.That(png.ExpectedByteCount, Is.Not.EqualTo(sidecar.ExpectedByteCount));
        }

        [Test]
        public void FreshRoute_DeletePublicationPlan_And_RemoveStagingFramesRoot()
        {
            long[] frameIds = { 1, 2 };
            PngJsonCapturePublicationArtifactInspectionAuthority fresh = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(fresh, 2000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(token, operation, 0, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected),
                MakeIndexObservation(token, operation, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            CaptureRunPublicationPathSet publicationPaths = result.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            // Fresh: RemoveStagingFramesRoot, DeletePublicationPlan, 4 tail.
            Assert.That(plan.Count, Is.EqualTo(6));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation frames =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(frames.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));
            Assert.That(frames.TargetPath, Is.EqualTo(publicationPaths.StagingFramesRoot));

            PngJsonCapturePublicationCaptureCompleteCleanupOperation publication =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 1);
            Assert.That(publication.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan));
            Assert.That(publication.TargetPath, Is.EqualTo(publicationPaths.PublicationPlanPath));
        }

        [Test]
        public void CreateIndexLocal_ForeignToken_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken foreignToken;
            other.TryValidate(out foreignToken);

            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.CreateIndexLocal(
                    foreignToken, plan, publicationPaths, markerPaths, 0));
        }

        [Test]
        public void OwnerExpiry_IsValidFalse_AndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(token, operation, 0, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            CaptureRunPublicationPathSet publicationPaths = result.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation op =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken planToken;
            plan.TryValidate(out planToken);

            Assert.That(op.IsValid, Is.True);
            Assert.That(op.IsValidIndexLocal(planToken), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(op.IsValid, Is.False);
            Assert.That(op.IsValidIndexLocal(planToken), Is.False);
        }

        [Test]
        public void IsValidIndexLocal_NullToken_False_NoThrow()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);
            PngJsonCapturePublicationCaptureCompleteCleanupOperation op =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);

            Assert.That(op.IsValidIndexLocal(null), Is.False);
        }

        [Test]
        public void EntryArrayElementSwapAfterToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 2);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            CaptureRunPublicationPathSet publicationPaths = result.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupOperation op =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(op.IsValidIndexLocal(token), Is.True);

            // Swap snapshot entry 0 with entry 1 after the token was minted.
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = result.InspectionSnapshot;
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                (PngJsonCapturePublicationArtifactEntryObservation[])GetField(snapshot, "_entries");
            entries[0] = entries[1];

            Assert.That(op.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.CreateIndexLocal(
                    token, plan, publicationPaths, markerPaths, 0));
        }

        [Test]
        public void ArtifactPathSetArrayElementSwapAfterToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult(entryCount: 2);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
            CaptureRunPublicationPathSet publicationPaths = result.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupOperation op =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(op.IsValidIndexLocal(token), Is.True);

            // Swap the inspection operation's path set 0 with path set 1 after
            // the token was minted.
            PngJsonCapturePublicationArtifactInspectionOperation operation = result.InspectionSnapshot.Operation;
            PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPaths =
                (PngJsonCapturePublicationArtifactInspectionPathSet[])GetField(operation, "_artifactPaths");
            artifactPaths[0] = artifactPaths[1];

            Assert.That(op.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.CreateIndexLocal(
                    token, plan, publicationPaths, markerPaths, 0));
        }

        [Test]
        public void ArtifactPathSetStagingPathTamper_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            CaptureRunPublicationPathSet publicationPaths = plan.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupOperation op =
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(plan, publicationPaths, markerPaths, 0);
            Assert.That(op.IsValidIndexLocal(token), Is.True);

            // Tamper the captured path set's own staging PNG path to a
            // different non-empty value after the token was minted.
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                plan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);
            SetField(pathSet, "_stagingPngPath", IsWindows ? "C:\\forged\\evil.png.stage" : "/forged/evil.png.stage");

            Assert.That(op.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupOperation.CreateIndexLocal(
                    token, plan, publicationPaths, markerPaths, 0));
        }

        [Test]
        public void Source_IndexLocal_NoFullValidationNoScan()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupOperation.cs");

            int trustedIndex = source.IndexOf("private static bool TryCorrelateTrusted(", StringComparison.Ordinal);
            Assert.That(trustedIndex, Is.GreaterThan(0));
            int indexLocalIndex = source.IndexOf("private static bool TryCorrelateIndexLocal(", StringComparison.Ordinal);
            Assert.That(indexLocalIndex, Is.GreaterThan(trustedIndex));

            string trustedBody = source.Substring(trustedIndex, indexLocalIndex - trustedIndex);
            Assert.That(trustedBody, Does.Not.Contain("actionPlan.IsValid"));
            Assert.That(trustedBody, Does.Not.Contain("TryValidate"));
            Assert.That(trustedBody, Does.Not.Contain("GetEntry"));
            Assert.That(trustedBody, Does.Not.Contain("SerializeCanonical"));
            Assert.That(trustedBody, Does.Not.Contain("for ("));
        }

        [Test]
        public void Source_Factory_NoValidationOrPathDerivation()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupOperationFactory.cs");

            Assert.That(source, Does.Not.Contain("TryValidate"));
            Assert.That(source, Does.Not.Contain("IsValid"));
            Assert.That(source, Does.Not.Contain("Path.Combine"));
            Assert.That(source, Does.Not.Contain("GetStep"));
            Assert.That(source, Does.Contain("PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create("));
        }

        [Test]
        public void ThousandArtifactSteps_LinearConstruction_TokenOnceOutsideLoop()
        {
            long[] frameIds = new long[1000];
            for (int i = 0; i < frameIds.Length; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority fresh = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(fresh, 2000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken inspectionToken =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[1000];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(inspectionToken, operation, i, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected);
            }

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            CaptureRunPublicationPathSet publicationPaths = result.InspectionSnapshot.Operation.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            // Acquire the plan token once outside the loop.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken planToken;
            Assert.That(plan.TryValidate(out planToken), Is.True);

            // 1000 entries x 2 = 2000 staging artifact steps + RemoveStagingFramesRoot
            // + DeletePublicationPlan + 4 tail = 2006.
            Assert.That(plan.Count, Is.EqualTo(2006));

            for (int i = 0; i < 2000; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupOperation op =
                    PngJsonCapturePublicationCaptureCompleteCleanupOperation.CreateIndexLocal(
                        planToken, plan, publicationPaths, markerPaths, i);
                Assert.That(op.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
                Assert.That(op.IsValidIndexLocal(planToken), Is.True);
            }
        }
    }
}
