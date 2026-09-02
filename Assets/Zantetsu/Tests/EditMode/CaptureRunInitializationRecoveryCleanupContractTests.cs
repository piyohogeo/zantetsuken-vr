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
    public class CaptureRunInitializationRecoveryCleanupContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunRootRole NoneRole => CaptureRunRootRole.None;

        private static CaptureRunMarkerKind NoneKind => CaptureRunMarkerKind.None;

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
            bool limitExceeded = false,
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
                limitExceeded);
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

        private CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null,
            List<string> disposeLog = null)
        {
            return MakeSnapshot(staging, final, layout, disposeLog, out _);
        }

        private CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return MakeSnapshot(staging, final, layout, null, out owner);
        }

        private CaptureRunInitializationRecoveryInspectionSnapshot MakeSnapshot(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout,
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            layout = layout ?? MakeLayout();
            owner = MakeOwner(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            return new CaptureRunInitializationRecoveryInspectionSnapshot(new FakeInspector(), operation, staging, final);
        }

        private CaptureRunInitializationRecoveryActionPlan BuildPlan(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunRootLayout layout = null)
        {
            return CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(MakeSnapshot(staging, final, layout)));
        }

        private static CaptureRunInitializationRecoveryCleanupOperation MakeOp(
            CaptureRunInitializationRecoveryActionPlan plan,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            return new CaptureRunInitializationRecoveryCleanupOperation(plan, markerPaths, stepIndex);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryCleanupContractTests).Assembly.Location);
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
                throw new NotSupportedException("The cleanup contract must never call the inspector back.");
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public int CallCount { get; private set; }

            public CaptureRunInitializationRecoveryCleanupOperation LastOperation { get; private set; }

            public Exception ExceptionToThrow { get; set; }

            public Func<CaptureRunInitializationRecoveryCleanupOperation, CaptureRunInitializationRecoveryCleanupReceipt> ReceiptOverride { get; set; }

            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ReceiptOverride != null)
                {
                    return ReceiptOverride(operation);
                }

                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        // ---- Target path mapping ----

        [Test]
        public void TargetPath_TmpDeletion_FixedPaths()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRootObservation final = MakeObservation(Final, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(staging, final, layout);

            Assert.That(MakeOp(plan, markerPaths, 0).TargetPath, Is.EqualTo(markerPaths.StagingInitializationTemporaryPath));
            Assert.That(MakeOp(plan, markerPaths, 1).TargetPath, Is.EqualTo(markerPaths.StagingReadyTemporaryPath));
            Assert.That(MakeOp(plan, markerPaths, 2).TargetPath, Is.EqualTo(markerPaths.FinalInitializationTemporaryPath));
            Assert.That(MakeOp(plan, markerPaths, 3).TargetPath, Is.EqualTo(markerPaths.FinalReadyTemporaryPath));
        }

        [Test]
        public void TargetPath_RootRemoval_FixedPaths()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRootObservation final = MakeObservation(Final, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(staging, final, layout);

            Assert.That(MakeOp(plan, markerPaths, 4).TargetPath, Is.EqualTo(layout.FinalRunRoot));
            Assert.That(MakeOp(plan, markerPaths, 5).TargetPath, Is.EqualTo(layout.StagingRunRoot));
        }

        [Test]
        public void Operation_ForwardsActionRoleKind()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(staging, final, layout);

            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            Assert.That(op.Action, Is.EqualTo(CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary));
            Assert.That(op.RootRole, Is.EqualTo(Staging));
            Assert.That(op.MarkerKind, Is.EqualTo(InitKind));
            Assert.That(op.StepIndex, Is.EqualTo(0));
            Assert.That(op.ActionPlan, Is.SameAs(plan));
        }

        [Test]
        public void MarkerPathSet_IsValid_TrueForValid_FalseForCorrupted()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet valid = new CaptureRunMarkerPathSet(layout);
            Assert.That(valid.IsValid, Is.True);

            CaptureRunMarkerPathSet corrupted = ForgePathSet(valid, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);
            Assert.That(corrupted.IsValid, Is.False);
        }

        // ---- Correlation ----

        [Test]
        public void Operation_CorrelatesPlanPathSetAndLease()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            Assert.That(op.ActionPlan, Is.SameAs(plan));
            Assert.That(op.MarkerPaths, Is.SameAs(markerPaths));
            Assert.That(op.RootLayout, Is.SameAs(layout));
            Assert.That(op.LockIdentityEvidence, Is.SameAs(snapshot.Operation.LockIdentityEvidence));
            Assert.That(op.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(op.IsValid, Is.True);
        }

        // ---- Rejections ----

        [Test]
        public void Operation_NullPlan_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(null, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_NullMarkerPaths_Rejected()
        {
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(MakeAbsent(Staging), MakeAbsent(Final));

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, null, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void Operation_InvalidPlan_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_DifferentPathSet_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootLayout otherLayout = MakeLayout(2);
            CaptureRunMarkerPathSet otherPaths = new CaptureRunMarkerPathSet(otherLayout);

            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, otherPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void Operation_CorruptedTmpPath_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            string[] tmpFields =
            {
                "_stagingInitializationTemporaryPath",
                "_stagingReadyTemporaryPath",
                "_finalInitializationTemporaryPath",
                "_finalReadyTemporaryPath"
            };
            string[] canonicalTargets =
            {
                source.StagingInitializationPath,
                source.StagingReadyPath,
                source.FinalInitializationPath,
                source.FinalReadyPath
            };
            string[] otherTmpTargets =
            {
                source.StagingReadyTemporaryPath,
                source.StagingInitializationTemporaryPath,
                source.FinalReadyTemporaryPath,
                source.FinalInitializationTemporaryPath
            };

            for (int i = 0; i < tmpFields.Length; i++)
            {
                AssertPathSetRejected(plan, ForgePathSet(source, tmpFields[i], layout.FinalRunRoot));
                AssertPathSetRejected(plan, ForgePathSet(source, tmpFields[i], otherTmpTargets[i]));
                AssertPathSetRejected(plan, ForgePathSet(source, tmpFields[i], canonicalTargets[i]));
            }
        }

        [Test]
        public void Operation_CorruptedPathSet_OutOfRangeIndex_Priority()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, corrupted, plan.Count));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Operation_CorruptedPathSet_NonCleanupStep_Priority()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout); // step 0 = StartFreshInitialization

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, corrupted, 0));
            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        [Test]
        public void Operation_CorruptedPathSet_ReleasedLease_Priority()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingInitializationTemporaryPath", layout.FinalRunRoot);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            Assert.That(owner.IsCreated, Is.True);
            owner.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, corrupted, 0));
            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Operation_OutOfRangeIndex_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            foreach (int index in new[] { -1, plan.Count, plan.Count + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunInitializationRecoveryCleanupOperation(plan, markerPaths, index));

                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Operation_NonCleanupActions_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // StartFreshInitialization
            AssertNonCleanupRejected(BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout));

            // InitializationReady
            AssertNonCleanupRejected(BuildPlan(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout));

            // ContinuePublicationRecovery
            AssertNonCleanupRejected(BuildPlan(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding),
                layout));

            // StopRunRootCollision
            AssertNonCleanupRejected(BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final),
                layout));

            // ProvisionRoot (index 0) and WriteMarker (index 1)
            CaptureRunInitializationRecoveryActionPlan peerPlan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);
            AssertNonCleanupRejected(peerPlan, 0);
            AssertNonCleanupRejected(peerPlan, 1);
        }

        // ---- IsValid exception safety ----

        [Test]
        public void Operation_ReleasedLease_IsInvalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            Assert.That(op.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();

            Assert.That(op.IsValid, Is.False);
        }

        [Test]
        public void Operation_IsValid_False_WhenForged()
        {
            CaptureRunInitializationRecoveryCleanupOperation empty = (CaptureRunInitializationRecoveryCleanupOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryCleanupOperation));
            Assert.That(empty.IsValid, Is.False);

            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunMarkerPathSet brokenPaths = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));

            CaptureRunInitializationRecoveryCleanupOperation forged = (CaptureRunInitializationRecoveryCleanupOperation)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryCleanupOperation));
            SetField(forged, "_actionPlan", plan);
            SetField(forged, "_markerPaths", brokenPaths);
            SetField(forged, "_stepIndex", 0);
            Assert.That(forged.IsValid, Is.False);
        }

        // ---- Receipt ----

        [Test]
        public void Receipt_HoldsIssuerAndOperationByReference()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            CaptureRunInitializationRecoveryCleanupReceipt receipt = new CaptureRunInitializationRecoveryCleanupReceipt(backend, op);

            Assert.That(receipt.IssuedBy, Is.SameAs(backend));
            Assert.That(receipt.Operation, Is.SameAs(op));
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.ActionPlan, Is.SameAs(plan));
            Assert.That(receipt.TargetPath, Is.EqualTo(op.TargetPath));
            Assert.That(receipt.TestRunId, Is.EqualTo(layout.TestRunId));
        }

        [Test]
        public void Receipt_NullIssuer_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryCleanupReceipt(null, op));

            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void Receipt_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationRecoveryCleanupReceipt(new FakeCleanupBackend(), null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_InvalidOperation_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            Assert.That(owner.IsCreated, Is.True);
            owner.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupReceipt(new FakeCleanupBackend(), op));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_IsIssuedFor_TrueForMatchingBackendAndOperation()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            CaptureRunInitializationRecoveryCleanupReceipt receipt = backend.Execute(op);

            Assert.That(receipt.IsIssuedFor(backend, op), Is.True);
            Assert.That(receipt.IsIssuedFor(new FakeCleanupBackend(), op), Is.False);
            Assert.That(receipt.IsIssuedFor(backend, MakeOp(plan, markerPaths, 0)), Is.False);
        }

        [Test]
        public void FakeBackend_ForeignIssuerReceipt_Detected()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend();
            FakeCleanupBackend foreign = new FakeCleanupBackend();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            backend.ReceiptOverride = _ => new CaptureRunInitializationRecoveryCleanupReceipt(foreign, _);
            CaptureRunInitializationRecoveryCleanupReceipt receipt = backend.Execute(op);

            Assert.That(receipt.IsIssuedFor(backend, op), Is.False);
        }

        [Test]
        public void FakeBackend_DifferentOperationReceipt_Detected()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op1 = MakeOp(plan, markerPaths, 0);
            CaptureRunInitializationRecoveryCleanupOperation op2 = MakeOp(plan, markerPaths, 1);

            backend.ReceiptOverride = _ => new CaptureRunInitializationRecoveryCleanupReceipt(backend, op2);
            CaptureRunInitializationRecoveryCleanupReceipt receipt = backend.Execute(op1);

            Assert.That(receipt.IsIssuedFor(backend, op1), Is.False);
        }

        [Test]
        public void FakeBackend_NullReceipt_Detected()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            backend.ReceiptOverride = _ => null;
            CaptureRunInitializationRecoveryCleanupReceipt receipt = backend.Execute(op);

            Assert.That(receipt, Is.Null, "A backend must never return a null receipt.");
        }

        // ---- Fake backend ----

        [Test]
        public void FakeBackend_OneCall_ReturnsReceiptForSameOperation()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            CaptureRunInitializationRecoveryCleanupReceipt receipt = backend.Execute(op);

            Assert.That(backend.CallCount, Is.EqualTo(1));
            Assert.That(backend.LastOperation, Is.SameAs(op));
            Assert.That(receipt.Operation, Is.SameAs(op));
            Assert.That(receipt.IssuedBy, Is.SameAs(backend));
        }

        [Test]
        public void FakeBackend_Exception_NotTransformedOrRetried()
        {
            FakeCleanupBackend backend = new FakeCleanupBackend { ExceptionToThrow = new IOException("boom") };
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);

            IOException ex = Assert.Throws<IOException>(() => backend.Execute(op));

            Assert.That(ex.Message, Is.EqualTo("boom"));
            Assert.That(backend.CallCount, Is.EqualTo(1));
            Assert.That(backend.LastOperation, Is.SameAs(op));
        }

        // ---- Lease non-dispose ----

        [Test]
        public void OperationAndReceipt_DoNotDisposeOwner()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));
            CaptureRunInitializationRecoveryCleanupOperation op = MakeOp(plan, markerPaths, 0);
            CaptureRunInitializationRecoveryCleanupReceipt receipt = new CaptureRunInitializationRecoveryCleanupReceipt(new FakeCleanupBackend(), op);

            Assert.That(disposeLog, Is.Empty, "The operation and receipt must not dispose the owner.");
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(op.LockIdentityEvidence, Is.SameAs(snapshot.Operation.LockIdentityEvidence));
            Assert.That(receipt.LockIdentityEvidence, Is.SameAs(snapshot.Operation.LockIdentityEvidence));
        }

        // ---- Shape ----

        [Test]
        public void Operation_Shape_ThreeReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(CaptureRunInitializationRecoveryCleanupOperation);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));

            int planFields = 0;
            int pathSetFields = 0;
            int intFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationRecoveryActionPlan)) planFields++;
                else if (field.FieldType == typeof(CaptureRunMarkerPathSet)) pathSetFields++;
                else if (field.FieldType == typeof(int)) intFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(planFields, Is.EqualTo(1));
            Assert.That(pathSetFields, Is.EqualTo(1));
            Assert.That(intFields, Is.EqualTo(1));

            Assert.That(
                type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                              || p.PropertyType == typeof(CaptureRunLockLease)),
                Is.False,
                "The operation must not expose the ownership lease or raw lock lease.");
        }

        [Test]
        public void Receipt_Shape_TwoReadonlyFields()
        {
            Type type = typeof(CaptureRunInitializationRecoveryCleanupReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));

            int backendFields = 0;
            int operationFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(ICaptureRunInitializationRecoveryCleanupBackend)) backendFields++;
                else if (field.FieldType == typeof(CaptureRunInitializationRecoveryCleanupOperation)) operationFields++;
                else Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
            }

            Assert.That(backendFields, Is.EqualTo(1));
            Assert.That(operationFields, Is.EqualTo(1));

            Assert.That(
                type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(p => p.PropertyType == typeof(CaptureRunInitializationSessionOwnershipLease)
                              || p.PropertyType == typeof(CaptureRunLockLease)),
                Is.False,
                "The receipt must not expose the ownership lease or raw lock lease.");
        }

        [Test]
        public void Backend_IsInterface_SingleMethod()
        {
            Type type = typeof(ICaptureRunInitializationRecoveryCleanupBackend);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Has.Length.EqualTo(1));
        }

        [Test]
        public void NoMutableStaticState()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunInitializationRecoveryCleanupOperation),
                typeof(CaptureRunInitializationRecoveryCleanupReceipt)
            })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
                }
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryCleanupOperation.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunInitializationRecoveryCleanupBackend.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryCleanupReceipt.cs"
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
            }
        }

        [Test]
        public void BackendXml_MentionsNonRecursiveFlushAndReinspect()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunInitializationRecoveryCleanupBackend.cs"));

            Assert.That(source, Does.Contain("non-recursively"));
            Assert.That(source, Does.Contain("durably flushes"));
            Assert.That(source, Does.Contain("re-inspects"));
        }

        // ---- Assertion helpers ----

        private static void AssertNonCleanupRejected(CaptureRunInitializationRecoveryActionPlan plan, int stepIndex = 0)
        {
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(plan.RootLayout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, markerPaths, stepIndex));

            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }

        private static CaptureRunMarkerPathSet ForgePathSet(CaptureRunMarkerPathSet source, string fieldName, string corruptedValue)
        {
            CaptureRunMarkerPathSet forged = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));
            SetField(forged, "_rootLayout", source.RootLayout);
            SetField(forged, "_stagingInitializationTemporaryPath", source.StagingInitializationTemporaryPath);
            SetField(forged, "_stagingInitializationPath", source.StagingInitializationPath);
            SetField(forged, "_stagingReadyTemporaryPath", source.StagingReadyTemporaryPath);
            SetField(forged, "_stagingReadyPath", source.StagingReadyPath);
            SetField(forged, "_finalInitializationTemporaryPath", source.FinalInitializationTemporaryPath);
            SetField(forged, "_finalInitializationPath", source.FinalInitializationPath);
            SetField(forged, "_finalReadyTemporaryPath", source.FinalReadyTemporaryPath);
            SetField(forged, "_finalReadyPath", source.FinalReadyPath);
            SetField(forged, fieldName, corruptedValue);
            return forged;
        }

        private static void AssertPathSetRejected(CaptureRunInitializationRecoveryActionPlan plan, CaptureRunMarkerPathSet markerPaths)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryCleanupOperation(plan, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }
    }
}
