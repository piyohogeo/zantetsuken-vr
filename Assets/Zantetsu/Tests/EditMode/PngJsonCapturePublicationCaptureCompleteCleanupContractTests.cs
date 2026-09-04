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
    public class PngJsonCapturePublicationCaptureCompleteCleanupContractTests
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

        private PngJsonCapturePublicationCaptureCompleteCleanupActionPlan BuildTemporaryDocumentsPlan()
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

            SetField(result.Authority.RecoveryDecision.Snapshot, "_publicationPlanTemporary",
                MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocCanonical, 100, planValue));
            SetField(result.Authority.RecoveryDecision.Snapshot, "_publicationPlan",
                MakeDoc(PublicationPlan, DocCanonical, 100, planValue));

            return PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch BuildBatch(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan)
        {
            return PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.Create(plan);
        }

        // ---- Tests ----

        [Test]
        public void PreparedStep_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Batch_Shape_TwoReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Builder_Shape_StaticNoFields()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatchBuilder);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract && type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Shape_NoLeaseStreamBytesExposure()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep),
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(Stream)
                        || field.FieldType == typeof(byte[]),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, stream, or bytes.");
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
        }

        [Test]
        public void PreparedStep_AllNineActions_ExclusiveOperationPresence()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();

            // DeletePublicationPlanTemporary, DeleteCaptureIndexTemporary,
            // DeleteStagingArtifact(Png), DeleteStagingArtifact(Sidecar),
            // RemoveStagingFramesRoot, DeletePublicationPlan,
            // DeleteStagingReadyMarker, DeleteStagingInitializationMarker,
            // RemoveStagingRunRoot, CaptureCompleteReady.
            Assert.That(plan.Count, Is.EqualTo(10));

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            Assert.That(batch.Count, Is.EqualTo(10));

            for (int i = 0; i < batch.Count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                Assert.That(prepared.StepIndex, Is.EqualTo(i));

                CaptureRunPublicationCaptureCompleteCleanupAction action = prepared.Action;
                if (action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    Assert.That(prepared.CleanupOperation, Is.Null);
                }
                else
                {
                    Assert.That(prepared.CleanupOperation, Is.Not.Null);
                    Assert.That(ReferenceEquals(prepared.CleanupOperation.ActionPlan, plan), Is.True);
                    Assert.That(prepared.CleanupOperation.StepIndex, Is.EqualTo(i));
                    Assert.That(prepared.CleanupOperation.Action, Is.EqualTo(action));
                }
            }

            Assert.That(batch.GetStep(0).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary));
            Assert.That(batch.GetStep(1).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
            Assert.That(batch.GetStep(2).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            Assert.That(batch.GetStep(2).CleanupOperation.ArtifactKind, Is.EqualTo(Png));
            Assert.That(batch.GetStep(3).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            Assert.That(batch.GetStep(3).CleanupOperation.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(batch.GetStep(4).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));
            Assert.That(batch.GetStep(5).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan));
            Assert.That(batch.GetStep(6).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker));
            Assert.That(batch.GetStep(7).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker));
            Assert.That(batch.GetStep(8).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot));
            Assert.That(batch.GetStep(9).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
        }

        [Test]
        public void PreparedStep_NoneOrUndefinedAction_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            CaptureRunPublicationCaptureCompleteCleanupStep undefined =
                (CaptureRunPublicationCaptureCompleteCleanupStep)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationCaptureCompleteCleanupStep));
            SetField(undefined, "_action", (CaptureRunPublicationCaptureCompleteCleanupAction)9999);
            SetField(undefined, "_entryIndex", -1);
            SetField(undefined, "_artifactKind", CaptureRunPublicationArtifactKind.None);

            CaptureRunPublicationCaptureCompleteCleanupStep[] steps =
                (CaptureRunPublicationCaptureCompleteCleanupStep[])GetField(plan, "_steps");
            steps[0] = undefined;

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void Source_PreparedStep_DefaultCaseRejectsUndefinedAction()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.cs");

            int switchIndex = source.IndexOf("switch (action)", StringComparison.Ordinal);
            Assert.That(switchIndex, Is.GreaterThan(0));
            int returnIndex = source.IndexOf("return new PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep(", StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(switchIndex));
            string switchBody = source.Substring(switchIndex, returnIndex - switchIndex);

            Assert.That(switchBody, Does.Contain("default:"));
            Assert.That(switchBody, Does.Contain("Step action must be a defined cleanup action."));
            Assert.That(switchBody, Does.Contain("CaptureCompleteReady"));
            Assert.That(switchBody, Does.Contain("DeleteStagingArtifact"));
        }

        [Test]
        public void PreparedStep_NullArguments_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(null, token, 0));
            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, null, 0));
        }

        [Test]
        public void PreparedStep_InvalidPlan_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            SetField(plan, "_steps", null);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void PreparedStep_ForeignToken_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken foreign;
            Assert.That(other.TryValidate(out foreign), Is.True);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, foreign, 0));
        }

        [Test]
        public void PreparedStep_StaleToken_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            // Re-issue an orchestration token for the same result and swap it
            // into the plan; the stale plan token must fail closed.
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = plan.OrchestrationResult;
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken reissued;
            Assert.That(result.TryValidate(out reissued), Is.True);
            SetField(plan, "_orchestrationToken", reissued);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void PreparedStep_OutOfRangeIndex_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, plan.Count));
        }

        [Test]
        public void PreparedStep_CaptureCompleteReady_HasNoOperation()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            int last = plan.Count - 1;
            Assert.That(plan.GetStep(last).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));

            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, last);

            Assert.That(prepared.CleanupOperation, Is.Null);
            Assert.That(prepared.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
            Assert.That(prepared.IsValidIndexLocal(token), Is.True);
        }

        [Test]
        public void PreparedStep_OwnerExpiry_IsValidFalse_AndIndexLocalFalse()
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

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken planToken;
            Assert.That(plan.TryValidate(out planToken), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, planToken, 0);
            Assert.That(prepared.IsValid, Is.True);
            Assert.That(prepared.IsValidIndexLocal(planToken), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(prepared.IsValid, Is.False);
            Assert.That(prepared.IsValidIndexLocal(planToken), Is.False);
        }

        [Test]
        public void PreparedStep_IndexNullOperationTamper_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0);
            Assert.That(prepared.IsValidIndexLocal(token), Is.True);

            // Null the cleanup operation of a side-effecting step.
            SetField(prepared, "_cleanupOperation", null);
            Assert.That(prepared.IsValidIndexLocal(token), Is.False);

            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep fresh =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0);

            // Corrupt the step index.
            SetField(fresh, "_stepIndex", plan.Count + 5);
            Assert.That(fresh.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void PreparedStep_ForeignOperationSwap_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken otherToken;
            Assert.That(other.TryValidate(out otherToken), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0);
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep foreign =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(other, otherToken, 0);

            SetField(prepared, "_cleanupOperation", foreign.CleanupOperation);
            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void PreparedStep_OperationPathSetCorruption_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared =
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(plan, token, 0);
            Assert.That(prepared.IsValidIndexLocal(token), Is.True);

            // Tamper the operation's stored artifact path set staging path.
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                plan.OrchestrationResult.InspectionSnapshot.Operation.GetArtifactPaths(0);
            SetField(pathSet, "_stagingPngPath", IsWindows ? "C:\\forged\\evil.png.stage" : "/forged/evil.png.stage");

            Assert.That(prepared.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Batch_Create_ForwardsIdentityAndRun()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(ReferenceEquals(batch.ActionPlan, plan), Is.True);
            Assert.That(ReferenceEquals(batch.OrchestrationResult, plan.OrchestrationResult), Is.True);
            Assert.That(ReferenceEquals(batch.Authority, plan.Authority), Is.True);
            Assert.That(batch.AuthorityKind, Is.EqualTo(plan.AuthorityKind));
            Assert.That(ReferenceEquals(batch.AuthoritativePlan, plan.AuthoritativePlan), Is.True);
            Assert.That(ReferenceEquals(batch.RootLayout, plan.RootLayout), Is.True);
            Assert.That(batch.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(batch.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(batch.Count, Is.EqualTo(plan.Count));
        }

        [Test]
        public void Batch_NullPlan_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.Create(null));
            Assert.Throws<ArgumentNullException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatchBuilder.Build(null));
        }

        [Test]
        public void Batch_InvalidPlan_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            SetField(plan, "_steps", null);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.Create(plan));
        }

        [Test]
        public void Batch_GetStep_OutOfRange_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetStep(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.GetStep(batch.Count));
        }

        [Test]
        public void Batch_TryValidate_ReturnsReusablePlanToken()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);
            Assert.That(token, Is.Not.Null);
            Assert.That(token.IsIssuedFor(plan), Is.True);
            Assert.That(batch.IsValid, Is.True);
        }

        [Test]
        public void Batch_StepsArrayNull_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            SetField(batch, "_steps", null);
            Assert.That(batch.IsValid, Is.False);
        }

        [Test]
        public void Batch_StepsArrayShortened_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[] steps =
                (PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[] shortened =
                new PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[steps.Length - 1];
            Array.Copy(steps, shortened, shortened.Length);
            SetField(batch, "_steps", shortened);

            Assert.That(batch.IsValid, Is.False);
        }

        [Test]
        public void Batch_ElementSwap_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[] steps =
                (PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[])GetField(batch, "_steps");
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep tmp = steps[0];
            steps[0] = steps[1];
            steps[1] = tmp;

            Assert.That(batch.IsValid, Is.False);
        }

        [Test]
        public void Batch_OwnerExpiry_IsInvalid()
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

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            Assert.That(batch.IsValid, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(batch.IsValid, Is.False);
        }

        [Test]
        public void Batch_MaterializesAllSteps_AscendingSharedToken()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            Assert.That(batch.Count, Is.EqualTo(plan.Count));
            for (int i = 0; i < batch.Count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                Assert.That(prepared.StepIndex, Is.EqualTo(i));
                Assert.That(ReferenceEquals(prepared.ActionPlan, plan), Is.True);
                Assert.That(prepared.Action, Is.EqualTo(plan.GetStep(i).Action));
            }
        }

        [Test]
        public void Batch_SharedPublicationAndMarkerPathsAcrossSteps()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            CaptureRunPublicationPathSet publicationPaths = null;
            CaptureRunMarkerPathSet markerPaths = null;
            int sideEffecting = 0;

            for (int i = 0; i < batch.Count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupOperation operation = batch.GetStep(i).CleanupOperation;
                if (operation == null)
                {
                    continue;
                }

                sideEffecting++;
                if (publicationPaths == null)
                {
                    publicationPaths = operation.PublicationPaths;
                    markerPaths = operation.MarkerPaths;
                }
                else
                {
                    Assert.That(ReferenceEquals(operation.PublicationPaths, publicationPaths), Is.True);
                    Assert.That(ReferenceEquals(operation.MarkerPaths, markerPaths), Is.True);
                }
            }

            // DeleteStagingArtifact appears twice (Png and Sidecar), so the
            // eight side-effecting actions produce nine side-effecting steps
            // plus one CaptureCompleteReady routing step.
            Assert.That(sideEffecting, Is.EqualTo(9));
        }

        [Test]
        public void Source_Batch_MarkerPathSetCreatedOnceOutsideLoop()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.cs");

            int createIndex = source.IndexOf("internal static PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch Create(", StringComparison.Ordinal);
            Assert.That(createIndex, Is.GreaterThan(0));
            int returnIndex = source.IndexOf("return new PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch(", StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(createIndex));
            string createBody = source.Substring(createIndex, returnIndex - createIndex);

            // The marker path set is constructed exactly once, before the loop.
            int markerIndex = createBody.IndexOf("new CaptureRunMarkerPathSet(", StringComparison.Ordinal);
            Assert.That(markerIndex, Is.GreaterThan(0));
            int loopIndex = createBody.IndexOf("for (", StringComparison.Ordinal);
            Assert.That(loopIndex, Is.GreaterThan(markerIndex));

            Assert.That(
                createBody.IndexOf("new CaptureRunMarkerPathSet(", StringComparison.Ordinal),
                Is.EqualTo(createBody.LastIndexOf("new CaptureRunMarkerPathSet(", StringComparison.Ordinal)));

            // The loop materializes only through the shared-path overload.
            Assert.That(createBody, Does.Contain("CreateIndexLocalWithSharedPaths("));
        }

        [Test]
        public void Batch_ThousandEntries_LinearMaterialization_TokenOnce()
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

            // 1000 entries x 2 = 2000 staging steps + RemoveStagingFramesRoot
            // + DeletePublicationPlan + 4 tail = 2006.
            Assert.That(plan.Count, Is.EqualTo(2006));

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            Assert.That(batch.Count, Is.EqualTo(2006));

            for (int i = 0; i < 2000; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                Assert.That(prepared.StepIndex, Is.EqualTo(i));
                Assert.That(prepared.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
                Assert.That(prepared.CleanupOperation, Is.Not.Null);
            }

            Assert.That(batch.GetStep(2000).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));
            Assert.That(batch.GetStep(2001).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan));
            Assert.That(batch.GetStep(2005).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
        }

        [Test]
        public void Source_Batch_PlanValidatedOnceOutsideLoop()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.cs");

            int createIndex = source.IndexOf("internal static PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch Create(", StringComparison.Ordinal);
            Assert.That(createIndex, Is.GreaterThan(0));
            int returnIndex = source.IndexOf("return new PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch(", StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(createIndex));
            string createBody = source.Substring(createIndex, returnIndex - createIndex);

            Assert.That(createBody, Does.Contain("actionPlan.TryValidate("));
            Assert.That(
                createBody.IndexOf("TryValidate", StringComparison.Ordinal),
                Is.EqualTo(createBody.LastIndexOf("TryValidate", StringComparison.Ordinal)));
            Assert.That(createBody, Does.Not.Contain("actionPlan.IsValid"));
            Assert.That(createBody, Does.Not.Contain("GetEntry"));
        }

        [Test]
        public void Source_Builder_SimpleDelegation()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatchBuilder.cs");

            Assert.That(source, Does.Contain("PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.Create("));
            Assert.That(source, Does.Not.Contain("TryValidate"));
            Assert.That(source, Does.Not.Contain("IsValid"));
            Assert.That(source, Does.Not.Contain("for ("));
        }

        [Test]
        public void Source_PreparedStep_TokenAccessorBeforeCountOrGetStep()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.cs");

            int accessorIndex = source.IndexOf("token.TryGetIssuedCleanupInputs(", StringComparison.Ordinal);
            Assert.That(accessorIndex, Is.GreaterThan(0));
            int operationFactoryIndex = source.IndexOf("PngJsonCapturePublicationCaptureCompleteCleanupOperationFactory.CreateIndexLocal(", StringComparison.Ordinal);
            Assert.That(operationFactoryIndex, Is.GreaterThan(accessorIndex));

            string preFactory = source.Substring(accessorIndex, operationFactoryIndex - accessorIndex);
            Assert.That(preFactory, Does.Not.Contain("actionPlan.Count"));
            Assert.That(preFactory, Does.Not.Contain("actionPlan.GetStep"));
            Assert.That(preFactory, Does.Not.Contain("for ("));
        }

        [Test]
        public void Source_NoFilesystemOrBackendReference()
        {
            foreach (string relative in new[]
            {
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatchBuilder.cs"
            })
            {
                string source = ReadSource(relative);
                Assert.That(source, Does.Not.Contain("File."), relative);
                Assert.That(source, Does.Not.Contain("Directory."), relative);
                Assert.That(source, Does.Not.Contain("FileStream"), relative);
                Assert.That(source, Does.Not.Contain("ICaptureRunInitializationRecoveryCleanupBackend"), relative);
                Assert.That(source, Does.Not.Contain("Task"), relative);
            }
        }
    }
}
