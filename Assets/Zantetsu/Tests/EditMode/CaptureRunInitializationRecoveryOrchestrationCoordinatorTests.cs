using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationRecoveryOrchestrationCoordinatorTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerKind InitKind => CaptureRunMarkerKind.Initialization;

        private static CaptureRunMarkerKind ReadyKind => CaptureRunMarkerKind.Ready;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

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
                role,
                rootExists,
                hasInitTmp,
                initStatus,
                initMarker,
                hasReadyTmp,
                readyStatus,
                readyMarker,
                hasNonMarker,
                hasUnknown,
                false);
        }

        private static CaptureRunInitializationRootObservation MakeAbsent(CaptureRunRootRole role)
        {
            return MakeObservation(role, false, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeCanonicalInit(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker marker)
        {
            return MakeObservation(role, true, Canonical, marker, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(
            CaptureRunRootRole role,
            CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private CaptureRunInitializationSessionOwnershipLease MakeOwner(
            CaptureRunRootLayout layout,
            List<string> disposeLog)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            return owner;
        }

        private CaptureRunInitializationRecoveryInspectionOperation MakeInspectionOperation(
            CaptureRunRootLayout layout = null,
            List<string> disposeLog = null)
        {
            return MakeInspectionOperation(layout, disposeLog, out _);
        }

        private CaptureRunInitializationRecoveryInspectionOperation MakeInspectionOperation(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeInspectionOperation(layout, null, out owner);
        }

        private CaptureRunInitializationRecoveryInspectionOperation MakeInspectionOperation(
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            layout = layout ?? MakeLayout();
            owner = MakeOwner(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            return new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
        }

        private static CaptureRunInitializationRecoveryExecutionCoordinator MakeExecutionCoordinator(List<string> log = null)
        {
            return new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));
        }

        private static CaptureRunInitializationRecoveryOrchestrationCoordinator MakeOrchestrator(
            ICaptureRunInitializationRecoveryInspector inspector,
            CaptureRunInitializationRecoveryExecutionCoordinator executionCoordinator)
        {
            return new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, executionCoordinator);
        }

        private static FakeInspector MakeInspector(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            List<string> log = null)
        {
            FakeInspector inspector = null;
            inspector = new FakeInspector(
                operation => new CaptureRunInitializationRecoveryInspectionSnapshot(inspector, operation, staging, final),
                log);
            return inspector;
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryOrchestrationCoordinatorTests).Assembly.Location);
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
            private readonly Func<CaptureRunInitializationRecoveryInspectionOperation, CaptureRunInitializationRecoveryInspectionSnapshot> _factory;
            private readonly List<string> _log;

            public FakeInspector(
                Func<CaptureRunInitializationRecoveryInspectionOperation, CaptureRunInitializationRecoveryInspectionSnapshot> factory,
                List<string> log = null)
            {
                _factory = factory;
                _log = log;
            }

            public Exception ExceptionToThrow { get; set; }

            public int InspectCount { get; private set; }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                InspectCount++;
                _log?.Add("inspect");
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return _factory(operation);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            private readonly List<string> _log;

            public FakeCleanupBackend(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                _log?.Add("cleanup:" + operation.RootRole + ":" + operation.MarkerKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            private readonly List<string> _log;

            public FakeProvisioner(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                _log?.Add("provision:" + operation.RootRole);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;

            public FakeWriter(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                _log?.Add("write:" + operation.RootRole + ":" + operation.MarkerKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        // ---- Constructor contracts ----

        [Test]
        public void Coordinator_Constructor_NullDependencies_Rejected()
        {
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryOrchestrationCoordinator(null, execCoord));
            Assert.That(ex1.ParamName, Is.EqualTo("inspector"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryOrchestrationCoordinator(MakeInspector(MakeAbsent(Staging), MakeAbsent(Final)), null));
            Assert.That(ex2.ParamName, Is.EqualTo("executionCoordinator"));
        }

        [Test]
        public void Result_Constructor_NullArguments_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)),
                execCoord);
            CaptureRunInitializationRecoveryExecutionResult executionResult = orchestrator.Execute(MakeInspectionOperation(layout)).ExecutionResult;

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryOrchestrationResult(null, executionResult));
            Assert.That(ex1.ParamName, Is.EqualTo("issuedBy"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryOrchestrationResult(orchestrator, null));
            Assert.That(ex2.ParamName, Is.EqualTo("executionResult"));
        }

        // ---- Operation rejection ----

        [Test]
        public void Execute_NullOperation_Rejected_InspectorNotContacted()
        {
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeAbsent(Staging), MakeAbsent(Final), log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => orchestrator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(inspector.InspectCount, Is.EqualTo(0));
            Assert.That(log, Is.Empty);
        }

        [Test]
        public void Execute_InvalidOperation_Rejected_InspectorNotContacted()
        {
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeAbsent(Staging), MakeAbsent(Final), log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunInitializationRecoveryInspectionOperation invalid =
                (CaptureRunInitializationRecoveryInspectionOperation)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryInspectionOperation));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => orchestrator.Execute(invalid));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(inspector.InspectCount, Is.EqualTo(0));
            Assert.That(log, Is.Empty);
        }

        // ---- Snapshot verification ----

        [Test]
        public void Execute_InspectorNullSnapshot_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            FakeInspector inspector = new FakeInspector(_ => null, log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            Assert.Throws<InvalidOperationException>(() => orchestrator.Execute(MakeInspectionOperation(layout)));
        }

        [Test]
        public void Execute_ForeignIssuerSnapshot_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            FakeInspector foreign = MakeInspector(MakeAbsent(Staging), MakeAbsent(Final));
            FakeInspector inspector = new FakeInspector(
                op => new CaptureRunInitializationRecoveryInspectionSnapshot(foreign, op, MakeAbsent(Staging), MakeAbsent(Final)),
                log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            Assert.Throws<InvalidOperationException>(() => orchestrator.Execute(MakeInspectionOperation(layout)));
        }

        [Test]
        public void Execute_ForeignOperationSnapshot_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryInspectionOperation otherOperation = MakeInspectionOperation(MakeLayout(2));
            List<string> log = new List<string>();
            FakeInspector inspector = null;
            inspector = new FakeInspector(
                op => new CaptureRunInitializationRecoveryInspectionSnapshot(inspector, otherOperation, MakeAbsent(Staging), MakeAbsent(Final)),
                log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            Assert.Throws<InvalidOperationException>(() => orchestrator.Execute(MakeInspectionOperation(layout)));
        }

        [Test]
        public void Execute_InspectorException_PropagatesIdentical_NoRetry()
        {
            CaptureRunRootLayout layout = MakeLayout();
            IOException exception = new IOException("inspect boom");
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeAbsent(Staging), MakeAbsent(Final), log);
            inspector.ExceptionToThrow = exception;
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            IOException ex = Assert.Throws<IOException>(() => orchestrator.Execute(MakeInspectionOperation(layout)));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(inspector.InspectCount, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }), "No retry and no backend contact after an inspector exception.");
        }

        // ---- End-to-end dispositions ----

        [Test]
        public void Execute_AllDispositions_StatusMapping()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            AssertStatus(
                MakeAbsent(Staging), MakeAbsent(Final), layout,
                CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired);
            AssertStatus(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), layout,
                CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired);
            AssertStatus(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout,
                CaptureRunInitializationRecoveryExecutionStatus.InitializationReady);
            AssertStatus(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeCanonicalInit(Final, binding.FinalInitialization), layout,
                CaptureRunInitializationRecoveryExecutionStatus.InitializationReady);
            AssertStatus(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout,
                CaptureRunInitializationRecoveryExecutionStatus.InitializationReady);
            AssertStatus(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding), layout,
                CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired);
            AssertStatus(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), layout,
                CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision);
        }

        [Test]
        public void Execute_CompleteMissingPeer_InspectorFirst_EachStepOnce()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(MakeInspectionOperation(layout));

            Assert.That(log, Is.EqualTo(new[]
            {
                "inspect",
                "provision:Final",
                "write:Final:Initialization",
                "write:Staging:Ready",
                "write:Final:Ready"
            }));
            Assert.That(inspector.InspectCount, Is.EqualTo(1));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_ExecutionFailure_NoSubsequent_NoOwnerDispose()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> disposeLog = new List<string>();
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), log);

            FakeWriter writer = new FakeWriter(log) { ExceptionToThrow = new IOException("write failed") };
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), writer);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, execCoord);

            CaptureRunInitializationSessionOwnershipLease owner = null;
            Assert.Throws<IOException>(() => orchestrator.Execute(MakeInspectionOperation(layout, disposeLog, out owner)));

            Assert.That(log, Is.EqualTo(new[]
            {
                "inspect",
                "provision:Final",
                "write:Final:Initialization"
            }));
            Assert.That(disposeLog, Is.Empty, "The orchestrator must never dispose the owner on failure.");
            Assert.That(owner.IsCreated, Is.True);
        }

        [Test]
        public void Execute_RunRootCollision_NoBackendContact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(MakeInspectionOperation(layout));

            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }), "Collision must not contact any mutation backend.");
        }

        [Test]
        public void Execute_AlreadyInitialized_NoBackendContact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(MakeInspectionOperation(layout));

            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.InitializationReady));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }), "AlreadyInitialized must not contact any mutation backend.");
        }

        [Test]
        public void Execute_StartFresh_StatusOnly_NoBackendContact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(MakeAbsent(Staging), MakeAbsent(Final), log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(MakeInspectionOperation(layout));

            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired));
            Assert.That(log, Is.EqualTo(new[] { "inspect" }), "StartFresh must not contact any mutation backend.");
        }

        // ---- Result forwarding ----

        [Test]
        public void Result_Forwarding_ReferenceIdentity()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionOperation operation = MakeInspectionOperation(layout);
            FakeInspector inspector = MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final));
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, execCoord);

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(operation);
            CaptureRunInitializationRecoveryExecutionResult executionResult = result.ExecutionResult;

            Assert.That(result.IssuedBy, Is.SameAs(orchestrator));
            Assert.That(result.Batch, Is.SameAs(executionResult.Batch));
            Assert.That(result.ActionPlan, Is.SameAs(executionResult.Batch.ActionPlan));
            Assert.That(result.Decision, Is.SameAs(executionResult.Batch.ActionPlan.Decision));
            Assert.That(result.Snapshot, Is.SameAs(executionResult.Batch.ActionPlan.Decision.Snapshot));
            Assert.That(result.Status, Is.EqualTo(executionResult.Status));
            Assert.That(result.RootLayout, Is.SameAs(executionResult.RootLayout));
            Assert.That(result.LockIdentityEvidence, Is.SameAs(executionResult.LockIdentityEvidence));
            Assert.That(result.TestRunId, Is.EqualTo(executionResult.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(executionResult.RunInitializationId));
            Assert.That(result.Snapshot.Operation, Is.SameAs(operation));
            Assert.That(result.Snapshot.Operation.LockIdentityEvidence, Is.SameAs(result.LockIdentityEvidence));
            Assert.That(result.LockIdentityEvidence.LockPathSet, Is.SameAs(result.Snapshot.Operation.LockIdentityEvidence.LockPathSet));
            Assert.That(result.Snapshot.Operation.RootLayout, Is.SameAs(result.RootLayout));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_ReleasedOwner_IsInvalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionOperation operation = MakeInspectionOperation(layout, out CaptureRunInitializationSessionOwnershipLease owner);
            FakeInspector inspector = MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final));
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator());

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(operation);

            Assert.That(result.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();

            Assert.That(result.IsValid, Is.False);
        }

        // ---- Result direct-constructor defense ----

        [Test]
        public void Result_DirectConstructor_ForeignInspector_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();

            CaptureRunInitializationRecoveryOrchestrationCoordinator coordinatorA = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), execCoord);
            CaptureRunInitializationRecoveryOrchestrationCoordinator coordinatorB = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), execCoord);

            CaptureRunInitializationRecoveryExecutionResult resultB = coordinatorB.Execute(MakeInspectionOperation(layout)).ExecutionResult;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryOrchestrationResult(coordinatorA, resultB));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignExecutionCoordinator_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryOrchestrationCoordinator coordinatorA = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), MakeExecutionCoordinator());
            CaptureRunInitializationRecoveryOrchestrationCoordinator coordinatorB = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), MakeExecutionCoordinator());

            CaptureRunInitializationRecoveryExecutionResult resultB = coordinatorB.Execute(MakeInspectionOperation(layout)).ExecutionResult;

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryOrchestrationResult(coordinatorA, resultB));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignBatch_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), execCoord);

            CaptureRunInitializationRecoveryExecutionResult good = orchestrator.Execute(MakeInspectionOperation(layout)).ExecutionResult;

            // Build a different (valid) batch from a different operation.
            CaptureRunInitializationRecoveryOrchestrationCoordinator otherOrchestrator = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeCanonicalInit(Final, binding.FinalInitialization)), execCoord);
            CaptureRunInitializationRecoveryExecutionBatch otherBatch = otherOrchestrator.Execute(MakeInspectionOperation(layout)).Batch;

            CaptureRunInitializationRecoveryExecutionResult forged =
                (CaptureRunInitializationRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryExecutionResult));
            SetField(forged, "_issuedBy", good.IssuedBy);
            SetField(forged, "_batch", otherBatch);
            CaptureRunInitializationRecoveryCompletedStep[] steps = new CaptureRunInitializationRecoveryCompletedStep[good.Count];
            for (int i = 0; i < good.Count; i++)
            {
                steps[i] = good.GetCompletedStep(i);
            }

            SetField(forged, "_completedSteps", steps);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryOrchestrationResult(orchestrator, forged));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignOperation_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), execCoord);

            CaptureRunInitializationRecoveryExecutionResult good = orchestrator.Execute(MakeInspectionOperation(layout)).ExecutionResult;
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = good.Batch.ActionPlan.Decision.Snapshot;
            CaptureRunInitializationRecoveryInspectionOperation otherOperation = MakeInspectionOperation(MakeLayout(2));
            SetField(snapshot, "_operation", otherOperation);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryOrchestrationResult(orchestrator, good));
            Assert.That(ex.ParamName, Is.EqualTo("executionResult"));
        }

        // ---- Forged IsValid ----

        [Test]
        public void ForgedValues_IsValidFalse_WithoutException()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator execCoord = MakeExecutionCoordinator();
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(
                MakeInspector(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final)), execCoord);
            CaptureRunInitializationRecoveryExecutionResult good = orchestrator.Execute(MakeInspectionOperation(layout)).ExecutionResult;

            // null execution result
            CaptureRunInitializationRecoveryOrchestrationResult nullExec =
                (CaptureRunInitializationRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryOrchestrationResult));
            SetField(nullExec, "_issuedBy", orchestrator);
            SetField(nullExec, "_executionResult", null);
            Assert.That(nullExec.IsValid, Is.False);

            // null issuer
            CaptureRunInitializationRecoveryOrchestrationResult nullIssuer =
                (CaptureRunInitializationRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryOrchestrationResult));
            SetField(nullIssuer, "_issuedBy", null);
            SetField(nullIssuer, "_executionResult", good);
            Assert.That(nullIssuer.IsValid, Is.False);

            // forged foreign inspector on the nested snapshot
            CaptureRunInitializationRecoveryOrchestrationResult valid = new CaptureRunInitializationRecoveryOrchestrationResult(orchestrator, good);
            SetField(valid.Snapshot, "_issuedBy", MakeInspector(MakeAbsent(Staging), MakeAbsent(Final)));
            Assert.That(valid.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void OrchestrationCoordinator_Shape()
        {
            Type type = typeof(CaptureRunInitializationRecoveryOrchestrationCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            Assert.That(instanceFields.All(f => f.IsInitOnly), Is.True);

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "The coordinator must not hold mutable static state.");
        }

        [Test]
        public void OrchestrationResult_Shape()
        {
            Type type = typeof(CaptureRunInitializationRecoveryOrchestrationResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            Assert.That(instanceFields.All(f => f.IsInitOnly), Is.True);

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty, "The result must not hold mutable static state.");

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(
                properties.Any(p => p.PropertyType != typeof(string) && (p.PropertyType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))),
                Is.False,
                "The result must not expose arrays or mutable collections.");

            Assert.That(
                properties.Any(p => p.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                                   || p.PropertyType == typeof(CaptureRunLockLease)),
                Is.False,
                "The result must not expose the ownership lease or raw lock lease.");
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryOrchestrationCoordinator.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryOrchestrationResult.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Trace"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("System.IO"));
                Assert.That(source, Does.Not.Contain("Bootstrap"));
                Assert.That(source, Does.Not.Contain("Session"));
                Assert.That(source, Does.Not.Contain("Generator"));
                Assert.That(source, Does.Not.Contain("Acquire"));
            }
        }

        // ---- Assertion helpers ----

        private void AssertStatus(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout,
            CaptureRunInitializationRecoveryExecutionStatus expectedStatus)
        {
            List<string> log = new List<string>();
            FakeInspector inspector = MakeInspector(staging, final, log);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = MakeOrchestrator(inspector, MakeExecutionCoordinator(log));

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(MakeInspectionOperation(layout));

            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(result.IsValid, Is.True);
        }
    }
}
