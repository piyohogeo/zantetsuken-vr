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
    public class PngJsonCaptureRunCaptureIndexCommitOperationContractTests
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

        private static CaptureRunPublicationDocumentObservationStatus DocLimitExceeded => CaptureRunPublicationDocumentObservationStatus.LimitExceeded;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        private static CaptureRunPublicationArtifactRecoveryAction ReinspectArtifacts => CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts;

        private static CaptureRunPublicationArtifactRecoveryAction CommitCaptureIndexAction => CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryAction ContinueCaptureCompleteCleanup => CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup;

        private static CaptureRunPublicationArtifactRecoveryAction StopOrphanedPreTrace => CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace;

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

        // ---- Commit graph helpers ----

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeCommitSnapshot(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            return MakeSnapshotSingle(authority, EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
        }

        private static PngJsonCapturePublicationArtifactRecoveryActionPlan BuildCommitPlan(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            return BuildPlan(MakeCommitSnapshot(authority));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildRecoveryCommitPlan(
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            authority = MakeRecoveryAuthority();
            return BuildCommitPlan(authority);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildFreshCommitPlan(
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            authority = MakeFreshAuthority(10);
            return BuildCommitPlan(authority);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildRecoveryCommitPlan(
            out PngJsonCapturePublicationArtifactInspectionAuthority authority,
            PngJsonCapturePublicationPlan plan,
            CaptureRunPublicationDocumentObservation captureIndexTemporary)
        {
            authority = MakeRecoveryAuthority(plan, false, captureIndexTemporary);
            return BuildCommitPlan(authority);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildLargeRecoveryCommitPlan(
            PngJsonCapturePublicationPlan plan)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation(
                16 * 1024 * 1024, 1000, 512, out _);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeRecoverySnapshot(
                inspector,
                operation,
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);

            int count = plan.EntryCount;
            CaptureRunPublicationEvidenceStatus[] stagingPng = new CaptureRunPublicationEvidenceStatus[count];
            CaptureRunPublicationEvidenceStatus[] stagingSidecar = new CaptureRunPublicationEvidenceStatus[count];
            CaptureRunPublicationEvidenceStatus[] finalPng = new CaptureRunPublicationEvidenceStatus[count];
            CaptureRunPublicationEvidenceStatus[] finalSidecar = new CaptureRunPublicationEvidenceStatus[count];
            for (int i = 0; i < count; i++)
            {
                stagingPng[i] = EvAbsent;
                stagingSidecar[i] = EvAbsent;
                finalPng[i] = EvMatchesExpected;
                finalSidecar[i] = EvMatchesExpected;
            }

            return BuildPlan(MakeSnapshotArray(
                authority, EvMatchesExpected, 1, stagingPng, stagingSidecar, finalPng, finalSidecar, maximumPngByteCount: 2000));
        }

        private static CaptureRunPublicationPathSet GetPublicationPaths(
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan)
        {
            return plan.Authority.PublicationPaths;
        }

        private static PngJsonCaptureRunCaptureIndexCommitOperation ForgeOperation(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunCaptureIndexCommitMode mode,
            byte[] canonicalBytes)
        {
            PngJsonCaptureRunCaptureIndexCommitOperation operation = (PngJsonCaptureRunCaptureIndexCommitOperation)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCaptureRunCaptureIndexCommitOperation));
            SetField(operation, "_actionPlan", actionPlan);
            SetField(operation, "_stepIndex", stepIndex);
            SetField(operation, "_publicationPaths", publicationPaths);
            SetField(operation, "_mode", mode);
            SetField(operation, "_canonicalBytes", canonicalBytes);
            return operation;
        }

        // ---- Factory rejection ----

        [Test]
        public void Factory_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_InvalidPlan_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = (PngJsonCapturePublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_NullToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void Factory_StepIndexOutOfRange_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);

            foreach (int bad in new[] { -1, 1, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, bad));
                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Factory_PublishStep_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_ReinspectStep_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 1));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_StopStep_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            Assert.That(plan.Count, Is.EqualTo(1));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_CaptureCompleteStep_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(indexAuthoritative: true), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            Assert.That(plan.Count, Is.EqualTo(1));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_CrossPlanToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan planA = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan planB = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(planB, tokenA, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_StaleToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            owner.Dispose();
            _owners.Remove(owner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        // ---- Token API ----

        [Test]
        public void Token_TryGetIssuedCommitInputs_CommitStep_True()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(
                token.TryGetIssuedCommitInputs(plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                Is.True);
            Assert.That(step.Matches(CommitCaptureIndexAction, -1, None), Is.True);
            Assert.That(decision, Is.SameAs(plan.Decision));
        }

        [Test]
        public void Token_TryGetIssuedCommitInputs_PublishStep_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(
                token.TryGetIssuedCommitInputs(plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(decision, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedCommitInputs_OutOfRange_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            foreach (int bad in new[] { -1, 1, int.MinValue, int.MaxValue })
            {
                Assert.That(
                    token.TryGetIssuedCommitInputs(plan, bad, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                    Is.False);
                Assert.That(step, Is.Null);
                Assert.That(decision, Is.Null);
            }
        }

        [Test]
        public void Token_TryGetIssuedCommitInputs_ForeignPlan_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan planA = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan planB = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            Assert.That(
                tokenA.TryGetIssuedCommitInputs(planB, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(decision, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedCommitInputs_EntryIndexForged_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            SetField(steps[0], "_entryIndex", 0);

            Assert.That(
                token.TryGetIssuedCommitInputs(plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(decision, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedCommitInputs_ArtifactKindForged_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            SetField(steps[0], "_artifactKind", CaptureRunPublicationArtifactKind.Png);

            Assert.That(
                token.TryGetIssuedCommitInputs(plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(decision, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedCommitInputs_StepArraySubstituted_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            SetField(plan, "_steps", new[]
            {
                new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndexAction, -1, None),
                new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndexAction, -1, None)
            });

            Assert.That(
                token.TryGetIssuedCommitInputs(plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactRecoveryDecision decision),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(decision, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedCommitMode_Absent_CreateMode()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(token.TryGetIssuedCommitMode(plan, out CaptureRunCaptureIndexCommitMode mode), Is.True);
            Assert.That(mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
        }

        [Test]
        public void Token_TryGetIssuedCommitMode_Canonical_ReuseMode()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(
                out _, plan, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan));
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            Assert.That(token.TryGetIssuedCommitMode(actionPlan, out CaptureRunCaptureIndexCommitMode mode), Is.True);
            Assert.That(mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit));
        }

        [Test]
        public void Token_TryGetIssuedCommitMode_Invalid_ReplaceMode()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(
                out _, MakePlan(), MakeDoc(CaptureIndexTemporary, DocInvalid, 10));
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            Assert.That(token.TryGetIssuedCommitMode(actionPlan, out CaptureRunCaptureIndexCommitMode mode), Is.True);
            Assert.That(mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit));
        }

        [Test]
        public void Token_TryGetIssuedCommitMode_Fresh_CreateMode()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildFreshCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(token.TryGetIssuedCommitMode(plan, out CaptureRunCaptureIndexCommitMode mode), Is.True);
            Assert.That(mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
        }

        [Test]
        public void Token_TryGetIssuedCommitMode_PublishPlan_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(token.TryGetIssuedCommitMode(plan, out CaptureRunCaptureIndexCommitMode mode), Is.False);
            Assert.That(mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.None));
        }

        [Test]
        public void Token_TryGetIssuedCommitMode_IndexSwapped_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(plan.Authority.RecoveryDecision.Snapshot, "_captureIndex",
                MakeDoc(CaptureIndex, DocCanonical, 100, plan.AuthoritativePlan));

            Assert.That(token.TryGetIssuedCommitMode(plan, out CaptureRunCaptureIndexCommitMode mode), Is.False);
            Assert.That(mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.None));
        }

        // ---- Recovery mode derivation ----

        [Test]
        public void Operation_AbsentTemporary_CreateMode()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_CanonicalTemporary_ReuseMode()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(
                out _, plan, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan));

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(actionPlan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_InvalidTemporary_ReplaceMode()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(
                out _, MakePlan(), MakeDoc(CaptureIndexTemporary, DocInvalid, 10));

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(actionPlan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_CommittedIndexNotAbsent_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = actionPlan.Authority.RecoveryDecision.Snapshot;

            CaptureRunPublicationDocumentObservation[] forged = new[]
            {
                MakeDoc(CaptureIndex, DocCanonical, 100, actionPlan.AuthoritativePlan),
                MakeDoc(CaptureIndex, DocInvalid, 10),
                MakeDoc(CaptureIndex, DocLimitExceeded, 1001)
            };

            foreach (CaptureRunPublicationDocumentObservation index in forged)
            {
                SetField(publicationSnapshot, "_captureIndex", index);

                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(actionPlan, token, 0));
                Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
            }
        }

        [Test]
        public void Operation_CanonicalTemporaryMismatch_Rejected()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(
                out _, plan, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan));

            // Forging only the canonical temporary's plan content breaks the
            // publication classification, so a full validation rejects it.
            SetField(actionPlan.Authority.RecoveryDecision.Snapshot.CaptureIndexTemporary, "_plan", MakePlan(entries: new[] { MakeEntry(11) }));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(actionPlan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_CanonicalTemporaryPlanContentForgedAfterToken_IndexLocalTrustsProof()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(
                out _, plan, MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan));
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(actionPlan, token, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit));

            // Forge only the temporary's plan content after token issuance: the
            // O(1) index-local proof (references + status values) is intact, so
            // it must not re-scan the plan.
            SetField(actionPlan.Authority.RecoveryDecision.Snapshot.CaptureIndexTemporary, "_plan", MakePlan(entries: new[] { MakeEntry(11) }));

            Assert.That(commit.IsValidIndexLocal(token), Is.True);

            // A fresh full validation re-proves the classification and rejects.
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_LimitExceededTemporary_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            CaptureRunPublicationDocumentObservation tmp = actionPlan.Authority.RecoveryDecision.Snapshot.CaptureIndexTemporary;
            SetField(tmp, "_status", DocLimitExceeded);
            SetField(tmp, "_probedByteCount", 1001);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(actionPlan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_UndefinedTemporaryStatus_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            CaptureRunPublicationDocumentObservation tmp = actionPlan.Authority.RecoveryDecision.Snapshot.CaptureIndexTemporary;
            SetField(tmp, "_status", (CaptureRunPublicationDocumentObservationStatus)99);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(actionPlan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void DeriveMode_LimitExceeded_Rejected()
        {
            CaptureRunPublicationDocumentObservation limitExceeded = MakeDoc(CaptureIndexTemporary, DocLimitExceeded, 1001);
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperation.DeriveMode(limitExceeded));
            Assert.That(ex.ParamName, Is.EqualTo("captureIndexTemporary"));
        }

        [Test]
        public void DeriveMode_Null_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureRunCaptureIndexCommitOperation.DeriveMode(null));
            Assert.That(ex.ParamName, Is.EqualTo("captureIndexTemporary"));
        }

        // ---- Fresh ----

        [Test]
        public void Operation_Fresh_CreateTemporaryAndCommit()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildFreshCommitPlan(out PngJsonCapturePublicationArtifactInspectionAuthority authority);

            Assert.That(authority.Kind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(commit.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Source_FreshModeDoesNotTouchRecoveryObservations()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryActionPlan.cs");

            int freshIndex = source.IndexOf("IsFresh", StringComparison.Ordinal);
            Assert.That(freshIndex, Is.GreaterThan(0));

            int recoveryIndex = source.IndexOf("IsRecovery", StringComparison.Ordinal);
            Assert.That(recoveryIndex, Is.GreaterThan(freshIndex));

            string freshBranch = source.Substring(freshIndex, recoveryIndex - freshIndex);
            Assert.That(freshBranch, Does.Contain("CreateTemporaryAndCommit"));
            Assert.That(freshBranch, Does.Not.Contain("RecoveryDecision"));
            Assert.That(freshBranch, Does.Not.Contain("CaptureIndexTemporary"));
            Assert.That(freshBranch, Does.Not.Contain(".CaptureIndex"));
        }

        [Test]
        public void Source_IndexLocalPathDoesNotScanEntries()
        {
            string operationSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitOperation.cs");
            string planSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryActionPlan.cs");

            // The canonical temporary match is proven by the publication
            // classification during full validation; the index-local path must
            // not re-run a full plan comparison or validate a document's plan.
            Assert.That(operationSource, Does.Not.Contain("PlansEqual"));
            Assert.That(planSource, Does.Not.Contain("PlansEqual"));
            Assert.That(operationSource, Does.Not.Contain("captureIndexTemporary.IsValid"));
            Assert.That(operationSource, Does.Not.Contain("captureIndex.IsValid"));
        }

        // ---- Forwarding / paths / bytes ----

        [Test]
        public void Operation_ForwardsAllValues()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.ActionPlan, Is.SameAs(plan));
            Assert.That(commit.StepIndex, Is.EqualTo(0));
            Assert.That(commit.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(commit.Decision, Is.SameAs(plan.Decision));
            Assert.That(commit.Authority, Is.SameAs(plan.Authority));
            Assert.That(commit.AuthoritativePlan, Is.SameAs(plan.AuthoritativePlan));
            Assert.That(commit.PublicationPaths, Is.SameAs(GetPublicationPaths(plan)));
            Assert.That(commit.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(commit.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(commit.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(commit.RunManifestContentSha256, Is.EqualTo(plan.RunManifestContentSha256));
            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_TemporaryAndFinalPathExact()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.TemporaryPath, Is.EqualTo(paths.CaptureIndexTemporaryPath));
            Assert.That(commit.FinalPath, Is.EqualTo(paths.CaptureIndexPath));
            Assert.That(commit.TemporaryPath, Is.Not.EqualTo(commit.FinalPath));
            Assert.That(Path.GetFileName(commit.TemporaryPath), Is.EqualTo("capture.index.tmp"));
            Assert.That(Path.GetFileName(commit.FinalPath), Is.EqualTo("capture.index"));
        }

        [Test]
        public void Operation_CanonicalBytesMatchCodecOutput()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            byte[] expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(commit.AuthoritativePlan);
            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(expected));
            Assert.That(commit.ByteCount, Is.EqualTo((long)expected.Length));
        }

        [Test]
        public void GetCanonicalBytes_DefensiveCopy_NoExternalAlias()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            byte[] first = commit.GetCanonicalBytes();
            byte[] second = commit.GetCanonicalBytes();

            Assert.That(first, Is.Not.Null);
            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(first, Is.EqualTo(second));

            first[0] = (byte)(first[0] ^ 0xFF);

            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(second));
            Assert.That(commit.IsValid, Is.True);
        }

        // ---- Corruption ----

        [Test]
        public void Operation_AuthorityKindSwap_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildFreshCommitPlan(out PngJsonCapturePublicationArtifactInspectionAuthority authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(commit.IsValidIndexLocal(token), Is.True);

            SetField(authority, "_freshSeed", null);

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_DecisionSubstituted_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            PngJsonCapturePublicationArtifactRecoveryDecision other = BuildRecoveryCommitPlan(out _).Decision;
            SetField(plan, "_decision", other);

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_SnapshotSubstituted_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            PngJsonCapturePublicationArtifactInspectionSnapshot other =
                MakeCommitSnapshot(MakeRecoveryAuthority());
            SetField(plan.Decision, "_snapshot", other);

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_AuthoritySubstituted_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(plan.Decision.Snapshot.Operation, "_authority", MakeRecoveryAuthority());

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_PublicationPathsSubstituted_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out PngJsonCapturePublicationArtifactInspectionAuthority authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(authority.RecoveryDecision.Snapshot.Operation, "_publicationPaths", new CaptureRunPublicationPathSet(MakeLayout(2)));

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_RootLayoutSubstituted_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out PngJsonCapturePublicationArtifactInspectionAuthority authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(authority.PublicationPaths, "_rootLayout", MakeLayout(2));

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_TraceStatusCorrupt_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(plan.Decision.Snapshot, "_traceManifestStatus", EvMismatch);

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_RunIdCorrupt_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(plan.AuthoritativePlan, "_testRunId", 2L);

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_InitializationIdCorrupt_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(plan.AuthoritativePlan, "_runInitializationId", "11111111111111111111111111111111");

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_ManifestHashCorrupt_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildFreshCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            SetField(plan.AuthoritativePlan, "_runManifestContentSha256", ManifestHash);

            Assert.That(commit.IsValidIndexLocal(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_StepSubstitutedAfterToken_IsValidIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            steps[0] = new CaptureRunPublicationArtifactRecoveryStep(CommitCaptureIndexAction, -1, None);

            Assert.That(token.IsIssuedFor(plan), Is.False);
            Assert.That(commit.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Operation_CanonicalBytesTampered_IsValidWithTokenFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(commit.IsValidWithToken(token), Is.True);
            Assert.That(commit.IsValidIndexLocal(token), Is.True);

            byte[] tampered = commit.GetCanonicalBytes();
            tampered[0] = (byte)(tampered[0] ^ 0xFF);
            SetField(commit, "_canonicalBytes", tampered);

            Assert.That(commit.IsValidIndexLocal(token), Is.True);
            Assert.That(commit.IsValidWithToken(token), Is.False);
            Assert.That(commit.IsValid, Is.False);
        }

        [Test]
        public void Operation_OwnerRelease_AllFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(commit.IsValid, Is.True);
            Assert.That(commit.IsValidWithToken(token), Is.True);
            Assert.That(commit.IsValidIndexLocal(token), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(commit.IsValid, Is.False);
            Assert.That(commit.IsValidWithToken(token), Is.False);
            Assert.That(commit.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Operation_ForgedFields_IsValidFalse_NoException()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);

            Assert.That(commit.IsValid, Is.True);

            Assert.That(ForgeOperation(null, 0, paths, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 99, paths, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 0, null, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);

            CaptureRunPublicationPathSet foreign = new CaptureRunPublicationPathSet(MakeLayout(2));
            Assert.That(ForgeOperation(plan, 0, foreign, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);

            Assert.That(ForgeOperation(plan, 0, paths, CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, commit.GetCanonicalBytes()).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 0, paths, commit.Mode, null).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 0, paths, commit.Mode, new byte[0]).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 0, paths, commit.Mode, new byte[] { 1, 2, 3 }).IsValid, Is.False);

            // Forging an entry observation's final status invalidates the whole
            // plan, so the full validation fails.
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = plan.Decision.Snapshot;
            SetField(snapshot.GetEntry(0), "_finalPngStatus", EvAbsent);
            SetField(snapshot.GetEntry(0), "_finalPngProbedByteCount", 0L);
            Assert.That(commit.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Operation_TypeShape_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(PngJsonCaptureRunCaptureIndexCommitOperation);

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

        [Test]
        public void Operation_FieldShape_FiveReadonlyFields_NoStaticState()
        {
            FieldInfo[] fields = typeof(PngJsonCaptureRunCaptureIndexCommitOperation).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(5));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(byte[])));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationPathSet)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunCaptureIndexCommitMode)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(int)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            FieldInfo[] staticFields = typeof(PngJsonCaptureRunCaptureIndexCommitOperation).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "Operation must not hold static mutable state.");
        }

        [Test]
        public void Factory_IsStaticWithNoState()
        {
            Type type = typeof(PngJsonCaptureRunCaptureIndexCommitOperationFactory);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Shape_NoLeaseOrTokenExposure()
        {
            foreach (Type type in new[] { typeof(PngJsonCaptureRunCaptureIndexCommitOperation), typeof(PngJsonCaptureRunCaptureIndexCommitOperationFactory) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease or token.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a lease or token.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a lease or token.");
                }
            }
        }

        // ---- Source ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string operationSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitOperation.cs");
            string factorySource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitOperationFactory.cs");

            foreach (string source in new[] { operationSource, factorySource })
            {
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
                Assert.That(source, Does.Not.Contain("HashSet"));
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("SHA"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("Guid"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Notifier"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Backend"));
                Assert.That(source, Does.Not.Contain("Store"));
            }

            // The factory must not serialize or copy bytes; the operation
            // serializes once at construction and once in IsValidWithToken,
            // and defensively copies only in the byte getter.
            Assert.That(factorySource, Does.Not.Contain("Array.Copy"));
            Assert.That(factorySource, Does.Not.Contain("SerializeCanonical"));
            Assert.That(operationSource, Does.Contain("SerializeCanonical"));
            Assert.That(operationSource, Does.Contain("GetCanonicalBytes"));
        }

        [Test]
        public void Source_CreateSingleFullValidation_NoDuplication()
        {
            string operationSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitOperation.cs");
            string factorySource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitOperationFactory.cs");

            Assert.That(operationSource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(factorySource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(operationSource, Does.Contain("TryAcquireValidationToken"));

            // The factory must not duplicate full validation, serialization, or
            // mode derivation: it only delegates to the operation's statics.
            Assert.That(factorySource, Does.Not.Contain("TryAcquireValidationToken"));
            Assert.That(factorySource, Does.Not.Contain("SerializeCanonical"));
            Assert.That(factorySource, Does.Not.Contain("CaptureIndexTemporary"));
            Assert.That(factorySource, Does.Contain("PngJsonCaptureRunCaptureIndexCommitOperation.Create"));
            Assert.That(factorySource, Does.Contain("PngJsonCaptureRunCaptureIndexCommitOperation.CreateIndexLocal"));

            // The operation serializes exactly once at construction and once in
            // IsValidWithToken; the index-local validity path never serializes.
            int serializeCount = operationSource.Split(
                new[] { "SerializeCanonical" }, StringSplitOptions.None).Length - 1;
            Assert.That(serializeCount, Is.EqualTo(2));
        }

        // ---- Scale ----

        [Test]
        public void Factory_ThousandEntryPlan_SingleTokenSingleOperationSingleSerialization()
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
                stagingPng[i] = EvAbsent;
                stagingSidecar[i] = EvAbsent;
                finalPng[i] = EvMatchesExpected;
                finalSidecar[i] = EvMatchesExpected;
            }

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeFreshAuthority(frameIds), EvMatchesExpected, 1, stagingPng, stagingSidecar, finalPng, finalSidecar, maximumPngByteCount: 2000);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            Assert.That(plan.Count, Is.EqualTo(1));

            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(commit.IsValidIndexLocal(token), Is.True);
            Assert.That(commit.IsValid, Is.True);

            byte[] expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan.AuthoritativePlan);
            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void Recovery_CanonicalLargePlan_SingleTokenSingleSerialization()
        {
            const int count = 1000;

            PngJsonCapturePublicationPlanEntry[] entries = new PngJsonCapturePublicationPlanEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntry(i + 1);
            }

            PngJsonCapturePublicationPlan plan = MakePlan(entries: entries);
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildLargeRecoveryCommitPlan(plan);

            Assert.That(actionPlan.Count, Is.EqualTo(1));

            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(actionPlan, token, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit));
            Assert.That(commit.IsValidIndexLocal(token), Is.True);
            Assert.That(commit.IsValid, Is.True);

            byte[] expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan);
            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(expected));
        }
    }
}
