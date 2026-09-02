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
    public class CaptureRunInitializationRecoveryMarkerWriteOperationFactoryTests
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

        private static CaptureRunInitializationRecoveryAction WriteMarker => CaptureRunInitializationRecoveryAction.WriteMarker;

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

        private static CaptureRunInitializationRecoveryStep S(
            CaptureRunInitializationRecoveryAction action,
            CaptureRunRootRole role = CaptureRunRootRole.None,
            CaptureRunMarkerKind kind = CaptureRunMarkerKind.None)
        {
            return new CaptureRunInitializationRecoveryStep(action, role, kind);
        }

        private static CaptureRunInitializationRecoveryDecision ForgeDecision(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot,
            CaptureRunInitializationRecoveryDisposition disposition,
            CaptureRunMarkerBinding expectedBinding)
        {
            CaptureRunInitializationRecoveryDecision decision = (CaptureRunInitializationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryDecision));
            SetField(decision, "_snapshot", snapshot);
            SetField(decision, "_disposition", disposition);
            SetField(decision, "_expectedBinding", expectedBinding);
            return decision;
        }

        private static CaptureRunInitializationRecoveryActionPlan ForgePlan(
            CaptureRunInitializationRecoveryDecision decision,
            params CaptureRunInitializationRecoveryStep[] steps)
        {
            CaptureRunInitializationRecoveryActionPlan plan = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));
            SetField(plan, "_decision", decision);
            SetField(plan, "_steps", steps);
            return plan;
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryMarkerWriteOperationFactoryTests).Assembly.Location);
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
                throw new NotSupportedException("The write factory must never call the inspector back.");
            }
        }

        // ---- Four combinations ----

        [Test]
        public void Create_FourCombos_MarkerPathBytesExact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // Staging source: [ProvisionRoot(Final), Write(Final, Init), Write(Staging, Ready), Write(Final, Ready)]
            CaptureRunInitializationRecoveryActionPlan planA = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);

            AssertWrite(planA, markerPaths, 1, Final, InitKind,
                markerPaths.FinalInitializationTemporaryPath, markerPaths.FinalInitializationPath,
                CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization));
            AssertWrite(planA, markerPaths, 2, Staging, ReadyKind,
                markerPaths.StagingReadyTemporaryPath, markerPaths.StagingReadyPath,
                CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady));
            AssertWrite(planA, markerPaths, 3, Final, ReadyKind,
                markerPaths.FinalReadyTemporaryPath, markerPaths.FinalReadyPath,
                CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady));

            // Final source: [ProvisionRoot(Staging), Write(Staging, Init), Write(Staging, Ready), Write(Final, Ready)]
            CaptureRunInitializationRecoveryActionPlan planB = BuildPlan(
                MakeAbsent(Staging),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            AssertWrite(planB, markerPaths, 1, Staging, InitKind,
                markerPaths.StagingInitializationTemporaryPath, markerPaths.StagingInitializationPath,
                CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization));
        }

        // ---- Rejections ----

        [Test]
        public void Create_NullPlan_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(null, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Create_NullMarkerPaths_Rejected()
        {
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(MakeAbsent(Staging), MakeAbsent(Final));

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, null, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void Create_InvalidPlan_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = (CaptureRunInitializationRecoveryActionPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryActionPlan));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Create_DifferentRootLayout_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootLayout otherLayout = MakeLayout(2);
            CaptureRunMarkerPathSet otherPaths = new CaptureRunMarkerPathSet(otherLayout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true),
                MakeAbsent(Final),
                layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, otherPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void Create_CorruptedPathSet_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet source = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerPathSet corrupted = ForgePathSet(source, "_stagingReadyTemporaryPath", layout.FinalRunRoot);

            // CompleteReadyMarkers: [Write(Staging, Ready), Write(Final, Ready)]
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, corrupted, 0));

            Assert.That(ex.ParamName, Is.EqualTo("markerPaths"));
        }

        [Test]
        public void Create_OutOfRangeIndex_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            foreach (int index in new[] { -1, plan.Count, plan.Count + 1 })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, index));

                Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
            }
        }

        [Test]
        public void Create_NonWriteMarkerActions_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            // DeleteMarkerTemporary (cleanup)
            AssertStepRejected(BuildPlan(MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), layout), markerPaths, 0);

            // ProvisionRoot
            AssertStepRejected(BuildPlan(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout), markerPaths, 0);

            // StartFreshInitialization
            AssertStepRejected(BuildPlan(MakeAbsent(Staging), MakeAbsent(Final), layout), markerPaths, 0);

            // InitializationReady
            AssertStepRejected(BuildPlan(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout), markerPaths, 0);

            // ContinuePublicationRecovery
            AssertStepRejected(BuildPlan(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true),
                MakeFullyCanonical(Final, binding),
                layout), markerPaths, 0);

            // StopRunRootCollision
            AssertStepRejected(BuildPlan(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final),
                layout), markerPaths, 0);
        }

        [Test]
        public void Create_ReleasedLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRootObservation staging = MakeCanonicalInit(Staging, binding.StagingInitialization);
            CaptureRunInitializationRootObservation final = MakeCanonicalInit(Final, binding.FinalInitialization);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            Assert.That(owner.IsCreated, Is.True);
            owner.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Create_ExpectedBindingMissing_FailsClosed()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);
            CaptureRunInitializationRecoveryDecision decision = ForgeDecision(
                snapshot, CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers, null);
            CaptureRunInitializationRecoveryActionPlan plan = ForgePlan(
                decision, S(WriteMarker, Staging, ReadyKind), S(WriteMarker, Final, ReadyKind));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        [Test]
        public void Create_ExpectedBindingMismatch_FailsClosed()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootLayout otherLayout = MakeLayout(2);
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerBinding foreignBinding = MakeBinding(otherLayout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);

            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);
            CaptureRunInitializationRecoveryDecision decision = ForgeDecision(
                snapshot, CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers, foreignBinding);
            CaptureRunInitializationRecoveryActionPlan plan = ForgePlan(
                decision, S(WriteMarker, Staging, ReadyKind), S(WriteMarker, Final, ReadyKind));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0));

            Assert.That(ex.ParamName, Is.EqualTo("actionPlan"));
        }

        // ---- Canonical marker non-overwrite ----

        [Test]
        public void Create_SourceInitAndCanonicalReady_NeverGenerated()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            // CompleteMissingPeer (staging source): no Write(Staging, Init)
            CaptureRunInitializationRecoveryActionPlan peerPlan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeAbsent(Final),
                layout);
            foreach (int i in Enumerable.Range(0, peerPlan.Count))
            {
                CaptureRunInitializationRecoveryStep step = peerPlan.GetStep(i);
                bool writesSourceInit = step.Action == WriteMarker && step.RootRole == Staging && step.MarkerKind == InitKind;
                Assert.That(writesSourceInit, Is.False, "The source initialization marker must never be written.");
            }

            // CompleteReadyMarkers (staging ready present): only Write(Final, Ready)
            CaptureRunInitializationRecoveryActionPlan readyPlan = BuildPlan(
                MakeFullyCanonical(Staging, binding),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);
            foreach (int i in Enumerable.Range(0, readyPlan.Count))
            {
                CaptureRunInitializationRecoveryStep step = readyPlan.GetStep(i);
                bool writesStagingReady = step.Action == WriteMarker && step.RootRole == Staging && step.MarkerKind == ReadyKind;
                Assert.That(writesStagingReady, Is.False, "The canonical staging ready marker must never be rewritten.");
            }

            // AlreadyInitialized: no WriteMarker at all
            CaptureRunInitializationRecoveryActionPlan donePlan = BuildPlan(
                MakeFullyCanonical(Staging, binding),
                MakeFullyCanonical(Final, binding),
                layout);
            foreach (int i in Enumerable.Range(0, donePlan.Count))
            {
                Assert.That(donePlan.GetStep(i).Action, Is.Not.EqualTo(WriteMarker));
            }
        }

        // ---- Ownership / defensive copy / independence ----

        [Test]
        public void Create_ConsecutiveCalls_DoNotShareOperationOrBytes()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRecoveryActionPlan plan = BuildPlan(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, binding.FinalInitialization),
                layout);

            CaptureRunMarkerWriteOperation op1 = CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0);
            CaptureRunMarkerWriteOperation op2 = CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0);

            Assert.That(ReferenceEquals(op1, op2), Is.False);

            byte[] expected = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
            byte[] copy1 = op1.GetCanonicalBytes();
            Assert.That(copy1, Is.EqualTo(expected));

            copy1[0] ^= 0xFF;
            Assert.That(op1.GetCanonicalBytes(), Is.EqualTo(expected), "GetCanonicalBytes must return a fresh defensive copy.");
            Assert.That(op2.GetCanonicalBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void Create_DoesNotDisposeOwnerOrMutateInputs()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunInitializationRootObservation staging = MakeCanonicalInit(Staging, binding.StagingInitialization);
            CaptureRunInitializationRootObservation final = MakeCanonicalInit(Final, binding.FinalInitialization);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, disposeLog, out CaptureRunInitializationSessionOwnershipLease owner);
            CaptureRunInitializationRecoveryActionPlan plan = CaptureRunInitializationRecoveryActionPlanBuilder.Build(
                CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            CaptureRunMarkerWriteOperation op = CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, 0);

            Assert.That(disposeLog, Is.Empty, "The write factory must not dispose the owner.");
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(snapshot.Staging, Is.SameAs(staging));
            Assert.That(snapshot.Final, Is.SameAs(final));
            Assert.That(staging.InitializationMarker, Is.SameAs(binding.StagingInitialization));
            Assert.That(final.InitializationMarker, Is.SameAs(binding.FinalInitialization));
            Assert.That(op.GetCanonicalBytes(), Is.EqualTo(CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady)));
        }

        // ---- Shape ----

        [Test]
        public void Factory_Shape_NoFields_NoPublicApi()
        {
            Type type = typeof(CaptureRunInitializationRecoveryMarkerWriteOperationFactory);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), Is.Empty);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        // ---- Source inspection ----

        [Test]
        public void FactorySource_DelegatesToCodecs_NoForbiddenDependencies()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryMarkerWriteOperationFactory.cs"));

            Assert.That(source, Does.Contain("CaptureRunInitializationMarkerCodec.SerializeCanonical"));
            Assert.That(source, Does.Contain("CaptureRunReadyMarkerCodec.SerializeCanonical"));
            Assert.That(source, Does.Not.Contain("catch"));
            Assert.That(source, Does.Not.Contain("try"));

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

            Assert.That(source, Does.Not.Contain("ICaptureRunRootProvisioner"));
            Assert.That(source, Does.Not.Contain("ICaptureRunMarkerAtomicWriter"));
            Assert.That(source, Does.Not.Contain("ICaptureRunInitializationRecoveryCleanupBackend"));
        }

        // ---- Assertion helpers ----

        private static void AssertWrite(
            CaptureRunInitializationRecoveryActionPlan plan,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunRootRole role,
            CaptureRunMarkerKind kind,
            string temporaryPath,
            string finalPath,
            byte[] expectedBytes)
        {
            CaptureRunMarkerWriteOperation op = CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, stepIndex);

            Assert.That(op.RootRole, Is.EqualTo(role));
            Assert.That(op.MarkerKind, Is.EqualTo(kind));
            Assert.That(op.TemporaryPath, Is.EqualTo(temporaryPath));
            Assert.That(op.FinalPath, Is.EqualTo(finalPath));
            Assert.That(op.GetCanonicalBytes(), Is.EqualTo(expectedBytes));
            Assert.That(op.ByteCount, Is.EqualTo(expectedBytes.Length));
            Assert.That(op.ByteCount, Is.GreaterThan(0));
            Assert.That(op.ByteCount, Is.LessThanOrEqualTo(4 * 1024));
        }

        private static void AssertStepRejected(
            CaptureRunInitializationRecoveryActionPlan plan,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryMarkerWriteOperationFactory.Create(plan, markerPaths, stepIndex));

            Assert.That(ex.ParamName, Is.EqualTo("stepIndex"));
        }
    }
}
