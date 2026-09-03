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
    public class PngJsonCaptureRunCaptureIndexCommitterReceiptContractTests
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

        private sealed class FakeCommitter : IPngJsonCaptureRunCaptureIndexCommitter
        {
            public PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
                PngJsonCaptureRunCaptureIndexCommitOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return PngJsonCaptureRunCaptureIndexCommitReceipt.Create(this, operation, token);
            }
        }

        private sealed class ThrowingCommitter : IPngJsonCaptureRunCaptureIndexCommitter
        {
            public PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
                PngJsonCaptureRunCaptureIndexCommitOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                throw new InvalidOperationException("Backend failure.");
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

        private static PngJsonCaptureRunCaptureIndexCommitReceipt ForgeReceipt(
            IPngJsonCaptureRunCaptureIndexCommitter issuedBy,
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = (PngJsonCaptureRunCaptureIndexCommitReceipt)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCaptureRunCaptureIndexCommitReceipt));
            SetField(receipt, "_issuedBy", issuedBy);
            SetField(receipt, "_operation", operation);
            SetField(receipt, "_token", token);
            return receipt;
        }

        // ---- Interface ----

        [Test]
        public void Interface_SingleMethodOnly()
        {
            MethodInfo[] methods = typeof(IPngJsonCaptureRunCaptureIndexCommitter).GetMethods();

            Assert.That(methods.Length, Is.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Commit"));
            Assert.That(methods[0].ReturnType, Is.EqualTo(typeof(PngJsonCaptureRunCaptureIndexCommitReceipt)));

            ParameterInfo[] parameters = methods[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(2));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(PngJsonCaptureRunCaptureIndexCommitOperation)));
            Assert.That(parameters[0].Name, Is.EqualTo("operation"));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)));
            Assert.That(parameters[1].Name, Is.EqualTo("token"));
        }

        [Test]
        public void CrossReceiptSubstitution_TypeDistinctAndNonAssignable()
        {
            Type publisherInterface = typeof(IPngJsonCapturePublicationArtifactPublisher);
            Type committerInterface = typeof(IPngJsonCaptureRunCaptureIndexCommitter);

            Assert.That(publisherInterface, Is.Not.SameAs(committerInterface));
            Assert.That(publisherInterface.IsAssignableFrom(committerInterface), Is.False);
            Assert.That(committerInterface.IsAssignableFrom(publisherInterface), Is.False);

            Assert.That(typeof(PngJsonCapturePublicationArtifactPublishReceipt), Is.Not.SameAs(typeof(PngJsonCaptureRunCaptureIndexCommitReceipt)));
        }

        // ---- Issuance / forwarding ----

        [Test]
        public void Receipt_ForwardsAllValuesAndReferences()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, token);

            Assert.That(receipt.IssuedBy, Is.SameAs(committer));
            Assert.That(receipt.Operation, Is.SameAs(commit));
            Assert.That(receipt.ActionPlan, Is.SameAs(plan));
            Assert.That(receipt.StepIndex, Is.EqualTo(0));
            Assert.That(receipt.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(receipt.TemporaryPath, Is.EqualTo(paths.CaptureIndexTemporaryPath));
            Assert.That(receipt.FinalPath, Is.EqualTo(paths.CaptureIndexPath));
            Assert.That(receipt.ByteCount, Is.EqualTo(commit.ByteCount));
            Assert.That(receipt.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(receipt.LockIdentityEvidence, Is.SameAs(plan.LockIdentityEvidence));
            Assert.That(receipt.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(receipt.RunInitializationId, Is.EqualTo(plan.RunInitializationId));

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(committer, commit, token), Is.True);
        }

        // ---- Factory null rejection ----

        [Test]
        public void Factory_NullIssuer_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(null, commit, token));
            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void Factory_NullOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(new FakeCommitter(), null, token));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Factory_NullToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(new FakeCommitter(), commit, null));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        // ---- Factory correlation rejection ----

        [Test]
        public void Factory_ForeignToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan planA = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan planB = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken tokenB = planB.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(planA, tokenA, 0);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(new FakeCommitter(), commit, tokenB));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Factory_StaleToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            owner.Dispose();
            _owners.Remove(owner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(new FakeCommitter(), commit, token));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Factory_CanonicalBytesTampered_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            byte[] tampered = commit.GetCanonicalBytes();
            tampered[0] = (byte)(tampered[0] ^ 0xFF);
            SetField(commit, "_canonicalBytes", tampered);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(new FakeCommitter(), commit, token));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        // ---- IsIssuedFor reference identity ----

        [Test]
        public void IsIssuedFor_SameValueDifferentReferenceOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation first =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);
            PngJsonCaptureRunCaptureIndexCommitOperation second =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(first, token);

            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(receipt.IsIssuedFor(committer, first, token), Is.True);
            Assert.That(receipt.IsIssuedFor(committer, second, token), Is.False);
        }

        [Test]
        public void IsIssuedFor_ReissuedTokenForSamePlan_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken first = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken second = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, first, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, first);

            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(receipt.IsIssuedFor(committer, commit, first), Is.True);
            Assert.That(receipt.IsIssuedFor(committer, commit, second), Is.False);
        }

        [Test]
        public void IsIssuedFor_ForeignBackend_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            FakeCommitter foreign = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, token);

            Assert.That(receipt.IsIssuedFor(committer, commit, token), Is.True);
            Assert.That(receipt.IsIssuedFor(foreign, commit, token), Is.False);
        }

        // ---- Owner release / corruption fail-closed ----

        [Test]
        public void Receipt_OwnerRelease_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, token);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(committer, commit, token), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(committer, commit, token), Is.False);
        }

        [Test]
        public void Receipt_OperationFieldCorruption_IsValidFalse_NoException()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt valid = committer.Commit(commit, token);
            Assert.That(valid.IsValid, Is.True);

            Assert.That(ForgeReceipt(committer, null, token).IsValid, Is.False);
            Assert.That(ForgeReceipt(committer, null, token).IsIssuedFor(committer, null, token), Is.False);

            Assert.That(ForgeReceipt(committer, ForgeOperation(null, 0, paths, commit.Mode, commit.GetCanonicalBytes()), token).IsValid, Is.False);
            Assert.That(ForgeReceipt(committer, ForgeOperation(plan, 99, paths, commit.Mode, commit.GetCanonicalBytes()), token).IsValid, Is.False);
            Assert.That(ForgeReceipt(committer, ForgeOperation(plan, 0, null, commit.Mode, commit.GetCanonicalBytes()), token).IsValid, Is.False);

            Assert.That(ForgeReceipt(committer, valid.Operation, null).IsValid, Is.False);
        }

        [Test]
        public void Receipt_PlanDecisionCorruption_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, token);
            Assert.That(receipt.IsValid, Is.True);

            PngJsonCapturePublicationArtifactRecoveryDecision other = BuildRecoveryCommitPlan(out _).Decision;
            SetField(plan, "_decision", other);

            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(committer, commit, token), Is.False);
        }

        [Test]
        public void Receipt_CanonicalBytesTampered_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, token);
            Assert.That(receipt.IsValid, Is.True);

            byte[] tampered = commit.GetCanonicalBytes();
            tampered[0] = (byte)(tampered[0] ^ 0xFF);
            SetField(commit, "_canonicalBytes", tampered);

            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(committer, commit, token), Is.False);
        }

        [Test]
        public void Receipt_DoesNotMutateOrReleaseInputs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(authority);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(commit, token);

            Assert.That(owner.IsCreated, Is.True);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(commit.IsValid, Is.True);
            Assert.That(token.IsIssuedFor(plan), Is.True);
            Assert.That(receipt.IsValid, Is.True);
        }

        // ---- Backend exception ----

        [Test]
        public void Backend_ExceptionPropagates_NoReceipt()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCaptureRunCaptureIndexCommitOperation commit =
                PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0);

            Assert.Throws<InvalidOperationException>(() => new ThrowingCommitter().Commit(commit, token));
        }

        // ---- Shape ----

        [Test]
        public void Receipt_TypeShape_SealedNotDisposableNoPublicCtor_PrivateCtor()
        {
            Type type = typeof(PngJsonCaptureRunCaptureIndexCommitReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            ConstructorInfo ctor = type.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    typeof(IPngJsonCaptureRunCaptureIndexCommitter),
                    typeof(PngJsonCaptureRunCaptureIndexCommitOperation),
                    typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                },
                null);
            Assert.That(ctor, Is.Not.Null);
            Assert.That(ctor.IsPrivate, Is.True);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Receipt_FieldShape_ThreeReadonlyReferences_NoStaticState()
        {
            FieldInfo[] fields = typeof(PngJsonCaptureRunCaptureIndexCommitReceipt).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(IPngJsonCaptureRunCaptureIndexCommitter)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCaptureRunCaptureIndexCommitOperation)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(typeof(PngJsonCaptureRunCaptureIndexCommitReceipt).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Shape_NoLeaseOwnerTokenBytesStreamHandleExposure()
        {
            Type type = typeof(PngJsonCaptureRunCaptureIndexCommitReceipt);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
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
                    || method.ReturnType == typeof(PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken)
                    || method.ReturnType == typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)
                    || method.ReturnType == typeof(byte[])
                    || method.ReturnType == typeof(Stream)
                    || method.ReturnType == typeof(ICaptureRunLockHandle),
                    Is.False,
                    type.Name + "." + method.Name + " must not return a lease, owner, token, bytes, stream, or handle.");
            }

            // The commit receipt must never hold a canonical byte array.
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(field.FieldType == typeof(byte[]), Is.False, type.Name + "." + field.Name + " must not hold a byte array.");
            }
        }

        // ---- Source ----

        [Test]
        public void Source_NoFilesystemOrForbiddenDependencies()
        {
            string receiptSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitReceipt.cs");
            string interfaceSource = ReadSource("Assets/Zantetsu/Runtime/Observability/IPngJsonCaptureRunCaptureIndexCommitter.cs");

            foreach (string source in new[] { receiptSource, interfaceSource })
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
            }

            // The receipt must never re-issue a token, never re-validate the
            // whole plan, and never serialize canonical bytes itself; it must
            // require the full token validity including canonical byte
            // re-verification, so index-local-only validity is forbidden here.
            Assert.That(receiptSource, Does.Not.Contain("TryAcquireValidationToken"));
            Assert.That(receiptSource, Does.Not.Contain("AcquireValidationToken"));
            Assert.That(receiptSource, Does.Not.Contain("SerializeCanonical"));
            Assert.That(receiptSource, Does.Not.Contain("IsValidIndexLocal"));
            Assert.That(receiptSource, Does.Contain("IsValidWithToken"));
        }
    }
}
