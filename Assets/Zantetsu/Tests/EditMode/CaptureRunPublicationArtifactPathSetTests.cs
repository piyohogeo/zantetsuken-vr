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
    public class CaptureRunPublicationArtifactPathSetTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string OtherInitId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

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
            long sidecarByteLength = 32,
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
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationSessionOwnershipLease MakeOwner(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            return owner;
        }

        private CaptureRunInitializationOpenOutcome MakeOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CaptureRunLockIdentityEvidence identity)
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

            owner = MakeOwner(layout, disposeLog, out identity);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(List<string> disposeLog = null)
        {
            return MakeOutcome(disposeLog, out _, out _);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOutcome(null, out owner, out _);
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOutcome(disposeLog, out owner, out _);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeOperation(
            int maximumPlanBytes = 1000,
            int maximumEntryCount = 4,
            int maximumPathBytes = 64)
        {
            return MakeOperation(maximumPlanBytes, maximumEntryCount, maximumPathBytes, out _);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeOperation(
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

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeSnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            CaptureRunPublicationFramesObservationStatus finalFramesStatus = CaptureRunPublicationFramesObservationStatus.Directory,
            bool stagingHasUnexpectedEntries = false,
            bool finalHasUnexpectedEntries = false,
            bool stagingRootEntryLimitExceeded = false,
            bool finalRootEntryLimitExceeded = false)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                stagingFramesStatus,
                finalFramesStatus,
                stagingHasUnexpectedEntries,
                finalHasUnexpectedEntries,
                stagingRootEntryLimitExceeded,
                finalRootEntryLimitExceeded);
        }

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan = null,
            bool indexAuthoritative = false)
        {
            return MakeDecision(plan ?? MakePlan(), indexAuthoritative, out _);
        }

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeDecision(plan, false, out owner);
        }

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation(1000, 4, 64, out owner);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeSnapshot(inspector, operation, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeSnapshot(inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationArtifactPathSetTests).Assembly.Location);
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
            public int InspectCount { get; private set; }

            public CaptureRunPublicationRecoveryInspectionOperation LastOperation { get; private set; }

            public CaptureRunPublicationRecoveryInspectionSnapshot SnapshotToReturn { get; set; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunPublicationRecoveryInspectionSnapshot Inspect(CaptureRunPublicationRecoveryInspectionOperation operation)
            {
                InspectCount++;
                LastOperation = operation;
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return SnapshotToReturn;
            }
        }

        // ---- Constructor ----

        [Test]
        public void Constructor_NullDecision_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunPublicationArtifactPathSet(null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Constructor_InvalidDecision_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = (CaptureRunPublicationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryDecision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactPathSet(decision, 0));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Constructor_NoAuthoritativeDocument_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(inspector, operation);
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.NoAuthoritativeDocument));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactPathSet(decision, 0));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Constructor_RunRootCollision_Rejected()
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                inspector, operation, stagingRootEntryLimitExceeded: true);
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.RunRootCollision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunPublicationArtifactPathSet(decision, 0));
            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void Constructor_PlanAndIndexAuthoritative_Accepted()
        {
            CaptureRunPublicationRecoveryDecision planDecision = MakeDecision(indexAuthoritative: false);
            Assert.That(planDecision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative));
            Assert.That(new CaptureRunPublicationArtifactPathSet(planDecision, 0).IsValid, Is.True);

            CaptureRunPublicationRecoveryDecision indexDecision = MakeDecision(indexAuthoritative: true);
            Assert.That(indexDecision.Disposition, Is.EqualTo(CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative));
            Assert.That(new CaptureRunPublicationArtifactPathSet(indexDecision, 0).IsValid, Is.True);
        }

        [Test]
        public void Constructor_EntryIndexOutOfRange_Rejected()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            Assert.That(decision.AuthoritativePlan.EntryCount, Is.EqualTo(1));

            foreach (int index in new[] { -1, 1, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunPublicationArtifactPathSet(decision, index));
                Assert.That(ex.ParamName, Is.EqualTo("entryIndex"));
            }
        }

        // ---- Resolved paths ----

        [Test]
        public void Paths_ExactMatch()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            CaptureRunPublicationPathSet paths = decision.Snapshot.Operation.PublicationPaths;
            string id = "10";

            Assert.That(pathSet.StagingPngPath, Is.EqualTo(Path.Combine(paths.StagingFramesRoot, id + ".png.stage")));
            Assert.That(pathSet.StagingSidecarPath, Is.EqualTo(Path.Combine(paths.StagingFramesRoot, id + ".json.stage")));
            Assert.That(pathSet.FinalPngPath, Is.EqualTo(Path.Combine(paths.FinalFramesRoot, id + ".png")));
            Assert.That(pathSet.FinalSidecarPath, Is.EqualTo(Path.Combine(paths.FinalFramesRoot, id + ".json")));
        }

        [Test]
        public void CaptureFrameIdLongMax_ShortestDecimalBasename()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(long.MaxValue) });
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan);
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            Assert.That(Path.GetFileName(pathSet.StagingPngPath), Is.EqualTo("9223372036854775807.png.stage"));
            Assert.That(Path.GetFileName(pathSet.StagingSidecarPath), Is.EqualTo("9223372036854775807.json.stage"));
            Assert.That(Path.GetFileName(pathSet.FinalPngPath), Is.EqualTo("9223372036854775807.png"));
            Assert.That(Path.GetFileName(pathSet.FinalSidecarPath), Is.EqualTo("9223372036854775807.json"));
        }

        [Test]
        public void Parents_ExactFramesRoot()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);
            CaptureRunPublicationPathSet paths = decision.Snapshot.Operation.PublicationPaths;

            Assert.That(Path.GetDirectoryName(pathSet.StagingPngPath), Is.EqualTo(paths.StagingFramesRoot));
            Assert.That(Path.GetDirectoryName(pathSet.StagingSidecarPath), Is.EqualTo(paths.StagingFramesRoot));
            Assert.That(Path.GetDirectoryName(pathSet.FinalPngPath), Is.EqualTo(paths.FinalFramesRoot));
            Assert.That(Path.GetDirectoryName(pathSet.FinalSidecarPath), Is.EqualTo(paths.FinalFramesRoot));
        }

        [Test]
        public void ForwardsPlanAndEntryByReference()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryDecision decision = MakeDecision(plan, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            Assert.That(pathSet.Decision, Is.SameAs(decision));
            Assert.That(pathSet.EntryIndex, Is.EqualTo(0));
            Assert.That(pathSet.Plan, Is.SameAs(plan));
            Assert.That(pathSet.Plan, Is.SameAs(decision.AuthoritativePlan));
            Assert.That(pathSet.Entry, Is.SameAs(plan.GetEntry(0)));
            Assert.That(pathSet.CaptureFrameId, Is.EqualTo(10));
            Assert.That(pathSet.TestRunId, Is.EqualTo(decision.TestRunId));
            Assert.That(pathSet.RunInitializationId, Is.EqualTo(decision.RunInitializationId));
            Assert.That(
                pathSet.Decision.Snapshot.Operation.LockIdentityEvidence,
                Is.SameAs(decision.Snapshot.Operation.LockIdentityEvidence));
            Assert.That(
                decision.Snapshot.Operation.LockIdentityEvidence.IsIssuedFor(owner),
                Is.True);
        }

        [Test]
        public void RootLayoutAndPathSetReferenceCorrelation()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            Assert.That(pathSet.RootLayout, Is.SameAs(decision.RootLayout));
            Assert.That(pathSet.RootLayout, Is.SameAs(decision.Snapshot.Operation.RootLayout));
            Assert.That(pathSet.RootLayout, Is.SameAs(decision.Snapshot.Operation.PublicationPaths.RootLayout));
        }

        [Test]
        public void Paths_MutuallyDistinct()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            Assert.That(pathSet.StagingPngPath, Is.Not.EqualTo(pathSet.StagingSidecarPath));
            Assert.That(pathSet.StagingPngPath, Is.Not.EqualTo(pathSet.FinalPngPath));
            Assert.That(pathSet.StagingPngPath, Is.Not.EqualTo(pathSet.FinalSidecarPath));
            Assert.That(pathSet.StagingSidecarPath, Is.Not.EqualTo(pathSet.FinalPngPath));
            Assert.That(pathSet.StagingSidecarPath, Is.Not.EqualTo(pathSet.FinalSidecarPath));
            Assert.That(pathSet.FinalPngPath, Is.Not.EqualTo(pathSet.FinalSidecarPath));
        }

        // ---- Forged corruption ----

        [Test]
        public void Forge_EntryRelativePathCorruption_IsValidFalse_NoException()
        {
            string[] pathFields =
            {
                "_pngStagingRelativePath",
                "_sidecarStagingRelativePath",
                "_pngFinalRelativePath",
                "_sidecarFinalRelativePath"
            };

            foreach (string pathField in pathFields)
            {
                PngJsonCapturePublicationPlanEntry entry = MakeEntry(10);
                CaptureRunPublicationRecoveryDecision decision = MakeDecision(MakePlan(entries: new[] { entry }));
                CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);
                Assert.That(pathSet.IsValid, Is.True, pathField);

                SetField(entry, pathField, "frames/999.png.stage");
                Assert.That(pathSet.IsValid, Is.False, pathField);
            }
        }

        [Test]
        public void Forge_StoredPathCorruption_IsValidFalse_NoException()
        {
            string[] pathFields =
            {
                "_stagingPngPath",
                "_stagingSidecarPath",
                "_finalPngPath",
                "_finalSidecarPath"
            };

            foreach (string pathField in pathFields)
            {
                CaptureRunPublicationRecoveryDecision decision = MakeDecision();
                CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);
                Assert.That(pathSet.IsValid, Is.True, pathField);

                SetField(pathSet, pathField, IsWindows ? "C:\\wrong\\artifact.png" : "/wrong/artifact.png");
                Assert.That(pathSet.IsValid, Is.False, pathField);
            }
        }

        [Test]
        public void Forge_PathCorruptionVariants_Rejected()
        {
            string rooted = IsWindows ? "C:\\rooted.png.stage" : "/rooted.png.stage";
            string[] corruptions =
            {
                rooted,
                "../frames/10.png.stage",
                "other/10.png.stage",
                "frames/10.PNG.STAGE"
            };

            foreach (string corruption in corruptions)
            {
                PngJsonCapturePublicationPlanEntry entry = MakeEntry(10);
                CaptureRunPublicationRecoveryDecision decision = MakeDecision(MakePlan(entries: new[] { entry }));

                SetField(entry, "_pngStagingRelativePath", corruption);
                Assert.That(entry.IsValid, Is.False, corruption);
                Assert.That(decision.IsValid, Is.False, corruption);

                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => new CaptureRunPublicationArtifactPathSet(decision, 0));
                Assert.That(ex.ParamName, Is.EqualTo("decision"));
            }
        }

        [Test]
        public void Forge_RootLayoutCorruption_IsValidFalse()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);
            Assert.That(pathSet.IsValid, Is.True);

            SetField(decision.RootLayout, "_stagingRunRoot", "relative");
            Assert.That(pathSet.IsValid, Is.False);
        }

        [Test]
        public void OwnerRelease_IsValidFalse_NoException()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            Assert.That(pathSet.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();

            Assert.That(decision.IsValid, Is.False);
            Assert.That(pathSet.IsValid, Is.False);
            Assert.That(disposeLog, Is.Not.Empty);
        }

        [Test]
        public void ConstructorFailure_LeavesInputUnchanged()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunPublicationArtifactPathSet(decision, -1));

            Assert.That(decision.IsValid, Is.True);
            Assert.That(plan.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(disposeLog, Is.Empty, "Constructor failure must not dispose the owner.");
        }

        [Test]
        public void NoFilesystemContact()
        {
            CaptureRunPublicationRecoveryDecision decision = MakeDecision();
            CaptureRunPublicationArtifactPathSet pathSet = new CaptureRunPublicationArtifactPathSet(decision, 0);

            Assert.That(File.Exists(pathSet.StagingPngPath), Is.False);
            Assert.That(File.Exists(pathSet.StagingSidecarPath), Is.False);
            Assert.That(File.Exists(pathSet.FinalPngPath), Is.False);
            Assert.That(File.Exists(pathSet.FinalSidecarPath), Is.False);
            Assert.That(Directory.Exists(decision.Snapshot.Operation.PublicationPaths.StagingFramesRoot), Is.False);
            Assert.That(Directory.Exists(decision.Snapshot.Operation.PublicationPaths.FinalFramesRoot), Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Shape_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationArtifactPathSet);

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
        public void Shape_FieldsReadonly_NoArraysOrCollections()
        {
            Type type = typeof(CaptureRunPublicationArtifactPathSet);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(6));

            int decisionFields = 0;
            int intFields = 0;
            int stringFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(typeof(System.Collections.ICollection).IsAssignableFrom(field.FieldType), Is.False,
                    field.Name + " must not be an array or mutable collection.");
                if (field.FieldType == typeof(CaptureRunPublicationRecoveryDecision)) decisionFields++;
                else if (field.FieldType == typeof(int)) intFields++;
                else if (field.FieldType == typeof(string)) stringFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(decisionFields, Is.EqualTo(1));
            Assert.That(intFields, Is.EqualTo(1));
            Assert.That(stringFields, Is.EqualTo(4));
        }

        [Test]
        public void Shape_NoMutableStaticState()
        {
            Type type = typeof(CaptureRunPublicationArtifactPathSet);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        [Test]
        public void Shape_NoLeaseOrIdentityExposure()
        {
            Type type = typeof(CaptureRunPublicationArtifactPathSet);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(
                    field.FieldType == typeof(CaptureRunLockLease)
                    || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    field.Name + " must not hold a raw or ownership lease.");
            }

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    prop.Name + " must not expose a raw or ownership lease.");
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockIdentityEvidence),
                    Is.False,
                    prop.Name + " must not expose the lock identity evidence directly.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    method.ReturnType == typeof(CaptureRunLockLease)
                    || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    method.Name + " must not return a raw or ownership lease.");
                Assert.That(
                    method.ReturnType == typeof(CaptureRunLockIdentityEvidence),
                    Is.False,
                    method.Name + " must not return the lock identity evidence directly.");
            }
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationArtifactPathSet.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("SHA"));
            Assert.That(source, Does.Not.Contain("Codec"));
            Assert.That(source, Does.Not.Contain("Serialize"));
            Assert.That(source, Does.Not.Contain("Deserialize"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
        }
    }
}
