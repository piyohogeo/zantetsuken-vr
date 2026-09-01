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
    public class PngJsonCapturePublicationArtifactInspectionSnapshotContractTests
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

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationEvidenceStatus EvInvalid => CaptureRunPublicationEvidenceStatus.Invalid;

        private static CaptureRunPublicationEvidenceStatus EvLimitExceeded => CaptureRunPublicationEvidenceStatus.LimitExceeded;

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

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationArtifactInspectionSnapshotContractTests).Assembly.Location);
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

        private static string ExtractMethodBody(string source, string signatureMarker)
        {
            int start = source.IndexOf(signatureMarker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "Method not found: " + signatureMarker);

            int open = source.IndexOf('{', start);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Opening brace not found for: " + signatureMarker);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Unbalanced braces for method: " + signatureMarker);
            return null;
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

        private static void AssertNoForbiddenDependencies(string source)
        {
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("ComputeHash"));
            Assert.That(source, Does.Not.Contain(".Serialize("));
            Assert.That(source, Does.Not.Contain(".Deserialize("));
            Assert.That(source, Does.Not.Contain(".Encode("));
            Assert.That(source, Does.Not.Contain(".Decode("));
            Assert.That(source, Does.Not.Contain("using System.Linq"));
            Assert.That(source, Does.Not.Contain(".Where("));
            Assert.That(source, Does.Not.Contain(".Select("));
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("Dictionary"));
            Assert.That(source, Does.Not.Contain("HashSet"));
            Assert.That(source, Does.Not.Contain("ToArray"));
            Assert.That(source, Does.Not.Contain("Array.Copy"));
            Assert.That(source, Does.Not.Contain("using UnityEngine"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Notifier"));
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

        private static CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
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
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(PngJsonCapturePublicationPlan plan = null)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(MakeDecision(plan));
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeFreshAuthority(params long[] frameIds)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(MakeSeed(frameIds));
        }

        private static PngJsonCapturePublicationArtifactInspectionOperation MakeOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount = 1000)
        {
            return PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactEntryObservation MakeObservation(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionPathSet paths)
        {
            return PngJsonCapturePublicationArtifactEntryObservation.Create(
                operation, paths, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);
        }

        private static PngJsonCapturePublicationArtifactEntryObservation[] MakeEntries(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token)
        {
            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[operation.EntryCount];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(i),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);
            }

            return entries;
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshot(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            IPngJsonCapturePublicationArtifactInspector issuedBy,
            PngJsonCapturePublicationArtifactEntryObservation[] entries = null,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long traceCount = 0)
        {
            return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                issuedBy, operation, traceStatus, traceCount, entries ?? MakeEntries(
                    operation, PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation)));
        }

        // ---- Interface shape ----

        [Test]
        public void Interface_Shape()
        {
            Type type = typeof(IPngJsonCapturePublicationArtifactInspector);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            MethodInfo[] methods = type.GetMethods();
            Assert.That(methods.Length, Is.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Inspect"));
            Assert.That(methods[0].ReturnType, Is.EqualTo(typeof(PngJsonCapturePublicationArtifactInspectionSnapshot)));

            ParameterInfo[] parameters = methods[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(PngJsonCapturePublicationArtifactInspectionOperation)));
        }

        [Test]
        public void Interface_XmlContractKeywords()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/IPngJsonCapturePublicationArtifactInspector.cs"));

            Assert.That(source, Does.Contain("single-attempt"));
            Assert.That(source, Does.Contain("read-only"));
            Assert.That(source, Does.Contain("no-follow"));
            Assert.That(source, Does.Contain("bounded"));
            Assert.That(source, Does.Contain("ArgumentNullException"));
            Assert.That(source, Does.Contain("ArgumentException"));
            Assert.That(source, Does.Contain("IsIssuedFor"));
        }

        // ---- Snapshot shape ----

        [Test]
        public void Snapshot_TypeShape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactInspectionSnapshot);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(5));
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(property.PropertyType.IsArray, Is.False, property.Name + " must not expose an array.");
            }
        }

        // ---- Normal ----

        [Test]
        public void Recovery_ZeroEntry_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new PngJsonCapturePublicationPlanEntry[0])));
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, new FakeArtifactInspector());

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.Count, Is.EqualTo(0));
        }

        [Test]
        public void Recovery_SingleEntry_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, new FakeArtifactInspector());

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.Count, Is.EqualTo(1));
        }

        [Test]
        public void Recovery_MultipleEntries_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20), MakeEntry(30) })));
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, new FakeArtifactInspector());

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.Count, Is.EqualTo(3));
        }

        [Test]
        public void Fresh_MultipleEntries_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeFreshAuthority(1, 2, 3));
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, new FakeArtifactInspector());

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.Count, Is.EqualTo(3));
            Assert.That(snapshot.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));
        }

        [Test]
        public void Trace_EachValidStatus_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            FakeArtifactInspector issuedBy = new FakeArtifactInspector();
            long limit = operation.MaximumTraceManifestByteCount;

            Assert.That(MakeSnapshot(operation, issuedBy, traceStatus: EvAbsent, traceCount: 0).IsValid, Is.True);
            Assert.That(MakeSnapshot(operation, issuedBy, traceStatus: EvMatchesExpected, traceCount: 1).IsValid, Is.True);
            Assert.That(MakeSnapshot(operation, issuedBy, traceStatus: EvMismatch, traceCount: limit).IsValid, Is.True);
            Assert.That(MakeSnapshot(operation, issuedBy, traceStatus: EvInvalid, traceCount: 0).IsValid, Is.True);
            Assert.That(MakeSnapshot(operation, issuedBy, traceStatus: EvLimitExceeded, traceCount: limit + 1).IsValid, Is.True);
        }

        [Test]
        public void Snapshot_ForwardsAllValues()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            FakeArtifactInspector issuedBy = new FakeArtifactInspector();
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, issuedBy, traceStatus: EvMismatch, traceCount: 5);

            Assert.That(ReferenceEquals(snapshot.IssuedBy, issuedBy), Is.True);
            Assert.That(ReferenceEquals(snapshot.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(snapshot.Authority, operation.Authority), Is.True);
            Assert.That(snapshot.AuthorityKind, Is.EqualTo(operation.AuthorityKind));
            Assert.That(ReferenceEquals(snapshot.Plan, operation.Plan), Is.True);
            Assert.That(snapshot.TraceManifestStatus, Is.EqualTo(EvMismatch));
            Assert.That(snapshot.TraceManifestProbedByteCount, Is.EqualTo(5));
            Assert.That(snapshot.Count, Is.EqualTo(2));
            Assert.That(ReferenceEquals(snapshot.RootLayout, operation.RootLayout), Is.True);
            Assert.That(ReferenceEquals(snapshot.LockLease, operation.LockLease), Is.True);
            Assert.That(snapshot.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(snapshot.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(snapshot.RunManifestContentSha256, Is.EqualTo(operation.RunManifestContentSha256));
        }

        [Test]
        public void GetEntry_TryGetEntry()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, new FakeArtifactInspector());

            Assert.That(snapshot.GetEntry(0).EntryIndex, Is.EqualTo(0));
            Assert.That(snapshot.GetEntry(1).EntryIndex, Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetEntry(2));

            Assert.That(snapshot.TryGetEntry(1, out PngJsonCapturePublicationArtifactEntryObservation one), Is.True);
            Assert.That(one.EntryIndex, Is.EqualTo(1));
            Assert.That(snapshot.TryGetEntry(2, out _), Is.False);
        }

        [Test]
        public void DefensiveCopy_CallerArrayMutation_DoesNotAffectSnapshot()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactEntryObservation[] entries = MakeEntries(
                operation, PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation));

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot =
                PngJsonCapturePublicationArtifactInspectionSnapshot.Create(new FakeArtifactInspector(), operation, EvAbsent, 0, entries);

            PngJsonCapturePublicationArtifactEntryObservation original = entries[0];
            entries[0] = null;

            Assert.That(ReferenceEquals(snapshot.GetEntry(0), original), Is.True);
        }

        [Test]
        public void IsIssuedFor_Normal()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            FakeArtifactInspector issuedBy = new FakeArtifactInspector();
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, issuedBy);

            Assert.That(snapshot.IsIssuedFor(issuedBy, operation), Is.True);
            Assert.That(snapshot.IsIssuedFor(new FakeArtifactInspector(), operation), Is.False);
            Assert.That(snapshot.IsIssuedFor(issuedBy, MakeOperation(MakeRecoveryAuthority())), Is.False);
        }

        // ---- Trace status table ----

        private static void AssertTraceStatusTable(
            Func<CaptureRunPublicationEvidenceStatus, long, PngJsonCapturePublicationArtifactInspectionSnapshot> build,
            long limit)
        {
            Assert.That(build(EvAbsent, 0).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => build(EvAbsent, -1));
            Assert.Throws<ArgumentException>(() => build(EvAbsent, 1));

            Assert.That(build(EvMatchesExpected, 1).IsValid, Is.True);
            Assert.That(build(EvMatchesExpected, limit).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => build(EvMatchesExpected, 0));
            Assert.Throws<ArgumentException>(() => build(EvMatchesExpected, limit + 1));

            Assert.That(build(EvMismatch, 1).IsValid, Is.True);
            Assert.That(build(EvMismatch, limit).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => build(EvMismatch, 0));
            Assert.Throws<ArgumentException>(() => build(EvMismatch, limit + 1));

            Assert.That(build(EvInvalid, 0).IsValid, Is.True);
            Assert.That(build(EvInvalid, limit).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => build(EvInvalid, -1));
            Assert.Throws<ArgumentException>(() => build(EvInvalid, limit + 1));

            Assert.That(build(EvLimitExceeded, limit + 1).IsValid, Is.True);
            Assert.Throws<ArgumentException>(() => build(EvLimitExceeded, limit));

            Assert.Throws<ArgumentOutOfRangeException>(() => build(CaptureRunPublicationEvidenceStatus.None, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => build((CaptureRunPublicationEvidenceStatus)999, 0));
        }

        [Test]
        public void Trace_StatusTable()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            FakeArtifactInspector issuedBy = new FakeArtifactInspector();
            PngJsonCapturePublicationArtifactEntryObservation[] entries = MakeEntries(
                operation, PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation));
            long limit = operation.MaximumTraceManifestByteCount;

            AssertTraceStatusTable(
                (s, c) => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(issuedBy, operation, s, c, entries),
                limit);
        }

        // ---- Rejection ----

        [Test]
        public void NullIssuer_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                    null, operation, EvAbsent, 0,
                    MakeEntries(operation, PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation))));
            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                    new FakeArtifactInspector(), null, EvAbsent, 0, new PngJsonCapturePublicationArtifactEntryObservation[0]));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void NullEntries_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                    new FakeArtifactInspector(), operation, EvAbsent, 0, null));
            Assert.That(ex.ParamName, Is.EqualTo("entries"));
        }

        [Test]
        public void InvalidOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation bad =
                (PngJsonCapturePublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                    new FakeArtifactInspector(), bad, EvAbsent, 0, new PngJsonCapturePublicationArtifactEntryObservation[0]));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void EntryCountMismatch_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactEntryObservation[] tooFew =
                new PngJsonCapturePublicationArtifactEntryObservation[1];
            tooFew[0] = PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                token, operation, operation.GetArtifactPaths(0), EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(new FakeArtifactInspector(), operation, EvAbsent, 0, tooFew));
        }

        [Test]
        public void NullEntry_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                    new FakeArtifactInspector(), operation, EvAbsent, 0,
                    new PngJsonCapturePublicationArtifactEntryObservation[] { null }));
        }

        [Test]
        public void ForeignOperationObservation_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operationA = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation operationB = MakeOperation(MakeFreshAuthority(1));

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operationA, new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactEntryObservation foreign = MakeObservation(operationB, operationB.GetArtifactPaths(0));

            Assert.That(snapshot.TryGetEntry(0, out _), Is.True);
            PngJsonCapturePublicationArtifactEntryObservation[] entries = { foreign };
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(new FakeArtifactInspector(), operationA, EvAbsent, 0, entries));
        }

        [Test]
        public void EntryOrderSwapped_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation[] entries = MakeEntries(operation, token);

            PngJsonCapturePublicationArtifactEntryObservation[] swapped = { entries[1], entries[0] };

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(new FakeArtifactInspector(), operation, EvAbsent, 0, swapped));
        }

        [Test]
        public void StaleLease_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);
            PngJsonCapturePublicationArtifactEntryObservation[] entries = MakeEntries(
                operation, PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation));

            authority.LockLease.Dispose();

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionSnapshot.Create(new FakeArtifactInspector(), operation, EvAbsent, 0, entries));
        }

        // ---- Tamper / token ----

        [Test]
        public void CrossSnapshotToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshotA = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshotB = MakeSnapshot(
                MakeOperation(MakeFreshAuthority(1)), new FakeArtifactInspector());

            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken tokenA =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshotA);

            Assert.That(tokenA.IsIndexLocalCorrelated(snapshotB, 0), Is.False);
        }

        [Test]
        public void IssuedBySwapped_False()
        {
            IPngJsonCapturePublicationArtifactInspector issuedBy = new FakeArtifactInspector();
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), issuedBy);
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot, "_issuedBy", new FakeArtifactInspector());

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
            Assert.That(snapshot.IsIssuedFor(issuedBy, snapshot.Operation), Is.False);
        }

        [Test]
        public void OperationSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot, "_operation", MakeOperation(MakeFreshAuthority(1)));

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void TraceStatusMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector(), traceStatus: EvAbsent, traceCount: 0);
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot, "_traceManifestStatus", EvMismatch);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void TraceCountMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector(), traceStatus: EvMismatch, traceCount: 5);
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot, "_traceManifestProbedByteCount", 6L);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void EntriesNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot, "_entries", null);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
            Assert.That(snapshot.IsValid, Is.False);
        }

        [Test]
        public void EntriesElementSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(operation, new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            PngJsonCapturePublicationArtifactEntryObservation[] swapped =
            {
                snapshot.GetEntry(1),
                snapshot.GetEntry(0)
            };
            SetField(snapshot, "_entries", swapped);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void ObservationOperationSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot.GetEntry(0), "_operation", MakeOperation(MakeFreshAuthority(1)));

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void ObservationPathSetSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken authorityToken =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(snapshot.Authority);
            PngJsonCapturePublicationArtifactInspectionPathSet other =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(authorityToken, snapshot.Authority, 0);
            SetField(snapshot.GetEntry(0), "_artifactPaths", other);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void ObservationStatusMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot.GetEntry(0), "_stagingPngStatus", EvMatchesExpected);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void ObservationCountMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(snapshot.GetEntry(0), "_stagingPngProbedByteCount", 1L);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void TokenProofNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(token, "_proof", null);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void TokenOperationTokenNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            SetField(token, "_operationToken", null);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void TokenOperationTokenSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken fresh =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(snapshot.Operation);
            SetField(token, "_operationToken", fresh);

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void UninitializedSnapshot_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot =
                (PngJsonCapturePublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionSnapshot));

            Assert.That(snapshot.IsValid, Is.False);
            Assert.That(snapshot.TryValidate(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token), Is.False);
            Assert.That(token, Is.Null);
        }

        [Test]
        public void UninitializedToken_False()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                (PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken));

            Assert.That(token.IsIndexLocalCorrelated(snapshot, 0), Is.False);
        }

        [Test]
        public void TryValidate_Failure_TokenNull()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshot(
                MakeOperation(MakeRecoveryAuthority()), new FakeArtifactInspector());
            SetField(snapshot, "_entries", null);

            Assert.That(snapshot.TryValidate(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token), Is.False);
            Assert.That(token, Is.Null);
        }

        // ---- Source shape ----

        [Test]
        public void Source_NoForbiddenDepsAndStructure()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactInspectionSnapshot.cs"));

            AssertNoForbiddenDependencies(source);

            string createBody = ExtractMethodBody(source, "static PngJsonCapturePublicationArtifactInspectionSnapshot Create(");

            Assert.That(CountOccurrences(createBody, "TryValidate("), Is.EqualTo(1));
            Assert.That(CountOccurrences(createBody, "for (int i = 0; i < entries.Length; i++)"), Is.EqualTo(1));
            Assert.That(CountOccurrences(createBody, "entries[i]"), Is.EqualTo(1));
            Assert.That(CountOccurrences(createBody, "copy[i] = entry"), Is.EqualTo(1));
            Assert.That(source, Does.Not.Contain("new PngJsonCapturePublicationArtifactEntryObservation("));
        }

        // ---- Scale ----

        [Test]
        public void Fresh_1000Entries_Snapshot()
        {
            const int entryCount = 1000;
            long[] frameIds = new long[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 2000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken operationToken =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactEntryObservation[] entries = MakeEntries(operation, operationToken);

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot =
                PngJsonCapturePublicationArtifactInspectionSnapshot.Create(new FakeArtifactInspector(), operation, EvAbsent, 0, entries);

            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(snapshot.Count, Is.EqualTo(entryCount));

            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);

            for (int i = 0; i < entryCount; i++)
            {
                Assert.That(token.IsIndexLocalCorrelated(snapshot, i), Is.True);
                Assert.That(snapshot.GetEntry(i).EntryIndex, Is.EqualTo(i));
                Assert.That(snapshot.GetEntry(i).CaptureFrameId, Is.EqualTo(i + 1));
                Assert.That(snapshot.GetEntry(i).StagingPngStatus, Is.EqualTo(EvAbsent));
                Assert.That(snapshot.GetEntry(i).StagingPngProbedByteCount, Is.EqualTo(0));
            }
        }
    }
}
