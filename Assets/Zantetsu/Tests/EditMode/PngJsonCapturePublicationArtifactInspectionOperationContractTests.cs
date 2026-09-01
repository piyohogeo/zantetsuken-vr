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
    public class PngJsonCapturePublicationArtifactInspectionOperationContractTests
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

        private static long MaxPng => PngJsonCapturePublicationArtifactInspectionOperation.MaximumAllowedPngByteCount;

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

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationArtifactInspectionOperationContractTests).Assembly.Location);
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

        // ---- Shape ----

        [Test]
        public void Operation_TypeShape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactInspectionOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(3));
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsLiteral || field.IsInitOnly, Is.True, field.Name + " must be a constant or readonly static field.");
            }

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            MethodInfo create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(create, Is.Not.Null);
            Assert.That(create.ReturnType, Is.EqualTo(type));
            ParameterInfo[] parameters = create.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(2));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(PngJsonCapturePublicationArtifactInspectionAuthority)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(long)));
        }

        [Test]
        public void Token_PrivateCtorOnly()
        {
            Type tokenType = typeof(PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken);

            Assert.That(tokenType.IsPublic, Is.False);
            Assert.That(tokenType.IsSealed, Is.True);

            ConstructorInfo[] constructors = tokenType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(tokenType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            Assert.That(tokenType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);

            foreach (FieldInfo field in tokenType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        // ---- Normal construction ----

        [Test]
        public void Recovery_ZeroEntry_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new PngJsonCapturePublicationPlanEntry[0]));
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(0));
        }

        [Test]
        public void Recovery_SingleEntry_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(1));
        }

        [Test]
        public void Recovery_MultipleEntries_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20), MakeEntry(30) }));
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(3));
        }

        [Test]
        public void Fresh_ZeroEntry_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(0));
            Assert.That(operation.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));
        }

        [Test]
        public void Fresh_SingleEntry_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(1));
        }

        [Test]
        public void Fresh_MultipleEntries_Constructs()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1, 2, 3);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(3));
        }

        [Test]
        public void Operation_ForwardsAllValues()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) }));
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);

            Assert.That(ReferenceEquals(operation.Authority, authority), Is.True);
            Assert.That(operation.AuthorityKind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision));
            Assert.That(ReferenceEquals(operation.Plan, authority.AuthoritativePlan), Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(2));
            Assert.That(operation.MaximumPngByteCount, Is.EqualTo(1000));
            Assert.That(operation.MaximumSidecarByteCount, Is.EqualTo(CaptureFramePngArtifactCodec.MaximumCanonicalByteCount));
            Assert.That(operation.MaximumTraceManifestByteCount, Is.EqualTo(TraceRunManifestCodec.MaximumCanonicalByteCount));
            Assert.That(ReferenceEquals(operation.PublicationPaths, authority.PublicationPaths), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, authority.RootLayout), Is.True);
            Assert.That(ReferenceEquals(operation.LockLease, authority.LockLease), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(authority.TestRunId));
            Assert.That(operation.RunInitializationId, Is.EqualTo(authority.RunInitializationId));
            Assert.That(operation.RunManifestContentSha256, Is.EqualTo(authority.RunManifestContentSha256));
        }

        [Test]
        public void PathSets_AscendingAndSameAuthority()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20), MakeEntry(30) }));
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);

            for (int i = 0; i < operation.EntryCount; i++)
            {
                PngJsonCapturePublicationArtifactInspectionPathSet pathSet = operation.GetArtifactPaths(i);
                Assert.That(pathSet.EntryIndex, Is.EqualTo(i));
                Assert.That(ReferenceEquals(pathSet.Authority, authority), Is.True);
            }
        }

        [Test]
        public void Recovery_MatchesExistingOperationPaths()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20), MakeEntry(30) }));
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);
            PngJsonCapturePublicationArtifactInspectionOperation mine = MakeOperation(authority, 1000);
            CaptureRunPublicationArtifactInspectionOperation existing = new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);

            Assert.That(mine.EntryCount, Is.EqualTo(existing.EntryCount));
            for (int i = 0; i < mine.EntryCount; i++)
            {
                CaptureRunPublicationArtifactPathSet e = existing.GetArtifactPaths(i);
                PngJsonCapturePublicationArtifactInspectionPathSet m = mine.GetArtifactPaths(i);

                Assert.That(string.Equals(m.StagingPngPath, e.StagingPngPath, StringComparison.Ordinal), Is.True);
                Assert.That(string.Equals(m.StagingSidecarPath, e.StagingSidecarPath, StringComparison.Ordinal), Is.True);
                Assert.That(string.Equals(m.FinalPngPath, e.FinalPngPath, StringComparison.Ordinal), Is.True);
                Assert.That(string.Equals(m.FinalSidecarPath, e.FinalSidecarPath, StringComparison.Ordinal), Is.True);
            }
        }

        [Test]
        public void PngLimit_ExactlyMaximum()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10, pngByteLength: 1000) }));
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);

            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void SidecarLimit_ExactlyMaximum()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10, sidecarByteLength: CaptureFramePngArtifactCodec.MaximumCanonicalByteCount) }));
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 1000);

            Assert.That(operation.IsValid, Is.True);
        }

        // ---- Rejection ----

        [Test]
        public void NullAuthority_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionOperation.Create(null, 1000));
            Assert.That(ex.ParamName, Is.EqualTo("authority"));
        }

        [Test]
        public void PngLimit_ZeroNegativeOverMax_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();

            Assert.That(MakeOperation(authority, MaxPng).IsValid, Is.True);

            foreach (long bad in new[] { 0L, -1L, MaxPng + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => MakeOperation(authority, bad));
                Assert.That(ex.ParamName, Is.EqualTo("maximumPngByteCount"));
            }
        }

        [Test]
        public void EntryPngExceedsMax_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10, pngByteLength: 1001) }));

            Assert.Throws<ArgumentException>(() => MakeOperation(authority, 1000));
        }

        [Test]
        public void EntrySidecarExceedsMax_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10, sidecarByteLength: CaptureFramePngArtifactCodec.MaximumCanonicalByteCount + 1) }));

            Assert.Throws<ArgumentException>(() => MakeOperation(authority, 1000));
        }

        [Test]
        public void InvalidAuthority_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority bad =
                (PngJsonCapturePublicationArtifactInspectionAuthority)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionAuthority));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => MakeOperation(bad, 1000));
            Assert.That(ex.ParamName, Is.EqualTo("authority"));
        }

        [Test]
        public void LeaseExpired_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            authority.LockLease.Dispose();

            Assert.Throws<ArgumentException>(() => MakeOperation(authority, 1000));
        }

        [Test]
        public void AuthorityExclusiveCorruption_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            SetField(authority, "_freshSeed", MakeSeed(1));

            Assert.That(authority.Kind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.None));
            Assert.Throws<ArgumentException>(() => MakeOperation(authority, 1000));
        }

        // ---- Tamper / token ----

        [Test]
        public void CrossOperationToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operationA = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation operationB = MakeOperation(MakeFreshAuthority(1));

            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken tokenA =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operationA);

            Assert.That(tokenA.IsIndexLocalCorrelated(operationB, 0), Is.False);
        }

        [Test]
        public void StaleToken_LeaseDispose_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.True);

            authority.LockLease.Dispose();

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void ArtifactPathsNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation, "_artifactPaths", null);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
            Assert.That(operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken t2), Is.False);
            Assert.That(t2, Is.Null);
        }

        [Test]
        public void ArtifactPathsShortened_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation, "_artifactPaths", new PngJsonCapturePublicationArtifactInspectionPathSet[1]);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void ArtifactPathsSwappedArray_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation, "_artifactPaths", new PngJsonCapturePublicationArtifactInspectionPathSet[1]);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void ArtifactPathsElementSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactInspectionPathSet[] swapped = new PngJsonCapturePublicationArtifactInspectionPathSet[2];
            swapped[0] = operation.GetArtifactPaths(1);
            swapped[1] = operation.GetArtifactPaths(0);
            SetField(operation, "_artifactPaths", swapped);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void PathSetAuthoritySwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.GetArtifactPaths(0), "_authority", MakeFreshAuthority(1));

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void PathSetEntryIndexSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.GetArtifactPaths(0), "_entryIndex", 999);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void PathSetPathMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.GetArtifactPaths(0), "_stagingPngPath", "C:\\wrong\\a.png");

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void AuthorityRecoveryFreshSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(authority, "_freshSeed", MakeSeed(1));

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void PlanEntryArrayNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.Plan, "_entries", null);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void PlanEntryElementSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationPlanEntry[] swapped = new PngJsonCapturePublicationPlanEntry[2];
            swapped[0] = operation.Plan.GetEntry(1);
            swapped[1] = operation.Plan.GetEntry(0);
            SetField(operation.Plan, "_entries", swapped);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void Entry_ByteLengthMutatedToValidValue_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.GetArtifactPaths(0).Entry, "_pngByteLength", 17L);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void Entry_HashMutatedToValidValue_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.GetArtifactPaths(0).Entry, "_pngContentSha256", HashB);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void TokenProofNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(token, "_proof", null);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void TokenProofShortened_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })));
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(token, "_proof", new PngJsonCapturePublicationArtifactInspectionPathSet[1]);

            Assert.That(token.IsIndexLocalCorrelated(operation, 1), Is.False);
        }

        [Test]
        public void TokenAuthorityTokenNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(token, "_authorityToken", null);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void TokenAuthorityTokenSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken other =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(MakeFreshAuthority(1));
            SetField(token, "_authorityToken", other);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void Token_PngByteCountSwapped_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority(), 1000);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation, "_maximumPngByteCount", 2000L);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void TokenAuthorityTokenSwappedToSameAuthority_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            SetField(operation.GetArtifactPaths(0).Entry, "_pngByteLength", 17L);

            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken freshAuthorityToken =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(operation.Authority);
            SetField(token, "_authorityToken", freshAuthorityToken);

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        [Test]
        public void UninitializedOperation_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation =
                (PngJsonCapturePublicationArtifactInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionOperation));

            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token), Is.False);
            Assert.That(token, Is.Null);
        }

        [Test]
        public void UninitializedToken_False()
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(MakeRecoveryAuthority());
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                (PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken));

            Assert.That(token.IsIndexLocalCorrelated(operation, 0), Is.False);
        }

        // ---- Source shape ----

        [Test]
        public void Source_NoForbiddenDepsAndStructure()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactInspectionOperation.cs"));

            AssertNoForbiddenDependencies(source);

            string createBody = ExtractMethodBody(source, "static PngJsonCapturePublicationArtifactInspectionOperation Create(");

            Assert.That(createBody, Does.Not.Contain("authority.IsValid"));
            Assert.That(createBody, Does.Not.Contain("new PngJsonCapturePublicationArtifactInspectionPathSet("));
            Assert.That(CountOccurrences(createBody, "ValidationToken.TryAcquire("), Is.EqualTo(1));
            Assert.That(CountOccurrences(createBody, "CreateIndexLocal("), Is.GreaterThanOrEqualTo(1));
        }

        // ---- Scale ----

        [Test]
        public void Fresh_1000Entries_Operation()
        {
            const int entryCount = 1000;
            long[] frameIds = new long[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, 2000);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.EntryCount, Is.EqualTo(entryCount));

            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);

            for (int i = 0; i < entryCount; i++)
            {
                Assert.That(token.IsIndexLocalCorrelated(operation, i), Is.True);
            }
        }
    }
}
