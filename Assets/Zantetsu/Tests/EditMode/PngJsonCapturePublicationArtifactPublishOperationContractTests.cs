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
    public class PngJsonCapturePublicationArtifactPublishOperationContractTests
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

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishMissingArtifacts => CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;

        private static CaptureRunPublicationArtifactRecoveryAction PublishArtifact => CaptureRunPublicationArtifactRecoveryAction.PublishArtifact;

        private static CaptureRunPublicationArtifactRecoveryAction ReinspectArtifacts => CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts;

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

        private PngJsonCapturePublicationArtifactInspectionSnapshot MakePublishPngSnapshot(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            operation = snapshot.Operation;
            return snapshot;
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPublishPngPlan(
            out PngJsonCapturePublicationArtifactInspectionOperation operation,
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out operation, out authority);
            return BuildPlan(snapshot);
        }

        private static PngJsonCapturePublicationArtifactPublishOperation ForgeOperation(
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan,
            int stepIndex,
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet)
        {
            PngJsonCapturePublicationArtifactPublishOperation operation = (PngJsonCapturePublicationArtifactPublishOperation)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactPublishOperation));
            SetField(operation, "_actionPlan", plan);
            SetField(operation, "_stepIndex", stepIndex);
            SetField(operation, "_artifactPaths", pathSet);
            return operation;
        }

        // ---- Forwarding ----

        [Test]
        public void Operation_Png_ForwardsAllValues()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(
                out PngJsonCapturePublicationArtifactInspectionOperation operation, out PngJsonCapturePublicationArtifactInspectionAuthority authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet = operation.GetArtifactPaths(0);

            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(publish.ActionPlan, Is.SameAs(plan));
            Assert.That(publish.Decision, Is.SameAs(plan.Decision));
            Assert.That(publish.Authority, Is.SameAs(authority));
            Assert.That(publish.AuthorityKind, Is.EqualTo(authority.Kind));
            Assert.That(publish.StepIndex, Is.EqualTo(0));
            Assert.That(publish.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(publish.EntryIndex, Is.EqualTo(0));
            Assert.That(publish.ArtifactKind, Is.EqualTo(Png));
            Assert.That(publish.ArtifactPaths, Is.SameAs(pathSet));
            Assert.That(publish.Entry, Is.SameAs(pathSet.Entry));
            Assert.That(publish.CaptureFrameId, Is.EqualTo(10));
            Assert.That(publish.SourcePath, Is.EqualTo(pathSet.StagingPngPath));
            Assert.That(publish.DestinationPath, Is.EqualTo(pathSet.FinalPngPath));
            Assert.That(publish.ExpectedByteCount, Is.EqualTo(pathSet.Entry.PngByteLength));
            Assert.That(publish.ExpectedContentSha256, Is.EqualTo(pathSet.Entry.PngContentSha256));
            Assert.That(publish.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(publish.LockIdentityEvidence, Is.SameAs(plan.LockIdentityEvidence));
            Assert.That(publish.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(publish.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(publish.RunManifestContentSha256, Is.EqualTo(plan.RunManifestContentSha256));
            Assert.That(publish.IsValid, Is.True);
        }

        [Test]
        public void Operation_Sidecar_ForwardsAllValues()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvMatchesExpected, EvMatchesExpected, EvAbsent);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet = snapshot.Operation.GetArtifactPaths(0);

            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(publish.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(publish.SourcePath, Is.EqualTo(pathSet.StagingSidecarPath));
            Assert.That(publish.DestinationPath, Is.EqualTo(pathSet.FinalSidecarPath));
            Assert.That(publish.ExpectedByteCount, Is.EqualTo(pathSet.Entry.SidecarByteLength));
            Assert.That(publish.ExpectedContentSha256, Is.EqualTo(pathSet.Entry.SidecarContentSha256));
            Assert.That(publish.IsValid, Is.True);
        }

        [Test]
        public void Operation_FreshAuthority_Png_Works()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeFreshAuthority(10), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            Assert.That(plan.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));

            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(publish.ArtifactKind, Is.EqualTo(Png));
            Assert.That(publish.EntryIndex, Is.EqualTo(0));
            Assert.That(publish.IsValid, Is.True);
        }

        [Test]
        public void Operation_BothKinds_StepOrderPngThenSidecar()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            Assert.That(plan.Count, Is.EqualTo(3));

            PngJsonCapturePublicationArtifactPublishOperation png =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);
            PngJsonCapturePublicationArtifactPublishOperation sidecar =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 1);

            Assert.That(png.ArtifactKind, Is.EqualTo(Png));
            Assert.That(png.EntryIndex, Is.EqualTo(0));
            Assert.That(png.IsValid, Is.True);
            Assert.That(sidecar.ArtifactKind, Is.EqualTo(Sidecar));
            Assert.That(sidecar.EntryIndex, Is.EqualTo(0));
            Assert.That(sidecar.IsValid, Is.True);
        }

        // ---- Factory rejection ----

        [Test]
        public void Factory_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_InvalidPlan_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = (PngJsonCapturePublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_StepIndexOutOfRange_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);

            foreach (int bad in new[] { -1, 2, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, bad));
                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Factory_NonPublishRoutingStep_Rejected()
        {
            // A CommitCaptureIndex plan's only step is a routing step.
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            Assert.That(plan.Count, Is.EqualTo(1));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_ReinspectStep_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);
            int reinspectIndex = plan.Count - 1;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, reinspectIndex));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_NullToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void Factory_CrossPlanToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan planA = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan planB = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(planB, tokenA, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Factory_StaleToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            owner.Dispose();
            _owners.Remove(owner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        // ---- Precondition rejection ----

        [Test]
        public void Factory_StagingNotMatches_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(snapshot.GetEntry(0), "_stagingPngStatus", EvAbsent);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void Factory_FinalNotAbsent_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(snapshot.GetEntry(0), "_finalPngStatus", EvMatchesExpected);
            SetField(snapshot.GetEntry(0), "_finalPngProbedByteCount", 16L);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void Factory_SourceEqualsDestination_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out PngJsonCapturePublicationArtifactInspectionOperation operation, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            PngJsonCapturePublicationArtifactInspectionPathSet pathSet = operation.GetArtifactPaths(0);
            SetField(pathSet, "_stagingPngPath", pathSet.FinalPngPath);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void Factory_ExpectedByteCountCorrupt_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out PngJsonCapturePublicationArtifactInspectionOperation operation, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(operation.GetArtifactPaths(0).Entry, "_pngByteLength", 0L);

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        [Test]
        public void Factory_ExpectedHashCorrupt_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out PngJsonCapturePublicationArtifactInspectionOperation operation, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(operation.GetArtifactPaths(0).Entry, "_pngContentSha256", "nothex");

            Assert.That(plan.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0));
        }

        // ---- Forge defense and validity under mutation ----

        [Test]
        public void Operation_ForgedFields_IsValidFalse_NoException()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(
                out PngJsonCapturePublicationArtifactInspectionOperation operation, out _);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet = operation.GetArtifactPaths(0);

            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);
            Assert.That(publish.IsValid, Is.True);

            // Null plan.
            Assert.That(ForgeOperation(null, 0, pathSet).IsValid, Is.False);

            // Step index out of range.
            Assert.That(ForgeOperation(plan, 99, pathSet).IsValid, Is.False);

            // Null path set.
            Assert.That(ForgeOperation(plan, 0, null).IsValid, Is.False);

            // Foreign path set (same plan authority, different entry index impossible;
            // a different authority's path set must fail the authority correlation).
            PngJsonCapturePublicationArtifactInspectionPathSet foreign = MakeOperation(MakeRecoveryAuthority()).GetArtifactPaths(0);
            Assert.That(ForgeOperation(plan, 0, foreign).IsValid, Is.False);
        }

        [Test]
        public void Operation_StepSubstitutionAfterToken_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(publish.IsValidIndexLocal(token), Is.True);

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            steps[0] = new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, Png);

            Assert.That(token.IsIssuedFor(plan), Is.False);
            Assert.That(publish.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Operation_OwnerRelease_IsValidAndIndexLocalFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(publish.IsValid, Is.True);
            Assert.That(publish.IsValidIndexLocal(token), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(publish.IsValid, Is.False);
            Assert.That(publish.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Operation_KindSwapAfterToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakePublishPngSnapshot(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.That(publish.IsValidIndexLocal(token), Is.True);

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                (CaptureRunPublicationArtifactRecoveryStep[])GetField(plan, "_steps");
            steps[0] = new CaptureRunPublicationArtifactRecoveryStep(PublishArtifact, 0, Sidecar);

            Assert.That(publish.IsValid, Is.False);
            Assert.That(publish.IsValidIndexLocal(token), Is.False);
        }

        // ---- Isolation / shape ----

        [Test]
        public void Operation_RepeatedCreate_DistinctInstances_SharedInput()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);

            PngJsonCapturePublicationArtifactPublishOperation first =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);
            PngJsonCapturePublicationArtifactPublishOperation second =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(first.ActionPlan, Is.SameAs(plan));
            Assert.That(second.ActionPlan, Is.SameAs(plan));
            Assert.That(first.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(second.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(first.ArtifactPaths, Is.SameAs(second.ArtifactPaths));
            Assert.That(first.SourcePath, Is.EqualTo(second.SourcePath));
            Assert.That(first.DestinationPath, Is.EqualTo(second.DestinationPath));
        }

        [Test]
        public void Operation_DoesNotMutateOrDisposeInputs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);

            PngJsonCapturePublicationArtifactPublishOperation publish =
                PngJsonCapturePublicationArtifactPublishOperationFactory.Create(plan, 0);

            Assert.That(owner.IsCreated, Is.True);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(publish.IsValid, Is.True);
        }

        [Test]
        public void Operation_TypeShape_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactPublishOperation);

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
        public void Operation_FieldShape_ThreeReadonlyFields()
        {
            FieldInfo[] fields = typeof(PngJsonCapturePublicationArtifactPublishOperation).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(3));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Factory_IsStaticWithNoState()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactPublishOperationFactory);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Shape_NoLeaseTokenBytesOrStreamExposure()
        {
            foreach (Type type in new[] { typeof(PngJsonCapturePublicationArtifactPublishOperation), typeof(PngJsonCapturePublicationArtifactPublishOperationFactory) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || field.FieldType == typeof(byte[])
                        || field.FieldType == typeof(Stream),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, token, bytes, or stream.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || prop.PropertyType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || prop.PropertyType == typeof(byte[])
                        || prop.PropertyType == typeof(Stream),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a lease, token, bytes, or stream.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                        || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                        || method.ReturnType == typeof(byte[])
                        || method.ReturnType == typeof(Stream),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a lease, token, bytes, or stream.");
                }
            }
        }

        [Test]
        public void Source_NoFilesystemOrForbiddenDependencies()
        {
            string operationSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublishOperation.cs");
            string factorySource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublishOperationFactory.cs");

            foreach (string source in new[] { operationSource, factorySource })
            {
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
                Assert.That(source, Does.Not.Contain("HashSet"));
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("SHA"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("Deserialize"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Notifier"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Backend"));
            }

            // The index-local path must not re-validate the whole plan: full
            // token issuance happens exactly once each in Create and IsValid,
            // never in CreateIndexLocal or IsValidIndexLocal.
            int fullValidationCount = operationSource.Split(
                new[] { "TryAcquireValidationToken" }, StringSplitOptions.None).Length - 1;
            Assert.That(fullValidationCount, Is.EqualTo(2));
            Assert.That(operationSource, Does.Not.Contain("actionPlan.IsValid"));
            Assert.That(operationSource, Does.Not.Contain("_actionPlan.IsValid"));
            Assert.That(operationSource, Does.Contain("TryGetIssuedPublishInputs"));
        }

        [Test]
        public void Factory_Create_SingleFullValidation()
        {
            string factorySource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublishOperationFactory.cs");
            string operationSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublishOperation.cs");

            Assert.That(factorySource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(operationSource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(operationSource, Does.Contain("TryAcquireValidationToken"));

            // Create delegates full validation to the operation's static factory.
            Assert.That(factorySource, Does.Contain("PngJsonCapturePublicationArtifactPublishOperation.Create"));
            Assert.That(factorySource, Does.Contain("PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal"));
        }

        // ---- Token API ----

        [Test]
        public void Token_TryAcquire_ValidPlan_True()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);

            Assert.That(plan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token), Is.True);
            Assert.That(token, Is.Not.Null);
            Assert.That(token.IsIssuedFor(plan), Is.True);
        }

        [Test]
        public void Token_TryAcquire_InvalidPlan_FalseNoThrow()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = (PngJsonCapturePublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan));

            Assert.That(plan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token), Is.False);
            Assert.That(token, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedPublishInputs_PublishStep_True()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(
                token.TryGetIssuedPublishInputs(
                    plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactEntryObservation observation),
                Is.True);
            Assert.That(step.Matches(PublishArtifact, 0, Png), Is.True);
            Assert.That(observation.EntryIndex, Is.EqualTo(0));
        }

        [Test]
        public void Token_TryGetIssuedPublishInputs_RoutingStep_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(snapshot);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            Assert.That(
                token.TryGetIssuedPublishInputs(
                    plan, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactEntryObservation observation),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(observation, Is.Null);
        }

        [Test]
        public void Token_TryGetIssuedPublishInputs_OutOfRange_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            foreach (int bad in new[] { -1, plan.Count, int.MinValue, int.MaxValue })
            {
                Assert.That(
                    token.TryGetIssuedPublishInputs(
                        plan, bad, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactEntryObservation observation),
                    Is.False);
                Assert.That(step, Is.Null);
                Assert.That(observation, Is.Null);
            }
        }

        [Test]
        public void Token_TryGetIssuedPublishInputs_ForeignPlan_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan planA = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan planB = BuildPublishPngPlan(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            Assert.That(
                tokenA.TryGetIssuedPublishInputs(
                    planB, 0, out CaptureRunPublicationArtifactRecoveryStep step, out PngJsonCapturePublicationArtifactEntryObservation observation),
                Is.False);
            Assert.That(step, Is.Null);
            Assert.That(observation, Is.Null);
        }

        // ---- Scale ----

        [Test]
        public void Factory_ThousandPublishSteps_TokenOnce_IndexLocalPerStep()
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

            Assert.That(plan.Disposition, Is.EqualTo(PublishMissingArtifacts));
            Assert.That(plan.Count, Is.EqualTo(count + 1));

            // Acquire the token exactly once outside the loop.
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            for (int i = 0; i < count; i++)
            {
                PngJsonCapturePublicationArtifactPublishOperation publish =
                    PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(plan, token, i);

                Assert.That(publish.ArtifactKind, Is.EqualTo(Png));
                Assert.That(publish.EntryIndex, Is.EqualTo(i));
                Assert.That(publish.CaptureFrameId, Is.EqualTo(frameIds[i]));
                Assert.That(publish.IsValidIndexLocal(token), Is.True);
            }

            Assert.That(token.IsIssuedFor(plan), Is.True);
        }
    }
}
