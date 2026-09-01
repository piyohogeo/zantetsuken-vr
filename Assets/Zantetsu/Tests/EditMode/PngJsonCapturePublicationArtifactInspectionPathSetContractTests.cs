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
    public class PngJsonCapturePublicationArtifactInspectionPathSetContractTests
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

            string dir = Path.GetDirectoryName(typeof(PngJsonCapturePublicationArtifactInspectionPathSetContractTests).Assembly.Location);
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
            Assert.That(source, Does.Not.Contain("Inspector"));
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("Draft"));
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

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(PngJsonCapturePublicationPlan plan = null)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(MakeDecision(plan));
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority MakeFreshAuthority(params long[] frameIds)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(MakeSeed(frameIds));
        }

        // ---- Shape ----

        [Test]
        public void PathSet_TypeShape()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactInspectionPathSet);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(6));
            int referenceCount = 0;
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (!field.FieldType.IsValueType)
                {
                    referenceCount++;
                }
            }

            Assert.That(referenceCount, Is.EqualTo(5));
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Token_PrivateCtorOnly()
        {
            Type tokenType = typeof(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken);

            Assert.That(tokenType.IsPublic, Is.False);
            Assert.That(tokenType.IsSealed, Is.True);

            ConstructorInfo[] constructors = tokenType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.EqualTo(1));
            Assert.That(constructors[0].IsPrivate, Is.True);
            Assert.That(tokenType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Token_ExposesNoProofArrayOrEntryList()
        {
            Type tokenType = typeof(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken);

            Assert.That(tokenType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty);

            foreach (FieldInfo field in tokenType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
            }
        }

        [Test]
        public void Token_NoNonValidatingMintApi()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority bad =
                (PngJsonCapturePublicationArtifactInspectionAuthority)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionAuthority));

            Assert.That(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.TryAcquire(bad, out _), Is.False);
            Assert.That(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.TryAcquire(null, out _), Is.False);
            Assert.Throws<InvalidOperationException>(
                () => PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(bad));
        }

        // ---- Normal construction ----

        [Test]
        public void Recovery_FourPathsExact()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0);

            PngJsonCapturePublicationPlanEntry entry = authority.AuthoritativePlan.GetEntry(0);
            string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);
            CaptureRunPublicationPathSet publicationPaths = authority.PublicationPaths;

            Assert.That(pathSet.IsValid, Is.True);
            Assert.That(ReferenceEquals(pathSet.Authority, authority), Is.True);
            Assert.That(pathSet.EntryIndex, Is.EqualTo(0));
            Assert.That(pathSet.CaptureFrameId, Is.EqualTo(entry.CaptureFrameId));
            Assert.That(pathSet.StagingPngPath, Is.EqualTo(Path.Combine(publicationPaths.StagingFramesRoot, id + ".png.stage")));
            Assert.That(pathSet.StagingSidecarPath, Is.EqualTo(Path.Combine(publicationPaths.StagingFramesRoot, id + ".json.stage")));
            Assert.That(pathSet.FinalPngPath, Is.EqualTo(Path.Combine(publicationPaths.FinalFramesRoot, id + ".png")));
            Assert.That(pathSet.FinalSidecarPath, Is.EqualTo(Path.Combine(publicationPaths.FinalFramesRoot, id + ".json")));
            Assert.That(ReferenceEquals(pathSet.RootLayout, authority.RootLayout), Is.True);
            Assert.That(ReferenceEquals(pathSet.LockLease, authority.LockLease), Is.True);
            Assert.That(pathSet.TestRunId, Is.EqualTo(authority.TestRunId));
            Assert.That(pathSet.RunInitializationId, Is.EqualTo(authority.RunInitializationId));
        }

        [Test]
        public void Fresh_FourPathsExact()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1, 2);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 1);

            PngJsonCapturePublicationPlanEntry entry = authority.AuthoritativePlan.GetEntry(1);
            string id = entry.CaptureFrameId.ToString(CultureInfo.InvariantCulture);
            CaptureRunPublicationPathSet publicationPaths = authority.PublicationPaths;

            Assert.That(pathSet.IsValid, Is.True);
            Assert.That(pathSet.CaptureFrameId, Is.EqualTo(2));
            Assert.That(pathSet.StagingPngPath, Is.EqualTo(Path.Combine(publicationPaths.StagingFramesRoot, id + ".png.stage")));
            Assert.That(pathSet.StagingSidecarPath, Is.EqualTo(Path.Combine(publicationPaths.StagingFramesRoot, id + ".json.stage")));
            Assert.That(pathSet.FinalPngPath, Is.EqualTo(Path.Combine(publicationPaths.FinalFramesRoot, id + ".png")));
            Assert.That(pathSet.FinalSidecarPath, Is.EqualTo(Path.Combine(publicationPaths.FinalFramesRoot, id + ".json")));
        }

        [Test]
        public void Recovery_MatchesExistingPathSet()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);

            CaptureRunPublicationArtifactPathSet existing = new CaptureRunPublicationArtifactPathSet(decision, 0);
            PngJsonCapturePublicationArtifactInspectionPathSet mine =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0);

            Assert.That(string.Equals(mine.StagingPngPath, existing.StagingPngPath, StringComparison.Ordinal), Is.True);
            Assert.That(string.Equals(mine.StagingSidecarPath, existing.StagingSidecarPath, StringComparison.Ordinal), Is.True);
            Assert.That(string.Equals(mine.FinalPngPath, existing.FinalPngPath, StringComparison.Ordinal), Is.True);
            Assert.That(string.Equals(mine.FinalSidecarPath, existing.FinalSidecarPath, StringComparison.Ordinal), Is.True);
        }

        [Test]
        public void PathSet_FirstAndLastIndex()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20), MakeEntry(30) }));

            PngJsonCapturePublicationArtifactInspectionPathSet first =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0);
            PngJsonCapturePublicationArtifactInspectionPathSet last =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 2);

            Assert.That(first.IsValid, Is.True);
            Assert.That(last.IsValid, Is.True);
            Assert.That(first.CaptureFrameId, Is.EqualTo(10));
            Assert.That(last.CaptureFrameId, Is.EqualTo(30));
        }

        [Test]
        public void PathSet_OutOfRangeIndex_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();

            Assert.Throws<ArgumentOutOfRangeException>(() => new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PngJsonCapturePublicationArtifactInspectionPathSet(authority, -1));
        }

        [Test]
        public void PathSet_ZeroEntry_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority();

            Assert.Throws<ArgumentOutOfRangeException>(() => new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0));
        }

        // ---- Rejection ----

        [Test]
        public void Normal_NullAuthority_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new PngJsonCapturePublicationArtifactInspectionPathSet(null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("authority"));
        }

        [Test]
        public void Normal_InvalidAuthority_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority bad =
                (PngJsonCapturePublicationArtifactInspectionAuthority)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionAuthority));

            Assert.Throws<ArgumentException>(() => new PngJsonCapturePublicationArtifactInspectionPathSet(bad, 0));
        }

        [Test]
        public void Trusted_NullToken_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(null, MakeRecoveryAuthority(), 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void Trusted_NullAuthority_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(MakeRecoveryAuthority());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("authority"));
        }

        [Test]
        public void Trusted_CrossToken_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authorityA = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority authorityB = MakeFreshAuthority(1);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken tokenA =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authorityA);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(tokenA, authorityB, 0));
        }

        [Test]
        public void Trusted_StaleTokenAfterLeaseDispose_Rejected()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);

            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);
            Assert.That(pathSet.IsValidIndexLocal(token), Is.True);

            authority.LockLease.Dispose();

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        // ---- Authority reference tamper ----

        [Test]
        public void Recovery_SwapFreshSeedIn_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority, "_freshSeed", MakeSeed(1));

            Assert.That(pathSet.IsValid, Is.False);
            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Fresh_SwapRecoveryDecisionIn_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority, "_recoveryDecision", MakeDecision());

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        // ---- Recovery graph tamper ----

        [Test]
        public void Recovery_PlanSwap_False()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(decision, "_authoritativePlan", MakePlan(entries: new[] { MakeEntry(99) }));

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        [Test]
        public void Recovery_PublicationPathsSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority.RecoveryDecision.Snapshot.Operation, "_publicationPaths", new CaptureRunPublicationPathSet(MakeLayout(99)));

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Recovery_RootLayoutSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority.RecoveryDecision.Snapshot.Operation.PublicationPaths, "_rootLayout", MakeLayout(99));

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        // ---- Fresh graph tamper ----

        [Test]
        public void Fresh_PlanSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority.FreshSeed.PlanBinding, "_legacyPlan", MakeSeed(2).AuthoritativePlan);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Fresh_PublicationPathsSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority.FreshSeed, "_publicationPaths", new CaptureRunPublicationPathSet(MakeLayout(99)));

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Fresh_RootLayoutSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(1);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            CaptureRunRootLayout otherLayout = MakeLayout(99);
            CaptureRunInitializationSession otherSession = MakeLifecycleSession(otherLayout, MakeLease(otherLayout));
            SetField(authority.FreshSeed.FreezeReceipt, "_runSession", otherSession);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        // ---- Entry array / element tamper ----

        [Test]
        public void EntryArrayNull_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(authority.AuthoritativePlan, "_entries", null);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.That(pathSet.IsValid, Is.False);
        }

        [Test]
        public void EntryArrayNull_ConstructorRejects()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);

            SetField(authority.AuthoritativePlan, "_entries", null);

            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        [Test]
        public void EntryArraySwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
            PngJsonCapturePublicationPlanEntry[] swapped = new PngJsonCapturePublicationPlanEntry[plan.EntryCount];
            for (int i = 0; i < swapped.Length; i++)
            {
                swapped[i] = MakeEntry(100 + i);
            }

            SetField(plan, "_entries", swapped);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void EntryElementSwap_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) }));
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
            PngJsonCapturePublicationPlanEntry[] swapped = new PngJsonCapturePublicationPlanEntry[2];
            swapped[0] = plan.GetEntry(1);
            swapped[1] = plan.GetEntry(0);
            SetField(plan, "_entries", swapped);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        // ---- Entry value tamper ----

        [Test]
        public void Entry_PathMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(pathSet.Entry, "_pngStagingRelativePath", "frames/999.png.stage");

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.That(pathSet.IsValid, Is.False);
        }

        [Test]
        public void Entry_CaptureFrameIdMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(pathSet.Entry, "_captureFrameId", 999L);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Entry_ByteLengthMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(pathSet.Entry, "_pngByteLength", 0L);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Entry_HashMutated_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(pathSet.Entry, "_pngContentSha256", "broken");

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void Entry_ByteLengthMutatedToValidValue_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(pathSet.Entry, "_pngByteLength", 17L);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        [Test]
        public void Entry_HashMutatedToValidValue_False()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(pathSet.Entry, "_pngContentSha256", HashB);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        // ---- Forged token ----

        [Test]
        public void Token_EntriesNull_FailsClosed()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0);

            SetField(token, "_entries", null);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        [Test]
        public void Token_EntriesShortened_FailsClosed()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(
                MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) }));
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 1);

            Type tokenType = typeof(PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken);
            Type snapshotType = tokenType.GetNestedType("EntrySnapshot", BindingFlags.NonPublic);
            Assert.That(snapshotType, Is.Not.Null);
            Array empty = Array.CreateInstance(snapshotType, 0);
            SetField(token, "_entries", empty);

            Assert.That(pathSet.IsValidIndexLocal(token), Is.False);
            Assert.Throws<ArgumentException>(
                () => PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, 0));
        }

        // ---- Stored path tamper ----

        [Test]
        public void FourPathsMutated_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority();
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0);

            SetField(pathSet, "_stagingPngPath", "C:\\wrong\\a.png");

            Assert.That(pathSet.IsValid, Is.False);
        }

        [Test]
        public void Uninitialized_IsValidFalse()
        {
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                (PngJsonCapturePublicationArtifactInspectionPathSet)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionPathSet));

            Assert.That(pathSet.IsValid, Is.False);
            Assert.That(pathSet.IsValidIndexLocal(null), Is.False);
        }

        [Test]
        public void Uninitialized_IndexLocalPathCorrelation_False()
        {
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                (PngJsonCapturePublicationArtifactInspectionPathSet)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactInspectionPathSet));

            Assert.That(pathSet.IsIndexLocalPathCorrelationIntact(), Is.False);
        }

        [Test]
        public void NullAuthority_IndexLocalPathCorrelation_False()
        {
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(MakeRecoveryAuthority(), 0);
            SetField(pathSet, "_authority", null);

            Assert.That(pathSet.IsIndexLocalPathCorrelationIntact(), Is.False);
        }

        [Test]
        public void PlanEntryArrayNull_IndexLocalPathCorrelation_False()
        {
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(MakeRecoveryAuthority(), 0);
            SetField(pathSet.Plan, "_entries", null);

            Assert.That(pathSet.IsIndexLocalPathCorrelationIntact(), Is.False);
        }

        [Test]
        public void RecoverySnapshotNull_IndexLocalPathCorrelation_False()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0);

            SetField(decision, "_snapshot", null);

            Assert.That(pathSet.IsIndexLocalPathCorrelationIntact(), Is.False);
        }

        [Test]
        public void FreshBindingNull_IndexLocalPathCorrelation_False()
        {
            PngJsonCaptureFrozenRunArtifactInspectionSeed seed = MakeSeed(1);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromFresh(seed);
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                new PngJsonCapturePublicationArtifactInspectionPathSet(authority, 0);

            SetField(seed, "_planBinding", null);

            Assert.That(pathSet.IsIndexLocalPathCorrelationIntact(), Is.False);
        }

        // ---- Source shape ----

        [Test]
        public void Source_NoForbiddenDeps()
        {
            string source = File.ReadAllText(
                LocateSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactInspectionPathSet.cs"));

            AssertNoForbiddenDependencies(source);
        }

        // ---- Scale ----

        [Test]
        public void Fresh_1000Entries_TrustedLoop()
        {
            const int entryCount = 1000;
            long[] frameIds = new long[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                frameIds[i] = i + 1;
            }

            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeFreshAuthority(frameIds);
            PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.Acquire(authority);

            for (int i = 0; i < entryCount; i++)
            {
                PngJsonCapturePublicationArtifactInspectionPathSet pathSet =
                    PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(token, authority, i);

                Assert.That(pathSet.IsValidIndexLocal(token), Is.True);
                Assert.That(pathSet.CaptureFrameId, Is.EqualTo(i + 1));

                string id = (i + 1).ToString(CultureInfo.InvariantCulture);
                Assert.That(pathSet.FinalPngPath, Is.EqualTo(Path.Combine(authority.PublicationPaths.FinalFramesRoot, id + ".png")));
            }
        }
    }
}
