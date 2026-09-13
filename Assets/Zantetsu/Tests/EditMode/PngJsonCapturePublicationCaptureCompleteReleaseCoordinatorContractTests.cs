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
    public class PngJsonCapturePublicationCaptureCompleteReleaseCoordinatorContractTests
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

        private static int CountOccurrences(string source, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
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

        private sealed class RecordingReleaser : IPngJsonCapturePublicationCaptureCompleteReleaser
        {
            public int Calls;

            public Exception ToThrow;

            public Func<
                PngJsonCapturePublicationCaptureCompleteReleaseOperation,
                PngJsonCapturePublicationCaptureCompleteReleaseReceipt> Override;

            public PngJsonCapturePublicationCaptureCompleteReleaseReceipt Release(
                PngJsonCapturePublicationCaptureCompleteReleaseOperation operation)
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

                CaptureRunInitializationSessionOwnershipLease ownershipLease = operation.OwnershipLease;
                ownershipLease.Dispose();
                return new PngJsonCapturePublicationCaptureCompleteReleaseReceipt(this, operation);
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

        private static PngJsonCapturePublicationCaptureCompleteReleaseOperation MakeReleaseOperation(
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence)
        {
            return PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence);
        }

        private static PngJsonCapturePublicationCaptureCompleteReleaseCoordinator MakeReleaseCoordinator(
            IPngJsonCapturePublicationCaptureCompleteReleaser releaser = null)
        {
            return new PngJsonCapturePublicationCaptureCompleteReleaseCoordinator(
                releaser ?? new PngJsonCapturePublicationCaptureCompleteReleaser());
        }

        private PngJsonCapturePublicationCaptureCompleteReleaseResult MakeResult(
            out PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator)
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            coordinator = MakeReleaseCoordinator();
            return coordinator.Execute(operation);
        }

        // ---- Tests ----

        [Test]
        public void Coordinator_Constructor_NullReleaser_Rejected()
        {
            Assert.That(
                Assert.Throws<ArgumentNullException>(() =>
                    new PngJsonCapturePublicationCaptureCompleteReleaseCoordinator(null)).ParamName,
                Is.EqualTo("releaser"));
        }

        [Test]
        public void Coordinator_Shape_TwoReadonlyFields_NonDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaseCoordinator);

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
        public void Status_Enum_UnderlyingTypeAndValues()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaseStatus);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsEnum, Is.True);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(2));
            Assert.That((int)PngJsonCapturePublicationCaptureCompleteReleaseStatus.None, Is.EqualTo(0));
            Assert.That((int)PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullOperation_Rejected()
        {
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator();

            Assert.That(
                Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null)).ParamName,
                Is.EqualTo("operation"));
        }

        [Test]
        public void Execute_NonReleasableOperation_Rejected_ReleaserNotCalled()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaser concrete = new PngJsonCapturePublicationCaptureCompleteReleaser();
            concrete.Release(operation);
            Assert.That(operation.CanRelease, Is.False);

            RecordingReleaser recording = new RecordingReleaser();
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator(recording);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(recording.Calls, Is.EqualTo(0));
        }

        [Test]
        public void Execute_Fresh_Success_ForwardsAndOwnerReleased()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeFreshEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CaptureEvidenceRunFreezeReceipt freezeReceipt,
                out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator();

            PngJsonCapturePublicationCaptureCompleteReleaseResult result = coordinator.Execute(operation);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased));
            Assert.That(result.IsReleaseComplete, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Execute_Recovery_Success_ForwardsAndOwnerReleased()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator();

            PngJsonCapturePublicationCaptureCompleteReleaseResult result = coordinator.Execute(operation);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased));
            Assert.That(result.IsReleaseComplete, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Execute_ReleaserCalledExactlyOnce()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            RecordingReleaser recording = new RecordingReleaser();
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator(recording);

            PngJsonCapturePublicationCaptureCompleteReleaseResult result = coordinator.Execute(operation);

            Assert.That(recording.Calls, Is.EqualTo(1));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_ReleaserException_PropagatesSameInstance_NoResult_NoRetry()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            RecordingReleaser recording = new RecordingReleaser();
            InvalidOperationException expected = new InvalidOperationException("boom");
            recording.ToThrow = expected;
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator(recording);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));

            Assert.That(thrown, Is.SameAs(expected));
            Assert.That(recording.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Execute_PartialFailure_RetrySameOperation_Succeeds()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out CaptureRunInitializationSessionOwnershipLease owner,
                out _,
                throwingFirstHandle: true);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator();

            Assert.Throws<AggregateException>(() => coordinator.Execute(operation));
            Assert.That(operation.CanRelease, Is.True);

            PngJsonCapturePublicationCaptureCompleteReleaseResult result = coordinator.Execute(operation);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased));
            Assert.That(owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Execute_BadReceipt_RejectedBeforeResult_NoRecall()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);

            // null receipt.
            RecordingReleaser nullReleaser = new RecordingReleaser();
            nullReleaser.Override = op => null;
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator nullCoordinator = MakeReleaseCoordinator(nullReleaser);
            Assert.Throws<InvalidOperationException>(() => nullCoordinator.Execute(operation));
            Assert.That(nullReleaser.Calls, Is.EqualTo(1));

            // foreign issuer and different operation receipt.
            PngJsonCapturePublicationCaptureCompleteReleaseOperation otherOperation = MakeReleaseOperation(
                MakeRecoveryEvidence(out _, out _));
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt foreignReceipt =
                new PngJsonCapturePublicationCaptureCompleteReleaser().Release(otherOperation);

            RecordingReleaser foreignReleaser = new RecordingReleaser();
            foreignReleaser.Override = op => foreignReceipt;
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator foreignCoordinator = MakeReleaseCoordinator(foreignReleaser);
            Assert.Throws<InvalidOperationException>(() => foreignCoordinator.Execute(operation));
            Assert.That(foreignReleaser.Calls, Is.EqualTo(1));

            // invalid receipt (nulled issuer).
            PngJsonCapturePublicationCaptureCompleteReleaseOperation op3 = MakeReleaseOperation(
                MakeRecoveryEvidence(out _, out _));
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt invalidReceipt =
                new PngJsonCapturePublicationCaptureCompleteReleaser().Release(op3);
            SetField(invalidReceipt, "_issuedBy", null);

            RecordingReleaser invalidReleaser = new RecordingReleaser();
            invalidReleaser.Override = op => invalidReceipt;
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator invalidCoordinator = MakeReleaseCoordinator(invalidReleaser);
            Assert.Throws<InvalidOperationException>(() => invalidCoordinator.Execute(operation));
            Assert.That(invalidReleaser.Calls, Is.EqualTo(1));
        }

        [Test]
        public void Result_Shape_FourFields_NoPublicCtor_NonDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaseResult);

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
        public void Result_Forwarding_AllAccessors_StatusOwnerReleased()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator();

            PngJsonCapturePublicationCaptureCompleteReleaseResult result = coordinator.Execute(operation);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased));
            Assert.That(ReferenceEquals(result.IssuedBy, coordinator), Is.True);
            Assert.That(ReferenceEquals(result.Releaser, coordinator.Releaser), Is.True);
            Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(result.Receipt, result.Receipt), Is.True);
            Assert.That(result.Receipt, Is.Not.Null);
            Assert.That(ReferenceEquals(result.LifecycleEvidence, operation.LifecycleEvidence), Is.True);
            Assert.That(ReferenceEquals(result.NotificationResult, operation.NotificationResult), Is.True);
            Assert.That(result.Kind, Is.EqualTo(operation.Kind));
            Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
            Assert.That(ReferenceEquals(result.LockIdentityEvidence, operation.LockIdentityEvidence), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(result.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
            Assert.That(result.CaptureIndexPath, Is.EqualTo(operation.CaptureIndexPath));
            Assert.That(result.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Result_ValidAfterLifecycleEvidenceInvalid()
        {
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence = MakeRecoveryEvidence(
                out _, out _);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation = MakeReleaseOperation(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator coordinator = MakeReleaseCoordinator();

            PngJsonCapturePublicationCaptureCompleteReleaseResult result = coordinator.Execute(operation);

            Assert.That(evidence.IsValid, Is.False);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased));
        }

        [Test]
        public void Result_NotificationResultInternalsNulled_IsValidFalse_StatusNone_NoThrow()
        {
            PngJsonCapturePublicationCaptureCompleteReleaseResult result = MakeResult(out _);
            Assert.That(result.IsValid, Is.True);

            SetField(result.Operation.NotificationResult, "_operation", null);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.None));
        }

        [Test]
        public void Result_FieldNullSwap_FailClosed()
        {
            PngJsonCapturePublicationCaptureCompleteReleaseResult result = MakeResult(out _);
            SetField(result, "_issuedBy", null);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.None));

            result = MakeResult(out _);
            SetField(result, "_proof", null);
            Assert.That(result.IsValid, Is.False);

            result = MakeResult(out _);
            SetField(result, "_operation", null);
            Assert.That(result.IsValid, Is.False);

            result = MakeResult(out _);
            SetField(result, "_receipt", null);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Proof_ReferenceSwap_FailClosed()
        {
            foreach (string field in new[] { "_coordinator", "_gate", "_releaser", "_operation", "_receipt" })
            {
                PngJsonCapturePublicationCaptureCompleteReleaseResult result = MakeResult(out _);
                object proof = GetField(result, "_proof");
                SetField(proof, field, null);
                Assert.That(result.IsValid, Is.False, field + " null must fail closed.");
                Assert.That(result.Status, Is.EqualTo(PngJsonCapturePublicationCaptureCompleteReleaseStatus.None));
            }
        }

        [Test]
        public void Result_NoLeaseOrTokenExposure()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteReleaseResult);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
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
            Assert.That(type.GetProperty("Token", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
        }

        [Test]
        public void Source_CreateDoesNotRevalidateIsIssuedFor()
        {
            string source = ReadSource(
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaseResult.cs");

            // The only code call to receipt.IsIssuedFor lives in the full
            // validation predicate; the atomic factory re-runs only O(1)
            // binding.
            Assert.That(CountOccurrences(source, "receipt.IsIssuedFor("), Is.EqualTo(1));
            Assert.That(source, Does.Contain("private static bool IsFullyValid"));
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaseResult.cs",
                "Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteReleaseStatus.cs"
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
