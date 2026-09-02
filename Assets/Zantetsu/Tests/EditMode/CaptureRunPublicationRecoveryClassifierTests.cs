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
    public class CaptureRunPublicationRecoveryClassifierTests
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

        private static CaptureRunPublicationDocumentKind PublicationPlanTemporary => CaptureRunPublicationDocumentKind.PublicationPlanTemporary;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationDocumentObservationStatus DocLimitExceeded => CaptureRunPublicationDocumentObservationStatus.LimitExceeded;

        private static CaptureRunPublicationFramesObservationStatus FrameInvalid => CaptureRunPublicationFramesObservationStatus.Invalid;

        private static CaptureRunPublicationRecoveryDisposition NoAuthoritativeDocument => CaptureRunPublicationRecoveryDisposition.NoAuthoritativeDocument;

        private static CaptureRunPublicationRecoveryDisposition PublicationPlanAuthoritative => CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative;

        private static CaptureRunPublicationRecoveryDisposition CaptureIndexAuthoritative => CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative;

        private static CaptureRunPublicationRecoveryDisposition RunRootCollision => CaptureRunPublicationRecoveryDisposition.RunRootCollision;

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
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(),
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
                publicationPlanTemporary ?? MakeDoc(PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                stagingFramesStatus,
                finalFramesStatus,
                stagingHasUnexpectedEntries,
                finalHasUnexpectedEntries,
                stagingRootEntryLimitExceeded,
                finalRootEntryLimitExceeded);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot ForgeSnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null)
        {
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = (CaptureRunPublicationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionSnapshot));
            SetField(snapshot, "_issuedBy", issuedBy);
            SetField(snapshot, "_operation", operation);
            SetField(snapshot, "_publicationPlanTemporary", publicationPlanTemporary ?? MakeDoc(PublicationPlanTemporary, DocAbsent));
            SetField(snapshot, "_publicationPlan", publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent));
            SetField(snapshot, "_captureIndexTemporary", captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent));
            SetField(snapshot, "_captureIndex", captureIndex ?? MakeDoc(CaptureIndex, DocAbsent));
            SetField(snapshot, "_stagingFramesStatus", CaptureRunPublicationFramesObservationStatus.Directory);
            SetField(snapshot, "_finalFramesStatus", CaptureRunPublicationFramesObservationStatus.Directory);
            return snapshot;
        }

        private CaptureRunPublicationRecoveryDecision Classify(
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
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                inspector,
                operation,
                publicationPlanTemporary,
                publicationPlan,
                captureIndexTemporary,
                captureIndex,
                stagingFramesStatus,
                finalFramesStatus,
                stagingHasUnexpectedEntries,
                finalHasUnexpectedEntries,
                stagingRootEntryLimitExceeded,
                finalRootEntryLimitExceeded);
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunPublicationRecoveryClassifierTests).Assembly.Location);
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

        // ---- Enum ----

        [Test]
        public void Enum_Contract()
        {
            AssertEnumContract(typeof(CaptureRunPublicationRecoveryDisposition),
                new[] { "None", "NoAuthoritativeDocument", "PublicationPlanAuthoritative", "CaptureIndexAuthoritative", "RunRootCollision" });
        }

        // ---- Rejection ----

        [Test]
        public void Classify_NullSnapshot_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunPublicationRecoveryClassifier.Classify(null));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        [Test]
        public void Classify_InvalidSnapshot_Rejected()
        {
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = (CaptureRunPublicationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryInspectionSnapshot));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunPublicationRecoveryClassifier.Classify(snapshot));
            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        // ---- Collision priority ----

        [Test]
        public void Classify_RootEntryLimitExceeded_Collision()
        {
            Assert.That(Classify(stagingRootEntryLimitExceeded: true).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(finalRootEntryLimitExceeded: true).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_UnexpectedEntries_Collision()
        {
            Assert.That(Classify(stagingHasUnexpectedEntries: true).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(finalHasUnexpectedEntries: true).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_FramesInvalid_Collision()
        {
            Assert.That(Classify(stagingFramesStatus: FrameInvalid).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(finalFramesStatus: FrameInvalid).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_EachDocumentLimitExceeded_Collision()
        {
            Assert.That(Classify(publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocLimitExceeded, 1001)).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(publicationPlan: MakeDoc(PublicationPlan, DocLimitExceeded, 1001)).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocLimitExceeded, 1001)).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(captureIndex: MakeDoc(CaptureIndex, DocLimitExceeded, 1001)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_AuthoritativeInvalid_Collision()
        {
            Assert.That(Classify(publicationPlan: MakeDoc(PublicationPlan, DocInvalid, 0)).Disposition, Is.EqualTo(RunRootCollision));
            Assert.That(Classify(captureIndex: MakeDoc(CaptureIndex, DocInvalid, 0)).Disposition, Is.EqualTo(RunRootCollision));
        }

        // ---- Authoritative dispositions ----

        [Test]
        public void Classify_PlanOnly_PlanAuthoritative()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryDecision decision = Classify(publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));

            Assert.That(decision.Disposition, Is.EqualTo(PublicationPlanAuthoritative));
            Assert.That(decision.AuthoritativePlan, Is.SameAs(plan));
        }

        [Test]
        public void Classify_IndexOnly_IndexAuthoritative()
        {
            PngJsonCapturePublicationPlan indexPlan = MakePlan();
            CaptureRunPublicationRecoveryDecision decision = Classify(captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, indexPlan));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureIndexAuthoritative));
            Assert.That(decision.AuthoritativePlan, Is.SameAs(indexPlan));
        }

        [Test]
        public void Classify_PlanAndIndexEqual_IndexPriority()
        {
            PngJsonCapturePublicationPlan planValue = MakePlan();
            PngJsonCapturePublicationPlan indexValue = MakePlan();

            CaptureRunPublicationRecoveryDecision decision = Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, planValue),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, indexValue));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureIndexAuthoritative));
            Assert.That(decision.AuthoritativePlan, Is.SameAs(indexValue));
        }

        [Test]
        public void Classify_NoAuthoritativeDocument()
        {
            CaptureRunPublicationRecoveryDecision decision = Classify();

            Assert.That(decision.Disposition, Is.EqualTo(NoAuthoritativeDocument));
            Assert.That(decision.AuthoritativePlan, Is.Null);
        }

        [Test]
        public void Classify_TmpOnly_NotAuthoritative()
        {
            PngJsonCapturePublicationPlan tmpPlan = MakePlan();

            Assert.That(Classify(publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocCanonical, 100, tmpPlan)).Disposition, Is.EqualTo(NoAuthoritativeDocument));
            Assert.That(Classify(captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, tmpPlan)).Disposition, Is.EqualTo(NoAuthoritativeDocument));
        }

        [Test]
        public void Classify_ConsistentTmpPair_NotAuthoritative()
        {
            PngJsonCapturePublicationPlan tmpPlan = MakePlan();

            Assert.That(Classify(
                publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocCanonical, 100, tmpPlan),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, MakePlan())).Disposition, Is.EqualTo(NoAuthoritativeDocument));
        }

        [Test]
        public void Classify_InvalidTmp_DoesNotBlock()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryDecision decision = Classify(
                publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocInvalid, 0),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocInvalid, 0),
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));

            Assert.That(decision.Disposition, Is.EqualTo(PublicationPlanAuthoritative));
            Assert.That(decision.AuthoritativePlan, Is.SameAs(plan));
        }

        // ---- Cross-document mismatch ----

        [Test]
        public void Classify_TestRunIdMismatch_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(testRunId: 2);
            Assert.That(Classify(publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_InitIdMismatch_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(initId: OtherInitId);
            Assert.That(Classify(publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_ManifestHashMismatch_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(manifestHash: StagingHash);
            PngJsonCapturePublicationPlan index = MakePlan(manifestHash: FinalHash);

            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, index)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_PlanVsIndexMismatch_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10) });
            PngJsonCapturePublicationPlan index = MakePlan(entries: new[] { MakeEntry(20) });

            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, index)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_PlanVsEachTmpMismatch_Collision()
        {
            PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { MakeEntry(10) });
            PngJsonCapturePublicationPlan tmp = MakePlan(entries: new[] { MakeEntry(20) });

            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocCanonical, 100, tmp)).Disposition, Is.EqualTo(RunRootCollision));

            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, tmp)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_IndexVsEachTmpMismatch_Collision()
        {
            PngJsonCapturePublicationPlan index = MakePlan(entries: new[] { MakeEntry(10) });
            PngJsonCapturePublicationPlan tmp = MakePlan(entries: new[] { MakeEntry(20) });

            Assert.That(Classify(
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, index),
                publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocCanonical, 100, tmp)).Disposition, Is.EqualTo(RunRootCollision));

            Assert.That(Classify(
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, index),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, tmp)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_TmpPairMismatch_Collision()
        {
            PngJsonCapturePublicationPlan planTmp = MakePlan(entries: new[] { MakeEntry(10) });
            PngJsonCapturePublicationPlan indexTmp = MakePlan(entries: new[] { MakeEntry(20) });

            Assert.That(Classify(
                publicationPlanTemporary: MakeDoc(PublicationPlanTemporary, DocCanonical, 100, planTmp),
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, indexTmp)).Disposition, Is.EqualTo(RunRootCollision));
        }

        [Test]
        public void Classify_SingleFieldDifference_Collision()
        {
            // EntryCount.
            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10), MakeEntry(20) })),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10) }))).Disposition, Is.EqualTo(RunRootCollision));

            // CaptureFrameId.
            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10) })),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(11) }))).Disposition, Is.EqualTo(RunRootCollision));

            // PngByteLength.
            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, pngByteLength: 16) })),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, pngByteLength: 32) }))).Disposition, Is.EqualTo(RunRootCollision));

            // SidecarByteLength.
            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, sidecarByteLength: 32) })),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, sidecarByteLength: 64) }))).Disposition, Is.EqualTo(RunRootCollision));

            // PngContentSha256.
            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, pngHash: StagingHash) })),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, pngHash: FinalHash) }))).Disposition, Is.EqualTo(RunRootCollision));

            // SidecarContentSha256.
            Assert.That(Classify(
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, sidecarHash: StagingHash) })),
                captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10, sidecarHash: FinalHash) }))).Disposition, Is.EqualTo(RunRootCollision));
        }

        // ---- Decision correlation ----

        [Test]
        public void Decision_HoldsAuthoritativePlanByReference()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationRecoveryDecision decision = Classify(publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));

            Assert.That(decision.Snapshot.PublicationPlan.Plan, Is.SameAs(plan));
            Assert.That(decision.AuthoritativePlan, Is.SameAs(plan));
            Assert.That(decision.RootLayout, Is.SameAs(decision.Snapshot.Operation.RootLayout));
            Assert.That(decision.TestRunId, Is.EqualTo(1));
            Assert.That(decision.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Decision_ForgedDispositionOrPlan_IsValidFalse()
        {
            CaptureRunPublicationRecoveryDecision decision = Classify(publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan()));
            Assert.That(decision.IsValid, Is.True);

            CaptureRunPublicationRecoveryDecision forgedDisposition = (CaptureRunPublicationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryDecision));
            SetField(forgedDisposition, "_snapshot", decision.Snapshot);
            SetField(forgedDisposition, "_disposition", RunRootCollision);
            SetField(forgedDisposition, "_authoritativePlan", decision.AuthoritativePlan);
            Assert.That(forgedDisposition.IsValid, Is.False);

            CaptureRunPublicationRecoveryDecision forgedPlan = (CaptureRunPublicationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryDecision));
            SetField(forgedPlan, "_snapshot", decision.Snapshot);
            SetField(forgedPlan, "_disposition", decision.Disposition);
            SetField(forgedPlan, "_authoritativePlan", MakePlan());
            Assert.That(forgedPlan.IsValid, Is.False);
        }

        [Test]
        public void Decision_Uninitialized_IsInvalid()
        {
            CaptureRunPublicationRecoveryDecision decision = (CaptureRunPublicationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationRecoveryDecision));
            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Classify_ForgedNestedCorruption_NoException()
        {
            PngJsonCapturePublicationPlan uninitializedPlan = (PngJsonCapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(PngJsonCapturePublicationPlan));
            Assert.That(uninitializedPlan.IsValid, Is.False);

            PngJsonCapturePublicationPlan nullArrayPlan = (PngJsonCapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(PngJsonCapturePublicationPlan));
            SetField(nullArrayPlan, "_testRunId", 1L);
            SetField(nullArrayPlan, "_runInitializationId", InitId);
            SetField(nullArrayPlan, "_runManifestContentSha256", StagingHash);
            SetField(nullArrayPlan, "_entries", null);
            Assert.That(nullArrayPlan.IsValid, Is.False);

            PngJsonCapturePublicationPlan nullEntryPlan = (PngJsonCapturePublicationPlan)FormatterServices.GetUninitializedObject(typeof(PngJsonCapturePublicationPlan));
            SetField(nullEntryPlan, "_testRunId", 1L);
            SetField(nullEntryPlan, "_runInitializationId", InitId);
            SetField(nullEntryPlan, "_runManifestContentSha256", StagingHash);
            SetField(nullEntryPlan, "_entries", new PngJsonCapturePublicationPlanEntry[] { null });
            Assert.That(nullEntryPlan.IsValid, Is.False);

            PngJsonCapturePublicationPlanEntry uninitializedEntry = (PngJsonCapturePublicationPlanEntry)FormatterServices.GetUninitializedObject(typeof(PngJsonCapturePublicationPlanEntry));
            Assert.That(uninitializedEntry.IsValid, Is.False);
        }

        [Test]
        public void Classify_ForgedPathDifference_Collision_NoException()
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
                PngJsonCapturePublicationPlanEntry forgedEntry = MakeEntry(10);
                SetField(forgedEntry, pathField, "frames/forged.png");
                Assert.That(forgedEntry.IsValid, Is.False, pathField);

                PngJsonCapturePublicationPlan plan = MakePlan(entries: new[] { forgedEntry });
                Assert.That(plan.IsValid, Is.False, pathField);

                CaptureRunPublicationDocumentObservation forgedObservation = (CaptureRunPublicationDocumentObservation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunPublicationDocumentObservation));
                SetField(forgedObservation, "_kind", PublicationPlan);
                SetField(forgedObservation, "_status", DocCanonical);
                SetField(forgedObservation, "_probedByteCount", 100);
                SetField(forgedObservation, "_plan", plan);

                FakePublicationInspector inspector = new FakePublicationInspector();
                CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();
                CaptureRunPublicationRecoveryInspectionSnapshot snapshot = ForgeSnapshot(
                    inspector,
                    operation,
                    publicationPlan: forgedObservation,
                    captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, MakePlan(entries: new[] { MakeEntry(10) })));

                Assert.That(snapshot.IsValid, Is.False, pathField);
                Assert.Throws<ArgumentException>(() => CaptureRunPublicationRecoveryClassifier.Classify(snapshot));
            }
        }

        [Test]
        public void Decision_OwnerRelease_Invalid_NoException()
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                inspector, operation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan()));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();

            Assert.That(snapshot.IsValid, Is.False);
            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Classify_DoesNotMutateOrDisposeInputs()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationRecoveryInspectionOperation operation = new CaptureRunPublicationRecoveryInspectionOperation(outcome, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationDocumentObservation observation = MakeDoc(PublicationPlan, DocCanonical, 100, plan);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(inspector, operation, publicationPlan: observation);

            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Snapshot, Is.SameAs(snapshot));
            Assert.That(snapshot.PublicationPlan, Is.SameAs(observation));
            Assert.That(snapshot.PublicationPlan.Plan, Is.SameAs(plan));
            Assert.That(disposeLog, Is.Empty, "Classification must not dispose the owner.");
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
        }

        [Test]
        public void Classify_ForgedOutcomeCorruption_Rejected_NoException()
        {
            // 1. OpenOutcome _lockIdentityEvidence = null
            CaptureRunInitializationOpenOutcome nullIdentity = MakePublicationRecoveryOutcome();
            SetField(nullIdentity, "_lockIdentityEvidence", null);
            Assert.That(nullIdentity.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationRecoveryInspectionOperation(nullIdentity, 1000, 4, 64));

            // 2. Foreign identity swap
            CaptureRunInitializationOpenOutcome foreignIdentityOutcome = MakePublicationRecoveryOutcome();
            MakeOwner(MakeLayout(), null, out CaptureRunLockIdentityEvidence foreignIdentity);
            SetField(foreignIdentityOutcome, "_lockIdentityEvidence", foreignIdentity);
            Assert.That(foreignIdentityOutcome.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationRecoveryInspectionOperation(foreignIdentityOutcome, 1000, 4, 64));

            // 3. OpenOutcome _orchestrationResult swap
            CaptureRunInitializationOpenOutcome swappedResult = MakePublicationRecoveryOutcome();
            CaptureRunInitializationOpenOutcome other = MakePublicationRecoveryOutcome();
            SetField(swappedResult, "_orchestrationResult", other.OrchestrationResult);
            Assert.That(swappedResult.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationRecoveryInspectionOperation(swappedResult, 1000, 4, 64));

            // 4. Nested initialization recovery operation identity swap
            CaptureRunInitializationOpenOutcome nestedSwap = MakePublicationRecoveryOutcome();
            CaptureRunInitializationRecoveryInspectionOperation nested = nestedSwap.OrchestrationResult.Snapshot.Operation;
            MakeOwner(MakeLayout(), null, out CaptureRunLockIdentityEvidence nestedForeign);
            SetField(nested, "_lockIdentityEvidence", nestedForeign);
            Assert.That(nestedSwap.IsValid, Is.False);
            Assert.Throws<ArgumentException>(() => new CaptureRunPublicationRecoveryInspectionOperation(nestedSwap, 1000, 4, 64));

            // 5. Publication recovery operation open-outcome swap
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            SetField(operation, "_openOutcome", MakePublicationRecoveryOutcome());
            Assert.That(operation.IsValid, Is.False);

            // 6. Owner release
            CaptureRunInitializationOpenOutcome ownerReleased = MakePublicationRecoveryOutcome(out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationRecoveryInspectionOperation ownerOperation =
                new CaptureRunPublicationRecoveryInspectionOperation(ownerReleased, 1000, 4, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                inspector, ownerOperation, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, MakePlan()));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();

            Assert.That(ownerReleased.IsValid, Is.False);
            Assert.That(snapshot.IsValid, Is.False);
            Assert.That(decision.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Decision_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunPublicationRecoveryDecision);

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
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    method.ReturnType == typeof(CaptureRunLockLease)
                    || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    method.Name + " must not return a raw or ownership lease.");
            }
        }

        [Test]
        public void OperationSnapshot_Shape_NoLeaseExposure()
        {
            foreach (Type type in new[] { typeof(CaptureRunPublicationRecoveryInspectionOperation), typeof(CaptureRunPublicationRecoveryInspectionSnapshot) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(
                        field.FieldType == typeof(CaptureRunLockLease)
                        || field.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease),
                        Is.False,
                        type.Name + "." + field.Name + " must not hold a raw or ownership lease.");
                }

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        prop.PropertyType == typeof(CaptureRunLockLease)
                        || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                        Is.False,
                        type.Name + "." + prop.Name + " must not expose a raw or ownership lease.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(
                        method.ReturnType == typeof(CaptureRunLockLease)
                        || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                        Is.False,
                        type.Name + "." + method.Name + " must not return a raw or ownership lease.");
                }
            }
        }

        [Test]
        public void Classifier_IsStaticWithNoState()
        {
            Type type = typeof(CaptureRunPublicationRecoveryClassifier);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    prop.PropertyType == typeof(CaptureRunLockLease)
                    || prop.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    prop.Name + " must not expose a raw or ownership lease.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(
                    method.ReturnType == typeof(CaptureRunLockLease)
                    || method.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease),
                    Is.False,
                    method.Name + " must not return a raw or ownership lease.");
            }
        }

        [Test]
        public void Classifier_Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunPublicationRecoveryClassifier.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("SHA"));
            Assert.That(source, Does.Not.Contain("Serialize"));
            Assert.That(source, Does.Not.Contain("Deserialize"));
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("HashSet"));
            Assert.That(source, Does.Not.Contain("Dictionary"));
        }
    }
}
