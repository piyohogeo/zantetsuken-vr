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
    public class CaptureRunPublicationArtifactRecoveryClassifierTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const long PngBytes = 16;

        private const long SidecarBytes = 32;

        private const long TraceLimit = 65536;

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

        private static CaptureRunPublicationEvidenceStatus EvInvalid => CaptureRunPublicationEvidenceStatus.Invalid;

        private static CaptureRunPublicationEvidenceStatus EvLimitExceeded => CaptureRunPublicationEvidenceStatus.LimitExceeded;

        private static CaptureRunPublicationArtifactRecoveryDisposition OrphanedPreTrace => CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace;

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishMissingArtifacts => CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts;

        private static CaptureRunPublicationArtifactRecoveryDisposition CommitCaptureIndex => CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex;

        private static CaptureRunPublicationArtifactRecoveryDisposition CaptureComplete => CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;

        private static CaptureRunPublicationArtifactRecoveryDisposition ArtifactSourceMissing => CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition PublishedArtifactMissing => CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing;

        private static CaptureRunPublicationArtifactRecoveryDisposition RunRootCollision => CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision;

        // ---- Helpers ----

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

        private static CaptureRunMarkerBinding MakeBinding(CaptureRunRootLayout layout)
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
            long pngByteLength = PngBytes,
            long sidecarByteLength = SidecarBytes,
            string pngHash = null,
            string sidecarHash = null)
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
                pngHash ?? StagingHash,
                sidecarHash ?? StagingHash);
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
            string initId = null,
            string manifestHash = null,
            PngJsonCapturePublicationPlanEntry[] entries = null)
        {
            return new PngJsonCapturePublicationPlan(
                testRunId,
                initId ?? InitId,
                manifestHash ?? StagingHash,
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
            CaptureRunMarkerBinding binding = MakeBinding(layout);

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

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            bool indexAuthoritative = false,
            PngJsonCapturePublicationPlan plan = null,
            int maximumEntryCount = 4,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(
                outcome, 1000, maximumEntryCount, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            plan = plan ?? MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, recoveryOperation, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, recoveryOperation, captureIndexTemporary: captureIndexTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);
            return new CaptureRunPublicationArtifactInspectionOperation(decision, 1000);
        }

        private static CaptureRunPublicationArtifactEntryObservation MakeEntryObservation(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingPngCount = 0,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingSidecarCount = 0,
            CaptureRunPublicationEvidenceStatus finalPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalPngCount = 0,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalSidecarCount = 0)
        {
            return new CaptureRunPublicationArtifactEntryObservation(
                operation,
                artifactPaths,
                stagingPngStatus,
                stagingPngCount,
                stagingSidecarStatus,
                stagingSidecarCount,
                finalPngStatus,
                finalPngCount,
                finalSidecarStatus,
                finalSidecarCount);
        }

        private static CaptureRunPublicationArtifactInspectionSnapshot MakeArtifactSnapshot(
            ICaptureRunPublicationArtifactInspector issuedBy,
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long traceCount = 0,
            CaptureRunPublicationArtifactEntryObservation[] entries = null)
        {
            if (entries == null)
            {
                entries = new CaptureRunPublicationArtifactEntryObservation[operation.EntryCount];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = MakeEntryObservation(operation, operation.GetArtifactPaths(i));
                }
            }

            return new CaptureRunPublicationArtifactInspectionSnapshot(issuedBy, operation, traceStatus, traceCount, entries);
        }

        private static CaptureRunPublicationArtifactRecoveryDecision Classify(
            bool indexAuthoritative = false,
            PngJsonCapturePublicationPlan plan = null,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long traceCount = 0,
            CaptureRunPublicationArtifactEntryObservation[] entries = null,
            int maximumEntryCount = 4)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, indexAuthoritative, plan, maximumEntryCount);
            return CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, traceStatus, traceCount, entries));
        }

        private static CaptureRunPublicationArtifactRecoveryDisposition ClassifySingleEntry(
            CaptureRunPublicationEvidenceStatus stagingPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingPngCount = 0,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long stagingSidecarCount = 0,
            CaptureRunPublicationEvidenceStatus finalPngStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalPngCount = 0,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus = CaptureRunPublicationEvidenceStatus.Absent,
            long finalSidecarCount = 0,
            bool indexAuthoritative = false,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, indexAuthoritative);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus, stagingPngCount,
                stagingSidecarStatus, stagingSidecarCount,
                finalPngStatus, finalPngCount,
                finalSidecarStatus, finalSidecarCount);
            return CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, traceStatus, traceCount, new[] { observation })).Disposition;
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

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactRecoveryClassifierTests).Assembly.Location);
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

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
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

        private sealed class FakeArtifactInspector : ICaptureRunPublicationArtifactInspector
        {
            public CaptureRunPublicationArtifactInspectionSnapshot Inspect(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        // ---- Enum ----

        [Test]
        public void Enum_Contract()
        {
            AssertEnumContract(typeof(CaptureRunPublicationArtifactRecoveryDisposition),
                new[]
                {
                    "None", "OrphanedPreTrace", "PublishMissingArtifacts", "CommitCaptureIndex",
                    "CaptureComplete", "ArtifactSourceMissing", "PublishedArtifactMissing", "RunRootCollision"
                });
        }

        // ---- Rejection ----

        [Test]
        public void Classify_NullSnapshot_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationArtifactRecoveryClassifier.Classify(null));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        [Test]
        public void Classify_InvalidSnapshot_Rejected()
        {
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = (CaptureRunPublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionSnapshot));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        [Test]
        public void Classify_ForgedSnapshotCorruption_RejectedNoException()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = (CaptureRunPublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionSnapshot));
            SetField(snapshot, "_issuedBy", inspector);
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_traceManifestStatus", EvMatchesExpected);
            SetField(snapshot, "_traceManifestProbedByteCount", 100L);
            SetField(snapshot, "_entries", null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        // ---- Collision priority ----

        [Test]
        public void Classify_TraceAnomaly_Collision()
        {
            Assert.That(Classify(traceStatus: EvMismatch, traceCount: 100).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(traceStatus: EvInvalid, traceCount: 0).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(traceStatus: EvLimitExceeded, traceCount: TraceLimit + 1).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_EachArtifactAnomaly_Collision()
        {
            // Staging PNG.
            Assert.That(ClassifySingleEntry(stagingPngStatus: EvMismatch, stagingPngCount: 1), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(stagingPngStatus: EvInvalid, stagingPngCount: 0), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(stagingPngStatus: EvLimitExceeded, stagingPngCount: PngBytes + 1), Is.EqualTo(RunRootCollision));

            // Staging sidecar.
            Assert.That(ClassifySingleEntry(stagingSidecarStatus: EvMismatch, stagingSidecarCount: 1), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(stagingSidecarStatus: EvInvalid, stagingSidecarCount: 0), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(stagingSidecarStatus: EvLimitExceeded, stagingSidecarCount: SidecarBytes + 1), Is.EqualTo(RunRootCollision));

            // Final PNG.
            Assert.That(ClassifySingleEntry(finalPngStatus: EvMismatch, finalPngCount: 1), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(finalPngStatus: EvInvalid, finalPngCount: 0), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(finalPngStatus: EvLimitExceeded, finalPngCount: PngBytes + 1), Is.EqualTo(RunRootCollision));

            // Final sidecar.
            Assert.That(ClassifySingleEntry(finalSidecarStatus: EvMismatch, finalSidecarCount: 1), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(finalSidecarStatus: EvInvalid, finalSidecarCount: 0), Is.EqualTo(RunRootCollision));
            Assert.That(ClassifySingleEntry(finalSidecarStatus: EvLimitExceeded, finalSidecarCount: SidecarBytes + 1), Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_UnusedStagingAnomaly_Collision()
        {
            // A mismatched staging source collides even when the final artifact is fully matched.
            Assert.That(ClassifySingleEntry(
                stagingPngStatus: EvMismatch, stagingPngCount: 1,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_IndexAuthoritativeTraceAbsent_Collision()
        {
            Assert.That(Classify(indexAuthoritative: true, traceStatus: EvAbsent, traceCount: 0).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_CollisionBeatsCaptureComplete()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, indexAuthoritative: true);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMismatch, stagingPngCount: 1,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, new[] { observation }));

            Assert.That(decision.Disposition, Is.EqualTo(RunRootCollision));
        }

        // ---- Plan authoritative ----

        [Test]
        public void Classify_PlanTraceAbsent_OrphanedPreTrace()
        {
            Assert.That(Classify(traceStatus: EvAbsent, traceCount: 0).Disposition, Is.EqualTo(OrphanedPreTrace));
        }

        [Test]
        public void Classify_TraceAbsentFinalPngMatches_Collision()
        {
            Assert.That(ClassifySingleEntry(
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                traceStatus: EvAbsent, traceCount: 0), Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_TraceAbsentFinalSidecarMatches_Collision()
        {
            Assert.That(ClassifySingleEntry(
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes,
                traceStatus: EvAbsent, traceCount: 0), Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_TraceAbsentOneFinalMatches_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, plan, 4);

            CaptureRunPublicationArtifactEntryObservation e0 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactEntryObservation e1 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(1),
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes);

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvAbsent, 0, new[] { e0, e1 }));

            Assert.That(decision.Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_TraceAbsentStagingMatchAllFinalAbsent_OrphanedPreTrace()
        {
            Assert.That(ClassifySingleEntry(
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0,
                traceStatus: EvAbsent, traceCount: 0), Is.EqualTo(OrphanedPreTrace));
        }

        [Test]
        public void Classify_TraceAbsentCanonicalIndexTemporary_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                null, false, plan, 4,
                MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan));
            FakeArtifactInspector inspector = new FakeArtifactInspector();

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvAbsent, 0, null));

            Assert.That(decision.Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_TraceAbsentInvalidIndexTemporary_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(
                null, false, plan, 4,
                MakeDoc(CaptureIndexTemporary, DocInvalid, 0));
            FakeArtifactInspector inspector = new FakeArtifactInspector();

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvAbsent, 0, null));

            Assert.That(decision.Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void ComputeDisposition_TraceAbsentLimitExceededIndexTemporary_Collision()
        {
            // A limit-exceeded capture.index.tmp is already rejected by the
            // publication classifier before a valid artifact snapshot can
            // exist, so this ordering violation is asserted on the shared pure
            // computation over a forged snapshot whose operation is otherwise
            // intact.
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false);
            SetField(operation.Decision.Snapshot, "_captureIndexTemporary",
                MakeDoc(CaptureIndexTemporary, DocLimitExceeded, 1001));

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = (CaptureRunPublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionSnapshot));
            SetField(snapshot, "_issuedBy", new FakeArtifactInspector());
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_traceManifestStatus", EvAbsent);
            SetField(snapshot, "_traceManifestProbedByteCount", 0L);
            SetField(snapshot, "_entries", new CaptureRunPublicationArtifactEntryObservation[0]);

            Assert.That(CaptureRunPublicationArtifactRecoveryClassifier.ComputeDisposition(snapshot), Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_PlanAllFinalMatch_CommitCaptureIndex()
        {
            Assert.That(ClassifySingleEntry(
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Classify_PlanFinalAbsentStagingMatch_PublishMissingArtifacts()
        {
            // Final PNG missing with a matching staging PNG.
            Assert.That(ClassifySingleEntry(
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(PublishMissingArtifacts));

            // Final sidecar missing with a matching staging sidecar.
            Assert.That(ClassifySingleEntry(
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0), Is.EqualTo(PublishMissingArtifacts));
        }

        [Test]
        public void Classify_PlanMultiplePublishableMissing_PublishMissingArtifacts()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, plan, 4);

            CaptureRunPublicationArtifactEntryObservation e0 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactEntryObservation e1 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(1),
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0);

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, new[] { e0, e1 }));

            Assert.That(decision.Disposition, Is.EqualTo(PublishMissingArtifacts));
        }

        [Test]
        public void Classify_PlanFinalAbsentStagingAbsent_ArtifactSourceMissing()
        {
            Assert.That(ClassifySingleEntry(
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(ArtifactSourceMissing));
        }

        [Test]
        public void Classify_PlanMixedPublishableAndSourceMissing_ArtifactSourceMissing()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(1), MakeEntry(2) });
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, plan, 4);

            CaptureRunPublicationArtifactEntryObservation e0 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);
            CaptureRunPublicationArtifactEntryObservation e1 = MakeEntryObservation(
                operation, operation.GetArtifactPaths(1),
                finalPngStatus: EvAbsent, finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes);

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, new[] { e0, e1 }));

            Assert.That(decision.Disposition, Is.EqualTo(ArtifactSourceMissing));
        }

        [Test]
        public void Classify_PlanFinalMatchStagingAbsent_CommitCaptureIndex()
        {
            // A matched final artifact with an absent staging source is normal.
            Assert.That(ClassifySingleEntry(
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Classify_PlanEmptyEntries_CommitCaptureIndex()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = Classify(
                plan: MakePlan(entries: new PngJsonCapturePublicationPlanEntry[0]),
                traceStatus: EvMatchesExpected,
                traceCount: 100);

            Assert.That(decision.Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        // ---- Index authoritative ----

        [Test]
        public void Classify_IndexAllFinalMatch_CaptureComplete()
        {
            Assert.That(ClassifySingleEntry(
                indexAuthoritative: true,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(CaptureComplete));
        }

        [Test]
        public void Classify_IndexFinalAbsent_PublishedArtifactMissing()
        {
            Assert.That(ClassifySingleEntry(indexAuthoritative: true, finalPngStatus: EvAbsent, finalPngCount: 0), Is.EqualTo(PublishedArtifactMissing));
            Assert.That(ClassifySingleEntry(indexAuthoritative: true, finalSidecarStatus: EvAbsent, finalSidecarCount: 0), Is.EqualTo(PublishedArtifactMissing));
            Assert.That(ClassifySingleEntry(
                indexAuthoritative: true,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvAbsent, finalSidecarCount: 0), Is.EqualTo(PublishedArtifactMissing));
        }

        [Test]
        public void Classify_IndexStagingVaries_DoesNotAffectCompletion()
        {
            // Staging match or absence does not affect index-authoritative completion.
            Assert.That(ClassifySingleEntry(
                indexAuthoritative: true,
                stagingPngStatus: EvMatchesExpected, stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected, stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(CaptureComplete));

            Assert.That(ClassifySingleEntry(
                indexAuthoritative: true,
                stagingPngStatus: EvAbsent, stagingPngCount: 0,
                stagingSidecarStatus: EvAbsent, stagingSidecarCount: 0,
                finalPngStatus: EvMatchesExpected, finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected, finalSidecarCount: SidecarBytes), Is.EqualTo(CaptureComplete));
        }

        // ---- Decision correlation ----

        [Test]
        public void Decision_HoldsSnapshotAndForwards()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, plan);
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, null);
            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Snapshot, Is.SameAs(snapshot));
            Assert.That(decision.Operation, Is.SameAs(operation));
            Assert.That(decision.PublicationDecision, Is.SameAs(operation.Decision));
            Assert.That(decision.AuthoritativePlan, Is.SameAs(plan));
            Assert.That(decision.RootLayout, Is.SameAs(operation.RootLayout));
            Assert.That(decision.TestRunId, Is.EqualTo(1));
            Assert.That(decision.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Decision_ForgedDisposition_IsValidFalse()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = Classify(
                traceStatus: EvMatchesExpected, traceCount: 100);
            Assert.That(decision.IsValid, Is.True);

            CaptureRunPublicationArtifactRecoveryDecision forged = (CaptureRunPublicationArtifactRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryDecision));
            SetField(forged, "_snapshot", decision.Snapshot);
            SetField(forged, "_disposition", RunRootCollision);
            Assert.That(forged.IsValid, Is.False);
        }

        [Test]
        public void Decision_Uninitialized_IsInvalid()
        {
            CaptureRunPublicationArtifactRecoveryDecision decision = (CaptureRunPublicationArtifactRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryDecision));
            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_ForgedSnapshotCorruption_IsValidFalse_NoException()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = (CaptureRunPublicationArtifactInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactInspectionSnapshot));
            SetField(snapshot, "_issuedBy", inspector);
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_traceManifestStatus", EvMatchesExpected);
            SetField(snapshot, "_traceManifestProbedByteCount", 100L);
            SetField(snapshot, "_entries", null);

            CaptureRunPublicationArtifactRecoveryDecision decision = (CaptureRunPublicationArtifactRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryDecision));
            SetField(decision, "_snapshot", snapshot);
            SetField(decision, "_disposition", CommitCaptureIndex);

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_LeaseRelease_Invalid_NoException()
        {
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation();
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, null);
            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.IsValid, Is.True);

            operation.LockLease.Dispose();

            Assert.That(snapshot.IsValid, Is.False);
            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Classify_DoesNotMutateOrDisposeInputs()
        {
            List<string> disposeLog = new List<string>();
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(disposeLog);
            CaptureRunPublicationArtifactInspectionSnapshot snapshot = MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, null);

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Snapshot, Is.SameAs(snapshot));
            Assert.That(disposeLog, Is.Empty, "Classification must not dispose the lease.");
            Assert.That(operation.LockLease.IsCreated, Is.True);
            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
        }

        // ---- Shape ----

        [Test]
        public void Decision_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryDecision);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Decision_FieldShape_TwoReadonlyFields()
        {
            FieldInfo[] fields = typeof(CaptureRunPublicationArtifactRecoveryDecision).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(2));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Classifier_IsStaticWithNoState()
        {
            Type type = typeof(CaptureRunPublicationArtifactRecoveryClassifier);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        // ---- Linearity ----

        [Test]
        public void Classify_LargePlan_LinearAndCorrect()
        {
            int count = 1000;
            PngJsonCapturePublicationPlan plan = MakePlan(entries: MakeEntries(count));
            FakeArtifactInspector inspector = new FakeArtifactInspector();
            CaptureRunPublicationArtifactInspectionOperation operation = MakeOperation(null, false, plan, count);

            CaptureRunPublicationArtifactEntryObservation[] entries = new CaptureRunPublicationArtifactEntryObservation[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = MakeEntryObservation(
                    operation, operation.GetArtifactPaths(i),
                    EvMatchesExpected, PngBytes, EvMatchesExpected, SidecarBytes,
                    EvMatchesExpected, PngBytes, EvMatchesExpected, SidecarBytes);
            }

            CaptureRunPublicationArtifactRecoveryDecision decision = CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                MakeArtifactSnapshot(inspector, operation, EvMatchesExpected, 100, entries));

            Assert.That(decision.Disposition, Is.EqualTo(CommitCaptureIndex));
        }

        [Test]
        public void Classifier_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactRecoveryClassifier.cs"));

            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("Dictionary"));
            Assert.That(source, Does.Not.Contain("HashSet"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("SHA"));
            Assert.That(source, Does.Not.Contain("Serialize"));
            Assert.That(source, Does.Not.Contain("Deserialize"));

            // The only full snapshot validation is the single entry check in
            // Classify; the entry walk never re-validates per entry.
            int computeIndex = source.IndexOf("ComputeDisposition", StringComparison.Ordinal);
            Assert.That(computeIndex, Is.GreaterThan(0));
            Assert.That(source.Substring(computeIndex), Does.Not.Contain(".IsValid"));

            int isValidCount = 0;
            int from = 0;
            while ((from = source.IndexOf(".IsValid", from, StringComparison.Ordinal)) >= 0)
            {
                isValidCount++;
                from += ".IsValid".Length;
            }

            Assert.That(isValidCount, Is.EqualTo(1));
        }
    }
}
