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
    public class PngJsonCapturePublicationCaptureCompleteCleanupActionPlanContractTests
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

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationDocumentObservationStatus DocLimitExceeded => CaptureRunPublicationDocumentObservationStatus.LimitExceeded;

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

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationCaptureCompleteCleanupActionPlanContractTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
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

        // ---- Tests ----

        [Test]
        public void Plan_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Builder_Shape_StaticNoFields()
        {
            Type type = typeof(PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract && type.IsSealed, Is.True);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            Assert.That(fields, Is.Empty, "The builder must hold no fields.");
        }

        [Test]
        public void Shape_NoLeaseProofArrayExposure()
        {
            foreach (Type type in new[]
            {
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan),
                typeof(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken)
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
                        type.Name + "." + field.Name + " must not hold a lease, stream, or byte sequence.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || prop.PropertyType == typeof(Stream)
                        || prop.PropertyType == typeof(byte[])
                        || prop.PropertyType == typeof(CaptureRunPublicationCaptureCompleteCleanupStep[]),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a lease, stream, bytes, or the step array.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease)
                        || method.ReturnType == typeof(Stream)
                        || method.ReturnType == typeof(byte[])
                        || method.ReturnType == typeof(CaptureRunPublicationCaptureCompleteCleanupStep[]),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a lease, stream, bytes, or the step array.");
                }
            }
        }

        [Test]
        public void Builder_NullResult_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(null));
            Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
        }

        [Test]
        public void Builder_InvalidResult_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult forged =
                (PngJsonCapturePublicationArtifactRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryOrchestrationResult));
            SetField(forged, "_issuedBy", result.IssuedBy);
            SetField(forged, "_executionResult", null);
            SetField(forged, "_token", GetField(result, "_token"));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(forged));
            Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
        }

        [Test]
        public void Builder_Rejects_NonCleanupStatuses()
        {
            // ReinspectionRequired.
            PngJsonCapturePublicationArtifactInspectionAuthority publishAuthority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation publishOperation = MakeOperation(publishAuthority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken publishToken =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(publishOperation);
            PngJsonCapturePublicationArtifactEntryObservation[] publishEntries =
            {
                MakeIndexObservation(publishToken, publishOperation, 0, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent)
            };
            FakeArtifactInspector publishInspector = MakeArtifactInspector(publishOperation, publishEntries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult publish =
                MakeOrchestrator(publishInspector, MakeExecutionCoordinator()).Execute(publishOperation);

            Assert.That(publish.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(publish));
            Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
        }

        [Test]
        public void Builder_Rejects_StopStatuses()
        {
            // OrphanedPreTrace.
            AssertStopStatusRejected(EvAbsent, EvAbsent, EvAbsent, EvAbsent, EvAbsent, CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace);

            // RunRootCollision (trace mismatch).
            AssertStopStatusRejected(EvAbsent, EvAbsent, EvAbsent, EvAbsent, EvMismatch, CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision);
        }

        private void AssertStopStatusRejected(
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar,
            CaptureRunPublicationEvidenceStatus traceStatus,
            CaptureRunPublicationArtifactRecoveryExecutionStatus expectedStatus)
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(token, operation, 0, stagingPng, stagingSidecar, finalPng, finalSidecar)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, traceStatus, traceStatus == EvAbsent ? 0 : 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);

            Assert.That(result.Status, Is.EqualTo(expectedStatus));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
            Assert.That(ex.ParamName, Is.EqualTo("orchestrationResult"));
        }

        [Test]
        public void CommitRoute_StepSequence()
        {
            // 1 entry, staging MatchesExpected, publication.plan Canonical,
            // staging frames Directory, tmp documents absent, commit route.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            Assert.That(plan.Count, Is.EqualTo(8));
            Assert.That(plan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, Png), Is.True);
            Assert.That(plan.GetStep(1).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, Sidecar), Is.True);
            Assert.That(plan.GetStep(2).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(3).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(4).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(5).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(6).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(7).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady, -1, CaptureRunPublicationArtifactKind.None), Is.True);
        }

        [Test]
        public void CommitRoute_NoCaptureIndexTemporaryDelete()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);

            for (int i = 0; i < plan.Count; i++)
            {
                Assert.That(plan.GetStep(i).Action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
            }
        }

        [Test]
        public void CommitRoute_MissingReceipt_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            SetField(result.ExecutionResult.GetCompletedStep(0), "_commitReceipt", null);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CommitRoute_ForeignIssuer_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            SetField(result.ExecutionResult.GetCompletedStep(0).CommitReceipt, "_issuedBy", new FakeCommitter());

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CommitRoute_DifferentOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult other = BuildCommitResult();

            PngJsonCaptureRunCaptureIndexCommitOperation otherOperation =
                other.Batch.GetStep(0).CaptureIndexCommitOperation;
            SetField(result.ExecutionResult.GetCompletedStep(0).CommitReceipt, "_operation", otherOperation);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CommitRoute_CorruptedReceipt_Rejected()
        {
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = BuildCommitResult();
            SetField(result.ExecutionResult.GetCompletedStep(0).CommitReceipt, "_operation", null);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void CaptureCompleteRoute_StepSequence()
        {
            // capture index Canonical, publication plan Canonical? No: indexAuthoritative
            // puts the plan at the capture index; publication plan is Absent by
            // default, so only the index and its tmp are candidates.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);

            // 1 entry (2 staging steps) + frames root + 4 tail = 7.
            Assert.That(plan.Count, Is.EqualTo(7));
            Assert.That(plan.GetStep(0).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, Png), Is.True);
            Assert.That(plan.GetStep(1).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, 0, Sidecar), Is.True);
            Assert.That(plan.GetStep(2).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(3).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(4).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(5).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot, -1, CaptureRunPublicationArtifactKind.None), Is.True);
            Assert.That(plan.GetStep(6).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady, -1, CaptureRunPublicationArtifactKind.None), Is.True);
        }

        [Test]
        public void CaptureComplete_MissingCanonicalIndex_Invalid()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);
            Assert.That(plan.IsValid, Is.True);

            SetField(plan.OrchestrationResult.Authority.RecoveryDecision.Snapshot, "_captureIndex", MakeDoc(CaptureIndex, DocAbsent));

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void CaptureComplete_DifferentPlan_Invalid()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);
            Assert.That(plan.IsValid, Is.True);

            PngJsonCapturePublicationPlan other = MakePlan(testRunId: 1, entries: MakeEntries(2));
            SetField(plan.OrchestrationResult.Authority.RecoveryDecision.Snapshot, "_captureIndex", MakeDoc(CaptureIndex, DocCanonical, 100, other));

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void CaptureComplete_IndexTemporaryCanonicalGeneratesStep()
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

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            Assert.That(plan.GetStep(0).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
        }

        [Test]
        public void CaptureComplete_IndexTemporaryForeignOrPartialOrLimitExceeded_Rejected()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan(entries: MakeEntries(1));
            PngJsonCapturePublicationPlan otherPlan = MakePlan(testRunId: 1, entries: MakeEntries(2));

            // Foreign plan.
            AssertCaptureCompleteRejectedAfterDocumentSwap(MakeDoc(CaptureIndexTemporary, DocCanonical, 100, otherPlan));

            // Partial / invalid.
            AssertCaptureCompleteRejectedAfterDocumentSwap(MakeDoc(CaptureIndexTemporary, DocInvalid, 0));

            // Limit exceeded.
            AssertCaptureCompleteRejectedAfterDocumentSwap(MakeDoc(CaptureIndexTemporary, DocLimitExceeded, 1000));
        }

        private void AssertCaptureCompleteRejectedAfterDocumentSwap(CaptureRunPublicationDocumentObservation captureIndexTemporary)
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: false);
            Assert.That(plan.IsValid, Is.True);

            SetField(plan.OrchestrationResult.Authority.RecoveryDecision.Snapshot, "_captureIndexTemporary", captureIndexTemporary);

            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void TraceManifestMismatch_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(token, operation, 0, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMismatch, 1);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void FinalArtifactMismatch_Rejected()
        {
            // final Png mismatched.
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
            {
                MakeIndexObservation(token, operation, 0, EvAbsent, EvAbsent, EvMismatch, EvMatchesExpected)
            };
            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result));
        }

        [Test]
        public void StagingArtifact_AbsentGeneratesNoStep()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(
                    BuildCommitResult(1, EvAbsent));

            for (int i = 0; i < plan.Count; i++)
            {
                Assert.That(plan.GetStep(i).Action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact));
            }
        }

        [Test]
        public void StagingArtifact_EntryAscendingPngBeforeSidecar()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(
                    BuildCommitResult(3, EvMatchesExpected));

            int position = 0;
            for (int entry = 0; entry < 3; entry++)
            {
                Assert.That(plan.GetStep(position).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, entry, Png), Is.True);
                Assert.That(plan.GetStep(position + 1).Matches(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact, entry, Sidecar), Is.True);
                position += 2;
            }
        }

        [Test]
        public void ZeroOneManyEntries()
        {
            // 0 entries: no staging steps; document/root steps remain.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan zero =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(BuildCommitResult(0, EvMatchesExpected));
            Assert.That(zero.Count, Is.EqualTo(6));

            // 1 entry: 2 staging steps.
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan one = BuildPlan(commitRoute: true);
            Assert.That(one.Count, Is.EqualTo(8));

            // 1000 entries via the Fresh authority (Fresh has no entry cap).
            long[] frameIds = new long[1000];
            for (int i = 0; i < frameIds.Length; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority fresh = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(fresh, 2000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[1000];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(token, operation, i, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            }

            FakeArtifactInspector inspector = MakeArtifactInspector(operation, entries, EvMatchesExpected, 100);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult many =
                MakeOrchestrator(inspector, MakeExecutionCoordinator()).Execute(operation);
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan manyPlan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(many);

            // Fresh: no staging steps, no document/root steps → 4 tail steps.
            Assert.That(manyPlan.Count, Is.EqualTo(4));
        }

        [Test]
        public void ForgedStepArray_NullReorder_IsValidFalse()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            Assert.That(plan.IsValid, Is.True);

            CaptureRunPublicationCaptureCompleteCleanupStep[] original =
                (CaptureRunPublicationCaptureCompleteCleanupStep[])GetField(plan, "_steps");

            // Null array.
            SetField(plan, "_steps", null);
            Assert.That(plan.IsValid, Is.False);
            SetField(plan, "_steps", original);

            // Shortened array.
            CaptureRunPublicationCaptureCompleteCleanupStep[] shortened = new CaptureRunPublicationCaptureCompleteCleanupStep[original.Length - 1];
            Array.Copy(original, shortened, shortened.Length);
            SetField(plan, "_steps", shortened);
            Assert.That(plan.IsValid, Is.False);
            SetField(plan, "_steps", original);

            // Reordered array.
            CaptureRunPublicationCaptureCompleteCleanupStep[] reordered = new CaptureRunPublicationCaptureCompleteCleanupStep[original.Length];
            Array.Copy(original, reordered, reordered.Length);
            CaptureRunPublicationCaptureCompleteCleanupStep temp = reordered[0];
            reordered[0] = reordered[1];
            reordered[1] = temp;
            SetField(plan, "_steps", reordered);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void ResultTokenSwap_IsValidFalse()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            Assert.That(plan.IsValid, Is.True);

            // Swap the held orchestration token for another result's token.
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult other = BuildCommitResult();
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult.ValidationToken otherToken;
            other.TryValidate(out otherToken);

            SetField(plan, "_orchestrationToken", otherToken);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void ExecutionResultSwap_IsValidFalse()
        {
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = BuildPlan(commitRoute: true);
            Assert.That(plan.IsValid, Is.True);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult other = BuildCommitResult();
            SetField(plan.OrchestrationResult, "_executionResult", other.ExecutionResult);
            Assert.That(plan.IsValid, Is.False);
        }

        [Test]
        public void OwnerExpiry_IsValidFalse_AndTokenFails()
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
            Assert.That(plan.IsValid, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(plan.IsValid, Is.False);
            Assert.That(plan.IsValidIndexLocal(planToken, 0), Is.False);
        }

        [Test]
        public void FreshRoute_Commit_NoDocumentOrRootSteps()
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

            Assert.That(result.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired));
            Assert.That(result.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));

            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan =
                PngJsonCapturePublicationCaptureCompleteCleanupActionPlanBuilder.Build(result);

            // Fresh never fabricates document or root steps.
            for (int i = 0; i < plan.Count; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupAction action = plan.GetStep(i).Action;
                Assert.That(action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary));
                Assert.That(action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary));
                Assert.That(action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot));
                Assert.That(action, Is.Not.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan));
            }

            Assert.That(plan.Count, Is.EqualTo(4));
            Assert.That(plan.GetStep(0).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker));
            Assert.That(plan.GetStep(1).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker));
            Assert.That(plan.GetStep(2).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot));
            Assert.That(plan.GetStep(3).Action, Is.EqualTo(CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady));
        }

        [Test]
        public void Source_FreshNeverReadsRecoveryDecision()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.cs");

            int recoveryBranch = source.IndexOf("RecoveryDecision", StringComparison.Ordinal);
            Assert.That(recoveryBranch, Is.GreaterThan(0));
            Assert.That(
                source.IndexOf("authorityKind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision", StringComparison.Ordinal),
                Is.GreaterThan(0),
                "Recovery decision access must be gated by the RecoveryDecision authority kind.");
        }

        [Test]
        public void Source_RecoveryNeverReadsFreshSeed()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.cs");

            Assert.That(source, Does.Not.Contain("FreshSeed"));
            Assert.That(source, Does.Not.Contain("FreshFrozenRun"));
        }

        [Test]
        public void Source_PlanBuild_SingleResultValidation()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.cs");

            int createIndex = source.IndexOf("internal static PngJsonCapturePublicationCaptureCompleteCleanupActionPlan Create(", StringComparison.Ordinal);
            Assert.That(createIndex, Is.GreaterThan(0));
            int returnIndex = source.IndexOf("return new PngJsonCapturePublicationCaptureCompleteCleanupActionPlan(", StringComparison.Ordinal);
            Assert.That(returnIndex, Is.GreaterThan(createIndex));
            string createBody = source.Substring(createIndex, returnIndex - createIndex);

            // Exactly one full result validation via TryValidate.
            Assert.That(
                createBody.IndexOf("TryValidate", StringComparison.Ordinal),
                Is.EqualTo(createBody.LastIndexOf("TryValidate", StringComparison.Ordinal)));
            Assert.That(createBody, Does.Contain("orchestrationResult.TryValidate("));
            Assert.That(createBody, Does.Not.Contain("orchestrationResult.IsValid"));
        }

        [Test]
        public void Source_TokenAcquire_OutsideLoop()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.cs");

            int createIndex = source.IndexOf("internal static PngJsonCapturePublicationCaptureCompleteCleanupActionPlan Create(", StringComparison.Ordinal);
            int returnIndex = source.IndexOf("return new PngJsonCapturePublicationCaptureCompleteCleanupActionPlan(", StringComparison.Ordinal);
            string createBody = source.Substring(createIndex, returnIndex - createIndex);

            Assert.That(createBody, Does.Contain("orchestrationResult.TryValidate("));
            Assert.That(createBody, Does.Not.Contain("for ("));
        }

        [Test]
        public void Source_IndexLocal_NoEntryScanNoSerialize()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.cs");

            int indexLocalIndex = source.IndexOf("internal bool IsValidIndexLocal(", StringComparison.Ordinal);
            Assert.That(indexLocalIndex, Is.GreaterThan(0));
            int structureIndex = source.IndexOf("internal bool IsIndexLocalStructureIntact()", StringComparison.Ordinal);
            Assert.That(structureIndex, Is.GreaterThan(indexLocalIndex));
            string indexLocalBody = source.Substring(indexLocalIndex, structureIndex - indexLocalIndex);

            Assert.That(indexLocalBody, Does.Not.Contain("GetEntry"));
            Assert.That(indexLocalBody, Does.Not.Contain("SerializeCanonical"));
            Assert.That(indexLocalBody, Does.Not.Contain("File."));
            Assert.That(indexLocalBody, Does.Not.Contain("for ("));
        }
    }
}
