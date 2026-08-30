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
    public class CaptureRunCaptureIndexCommitterReceiptTests
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

        private static CapturePublicationPlanEntry MakeEntry(
            long captureFrameId,
            long pngByteLength = PngBytes,
            long sidecarByteLength = SidecarBytes,
            string pngHash = null,
            string sidecarHash = null)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new CapturePublicationPlanEntry(
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

        private static CapturePublicationPlan MakePlan(
            long testRunId = 1,
            string initId = null,
            string manifestHash = null,
            CapturePublicationPlanEntry[] entries = null)
        {
            return new CapturePublicationPlan(
                testRunId,
                initId ?? InitId,
                manifestHash ?? StagingHash,
                entries ?? new[] { MakeEntry(10) });
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            CapturePublicationPlan plan = null)
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
                captureIndexTemporary ?? MakeDoc(CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private static CaptureRunPublicationArtifactInspectionOperation MakeOperation(
            List<string> disposeLog = null,
            CapturePublicationPlan plan = null,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null,
            int maximumEntryCount = 4)
        {
            CaptureRunInitializationOpenOutcome outcome = MakePublicationRecoveryOutcome(disposeLog);
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

        private static CaptureRunPublicationArtifactRecoveryActionPlan BuildCommitPlan(
            out CaptureRunPublicationArtifactInspectionOperation operation,
            out CaptureRunPublicationArtifactEntryObservation observation,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CapturePublicationPlan plan = null)
        {
            operation = MakeOperation(captureIndexTemporary: captureIndexTemporary, plan: plan);
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

        private static CaptureRunPublicationPathSet GetPublicationPaths(CaptureRunPublicationArtifactRecoveryActionPlan plan)
        {
            return plan.Decision.PublicationDecision.Snapshot.Operation.PublicationPaths;
        }

        private static CaptureRunCaptureIndexCommitOperation MakeCommitOperation(
            out CaptureRunPublicationArtifactRecoveryActionPlan plan,
            out CaptureRunPublicationArtifactInspectionOperation inspectionOperation)
        {
            plan = BuildCommitPlan(out inspectionOperation, out _);
            return CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);
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

        private static CaptureRunCaptureIndexCommitReceipt ForgeReceipt(
            ICaptureRunCaptureIndexCommitter issuedBy,
            CaptureRunCaptureIndexCommitOperation operation)
        {
            CaptureRunCaptureIndexCommitReceipt receipt = (CaptureRunCaptureIndexCommitReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunCaptureIndexCommitReceipt));
            SetField(receipt, "_issuedBy", issuedBy);
            SetField(receipt, "_operation", operation);
            return receipt;
        }

        private static bool ValidateReceipt(
            ICaptureRunCaptureIndexCommitter committer,
            CaptureRunCaptureIndexCommitOperation operation,
            CaptureRunCaptureIndexCommitReceipt receipt)
        {
            return receipt != null && receipt.IsIssuedFor(committer, operation);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunCaptureIndexCommitterReceiptTests).Assembly.Location);
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

        private sealed class FakeCommitter : ICaptureRunCaptureIndexCommitter
        {
            public CaptureRunCaptureIndexCommitReceipt Commit(CaptureRunCaptureIndexCommitOperation operation)
            {
                return new CaptureRunCaptureIndexCommitReceipt(this, operation);
            }
        }

        private sealed class CountingCommitter : ICaptureRunCaptureIndexCommitter
        {
            public int Calls;

            public InvalidOperationException Exception;

            public CaptureRunCaptureIndexCommitReceipt Result;

            public CaptureRunCaptureIndexCommitOperation LastOperation;

            public CaptureRunCaptureIndexCommitReceipt Commit(CaptureRunCaptureIndexCommitOperation operation)
            {
                Calls++;
                LastOperation = operation;
                if (Exception != null)
                {
                    throw Exception;
                }

                return Result;
            }
        }

        private sealed class ConfigurableCommitter : ICaptureRunCaptureIndexCommitter
        {
            public CaptureRunCaptureIndexCommitReceipt Result;

            public CaptureRunCaptureIndexCommitReceipt Commit(CaptureRunCaptureIndexCommitOperation operation)
            {
                return Result;
            }
        }

        // ---- Interface ----

        [Test]
        public void Interface_InternalSingleMethod_SignatureExact()
        {
            Type type = typeof(ICaptureRunCaptureIndexCommitter);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsInterface, Is.True);

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(methods.Length, Is.EqualTo(1));
            Assert.That(methods[0].Name, Is.EqualTo("Commit"));
            Assert.That(methods[0].ReturnType, Is.EqualTo(typeof(CaptureRunCaptureIndexCommitReceipt)));

            ParameterInfo[] parameters = methods[0].GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunCaptureIndexCommitOperation)));
        }

        // ---- Rejection ----

        [Test]
        public void Receipt_NullIssuer_Rejected()
        {
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunCaptureIndexCommitReceipt(null, operation));
            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void Receipt_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_InvalidOperation_Rejected()
        {
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);

            SetField(operation, "_canonicalBytes", new byte[] { 1, 2, 3 });

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_ForgedOperation_Rejected()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out _, out _);
            CaptureRunCaptureIndexCommitOperation forged = ForgeOperation(
                plan, 0, GetPublicationPaths(plan), CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), forged));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        // ---- Correlation / forwarding ----

        [Test]
        public void Receipt_IssuedByAndOperationReferenceEquals()
        {
            FakeCommitter committer = new FakeCommitter();
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);

            CaptureRunCaptureIndexCommitReceipt receipt = new CaptureRunCaptureIndexCommitReceipt(committer, operation);

            Assert.That(receipt.IssuedBy, Is.SameAs(committer));
            Assert.That(receipt.Operation, Is.SameAs(operation));
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_ForwardsAllValues()
        {
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out CaptureRunPublicationArtifactRecoveryActionPlan plan, out _);

            CaptureRunCaptureIndexCommitReceipt receipt = new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), operation);

            Assert.That(receipt.Mode, Is.EqualTo(operation.Mode));
            Assert.That(receipt.TemporaryPath, Is.EqualTo(operation.TemporaryPath));
            Assert.That(receipt.FinalPath, Is.EqualTo(operation.FinalPath));
            Assert.That(receipt.ByteCount, Is.EqualTo(operation.ByteCount));
            Assert.That(receipt.ActionPlan, Is.SameAs(operation.ActionPlan));
            Assert.That(receipt.RootLayout, Is.SameAs(operation.RootLayout));
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(receipt.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
            Assert.That(receipt.ActionPlan, Is.SameAs(plan));
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_IsValid_NormalTrue()
        {
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);
            CaptureRunCaptureIndexCommitReceipt receipt = new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), operation);

            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_IsValid_UninitializedFalse()
        {
            CaptureRunCaptureIndexCommitReceipt receipt = (CaptureRunCaptureIndexCommitReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunCaptureIndexCommitReceipt));

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Receipt_IsIssuedFor_MatchForeignAndDifferentOperation()
        {
            FakeCommitter committer = new FakeCommitter();
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);
            CaptureRunCaptureIndexCommitOperation other = MakeCommitOperation(out _, out _);

            CaptureRunCaptureIndexCommitReceipt receipt = new CaptureRunCaptureIndexCommitReceipt(committer, operation);

            Assert.That(receipt.IsIssuedFor(committer, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(new FakeCommitter(), operation), Is.False);
            Assert.That(receipt.IsIssuedFor(committer, other), Is.False);
            Assert.That(receipt.IsIssuedFor(null, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(committer, null), Is.False);
        }

        // ---- Fake committer ----

        [Test]
        public void FakeCommitter_ReturnsReceiptForSameOperation()
        {
            FakeCommitter committer = new FakeCommitter();
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);

            CaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation);

            Assert.That(receipt.IssuedBy, Is.SameAs(committer));
            Assert.That(receipt.Operation, Is.SameAs(operation));
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void FakeCommitter_ExceptionNotTransformedOrRetried()
        {
            CountingCommitter committer = new CountingCommitter();
            committer.Exception = new InvalidOperationException("boom");
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => committer.Commit(operation));

            Assert.That(ex, Is.SameAs(committer.Exception));
            Assert.That(committer.Calls, Is.EqualTo(1));
            Assert.That(committer.LastOperation, Is.SameAs(operation));
        }

        [Test]
        public void Coordinator_RejectsNullForeignAndDifferentOperationReceipts()
        {
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);
            CaptureRunCaptureIndexCommitOperation other = MakeCommitOperation(out _, out _);

            // A null receipt is rejected.
            ConfigurableCommitter nullCommitter = new ConfigurableCommitter();
            nullCommitter.Result = null;
            Assert.That(ValidateReceipt(nullCommitter, operation, nullCommitter.Commit(operation)), Is.False);

            FakeCommitter real = new FakeCommitter();

            // A receipt issued by a foreign committer is rejected.
            CaptureRunCaptureIndexCommitReceipt foreign = new CaptureRunCaptureIndexCommitReceipt(real, operation);
            Assert.That(ValidateReceipt(new FakeCommitter(), operation, foreign), Is.False);

            // A receipt for a different operation is rejected.
            CaptureRunCaptureIndexCommitReceipt wrongOperation = new CaptureRunCaptureIndexCommitReceipt(real, other);
            Assert.That(ValidateReceipt(real, operation, wrongOperation), Is.False);

            // A matching receipt is accepted.
            CaptureRunCaptureIndexCommitReceipt correct = new CaptureRunCaptureIndexCommitReceipt(real, operation);
            Assert.That(ValidateReceipt(real, operation, correct), Is.True);
        }

        [Test]
        public void Receipt_DoesNotMutateOperationOrBytes()
        {
            CaptureRunCaptureIndexCommitOperation operation = MakeCommitOperation(out _, out _);
            byte[] before = operation.GetCanonicalBytes();

            CaptureRunCaptureIndexCommitReceipt receipt = new CaptureRunCaptureIndexCommitReceipt(new FakeCommitter(), operation);
            byte[] afterConstruct = operation.GetCanonicalBytes();

            Assert.That(afterConstruct, Is.EqualTo(before));
            Assert.That(operation.IsValid, Is.True);

            FakeCommitter committer = new FakeCommitter();
            committer.Commit(operation);
            byte[] afterCommit = operation.GetCanonicalBytes();

            Assert.That(afterCommit, Is.EqualTo(before));
            Assert.That(receipt.IsValid, Is.True);
        }

        // ---- Forge defense ----

        [Test]
        public void Receipt_ForgedFields_IsValidFalse_NoException()
        {
            CaptureRunPublicationArtifactRecoveryActionPlan plan = BuildCommitPlan(out CaptureRunPublicationArtifactInspectionOperation inspectionOperation, out _);
            CaptureRunCaptureIndexCommitOperation operation = CaptureRunCaptureIndexCommitOperationFactory.Create(plan, 0);
            FakeCommitter committer = new FakeCommitter();

            CaptureRunCaptureIndexCommitReceipt receipt = new CaptureRunCaptureIndexCommitReceipt(committer, operation);
            Assert.That(receipt.IsValid, Is.True);

            // Null issuer.
            Assert.That(ForgeReceipt(null, operation).IsValid, Is.False);

            // Null operation.
            Assert.That(ForgeReceipt(committer, null).IsValid, Is.False);

            // Forged canonical bytes.
            CaptureRunCaptureIndexCommitOperation forgedBytes = ForgeOperation(
                plan, 0, GetPublicationPaths(plan), operation.Mode, new byte[] { 1, 2, 3 });
            Assert.That(ForgeReceipt(committer, forgedBytes).IsValid, Is.False);

            // Forged mode.
            CaptureRunCaptureIndexCommitOperation forgedMode = ForgeOperation(
                plan, 0, GetPublicationPaths(plan), CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, operation.GetCanonicalBytes());
            Assert.That(ForgeReceipt(committer, forgedMode).IsValid, Is.False);

            // Forged publication path set.
            CaptureRunPublicationPathSet foreign = MakeOperation().Decision.Snapshot.Operation.PublicationPaths;
            CaptureRunCaptureIndexCommitOperation forgedPaths = ForgeOperation(
                plan, 0, foreign, operation.Mode, operation.GetCanonicalBytes());
            Assert.That(ForgeReceipt(committer, forgedPaths).IsValid, Is.False);

            // Released lease invalidates the whole operation and receipt.
            inspectionOperation.LockLease.Dispose();
            Assert.That(receipt.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Receipt_SealedNotDisposableNotUnityObject_NoPublicCtor()
        {
            Type type = typeof(CaptureRunCaptureIndexCommitReceipt);

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
        public void Receipt_FieldShape_TwoReadonlyRefs_NoStaticState()
        {
            FieldInfo[] fields = typeof(CaptureRunCaptureIndexCommitReceipt).GetFields(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(ICaptureRunCaptureIndexCommitter)));
            Assert.That(fields, Has.Exactly(1).Matches<FieldInfo>(f => f.FieldType == typeof(CaptureRunCaptureIndexCommitOperation)));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsArray, Is.False);
                Assert.That(typeof(Stream).IsAssignableFrom(field.FieldType), Is.False);
            }

            FieldInfo[] staticFields = typeof(CaptureRunCaptureIndexCommitReceipt).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "Receipt must not hold static mutable state.");
        }

        // ---- Source / contract ----

        [Test]
        public void Source_NoForbiddenDependenciesAndContract()
        {
            string interfaceSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunCaptureIndexCommitter.cs"));
            string receiptSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunCaptureIndexCommitReceipt.cs"));

            foreach (string source in new[] { interfaceSource, receiptSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("Serialize"));
                Assert.That(source, Does.Not.Contain("Deserialize"));
                Assert.That(source, Does.Not.Contain("ComputeHash"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Trace"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("Guid"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("List<"));
                Assert.That(source, Does.Not.Contain("Dictionary"));
                Assert.That(source, Does.Not.Contain("HashSet"));
            }

            Assert.That(interfaceSource, Does.Contain("overwrite"));
            Assert.That(interfaceSource, Does.Contain("durably"));
            Assert.That(interfaceSource, Does.Contain("re-inspection"));
            Assert.That(interfaceSource, Does.Contain("volumes"));
            Assert.That(interfaceSource, Does.Contain("must not return a receipt"));
            Assert.That(interfaceSource, Does.Contain("atomic rename"));
            Assert.That(interfaceSource, Does.Contain("no-follow"));
            Assert.That(interfaceSource, Does.Contain("CreateTemporaryAndCommit"));
            Assert.That(interfaceSource, Does.Contain("ReuseCanonicalTemporaryAndCommit"));
            Assert.That(interfaceSource, Does.Contain("ReplaceInvalidTemporaryAndCommit"));

            // The returned receipt must be correlated to this committer and
            // the supplied operation, and rejectable otherwise.
            Assert.That(interfaceSource, Does.Contain("never null"));
            Assert.That(interfaceSource, Does.Contain("ReferenceEquals(receipt.IssuedBy, this)"));
            Assert.That(interfaceSource, Does.Contain("ReferenceEquals(receipt.Operation, operation)"));
            Assert.That(interfaceSource, Does.Contain("receipt.IsIssuedFor(this, operation)"));
            Assert.That(interfaceSource, Does.Contain("fail closed"));
        }
    }
}
