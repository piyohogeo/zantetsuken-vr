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
    public class PngJsonCapturePublicationArtifactEntryObservationContractTests
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

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationArtifactEntryObservationContractTests).Assembly.Location);
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

        private static void AssertCountParamName(TestDelegate action, string paramName)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(action);
            Assert.That(ex.ParamName, Is.EqualTo(paramName));
        }

        private static void AssertStatusParamName(TestDelegate action, string paramName)
        {
            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(action);
            Assert.That(ex.ParamName, Is.EqualTo(paramName));
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
            Assert.That(source, Does.Not.Contain("Inspector"));
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
            PngJsonCapturePublicationArtifactInspectionPathSet paths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingPngCount = 0,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingSidecarCount = 0,
            CaptureRunPublicationEvidenceStatus finalPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalPngCount = 0,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalSidecarCount = 0)
        {
            return PngJsonCapturePublicationArtifactEntryObservation.Create(
                operation,
                paths,
                stagingPngStatus,
                stagingPngCount,
                stagingSidecarStatus,
                stagingSidecarCount,
                finalPngStatus,
                finalPngCount,
                finalSidecarStatus,
                finalSidecarCount);
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
        }

        // ---- Shape ----

        [Test]
        public void Observation_TypeShape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactEntryObservation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(10));
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        // ---- Normal ----

        [Test]
        public void Recovery_Absent_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(operation, operation.GetArtifactPaths(0));

            Assert.That(observation.IsValid, Is.True);
            Assert.That(observation.StagingPngStatus, Is.EqualTo(EvAbsent));
            Assert.That(observation.StagingPngProbedByteCount, Is.EqualTo(0));
        }

        [Test]
        public void Fresh_Absent_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeFreshAuthority(1, 2));
            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(operation, operation.GetArtifactPaths(1));

            Assert.That(observation.IsValid, Is.True);
            Assert.That(observation.CaptureFrameId, Is.EqualTo(2));
        }

        [Test]
        public void Recovery_MatchesExpected_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            long png = paths.Entry.PngByteLength;
            long sidecar = paths.Entry.SidecarByteLength;

            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(
                operation, paths,
                EvMatchesExpected, png,
                EvMatchesExpected, sidecar,
                EvMatchesExpected, png,
                EvMatchesExpected, sidecar);

            Assert.That(observation.IsValid, Is.True);
        }

        [Test]
        public void Mixed_Statuses_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            long png = paths.Entry.PngByteLength;
            long sidecar = paths.Entry.SidecarByteLength;

            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(
                operation, paths,
                EvAbsent, 0,
                EvMatchesExpected, sidecar,
                EvMismatch, png - 1,
                EvInvalid, 0);

            Assert.That(observation.IsValid, Is.True);
            Assert.That(observation.StagingPngStatus, Is.EqualTo(EvAbsent));
            Assert.That(observation.StagingSidecarStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.FinalPngStatus, Is.EqualTo(EvMismatch));
            Assert.That(observation.FinalSidecarStatus, Is.EqualTo(EvInvalid));
        }

        [Test]
        public void Observation_ForwardsAllValues()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(
                MakeRecoveryAuthority(MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(1);
            long png = paths.Entry.PngByteLength;
            long sidecar = paths.Entry.SidecarByteLength;

            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(
                operation, paths,
                EvMatchesExpected, png,
                EvMatchesExpected, sidecar,
                EvMatchesExpected, png,
                EvMatchesExpected, sidecar);

            Assert.That(ReferenceEquals(observation.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(observation.ArtifactPaths, paths), Is.True);
            Assert.That(observation.EntryIndex, Is.EqualTo(1));
            Assert.That(observation.CaptureFrameId, Is.EqualTo(20));
            Assert.That(observation.StagingPngStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.StagingPngProbedByteCount, Is.EqualTo(png));
            Assert.That(observation.StagingSidecarStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.StagingSidecarProbedByteCount, Is.EqualTo(sidecar));
            Assert.That(observation.FinalPngStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.FinalPngProbedByteCount, Is.EqualTo(png));
            Assert.That(observation.FinalSidecarStatus, Is.EqualTo(EvMatchesExpected));
            Assert.That(observation.FinalSidecarProbedByteCount, Is.EqualTo(sidecar));
        }

        // ---- Status table ----

        private static void AssertArtifactStatusTable(
            Func<CaptureRunPublicationEvidenceStatus, long, PngJsonCapturePublicationArtifactEntryObservation> build,
            long expected,
            long limit,
            string statusParamName,
            string countParamName)
        {
            Assert.That(build(EvAbsent, 0).IsValid, Is.True);
            AssertCountParamName(() => build(EvAbsent, -1), countParamName);
            AssertCountParamName(() => build(EvAbsent, 1), countParamName);

            Assert.That(build(EvMatchesExpected, expected).IsValid, Is.True);
            AssertCountParamName(() => build(EvMatchesExpected, expected - 1), countParamName);
            AssertCountParamName(() => build(EvMatchesExpected, expected + 1), countParamName);

            Assert.That(build(EvMismatch, 1).IsValid, Is.True);
            Assert.That(build(EvMismatch, limit).IsValid, Is.True);
            AssertCountParamName(() => build(EvMismatch, 0), countParamName);
            AssertCountParamName(() => build(EvMismatch, limit + 1), countParamName);

            Assert.That(build(EvInvalid, 0).IsValid, Is.True);
            Assert.That(build(EvInvalid, limit).IsValid, Is.True);
            AssertCountParamName(() => build(EvInvalid, -1), countParamName);
            AssertCountParamName(() => build(EvInvalid, limit + 1), countParamName);

            Assert.That(build(EvLimitExceeded, limit + 1).IsValid, Is.True);
            AssertCountParamName(() => build(EvLimitExceeded, limit), countParamName);
            AssertCountParamName(() => build(EvLimitExceeded, limit + 2), countParamName);

            AssertStatusParamName(() => build(CaptureRunPublicationEvidenceStatus.None, 0), statusParamName);
            AssertStatusParamName(() => build((CaptureRunPublicationEvidenceStatus)999, 0), statusParamName);
            AssertStatusParamName(() => build((CaptureRunPublicationEvidenceStatus)(-1), 0), statusParamName);
        }

        [Test]
        public void StagingPng_StatusTable()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            long expected = paths.Entry.PngByteLength;
            long limit = Min(expected, operation.MaximumPngByteCount);

            AssertArtifactStatusTable(
                (s, c) => MakeObservation(operation, paths, stagingPngStatus: s, stagingPngCount: c),
                expected, limit, "stagingPngStatus", "stagingPngProbedByteCount");
        }

        [Test]
        public void StagingSidecar_StatusTable()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            long expected = paths.Entry.SidecarByteLength;
            long limit = Min(expected, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

            AssertArtifactStatusTable(
                (s, c) => MakeObservation(operation, paths, stagingSidecarStatus: s, stagingSidecarCount: c),
                expected, limit, "stagingSidecarStatus", "stagingSidecarProbedByteCount");
        }

        [Test]
        public void FinalPng_StatusTable()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            long expected = paths.Entry.PngByteLength;
            long limit = Min(expected, operation.MaximumPngByteCount);

            AssertArtifactStatusTable(
                (s, c) => MakeObservation(operation, paths, finalPngStatus: s, finalPngCount: c),
                expected, limit, "finalPngStatus", "finalPngProbedByteCount");
        }

        [Test]
        public void FinalSidecar_StatusTable()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            long expected = paths.Entry.SidecarByteLength;
            long limit = Min(expected, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

            AssertArtifactStatusTable(
                (s, c) => MakeObservation(operation, paths, finalSidecarStatus: s, finalSidecarCount: c),
                expected, limit, "finalSidecarStatus", "finalSidecarProbedByteCount");
        }

        // ---- Rejection ----

        [Test]
        public void NullOperation_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeObservation(null, operation.GetArtifactPaths(0)));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void NullArtifactPaths_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeObservation(operation, null));
            Assert.That(ex.ParamName, Is.EqualTo("artifactPaths"));
        }

        [Test]
        public void NullToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    null, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void CrossOperationToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operationA = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation operationB = MakeOperation(MakeFreshAuthority(1));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken tokenA =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operationA);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    tokenA, operationB, operationB.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0));
        }

        [Test]
        public void StaleToken_LeaseDispose_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);
            Assert.That(observation.IsValidIndexLocal(token), Is.True);

            authority.LockLease.Dispose();

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0));
        }

        [Test]
        public void ForeignAuthorityPathSet_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionPathSet foreign = MakeOperation(MakeFreshAuthority(1)).GetArtifactPaths(0);

            Assert.Throws<ArgumentException>(() => MakeObservation(operation, foreign));
        }

        [Test]
        public void DifferentOperationPathSet_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operationA = MakeOperation(authority);
            PngJsonCapturePublicationArtifactInspectionOperation operationB = MakeOperation(authority);

            Assert.Throws<ArgumentException>(() => MakeObservation(operationA, operationB.GetArtifactPaths(0)));
        }

        [Test]
        public void DifferentInstancePathSet_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken authorityToken =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet other =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(authorityToken, authority, 0);

            Assert.Throws<ArgumentException>(() => MakeObservation(operation, other));
        }

        // ---- Tamper ----

        [Test]
        public void EntryIndexMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, paths, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(paths, "_entryIndex", 999);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void PathSetPathMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, paths, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(paths, "_stagingPngPath", "C:\\wrong\\a.png");

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void OperationPngByteCountMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(), 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(operation, "_maximumPngByteCount", 2000L);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void OperationArtifactPathsNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(operation, "_artifactPaths", null);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void OperationArtifactPathsElementSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            PngJsonCapturePublicationArtifactInspectionPathSet[] swapped = new PngJsonCapturePublicationArtifactInspectionPathSet[2];
            swapped[0] = operation.GetArtifactPaths(1);
            swapped[1] = operation.GetArtifactPaths(0);
            SetField(operation, "_artifactPaths", swapped);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void AuthorityRecoveryFreshSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(authority, "_freshSeed", MakeSeed(1));

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Entry_ValidValueMutation_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(0);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, paths, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(paths.Entry, "_pngByteLength", 17L);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void TokenProofNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(token, "_proof", null);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void TokenAuthorityTokenSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                    token, operation, operation.GetArtifactPaths(0),
                    EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken freshAuthorityToken =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(operation.Authority);
            SetField(token, "_authorityToken", freshAuthorityToken);

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void ObservationFieldMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(operation, operation.GetArtifactPaths(0));

            SetField(observation, "_stagingPngProbedByteCount", 1L);

            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void ObservationStatusMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(operation, operation.GetArtifactPaths(0));

            SetField(observation, "_stagingPngStatus", CaptureRunPublicationEvidenceStatus.None);

            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void ObservationOperationSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactEntryObservation observation = MakeObservation(operation, operation.GetArtifactPaths(0));

            SetField(observation, "_operation", MakeOperation(MakeFreshAuthority(1)));

            Assert.That(observation.IsValid, Is.False);
        }

        [Test]
        public void UninitializedObservation_False()
        {
            PngJsonCapturePublicationArtifactEntryObservation observation =
                (PngJsonCapturePublicationArtifactEntryObservation)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactEntryObservation));

            Assert.That(observation.IsValid, Is.False);
            Assert.That(observation.IsValidIndexLocal(null), Is.False);
        }

        [Test]
        public void ForgedOperation_NoException()
        {
            PngJsonCapturePublicationArtifactInspectionOperation valid = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(valid);

            PngJsonCapturePublicationArtifactEntryObservation observation =
                PngJsonCapturePublicationArtifactEntryObservation.Create(
                    valid, valid.GetArtifactPaths(0), EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

            SetField(observation, "_operation",
                (PngJsonCapturePublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionOperation)));

            Assert.That(observation.IsValidIndexLocal(token), Is.False);
        }

        // ---- Source shape ----

        [Test]
        public void Source_NoForbiddenDepsAndStructure()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactEntryObservation.cs"));

            AssertNoForbiddenDependencies(source);

            string createBody = ExtractMethodBody(source, "static PngJsonCapturePublicationArtifactEntryObservation Create(");
            string createIndexLocalBody = ExtractMethodBody(source, "static PngJsonCapturePublicationArtifactEntryObservation CreateIndexLocal(");

            Assert.That(CountOccurrences(createBody, "TryValidate("), Is.EqualTo(1));
            Assert.That(createBody, Does.Not.Contain("IsValid"));
            Assert.That(CountOccurrences(createBody, "CreateIndexLocal("), Is.EqualTo(1));

            Assert.That(createIndexLocalBody, Does.Not.Contain("TryValidate("));
            Assert.That(createIndexLocalBody, Does.Not.Contain(".IsValid"));
            Assert.That(createIndexLocalBody, Does.Not.Contain("Acquire("));
        }

        // ---- Scale ----

        [Test]
        public void Fresh_1000Entries_Observation()
        {
            const int entryCount = 1000;
            long[] frameIds = new long[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 2000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            for (int i = 0; i < entryCount; i++)
            {
                PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(i);
                PngJsonCapturePublicationArtifactEntryObservation observation =
                    PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                        token, operation, paths,
                        EvAbsent, 0, EvAbsent, 0, EvAbsent, 0, EvAbsent, 0);

                Assert.That(observation.IsValidIndexLocal(token), Is.True);
                Assert.That(observation.EntryIndex, Is.EqualTo(i));
                Assert.That(observation.CaptureFrameId, Is.EqualTo(i + 1));
                Assert.That(observation.StagingPngStatus, Is.EqualTo(EvAbsent));
                Assert.That(observation.StagingPngProbedByteCount, Is.EqualTo(0));
            }
        }
    }
}
