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
    public class PngJsonCapturePublicationCaptureCompleteCleanupExecutionContractTests
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

        /// <summary>
        /// Records the step index of every backend contact and supports
        /// throwing an exact exception at a specific call index or returning a
        /// caller-supplied receipt instead of the canonical one.
        /// </summary>
        private sealed class RecordingBackend : IPngJsonCapturePublicationCaptureCompleteCleanupBackend
        {
            public readonly List<int> Calls = new List<int>();

            public int ThrowAt = -1;

            public Exception ToThrow;

            public Func<
                PngJsonCapturePublicationCaptureCompleteCleanupOperation,
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken,
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt> Override;

            public PngJsonCapturePublicationCaptureCompleteCleanupReceipt Execute(
                PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
            {
                int call = Calls.Count;
                Calls.Add(operation.StepIndex);

                if (call == ThrowAt && ToThrow != null)
                {
                    throw ToThrow;
                }

                if (Override != null)
                {
                    return Override(operation, token);
                }

                return PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(this, operation, token);
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
        public void CompletedStep_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Coordinator_Shape_OneReadonlyField_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(IPngJsonCapturePublicationCaptureCompleteCleanupBackend)));
        }

        [Test]
        public void Result_Shape_FourReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Result_ValidationToken_Shape_FiveReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(5));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Shape_NoLeaseStreamBytesProofExposure()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep),
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator),
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult),
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(Stream)
                        || field.FieldType == typeof(byte[])
                        || field.FieldType == typeof(PngJsonCapturePublicationArtifactInspectionPathSet),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, stream, bytes, or path set.");
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
        public void Execute_FullActionTable_BackendCallOrderAndReceipts()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            // DeletePublicationPlanTemporary, DeleteCaptureIndexTemporary,
            // DeleteStagingArtifact(Png), DeleteStagingArtifact(Sidecar),
            // RemoveStagingFramesRoot, DeletePublicationPlan,
            // DeleteStagingReadyMarker, DeleteStagingInitializationMarker,
            // RemoveStagingRunRoot, CaptureCompleteReady.
            Assert.That(batch.Count, Is.EqualTo(10));

            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.Count, Is.EqualTo(10));
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));

            // Exactly one backend contact per side-effecting step, ascending.
            Assert.That(backend.Calls.Count, Is.EqualTo(9));
            for (int i = 0; i < backend.Calls.Count; i++)
            {
                Assert.That(backend.Calls[i], Is.EqualTo(i));
            }

            for (int i = 0; i < result.Count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed = result.GetCompletedStep(i);
                Assert.That(completed.StepIndex, Is.EqualTo(i));
                Assert.That(ReferenceEquals(completed.PreparedStep, batch.GetStep(i)), Is.True);

                if (completed.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    Assert.That(completed.CleanupReceipt, Is.Null);
                }
                else
                {
                    Assert.That(completed.CleanupReceipt, Is.Not.Null);
                    Assert.That(ReferenceEquals(completed.CleanupReceipt.IssuedBy, backend), Is.True);
                    Assert.That(
                        ReferenceEquals(completed.CleanupReceipt.Operation, completed.PreparedStep.CleanupOperation),
                        Is.True);
                }
            }

            Assert.That(result.GetCompletedStep(9).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_BackendException_PropagatesIdenticallyAndStops()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            RecordingBackend backend = new RecordingBackend();
            backend.ThrowAt = 2;
            backend.ToThrow = new InvalidOperationException("boom");
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            InvalidOperationException thrown =
                Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));

            Assert.That(thrown, Is.SameAs(backend.ToThrow));
            // Steps 0 and 1 were contacted before the failure; step 2 was
            // contacted and threw; steps 3+ were never contacted.
            Assert.That(backend.Calls.Count, Is.EqualTo(3));
            Assert.That(backend.Calls[0], Is.EqualTo(0));
            Assert.That(backend.Calls[1], Is.EqualTo(1));
            Assert.That(backend.Calls[2], Is.EqualTo(2));
        }

        [Test]
        public void Execute_NullReceipt_InvalidOperationException()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            RecordingBackend backend = new RecordingBackend();
            backend.Override = (operation, token) => null;
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ForeignIssuerReceipt_InvalidOperationException()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);

            FakeCleanup foreignBackend = new FakeCleanup();
            RecordingBackend backend = new RecordingBackend();
            backend.Override = (operation, token) =>
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(foreignBackend, operation, token);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void Execute_OtherOperationReceipt_InvalidOperationException()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupOperation otherOperation =
                batch.GetStep(1).CleanupOperation;
            RecordingBackend backend = new RecordingBackend();
            backend.Override = (operation, calledToken) =>
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(backend, otherOperation, calledToken);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void Execute_OtherTokenReceipt_InvalidOperationException()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken reissued;
            Assert.That(plan.TryValidate(out reissued), Is.True);

            RecordingBackend backend = new RecordingBackend();
            backend.Override = (operation, calledToken) =>
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(backend, operation, reissued);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void Result_Forwarders()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Batch, batch), Is.True);
            Assert.That(ReferenceEquals(result.ActionPlan, plan), Is.True);
            Assert.That(ReferenceEquals(result.OrchestrationResult, plan.OrchestrationResult), Is.True);
            Assert.That(ReferenceEquals(result.Authority, plan.Authority), Is.True);
            Assert.That(result.AuthorityKind, Is.EqualTo(plan.AuthorityKind));
            Assert.That(ReferenceEquals(result.AuthoritativePlan, plan.AuthoritativePlan), Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, plan.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockIdentityEvidence, plan.LockIdentityEvidence), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(plan.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(plan.RunInitializationId));
            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady));
            Assert.That(result.Count, Is.EqualTo(batch.Count));

            Assert.Throws<ArgumentOutOfRangeException>(() => result.GetCompletedStep(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => result.GetCompletedStep(result.Count));
        }

        [Test]
        public void Result_DefensiveCopy_CallerArrayMutationDoesNotAffect()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);

            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] steps =
                new PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                    prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady
                        ? null
                        : backend.Execute(prepared.CleanupOperation, token);
                steps[i] = PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(
                    backend, prepared, receipt, token);
            }

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result =
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.Create(coordinator, batch, steps, token);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] internalArray =
                (PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[])GetField(result, "_completedSteps");
            Assert.That(ReferenceEquals(internalArray, steps), Is.False);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep original = steps[0];
            steps[0] = null;

            Assert.That(result.GetCompletedStep(0), Is.SameAs(original));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_Create_ForeignBackendReceipt_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);

            FakeCleanup backend = new FakeCleanup();
            FakeCleanup foreignBackend = new FakeCleanup();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] steps =
                new PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(i);
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt =
                    prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady
                        ? null
                        : foreignBackend.Execute(prepared.CleanupOperation, token);
                steps[i] = PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(
                    foreignBackend, prepared, receipt, token);
            }

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.Create(coordinator, batch, steps, token));
        }

        [Test]
        public void Result_TryValidate_TokenIssuedFor_IsValidWithToken()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token), Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);
            Assert.That(result.IsValidWithToken(token), Is.True);
            Assert.That(token.IsIssuedFor(null), Is.False);
        }

        [Test]
        public void Result_IsValidWithToken_ForeignToken_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch otherBatch = BuildBatch(plan);
            RecordingBackend otherBackend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator otherCoordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(otherBackend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult other = otherCoordinator.Execute(otherBatch);

            Assert.That(other.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken foreign), Is.True);
            Assert.That(result.IsValidWithToken(foreign), Is.False);
            Assert.That(result.IsValidWithToken(null), Is.False);
        }

        [Test]
        public void OwnerExpiry_InvalidatesCompletedStepResultAndToken()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionOperation inspection = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken inspectionToken =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(inspection);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(inspectionToken, inspection, 0, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected, EvMatchesExpected)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(inspection, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result0 =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(inspection);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result0);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed = result.GetCompletedStep(0);
            Assert.That(result.IsValid, Is.True);
            Assert.That(completed.IsValid, Is.True);
            Assert.That(result.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token), Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);
            Assert.That(result.IsValidWithToken(token), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(completed.IsValid, Is.False);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.IsValidWithToken(token), Is.False);
        }

        [Test]
        public void CompletedStep_IsValidIndexLocal_ReissuedToken_False()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);

            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(prepared.CleanupOperation, token);
            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed =
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, prepared, receipt, token);
            Assert.That(completed.IsValidIndexLocal(token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken reissued;
            Assert.That(plan.TryValidate(out reissued), Is.True);

            // A separately re-issued token for the same plan must be rejected
            // even though both tokens bind to the plan.
            Assert.That(completed.IsValidIndexLocal(reissued), Is.False);
            Assert.That(completed.IsValidIndexLocal(null), Is.False);
        }

        [Test]
        public void CompletedStep_Create_NullAndForeign_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan other = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken foreign;
            Assert.That(other.TryValidate(out foreign), Is.True);

            FakeCleanup backend = new FakeCleanup();
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep otherPrepared = BuildBatch(other).GetStep(0);
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(prepared.CleanupOperation, token);

            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, null, receipt, token)).ParamName,
                Is.EqualTo("preparedStep"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, prepared, receipt, null)).ParamName,
                Is.EqualTo("token"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(null, prepared, receipt, token)).ParamName,
                Is.EqualTo("backend"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, prepared, null, token)).ParamName,
                Is.EqualTo("cleanupReceipt"));
            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, prepared, receipt, foreign));
            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, otherPrepared, receipt, token));
        }

        [Test]
        public void CompletedStep_CaptureCompleteReady_ReceiptNullRequired()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);

            FakeCleanup backend = new FakeCleanup();
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep ready = batch.GetStep(9);
            Assert.That(ready.Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed =
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, ready, null, token);
            Assert.That(completed.CleanupReceipt, Is.Null);
            Assert.That(completed.IsValidIndexLocal(token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupReceipt forgedReceipt =
                PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(backend, batch.GetStep(0).CleanupOperation, token);
            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, ready, forgedReceipt, token));
        }

        [Test]
        public void CompletedStep_Create_CorruptedPreparedStep_RejectedWithArgumentException()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(batch.TryValidate(out token), Is.True);

            FakeCleanup backend = new FakeCleanup();
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = batch.GetStep(0);
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = backend.Execute(prepared.CleanupOperation, token);

            // Corrupt the prepared step's action plan: reading its Action getter
            // would leak a NullReferenceException, but the factory must reject
            // with ArgumentException before reading the action.
            SetField(prepared, "_actionPlan", null);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.CreateIndexLocal(backend, prepared, receipt, token));
        }

        [Test]
        public void Result_CompletedStepsArrayCorruption_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildTemporaryDocumentsPlan();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);
            Assert.That(result.IsValid, Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[] steps =
                (PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep[])GetField(result, "_completedSteps");

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep tmp = steps[0];
            steps[0] = steps[1];
            steps[1] = tmp;
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_TokenSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token), Is.True);
            Assert.That(token.IsIssuedFor(result), Is.True);
            Assert.That(result.IsValidWithToken(token), Is.True);

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken reissued;
            Assert.That(plan.TryValidate(out reissued), Is.True);
            SetField(result, "_actionPlanToken", reissued);

            Assert.That(result.IsValid, Is.False);
            Assert.That(token.IsIssuedFor(result), Is.False);
            Assert.That(result.IsValidWithToken(token), Is.False);
        }

        [Test]
        public void ReceiptTokenNull_InvalidatesCompletedStepResultAndToken()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed = result.GetCompletedStep(0);
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = completed.CleanupReceipt;
            Assert.That(receipt, Is.Not.Null);

            Assert.That(result.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token), Is.True);
            Assert.That(completed.IsValid, Is.True);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.IsValidWithToken(token), Is.True);

            // Null the receipt's held token: the completed step, the result, and
            // the already-issued result token must all fail closed.
            SetField(receipt, "_token", null);

            Assert.That(completed.IsValid, Is.False);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.IsValidWithToken(token), Is.False);
        }

        [Test]
        public void ReceiptReissuedTokenSwap_InvalidatesCompletedStepResultAndToken()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep completed = result.GetCompletedStep(0);
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt receipt = completed.CleanupReceipt;
            Assert.That(receipt, Is.Not.Null);

            Assert.That(result.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token), Is.True);
            Assert.That(completed.IsValid, Is.True);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.IsValidWithToken(token), Is.True);

            // Swap the receipt's held token for a separately re-issued token for
            // the same plan: the completed step, the result, and the
            // already-issued result token must all fail closed.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken reissued;
            Assert.That(plan.TryValidate(out reissued), Is.True);
            SetField(receipt, "_token", reissued);

            Assert.That(completed.IsValid, Is.False);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.IsValidWithToken(token), Is.False);
        }

        [Test]
        public void ActionPlan_IsValidWithToken_StepArraySubstitution_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token;
            Assert.That(plan.TryValidate(out token), Is.True);
            Assert.That(plan.IsValidWithToken(token), Is.True);

            // Replace the step array with a same-length array of same-valued but
            // distinct step instances. The plan's own structural re-validation
            // still succeeds, but the old token's issued step proofs must reject
            // the substitution.
            CaptureRunPublicationCaptureCompleteCleanupStep[] current =
                (CaptureRunPublicationCaptureCompleteCleanupStep[])GetField(plan, "_steps");
            CaptureRunPublicationCaptureCompleteCleanupStep[] cloned =
                new CaptureRunPublicationCaptureCompleteCleanupStep[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                cloned[i] = new CaptureRunPublicationCaptureCompleteCleanupStep(
                    current[i].Action, current[i].EntryIndex, current[i].ArtifactKind);
            }

            SetField(plan, "_steps", cloned);

            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.IsValidWithToken(token), Is.False);
        }

        [Test]
        public void LargeBatch_Execute_AscendingOrder_OncePerStep()
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
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result0 =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result0);

            PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = BuildBatch(plan);
            Assert.That(batch.Count, Is.EqualTo(2006));

            RecordingBackend backend = new RecordingBackend();
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator coordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(backend);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult result = coordinator.Execute(batch);

            // 2000 DeleteStagingArtifact + RemoveStagingFramesRoot +
            // DeletePublicationPlan + 3 marker/root steps = 2005 operations.
            Assert.That(backend.Calls.Count, Is.EqualTo(2005));
            for (int i = 0; i < backend.Calls.Count; i++)
            {
                Assert.That(backend.Calls[i], Is.EqualTo(i));
            }

            Assert.That(result.Count, Is.EqualTo(2006));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Source_Coordinator_BatchTryValidateOnceOutsideLoop()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator.cs");

            int first = source.IndexOf("batch.TryValidate(", StringComparison.Ordinal);
            Assert.That(first, Is.GreaterThan(0));
            int last = source.LastIndexOf("batch.TryValidate(", StringComparison.Ordinal);
            Assert.That(last, Is.EqualTo(first));

            int loopIndex = source.IndexOf("for (int i = 0; i < count; i++)", StringComparison.Ordinal);
            Assert.That(loopIndex, Is.GreaterThan(first));
        }

        [Test]
        public void Source_Result_NoTokenReissuance()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.cs");

            Assert.That(source, Does.Not.Contain("batch.TryValidate"));
            Assert.That(source, Does.Not.Contain("_actionPlan.TryValidate"));
            Assert.That(source, Does.Not.Contain("ActionPlan.TryValidate"));
            Assert.That(source, Does.Contain("actionPlan.IsValidWithToken(token)"));
            Assert.That(source, Does.Not.Contain("actionPlan.IsValid)"));
        }

        [Test]
        public void Source_CompletedStep_UsesIndexLocalOnly()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.cs");

            int createIndex = source.IndexOf("internal static PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep CreateIndexLocal(", StringComparison.Ordinal);
            Assert.That(createIndex, Is.GreaterThan(0));
            int returnIndex = source.IndexOf("return new PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep(", StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(createIndex));
            string createBody = source.Substring(createIndex, returnIndex - createIndex);

            Assert.That(createBody, Does.Contain("preparedStep.IsValidIndexLocal(token)"));
            Assert.That(createBody, Does.Contain("cleanupReceipt.IsIssuedFor("));
            Assert.That(createBody, Does.Not.Contain("TryValidate"));
        }

        [Test]
        public void Source_CompletedStep_IsValidUsesTokenGatedPlanValidation()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.cs");

            Assert.That(source, Does.Contain("actionPlan.IsValidWithToken(_token)"));
            Assert.That(source, Does.Not.Contain("actionPlan.IsValid)"));
        }

        [Test]
        public void Source_ActionPlan_IsValidWithToken_NoTokenReissuance()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.cs");

            int sig = source.IndexOf("internal bool IsValidWithToken(ValidationToken token)", StringComparison.Ordinal);
            Assert.That(sig, Is.GreaterThan(0));
            int brace = source.IndexOf('{', sig);
            Assert.That(brace, Is.GreaterThan(sig));
            int next = source.IndexOf("private bool VerifyStructure(", sig, StringComparison.Ordinal);
            Assert.That(next, Is.GreaterThan(brace));
            string body = source.Substring(brace, next - brace);

            Assert.That(body, Does.Contain("IsTokenBound(token)"));
            Assert.That(body, Does.Contain("IsStepIdentityAt(token, i)"));
            Assert.That(body, Does.Contain("VerifyStructure()"));
            Assert.That(body, Does.Not.Contain("return IsValid;"));
            Assert.That(body, Does.Not.Contain("TryValidate("));
            Assert.That(body, Does.Not.Contain("TryAcquire"));
        }

        [Test]
        public void Source_NoFilesystemOrPlanValidation()
        {
            foreach (string path in new[]
            {
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.cs"
            })
            {
                string source = ReadSource(path);

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("Task"));
                Assert.That(source, Does.Not.Contain(".Dispose()"));
            }
        }
    }
}
