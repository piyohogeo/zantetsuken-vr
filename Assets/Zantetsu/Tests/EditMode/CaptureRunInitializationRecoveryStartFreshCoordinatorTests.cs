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
    public class CaptureRunInitializationRecoveryStartFreshCoordinatorTests
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

        private static CaptureRunInitializationSessionOwnershipLease MakeOwnershipLease(CaptureRunRootLayout layout, List<string> disposeLog, out FakeHandle first, out FakeHandle second)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog, out first, out second);
            return CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
        }

        private static CaptureRunInitializationSessionOwnershipLease MakeOwnershipLease(CaptureRunRootLayout layout, List<string> disposeLog)
        {
            return MakeOwnershipLease(layout, disposeLog, out _, out _);
        }

        private static CaptureRunLockIdentityEvidence MakeIdentityEvidence(CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            return CaptureRunLockIdentityEvidence.Create(ownershipLease, ownershipLease.LockPathSet);
        }

        private static CaptureRunInitializationRecoveryOrchestrationResult MakeRecoveryResult(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunRootLayout layout = identity.RootLayout;
            FakeInspector inspector = MakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator executionCoordinator = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeRecoveryCleanup(), new FakeProvisioner(null), new FakeWriter(null));
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                inspector, executionCoordinator);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
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

        private static CaptureRunInitializationRecoveryStartFreshCoordinator MakeCoordinator(
            FakeIdSource idSource, FakeProvisioner provisioner, FakeWriter writer)
        {
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(provisioner, writer);
            return new CaptureRunInitializationRecoveryStartFreshCoordinator(idSource, executionCoordinator);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryStartFreshCoordinatorTests).Assembly.Location);
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

            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
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
            private readonly Dictionary<int, Exception> _exceptions = new Dictionary<int, Exception>();
            private int _callCount;

            public FakeProvisioner(List<string> log)
            {
                _log = log;
            }

            public int CallCount => _callCount;

            public void ThrowOnCall(int callNumber, Exception exception)
            {
                _exceptions[callNumber] = exception;
            }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                _callCount++;
                _log?.Add("Provision:" + operation.RootRole);

                if (_exceptions.TryGetValue(_callCount, out Exception exception))
                {
                    throw exception;
                }

                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;
            private readonly Dictionary<int, Exception> _exceptions = new Dictionary<int, Exception>();
            private int _callCount;

            public FakeWriter(List<string> log)
            {
                _log = log;
            }

            public int CallCount => _callCount;

            public void ThrowOnCall(int callNumber, Exception exception)
            {
                _exceptions[callNumber] = exception;
            }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                _callCount++;
                _log?.Add("Write:" + operation.RootRole + ":" + operation.MarkerKind);

                if (_exceptions.TryGetValue(_callCount, out Exception exception))
                {
                    throw exception;
                }

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
        public void Coordinator_Constructor_NullDependencies_Rejected()
        {
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(null), new FakeWriter(null));

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryStartFreshCoordinator(null, executionCoordinator));
            Assert.That(ex1.ParamName, Is.EqualTo("initializationIdSource"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryStartFreshCoordinator(new FakeIdSource(null), null));
            Assert.That(ex2.ParamName, Is.EqualTo("executionCoordinator"));
        }

        // ---- Normal completion ----

        [Test]
        public void Continue_StartFresh_CompletesAndIssues()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log) { NextId = OtherInitId };
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue = coordinator.Continue(recoveryResult, owner, identity);

            Assert.That(issue, Is.Not.Null);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(issue.OwnershipLease, Is.SameAs(owner));
            Assert.That(issue.LockIdentityEvidence, Is.SameAs(identity));
            Assert.That(disposeLog, Is.Empty, "Ownership lease retained by the caller, not disposed here.");
            Assert.That(issue.Session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(issue.Session.ExecutionReceipt, Is.Not.Null);
            Assert.That(issue.Session.RecoveryOrchestrationResult, Is.Null);
            Assert.That(issue.Session.RootLayout, Is.SameAs(layout));
            Assert.That(issue.Session.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(issue.Session.RunInitializationId, Is.EqualTo(OtherInitId));
            Assert.That(idSource.CallCount, Is.EqualTo(1));

            owner.Dispose();
        }

        [Test]
        public void Continue_CleanupTemporaryAndStartFresh_Completes()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue = coordinator.Continue(recoveryResult, owner, identity);

            Assert.That(issue, Is.Not.Null);
            Assert.That(issue.Session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(issue.Session.RunInitializationId, Is.EqualTo(OtherInitId));
            Assert.That(idSource.CallCount, Is.EqualTo(1));

            owner.Dispose();
        }

        [Test]
        public void Continue_IdIssuedOnce_FixedOrder()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue = coordinator.Continue(recoveryResult, owner, identity);

            Assert.That(log, Is.EqualTo(new[]
            {
                "Id:Create",
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready",
                "Write:Final:Ready"
            }));
            Assert.That(idSource.CallCount, Is.EqualTo(1));
            Assert.That(provisioner.CallCount, Is.EqualTo(2));
            Assert.That(writer.CallCount, Is.EqualTo(4));
            Assert.That(issue.Session.RunInitializationId, Is.EqualTo(OtherInitId));

            owner.Dispose();
        }

        // ---- Disposition rejection ----

        [Test]
        public void Continue_InitializationReady_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, owner, identity));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        [Test]
        public void Continue_PublicationRecoveryRequired_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, owner, identity));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        [Test]
        public void Continue_RunRootCollision_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, owner, identity));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        [Test]
        public void Continue_ForgedDisposition_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            CaptureRunInitializationRecoveryDecision decision = recoveryResult.Batch.ActionPlan.Decision;
            SetField(decision, "_disposition", CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, owner, identity));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        [Test]
        public void Continue_StartFreshWithExpectedBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            CaptureRunInitializationRecoveryDecision decision = recoveryResult.Batch.ActionPlan.Decision;
            SetField(decision, "_expectedBinding", MakeBinding(layout));

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, owner, identity));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        // ---- Lease rejection ----

        [Test]
        public void Continue_ExpectedIdentityForeignOwner_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identityA = MakeIdentityEvidence(ownerA);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identityA);

            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null);
            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, ownerB, identityA));
            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(ownerA.IsCreated, Is.True);
            Assert.That(ownerB.IsCreated, Is.True);
            ownerA.Dispose();
            ownerB.Dispose();
        }

        [Test]
        public void Continue_ExpectedOwnerForeignIdentity_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identityA = MakeIdentityEvidence(ownerA);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identityA);

            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identityB = MakeIdentityEvidence(ownerB);
            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, ownerA, identityB));
            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(ownerA.IsCreated, Is.True);
            Assert.That(ownerB.IsCreated, Is.True);
            ownerA.Dispose();
            ownerB.Dispose();
        }

        [Test]
        public void Continue_ForeignCorrelatedPair_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identityA = MakeIdentityEvidence(ownerA);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identityA);

            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identityB = MakeIdentityEvidence(ownerB);
            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, ownerB, identityB));
            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(ownerA.IsCreated, Is.True);
            Assert.That(ownerB.IsCreated, Is.True);
            ownerA.Dispose();
            ownerB.Dispose();
        }

        [Test]
        public void Continue_DisposedOwner_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease evidenceOwner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence evidenceIdentity = MakeIdentityEvidence(evidenceOwner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), evidenceIdentity);

            CaptureRunInitializationSessionOwnershipLease disposedOwner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence disposedIdentity = MakeIdentityEvidence(disposedOwner);
            disposedOwner.Dispose();

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, disposedOwner, disposedIdentity));
            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            evidenceOwner.Dispose();
        }

        [Test]
        public void Continue_ForgedRootLayout_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            SetField(recoveryResult.Snapshot.Operation, "_rootLayout", MakeLayout(2));

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, new FakeProvisioner(log), new FakeWriter(log));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Continue(recoveryResult, owner, identity));
            Assert.That(ex.ParamName, Is.EqualTo("recoveryResult"));
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        [Test]
        public void Continue_NullInputs_Rejected_NoIdNoExecution()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            ArgumentNullException exNull = Assert.Throws<ArgumentNullException>(() => coordinator.Continue(null, owner, identity));
            Assert.That(exNull.ParamName, Is.EqualTo("recoveryResult"));

            ArgumentNullException exOwnerNull = Assert.Throws<ArgumentNullException>(() => coordinator.Continue(recoveryResult, null, identity));
            Assert.That(exOwnerNull.ParamName, Is.EqualTo("ownershipLease"));

            ArgumentNullException exIdentityNull = Assert.Throws<ArgumentNullException>(() => coordinator.Continue(recoveryResult, owner, null));
            Assert.That(exIdentityNull.ParamName, Is.EqualTo("lockIdentityEvidence"));

            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(owner.IsCreated, Is.True);
            owner.Dispose();
        }

        // ---- Exception propagation ----

        [Test]
        public void Continue_IdSourceException_PropagatesIdentical_OwnerUnchanged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

            IOException injected = new IOException("id boom");
            List<string> log = new List<string>();
            FakeIdSource idSource = new FakeIdSource(log) { Throw = injected };
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Continue(recoveryResult, owner, identity));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(disposeLog, Is.Empty);
            Assert.That(idSource.CallCount, Is.EqualTo(1));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            owner.Dispose();
        }

        [Test]
        public void Continue_ExecutionFailures_AllPositions_Propagate_NoRetry_OwnerUnchanged()
        {
            for (int position = 1; position <= 6; position++)
            {
                CaptureRunRootLayout layout = MakeLayout();
                List<string> disposeLog = new List<string>();
                CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog);
                CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
                CaptureRunInitializationRecoveryOrchestrationResult recoveryResult = MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity);

                IOException injected = new IOException("exec boom " + position);
                List<string> log = new List<string>();
                FakeIdSource idSource = new FakeIdSource(log);
                FakeProvisioner provisioner = new FakeProvisioner(log);
                FakeWriter writer = new FakeWriter(log);

                switch (position)
                {
                    case 1: provisioner.ThrowOnCall(1, injected); break;
                    case 2: writer.ThrowOnCall(1, injected); break;
                    case 3: provisioner.ThrowOnCall(2, injected); break;
                    case 4: writer.ThrowOnCall(2, injected); break;
                    case 5: writer.ThrowOnCall(3, injected); break;
                    default: writer.ThrowOnCall(4, injected); break;
                }

                CaptureRunInitializationRecoveryStartFreshCoordinator coordinator = MakeCoordinator(idSource, provisioner, writer);

                IOException ex = Assert.Throws<IOException>(() => coordinator.Continue(recoveryResult, owner, identity));

                Assert.That(ex, Is.SameAs(injected), "position " + position);
                Assert.That(owner.IsCreated, Is.True, "position " + position);
                Assert.That(disposeLog, Is.Empty, "position " + position);
                Assert.That(idSource.CallCount, Is.EqualTo(1), "position " + position);
                owner.Dispose();
            }
        }

        // ---- Shape ----

        [Test]
        public void Coordinator_Shape_TwoReadonlyDeps_NotDisposable_NoStaticState()
        {
            Type type = typeof(CaptureRunInitializationRecoveryStartFreshCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(2));
            Assert.That(instanceFields.All(f => f.IsInitOnly), Is.True);

            int idFields = 0;
            int executionFields = 0;
            foreach (FieldInfo field in instanceFields)
            {
                if (field.FieldType == typeof(ICaptureRunInitializationIdSource))
                {
                    idFields++;
                }
                else if (field.FieldType == typeof(CaptureRunInitializationExecutionCoordinator))
                {
                    executionFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(idFields, Is.EqualTo(1));
            Assert.That(executionFields, Is.EqualTo(1));
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty, "No mutable static state.");
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryStartFreshCoordinator.cs"));

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
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationRecoveryExecutionCoordinator"));
        }
    }
}
