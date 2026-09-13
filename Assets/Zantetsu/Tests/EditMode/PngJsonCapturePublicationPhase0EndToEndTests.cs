using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Phase 0 end-to-end acceptance: wires the existing Production boundaries
    /// — Inspection, Classification, Publish/Commit, Reinspection, Cleanup,
    /// Notification, LifecycleEvidence, and Release — on a real filesystem
    /// tree, for both the Fresh and Recovery normal paths, plus one Deferred
    /// stop and one partial-release retry.
    /// </summary>
    public class PngJsonCapturePublicationPhase0EndToEndTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus ReinspectionRequired =>
            CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired;

        private static CaptureRunPublicationArtifactRecoveryDisposition CommitCaptureIndex =>
            CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;

        private static CaptureRunPublicationCaptureCompleteCleanupExecutionStatus CaptureCompleteReady =>
            CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }

            _owners.Clear();

            for (int i = _sandboxes.Count - 1; i >= 0; i--)
            {
                string sandbox = _sandboxes[i];
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, true);
                }
            }

            _sandboxes.Clear();
        }

        // ---- General helpers ----

        private static (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-phase0-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            return (sandbox, staging, final);
        }

        private static CaptureRunRootLayout MakeLayout(string stagingBase, string finalBase, long testRunId = 1)
        {
            return new CaptureRunRootLayout(stagingBase, finalBase, testRunId);
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

        private static string Sha256(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = hex[hash[i] >> 4];
                chars[i * 2 + 1] = hex[hash[i] & 15];
            }

            return new string(chars);
        }

        private static void WriteBytes(string absolutePath, byte[] bytes)
        {
            string parent = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(absolutePath, bytes);
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            PngJsonCapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        // ---- Manifest / artifact construction ----

        private static TraceRunManifest MakeManifest(long testRunId = 1)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                "build-1",
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));

            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                recorder.TryTrigger();
                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                return TraceRunManifest.Create(snapshot, context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static CaptureRunReference MakeRun(TraceRunManifest manifest)
        {
            return new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, 1, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(
                context,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureRunReference run, CaptureFrameRequest request)
        {
            return new CaptureFrameRecord(run, request, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
        }

        private static CaptureFramePngSaveReceipt MakeReceipt(string path, int byteCount, string hash)
        {
            ConstructorInfo ctor = typeof(CaptureFramePngSaveReceipt).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(int), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFramePngSaveReceipt)ctor.Invoke(new object[] { path, byteCount, hash });
        }

        private static CaptureFramePngArtifact MakeArtifact(
            TraceRunManifest manifest,
            long captureFrameId,
            byte[] pngBytes)
        {
            CaptureRunReference run = MakeRun(manifest);
            CaptureFrameRequest request = MakeRequest(captureFrameId);
            CaptureFrameRecord record = MakeRecord(run, request);
            CaptureFramePngSaveReceipt receipt = MakeReceipt(@"C:\capture\out.png", pngBytes.Length, Sha256(pngBytes));
            return new CaptureFramePngArtifact(record, request, receipt);
        }

        // ---- PngJson plan ----

        private static PngJsonCapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength,
            long sidecarByteLength,
            string pngContentSha256,
            string sidecarContentSha256)
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
                pngContentSha256,
                sidecarContentSha256);
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId,
            string runManifestContentSha256,
            PngJsonCapturePublicationPlanEntry[] entries)
        {
            return new PngJsonCapturePublicationPlan(testRunId, InitId, runManifestContentSha256, entries);
        }

        // ---- Generic (frozen) plan construction for Fresh seed ----

        private static CaptureArtifactDescriptor MakeImageDescriptor(long id, long byteLength, string hash)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureArtifactDescriptor(
                "frame/" + idStr + "/image",
                CaptureArtifactKind.FrameImage,
                "image/png",
                1,
                "frames/" + idStr + ".png.stage",
                "frames/" + idStr + ".png",
                byteLength,
                hash);
        }

        private static CaptureArtifactDescriptor MakeMetadataDescriptor(long id, long byteLength, string hash)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureArtifactDescriptor(
                "frame/" + idStr + "/metadata",
                CaptureArtifactKind.FrameMetadata,
                "application/vnd.zantetsu.capture-frame+json",
                2,
                "frames/" + idStr + ".json.stage",
                "frames/" + idStr + ".json",
                byteLength,
                hash);
        }

        private static CaptureFrameEvidenceEntry MakeFrameEvidence(long id)
        {
            string idStr = id.ToString(CultureInfo.InvariantCulture);
            return new CaptureFrameEvidenceEntry(
                id,
                new[] { "frame/" + idStr + "/image", "frame/" + idStr + "/metadata" });
        }

        private static CapturePublicationPlan MakeGenericPlan(
            long testRunId,
            long[] frameIds,
            string runManifestContentSha256,
            byte[] png,
            byte[] sidecar)
        {
            CaptureArtifactDescriptor[] descriptors = new CaptureArtifactDescriptor[frameIds.Length * 2];
            CaptureFrameEvidenceEntry[] evidence = new CaptureFrameEvidenceEntry[frameIds.Length];
            int d = 0;
            for (int i = 0; i < frameIds.Length; i++)
            {
                descriptors[d++] = MakeImageDescriptor(frameIds[i], png.LongLength, Sha256(png));
                descriptors[d++] = MakeMetadataDescriptor(frameIds[i], sidecar.LongLength, Sha256(sidecar));
                evidence[i] = MakeFrameEvidence(frameIds[i]);
            }

            Array.Sort(descriptors, (a, b) => string.CompareOrdinal(a.ArtifactId, b.ArtifactId));
            return new CapturePublicationPlan(testRunId, InitId, runManifestContentSha256, descriptors, evidence);
        }

        // ---- Lease / session / freeze receipt forge ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, bool throwingFirstHandle = false)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            ICaptureRunLockHandle first = throwingFirstHandle
                ? new ThrowingOnceHandle(pathSet.FirstLockPath)
                : (ICaptureRunLockHandle)new FakeHandle(pathSet.FirstLockPath, true);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private CaptureRunInitializationSession MakeLifecycleSession(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunLockLease lease = MakeLease(layout);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationDocumentSet documents = new CaptureRunInitializationDocumentSet(layout, InitId);
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
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationSession session = MakeLifecycleSession(layout, out owner, out CaptureRunLockIdentityEvidence identity);
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

        private CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(
            CaptureRunRootLayout layout,
            CapturePublicationPlan genericPlan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout, out owner);
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, genericPlan, store.PublicationPlanPath, 16);
            return CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator,
                MintProof(coordinator, freezeReceipt, writeReceipt),
                freezeReceipt,
                writeReceipt);
        }

        private PngJsonCaptureFrozenRunArtifactInspectionSeed MakeSeed(
            CaptureRunRootLayout layout,
            string manifestHash,
            byte[] png,
            byte[] sidecar,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CapturePublicationPlan genericPlan = MakeGenericPlan(layout.TestRunId, new[] { 10L }, manifestHash, png, sidecar);
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(layout, genericPlan, out owner);
            PngJsonCaptureFrozenRunPublicationPlanBinding binding = PngJsonCaptureFrozenRunPublicationPlanBinding.Create(frozen);
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
            bool hasNonMarker = false)
        {
            return new CaptureRunInitializationRootObservation(
                role, rootExists, false, initStatus, initMarker,
                false, readyStatus, readyMarker, hasNonMarker, false, false);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(
            CaptureRunRootRole role,
            CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeRootObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome =
                (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            CaptureRunRootLayout layout,
            bool throwingFirstHandle,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, throwingFirstHandle);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection =
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            CaptureRunRootLayout layout,
            PngJsonCapturePublicationPlan plan,
            bool throwingFirstHandle,
            out CaptureRunInitializationOpenOutcome openOutcome,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            openOutcome = MakePublicationRecoveryOutcome(layout, throwingFirstHandle, out owner);

            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation inspection =
                new CaptureRunPublicationRecoveryInspectionOperation(openOutcome, 1000, 4, 64);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector,
                inspection,
                MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                MakeDoc(CaptureIndexTemporary, DocAbsent),
                MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);

            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                CaptureRunPublicationRecoveryClassifier.Classify(snapshot));
        }

        // ---- Filesystem fixtures ----

        private static void WriteMarkers(CaptureRunRootLayout layout)
        {
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            string stagingRoot = layout.StagingRunRoot;
            string finalRoot = layout.FinalRunRoot;
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(finalRoot);

            WriteBytes(Path.Combine(stagingRoot, "run.init"), CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization));
            WriteBytes(Path.Combine(finalRoot, "run.init"), CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization));
            WriteBytes(Path.Combine(stagingRoot, "run.ready"), CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady));
            WriteBytes(Path.Combine(finalRoot, "run.ready"), CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady));
        }

        private static List<string> SnapshotTree(string root)
        {
            List<string> entries = new List<string>();
            if (Directory.Exists(root))
            {
                foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                {
                    entries.Add("D:" + dir.Substring(root.Length).TrimStart('\\', '/'));
                }

                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    entries.Add("F:" + file.Substring(root.Length).TrimStart('\\', '/') + ":" + Sha256(File.ReadAllBytes(file)));
                }
            }

            entries.Sort(StringComparer.Ordinal);
            return entries;
        }

        private static void WaitForFile(string path)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    using (FileStream stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                }
                catch (System.UnauthorizedAccessException)
                {
                }

                System.Threading.Thread.Sleep(25);
            }
        }

        // ---- Fakes ----

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated = true)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public void Dispose()
            {
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

        private sealed class RecordingSink : IPngJsonCapturePublicationCaptureCompleteNotificationSink
        {
            public PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Response =
                PngJsonCapturePublicationCaptureCompleteNotificationAcceptance.Accepted;

            public int CallCount;

            public readonly List<PngJsonCapturePublicationCaptureCompleteNotificationIdentity> Identities =
                new List<PngJsonCapturePublicationCaptureCompleteNotificationIdentity>();

            public PngJsonCapturePublicationCaptureCompleteNotificationAcceptance Accept(
                PngJsonCapturePublicationCaptureCompleteNotificationIdentity identity)
            {
                CallCount++;
                Identities.Add(identity);
                return Response;
            }
        }

        // ---- Tests ----

        [Test]
        public void Fresh_NormalPath_PublishCommitCleanupNotifyRelease()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            string pngHash = Sha256(png);
            CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, png);
            byte[] sidecar = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            string sidecarHash = Sha256(sidecar);

            // Fresh authority from the exact freeze seed.
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed =
                MakeSeed(layout, manifestHash, png, sidecar, out CaptureRunInitializationSessionOwnershipLease owner);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed);

            // Real filesystem tree: staging artifacts, plan, and markers.
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), sidecar);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "publication.plan"),
                PngJsonCapturePublicationPlanCodec.SerializeCanonical(authority.AuthoritativePlan));
            WriteMarkers(layout);

            // Real boundaries.
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution =
                new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, committer);
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, execution);

            // First orchestration: publish the missing artifacts, then require reinspection.
            PngJsonCapturePublicationArtifactInspectionOperation firstOperation =
                PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult first = orchestrator.Execute(firstOperation);
            Assert.That(first.Status, Is.EqualTo(ReinspectionRequired));

            // Second orchestration: everything matches, so commit capture.index.
            PngJsonCapturePublicationArtifactInspectionOperation secondOperation =
                PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult second = orchestrator.Execute(secondOperation);
            Assert.That(second.Disposition, Is.EqualTo(CommitCaptureIndex));
            WaitForFile(Path.Combine(layout.FinalRunRoot, "capture.index"));

            // Cleanup.
            PngJsonCapturePublicationCaptureCompleteCleanupBackend cleanupBackend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator cleanupExecution =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(cleanupBackend);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator cleanupCoordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(cleanupExecution);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult =
                cleanupCoordinator.Execute(second);
            Assert.That(cleanupResult.Status, Is.EqualTo(CaptureCompleteReady));

            // Notification (single acceptance).
            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator notificationCoordinator =
                new PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(notifier);
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult =
                notificationCoordinator.Execute(cleanupResult);

            // Lifecycle evidence + release operation held as a local.
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence =
                PngJsonCapturePublicationCaptureCompleteLifecycleEvidence.FromFresh(notificationResult, seed.FreezeReceipt, owner);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation releaseOperation =
                PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence);

            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator releaseCoordinator =
                new PngJsonCapturePublicationCaptureCompleteReleaseCoordinator(new PngJsonCapturePublicationCaptureCompleteReleaser());
            PngJsonCapturePublicationCaptureCompleteReleaseResult releaseResult = releaseCoordinator.Execute(releaseOperation);

            // Final state.
            Assert.That(File.ReadAllBytes(Path.Combine(layout.FinalRunRoot, "frames", "10.png")), Is.EqualTo(png));
            Assert.That(File.ReadAllBytes(Path.Combine(layout.FinalRunRoot, "frames", "10.json")), Is.EqualTo(sidecar));
            Assert.That(File.ReadAllBytes(Path.Combine(layout.FinalRunRoot, "capture.index")),
                Is.EqualTo(PngJsonCapturePublicationPlanCodec.SerializeCanonical(authority.AuthoritativePlan)));
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);

            Assert.That(sink.CallCount, Is.EqualTo(1));
            Assert.That(sink.Identities[0].TestRunId, Is.EqualTo(notificationResult.TestRunId));
            Assert.That(sink.Identities[0].RunInitializationId, Is.EqualTo(notificationResult.RunInitializationId));
            Assert.That(sink.Identities[0].RunManifestContentSha256, Is.EqualTo(notificationResult.RunManifestContentSha256));
            Assert.That(sink.Identities[0].CaptureIndexPath, Is.EqualTo(notificationResult.CaptureIndexPath));

            Assert.That(releaseResult.IsValid, Is.True);
            Assert.That(releaseOperation.IsReleaseComplete, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.True);

            // Final artifacts survive release.
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.True);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "frames", "10.png")), Is.True);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "frames", "10.json")), Is.True);
        }

        [Test]
        public void Recovery_NormalPath_PublishCommitCleanupNotifyRelease()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            string pngHash = Sha256(png);
            CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, png);
            byte[] sidecar = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);
            string sidecarHash = Sha256(sidecar);

            PngJsonCapturePublicationPlan plan = MakePlan(
                1, manifestHash, new[] { MakeEntry(10, png.LongLength, sidecar.LongLength, pngHash, sidecarHash) });

            // Recovery authority referencing the exact PublicationRecoveryRequired open outcome.
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(layout, plan, false, out CaptureRunInitializationOpenOutcome openOutcome,
                    out CaptureRunInitializationSessionOwnershipLease owner);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), sidecar);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "publication.plan"),
                PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan));
            WriteMarkers(layout);

            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution =
                new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, committer);
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, execution);

            PngJsonCapturePublicationArtifactInspectionOperation firstOperation =
                PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult first = orchestrator.Execute(firstOperation);
            Assert.That(first.Status, Is.EqualTo(ReinspectionRequired));

            PngJsonCapturePublicationArtifactInspectionOperation secondOperation =
                PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult second = orchestrator.Execute(secondOperation);
            Assert.That(second.Disposition, Is.EqualTo(CommitCaptureIndex));
            WaitForFile(Path.Combine(layout.FinalRunRoot, "capture.index"));

            PngJsonCapturePublicationCaptureCompleteCleanupBackend cleanupBackend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator cleanupExecution =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(cleanupBackend);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator cleanupCoordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(cleanupExecution);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult =
                cleanupCoordinator.Execute(second);

            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator notificationCoordinator =
                new PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(notifier);
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult =
                notificationCoordinator.Execute(cleanupResult);

            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence =
                PngJsonCapturePublicationCaptureCompleteLifecycleEvidence.FromRecovery(notificationResult, openOutcome, owner);
            PngJsonCapturePublicationCaptureCompleteReleaseOperation releaseOperation =
                PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence);

            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator releaseCoordinator =
                new PngJsonCapturePublicationCaptureCompleteReleaseCoordinator(new PngJsonCapturePublicationCaptureCompleteReleaser());
            PngJsonCapturePublicationCaptureCompleteReleaseResult releaseResult = releaseCoordinator.Execute(releaseOperation);

            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.True);
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(sink.CallCount, Is.EqualTo(1));
            Assert.That(releaseResult.IsValid, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Deferred_BufferUnavailable_StopsBeforeAnySideEffect()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, png);
            byte[] sidecar = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

            PngJsonCapturePublicationPlan plan = MakePlan(
                1, manifestHash, new[] { MakeEntry(10, png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar)) });
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(layout, plan, false, out _, out CaptureRunInitializationSessionOwnershipLease owner);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), sidecar);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "publication.plan"),
                PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan));
            WriteMarkers(layout);

            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution =
                new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, committer);

            // Exhaust the verification buffer before the first inspection.
            CaptureArtifactVerificationBufferPool pool =
                new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
            CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
            Assert.That(held, Is.Not.Null);

            RecordingSink sink = new RecordingSink();

            // Snapshot the full tree before the attempt.
            List<string> stagingBefore = SnapshotTree(layout.StagingRunRoot);
            List<string> finalBefore = SnapshotTree(layout.FinalRunRoot);

            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult deferred;
            try
            {
                PngJsonCapturePublicationArtifactInspector inspector =
                    new PngJsonCapturePublicationArtifactInspector(bundleDirectory, CaptureArtifactNoFollowOpen.Create(), pool);
                PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                    new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, execution);

                PngJsonCapturePublicationArtifactInspectionOperation operation =
                    PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);

                deferred = orchestrator.Execute(operation);
            }
            finally
            {
                pool.Return(held);
            }

            // The attempt converges to the Deferred terminal, not a typed exception.
            Assert.That(deferred.Status, Is.EqualTo(CaptureRunPublicationArtifactRecoveryExecutionStatus.Deferred));
            Assert.That(deferred.Disposition, Is.EqualTo(CaptureRunPublicationArtifactRecoveryDisposition.None));
            Assert.That(deferred.IsValid, Is.True);
            Assert.That(deferred.InspectionSnapshot, Is.Null);
            Assert.That(deferred.Decision, Is.Null);
            Assert.That(deferred.Batch, Is.Null);
            Assert.That(deferred.ExecutionResult, Is.Null);

            // The file tree is byte-for-byte unchanged.
            Assert.That(SnapshotTree(layout.StagingRunRoot), Is.EqualTo(stagingBefore));
            Assert.That(SnapshotTree(layout.FinalRunRoot), Is.EqualTo(finalBefore));

            // No publish, commit, cleanup, or notification happened.
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
            Assert.That(sink.CallCount, Is.Zero);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(pool.OutstandingRentCount, Is.Zero);
        }

        [Test]
        public void Release_PartialFailure_Retry_CompletesWithoutRebuildingOperation()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = MakeLayout(staging, final, 1);

            TraceRunManifest manifest = MakeManifest(1);
            byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            string manifestHash = TraceRunManifestCodec.ComputeContentSha256(manifest);

            string bundleDirectory = Path.Combine(sandbox, "bundle");
            WriteBytes(Path.Combine(bundleDirectory, "manifest.json"), manifestBytes);

            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
            CaptureFramePngArtifact artifact = MakeArtifact(manifest, 10, png);
            byte[] sidecar = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

            PngJsonCapturePublicationPlan plan = MakePlan(
                1, manifestHash, new[] { MakeEntry(10, png.LongLength, sidecar.LongLength, Sha256(png), Sha256(sidecar)) });

            // Recovery path with a throw-once first handle for partial release.
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                MakeRecoveryAuthority(layout, plan, true, out CaptureRunInitializationOpenOutcome openOutcome,
                    out CaptureRunInitializationSessionOwnershipLease owner);

            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage"), png);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage"), sidecar);
            WriteBytes(Path.Combine(layout.StagingRunRoot, "publication.plan"),
                PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan));
            WriteMarkers(layout);

            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator execution =
                new PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator(publisher, committer);
            PngJsonCapturePublicationArtifactInspector inspector =
                new PngJsonCapturePublicationArtifactInspector(bundleDirectory);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator orchestrator =
                new PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator(inspector, execution);

            PngJsonCapturePublicationArtifactInspectionOperation firstOperation =
                PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult first = orchestrator.Execute(firstOperation);
            Assert.That(first.Status, Is.EqualTo(ReinspectionRequired));

            PngJsonCapturePublicationArtifactInspectionOperation secondOperation =
                PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, 1000);
            PngJsonCapturePublicationArtifactRecoveryOrchestrationResult second = orchestrator.Execute(secondOperation);
            Assert.That(second.Disposition, Is.EqualTo(CommitCaptureIndex));
            WaitForFile(Path.Combine(layout.FinalRunRoot, "capture.index"));

            PngJsonCapturePublicationCaptureCompleteCleanupBackend cleanupBackend =
                new PngJsonCapturePublicationCaptureCompleteCleanupBackend(layout);
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator cleanupExecution =
                new PngJsonCapturePublicationCaptureCompleteCleanupExecutionCoordinator(cleanupBackend);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator cleanupCoordinator =
                new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator(cleanupExecution);
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult =
                cleanupCoordinator.Execute(second);

            RecordingSink sink = new RecordingSink();
            PngJsonCapturePublicationCaptureCompleteNotifier notifier =
                new PngJsonCapturePublicationCaptureCompleteNotifier(sink);
            PngJsonCapturePublicationCaptureCompleteNotificationCoordinator notificationCoordinator =
                new PngJsonCapturePublicationCaptureCompleteNotificationCoordinator(notifier);
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult =
                notificationCoordinator.Execute(cleanupResult);

            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence evidence =
                PngJsonCapturePublicationCaptureCompleteLifecycleEvidence.FromRecovery(notificationResult, openOutcome, owner);

            // The exact release operation is held as a local and reused.
            PngJsonCapturePublicationCaptureCompleteReleaseOperation releaseOperation =
                PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(evidence);
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator releaseCoordinator =
                new PngJsonCapturePublicationCaptureCompleteReleaseCoordinator(new PngJsonCapturePublicationCaptureCompleteReleaser());

            // First attempt fails partially: no receipt or result.
            Assert.Throws<AggregateException>(() => releaseCoordinator.Execute(releaseOperation));
            Assert.That(releaseOperation.IsReleaseComplete, Is.False);

            // No cleanup or notification re-run.
            Assert.That(sink.CallCount, Is.EqualTo(1));

            // Second attempt with the exact same operation completes.
            PngJsonCapturePublicationCaptureCompleteReleaseResult releaseResult = releaseCoordinator.Execute(releaseOperation);
            Assert.That(releaseResult.IsValid, Is.True);
            Assert.That(releaseOperation.IsReleaseComplete, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.True);
        }
    }
}
