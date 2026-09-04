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
    public class PngJsonCapturePublicationCaptureCompleteNotificationCoordinatorContractTests
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

        /// <summary>
        /// Records notify calls and supports throwing an exact exception or
        /// returning a caller-supplied receipt.
        /// </summary>
        private sealed class RecordingNotifier : IPngJsonCapturePublicationCaptureCompleteNotifier
        {
            public int Calls;

            public Exception ToThrow;

            public Func<
                PngJsonCapturePublicationCaptureCompleteNotificationOperation,
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt> Override;

            public PngJsonCapturePublicationCaptureCompleteNotificationReceipt Notify(
                PngJsonCapturePublicationCaptureCompleteNotificationOperation operation)
            {
                Calls++;
                if (ToThrow != null)
                {
                    throw ToThrow;
                }

                if (Override != null)
                {
                    return Override(operation);
                }

                return PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(this, operation);
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

        private static PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator MakeOrchestrationCoordinator(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend backend = null)
        {
            return new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(
                    backend ?? new FakeCleanup()));
        }

        private PngJsonCapturePublicationCaptureCompleteNotificationOperation BuildNotification(
            bool commitRoute,
            out PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            cleanupResult = MakeOrchestrationCoordinator().Execute(
                commitRoute ? BuildCommitResult() : BuildCaptureCompleteResult());
            return PngJsonCapturePublicationCaptureCompleteNotificationOperationFactory.Build(cleanupResult);
        }

        private PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult BuildCleanupResult(
            bool commitRoute = true)
        {
            return MakeOrchestrationCoordinator().Execute(
                commitRoute ? BuildCommitResult() : BuildCaptureCompleteResult());
        }

        private static PngJsonCapturePublicationCaptureCompleteNotificationCoordinator MakeCoordinator(
            IPngJsonCapturePublicationCaptureCompleteNotifier notifier = null)
        {
            return new PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(notifier ?? new FakeNotifier());
        }

        // ---- Tests ----

        [Test]
        public void Coordinator_Shape_TwoReadonlyFields_NonDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteNotificationCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Coordinator_Constructor_NullNotifier_Rejected()
        {
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    new PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(null)).ParamName,
                Is.EqualTo("notifier"));
        }

        [Test]
        public void Coordinator_Execute_NullCleanupResult_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();

            Assert.That(
                Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null)).ParamName,
                Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void Execute_NotifyExactlyOnce_IssuesResult()
        {
            RecordingNotifier notifier = new RecordingNotifier();
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);

            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());

            Assert.That(notifier.Calls, Is.EqualTo(1));
            Assert.That(result.IsValid, Is.True);
            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Notifier, notifier), Is.True);
        }

        [Test]
        public void Execute_NotifierException_PropagatesIdentically_NoRetry()
        {
            RecordingNotifier notifier = new RecordingNotifier();
            notifier.ToThrow = new InvalidOperationException("boom");
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);

            InvalidOperationException thrown =
                Assert.Throws<InvalidOperationException>(() => coordinator.Execute(BuildCleanupResult()));

            Assert.That(thrown, Is.SameAs(notifier.ToThrow));
            Assert.That(notifier.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullReceipt_Rejected()
        {
            RecordingNotifier notifier = new RecordingNotifier();
            notifier.Override = operation => null;
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(BuildCleanupResult()));
            Assert.That(notifier.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ForeignIssuerReceipt_Rejected()
        {
            FakeNotifier foreignNotifier = new FakeNotifier();
            RecordingNotifier notifier = new RecordingNotifier();
            notifier.Override = operation =>
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(foreignNotifier, operation);
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(BuildCleanupResult()));
            Assert.That(notifier.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Execute_OtherOperationReceipt_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation otherOperation =
                BuildNotification(commitRoute: true, out _);
            RecordingNotifier notifier = new RecordingNotifier();
            notifier.Override = operation =>
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(notifier, otherOperation);
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(BuildCleanupResult()));
            Assert.That(notifier.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Execute_InvalidReceipt_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation =
                BuildNotification(commitRoute: true, out PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult);
            RecordingNotifier notifier = new RecordingNotifier();
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt invalidReceipt =
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(notifier, operation);
            SetField(invalidReceipt, "_issuedBy", null);
            notifier.Override = op => invalidReceipt;
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(cleanupResult));
            Assert.That(notifier.Calls, Is.EqualTo(1));
        }

        [Test]
        public void IssuanceProof_Shape_PrivateConstructor_FourFields()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void IssuanceProof_IsMintedByThis_ExactBinding()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof =
                (PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof)GetField(result, "_proof");
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation = result.Operation;
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = result.Receipt;

            Assert.That(coordinator.IsMintedByThis(proof, operation, receipt), Is.True);

            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator otherCoordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult other =
                otherCoordinator.Execute(BuildCleanupResult(commitRoute: false));

            Assert.That(otherCoordinator.IsMintedByThis(proof, operation, receipt), Is.False);
            Assert.That(coordinator.IsMintedByThis(proof, other.Operation, receipt), Is.False);
            Assert.That(coordinator.IsMintedByThis(proof, operation, other.Receipt), Is.False);
            Assert.That(coordinator.IsMintedByThis(null, operation, receipt), Is.False);
        }

        [Test]
        public void Coordinator_NoAuthorityExposure()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteNotificationCoordinator);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(object)
                    || prop.PropertyType == typeof(PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof),
                    Is.False,
                    type.Name + "." + prop.Name + " must not expose the issuance authority or proof.");
            }
        }

        [Test]
        public void Result_Shape_FourReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteNotificationResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Shape_NoLeaseTokenStreamBytesExposure()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationCaptureCompleteNotificationCoordinator),
                typeof(PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof),
                typeof(PngJsonCapturePublicationCaptureCompleteNotificationResult)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || field.FieldType == typeof(Stream)
                        || field.FieldType == typeof(byte[])
                        || field.FieldType == typeof(PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult.ValidationToken),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a lease, stream, bytes, or token.");
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
        public void Result_Forwarding_AllValues()
        {
            RecordingNotifier notifier = new RecordingNotifier();
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator(notifier);
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            PngJsonCapturePublicationCaptureCompleteNotificationOperation operation = result.Operation;
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt receipt = result.Receipt;

            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Notifier, notifier), Is.True);
            Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(result.Receipt, receipt), Is.True);
            Assert.That(ReferenceEquals(result.CleanupResult, operation.CleanupResult), Is.True);
            Assert.That(ReferenceEquals(result.ExecutionResult, operation.ExecutionResult), Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockIdentityEvidence, operation.LockIdentityEvidence), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(result.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
            Assert.That(result.CaptureIndexPath, Is.EqualTo(operation.CaptureIndexPath));
            Assert.That(result.Disposition, Is.EqualTo(operation.Disposition));
            Assert.That(result.Status, Is.EqualTo(operation.Status));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_Create_NullArguments_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof =
                (PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof)GetField(result, "_proof");

            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteNotificationResult.Create(null, proof, result.Operation, result.Receipt)).ParamName,
                Is.EqualTo("issuedBy"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteNotificationResult.Create(coordinator, null, result.Operation, result.Receipt)).ParamName,
                Is.EqualTo("proof"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteNotificationResult.Create(coordinator, proof, null, result.Receipt)).ParamName,
                Is.EqualTo("operation"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    PngJsonCapturePublicationCaptureCompleteNotificationResult.Create(coordinator, proof, result.Operation, null)).ParamName,
                Is.EqualTo("receipt"));
        }

        [Test]
        public void Result_OwnerRelease_FailClosed()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult recovery =
                BuildCommitResult(1, EvMatchesExpected, out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult =
                MakeOrchestrationCoordinator().Execute(recovery);
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = MakeCoordinator().Execute(cleanupResult);
            Assert.That(result.IsValid, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_OperationHeldTokenCorruption_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            Assert.That(result.IsValid, Is.True);

            SetField(result.Operation.CleanupResult, "_token", null);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_ReceiptFieldNullSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            Assert.That(result.IsValid, Is.True);

            SetField(result.Receipt, "_issuedBy", null);
            Assert.That(result.IsValid, Is.False);

            PngJsonCapturePublicationCaptureCompleteNotificationResult result2 = coordinator.Execute(BuildCleanupResult());
            SetField(result2.Receipt, "_operation", null);
            Assert.That(result2.IsValid, Is.False);

            PngJsonCapturePublicationCaptureCompleteNotificationResult result3 = coordinator.Execute(BuildCleanupResult());
            PngJsonCapturePublicationCaptureCompleteNotificationOperation otherOperation =
                BuildNotification(commitRoute: true, out _);
            SetField(result3.Receipt, "_operation", otherOperation);
            Assert.That(result3.IsValid, Is.False);
        }

        [Test]
        public void Result_FieldSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            Assert.That(result.IsValid, Is.True);

            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator otherCoordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult other =
                otherCoordinator.Execute(BuildCleanupResult(commitRoute: false));
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof otherProof =
                (PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof)GetField(other, "_proof");

            PngJsonCapturePublicationCaptureCompleteNotificationResult r1 = coordinator.Execute(BuildCleanupResult());
            SetField(r1, "_issuedBy", otherCoordinator);
            Assert.That(r1.IsValid, Is.False);

            PngJsonCapturePublicationCaptureCompleteNotificationResult r2 = coordinator.Execute(BuildCleanupResult());
            SetField(r2, "_proof", otherProof);
            Assert.That(r2.IsValid, Is.False);

            PngJsonCapturePublicationCaptureCompleteNotificationResult r3 = coordinator.Execute(BuildCleanupResult());
            SetField(r3, "_operation", other.Operation);
            Assert.That(r3.IsValid, Is.False);

            PngJsonCapturePublicationCaptureCompleteNotificationResult r4 = coordinator.Execute(BuildCleanupResult());
            SetField(r4, "_receipt", other.Receipt);
            Assert.That(r4.IsValid, Is.False);
        }

        [Test]
        public void Result_SameIdentityDifferentOperationSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            Assert.That(result.IsValid, Is.True);

            // Same notification identity, but a distinct operation instance.
            PngJsonCapturePublicationCaptureCompleteNotificationOperation otherOperation =
                PngJsonCapturePublicationCaptureCompleteNotificationOperationFactory.Build(
                    MakeOrchestrationCoordinator().Execute(BuildCommitResult()));
            SetField(result, "_operation", otherOperation);

            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_Create_DirectReceipt_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator coordinator = MakeCoordinator();
            PngJsonCapturePublicationCaptureCompleteNotificationResult result = coordinator.Execute(BuildCleanupResult());
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof proof =
                (PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.IssuanceProof)GetField(result, "_proof");

            // A directly-minted receipt (bypassing the coordinator) cannot
            // produce a valid result: the proof binds only to the
            // coordinator-issued receipt.
            PngJsonCapturePublicationCaptureCompleteNotificationReceipt directReceipt =
                PngJsonCapturePublicationCaptureCompleteNotificationReceipt.Create(coordinator.Notifier, result.Operation);

            Assert.Throws<ArgumentException>(() =>
                PngJsonCapturePublicationCaptureCompleteNotificationResult.Create(coordinator, proof, result.Operation, directReceipt));
        }

        [Test]
        public void Source_Result_Create_NoRevalidation()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteNotificationResult.cs");

            int createIndex = source.IndexOf(
                "internal static PngJsonCapturePublicationCaptureCompleteNotificationResult Create(",
                StringComparison.Ordinal);
            Assert.That(createIndex, Is.GreaterThan(0));
            int returnIndex = source.IndexOf(
                "return new PngJsonCapturePublicationCaptureCompleteNotificationResult(",
                StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(createIndex));
            string createBody = source.Substring(createIndex, returnIndex - createIndex);

            Assert.That(createBody, Does.Contain("IsBoundO1("));
            Assert.That(createBody, Does.Not.Contain("IsIssuedFor"));
            Assert.That(createBody, Does.Not.Contain("IsValid"));
            Assert.That(createBody, Does.Not.Contain("IsFullyValid"));
        }

        [Test]
        public void Source_Coordinator_NotifyOnce()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.cs");

            int first = source.IndexOf(".Notify(", StringComparison.Ordinal);
            Assert.That(first, Is.GreaterThan(0));
            int last = source.LastIndexOf(".Notify(", StringComparison.Ordinal);
            Assert.That(last, Is.EqualTo(first));
        }

        [Test]
        public void Source_Coordinator_NoRetryLoopLeaseFilesystem()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteNotificationCoordinator.cs");

            Assert.That(source, Does.Not.Contain("for ("));
            Assert.That(source, Does.Not.Contain("while ("));
            Assert.That(source, Does.Not.Contain(".Dispose()"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
        }
    }
}
