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
    public class CaptureRunCaptureIndexCommitOperationFactoryTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const long PngBytes = 16;

        private const long SidecarBytes = 32;

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationDocumentObservationStatus DocLimitExceeded => CaptureRunPublicationDocumentObservationStatus.LimitExceeded;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private static CaptureRunPublicationEvidenceStatus EvMismatch => CaptureRunPublicationEvidenceStatus.Mismatch;

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
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOutcome(disposeLog, out owner, out _);
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
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            PngJsonCapturePublicationPlan plan = null,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            int maximumEntryCount = 4)
        {
            return MakeOperation(disposeLog, plan, publicationPlanTemporary, publicationPlan, captureIndexTemporary, captureIndex, maximumEntryCount, out _);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeOperation(null, null, null, null, null, null, 4, out owner);
        }

        private CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog,
            PngJsonCapturePublicationPlan plan,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            CaptureRunPublicationDocumentObservation publicationPlan,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationDocumentObservation captureIndex,
            int maximumEntryCount,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog, out owner);
            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = new CaptureRunPublicationRecoveryInspectionOperation(
                outcome, 1000, maximumEntryCount, 64);
            FakePublicationInspector inspector = new FakePublicationInspector();
            plan = plan ?? MakePlan();
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = MakeRecoverySnapshot(
                inspector,
                recoveryOperation,
                publicationPlanTemporary: publicationPlanTemporary,
                publicationPlan: publicationPlan ?? MakeDoc(PublicationPlan, DocCanonical, 100, plan),
                captureIndexTemporary: captureIndexTemporary,
                captureIndex: captureIndex);
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

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildPlan(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactEntryObservation[] entries,
            CaptureRunPublicationEvidenceStatus traceStatus = CaptureRunPublicationEvidenceStatus.MatchesExpected,
            long traceCount = 100)
        {
            return CaptureRunPublicationArtifactRecoveryActionPlanBuilder.Build(
                CaptureRunPublicationArtifactRecoveryClassifier.Classify(
                    MakeArtifactSnapshot(new FakeArtifactInspector(), operation, traceStatus, traceCount, entries)));
        }

        private CaptureRunPublicationArtifactRecoveryActionPlan BuildCommitPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            PngJsonCapturePublicationPlan plan = null)
        {
            return BuildCommitPlan(out operation, out observation, captureIndexTemporary, plan, out _);
        }

        private CaptureRunPublicationArtifactRecoveryActionPlan BuildCommitPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return BuildCommitPlan(out operation, out observation, null, null, out owner);
        }

        private CaptureRunPublicationArtifactRecoveryActionPlan BuildCommitPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            PngJsonCapturePublicationPlan plan,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            operation = MakeOperation(null, plan, null, null, captureIndexTemporary, null, 4, out owner);
            observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvMatchesExpected,
                finalPngCount: PngBytes,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishPngPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation)
        {
            return BuildPublishPngPlan(out operation, out _);
        }

        private CaptureRunPublicationArtifactRecoveryActionPlan BuildPublishPngPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            operation = MakeOperation(out owner);
            CaptureRunPublicationArtifactEntryObservation observation = MakeEntryObservation(
                operation,
                operation.GetArtifactPaths(0),
                stagingPngStatus: EvMatchesExpected,
                stagingPngCount: PngBytes,
                stagingSidecarStatus: EvMatchesExpected,
                stagingSidecarCount: SidecarBytes,
                finalPngStatus: EvAbsent,
                finalPngCount: 0,
                finalSidecarStatus: EvMatchesExpected,
                finalSidecarCount: SidecarBytes);
            return BuildPlan(operation, new[] { observation });
        }

        private static CaptureRunPublicationPathSet GetPublicationPaths(CaptureRunPublicationArtifactRecoveryActionPlan plan)
        {
            return plan.Decision.PublicationDecision.Snapshot.Operation.PublicationPaths;
        }

        private static CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken MintBytesToken(
            PngJsonCapturePublicationPlan plan)
        {
            return CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken.Acquire(plan);
        }

        private static CaptureRunCaptureIndexCommitOperation ForgeOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunCaptureIndexCommitMode mode,
            byte[] canonicalBytes)
        {
            CaptureRunCaptureIndexCommitOperation operation = (CaptureRunCaptureIndexCommitOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunCaptureIndexCommitOperation));
            SetField(operation, "_actionPlan", actionPlan);
            SetField(operation, "_stepIndex", stepIndex);
            SetField(operation, "_publicationPaths", publicationPaths);
            SetField(operation, "_mode", mode);
            SetField(operation, "_canonicalBytes", canonicalBytes);
            return operation;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunCaptureIndexCommitOperationFactoryTests).Assembly.Location);
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
        public void Mode_UnderlyingTypeAndFixedValues()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(CaptureRunCaptureIndexCommitMode)), Is.EqualTo(typeof(int)));
            Assert.That((int)CaptureRunCaptureIndexCommitMode.None, Is.EqualTo(0));
            Assert.That((int)CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, Is.EqualTo(1));
            Assert.That((int)CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit, Is.EqualTo(2));
            Assert.That((int)CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, Is.EqualTo(3));
            Assert.That(Enum.GetValues(typeof(CaptureRunCaptureIndexCommitMode)).Length, Is.EqualTo(4));

            Array values = Enum.GetValues(typeof(CaptureRunCaptureIndexCommitMode));
            for (int i = 0; i < values.Length; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i), "Values must be contiguous from zero without aliases.");
            }
        }

        // ---- Mode derivation ----

        [Test]
        public void Operation_AbsentTemporary_CreateMode()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_CanonicalTemporary_ReuseMode()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(
                out _, out _,
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan),
                plan: plan);

            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(actionPlan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_InvalidTemporary_ReplaceMode()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(
                out _, out _,
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocInvalid, 10));

            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(actionPlan, 0);

            Assert.That(commit.Mode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void DeriveMode_LimitExceeded_Rejected()
        {
            CaptureRunPublicationDocumentObservation limitExceeded = MakeDoc(CaptureIndexTemporary, DocLimitExceeded, 1001);
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunCaptureIndexCommitOperation.DeriveMode(limitExceeded));
            Assert.That(ex.ParamName, Is.EqualTo("captureIndexTemporary"));
        }

        [Test]
        public void Operation_LimitExceededTemporary_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            CaptureRunPublicationDocumentObservation tmp = actionPlan.Decision.PublicationDecision.Snapshot.CaptureIndexTemporary;
            SetField(tmp, "_status", DocLimitExceeded);
            SetField(tmp, "_probedByteCount", 1001);

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(actionPlan.AuthoritativePlan);

            Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
            Assert.That(bytesToken, Is.Not.Null);
        }

        [Test]
        public void Operation_InvalidTemporaryObservation_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            CaptureRunPublicationDocumentObservation tmp = actionPlan.Decision.PublicationDecision.Snapshot.CaptureIndexTemporary;
            PngJsonCapturePublicationPlan authoritativePlan = actionPlan.AuthoritativePlan;

            // Invalid status with a negative probed byte count is inconsistent.
            SetField(tmp, "_status", DocInvalid);
            SetField(tmp, "_probedByteCount", -5);

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(authoritativePlan);
            Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
            Assert.That(bytesToken, Is.Not.Null);

            // Absent status with a non-null plan is inconsistent.
            SetField(tmp, "_status", DocAbsent);
            SetField(tmp, "_probedByteCount", 0);
            SetField(tmp, "_plan", MakePlan());

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken2 = MintBytesToken(authoritativePlan);
            Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken2));
            Assert.That(bytesToken2, Is.Not.Null);
        }

        // ---- Rejection ----

        [Test]
        public void Factory_NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunCaptureIndexCommitOperationFactory.Create(null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_InvalidPlan_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = (CaptureRunPublicationArtifactRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunPublicationArtifactRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Factory_StepIndexOutOfRange_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);

            foreach (int bad in new[] { -1, 2, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => CaptureRunCaptureIndexCommitOperationFactory.Create(plan, bad));
                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Factory_NonCommitStep_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildPublishPngPlan(out _);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Operation_CommittedIndexNotAbsent_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();
            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = actionPlan.Decision.PublicationDecision.Snapshot;
            PngJsonCapturePublicationPlan plan = actionPlan.AuthoritativePlan;

            CaptureRunPublicationDocumentObservation[] forged = new[]
            {
                MakeDoc(CaptureIndex, DocCanonical, 100, plan),
                MakeDoc(CaptureIndex, DocInvalid, 10),
                MakeDoc(CaptureIndex, DocLimitExceeded, 1001)
            };

            foreach (CaptureRunPublicationDocumentObservation index in forged)
            {
                SetField(publicationSnapshot, "_captureIndex", index);

                CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(plan);

                Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
                Assert.That(bytesToken, Is.Not.Null);
            }
        }

        [Test]
        public void Operation_CanonicalTemporaryMismatch_Rejected()
        {
            PngJsonCapturePublicationPlan plan = MakePlan();
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(
                out _, out _,
                captureIndexTemporary: MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan),
                plan: plan);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = actionPlan.Decision.PublicationDecision.Snapshot;
            SetField(publicationSnapshot.CaptureIndexTemporary, "_plan", MakePlan(entries: new[] { MakeEntry(11) }));

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(actionPlan.AuthoritativePlan);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
            Assert.That(bytesToken, Is.Not.Null);
        }

        [Test]
        public void Operation_PublicationDecisionNonAuthoritative_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            SetField(actionPlan.Decision.PublicationDecision, "_disposition", CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative);

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(actionPlan.AuthoritativePlan);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
            Assert.That(bytesToken, Is.Not.Null);
        }

        [Test]
        public void Operation_TraceMismatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            SetField(actionPlan.Decision.Snapshot, "_traceManifestStatus", EvMismatch);

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(actionPlan.AuthoritativePlan);

            Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
            Assert.That(bytesToken, Is.Not.Null);
        }

        [Test]
        public void Operation_FinalArtifactMissingOrMismatch_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(out _, out CaptureRunPublicationArtifactEntryObservation observation);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = actionPlan.AcquireValidationToken();

            SetField(observation, "_finalPngStatus", EvAbsent);
            SetField(observation, "_finalPngProbedByteCount", 0);

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(actionPlan.AuthoritativePlan);

            Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(actionPlan, token, 0, ref bytesToken));
            Assert.That(bytesToken, Is.Not.Null);
        }

        // ---- Token ----

        [Test]
        public void Factory_CrossToken_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan planA = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan planB = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken tokenA = planA.AcquireValidationToken();

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                CaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(planB, tokenA, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        [Test]
        public void Factory_StaleToken_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(
                out CaptureRunPublicationArtifactInspectionOperation operation,
                out _,
                out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            owner.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                CaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(plan, token, 0));
            Assert.That(ex.ParamName, Is.EqualTo("token"));
        }

        // ---- Forwarding / paths / bytes ----

        [Test]
        public void Operation_ForwardsAllValues()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);

            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.ActionPlan, Is.SameAs(plan));
            Assert.That(commit.StepIndex, Is.EqualTo(0));
            Assert.That(commit.Step, Is.SameAs(plan.GetStep(0)));
            Assert.That(commit.Decision, Is.SameAs(plan.Decision));
            Assert.That(commit.PublicationDecision, Is.SameAs(plan.Decision.PublicationDecision));
            Assert.That(commit.AuthoritativePlan, Is.SameAs(plan.AuthoritativePlan));
            Assert.That(commit.RootLayout, Is.SameAs(plan.RootLayout));
            Assert.That(commit.TestRunId, Is.EqualTo(1));
            Assert.That(commit.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(commit.IsValid, Is.True);
        }

        [Test]
        public void Operation_TemporaryAndFinalPathExact()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);

            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            Assert.That(commit.TemporaryPath, Is.EqualTo(paths.CaptureIndexTemporaryPath));
            Assert.That(commit.FinalPath, Is.EqualTo(paths.CaptureIndexPath));
            Assert.That(commit.TemporaryPath, Is.Not.EqualTo(commit.FinalPath));
            Assert.That(Path.GetFileName(commit.TemporaryPath), Is.EqualTo("capture.index.tmp"));
            Assert.That(Path.GetFileName(commit.FinalPath), Is.EqualTo("capture.index"));
        }

        [Test]
        public void Operation_CanonicalBytesMatchCodecOutput()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);

            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            byte[] expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(commit.AuthoritativePlan);
            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(expected));
            Assert.That(commit.ByteCount, Is.EqualTo((long)expected.Length));
        }

        // ---- Bytes ownership ----

        [Test]
        public void Constructor_Success_NullsRefAndTakesOwnership()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(plan.AuthoritativePlan);
            byte[] expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan.AuthoritativePlan);

            CaptureRunCaptureIndexCommitOperation commit = new CaptureRunCaptureIndexCommitOperation(plan, token, 0, ref bytesToken);

            Assert.That(bytesToken, Is.Null);
            Assert.That(commit.IsValid, Is.True);
            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void Constructor_Failure_TokenNotTransferred()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            // Step index out of range.
            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken1 = MintBytesToken(plan.AuthoritativePlan);
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunCaptureIndexCommitOperation(plan, token, 99, ref bytesToken1));
            Assert.That(bytesToken1, Is.Not.Null);

            // Null token.
            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken nullToken = null;
            Assert.Throws<ArgumentNullException>(() => new CaptureRunCaptureIndexCommitOperation(plan, token, 0, ref nullToken));
            Assert.That(nullToken, Is.Null);

            // Forged trace status.
            SetField(plan.Decision.Snapshot, "_traceManifestStatus", EvMismatch);
            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken2 = MintBytesToken(plan.AuthoritativePlan);
            Assert.Throws<ArgumentException>(() => new CaptureRunCaptureIndexCommitOperation(plan, token, 0, ref bytesToken2));
            Assert.That(bytesToken2, Is.Not.Null);
        }

        [Test]
        public void CanonicalBytesToken_ConsumedOrModifiedBytes_Rejected_NoOwnershipTransfer()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            // The token owns its byte array privately; TakeBytes consumes that
            // ownership. After the bytes are taken and mutated in place, the
            // empty token cannot be used to construct an operation and is not
            // nulled out (no ownership transfer).
            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(plan.AuthoritativePlan);
            byte[] bytes = bytesToken.TakeBytes();
            bytes[0] = (byte)(bytes[0] ^ 0xFF);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunCaptureIndexCommitOperation(plan, token, 0, ref bytesToken));
            Assert.That(ex.ParamName, Is.EqualTo("canonicalBytesToken"));
            Assert.That(bytesToken, Is.Not.Null);
        }

        [Test]
        public void CanonicalBytesToken_Acquire_ReturnsCanonicalSerialization()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken =
                CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken.Acquire(plan.AuthoritativePlan);

            Assert.That(bytesToken.IsIssuedFor(plan.AuthoritativePlan), Is.True);
            Assert.That(bytesToken.IsIssuedFor(MakePlan()), Is.False);

            byte[] expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan.AuthoritativePlan);
            Assert.That(bytesToken.TakeBytes(), Is.EqualTo(expected));

            // After TakeBytes the token is no longer issued for any plan.
            Assert.That(bytesToken.IsIssuedFor(plan.AuthoritativePlan), Is.False);
        }

        [Test]
        public void CanonicalBytesToken_EmptyBytes_Rejected_TokenNotConsumed()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token = plan.AcquireValidationToken();

            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken bytesToken = MintBytesToken(plan.AuthoritativePlan);
            CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken original = bytesToken;

            FieldInfo bytesField = typeof(CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken)
                .GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance);
            byte[] empty = new byte[0];
            bytesField.SetValue(bytesToken, empty);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                new CaptureRunCaptureIndexCommitOperation(plan, token, 0, ref bytesToken));

            Assert.That(ex.ParamName, Is.EqualTo("canonicalBytesToken"));
            Assert.That(bytesToken, Is.SameAs(original));
            Assert.That(bytesField.GetValue(bytesToken), Is.SameAs(empty));
        }

        [Test]
        public void CanonicalBytesToken_Acquire_DoesNotExposeBytes()
        {
            // A token can only be minted from a plan and keeps its byte array
            // private: Acquire takes no byte[] input or output and the token
            // has no public constructor or byte-returning property.
            MethodInfo acquire = typeof(CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken)
                .GetMethod("Acquire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(acquire, Is.Not.Null);

            ParameterInfo[] parameters = acquire.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(PngJsonCapturePublicationPlan)));

            Assert.That(
                typeof(CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken).GetConstructors(BindingFlags.Public | BindingFlags.Instance),
                Is.Empty);

            PropertyInfo[] properties = typeof(CaptureRunCaptureIndexCommitOperation.CanonicalBytesToken)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(properties, Is.Empty);
        }

        [Test]
        public void GetCanonicalBytes_DefensiveCopy_NoExternalAlias()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);

            byte[] first = commit.GetCanonicalBytes();
            byte[] second = commit.GetCanonicalBytes();

            Assert.That(first, Is.Not.Null);
            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(first, Is.EqualTo(second));

            first[0] = (byte)(first[0] ^ 0xFF);

            Assert.That(commit.GetCanonicalBytes(), Is.EqualTo(second));
            Assert.That(commit.IsValid, Is.True);
        }

        // ---- Forge defense ----

        [Test]
        public void Operation_ForgedFields_IsValidFalse_NoException()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out CaptureRunPublicationArtifactEntryObservation observation);
            CaptureRunCaptureIndexCommitOperation commit = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);
            CaptureRunPublicationPathSet paths = GetPublicationPaths(plan);

            Assert.That(commit.IsValid, Is.True);

            // Null action plan.
            Assert.That(ForgeOperation(null, 0, paths, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);

            // Step index out of range.
            Assert.That(ForgeOperation(plan, 99, paths, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);

            // Null publication path set.
            Assert.That(ForgeOperation(plan, 0, null, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);

            // Foreign publication path set.
            CaptureRunPublicationPathSet foreign = MakeOperation().Decision.Snapshot.Operation.PublicationPaths;
            Assert.That(ForgeOperation(plan, 0, foreign, commit.Mode, commit.GetCanonicalBytes()).IsValid, Is.False);

            // Forged mode mismatch.
            Assert.That(ForgeOperation(plan, 0, paths, CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, commit.GetCanonicalBytes()).IsValid, Is.False);

            // Null, empty, and wrong canonical bytes.
            Assert.That(ForgeOperation(plan, 0, paths, commit.Mode, null).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 0, paths, commit.Mode, new byte[0]).IsValid, Is.False);
            Assert.That(ForgeOperation(plan, 0, paths, commit.Mode, new byte[] { 1, 2, 3 }).IsValid, Is.False);

            // Forged observation invalidates the whole plan and the operation.
            SetField(observation, "_finalPngStatus", EvAbsent);
            SetField(observation, "_finalPngProbedByteCount", 0);
            Assert.That(commit.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Operation_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunCaptureIndexCommitOperation);

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
        public void Operation_FieldShape_FiveReadonlyFields_NoStaticState()
        {
            FieldInfo[] fields = typeof(CaptureRunCaptureIndexCommitOperation).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(5));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(byte[])));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationArtifactRecoveryActionPlan)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunPublicationPathSet)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunCaptureIndexCommitMode)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            FieldInfo[] staticFields = typeof(CaptureRunCaptureIndexCommitOperation).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "Operation must not hold static mutable state.");
        }

        [Test]
        public void Factory_IsStaticWithNoState()
        {
            Type type = typeof(CaptureRunCaptureIndexCommitOperationFactory);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Shape_NoLeaseExposure()
        {
            foreach (Type type in new[] { typeof(CaptureRunCaptureIndexCommitOperation), typeof(CaptureRunCaptureIndexCommitOperationFactory) })
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

        // ---- Source ----

        [Test]
        public void Factory_Create_SingleFullValidation()
        {
            string factorySource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunCaptureIndexCommitOperationFactory.cs"));
            string operationSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunCaptureIndexCommitOperation.cs"));

            Assert.That(factorySource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(operationSource, Does.Not.Contain("!actionPlan.IsValid"));
            Assert.That(factorySource, Does.Contain("AcquireValidationToken"));

            int indexLocal = factorySource.IndexOf("CreateIndexLocal", StringComparison.Ordinal);
            Assert.That(indexLocal, Is.GreaterThan(0));
            Assert.That(factorySource.Substring(indexLocal), Does.Not.Contain("AcquireValidationToken"));
            Assert.That(factorySource.Substring(indexLocal), Does.Not.Contain("actionPlan.IsValid"));
        }

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string operationSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunCaptureIndexCommitOperation.cs"));
            string factorySource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunCaptureIndexCommitOperationFactory.cs"));
            string modeSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunCaptureIndexCommitMode.cs"));

            foreach (string source in new[] { operationSource, factorySource, modeSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
                Assert.That(source, Does.Not.Contain("HashSet"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("Guid"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("TraceRunManifest"));
                Assert.That(source, Does.Not.Contain("TraceLogger"));
            }

            // The factory must not serialize or copy bytes; it mints the bytes
            // token, whose Acquire performs the single canonical serialization.
            Assert.That(factorySource, Does.Not.Contain("Array.Copy"));
            Assert.That(factorySource, Does.Not.Contain("SerializeCanonical"));
            Assert.That(factorySource, Does.Contain("Acquire"));

            // The operation serializes once inside Acquire and re-serializes in
            // IsValid, and defensively copies in the getter.
            Assert.That(operationSource, Does.Contain("SerializeCanonical"));
            Assert.That(operationSource, Does.Contain("GetCanonicalBytes"));
        }
    }
}
