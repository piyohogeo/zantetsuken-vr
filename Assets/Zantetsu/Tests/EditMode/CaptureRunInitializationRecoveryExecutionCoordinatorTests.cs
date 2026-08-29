using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationRecoveryExecutionCoordinatorTests
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

        private static CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null,
            List<string> disposeLog = null)
        {
            layout = layout ?? MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, lease, 4);
            return new CaptureRunInitializationRecoveryInspectionSnapshot(new FakeInspector(), operation, staging, final);
        }

        private static CaptureRunInitializationRecoveryExecutionBatch BuildBatch(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null)
        {
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(MakeSnapshot(staging, final, layout)));
            return CaptureRunInitializationRecoveryExecutionBatchBuilder.Build(plan);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryExecutionCoordinatorTests).Assembly.Location);
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
            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                throw new NotSupportedException("The coordinator must never call the inspector back.");
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            private readonly List<string> _log;

            public FakeCleanupBackend(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunInitializationRecoveryCleanupOperation, CaptureRunInitializationRecoveryCleanupReceipt> ReceiptOverride { get; set; }

            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                _log?.Add("cleanup:" + operation.RootRole + ":" + operation.MarkerKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                if (ReceiptOverride != null) return ReceiptOverride(operation);
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            private readonly List<string> _log;

            public FakeProvisioner(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunRootProvisionOperation, CaptureRunRootProvisionReceipt> ReceiptOverride { get; set; }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                _log?.Add("provision:" + operation.RootRole);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                if (ReceiptOverride != null) return ReceiptOverride(operation);
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;

            public FakeWriter(List<string> log = null) { _log = log; }

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunMarkerWriteOperation, CaptureRunMarkerWriteReceipt> ReceiptOverride { get; set; }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                _log?.Add("write:" + operation.RootRole + ":" + operation.MarkerKind);
                if (ExceptionToThrow != null) throw ExceptionToThrow;
                if (ReceiptOverride != null) return ReceiptOverride(operation);
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private static CaptureRunInitializationRecoveryExecutionCoordinator MakeCoordinator(
            FakeCleanupBackend cleanup, FakeProvisioner provisioner, FakeWriter writer)
        {
            return new CaptureRunInitializationRecoveryExecutionCoordinator(cleanup, provisioner, writer);
        }

        // ---- Call order ----

        [Test]
        public void Execute_CompleteMissingPeer_CallOrder()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(log, Is.EqualTo(new[]
            {
                "cleanup:Staging:Initialization",
                "provision:Final",
                "write:Final:Initialization",
                "write:Staging:Ready",
                "write:Final:Ready"
            }));
            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.InitializationReady));
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Execute_CompleteReadyMarkers_CallOrder()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Absent, null, hasInitTmp: true),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(log, Is.EqualTo(new[]
            {
                "cleanup:Staging:Initialization",
                "write:Staging:Ready",
                "write:Final:Ready"
            }));
            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.InitializationReady));
        }

        [Test]
        public void Execute_CleanupTemporaryAndStartFresh_StartFreshRequired()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(log, Is.EqualTo(new[]
            {
                "cleanup:Staging:Initialization",
                "cleanup:Staging:None"
            }));
            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired));
        }

        [Test]
        public void Execute_RoutingDispositions_NoBackendCalls()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> log = new List<string>();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch[] batches =
            {
                BuildBatch(MakeAbsent(Staging), MakeAbsent(Final), layout), // StartFresh
                BuildBatch(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout), // AlreadyInitialized
                BuildBatch(MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true), MakeFullyCanonical(Final, binding), layout), // Publication
                BuildBatch(MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), layout) // Collision
            };

            foreach (CaptureRunInitializationRecoveryExecutionBatch batch in batches)
            {
                CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(result.GetCompletedStep(0).CleanupReceipt, Is.Null);
                Assert.That(result.GetCompletedStep(0).ProvisionReceipt, Is.Null);
                Assert.That(result.GetCompletedStep(0).MarkerWriteReceipt, Is.Null);
            }

            Assert.That(log, Is.Empty, "Routing dispositions must never contact a backend.");
        }

        [Test]
        public void Execute_Collision_SingleRoutingCompletion_NoMutationReceipt()
        {
            CaptureRunRootLayout layout = MakeLayout();
            List<string> log = new List<string>();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.Count, Is.EqualTo(1));
            CaptureRunInitializationRecoveryCompletedStep completed = result.GetCompletedStep(0);
            Assert.That(completed.CleanupReceipt, Is.Null);
            Assert.That(completed.ProvisionReceipt, Is.Null);
            Assert.That(completed.MarkerWriteReceipt, Is.Null);
            Assert.That(result.Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision));
            Assert.That(log, Is.Empty);
        }

        // ---- Receipt violations ----

        [Test]
        public void Execute_Cleanup_NullReceipt_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            FakeCleanupBackend cleanup = new FakeCleanupBackend { ReceiptOverride = _ => null };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(cleanup, new FakeProvisioner(), new FakeWriter());

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Cleanup_ForeignIssuer_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            FakeCleanupBackend foreign = new FakeCleanupBackend();
            FakeCleanupBackend cleanup = new FakeCleanupBackend
            {
                ReceiptOverride = op => new CaptureRunInitializationRecoveryCleanupReceipt(foreign, op)
            };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(cleanup, new FakeProvisioner(), new FakeWriter());

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Provision_ForeignIssuer_Rejected_StopsSubsequentSteps()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeProvisioner foreign = new FakeProvisioner();
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log)
            {
                ReceiptOverride = op => new CaptureRunRootProvisionReceipt(foreign, op)
            };
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(log), provisioner, writer);

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
            Assert.That(log, Is.EqualTo(new[] { "provision:Final" }), "Subsequent write steps must not execute after a contract violation.");
        }

        [Test]
        public void Execute_Write_ForeignIssuer_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeWriter foreign = new FakeWriter();
            FakeWriter writer = new FakeWriter
            {
                ReceiptOverride = op => new CaptureRunMarkerWriteReceipt(foreign, op)
            };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(), new FakeProvisioner(), writer);

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Write_NullReceipt_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeWriter writer = new FakeWriter { ReceiptOverride = _ => null };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(), new FakeProvisioner(), writer);

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Write_DifferentOperation_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            FakeWriter writer = new FakeWriter();
            CaptureRunMarkerWriteOperation wrongOperation = batch.GetPreparedStep(1).MarkerWriteOperation;
            writer.ReceiptOverride = op => new CaptureRunMarkerWriteReceipt(writer, wrongOperation);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(), new FakeProvisioner(), writer);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Provision_NullReceipt_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeProvisioner provisioner = new FakeProvisioner { ReceiptOverride = _ => null };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(), provisioner, new FakeWriter());

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Provision_DifferentOperation_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeProvisioner provisioner = new FakeProvisioner();
            CaptureRunRootProvisionOperation wrongOperation = BuildBatch(
                MakeAbsent(Staging),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout).GetPreparedStep(0).ProvisionOperation;
            provisioner.ReceiptOverride = op => new CaptureRunRootProvisionReceipt(provisioner, wrongOperation);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(), provisioner, new FakeWriter());

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(batch));
        }

        [Test]
        public void Execute_Cleanup_DifferentOperation_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryExecutionBatch twoTmpBatch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true),
                MakeAbsent(Final),
                layout);

            FakeCleanupBackend cleanup = new FakeCleanupBackend();
            CaptureRunInitializationRecoveryCleanupOperation wrongOperation = twoTmpBatch.GetPreparedStep(1).CleanupOperation;
            cleanup.ReceiptOverride = op => new CaptureRunInitializationRecoveryCleanupReceipt(cleanup, wrongOperation);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(cleanup, new FakeProvisioner(), new FakeWriter());

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(twoTmpBatch));
        }

        // ---- Exception propagation / no retry / no rollback ----

        [Test]
        public void Execute_BackendException_PropagatesIdentical_NoRetry()
        {
            CaptureRunRootLayout layout = MakeLayout();
            IOException exception = new IOException("boom");
            List<string> log = new List<string>();
            FakeCleanupBackend cleanup = new FakeCleanupBackend(log) { ExceptionToThrow = exception };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(cleanup, new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            IOException ex = Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(ex, Is.SameAs(exception));
            Assert.That(log, Is.EqualTo(new[] { "cleanup:Staging:Initialization" }), "No retry and no subsequent steps after an exception.");
        }

        [Test]
        public void Execute_PartialFailure_NoLeaseDispose_NoRollback()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> disposeLog = new List<string>();
            List<string> log = new List<string>();

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, disposeLog);
            CaptureRunInitializationRecoveryExecutionBatch batch = CaptureRunInitializationRecoveryExecutionBatchBuilder.Build(
                CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                    CaptureRunInitializationRecoveryClassifier.Classify(snapshot)));

            FakeWriter writer = new FakeWriter(log) { ExceptionToThrow = new IOException("write failed") };
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(new FakeCleanupBackend(log), new FakeProvisioner(log), writer);

            Assert.Throws<IOException>(() => coordinator.Execute(batch));

            Assert.That(disposeLog, Is.Empty, "The coordinator must not dispose the lease on failure.");
            Assert.That(log, Is.EqualTo(new[] { "cleanup:Staging:Initialization", "provision:Final", "write:Final:Initialization" }));
        }

        // ---- Invalid batch rejection ----

        [Test]
        public void Execute_NullBatch_Rejected()
        {
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
            Assert.That(ex.ParamName, Is.EqualTo("batch"));
        }

        [Test]
        public void Execute_InvalidBatch_Rejected_NoBackendCalls()
        {
            List<string> log = new List<string>();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(log), new FakeProvisioner(log), new FakeWriter(log));

            CaptureRunInitializationRecoveryExecutionBatch batch = (CaptureRunInitializationRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryExecutionBatch));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute(batch));
            Assert.That(ex.ParamName, Is.EqualTo("batch"));
            Assert.That(log, Is.Empty);
        }

        // ---- Status mapping ----

        [Test]
        public void Result_StatusMapping()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());

            Assert.That(coordinator.Execute(BuildBatch(MakeAbsent(Staging), MakeAbsent(Final), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired));
            Assert.That(coordinator.Execute(BuildBatch(MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired));
            Assert.That(coordinator.Execute(BuildBatch(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.InitializationReady));
            Assert.That(coordinator.Execute(BuildBatch(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeCanonicalInit(Final, binding.FinalInitialization), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.InitializationReady));
            Assert.That(coordinator.Execute(BuildBatch(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.InitializationReady));
            Assert.That(coordinator.Execute(BuildBatch(MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true), MakeFullyCanonical(Final, binding), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired));
            Assert.That(coordinator.Execute(BuildBatch(MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), layout)).Status, Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision));
        }

        // ---- Result correlation ----

        [Test]
        public void Result_CompletedSteps_CountOrderOperationCorrelation()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());

            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(result.Count, Is.EqualTo(batch.Count));
            Assert.That(result.Batch, Is.SameAs(batch));
            Assert.That(result.RootLayout, Is.SameAs(layout));
            Assert.That(result.RunInitializationId, Is.EqualTo(InitId));

            for (int i = 0; i < batch.Count; i++)
            {
                CaptureRunInitializationRecoveryCompletedStep completed = result.GetCompletedStep(i);
                Assert.That(completed.PreparedStep, Is.SameAs(batch.GetPreparedStep(i)));
                if (completed.PreparedStep.Action == CaptureRunInitializationRecoveryAction.ProvisionRoot)
                {
                    Assert.That(completed.ProvisionReceipt.Operation, Is.SameAs(completed.PreparedStep.ProvisionOperation));
                }
                else
                {
                    Assert.That(completed.MarkerWriteReceipt.Operation, Is.SameAs(completed.PreparedStep.MarkerWriteOperation));
                }
            }

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_ArrayDefensiveCopy_NotExposed()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            Assert.That(
                typeof(CaptureRunInitializationRecoveryExecutionResult)
                    .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunInitializationRecoveryCompletedStep[])),
                Is.False,
                "The completed-step array must not be exposed.");
        }

        // ---- Result direct-constructor defense ----

        [Test]
        public void Result_DirectConstructor_MissingExtraSwappedForeign_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout); // 2 steps

            CaptureRunInitializationRecoveryExecutionBatch otherBatch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout); // 4 steps, different prepared steps

            CaptureRunInitializationRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunInitializationRecoveryCompletedStep step0 = good.GetCompletedStep(0);
            CaptureRunInitializationRecoveryCompletedStep step1 = good.GetCompletedStep(1);

            // missing
            AssertResultRejected(coordinator, batch, new[] { step0 });
            // extra
            AssertResultRejected(coordinator, batch, new[] { step0, step1, step0 });
            // swapped
            AssertResultRejected(coordinator, batch, new[] { step1, step0 });
            // foreign prepared step (from a different batch)
            CaptureRunInitializationRecoveryCompletedStep foreign = coordinator.Execute(otherBatch).GetCompletedStep(0);
            AssertResultRejected(coordinator, batch, new[] { foreign, step1 });
        }

        [Test]
        public void Result_IsValid_False_ForBrokenValues()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            CaptureRunInitializationRecoveryExecutionResult result = coordinator.Execute(batch);

            // forge a result with null steps
            CaptureRunInitializationRecoveryExecutionResult nullSteps = (CaptureRunInitializationRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryExecutionResult));
            SetField(nullSteps, "_issuedBy", coordinator);
            SetField(nullSteps, "_batch", batch);
            SetField(nullSteps, "_completedSteps", null);
            Assert.That(nullSteps.IsValid, Is.False);

            // forge a result with a null element
            CaptureRunInitializationRecoveryExecutionResult nullElement = (CaptureRunInitializationRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryExecutionResult));
            SetField(nullElement, "_issuedBy", coordinator);
            SetField(nullElement, "_batch", batch);
            SetField(nullElement, "_completedSteps", new CaptureRunInitializationRecoveryCompletedStep[] { null, result.GetCompletedStep(1) });
            Assert.That(nullElement.IsValid, Is.False);
        }

        [Test]
        public void Result_DirectConstructor_ForeignCleanupIssuer_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            FakeCleanupBackend cleanup = new FakeCleanupBackend();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                cleanup, new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout); // step 0 = cleanup

            CaptureRunInitializationRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunInitializationRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakeCleanupBackend foreign = new FakeCleanupBackend();
            CaptureRunInitializationRecoveryCleanupReceipt foreignReceipt =
                new CaptureRunInitializationRecoveryCleanupReceipt(foreign, original.CleanupReceipt.Operation);
            CaptureRunInitializationRecoveryCompletedStep forged = ForgeCompletedStep(original, foreignReceipt, null, null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryExecutionResult(coordinator, batch, WithReplaced(good, 0, forged)));
            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignProvisionerIssuer_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeProvisioner provisioner = new FakeProvisioner();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), provisioner, new FakeWriter());
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout); // step 0 = provision

            CaptureRunInitializationRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunInitializationRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakeProvisioner foreign = new FakeProvisioner();
            CaptureRunRootProvisionReceipt foreignReceipt =
                new CaptureRunRootProvisionReceipt(foreign, original.ProvisionReceipt.Operation);
            CaptureRunInitializationRecoveryCompletedStep forged = ForgeCompletedStep(original, null, foreignReceipt, null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryExecutionResult(coordinator, batch, WithReplaced(good, 0, forged)));
            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        [Test]
        public void Result_DirectConstructor_ForeignWriterIssuer_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            FakeWriter writer = new FakeWriter();
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), writer);
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout); // step 0 = write

            CaptureRunInitializationRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunInitializationRecoveryCompletedStep original = good.GetCompletedStep(0);

            FakeWriter foreign = new FakeWriter();
            CaptureRunMarkerWriteReceipt foreignReceipt =
                new CaptureRunMarkerWriteReceipt(foreign, original.MarkerWriteReceipt.Operation);
            CaptureRunInitializationRecoveryCompletedStep forged = ForgeCompletedStep(original, null, null, foreignReceipt);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryExecutionResult(coordinator, batch, WithReplaced(good, 0, forged)));
            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }

        [Test]
        public void CompletedStep_DirectConstructor_InvalidReceipt_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // cleanup: matching operation, null issuer
            CaptureRunInitializationRecoveryPreparedStep cleanupPrepared = BuildBatch(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout).GetPreparedStep(0);
            CaptureRunInitializationRecoveryCleanupReceipt brokenCleanup =
                (CaptureRunInitializationRecoveryCleanupReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryCleanupReceipt));
            SetField(brokenCleanup, "_operation", cleanupPrepared.CleanupOperation);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCompletedStep(cleanupPrepared, brokenCleanup, null, null));

            // provision: matching operation, null issuer
            CaptureRunInitializationRecoveryPreparedStep provisionPrepared = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout).GetPreparedStep(0);
            CaptureRunRootProvisionReceipt brokenProvision =
                (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunRootProvisionReceipt));
            SetField(brokenProvision, "_operation", provisionPrepared.ProvisionOperation);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCompletedStep(provisionPrepared, null, brokenProvision, null));

            // write: matching operation, null issuer
            CaptureRunInitializationRecoveryPreparedStep writePrepared = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout).GetPreparedStep(0);
            CaptureRunMarkerWriteReceipt brokenWrite =
                (CaptureRunMarkerWriteReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunMarkerWriteReceipt));
            SetField(brokenWrite, "_operation", writePrepared.MarkerWriteOperation);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCompletedStep(writePrepared, null, null, brokenWrite));
        }

        [Test]
        public void ForgedBrokenReceipt_IsValidFalse_WithoutException()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator = MakeCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryExecutionBatch batch = BuildBatch(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout); // step 0 = provision

            CaptureRunInitializationRecoveryExecutionResult good = coordinator.Execute(batch);
            CaptureRunInitializationRecoveryCompletedStep original = good.GetCompletedStep(0);

            CaptureRunRootProvisionReceipt brokenReceipt =
                (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunRootProvisionReceipt));
            SetField(brokenReceipt, "_operation", original.ProvisionReceipt.Operation);
            CaptureRunInitializationRecoveryCompletedStep brokenStep = ForgeCompletedStep(original, null, brokenReceipt, null);

            Assert.That(brokenStep.IsValid, Is.False);

            CaptureRunInitializationRecoveryExecutionResult brokenResult =
                (CaptureRunInitializationRecoveryExecutionResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryExecutionResult));
            SetField(brokenResult, "_issuedBy", coordinator);
            SetField(brokenResult, "_batch", batch);
            SetField(brokenResult, "_completedSteps", WithReplaced(good, 0, brokenStep));
            Assert.That(brokenResult.IsValid, Is.False);
        }

        // ---- Shape ----

        [Test]
        public void StatusEnum_Contract()
        {
            Type type = typeof(CaptureRunInitializationRecoveryExecutionStatus);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            string[] names = Enum.GetNames(type);
            Assert.That(names, Is.EqualTo(new[] { "None", "StartFreshRequired", "InitializationReady", "PublicationRecoveryRequired", "RunRootCollision" }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(5));
            for (int i = 0; i < 5; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        [Test]
        public void Coordinator_Shape_ThreeReadonlyDeps_NotDisposable()
        {
            Type type = typeof(CaptureRunInitializationRecoveryExecutionCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void CompletedStep_Shape_FourReadonlyFields()
        {
            Type type = typeof(CaptureRunInitializationRecoveryCompletedStep);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(4));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        [Test]
        public void Result_Shape_ThreeReadonlyFields()
        {
            Type type = typeof(CaptureRunInitializationRecoveryExecutionResult);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryExecutionStatus.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryCompletedStep.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryExecutionResult.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryExecutionCoordinator.cs"
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
            }
        }

        // ---- Assertion helpers ----

        private static CaptureRunInitializationRecoveryCompletedStep ForgeCompletedStep(
            CaptureRunInitializationRecoveryCompletedStep template,
            CaptureRunInitializationRecoveryCleanupReceipt cleanupReceipt,
            CaptureRunRootProvisionReceipt provisionReceipt,
            CaptureRunMarkerWriteReceipt markerWriteReceipt)
        {
            CaptureRunInitializationRecoveryCompletedStep forged = (CaptureRunInitializationRecoveryCompletedStep)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryCompletedStep));
            SetField(forged, "_preparedStep", template.PreparedStep);
            SetField(forged, "_cleanupReceipt", cleanupReceipt);
            SetField(forged, "_provisionReceipt", provisionReceipt);
            SetField(forged, "_markerWriteReceipt", markerWriteReceipt);
            return forged;
        }

        private static CaptureRunInitializationRecoveryCompletedStep[] WithReplaced(
            CaptureRunInitializationRecoveryExecutionResult result,
            int index,
            CaptureRunInitializationRecoveryCompletedStep replacement)
        {
            CaptureRunInitializationRecoveryCompletedStep[] steps = new CaptureRunInitializationRecoveryCompletedStep[result.Count];
            for (int i = 0; i < result.Count; i++)
            {
                steps[i] = i == index ? replacement : result.GetCompletedStep(i);
            }

            return steps;
        }

        private static void AssertResultRejected(
            CaptureRunInitializationRecoveryExecutionCoordinator coordinator,
            CaptureRunInitializationRecoveryExecutionBatch batch,
            CaptureRunInitializationRecoveryCompletedStep[] completedSteps)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryExecutionResult(coordinator, batch, completedSteps));

            Assert.That(ex.ParamName, Is.EqualTo("completedSteps"));
        }
    }
}
