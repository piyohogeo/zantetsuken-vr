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
    public class PngJsonCapturePublicationArtifactRecoveryExecutionCoordinatorResultContractTests
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

        private sealed class FakePublisher : IPngJsonCapturePublicationArtifactPublisher
        {
            private readonly List<string> _log;

            public FakePublisher(List<string> log = null)
            {
                _log = log;
            }

            public int Calls;

            public Exception ExceptionToThrow { get; set; }

            public Func<PngJsonCapturePublicationArtifactPublishOperation, PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken, PngJsonCapturePublicationArtifactPublishReceipt> ReceiptOverride { get; set; }

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

                if (ReceiptOverride != null)
                {
                    return ReceiptOverride(operation, token);
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

            public Func<PngJsonCaptureRunCaptureIndexCommitOperation, PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken, PngJsonCaptureRunCaptureIndexCommitReceipt> ReceiptOverride { get; set; }

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

                if (ReceiptOverride != null)
                {
                    return ReceiptOverride(operation, token);
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

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPublishPlan(int entryCount)
        {
            long[] frameIds = new long[entryCount];
            CaptureRunPublicationEvidenceStatus[] stagingPng = new CaptureRunPublicationEvidenceStatus[entryCount];
            CaptureRunPublicationEvidenceStatus[] stagingSidecar = new CaptureRunPublicationEvidenceStatus[entryCount];
            CaptureRunPublicationEvidenceStatus[] finalPng = new CaptureRunPublicationEvidenceStatus[entryCount];
            CaptureRunPublicationEvidenceStatus[] finalSidecar = new CaptureRunPublicationEvidenceStatus[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                frameIds[i] = i + 1;
                stagingPng[i] = EvMatchesExpected;
                stagingSidecar[i] = EvAbsent;
                finalPng[i] = EvAbsent;
                finalSidecar[i] = EvMatchesExpected;
            }

            return BuildPlan(MakeSnapshotArray(
                MakeFreshAuthority(frameIds), EvMatchesExpected, 1, stagingPng, stagingSidecar, finalPng, finalSidecar, maximumPngByteCount: 2000));
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildRecoveryCommitPlan(
            out PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            authority = MakeRecoveryAuthority();
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

        private static PngJsonCapturePublicationArtifactRecoveryExecutionBatch BuildBatch(
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan)
        {
            return PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
        }

        // ---- Forge helpers ----

        private static PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator MakeCoordinator(
            IPngJsonCapturePublicationArtifactPublisher publisher,
            IPngJsonCaptureRunCaptureIndexCommitter committer)
        {
            return new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, committer);
        }

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

        private static PngJsonCapturePublicationArtifactRecoveryCompletedStep ForgeCompletedStep(
            PngJsonCapturePublicationArtifactRecoveryCompletedStep template,
            PngJsonCapturePublicationArtifactPublishReceipt publishReceipt,
            PngJsonCaptureRunCaptureIndexCommitReceipt commitReceipt)
        {
            PngJsonCapturePublicationArtifactRecoveryCompletedStep forged =
                (PngJsonCapturePublicationArtifactRecoveryCompletedStep)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryCompletedStep));
            SetField(forged, "_preparedStep", template.PreparedStep);
            SetField(forged, "_publishReceipt", publishReceipt);
            SetField(forged, "_commitReceipt", commitReceipt);
            SetField(forged, "_token", GetField(template, "_token"));
            return forged;
        }

        private static PngJsonCapturePublicationArtifactPublishReceipt ForgePublishReceipt(
            IPngJsonCapturePublicationArtifactPublisher issuedBy,
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            PngJsonCapturePublicationArtifactPublishReceipt receipt =
                (PngJsonCapturePublicationArtifactPublishReceipt)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactPublishReceipt));
            SetField(receipt, "_issuedBy", issuedBy);
            SetField(receipt, "_operation", operation);
            SetField(receipt, "_token", token);
            return receipt;
        }

        private static PngJsonCaptureRunCaptureIndexCommitReceipt ForgeCommitReceipt(
            IPngJsonCaptureRunCaptureIndexCommitter issuedBy,
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt =
                (PngJsonCaptureRunCaptureIndexCommitReceipt)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCaptureRunCaptureIndexCommitReceipt));
            SetField(receipt, "_issuedBy", issuedBy);
            SetField(receipt, "_operation", operation);
            SetField(receipt, "_token", token);
            return receipt;
        }

        private static PngJsonCapturePublicationArtifactRecoveryCompletedStep[] WithReplaced(
            PngJsonCapturePublicationArtifactRecoveryExecutionResult result,
            int index,
            PngJsonCapturePublicationArtifactRecoveryCompletedStep replacement)
        {
            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] steps =
                new PngJsonCapturePublicationArtifactRecoveryCompletedStep[result.Count];
            for (int i = 0; i < result.Count; i++)
            {
                steps[i] = i == index ? replacement : result.GetCompletedStep(i);
            }

            return steps;
        }

        private static void AssertResultRejected(
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator,
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] completedSteps,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryExecutionResult.Create(coordinator, batch, completedSteps, token));

            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        // ---- Constructor / shape ----

        [Test]
        public void Coordinator_NullDependencies_Rejected()
        {
            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();

            ArgumentNullException pubEx = Assert.Throws<ArgumentNullException>(
                () => new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(null, committer));
            Assert.That(pubEx.ParamName, Is.EqualTo("publisher"));

            ArgumentNullException comEx = Assert.Throws<ArgumentNullException>(
                () => new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, null));
            Assert.That(comEx.ParamName, Is.EqualTo("committer"));
        }

        [Test]
        public void Coordinator_Shape_TwoReadonlyDeps_NotDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator);

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
        public void CompletedStep_Shape_FourReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryCompletedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Result_Shape_FourReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Shape_NoLeaseOwnerTokenBytesStreamHandleExposure()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator),
                typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult),
                typeof(PngJsonCapturePublicationArtifactRecoveryCompletedStep)
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
            }
        }

        // ---- Status mapping ----

        [Test]
        public void Result_StatusMapping()
        {
            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            Assert.That(coordinator.Execute(BuildBatch(BuildPublishPngSidecarPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired));
            Assert.That(coordinator.Execute(BuildBatch(BuildRecoveryCommitPlan(out _))).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired));
            Assert.That(coordinator.Execute(BuildBatch(BuildCaptureCompletePlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired));
            Assert.That(coordinator.Execute(BuildBatch(BuildOrphanedPreTracePlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace));
            Assert.That(coordinator.Execute(BuildBatch(BuildArtifactSourceMissingPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing));
            Assert.That(coordinator.Execute(BuildBatch(BuildPublishedArtifactMissingPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing));
            Assert.That(coordinator.Execute(BuildBatch(BuildRunRootCollisionPlan())).Status,
                Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision));
        }

        // ---- Execution order ----

        [Test]
        public void Execute_PublishMultiple_PlanOrderEntryOrderPngThenSidecar_OnceEach()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(log, Is.EqualTo(new[]
            {
                "publish:0:Png",
                "publish:0:Sidecar"
            }));
            Assert.That(publisher.Calls, Is.EqualTo(2));
            Assert.That(committer.Calls, Is.EqualTo(0));

            Assert.That(result.Count, Is.EqualTo(3));
            Assert.That(result.GetCompletedStep(0).PublishReceipt, Is.Not.Null);
            Assert.That(result.GetCompletedStep(0).CommitReceipt, Is.Null);
            Assert.That(result.GetCompletedStep(1).PublishReceipt, Is.Not.Null);
            Assert.That(result.GetCompletedStep(1).CommitReceipt, Is.Null);

            Assert.That(result.GetCompletedStep(2).PreparedStep.Action, Is.EqualTo(ReinspectArtifacts));
            Assert.That(result.GetCompletedStep(2).PublishReceipt, Is.Null);
            Assert.That(result.GetCompletedStep(2).CommitReceipt, Is.Null);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_Commit_CalledOnce()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(committer.Calls, Is.EqualTo(1));
            Assert.That(publisher.Calls, Is.EqualTo(0));
            Assert.That(log, Is.EqualTo(new[] { "commit:" + batch.GetStep(0).CaptureIndexCommitOperation.Mode }));
            Assert.That(result.Count, Is.EqualTo(1));
            Assert.That(result.GetCompletedStep(0).CommitReceipt, Is.Not.Null);
            Assert.That(result.GetCompletedStep(0).PublishReceipt, Is.Null);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_RoutingStop_NoBackendCalls()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan[] plans =
            {
                BuildOrphanedPreTracePlan(),
                BuildCaptureCompletePlan(),
                BuildArtifactSourceMissingPlan(),
                BuildPublishedArtifactMissingPlan(),
                BuildRunRootCollisionPlan()
            };

            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log);
            FakeCommitter committer = new FakeCommitter(log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            foreach (PngJsonCapturePublicationArtifactRecoveryActionPlan plan in plans)
            {
                PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(BuildBatch(plan));
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(result.GetCompletedStep(0).PublishReceipt, Is.Null);
                Assert.That(result.GetCompletedStep(0).CommitReceipt, Is.Null);
                Assert.That(result.IsValid, Is.True);
            }

            Assert.That(log, Is.Empty, "Routing and stop dispositions must never contact a backend.");
        }

        // ---- Receipt violations ----

        [Test]
        public void Execute_Publish_NullReceipt_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher publisher = new FakePublisher { ReceiptOverride = (_, _) => null };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Publish_ForeignIssuer_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher foreign = new FakePublisher();
            FakePublisher publisher = new FakePublisher
            {
                ReceiptOverride = (op, token) => PngJsonCapturePublicationArtifactPublishReceipt.Create(foreign, op, token)
            };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Publish_DifferentOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakePublisher publisher = new FakePublisher();
            PngJsonCapturePublicationArtifactPublishOperation wrongOperation = batch.GetStep(1).PublishOperation;
            publisher.ReceiptOverride = (op, token) => PngJsonCapturePublicationArtifactPublishReceipt.Create(publisher, wrongOperation, token);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Commit_NullReceipt_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakeCommitter committer = new FakeCommitter { ReceiptOverride = (_, _) => null };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Commit_ForeignIssuer_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            FakeCommitter foreign = new FakeCommitter();
            FakeCommitter committer = new FakeCommitter
            {
                ReceiptOverride = (op, token) => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(foreign, op, token)
            };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Commit_DifferentOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            PngJsonCapturePublicationArtifactRecoveryActionPlan otherPlan = BuildRecoveryCommitPlan(out _);
            PngJsonCaptureRunCaptureIndexCommitOperation wrongOperation = BuildBatch(otherPlan).GetStep(0).CaptureIndexCommitOperation;
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken otherToken = otherPlan.AcquireValidationToken();
            FakeCommitter committer = new FakeCommitter();
            committer.ReceiptOverride = (op, token) => PngJsonCaptureRunCaptureIndexCommitReceipt.Create(committer, wrongOperation, otherToken);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        // ---- Exception propagation / no retry / no rollback ----

        [Test]
        public void Execute_PublisherException_PropagatesIdentical_NoRetry_NoSubsequentSteps()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            IOException exception = new IOException("publish failed");
            List<string> log = new List<string>();
            FakePublisher publisher = new FakePublisher(log) { ExceptionToThrow = exception };
            FakeCommitter committer = new FakeCommitter(log);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(log, Is.EqualTo(new[] { "publish:0:Png" }), "No retry and no subsequent steps after an exception.");
        }

        [Test]
        public void Execute_CommitterException_PropagatesIdentical()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            IOException exception = new IOException("commit failed");
            FakeCommitter committer = new FakeCommitter { ExceptionToThrow = exception };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));
            Assert.That(ex, Is.SameAs(exception));
        }

        [Test]
        public void Execute_PartialFailure_NoOwnerDispose()
        {
            List<string> disposeLog = new List<string>();
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan publishPlan = BuildPlan(MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected));
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(publishPlan);

            IOException exception = new IOException("boom");
            FakePublisher publisher = new FakePublisher { ExceptionToThrow = exception };
            FakeCommitter committer = new FakeCommitter();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(publisher.Calls, Is.EqualTo(1), "The coordinator must not contact the publisher again after the failure.");
            Assert.That(committer.Calls, Is.EqualTo(0), "The coordinator must not contact the committer after the publisher failure.");
            Assert.That(owner.IsCreated, Is.True, "The coordinator must not dispose the owner on failure.");
            Assert.That(disposeLog, Is.Empty, "The coordinator must not dispose the owner on failure.");
        }

        [Test]
        public void Execute_PartialFailure_InputGraphUnchanged()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCaptureRunCaptureIndexCommitOperation commitOperation = batch.GetStep(0).CaptureIndexCommitOperation;
            byte[] before = commitOperation.GetCanonicalBytes();

            FakeCommitter committer = new FakeCommitter { ExceptionToThrow = new IOException("boom") };
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);

            Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(plan.IsValid, Is.True);
            Assert.That(commitOperation.IsValid, Is.True);
            Assert.That(commitOperation.GetCanonicalBytes(), Is.EqualTo(before));
        }

        // ---- Invalid batch rejection ----

        [Test]
        public void Execute_NullBatch_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("batch"));
        }

        [Test]
        public void Execute_InvalidBatch_Rejected_NoBackendCalls()
        {
            List<string> log = new List<string>();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakePublisher(log), new FakeCommitter(log));

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch =
                (PngJsonCapturePublicationArtifactRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(batch));
            Assert.That(ex.ParamName, Is.EqualTo("batch"));
            Assert.That(log, Is.Empty);
        }

        // ---- Result correlation ----

        [Test]
        public void Result_CompletedSteps_CountOrderPreparedStepReference()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());

            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.Count, Is.EqualTo(batch.Count));
            Assert.That(result.Batch, Is.SameAs(batch));
            Assert.That(result.IssuedBy, Is.SameAs(coordinator));
            Assert.That(result.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(result.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(InitId));

            for (int i = 0; i < batch.Count; i++)
            {
                PngJsonCapturePublicationArtifactRecoveryCompletedStep completed = result.GetCompletedStep(i);
                Assert.That(completed.PreparedStep, Is.SameAs(batch.GetStep(i)));
                if (completed.PreparedStep.Action == PublishArtifact)
                {
                    Assert.That(completed.PublishReceipt.Operation, Is.SameAs(completed.PreparedStep.PublishOperation));
                }
                else
                {
                    Assert.That(completed.PublishReceipt, Is.Null);
                    Assert.That(completed.CommitReceipt, Is.Null);
                }
            }

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_ArrayDefensiveCopy_NotExposed()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());

            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(
                typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult)
                    .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(PngJsonCapturePublicationArtifactRecoveryCompletedStep[])),
                Is.False,
                "The completed-step array must not be exposed.");
        }

        [Test]
        public void Result_DirectConstructor_MissingExtraSwappedForeign_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            batch.TryValidate(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            PngJsonCapturePublicationArtifactRecoveryCompletedStep step0 = good.GetCompletedStep(0);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep step1 = good.GetCompletedStep(1);

            AssertResultRejected(coordinator, batch, new[] { step0 }, token);
            AssertResultRejected(coordinator, batch, new[] { step0, step1, step0 }, token);
            AssertResultRejected(coordinator, batch, new[] { step1, step0 }, token);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch otherBatch = BuildBatch(BuildPublishPngSidecarPlan());
            PngJsonCapturePublicationArtifactRecoveryCompletedStep foreign = coordinator.Execute(otherBatch).GetCompletedStep(0);
            AssertResultRejected(coordinator, batch, new[] { foreign, step1 }, token);
        }

        [Test]
        public void Result_IsValid_False_ForBrokenValues()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult nullSteps =
                (PngJsonCapturePublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult));
            SetField(nullSteps, "_issuedBy", coordinator);
            SetField(nullSteps, "_batch", batch);
            SetField(nullSteps, "_completedSteps", null);
            SetField(nullSteps, "_token", GetField(result, "_token"));
            Assert.That(nullSteps.IsValid, Is.False);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult nullElement =
                (PngJsonCapturePublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult));
            SetField(nullElement, "_issuedBy", coordinator);
            SetField(nullElement, "_batch", batch);
            SetField(nullElement, "_completedSteps", new PngJsonCapturePublicationArtifactRecoveryCompletedStep[] { null, result.GetCompletedStep(1) });
            SetField(nullElement, "_token", GetField(result, "_token"));
            Assert.That(nullElement.IsValid, Is.False);
        }

        [Test]
        public void Result_ForeignIssuer_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            FakePublisher publisher = new FakePublisher();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            batch.TryValidate(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakePublisher foreign = new FakePublisher();
            PngJsonCapturePublicationArtifactPublishReceipt foreignReceipt =
                ForgePublishReceipt(foreign, original.PublishReceipt.Operation, token);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep forged = ForgeCompletedStep(original, foreignReceipt, null);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult broken =
                PngJsonCapturePublicationArtifactRecoveryExecutionResult.Create(
                    coordinator, batch, WithReplaced(good, 0, forged), token);

            Assert.That(broken.IsValid, Is.False);
        }

        [Test]
        public void Result_ForeignCommitterIssuer_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            FakeCommitter committer = new FakeCommitter();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), committer);
            PngJsonCapturePublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            batch.TryValidate(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakeCommitter foreign = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt foreignReceipt =
                ForgeCommitReceipt(foreign, original.CommitReceipt.Operation, token);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep forged = ForgeCompletedStep(original, null, foreignReceipt);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult broken =
                PngJsonCapturePublicationArtifactRecoveryExecutionResult.Create(
                    coordinator, batch, WithReplaced(good, 0, forged), token);

            Assert.That(broken.IsValid, Is.False);
        }

        // ---- Completed step factory defense ----

        [Test]
        public void CompletedStep_Factory_ReceiptKindOperationActionMismatch_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan publishPlan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch publishBatch = BuildBatch(publishPlan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken publishToken = publishPlan.AcquireValidationToken();

            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();

            PngJsonCapturePublicationArtifactRecoveryPreparedStep publishStep = publishBatch.GetStep(0);
            PngJsonCapturePublicationArtifactPublishReceipt publishReceipt =
                PngJsonCapturePublicationArtifactPublishReceipt.Create(publisher, publishStep.PublishOperation, publishToken);

            // A publish step must not hold a commit receipt.
            PngJsonCapturePublicationArtifactRecoveryActionPlan strayCommitPlan = BuildRecoveryCommitPlan(out _);
            PngJsonCaptureRunCaptureIndexCommitReceipt strayCommitReceipt = committer.Commit(
                BuildBatch(strayCommitPlan).GetStep(0).CaptureIndexCommitOperation,
                strayCommitPlan.AcquireValidationToken());
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    publishStep, publishToken, publisher, committer, publishReceipt, strayCommitReceipt));

            // A routing step must not hold any receipt.
            PngJsonCapturePublicationArtifactRecoveryPreparedStep reinspectStep = publishBatch.GetStep(2);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    reinspectStep, publishToken, publisher, committer, publishReceipt, null));

            // A commit step must not hold a publish receipt.
            PngJsonCapturePublicationArtifactRecoveryActionPlan commitPlan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch commitBatch = BuildBatch(commitPlan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken commitToken = commitPlan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep commitStep = commitBatch.GetStep(0);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    commitStep, commitToken, publisher, committer, publishReceipt, null));
        }

        [Test]
        public void CompletedStep_Factory_NullIssuerReceipt_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(0);

            PngJsonCapturePublicationArtifactPublishReceipt brokenReceipt =
                ForgePublishReceipt(null, prepared.PublishOperation, token);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    prepared, token, new FakePublisher(), new FakeCommitter(), brokenReceipt, null));
        }

        [Test]
        public void CompletedStep_Factory_CrossToken_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryActionPlan other = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(0);
            FakePublisher publisher = new FakePublisher();
            PngJsonCapturePublicationArtifactPublishReceipt receipt =
                PngJsonCapturePublicationArtifactPublishReceipt.Create(publisher, prepared.PublishOperation, plan.AcquireValidationToken());

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    prepared, other.AcquireValidationToken(), publisher, new FakeCommitter(), receipt, null));
            Assert.That(ex.ParamName, Is.EqualTo("preparedStep"));
        }

        [Test]
        public void CompletedStep_Factory_CorruptedCanonicalBytes_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(0);
            PngJsonCaptureRunCaptureIndexCommitOperation operation = prepared.CaptureIndexCommitOperation;

            FakeCommitter committer = new FakeCommitter();
            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);

            byte[] tampered = operation.GetCanonicalBytes();
            tampered[0] = (byte)(tampered[0] ^ 0xFF);
            SetField(operation, "_canonicalBytes", tampered);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    prepared, token, new FakePublisher(), committer, null, receipt));
        }

        [Test]
        public void Result_IsValid_False_ForCorruptedCommitBytes()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildRecoveryCommitPlan(out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep completed = result.GetCompletedStep(0);

            Assert.That(result.IsValid, Is.True);

            SetField(completed.CommitReceipt.Operation, "_canonicalBytes", new byte[] { 9, 9, 9 });

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void CompletedStep_Factory_OutOfRangeStepIndex_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactRecoveryPreparedStep prepared = batch.GetStep(0);
            FakePublisher publisher = new FakePublisher();
            PngJsonCapturePublicationArtifactPublishReceipt receipt =
                PngJsonCapturePublicationArtifactPublishReceipt.Create(publisher, prepared.PublishOperation, token);

            PngJsonCapturePublicationArtifactRecoveryPreparedStep forged = ForgePreparedStep(plan, int.MaxValue, prepared.PublishOperation, null);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    forged, token, publisher, new FakeCommitter(), receipt, null));
        }

        [Test]
        public void CompletedStep_Factory_SwappedPublishOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();
            PngJsonCapturePublicationArtifactPublishOperation sidecarOperation = batch.GetStep(1).PublishOperation;
            FakePublisher publisher = new FakePublisher();
            PngJsonCapturePublicationArtifactPublishReceipt receipt =
                PngJsonCapturePublicationArtifactPublishReceipt.Create(publisher, sidecarOperation, token);

            PngJsonCapturePublicationArtifactRecoveryPreparedStep forged = ForgePreparedStep(plan, 0, sidecarOperation, null);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryCompletedStep.CreateIndexLocal(
                    forged, token, publisher, new FakeCommitter(), receipt, null));
        }

        [Test]
        public void CompletedStep_IsValidIndexLocal_False_ForForgedOutOfRangePreparedStep()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            PngJsonCapturePublicationArtifactRecoveryPreparedStep forgedPrepared =
                ForgePreparedStep(plan, int.MaxValue, original.PublishReceipt.Operation, null);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep forgedCompleted = ForgeCompletedStep(original, original.PublishReceipt, null);
            SetField(forgedCompleted, "_preparedStep", forgedPrepared);

            Assert.That(forgedCompleted.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void CompletedStep_IsValidIndexLocal_StaleToken_NullSteps_False()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep completed = result.GetCompletedStep(0);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            SetField(plan, "_steps", null);

            Assert.That(completed.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void ForgedBrokenReceipt_ResultIsValidFalse_WithoutException()
        {
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPngSidecarPlan();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult good = coordinator.Execute(batch);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep original = good.GetCompletedStep(0);
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            PngJsonCapturePublicationArtifactPublishReceipt brokenReceipt =
                ForgePublishReceipt(null, original.PublishReceipt.Operation, token);
            PngJsonCapturePublicationArtifactRecoveryCompletedStep brokenStep = ForgeCompletedStep(original, brokenReceipt, null);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult brokenResult =
                (PngJsonCapturePublicationArtifactRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryExecutionResult));
            SetField(brokenResult, "_issuedBy", coordinator);
            SetField(brokenResult, "_batch", batch);
            SetField(brokenResult, "_completedSteps", WithReplaced(good, 0, brokenStep));
            SetField(brokenResult, "_token", GetField(good, "_token"));
            Assert.That(brokenResult.IsValid, Is.False);
        }

        [Test]
        public void Result_OwnerExpired_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPlan(MakeSnapshotSingle(
                authority, EvMatchesExpected, 1, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent));
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakePublisher(), new FakeCommitter());
            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(result.IsValid, Is.False);
        }

        // ---- Linearity ----

        [Test]
        public void Execute_LargePublishBatch_LinearExecution()
        {
            const int count = 1000;
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan = BuildPublishPlan(count);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(plan);

            Assert.That(plan.Count, Is.EqualTo(count + 1));

            FakePublisher publisher = new FakePublisher();
            FakeCommitter committer = new FakeCommitter();
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator coordinator = MakeCoordinator(publisher, committer);

            PngJsonCapturePublicationArtifactRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(publisher.Calls, Is.EqualTo(count));
            Assert.That(committer.Calls, Is.EqualTo(0));
            Assert.That(result.Count, Is.EqualTo(count + 1));
            Assert.That(result.GetCompletedStep(0).PublishReceipt, Is.Not.Null);
            Assert.That(result.GetCompletedStep(count - 1).PublishReceipt, Is.Not.Null);
            Assert.That(result.GetCompletedStep(count).PreparedStep.Action, Is.EqualTo(ReinspectArtifacts));
            Assert.That(result.GetCompletedStep(count).PublishReceipt, Is.Null);
            Assert.That(result.IsValid, Is.True);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryCompletedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionResult.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator.cs"
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
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }
        }

        [Test]
        public void Source_LoopNoFullValidationNoSerialize()
        {
            string coordinatorSource = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator.cs");

            int loopIndex = coordinatorSource.IndexOf("for (int i = 0; i < count; i++)", StringComparison.Ordinal);
            Assert.That(loopIndex, Is.GreaterThan(0));

            int resultIndex = coordinatorSource.IndexOf("return PngJsonCapturePublicationArtifactRecoveryExecutionResult.Create", StringComparison.Ordinal);
            Assert.That(resultIndex, Is.GreaterThan(loopIndex));

            string loopBody = coordinatorSource.Substring(loopIndex, resultIndex - loopIndex);
            Assert.That(loopBody, Does.Not.Contain("batch.TryValidate"));
            Assert.That(loopBody, Does.Not.Contain("TryAcquireValidationToken"));
            Assert.That(loopBody, Does.Not.Contain("AcquireValidationToken"));
            Assert.That(loopBody, Does.Not.Contain("SerializeCanonical"));
            Assert.That(loopBody, Does.Not.Contain("GetCanonicalBytes"));
            Assert.That(loopBody, Does.Contain("IsValidIndexLocal"));
        }
    }
}
