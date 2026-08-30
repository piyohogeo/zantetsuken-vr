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
    public class CaptureRunInitializationRecoverySessionRoutingCoordinatorTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string OtherInitId = "fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

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

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog, out FakeHandle first, out FakeHandle second)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog) { Tag = "first" };
            second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog) { Tag = "second" };
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog)
        {
            return MakeLease(layout, disposeLog, out _, out _);
        }

        private static CaptureRunInitializationRecoveryOrchestrationResult MakeRecoveryResult(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunLockLease lease)
        {
            CaptureRunRootLayout layout = lease.PathSet.RootLayout;
            FakeInspector inspector = MakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator executionCoordinator = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeRecoveryCleanup(), new FakeProvisioner(null), new FakeWriter(null));
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                inspector, executionCoordinator);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, lease, 4);
            return orchestrator.Execute(operation);
        }

        private static FakeInspector MakeInspector(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final)
        {
            FakeInspector inspector = null;
            inspector = new FakeInspector(
                operation => new CaptureRunInitializationRecoveryInspectionSnapshot(inspector, operation, staging, final));
            return inspector;
        }

        private static CaptureRunInitializationRecoverySessionRoutingCoordinator MakeRoutingCoordinator(
            FakeIdSource idSource, FakeProvisioner provisioner, FakeWriter writer)
        {
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(provisioner, writer);
            CaptureRunInitializationRecoveryStartFreshCoordinator startFresh = new CaptureRunInitializationRecoveryStartFreshCoordinator(idSource, executionCoordinator);
            return new CaptureRunInitializationRecoverySessionRoutingCoordinator(startFresh);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoverySessionRoutingCoordinatorTests).Assembly.Location);
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

            public string Tag { get; set; }

            public void Dispose()
            {
                _disposeLog?.Add(LockPath);
            }
        }

        private sealed class FakeIdSource : ICaptureRunInitializationIdSource
        {
            private readonly List<string> _log;

            public FakeIdSource(List<string> log)
            {
                _log = log;
            }

            public string NextId = OtherInitId;

            public Exception Throw { get; set; }

            public int CallCount { get; private set; }

            public string Create()
            {
                CallCount++;
                _log?.Add("Id:Create");
                if (Throw != null)
                {
                    throw Throw;
                }

                return NextId;
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            private readonly List<string> _log;

            public FakeProvisioner(List<string> log)
            {
                _log = log;
            }

            public int CallCount { get; private set; }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                CallCount++;
                _log?.Add("Provision:" + operation.RootRole);
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;

            public FakeWriter(List<string> log)
            {
                _log = log;
            }

            public int CallCount { get; private set; }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                CallCount++;
                _log?.Add("Write:" + operation.RootRole + ":" + operation.MarkerKind);
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly Func<CaptureRunInitializationRecoveryInspectionOperation, CaptureRunInitializationRecoveryInspectionSnapshot> _factory;

            public FakeInspector(Func<CaptureRunInitializationRecoveryInspectionOperation, CaptureRunInitializationRecoveryInspectionSnapshot> factory)
            {
                _factory = factory;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return _factory(operation);
            }
        }

        private sealed class FakeRecoveryCleanup : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        // ---- Constructor ----

        [Test]
        public void Coordinator_Constructor_NullDependency_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoverySessionRoutingCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("startFreshCoordinator"));
        }

        // ---- Start-fresh routing ----

        [Test]
        public void TryContinue_StartFresh_Completes()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log) { NextId = OtherInitId };
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            Assert.That(success, Is.True);
            Assert.That(session, Is.Not.Null);
            Assert.That(lease, Is.Null);
            Assert.That(session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(session.ExecutionReceipt, Is.Not.Null);
            Assert.That(session.RecoveryOrchestrationResult, Is.Null);
            Assert.That(session.RunInitializationId, Is.EqualTo(OtherInitId));
            Assert.That(idSource.CallCount, Is.EqualTo(1));
            Assert.That(provisioner.CallCount, Is.EqualTo(2));
            Assert.That(writer.CallCount, Is.EqualTo(4));
        }

        [Test]
        public void TryContinue_CleanupTemporaryAndStartFresh_Completes()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            Assert.That(success, Is.True);
            Assert.That(session, Is.Not.Null);
            Assert.That(lease, Is.Null);
            Assert.That(session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(idSource.CallCount, Is.EqualTo(1));
        }

        // ---- Initialization-ready routing ----

        [Test]
        public void TryContinue_CompleteMissingPeer_CreatesRecoverySession()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            AssertRecoverySession(success, session, lease, recoveryResult, idSource, provisioner, writer);
        }

        [Test]
        public void TryContinue_CompleteReadyMarkers_CreatesRecoverySession()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeCanonicalInit(Final, binding.FinalInitialization), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            AssertRecoverySession(success, session, lease, recoveryResult, idSource, provisioner, writer);
        }

        [Test]
        public void TryContinue_AlreadyInitialized_CreatesRecoverySession()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            AssertRecoverySession(success, session, lease, recoveryResult, idSource, provisioner, writer);
        }

        // ---- False routing ----

        [Test]
        public void TryContinue_PublicationRecoveryRequired_False_LeaseUnchanged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunLockLease before = lease;
            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            Assert.That(success, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(lease, Is.SameAs(before));
            Assert.That(lease.IsCreated, Is.True);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void TryContinue_RunRootCollision_False_LeaseUnchanged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunLockLease before = lease;
            CaptureRunInitializationSession session;
            bool success = coordinator.TryContinueToSession(recoveryResult, ref lease, out session);

            Assert.That(success, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(lease, Is.SameAs(before));
            Assert.That(lease.IsCreated, Is.True);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        // ---- Rejection ----

        [Test]
        public void TryContinue_NullOrInvalidResult_Rejected_SessionNull()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationSession session = null;
            ArgumentNullException exNull = Assert.Throws<ArgumentNullException>(() => coordinator.TryContinueToSession(null, ref lease, out session));
            Assert.That(exNull.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(session, Is.Null);

            CaptureRunInitializationRecoveryOrchestrationResult invalid =
                (CaptureRunInitializationRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryOrchestrationResult));
            ArgumentException exInvalid = Assert.Throws<ArgumentException>(() => coordinator.TryContinueToSession(invalid, ref lease, out session));
            Assert.That(exInvalid.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(session, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void TryContinue_NullLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), lease);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunLockLease nullLease = null;
            CaptureRunInitializationSession session = null;
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.TryContinueToSession(recoveryResult, ref nullLease, out session));
            Assert.That(ex.ParamName, Is.EqualTo("lockLease"));
            Assert.That(session, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void TryContinue_DisposedLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease evidenceLease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), evidenceLease);

            CaptureRunLockLease disposedLease = MakeLease(layout, null);
            disposedLease.Dispose();

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationSession session = null;
            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryContinueToSession(recoveryResult, ref disposedLease, out session));
            Assert.That(ex.ParamName, Is.EqualTo("lockLease"));
            Assert.That(session, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void TryContinue_ForeignLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease evidenceLease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), evidenceLease);

            CaptureRunLockLease otherLease = MakeLease(layout, null);
            CaptureRunLockLease before = otherLease;

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationSession session = null;
            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryContinueToSession(recoveryResult, ref otherLease, out session));
            Assert.That(ex.ParamName, Is.EqualTo("lockLease"));
            Assert.That(otherLease, Is.SameAs(before));
            Assert.That(session, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void TryContinue_ForgedRootLayout_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), lease);

            SetField(recoveryResult.Snapshot.Operation, "_rootLayout", MakeLayout(2));

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationSession session = null;
            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryContinueToSession(recoveryResult, ref lease, out session));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(session, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void TryContinue_StatusDispositionMismatch_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), lease);

            CaptureRunInitializationRecoveryDecision decision = recoveryResult.Batch.ActionPlan.Decision;
            SetField(decision, "_disposition", CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationSession session = null;
            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryContinueToSession(recoveryResult, ref lease, out session));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(session, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
        }

        // ---- Exception propagation ----

        [Test]
        public void TryContinue_StartFreshException_PropagatesIdentical_SessionNull_LeaseUnchanged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> disposeLog = new List<string>();
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), lease);

            IOException injected = new IOException("id boom");
            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log) { Throw = injected };
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoverySessionRoutingCoordinator coordinator = MakeRoutingCoordinator(idSource, provisioner, writer);

            CaptureRunLockLease before = lease;
            CaptureRunInitializationSession session = null;
            IOException ex = Assert.Throws<IOException>(() => coordinator.TryContinueToSession(recoveryResult, ref lease, out session));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(session, Is.Null);
            Assert.That(lease, Is.SameAs(before));
            Assert.That(lease.IsCreated, Is.True);
            Assert.That(disposeLog, Is.Empty);
            Assert.That(idSource.CallCount, Is.EqualTo(1));
        }

        // ---- Shape ----

        [Test]
        public void Coordinator_Shape_OneReadonlyDep_NotDisposable_NoStaticState()
        {
            Type type = typeof(CaptureRunInitializationRecoverySessionRoutingCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(1));
            Assert.That(instanceFields[0].IsInitOnly, Is.True);
            Assert.That(instanceFields[0].FieldType, Is.EqualTo(typeof(CaptureRunInitializationRecoveryStartFreshCoordinator)));
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty, "No mutable static state.");
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoverySessionRoutingCoordinator.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("Stream"));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("Logger"));
            Assert.That(source, Does.Not.Contain("Registry"));
            Assert.That(source, Does.Not.Contain("Draft"));
            Assert.That(source, Does.Not.Contain("Trace"));
            Assert.That(source, Does.Not.Contain("System.Threading"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("Bootstrap"));
            Assert.That(source, Does.Not.Contain("CaptureRunLockAcquisition"));
            Assert.That(source, Does.Not.Contain("Inspector"));
            Assert.That(source, Does.Not.Contain("CapturePublication"));
            Assert.That(source, Does.Not.Contain(".Dispose()"));
            Assert.That(source, Does.Not.Contain("Retry"));
            Assert.That(source, Does.Not.Contain("retry"));
            Assert.That(source, Does.Not.Contain("Reinspect"));
        }

        // ---- Assertion helpers ----

        private static void AssertRecoverySession(
            bool success,
            CaptureRunInitializationSession session,
            CaptureRunLockLease lease,
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult,
            FakeIdSource idSource,
            FakeProvisioner provisioner,
            FakeWriter writer)
        {
            Assert.That(success, Is.True);
            Assert.That(session, Is.Not.Null);
            Assert.That(lease, Is.Null);
            Assert.That(session.ReadyEvidence.IsRecovery, Is.True);
            Assert.That(session.ExecutionReceipt, Is.Null);
            Assert.That(session.RecoveryOrchestrationResult, Is.SameAs(recoveryResult));
            Assert.That(session.RootLayout, Is.SameAs(recoveryResult.RootLayout));
            Assert.That(session.TestRunId, Is.EqualTo(recoveryResult.TestRunId));
            Assert.That(session.RunInitializationId, Is.EqualTo(recoveryResult.RunInitializationId));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }
    }
}
