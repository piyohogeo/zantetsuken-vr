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
    public class PngJsonCapturePublicationArtifactInspectionAuthorityContractTests
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

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationRecoveryDisposition NoAuthoritativeDocument =>
            CaptureRunPublicationRecoveryDisposition.NoAuthoritativeDocument;

        private static PngJsonCapturePublicationArtifactInspectionAuthorityKind None =>
            PngJsonCapturePublicationArtifactInspectionAuthorityKind.None;

        private static PngJsonCapturePublicationArtifactInspectionAuthorityKind FreshFrozenRun =>
            PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun;

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

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationArtifactInspectionAuthorityContractTests).Assembly.Location);
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

        private static void AssertNoForbiddenDependencies(string source)
        {
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Codec"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("ComputeHash"));
            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain(".Where("));
            Assert.That(source, Does.Not.Contain(".Select("));
            Assert.That(source, Does.Not.Contain("using UnityEngine"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Notifier"));
            Assert.That(source, Does.Not.Contain("TryAcquire"));
            Assert.That(source, Does.Not.Contain(".Dispose()"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("Thread"));
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

        // ---- Lease ----

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        // ---- Fresh seed graph forging ----

        private static CaptureRunInitializationSession MakeLifecycleSession(
            CaptureRunRootLayout layout,
            CaptureRunLockLease lease)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator execution = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationExecutionReceipt receipt = execution.Execute(batch);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return new CaptureRunInitializationSession(lease, evidence);
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
            SetField(receipt, "_terminalBuffer", terminalBuffer);
            return receipt;
        }

        private static CaptureEvidenceRunFreezeReceipt MakeValidFreezeReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunLockLease lease = MakeLease(layout);
            CaptureRunInitializationSession session = MakeLifecycleSession(layout, lease);
            CaptureFrameDraftRegistry drafts = ForgeDraftRegistry(layout.TestRunId);
            CaptureArtifactRegistry artifacts = ForgeArtifactRegistry();
            return ForgeFreezeReceipt(session, drafts, artifacts);
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
                freezeReceipt.RunSession,
                freezeReceipt.LockLease);
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

        private static CaptureEvidenceFrozenRunPublicationResult MakeFrozenResult(CapturePublicationPlan genericPlan)
        {
            CaptureRunRootLayout layout = MakeLayout(genericPlan.TestRunId);
            CaptureArtifactFileStore store = ForgeStore(layout);
            CaptureEvidenceRunPublicationCoordinator coordinator = ForgeCoordinator(store);
            CaptureEvidenceRunFreezeReceipt freezeReceipt = MakeValidFreezeReceipt(layout);
            CapturePublicationPlanWriteReceipt writeReceipt = new CapturePublicationPlanWriteReceipt(
                store, genericPlan, store.PublicationPlanPath, 16);
            return CaptureEvidenceFrozenRunPublicationResult.Create(
                coordinator,
                MintProof(coordinator, freezeReceipt, writeReceipt),
                freezeReceipt,
                writeReceipt);
        }

        private static PngJsonCaptureFrozenRunPublicationPlanBinding MakeSeedBinding(params long[] frameIds)
        {
            CapturePublicationPlan genericPlan = MakeGenericPlan(3, frameIds);
            CaptureEvidenceFrozenRunPublicationResult frozen = MakeFrozenResult(genericPlan);
            return PngJsonCaptureFrozenRunPublicationPlanBindingBuilder.Build(frozen);
        }

        private static PngJsonCaptureFrozenRunArtifactInspectionSeed MakeSeed(params long[] frameIds)
        {
            return PngJsonCaptureFrozenRunArtifactInspectionSeedBuilder.Build(MakeSeedBinding(frameIds));
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

        private static CaptureRunInitializationRootObservation MakeObservation(
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

        private static CaptureRunInitializationRootObservation MakeAbsent(CaptureRunRootRole role)
        {
            return MakeObservation(role, false, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(CaptureRunRootRole role, CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
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

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockLease lease)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_lockLease", lease);
            return outcome;
        }

        private static CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(List<string> disposeLog = null)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, lease, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, lease);
        }

        private static CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryOperation(
            int maximumPlanBytes = 1000,
            int maximumEntryCount = 4,
            int maximumPathBytes = 64)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(),
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

        private static CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan = null,
            bool indexAuthoritative = false)
        {
            plan = plan ?? MakePlan();
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority()
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(MakeDecision());
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeFreshAuthority(params long[] frameIds)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(MakeSeed(frameIds));
        }

        // ---- Enum / shape ----

        [Test]
        public void Enum_UnderlyingTypeAndValues()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactInspectionAuthorityKind);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(
                Enum.GetNames(type),
                Is.EqualTo(new[] { "None", "RecoveryDecision", "FreshFrozenRun" }));
            Assert.That((int)PngJsonCapturePublicationArtifactInspectionAuthorityKind.None, Is.EqualTo(0));
            Assert.That((int)PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision, Is.EqualTo(1));
            Assert.That((int)PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun, Is.EqualTo(2));
        }

        [Test]
        public void Authority_TypeShape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactInspectionAuthority);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsValueType, Is.False, field.Name + " must be a reference field.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            MethodInfo fromRecovery = type.GetMethod("FromRecovery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(fromRecovery, Is.Not.Null);
            Assert.That(fromRecovery.ReturnType, Is.EqualTo(type));
            ParameterInfo[] recoveryParams = fromRecovery.GetParameters();
            Assert.That(recoveryParams.Length, Is.EqualTo(1));
            Assert.That(recoveryParams[0].ParameterType, Is.EqualTo(typeof(CaptureRunPublicationRecoveryDecision)));

            MethodInfo fromFresh = type.GetMethod("FromFresh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(fromFresh, Is.Not.Null);
            Assert.That(fromFresh.ReturnType, Is.EqualTo(type));
            ParameterInfo[] freshParams = fromFresh.GetParameters();
            Assert.That(freshParams.Length, Is.EqualTo(1));
            Assert.That(freshParams[0].ParameterType, Is.EqualTo(typeof(PngJsonCaptureFrozenRunArtifactInspectionSeed)));
        }

        // ---- Recovery normal ----

        [Test]
        public void Recovery_PublicationPlanAuthoritative_Valid()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();

            Assert.That(authority.IsValid, Is.True);
            Assert.That(authority.Kind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision));
            Assert.That(authority.IsRecovery, Is.True);
            Assert.That(authority.IsFresh, Is.False);
            Assert.That(authority.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
        }

        [Test]
        public void Recovery_CaptureIndexAuthoritative_Valid()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(indexAuthoritative: true);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);

            Assert.That(authority.IsValid, Is.True);
            Assert.That(authority.Kind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision));
            Assert.That(authority.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative));
            Assert.That(ReferenceEquals(authority.AuthoritativePlan, decision.AuthoritativePlan), Is.True);
        }

        [Test]
        public void Recovery_ExactForwarding()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);

            Assert.That(authority.IsValid, Is.True);
            Assert.That(ReferenceEquals(authority.RecoveryDecision, decision), Is.True);
            Assert.That(authority.FreshSeed, Is.Null);
            Assert.That(ReferenceEquals(authority.AuthoritativePlan, decision.AuthoritativePlan), Is.True);
            Assert.That(authority.Disposition, Is.EqualTo(decision.Disposition));
            Assert.That(ReferenceEquals(authority.PublicationPaths, decision.Snapshot.Operation.PublicationPaths), Is.True);
            Assert.That(ReferenceEquals(authority.RootLayout, decision.RootLayout), Is.True);
            Assert.That(ReferenceEquals(authority.LockLease, decision.Snapshot.Operation.LockLease), Is.True);
            Assert.That(authority.TestRunId, Is.EqualTo(decision.TestRunId));
            Assert.That(authority.RunInitializationId, Is.EqualTo(decision.RunInitializationId));
            Assert.That(authority.RunManifestContentSha256, Is.EqualTo(decision.AuthoritativePlan.RunManifestContentSha256));
        }

        // ---- Fresh normal ----

        [Test]
        public void Fresh_ZeroFrames_Valid()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority();

            Assert.That(authority.IsValid, Is.True);
            Assert.That(authority.Kind, Is.EqualTo(FreshFrozenRun));
            Assert.That(authority.IsFresh, Is.True);
            Assert.That(authority.IsRecovery, Is.False);
            Assert.That(authority.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
            Assert.That(authority.AuthoritativePlan.EntryCount, Is.EqualTo(0));
        }

        [Test]
        public void Fresh_MultipleFrames_Valid()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1, 2, 3);

            Assert.That(authority.IsValid, Is.True);
            Assert.That(authority.Kind, Is.EqualTo(FreshFrozenRun));
            Assert.That(authority.AuthoritativePlan.EntryCount, Is.EqualTo(3));
        }

        [Test]
        public void Fresh_ExactForwarding()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1, 2);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed);

            Assert.That(authority.IsValid, Is.True);
            Assert.That(authority.RecoveryDecision, Is.Null);
            Assert.That(ReferenceEquals(authority.FreshSeed, seed), Is.True);
            Assert.That(ReferenceEquals(authority.AuthoritativePlan, seed.AuthoritativePlan), Is.True);
            Assert.That(ReferenceEquals(authority.AuthoritativePlan, seed.PlanBinding.LegacyPlan), Is.True);
            Assert.That(authority.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
            Assert.That(ReferenceEquals(authority.PublicationPaths, seed.PublicationPaths), Is.True);
            Assert.That(ReferenceEquals(authority.RootLayout, seed.RootLayout), Is.True);
            Assert.That(ReferenceEquals(authority.LockLease, seed.LockLease), Is.True);
            Assert.That(authority.TestRunId, Is.EqualTo(seed.TestRunId));
            Assert.That(authority.RunInitializationId, Is.EqualTo(seed.RunInitializationId));
            Assert.That(authority.RunManifestContentSha256, Is.EqualTo(seed.RunManifestContentSha256));
        }

        // ---- Factory rejection ----

        [Test]
        public void FromRecovery_Null_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(null));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryDecision"));
        }

        [Test]
        public void FromFresh_Null_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(null));
            Assert.That(ex.ParamName, Is.EqualTo("freshSeed"));
        }

        [Test]
        public void FromRecovery_InvalidDecision_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = (CaptureRunPublicationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryDecision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryDecision"));
        }

        [Test]
        public void FromFresh_InvalidSeed_Rejected()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = (PngJsonCaptureFrozenRunArtifactInspectionSeed)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCaptureFrozenRunArtifactInspectionSeed));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed));
            Assert.That(ex.ParamName, Is.EqualTo("freshSeed"));
        }

        [Test]
        public void FromRecovery_NoAuthoritativeDocument_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = MakeRecoveryOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeRecoverySnapshot(inspector, recoveryOperation);
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(NoAuthoritativeDocument));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision));
        }

        [Test]
        public void FromRecovery_RunRootCollision_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = MakeRecoveryOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = new CaptureRunPublicationRecoveryInspectionSnapshot(
                inspector,
                recoveryOperation,
                MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                MakeDoc(PublicationPlan, DocAbsent),
                MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, true, false);
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.RunRootCollision));
            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision));
        }

        [Test]
        public void FromRecovery_ReleasedOutcome_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            decision.Snapshot.Operation.OpenOutcome.Dispose();

            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision));
        }

        [Test]
        public void FromFresh_ReleasedSession_Rejected()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            seed.RunSession.Dispose();

            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed));
        }

        [Test]
        public void FromFresh_ReleasedLease_Rejected()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            seed.LockLease.Dispose();

            Assert.Throws<ArgumentException>(() => PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed));
        }

        // ---- Tamper: Recovery path ----

        [Test]
        public void Recovery_SnapshotNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority.RecoveryDecision, "_snapshot", null);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Recovery_OperationNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority.RecoveryDecision.Snapshot, "_operation", null);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Recovery_ForeignRootLayout_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            CaptureRunPublicationRecoveryInspectionOperation operation = authority.RecoveryDecision.Snapshot.Operation;
            CaptureRunPublicationPathSet foreign = new CaptureRunPublicationPathSet(MakeLayout(99));
            SetField(operation, "_publicationPaths", foreign);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Recovery_TestRunIdMismatch_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority.RecoveryDecision.AuthoritativePlan, "_testRunId", 999L);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Recovery_InitializationIdMismatch_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority.RecoveryDecision.AuthoritativePlan, "_runInitializationId", "ffffffffffffffffffffffffffffffff");

            Assert.That(authority.IsValid, Is.False);
        }

        // ---- Tamper: Fresh path ----

        [Test]
        public void Fresh_BindingNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed, "_planBinding", null);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_FrozenResultNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed.PlanBinding, "_frozenPublicationResult", null);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_LegacyPlanNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed.PlanBinding, "_legacyPlan", null);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_TestRunIdMismatch_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed.GenericPlan, "_testRunId", 999L);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_InitializationIdMismatch_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed.GenericPlan, "_runInitializationId", "ffffffffffffffffffffffffffffffff");

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_ManifestHashMismatch_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed.GenericPlan, "_runManifestContentHash", HashB);

            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_PathSetRootLayoutSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority.FreshSeed.PublicationPaths, "_rootLayout", MakeLayout(99));

            Assert.That(authority.IsValid, Is.False);
        }

        // ---- Exclusive state ----

        [Test]
        public void Authority_BothFieldsNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority, "_recoveryDecision", null);

            Assert.That(authority.Kind, Is.EqualTo(None));
            Assert.That(authority.IsRecovery, Is.False);
            Assert.That(authority.IsFresh, Is.False);
            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Authority_BothFieldsNonNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority, "_freshSeed", MakeSeed(1));

            Assert.That(authority.Kind, Is.EqualTo(None));
            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Recovery_SwapFreshSeedIn_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority, "_freshSeed", MakeSeed(1));

            Assert.That(authority.Kind, Is.EqualTo(None));
            Assert.That(authority.IsRecovery, Is.False);
            Assert.That(authority.IsFresh, Is.False);
            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Fresh_SwapRecoveryDecisionIn_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            SetField(authority, "_recoveryDecision", MakeDecision());

            Assert.That(authority.Kind, Is.EqualTo(None));
            Assert.That(authority.IsRecovery, Is.False);
            Assert.That(authority.IsFresh, Is.False);
            Assert.That(authority.IsValid, Is.False);
        }

        [Test]
        public void Authority_Uninitialized_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                (PngJsonCapturePublicationArtifactInspectionAuthority)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionAuthority));

            Assert.That(authority.Kind, Is.EqualTo(None));
            Assert.That(authority.IsRecovery, Is.False);
            Assert.That(authority.IsFresh, Is.False);
            Assert.That(authority.IsValid, Is.False);
            Assert.That(authority.RecoveryDecision, Is.Null);
            Assert.That(authority.FreshSeed, Is.Null);
            Assert.That(authority.AuthoritativePlan, Is.Null);
            Assert.That(authority.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.None));
            Assert.That(authority.PublicationPaths, Is.Null);
            Assert.That(authority.RootLayout, Is.Null);
            Assert.That(authority.LockLease, Is.Null);
            Assert.That(authority.TestRunId, Is.EqualTo(0L));
            Assert.That(authority.RunInitializationId, Is.Null);
            Assert.That(authority.RunManifestContentSha256, Is.Null);
        }

        // ---- Validation structure ----

        [Test]
        public void Authority_Source_ValidationBoundariesAndNoForbiddenDeps()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactInspectionAuthority.cs"));

            AssertNoForbiddenDependencies(source);

            Assert.That(CountOccurrences(source, "recoveryDecision.IsValid"), Is.EqualTo(2));
            Assert.That(CountOccurrences(source, "freshSeed.IsValid"), Is.EqualTo(2));
            Assert.That(source, Does.Not.Contain("snapshot.IsValid"));
            Assert.That(source, Does.Not.Contain("plan.IsValid"));
            Assert.That(source, Does.Not.Contain("binding.IsValid"));
            Assert.That(source, Does.Not.Contain("genericPlan.IsValid"));
            Assert.That(source, Does.Not.Contain("legacyPlan.IsValid"));
            Assert.That(source, Does.Not.Contain("frozen.IsValid"));
            Assert.That(source, Does.Not.Contain("FrozenPublicationResult.IsValid"));
            Assert.That(source, Does.Not.Contain("new CaptureRunPublicationPathSet"));
            Assert.That(source, Does.Not.Contain("new PngJsonCapturePublicationPlan"));
            Assert.That(source, Does.Not.Contain("new CaptureRunPublicationRecoveryInspectionSnapshot"));
            Assert.That(source, Does.Not.Contain("new CaptureEvidenceFrozenRunPublicationResult"));
        }

        // ---- Scale ----

        [Test]
        public void Fresh_1000Frames_Authority_Valid()
        {
            const int frameCount = 1000;
            long[] frameIds = new long[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(frameIds);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed);

            Assert.That(authority.IsValid, Is.True);
            Assert.That(authority.Kind, Is.EqualTo(FreshFrozenRun));
            Assert.That(authority.AuthoritativePlan.EntryCount, Is.EqualTo(frameCount));
        }
    }
}
