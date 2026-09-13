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
    public class PngJsonCapturePublicationCaptureCompleteReleaseContractTests
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

        private sealed class ThrowingOnceHandle : ICaptureRunLockHandle
        {
            private int _calls;

            public ThrowingOnceHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
                if (_calls++ == 0)
                {
                    throw new InvalidOperationException("First release fails.");
                }
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
            public IPngJsonCapturePublicationArtifactPublishAttempt TryBegin(
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return new FakeAttempt();
            }

            public PngJsonCapturePublicationArtifactPublishReceipt PublishReserved(
                IPngJsonCapturePublicationArtifactPublishAttempt attempt,
                PngJsonCapturePublicationArtifactPublishOperation operation,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
            {
                return PngJsonCapturePublicationArtifactPublishReceipt.Create(this, operation, token);
            }

            public void End(IPngJsonCapturePublicationArtifactPublishAttempt attempt)
            {
            }

            private sealed class FakeAttempt : IPngJsonCapturePublicationArtifactPublishAttempt
            {
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

        private sealed class FakeCleanup : IPngJsonCapturePublicationCaptureCompleteCleanupBackend
        {
            public PngJsonCapturePublicationCaptureCompleteCleanupReceipt Execute(
                PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
            {
                return PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(this, operation, token);
            }
        }

        private sealed class FakeNotifier : IPngJsonCapturePublicationCaptureCompleteNotifier
        {
            public PngJsonCapturePublicationCaptureCompleteNotificationReceipt Notify(
                PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
            {
                return PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(this, operation);
            }
        }

        // ---- Lease ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null, bool throwingFirstHandle = false)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            ICaptureRunLockHandle first = throwingFirstHandle
                ? new ThrowingOnceHandle(pathSet.FirstLockPath)
                : (ICaptureRunLockHandle)new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
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

            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
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
            return PngJsonCaptureFrozenRunPublicationPlanBinding.Create(frozen);
        }

        private PngJsonCaptureFrozenRunArtifactInspectionSeed MakeSeed(
            long[] frameIds,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = MakeSeedBinding(frameIds, out owner);
            return PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(binding);
        }

        // ---- Recovery decision graph ----

        private static CaptureRunMarkerBinding MakeMarkerBinding(CaptureRunRootLayout layout)
        {
            return new CaptureRunMarkerBinding(
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
            bool throwingFirstHandle,
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

            CaptureRunLockLease lease = MakeLease(layout, disposeLog, throwingFirstHandle);
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
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(disposeLog, throwingFirstHandle, out owner),
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
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation =
                MakeRecoveryInspectionOperation(1000, 4, 64, disposeLog, throwingFirstHandle, out owner);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(plan ?? MakePlan(), indexAuthoritative, captureIndexTemporary, disposeLog, throwingFirstHandle, out owner));
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan = null,
            bool indexAuthoritative = false,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null)
        {
            return MakeRecoveryAuthority(plan, indexAuthoritative, captureIndexTemporary, null, false, out _);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeRecoveryAuthority(null, false, null, null, false, out owner);
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
            int entryCount,
            CaptureRunPublicationEvidenceStatus stagingStatus,
            List<string> disposeLog,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(entryCount));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(plan, false, null, disposeLog, throwingFirstHandle, out owner);
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

        private static PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator MakeOrchestrationCoordinator(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend backend = null)
        {
            return new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(
                    backend ?? new FakeCleanup()));
        }

        private static PngJsonCapturePublicationCaptureCompleteNotificationCoordinator MakeCoordinator(
            IPngJsonCapturePublicationCaptureCompleteNotifier notifier = null)
        {
            return new PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(notifier ?? new FakeNotifier());
        }

        // ---- Lifecycle evidence construction ----

        private CaptureRunInitializationOpenOutcome GetProvenanceOpenOutcome(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult)
        {
            return notificationResult.CleanupResult.Authority.RecoveryDecision.Snapshot.Operation.OpenOutcome;
        }

        private PngJsonCapturePublicationCaptureCompleteNotificationResult MakeRecoveryNotificationResult(
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunInitializationOpenOutcome openOutcome,
            List<string> disposeLog = null,
            bool throwingFirstHandle = false)
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult recovery =
                BuildCommitResult(1, EvMatchesExpected, disposeLog, throwingFirstHandle, out owner);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeOrchestrationCoordinator().Execute(recovery);
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult = MakeCoordinator().Execute(cleanup);
            openOutcome = GetProvenanceOpenOutcome(notificationResult);
            return notificationResult;
        }

        private PngJsonCapturePublicationCaptureCompleteNotificationResult MakeFreshNotificationResult(
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureEvidenceRunFreezeReceipt freezeReceipt,
            out PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed)
        {
            freshSeed = MakeSeed(new long[] { 1 }, out owner);
            freezeReceipt = freshSeed.FreezeReceipt;
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(freshSeed);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanup =
                MakeOrchestrationCoordinator().Execute(BuildCleanupResult(authority, 1, EvMatchesExpected));
            return MakeCoordinator().Execute(cleanup);
        }

        private PngJsonCapturePublicationCaptureCompleteLifecycleEvidence MakeFreshEvidence(
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureEvidenceRunFreezeReceipt freezeReceipt,
            out PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed)
        {
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult =
                MakeFreshNotificationResult(out owner, out freezeReceipt, out freshSeed);
            return PngJsonCapturePublicationCaptureCompleteLifecycleEvidence.FromFresh(
                notificationResult, freezeReceipt, owner);
        }

        private PngJsonCapturePublicationCaptureCompleteLifecycleEvidence MakeRecoveryEvidence(
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunInitializationOpenOutcome openOutcome,
            List<string> disposeLog = null,
            bool throwingFirstHandle = false)
        {
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult =
                MakeRecoveryNotificationResult(out owner, out openOutcome, disposeLog, throwingFirstHandle);
            return PngJsonCapturePublicationCaptureCompleteLifecycleEvidence.FromRecovery(
                notificationResult, openOutcome, owner);
        }

        // ---- Release construction ----

        private static PngJsonCapturePublicationCaptureCompleteReleaser MakeReleaser()
        {
            return new PngJsonCapturePublicationCaptureCompleteReleaser();
        }

        private static PngJsonCapturePublicationCaptureCompleteReleaseOperation MakeReleaseOperation(
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence)
        {
            return PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence);
        }

        // ---- Tests ----

        [Test]
        public void Operation_Shape_FourReadonlyFields_NoPublicCtor_NonDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaseOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        [Test]
        public void Operation_Create_NullEvidence_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(null));
            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_Create_ReleasedOwnerEvidence_Rejected_BeforeSideEffects()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner, out _);
            owner.Dispose();
            Assert.That(evidence.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence));
            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));
        }

        [Test]
        public void Operation_Create_SameRootDifferentOwner_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner, out _);

            CaptureRunLockLease sameRootLease = MakeLease(evidence.RootLayout);
            CaptureRunInitializationSessionOwnershipLease sameRootOwner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref sameRootLease);
            _owners.Add(sameRootOwner);

            SetField(evidence, "_ownershipLease", sameRootOwner);
            Assert.That(evidence.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence));
            Assert.That(ex.ParamName, Is.EqualTo("lifecycleEvidence"));

            Assert.That(owner.IsCreated, Is.True);
        }

        [Test]
        public void Operation_Fresh_IssuedAndForwardsExactRefs()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeFreshEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CaptureEvidenceRunFreezeReceipt freezeReceipt,
                out PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed);

            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsReleaseComplete, Is.False);
            Assert.That(ReferenceEquals(operation.LifecycleEvidence, evidence), Is.True);
            Assert.That(ReferenceEquals(operation.NotificationResult, evidence.NotificationResult), Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, owner), Is.True);
            Assert.That(ReferenceEquals(operation.LockIdentityEvidence, evidence.LockIdentityEvidence), Is.True);
        }

        [Test]
        public void Operation_Recovery_IssuedAndForwardsExactRefs()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CaptureRunInitializationOpenOutcome openOutcome);

            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsReleaseComplete, Is.False);
            Assert.That(ReferenceEquals(operation.LifecycleEvidence, evidence), Is.True);
            Assert.That(ReferenceEquals(operation.NotificationResult, evidence.NotificationResult), Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, owner), Is.True);
            Assert.That(ReferenceEquals(operation.LockIdentityEvidence, evidence.LockIdentityEvidence), Is.True);
        }

        [Test]
        public void Release_NullOperation_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => releaser.Release(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Release_FirstAttempt_SingleDispose_ReceiptValid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out _,
                disposeLog: log);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();

            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = releaser.Release(operation);

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            Assert.That(log, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
            Assert.That(operation.IsReleaseComplete, Is.True);
            Assert.That(operation.CanRelease, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        [Test]
        public void Release_AfterComplete_RejectedBeforeSideEffects()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();

            releaser.Release(operation);
            Assert.That(operation.IsReleaseComplete, Is.True);
            Assert.That(operation.CanRelease, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => releaser.Release(operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Release_PartialFailure_Propagates_Retryable_NoReceipt()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out _,
                throwingFirstHandle: true);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();

            AggregateException thrown = Assert.Throws<AggregateException>(() => releaser.Release(operation));
            Assert.That(thrown.InnerExceptions.Count, Is.EqualTo(1));
            Assert.That(thrown.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());

            Assert.That(owner.IsCreated, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsReleaseComplete, Is.False);

            // Receipt cannot be constructed before full release.
            Assert.Throws<ArgumentException>(
                () => new PngJsonCapturePublicationCaptureCompleteReleaseReceipt(releaser, operation));
        }

        [Test]
        public void Release_PartialRetry_CompletesAndReceiptValid()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out _,
                throwingFirstHandle: true);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();

            Assert.Throws<AggregateException>(() => releaser.Release(operation));

            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = releaser.Release(operation);

            Assert.That(operation.IsReleaseComplete, Is.True);
            Assert.That(operation.CanRelease, Is.False);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        [Test]
        public void Receipt_ValidAfterLifecycleEvidenceInvalid()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();

            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = releaser.Release(operation);

            Assert.That(evidence.IsValid, Is.False);
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_ForeignIssuerOrOperation_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = releaser.Release(operation);

            PngJsonCapturePublicationCaptureCompleteReleaser otherReleaser = MakeReleaser();
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence otherEvidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation otherOperation = MakeReleaseOperation(otherEvidence);

            Assert.That(receipt.IsIssuedFor(otherReleaser, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(releaser, otherOperation), Is.False);
        }

        [Test]
        public void Receipt_FieldNullSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = releaser.Release(operation);
            Assert.That(receipt.IsValid, Is.True);

            // Nulling the issuer fails closed.
            SetField(receipt, "_issuedBy", null);
            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.False);

            // Swapping the operation to a different, unreleased operation fails
            // closed.
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence otherEvidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation otherOperation = MakeReleaseOperation(otherEvidence);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation2 = MakeReleaseOperation(
                MakeRecoveryEvidence(out _, out _));
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt2 = releaser.Release(operation2);
            SetField(receipt2, "_operation", otherOperation);
            Assert.That(receipt2.IsValid, Is.False);
            Assert.That(receipt2.IsIssuedFor(releaser, operation2), Is.False);
        }

        [Test]
        public void Receipt_NotificationResultInternalsNulled_FailClosedWithoutThrow()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser releaser = MakeReleaser();
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt = releaser.Release(operation);
            Assert.That(receipt.IsValid, Is.True);

            // After release, the notification result's internal operation is
            // nulled. The forwarding getters must not throw, and the binding
            // and receipt must converge to false.
            SetField(operation.NotificationResult, "_operation", null);

            Assert.That(operation.IsIssuanceBindingIntact, Is.False);
            Assert.That(operation.CanRelease, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.False);
        }

        [Test]
        public void Operation_FieldSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);

            CaptureRunLockLease foreignLease = MakeLease(evidence.RootLayout);
            CaptureRunInitializationSessionOwnershipLease foreignOwner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref foreignLease);
            _owners.Add(foreignOwner);

            SetField(operation, "_ownershipLease", foreignOwner);
            Assert.That(operation.IsIssuanceBindingIntact, Is.False);
            Assert.That(operation.CanRelease, Is.False);
            Assert.That(operation.IsValid, Is.False);
            Assert.That(owner.IsCreated, Is.True);
        }

        [Test]
        public void Receipt_Shape_TwoFields_NoLeaseExposure_NonDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaseReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureRunInitializationSessionOwnershipLease)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureRunLockLease)));
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(CaptureRunInitializationSessionOwnershipLease)), property.Name);
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(CaptureRunLockLease)), property.Name);
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(CaptureRunInitializationSessionOwnershipLease)), method.Name);
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(CaptureRunLockLease)), method.Name);
            }

            Assert.That(type.GetProperty("OwnershipLease", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
            Assert.That(type.GetProperty("LockLease", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
        }

        [Test]
        public void Releaser_Shape_Stateless_NonDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaser);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(IPngJsonCapturePublicationCaptureCompleteReleaser).IsAssignableFrom(type), Is.True);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(0));
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaseOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/IPngJsonCapturePublicationCaptureCompleteReleaser.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaser.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaseReceipt.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = ReadSource(relativePath);

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Notifier"));
                Assert.That(source, Does.Not.Contain("CleanupBackend"));
                Assert.That(source, Does.Not.Contain("Task"));
                Assert.That(source, Does.Not.Contain("Thread"));
                Assert.That(source, Does.Not.Contain("CancellationToken"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("using System.Linq"));
                Assert.That(source, Does.Not.Contain("using UnityEngine"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("ToArray"));
                Assert.That(source, Does.Not.Contain("Array.Copy"));
            }
        }
    }
}
