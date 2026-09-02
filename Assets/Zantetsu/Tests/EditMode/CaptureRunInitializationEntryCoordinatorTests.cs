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
    public class CaptureRunInitializationEntryCoordinatorTests
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

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            return new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath, true, null) { Tag = "first" },
                new FakeHandle(pathSet.SecondLockPath, true, null) { Tag = "second" });
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return (T)field.GetValue(target);
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationEntryCoordinatorTests).Assembly.Location);
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

            public bool ThrowOnDispose { get; set; }

            public void Dispose()
            {
                DisposeCount++;
                _disposeLog?.Add(LockPath);
                if (ThrowOnDispose)
                {
                    throw new InvalidOperationException("Fake handle dispose failure" + (Tag == null ? string.Empty : ": " + Tag) + ".");
                }
            }
        }

        private sealed class FakeBackend : ICaptureRunLockBackend
        {
            private readonly List<string> _log;
            private readonly List<string> _disposeLog;
            private int _createdCount;

            public FakeBackend(List<string> log, List<string> disposeLog)
            {
                _log = log;
                _disposeLog = disposeLog;
            }

            public Func<string, string> Label { get; set; }

            public Func<string, bool> OnAcquire { get; set; }

            public Exception ThrowOnAcquire { get; set; }

            public bool ThrowOnDisposeSecond { get; set; }

            public List<FakeHandle> CreatedHandles { get; } = new List<FakeHandle>();

            public int AcquireCount { get; private set; }

            public bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle)
            {
                AcquireCount++;
                if (Label != null)
                {
                    _log?.Add(Label(absoluteLockPath));
                }

                if (ThrowOnAcquire != null)
                {
                    handle = null;
                    throw ThrowOnAcquire;
                }

                bool success = OnAcquire == null || OnAcquire(absoluteLockPath);
                if (success)
                {
                    _createdCount++;
                    FakeHandle created = new FakeHandle(absoluteLockPath, true, _disposeLog);
                    if (_createdCount == 2 && ThrowOnDisposeSecond)
                    {
                        created.ThrowOnDispose = true;
                    }

                    CreatedHandles.Add(created);
                    handle = created;
                    return true;
                }

                handle = null;
                return false;
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

            public Exception ThrowOnInspect { get; set; }

            public bool ReturnNullSnapshot { get; set; }

            public int InspectCount { get; private set; }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                InspectCount++;
                _log?.Add("Inspect");
                if (ThrowOnInspect != null)
                {
                    throw ThrowOnInspect;
                }

                if (ReturnNullSnapshot)
                {
                    return null;
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
                _log?.Add("Cleanup:" + operation.RootRole + ":" + operation.MarkerKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            private readonly List<string> _log;

            public FakeProvisioner(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public int CallCount { get; private set; }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                CallCount++;
                _log?.Add("Provision:" + operation.RootRole);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;

            public FakeWriter(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public int CallCount { get; private set; }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                CallCount++;
                _log?.Add("Write:" + operation.RootRole + ":" + operation.MarkerKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakeIdSource : ICaptureRunInitializationIdSource
        {
            private readonly List<string> _log;

            public FakeIdSource(List<string> log = null) { _log = log; }

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

        private sealed class Harness
        {
            public FakeBackend Backend;
            public FakeInspector Inspector;
            public FakeCleanupBackend RecoveryCleanup;
            public FakeProvisioner RecoveryProvisioner;
            public FakeWriter RecoveryWriter;
            public FakeIdSource IdSource;
            public FakeProvisioner FreshProvisioner;
            public FakeWriter FreshWriter;
            public CaptureRunInitializationEntryCoordinator Coordinator;
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

        private static Harness MakeHarness(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            List<string> log = null,
            List<string> disposeLog = null)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);

            Harness h = new Harness
            {
                Backend = new FakeBackend(log, disposeLog)
                {
                    Label = p => p == pathSet.FirstLockPath ? "Lock:first" : "Lock:second",
                    OnAcquire = _ => true
                },
                Inspector = MakeInspector(staging, final, log),
                RecoveryCleanup = new FakeCleanupBackend(log),
                RecoveryProvisioner = new FakeProvisioner(log),
                RecoveryWriter = new FakeWriter(log),
                IdSource = new FakeIdSource(log) { NextId = OtherInitId },
                FreshProvisioner = new FakeProvisioner(log),
                FreshWriter = new FakeWriter(log)
            };

            CaptureRunLockAcquisitionCoordinator lockCoordinator = new CaptureRunLockAcquisitionCoordinator(h.Backend);
            CaptureRunInitializationRecoveryExecutionCoordinator recoveryExecution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                h.RecoveryCleanup, h.RecoveryProvisioner, h.RecoveryWriter);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestration = new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                h.Inspector, recoveryExecution);
            CaptureRunInitializationExecutionCoordinator freshExecution = new CaptureRunInitializationExecutionCoordinator(
                h.FreshProvisioner, h.FreshWriter);
            CaptureRunInitializationRecoveryStartFreshCoordinator startFresh = new CaptureRunInitializationRecoveryStartFreshCoordinator(
                h.IdSource, freshExecution);
            CaptureRunInitializationRecoverySessionRoutingCoordinator routing = new CaptureRunInitializationRecoverySessionRoutingCoordinator(startFresh);
            h.Coordinator = new CaptureRunInitializationEntryCoordinator(lockCoordinator, orchestration, routing);
            return h;
        }

        // ---- Enum ----

        [Test]
        public void OpenStatus_EnumContract()
        {
            Type type = typeof(CaptureRunInitializationOpenStatus);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetNames(type), Is.EqualTo(new[] { "None", "SessionReady", "PublicationRecoveryRequired", "RunRootCollision" }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(4));
            for (int i = 0; i < 4; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        // ---- Constructor ----

        [Test]
        public void EntryCoordinator_Constructor_NullDependencies_Rejected()
        {
            FakeBackend backend = new FakeBackend(new List<string>(), new List<string>());
            CaptureRunLockAcquisitionCoordinator lockCoordinator = new CaptureRunLockAcquisitionCoordinator(backend);
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestration = new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                MakeInspector(MakeAbsent(Staging), MakeAbsent(Final)),
                new CaptureRunInitializationRecoveryExecutionCoordinator(new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter()));
            CaptureRunInitializationRecoverySessionRoutingCoordinator routing = new CaptureRunInitializationRecoverySessionRoutingCoordinator(
                new CaptureRunInitializationRecoveryStartFreshCoordinator(
                    new FakeIdSource(),
                    new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeWriter())));

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationEntryCoordinator(null, orchestration, routing));
            Assert.That(ex1.ParamName, Is.EqualTo("lockCoordinator"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationEntryCoordinator(lockCoordinator, null, routing));
            Assert.That(ex2.ParamName, Is.EqualTo("orchestrationCoordinator"));

            ArgumentNullException ex3 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationEntryCoordinator(lockCoordinator, orchestration, null));
            Assert.That(ex3.ParamName, Is.EqualTo("sessionRoutingCoordinator"));
        }

        // ---- Pre-validation ----

        [Test]
        public void TryOpen_NullRootLayout_Rejected()
        {
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final));

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => h.Coordinator.TryOpen(null, 4, out outcome, out owner));
            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.That(h.Backend.AcquireCount, Is.EqualTo(0));
        }

        [Test]
        public void TryOpen_EntryCountBoundaries_Rejected()
        {
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final));

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            Assert.Throws<ArgumentOutOfRangeException>(() => h.Coordinator.TryOpen(MakeLayout(), 0, out outcome, out owner));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.Throws<ArgumentOutOfRangeException>(() => h.Coordinator.TryOpen(MakeLayout(), 1025, out outcome, out owner));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.That(h.Backend.AcquireCount, Is.EqualTo(0));
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(0));
        }

        [Test]
        public void TryOpen_LockContention_False_NullOutcome_NoInspection()
        {
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final));
            h.Backend.OnAcquire = _ => false;

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner);

            Assert.That(success, Is.False);
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(0));
            Assert.That(h.IdSource.CallCount, Is.EqualTo(0));
        }

        // ---- Session-ready paths ----

        [Test]
        public void TryOpen_BothAbsent_SessionReady()
        {
            List<string> log = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), log);

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner);

            Assert.That(success, Is.True);
            Assert.That(outcome, Is.Not.Null);
            Assert.That(owner, Is.Not.Null);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
            Assert.That(outcome.Session, Is.Not.Null);
            Assert.That(outcome.LockIdentityEvidence.IsIssuedFor(owner), Is.True);
            Assert.That(outcome.Session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(outcome.Session.RunInitializationId, Is.EqualTo(OtherInitId));
            Assert.That(outcome.OrchestrationResult, Is.Not.Null);
            Assert.That(h.IdSource.CallCount, Is.EqualTo(1));
            owner.Dispose();
        }

        [Test]
        public void TryOpen_CleanupThenStartFresh_SessionReady()
        {
            Harness h = MakeHarness(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final));

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner);

            Assert.That(success, Is.True);
            Assert.That(owner, Is.Not.Null);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
            Assert.That(outcome.Session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(outcome.Session.RunInitializationId, Is.EqualTo(OtherInitId));
            Assert.That(h.IdSource.CallCount, Is.EqualTo(1));
            owner.Dispose();
        }

        [Test]
        public void TryOpen_PartialInit_SessionReady_RecoverySession()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            Harness h = MakeHarness(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final));

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.That(success, Is.True);
            Assert.That(owner, Is.Not.Null);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
            Assert.That(outcome.Session.ReadyEvidence.IsRecovery, Is.True);
            Assert.That(outcome.Session.ExecutionReceipt, Is.Null);
            Assert.That(outcome.Session.RecoveryOrchestrationResult, Is.SameAs(outcome.OrchestrationResult));
            Assert.That(outcome.Session.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(h.IdSource.CallCount, Is.EqualTo(0), "InitializationReady must not issue a fresh ID.");
            owner.Dispose();
        }

        [Test]
        public void TryOpen_AlreadyInitialized_SessionReady_NoMutation()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            Harness h = MakeHarness(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), log);

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.That(success, Is.True);
            Assert.That(owner, Is.Not.Null);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
            Assert.That(outcome.Session.ReadyEvidence.IsRecovery, Is.True);
            Assert.That(h.IdSource.CallCount, Is.EqualTo(0));
            Assert.That(h.FreshProvisioner.CallCount, Is.EqualTo(0));
            Assert.That(h.FreshWriter.CallCount, Is.EqualTo(0));
            Assert.That(log, Does.Not.Contain("Provision:"));
            Assert.That(log, Does.Not.Contain("Write:"));
            owner.Dispose();
        }

        // ---- Publication / collision paths ----

        [Test]
        public void TryOpen_PublicationRecovery_OutcomeHoldsLease()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding),
                null,
                disposeLog);

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.That(success, Is.True);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(outcome.Session, Is.Null);
            Assert.That(outcome.LockPathSet, Is.Not.Null);
            Assert.That(owner, Is.Not.Null);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(h.IdSource.CallCount, Is.EqualTo(0));

            owner.Dispose();
            Assert.That(disposeLog, Is.EqualTo(new[] { outcome.LockPathSet.SecondLockPath, outcome.LockPathSet.FirstLockPath }));
            Assert.That(owner.IsCreated, Is.False);
            Assert.That(outcome.IsValid, Is.False);
        }

        [Test]
        public void TryOpen_RunRootCollision_OutcomeHoldsLease_NoMutation()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), log, disposeLog);

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            bool success = h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.That(success, Is.True);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.RunRootCollision));
            Assert.That(outcome.Session, Is.Null);
            Assert.That(outcome.LockPathSet, Is.Not.Null);
            Assert.That(owner, Is.Not.Null);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(h.IdSource.CallCount, Is.EqualTo(0));
            Assert.That(log, Does.Not.Contain("Provision:"));
            Assert.That(log, Does.Not.Contain("Write:"));
            Assert.That(log, Does.Not.Contain("Cleanup:"));
            owner.Dispose();
        }

        // ---- Order ----

        [Test]
        public void TryOpen_FixedOrder_LockInspectThenRouting()
        {
            List<string> log = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), log);

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner);

            Assert.That(log, Is.EqualTo(new[]
            {
                "Lock:first",
                "Lock:second",
                "Inspect",
                "Id:Create",
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready",
                "Write:Final:Ready"
            }));
            Assert.That(h.Backend.AcquireCount, Is.EqualTo(2));
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(1));
            Assert.That(h.IdSource.CallCount, Is.EqualTo(1));
            owner.Dispose();
        }

        // ---- Failure propagation ----

        [Test]
        public void TryOpen_InspectorException_Propagates_LeaseReleased_OutcomeNull()
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout());
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), null, disposeLog);
            IOException injected = new IOException("inspect boom");
            h.Inspector.ThrowOnInspect = injected;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            IOException ex = Assert.Throws<IOException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.That(disposeLog, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
        }

        [Test]
        public void TryOpen_InspectorNullSnapshot_Rejected_LeaseReleased()
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout());
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), null, disposeLog);
            h.Inspector.ReturnNullSnapshot = true;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.That(disposeLog, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
        }

        [Test]
        public void TryOpen_IdSourceException_Propagates_LeaseReleased()
        {
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), null, disposeLog);
            IOException injected = new IOException("id boom");
            h.IdSource.Throw = injected;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            IOException ex = Assert.Throws<IOException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
        }

        [Test]
        public void TryOpen_FreshProvisionException_Propagates_LeaseReleased()
        {
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), null, disposeLog);
            IOException injected = new IOException("provision boom");
            h.FreshProvisioner.ExceptionToThrow = injected;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            IOException ex = Assert.Throws<IOException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
        }

        [Test]
        public void TryOpen_FreshWriterException_Propagates_LeaseReleased()
        {
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), null, disposeLog);
            IOException injected = new IOException("write boom");
            h.FreshWriter.ExceptionToThrow = injected;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            IOException ex = Assert.Throws<IOException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
        }

        [Test]
        public void TryOpen_RecoveryCleanupException_Propagates_LeaseReleased()
        {
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), null, disposeLog);
            IOException injected = new IOException("cleanup boom");
            h.RecoveryCleanup.ExceptionToThrow = injected;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            IOException ex = Assert.Throws<IOException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
        }

        [Test]
        public void TryOpen_Failure_CleanupAlsoFails_AggregateOrder()
        {
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final), null, disposeLog);
            h.Backend.ThrowOnDisposeSecond = true;
            IOException injected = new IOException("id boom");
            h.IdSource.Throw = injected;

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease owner = null;
            AggregateException ex = Assert.Throws<AggregateException>(() => h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner));

            Assert.That(outcome, Is.Null);
            Assert.That(owner, Is.Null);
            Assert.That(ex.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(ex.InnerExceptions[0], Is.SameAs(injected));
        }

        // ---- Ownership release ----

        [Test]
        public void Outcome_NonDisposable_SessionPath_OwnerReleases()
        {
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final));
            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            h.Coordinator.TryOpen(MakeLayout(), 4, out outcome, out owner);

            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureRunInitializationOpenOutcome)), Is.False);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(outcome.IsValid, Is.True);
            Assert.That(outcome.Session, Is.Not.Null);
            Assert.That(outcome.LockIdentityEvidence.IsIssuedFor(owner), Is.True);

            owner.Dispose();
            Assert.That(owner.IsCreated, Is.False);
            Assert.That(outcome.IsValid, Is.False);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
        }

        [Test]
        public void Owner_Dispose_Idempotent_And_RetryAfterFailure()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), null, disposeLog);
            h.Backend.ThrowOnDisposeSecond = true;

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.Throws<AggregateException>(() => owner.Dispose());
            Assert.That(owner.IsCreated, Is.False);

            h.Backend.CreatedHandles[1].ThrowOnDispose = false;
            owner.Dispose();
            owner.Dispose();
            Assert.That(owner.IsCreated, Is.False);
            Assert.That(disposeLog, Is.EqualTo(new[]
            {
                outcome.LockPathSet.SecondLockPath,
                outcome.LockPathSet.FirstLockPath,
                outcome.LockPathSet.SecondLockPath
            }));
        }

        [Test]
        public void RecoveryOnlyOutcome_OwnerRelease_InvalidAfterDispose()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> disposeLog = new List<string>();
            Harness h = MakeHarness(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding),
                null,
                disposeLog);

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(outcome.Session, Is.Null);
            Assert.That(owner, Is.Not.Null);
            Assert.That(outcome.IsValid, Is.True);

            owner.Dispose();
            Assert.That(owner.IsCreated, Is.False);
            Assert.That(outcome.IsValid, Is.False);
            Assert.That(outcome.Status, Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
        }

        [Test]
        public void Owner_DisposeFailure_Retryable_And_OutcomeInvalidated()
        {
            CaptureRunRootLayout layout = MakeLayout();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final));
            h.Backend.ThrowOnDisposeSecond = true;

            CaptureRunInitializationOpenOutcome outcome;
            CaptureRunInitializationSessionOwnershipLease owner;
            h.Coordinator.TryOpen(layout, 4, out outcome, out owner);

            Assert.That(owner.IsCreated, Is.True);
            Assert.That(outcome.IsValid, Is.True);

            Assert.Throws<AggregateException>(() => owner.Dispose());
            Assert.That(owner.IsCreated, Is.False, "A partially released ownership lease is no longer live but remains retryable.");
            Assert.That(outcome.IsValid, Is.False, "A partially released ownership lease invalidates the outcome identity evidence.");

            h.Backend.CreatedHandles[1].ThrowOnDispose = false;
            owner.Dispose();
            Assert.That(owner.IsCreated, Is.False);
            Assert.That(outcome.IsValid, Is.False);
        }

        // ---- Forged outcome ----

        [Test]
        public void Outcome_Forged_IsValidFalse_WithoutException()
        {
            CaptureRunRootLayout layout = MakeLayout();
            Harness h = MakeHarness(MakeAbsent(Staging), MakeAbsent(Final));
            CaptureRunInitializationOpenOutcome good;
            CaptureRunInitializationSessionOwnershipLease goodOwner;
            h.Coordinator.TryOpen(layout, 4, out good, out goodOwner);

            // Missing orchestration result.
            CaptureRunInitializationOpenOutcome nullOrchestration = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(nullOrchestration, "_lockIdentityEvidence", good.LockIdentityEvidence);
            SetField(nullOrchestration, "_sessionIssue", GetField<CaptureRunInitializationSessionIssue>(good, "_sessionIssue"));
            Assert.That(nullOrchestration.IsValid, Is.False);

            // Missing identity evidence.
            CaptureRunInitializationOpenOutcome nullIdentity = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(nullIdentity, "_orchestrationResult", good.OrchestrationResult);
            SetField(nullIdentity, "_sessionIssue", GetField<CaptureRunInitializationSessionIssue>(good, "_sessionIssue"));
            Assert.That(nullIdentity.IsValid, Is.False);

            // Session-ready outcome with its session issue removed.
            CaptureRunInitializationOpenOutcome noIssue = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(noIssue, "_orchestrationResult", good.OrchestrationResult);
            SetField(noIssue, "_lockIdentityEvidence", good.LockIdentityEvidence);
            SetField(noIssue, "_sessionIssue", null);
            Assert.That(noIssue.IsValid, Is.False);

            // Empty outcome.
            CaptureRunInitializationOpenOutcome empty = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            Assert.That(empty.IsValid, Is.False);

            goodOwner.Dispose();
        }

        // ---- Shape ----

        [Test]
        public void EntryCoordinator_Shape_ThreeReadonlyDeps_NotDisposable_NoStaticState()
        {
            Type type = typeof(CaptureRunInitializationEntryCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Outcome_Shape_NonDisposable_ThreeReadonlyFields_IdentityAlwaysHeld()
        {
            Type type = typeof(CaptureRunInitializationOpenOutcome);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
            Assert.That(fields.Any(f => f.FieldType == typeof(CaptureRunInitializationRecoveryOrchestrationResult)), Is.True);
            Assert.That(fields.Any(f => f.FieldType == typeof(CaptureRunInitializationSessionIssue)), Is.True);
            Assert.That(fields.Any(f => f.FieldType == typeof(CaptureRunLockIdentityEvidence)), Is.True);
            Assert.That(fields.Any(f => f.FieldType == typeof(CaptureRunLockLease)), Is.False);
            Assert.That(fields.Any(f => f.FieldType == typeof(CaptureRunInitializationSessionOwnershipLease)), Is.False);

            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(p => p.PropertyType == typeof(CaptureRunInitializationSessionIssue)), Is.False);
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(p => p.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)), Is.False);
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(m => m.ReturnType == typeof(CaptureRunInitializationSessionIssue)), Is.False);
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Any(m => m.ReturnType == typeof(CaptureRunInitializationSessionOwnershipLease)), Is.False);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationOpenStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationOpenOutcome.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationEntryCoordinator.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string source = File.ReadAllText(LocateSource(relativePath));

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
            }

            string entry = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationEntryCoordinator.cs"));
            string outcome = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationOpenOutcome.cs"));

            Assert.That(entry, Does.Not.Contain("Bootstrap"));
            Assert.That(entry, Does.Not.Contain("CapturePublication"));
            Assert.That(entry, Does.Not.Contain("IdGenerator"));
            Assert.That(outcome, Does.Not.Contain("Bootstrap"));
            Assert.That(outcome, Does.Not.Contain("CapturePublication"));
        }

        [Test]
        public void EntryCoordinator_Source_ExclusiveRawOrOwnerCleanup()
        {
            string entry = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationEntryCoordinator.cs"));

            // The catch must release exactly one held lock: the ownership lease
            // once the raw lease has been transferred, or the raw lease itself
            // when ownership transfer has not yet completed.
            Assert.That(entry, Does.Contain("if (heldOwnershipLease != null)"));
            Assert.That(entry, Does.Contain("else if (lease != null)"));
            Assert.That(entry, Does.Contain("heldOwnershipLease.Dispose();"));
            Assert.That(entry, Does.Contain("lease.Dispose();"));
        }
    }
}
