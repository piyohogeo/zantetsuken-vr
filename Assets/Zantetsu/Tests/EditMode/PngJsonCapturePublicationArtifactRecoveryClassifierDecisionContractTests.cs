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
    public class PngJsonCapturePublicationArtifactRecoveryClassifierDecisionContractTests
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

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

        private static CaptureRunPublicationEvidenceStatus EvInvalid => CaptureRunPublicationEvidenceStatus.Invalid;

        private static CaptureRunPublicationEvidenceStatus EvLimitExceeded => CaptureRunPublicationEvidenceStatus.LimitExceeded;

        private static CaptureRunPublicationArtifactRecoveryDisposition OrphanedPreTrace => CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishMissingArtifacts => CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;

        private static CaptureRunPublicationArtifactRecoveryDisposition CommitCaptureIndex => CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryDisposition CaptureComplete => CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;

        private static CaptureRunPublicationArtifactRecoveryDisposition ArtifactSourceMissing => CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishedArtifactMissing => CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition RunRootCollision => CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;

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

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationArtifactRecoveryClassifierDecisionContractTests).Assembly.Location);
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

        private static void AssertEnumContract(Type type, string[] expectedNames)
        {
            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetNames(type), Is.EqualTo(expectedNames));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(expectedNames.Length));
            for (int i = 0; i < expectedNames.Length; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
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
            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        private sealed class LegacyFakeArtifactInspector : ICaptureRunPublicationArtifactInspector
        {
            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
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

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
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

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
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
            bool indexAuthoritative = false,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null)
        {
            plan = plan ?? MakePlan();
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan = null,
            bool indexAuthoritative = false,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(plan, indexAuthoritative, captureIndexTemporary));
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

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotArray(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar,
            long maximumPngByteCount = 1000)
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, maximumPngByteCount);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            Assert.That(operation.EntryCount, Is.EqualTo(stagingPng.Length));

            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[stagingPng.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(token, operation, i, stagingPng[i], stagingSidecar[i], finalPng[i], finalSidecar[i]);
            }

            return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                new FakeArtifactInspector(), operation, traceStatus, traceCount, entries);
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotSingle(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar,
            long maximumPngByteCount = 1000)
        {
            return MakeSnapshotArray(
                authority,
                traceStatus,
                traceCount,
                new[] { stagingPng },
                new[] { stagingSidecar },
                new[] { finalPng },
                new[] { finalSidecar },
                maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactRecoveryDecision ClassifyDecision(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
        {
            return PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(snapshot);
        }

        private static CaptureRunPublicationRecoveryDecision MakeLegacyDecision(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative)
        {
            plan = plan ?? MakePlan();
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private static CaptureRunPublicationArtifactEntryObservation MakeLegacyEntryObservation(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar)
        {
            PngJsonCapturePublicationPlanEntry entry = artifactPaths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, operation.MaximumSidecarByteCount);
            return new CaptureRunPublicationArtifactEntryObservation(
                operation,
                artifactPaths,
                stagingPng, Probe(stagingPng, entry.PngByteLength, pngLimit),
                stagingSidecar, Probe(stagingSidecar, entry.SidecarByteLength, sidecarLimit),
                finalPng, Probe(finalPng, entry.PngByteLength, pngLimit),
                finalSidecar, Probe(finalSidecar, entry.SidecarByteLength, sidecarLimit));
        }

        private static void AssertParity(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar)
        {
            CaptureRunPublicationRecoveryDecision decision = MakeLegacyDecision(plan, indexAuthoritative);

            CaptureRunPublicationArtifactInspectionOperation oldOperation =
                new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
            int count = oldOperation.EntryCount;
            CaptureRunPublicationArtifactEntryObservation[] oldEntries = new CaptureRunPublicationArtifactEntryObservation[count];
            for (int i = 0; i < count; i++)
            {
                oldEntries[i] = MakeLegacyEntryObservation(oldOperation, oldOperation.GetArtifactPaths(i),
                    stagingPng[i], stagingSidecar[i], finalPng[i], finalSidecar[i]);
            }

            CaptureRunPublicationArtifactInspectionSnapshot oldSnapshot = new CaptureRunPublicationArtifactInspectionSnapshot(
                new LegacyFakeArtifactInspector(), oldOperation, traceStatus, traceCount, oldEntries);
            CaptureRunPublicationArtifactRecoveryDisposition oldDisposition =
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(oldSnapshot).Disposition;

            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);
            PngJsonCapturePublicationArtifactInspectionSnapshot newSnapshot = MakeSnapshotArray(
                authority, traceStatus, traceCount, stagingPng, stagingSidecar, finalPng, finalSidecar);
            CaptureRunPublicationArtifactRecoveryDisposition newDisposition =
                PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(newSnapshot).Disposition;

            Assert.That(newDisposition, Is.EqualTo(oldDisposition));
        }

        // ---- Exception ----

        [Test]
        public void Classify_NullSnapshot_ThrowsArgumentNullException()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(null));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        [Test]
        public void Classify_InvalidSnapshot_ThrowsArgumentException()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent);
            SetField(snapshot, "_entries", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(snapshot));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        // ---- Enum ----

        [Test]
        public void DispositionEnum_ValuesUnchanged()
        {
            AssertEnumContract(typeof(CaptureRunPublicationArtifactRecoveryDisposition),
                new[]
                {
                    "None", "OrphanedPreTrace", "PublishMissingArtifacts", "CommitCaptureIndex",
                    "CaptureComplete", "ArtifactSourceMissing", "PublishedArtifactMissing", "RunRootCollision"
                });
        }

        // ---- Recovery classifications ----

        [Test]
        public void Recovery_TraceAbsent_OrphanedPreTrace()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(OrphanedPreTrace));
        }

        [Test]
        public void Recovery_TraceAbsent_CaptureIndexTemporaryCanonical_RunRootCollision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(plan, captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan)),
                EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Recovery_TraceMatchesExpected_AllFinalMatch_CommitCaptureIndex()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Recovery_TraceMatchesExpected_Publishable_PublishMissingArtifacts()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(PublishMissingArtifacts));
        }

        [Test]
        public void Recovery_TraceMatchesExpected_SourceMissing_ArtifactSourceMissing()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(ArtifactSourceMissing));
        }

        [Test]
        public void Recovery_CaptureIndexAuthoritative_AllFinalMatch_CaptureComplete()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(MakePlan(), indexAuthoritative: true),
                EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(CaptureComplete));
        }

        [Test]
        public void Recovery_CaptureIndexAuthoritative_FinalMissing_PublishedArtifactMissing()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(MakePlan(), indexAuthoritative: true),
                EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(PublishedArtifactMissing));
        }

        [Test]
        public void Recovery_Anomaly_RunRootCollision()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMismatch, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(RunRootCollision));
        }

        // ---- Fresh classifications ----

        [Test]
        public void Fresh_TraceAbsent_OrphanedPreTrace()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeFreshAuthority(10), EvAbsent, 0, EvAbsent, EvAbsent, EvAbsent, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(OrphanedPreTrace));
        }

        [Test]
        public void Fresh_TraceMatchesExpected_AllFinalMatch_CommitCaptureIndex()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeFreshAuthority(10), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Fresh_TraceMatchesExpected_Publishable_PublishMissingArtifacts()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeFreshAuthority(10), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(PublishMissingArtifacts));
        }

        [Test]
        public void Fresh_TraceMatchesExpected_SourceMissing_ArtifactSourceMissing()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeFreshAuthority(10), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(ArtifactSourceMissing));
        }

        [Test]
        public void Fresh_Anomaly_RunRootCollision()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeFreshAuthority(10), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvInvalid, EvAbsent);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Fresh_DispositionAlwaysPublicationPlanAuthoritative()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(10);
            Assert.That(authority.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
            Assert.That(authority.Kind, Is.EqualTo(PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun));
        }

        // ---- PNG / Sidecar independent judgment and priority ----

        [Test]
        public void PngPublishable_SidecarComplete_PublishMissingArtifacts()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvMatchesExpected, EvAbsent, EvAbsent, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(PublishMissingArtifacts));
        }

        [Test]
        public void PngSourceMissing_SidecarComplete_ArtifactSourceMissing()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvAbsent, EvMatchesExpected);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(ArtifactSourceMissing));
        }

        [Test]
        public void SourceMissing_Beats_Publishable()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) });
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeRecoveryAuthority(plan), EvMatchesExpected, 1,
                new[] { EvAbsent, EvMatchesExpected },
                new[] { EvAbsent, EvAbsent },
                new[] { EvAbsent, EvAbsent },
                new[] { EvAbsent, EvAbsent });

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(ArtifactSourceMissing));
        }

        [Test]
        public void Anomaly_Beats_OtherResults()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) });
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeRecoveryAuthority(plan), EvMatchesExpected, 1,
                new[] { EvAbsent, EvMismatch },
                new[] { EvAbsent, EvAbsent },
                new[] { EvAbsent, EvAbsent },
                new[] { EvAbsent, EvAbsent });

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(RunRootCollision));
        }

        // ---- Scale ----

        [Test]
        public void Recovery_ZeroEntry_TraceAbsent_OrphanedPreTrace()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeRecoveryAuthority(MakePlan(entries: new PngJsonCapturePublicationPlanEntry[0])),
                EvAbsent, 0,
                new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0]);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(OrphanedPreTrace));
        }

        [Test]
        public void Recovery_ZeroEntry_TraceMatchesExpected_CommitCaptureIndex()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeRecoveryAuthority(MakePlan(entries: new PngJsonCapturePublicationPlanEntry[0])),
                EvMatchesExpected, 1,
                new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0],
                new CaptureRunPublicationEvidenceStatus[0]);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Fresh_1000Entries_TraceMatchesExpected_CommitCaptureIndex()
        {
            long[] frameIds = new long[1000];
            for (int i = 0; i < frameIds.Length; i++)
            {
                frameIds[i] = i + 1;
            }

            CaptureRunPublicationEvidenceStatus[] staging = new CaptureRunPublicationEvidenceStatus[1000];
            CaptureRunPublicationEvidenceStatus[] final = new CaptureRunPublicationEvidenceStatus[1000];
            for (int i = 0; i < 1000; i++)
            {
                staging[i] = EvAbsent;
                final[i] = EvMatchesExpected;
            }

            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeFreshAuthority(frameIds), EvMatchesExpected, 1, staging, staging, final, final, maximumPngByteCount: 2000);

            Assert.That(ClassifyDecision(snapshot).Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        // ---- Decision forwarding ----

        [Test]
        public void Decision_Forwarding()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);

            Assert.That(ReferenceEquals(decision.Snapshot, snapshot), Is.True);
            Assert.That(ReferenceEquals(decision.Operation, snapshot.Operation), Is.True);
            Assert.That(ReferenceEquals(decision.Authority, snapshot.Authority), Is.True);
            Assert.That(decision.AuthorityKind, Is.EqualTo(snapshot.AuthorityKind));
            Assert.That(ReferenceEquals(decision.AuthoritativePlan, snapshot.Plan), Is.True);
            Assert.That(ReferenceEquals(decision.RootLayout, snapshot.RootLayout), Is.True);
            Assert.That(ReferenceEquals(decision.LockLease, snapshot.LockLease), Is.True);
            Assert.That(decision.TestRunId, Is.EqualTo(snapshot.TestRunId));
            Assert.That(decision.RunInitializationId, Is.EqualTo(snapshot.RunInitializationId));
            Assert.That(decision.RunManifestContentSha256, Is.EqualTo(snapshot.RunManifestContentSha256));
            Assert.That(decision.Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Decision_DispositionMatchesClassifier()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);

            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        // ---- Tamper / IsValid ----

        private static PngJsonCapturePublicationArtifactRecoveryDecision MakeCommitDecision()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            return ClassifyDecision(snapshot);
        }

        [Test]
        public void Decision_InvalidAfterSnapshotEntriesNull()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = MakeCommitDecision();
            SetField(decision.Snapshot, "_entries", null);

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_InvalidAfterEntryArraySwap()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) });
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotArray(
                MakeRecoveryAuthority(plan), EvMatchesExpected, 1,
                new[] { EvAbsent, EvAbsent },
                new[] { EvAbsent, EvAbsent },
                new[] { EvMatchesExpected, EvMatchesExpected },
                new[] { EvMatchesExpected, EvMatchesExpected });
            PngJsonCapturePublicationArtifactRecoveryDecision decision = ClassifyDecision(snapshot);

            PngJsonCapturePublicationArtifactEntryObservation[] swapped =
            {
                snapshot.GetEntry(1),
                snapshot.GetEntry(0)
            };
            SetField(snapshot, "_entries", swapped);

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_InvalidAfterEntryValueMutation()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = MakeCommitDecision();
            SetField(decision.Snapshot.GetEntry(0), "_finalPngStatus", EvMismatch);

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_InvalidAfterOperationSwap()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = MakeCommitDecision();
            SetField(decision.Snapshot, "_operation", MakeOperation(MakeFreshAuthority(1), 1000));

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_InvalidAfterAuthorityMutation()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = MakeCommitDecision();
            SetField(decision.Snapshot.Authority, "_recoveryDecision", null);

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_InvalidAfterLeaseDisposed()
        {
            PngJsonCapturePublicationArtifactRecoveryDecision decision = MakeCommitDecision();
            decision.Snapshot.LockLease.Dispose();

            Assert.That(decision.IsValid, Is.False);
        }

        // ---- Token predicates ----

        [Test]
        public void Token_CrossSnapshot_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshotA = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshotB = MakeSnapshotSingle(
                MakeFreshAuthority(1), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);

            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshotA);

            Assert.That(token.IsIssuedForExactBindings(snapshotB), Is.False);
            Assert.That(token.TryGetIssuedEntry(snapshotB, 0, out PngJsonCapturePublicationArtifactEntryObservation observation), Is.False);
            Assert.That(observation, Is.Null);
        }

        [Test]
        public void Token_Uninitialized_PredicatesFalseNoThrow()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                (PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken));

            Assert.That(token.IsIssuedForExactBindings(snapshot), Is.False);
            Assert.That(token.TryGetIssuedEntry(snapshot, 0, out PngJsonCapturePublicationArtifactEntryObservation observation), Is.False);
            Assert.That(observation, Is.Null);
        }

        [Test]
        public void Token_InternalNull_PredicatesFalseNoThrow()
        {
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = MakeSnapshotSingle(
                MakeRecoveryAuthority(), EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken.Acquire(snapshot);
            SetField(token, "_proof", null);

            Assert.That(token.IsIssuedForExactBindings(snapshot), Is.False);
            Assert.That(token.TryGetIssuedEntry(snapshot, 0, out PngJsonCapturePublicationArtifactEntryObservation observation), Is.False);
            Assert.That(observation, Is.Null);
        }

        // ---- Type shape ----

        [Test]
        public void Decision_TypeShape_NoDispositionInjection()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryDecision);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            Assert.That(instanceFields[0].Name, Is.EqualTo("_snapshot"));
            Assert.That(instanceFields[1].Name, Is.EqualTo("_disposition"));
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.That(parameter.ParameterType, Is.Not.EqualTo(typeof(CaptureRunPublicationArtifactRecoveryDisposition)),
                        method.Name + " must not accept a disposition parameter.");
                }
            }
        }

        [Test]
        public void Classifier_TypeShape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactRecoveryClassifier);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        // ---- Source inspection ----

        [Test]
        public void ClassifierSource_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryClassifier.cs"));

            AssertNoForbiddenDependencies(source);
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("Draft"));
            Assert.That(source, Does.Not.Contain("Acquire("));
            Assert.That(source, Does.Not.Contain("FreshSeed"));
        }

        [Test]
        public void DecisionSource_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryDecision.cs"));

            AssertNoForbiddenDependencies(source);
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("Draft"));
            Assert.That(source, Does.Not.Contain("Acquire("));
        }

        [Test]
        public void ClassifierSource_ComputeDisposition_HasNoValidationOrTokenIssuance()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryClassifier.cs"));

            string classifyBody = ExtractMethodBody(source, "internal static PngJsonCapturePublicationArtifactRecoveryDecision Classify(");
            Assert.That(CountOccurrences(classifyBody, "TryValidate("), Is.EqualTo(1));

            string computeBody = ExtractMethodBody(source, "internal static CaptureRunPublicationArtifactRecoveryDisposition ComputeDisposition(");
            Assert.That(computeBody, Does.Not.Contain("TryValidate("));
            Assert.That(computeBody, Does.Not.Contain(".IsValid"));
            Assert.That(computeBody, Does.Not.Contain("Acquire("));
            Assert.That(computeBody, Does.Not.Contain("GetEntry("));
            Assert.That(CountOccurrences(computeBody, "TryGetIssuedEntry("), Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void DecisionSource_IsValid_SingleValidation()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactRecoveryDecision.cs"));

            string isValidBody = ExtractMethodBody(source, "internal bool IsValid");
            Assert.That(CountOccurrences(isValidBody, "TryValidate("), Is.EqualTo(1));
            Assert.That(isValidBody, Does.Not.Contain(".IsValid"));
            Assert.That(isValidBody, Does.Not.Contain("Acquire("));
        }

        // ---- Parity with the existing classifier ----

        [Test]
        public void Parity_TraceAbsent_OrphanedPreTrace()
        {
            AssertParity(MakePlan(), false, EvAbsent, 0,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent });
        }

        [Test]
        public void Parity_TraceMatchesExpected_CommitCaptureIndex()
        {
            AssertParity(MakePlan(), false, EvMatchesExpected, 1,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvMatchesExpected }, new[] { EvMatchesExpected });
        }

        [Test]
        public void Parity_TraceMatchesExpected_PublishMissingArtifacts()
        {
            AssertParity(MakePlan(), false, EvMatchesExpected, 1,
                new[] { EvMatchesExpected }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent });
        }

        [Test]
        public void Parity_TraceMatchesExpected_ArtifactSourceMissing()
        {
            AssertParity(MakePlan(), false, EvMatchesExpected, 1,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent });
        }

        [Test]
        public void Parity_CaptureIndexAuthoritative_CaptureComplete()
        {
            AssertParity(MakePlan(), true, EvMatchesExpected, 1,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvMatchesExpected }, new[] { EvMatchesExpected });
        }

        [Test]
        public void Parity_CaptureIndexAuthoritative_PublishedArtifactMissing()
        {
            AssertParity(MakePlan(), true, EvMatchesExpected, 1,
                new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent }, new[] { EvAbsent });
        }
    }
}
