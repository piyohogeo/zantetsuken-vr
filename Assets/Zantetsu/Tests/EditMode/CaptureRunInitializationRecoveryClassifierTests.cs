using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationRecoveryClassifierTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string OtherInitId = "fedcba9876543210fedcba9876543210";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunMarkerObservationStatus Invalid => CaptureRunMarkerObservationStatus.Invalid;

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

        private static CaptureRunMarkerBinding MakeBinding(CaptureRunRootLayout layout, string initId = InitId)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                initId,
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

        private static CaptureRunInitializationRootObservation MakeEmpty(CaptureRunRootRole role)
        {
            return MakeObservation(role, true, Absent, null, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeCanonicalInit(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker marker)
        {
            return MakeObservation(role, true, Canonical, marker, Absent, null);
        }

        private static CaptureRunInitializationRootObservation MakeCanonicalReady(
            CaptureRunRootRole role,
            CaptureRunReadyMarker marker)
        {
            return MakeObservation(role, true, Absent, null, Canonical, marker);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(
            CaptureRunRootRole role,
            CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunInitializationMarker ChangeInit(
            CaptureRunInitializationMarker source,
            long? testRunId = null,
            string runInitializationId = null,
            CaptureRunRootRole? rootRole = null,
            string stagingRunRootSha256 = null,
            string finalRunRootSha256 = null)
        {
            return new CaptureRunInitializationMarker(
                testRunId ?? source.TestRunId,
                runInitializationId ?? source.RunInitializationId,
                rootRole ?? source.RootRole,
                stagingRunRootSha256 ?? source.StagingRunRootSha256,
                finalRunRootSha256 ?? source.FinalRunRootSha256);
        }

        private static CaptureRunReadyMarker ChangeReady(
            CaptureRunReadyMarker source,
            long? testRunId = null,
            string runInitializationId = null,
            string stagingInitSha256 = null,
            string finalInitSha256 = null)
        {
            return new CaptureRunReadyMarker(
                testRunId ?? source.TestRunId,
                runInitializationId ?? source.RunInitializationId,
                stagingInitSha256 ?? source.StagingInitSha256,
                finalInitSha256 ?? source.FinalInitSha256);
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
            layout = layout ?? MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwner(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            return new CaptureRunInitializationRecoveryInspectionSnapshot(new FakeInspector(), operation, staging, final);
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationRecoveryClassifierTests).Assembly.Location);
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
                throw new NotSupportedException("The classifier must never call the inspector back.");
            }
        }

        // ---- Enum ----

        [Test]
        public void Enum_Contract()
        {
            Type type = typeof(CaptureRunInitializationRecoveryDisposition);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));

            string[] names = Enum.GetNames(type);
            Assert.That(names, Is.EqualTo(new[]
            {
                "None",
                "StartFresh",
                "CleanupTemporaryAndStartFresh",
                "CompleteMissingPeerInitialization",
                "CompleteReadyMarkers",
                "AlreadyInitialized",
                "RequiresPublicationRecovery",
                "RunRootCollision"
            }));

            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(8));
            for (int i = 0; i < 8; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        // ---- Classifier null / invalid ----

        [Test]
        public void Classify_NullSnapshot_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationRecoveryClassifier.Classify(null));

            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        [Test]
        public void Classify_InvalidSnapshot_Rejected()
        {
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = (CaptureRunInitializationRecoveryInspectionSnapshot)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryInspectionSnapshot));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationRecoveryClassifier.Classify(snapshot));

            Assert.That(ex.ParamName, Is.EqualTo("snapshot"));
        }

        // ---- Fresh / cleanup ----

        [Test]
        public void Classify_BothMissing_StartFresh()
        {
            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(MakeAbsent(Staging), MakeAbsent(Final)));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.StartFresh));
            Assert.That(decision.ExpectedBinding, Is.Null);
            Assert.That(decision.RunInitializationId, Is.Null);
            Assert.That(decision.IsValid, Is.True);
        }

        [Test]
        public void Classify_StagingMissingFinalEmpty_CleanupTemporaryAndStartFresh()
        {
            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(MakeAbsent(Staging), MakeEmpty(Final)));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh));
            Assert.That(decision.ExpectedBinding, Is.Null);
            Assert.That(decision.RunInitializationId, Is.Null);
        }

        [Test]
        public void Classify_BothTmpOnly_CleanupTemporaryAndStartFresh()
        {
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRootObservation final = MakeObservation(Final, true, Absent, null, Absent, null, hasInitTmp: true);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(staging, final));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh));
        }

        // ---- Zero init collision ----

        [Test]
        public void Classify_NoInit_ReadyPresent_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeCanonicalReady(Staging, binding.StagingReady);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            AssertCollision(MakeSnapshot(staging, final, layout));
        }

        [Test]
        public void Classify_NoInit_NonMarker_Collision()
        {
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            AssertCollision(MakeSnapshot(staging, final));
        }

        // ---- One-sided initialization ----

        [Test]
        public void Classify_StagingInitOnly_FinalAbsent_CompleteMissingPeerInitialization()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeCanonicalInit(Staging, binding.StagingInitialization);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(staging, final, layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization));
            Assert.That(decision.ExpectedBinding, Is.Not.Null);
            Assert.That(decision.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Classify_FinalInitOnly_StagingTmpOnly_CompleteMissingPeerInitialization()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true);
            CaptureRunInitializationRootObservation final = MakeCanonicalInit(Final, binding.FinalInitialization);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(staging, final, layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization));
            Assert.That(decision.ExpectedBinding, Is.Not.Null);
            Assert.That(decision.RunInitializationId, Is.EqualTo(InitId));
        }

        // ---- Source init mismatch (one-sided) ----

        [Test]
        public void Classify_SourceInit_TestRunIdMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker bad = ChangeInit(binding.StagingInitialization, testRunId: 999);

            AssertCollision(MakeSnapshot(MakeCanonicalInit(Staging, bad), MakeAbsent(Final), layout));
        }

        [Test]
        public void Classify_SourceInit_RoleMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker bad = ChangeInit(binding.StagingInitialization, rootRole: Final);

            AssertCollision(MakeSnapshot(MakeCanonicalInit(Staging, bad), MakeAbsent(Final), layout));
        }

        [Test]
        public void Classify_SourceInit_StagingRootHashMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker bad = ChangeInit(binding.StagingInitialization, stagingRunRootSha256: StagingHash);

            AssertCollision(MakeSnapshot(MakeCanonicalInit(Staging, bad), MakeAbsent(Final), layout));
        }

        [Test]
        public void Classify_SourceInit_FinalRootHashMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker bad = ChangeInit(binding.StagingInitialization, finalRunRootSha256: FinalHash);

            AssertCollision(MakeSnapshot(MakeCanonicalInit(Staging, bad), MakeAbsent(Final), layout));
        }

        // ---- Both init property mismatch ----

        [Test]
        public void Classify_BothInit_FinalTestRunIdMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker badFinal = ChangeInit(binding.FinalInitialization, testRunId: 999);

            AssertCollision(MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, badFinal),
                layout));
        }

        [Test]
        public void Classify_BothInit_FinalRoleMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker badFinal = ChangeInit(binding.FinalInitialization, rootRole: Staging);

            AssertCollision(MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, badFinal),
                layout));
        }

        [Test]
        public void Classify_BothInit_FinalRunInitializationIdMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker badFinal = ChangeInit(binding.FinalInitialization, runInitializationId: OtherInitId);

            AssertCollision(MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, badFinal),
                layout));
        }

        [Test]
        public void Classify_BothInit_FinalStagingRootHashMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker badFinal = ChangeInit(binding.FinalInitialization, stagingRunRootSha256: StagingHash);

            AssertCollision(MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, badFinal),
                layout));
        }

        [Test]
        public void Classify_BothInit_FinalFinalRootHashMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationMarker badFinal = ChangeInit(binding.FinalInitialization, finalRunRootSha256: FinalHash);

            AssertCollision(MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization),
                MakeCanonicalInit(Final, badFinal),
                layout));
        }

        // ---- Ready property mismatch ----

        [Test]
        public void Classify_StagingReady_TestRunIdMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunReadyMarker badReady = ChangeReady(binding.StagingReady, testRunId: 999);

            AssertCollision(MakeSnapshot(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, badReady),
                MakeFullyCanonical(Final, binding),
                layout));
        }

        [Test]
        public void Classify_StagingReady_RunInitializationIdMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunReadyMarker badReady = ChangeReady(binding.StagingReady, runInitializationId: OtherInitId);

            AssertCollision(MakeSnapshot(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, badReady),
                MakeFullyCanonical(Final, binding),
                layout));
        }

        [Test]
        public void Classify_StagingReady_StagingInitShaMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunReadyMarker badReady = ChangeReady(binding.StagingReady, stagingInitSha256: StagingHash);

            AssertCollision(MakeSnapshot(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, badReady),
                MakeFullyCanonical(Final, binding),
                layout));
        }

        [Test]
        public void Classify_StagingReady_FinalInitShaMismatch_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunReadyMarker badReady = ChangeReady(binding.StagingReady, finalInitSha256: FinalHash);

            AssertCollision(MakeSnapshot(
                MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, badReady),
                MakeFullyCanonical(Final, binding),
                layout));
        }

        // ---- Ready completion dispositions ----

        [Test]
        public void Classify_BothInit_NoReady_CompleteReadyMarkers()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(
                    MakeCanonicalInit(Staging, binding.StagingInitialization),
                    MakeCanonicalInit(Final, binding.FinalInitialization),
                    layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers));
            Assert.That(decision.ExpectedBinding, Is.Not.Null);
        }

        [Test]
        public void Classify_BothInit_OneReady_CompleteReadyMarkers()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(
                    MakeFullyCanonical(Staging, binding),
                    MakeCanonicalInit(Final, binding.FinalInitialization),
                    layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers));
        }

        [Test]
        public void Classify_BothInit_BothReady_AlreadyInitialized()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(
                    MakeFullyCanonical(Staging, binding),
                    MakeFullyCanonical(Final, binding),
                    layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.AlreadyInitialized));
            Assert.That(decision.ExpectedBinding, Is.Not.Null);
        }

        [Test]
        public void Classify_CompleteMarkers_NonMarker_RequiresPublicationRecovery()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(staging, final, layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery));
            Assert.That(decision.ExpectedBinding, Is.Not.Null);
            Assert.That(decision.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Classify_ReadyIncomplete_NonMarker_Collision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Absent, null, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeCanonicalInit(Final, binding.FinalInitialization);

            AssertCollision(MakeSnapshot(staging, final, layout));
        }

        // ---- Priority collision ----

        [Test]
        public void Classify_InvalidInitStatus_PriorityCollision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Invalid, null, Canonical, binding.StagingReady);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            AssertCollision(MakeSnapshot(staging, final, layout));
        }

        [Test]
        public void Classify_InvalidReadyStatus_PriorityCollision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Invalid, null);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            AssertCollision(MakeSnapshot(staging, final, layout));
        }

        [Test]
        public void Classify_UnknownEntry_PriorityCollision()
        {
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            AssertCollision(MakeSnapshot(staging, final));
        }

        [Test]
        public void Classify_EntryLimitExceeded_PriorityCollision()
        {
            CaptureRunInitializationRootObservation staging = MakeObservation(Staging, true, Absent, null, Absent, null, limitExceeded: true);
            CaptureRunInitializationRootObservation final = MakeAbsent(Final);

            AssertCollision(MakeSnapshot(staging, final));
        }

        // ---- Tmp invariance ----

        [Test]
        public void Classify_TmpPresence_DoesNotChangeCanonicalClassification()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasInitTmp: true, hasReadyTmp: true);
            CaptureRunInitializationRootObservation final = MakeObservation(
                Final, true, Canonical, binding.FinalInitialization, Canonical, binding.FinalReady, hasInitTmp: true);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(staging, final, layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.AlreadyInitialized));
        }

        // ---- ExpectedBinding matches factory ----

        [Test]
        public void Classify_ExpectedBinding_MatchesFactory()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding expected = MakeBinding(layout);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(
                MakeSnapshot(MakeFullyCanonical(Staging, expected), MakeFullyCanonical(Final, expected), layout));

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.AlreadyInitialized));

            CaptureRunMarkerBinding actual = decision.ExpectedBinding;
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual.TestRunId, Is.EqualTo(expected.TestRunId));
            Assert.That(actual.RunInitializationId, Is.EqualTo(expected.RunInitializationId));
            Assert.That(actual.StagingRunRootSha256, Is.EqualTo(expected.StagingRunRootSha256));
            Assert.That(actual.FinalRunRootSha256, Is.EqualTo(expected.FinalRunRootSha256));

            AssertInitEqual(actual.StagingInitialization, expected.StagingInitialization);
            AssertInitEqual(actual.FinalInitialization, expected.FinalInitialization);
            AssertReadyEqual(actual.StagingReady, expected.StagingReady);
            AssertReadyEqual(actual.FinalReady, expected.FinalReady);

            Assert.That(decision.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(decision.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(decision.RootLayout, Is.SameAs(layout));
        }

        // ---- Decision validation ----

        [Test]
        public void Decision_UndefinedDisposition_Rejected()
        {
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(MakeAbsent(Staging), MakeAbsent(Final));

            foreach (CaptureRunInitializationRecoveryDisposition disposition in new[]
            {
                CaptureRunInitializationRecoveryDisposition.None,
                (CaptureRunInitializationRecoveryDisposition)8,
                (CaptureRunInitializationRecoveryDisposition)(-1)
            })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunInitializationRecoveryDecision(snapshot, disposition, null));

                Assert.That(ex.ParamName, Is.EqualTo("disposition"));
            }
        }

        [Test]
        public void Decision_FreshWithBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(MakeAbsent(Staging), MakeAbsent(Final), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.StartFresh, MakeBinding(layout)));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_CollisionWithBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true),
                MakeAbsent(Final),
                layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.RunRootCollision, MakeBinding(layout)));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_CompletionWithoutBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, null));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_BindingRootLayoutMismatch_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootLayout otherLayout = MakeLayout(2);
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, MakeBinding(otherLayout)));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_FullyInitializedSnapshot_StartFresh_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.StartFresh, null));

            Assert.That(ex.ParamName, Is.EqualTo("disposition"));
        }

        [Test]
        public void Decision_BothMissingSnapshot_BindingRequiredDispositions_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(MakeAbsent(Staging), MakeAbsent(Final), layout);
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            foreach (CaptureRunInitializationRecoveryDisposition disposition in new[]
            {
                CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization,
                CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers,
                CaptureRunInitializationRecoveryDisposition.AlreadyInitialized,
                CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery
            })
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => new CaptureRunInitializationRecoveryDecision(snapshot, disposition, binding));

                Assert.That(ex.ParamName, Is.EqualTo("disposition"));
            }
        }

        [Test]
        public void Decision_OneSidedInitSnapshot_CompleteReadyMarkers_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers, binding));

            Assert.That(ex.ParamName, Is.EqualTo("disposition"));
        }

        [Test]
        public void Decision_BothInitReadySnapshot_CompleteMissingPeerInitialization_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization, binding));

            Assert.That(ex.ParamName, Is.EqualTo("disposition"));
        }

        [Test]
        public void Decision_SameLayoutDifferentInitializationIdBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding observed = MakeBinding(layout, InitId);
            CaptureRunMarkerBinding foreignId = MakeBinding(layout, OtherInitId);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, observed), MakeFullyCanonical(Final, observed), layout);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, foreignId));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_ObservedReadyDifferentBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding observed = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, observed), MakeFullyCanonical(Final, observed), layout);

            CaptureRunMarkerBinding forgedReady = (CaptureRunMarkerBinding)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerBinding));
            SetField(forgedReady, "_stagingInitialization", observed.StagingInitialization);
            SetField(forgedReady, "_finalInitialization", observed.FinalInitialization);
            SetField(forgedReady, "_stagingReady", ChangeReady(observed.StagingReady, stagingInitSha256: StagingHash));
            SetField(forgedReady, "_finalReady", observed.FinalReady);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, forgedReady));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_UninitializedBinding_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            CaptureRunMarkerBinding forged = (CaptureRunMarkerBinding)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerBinding));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, forged));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_BindingWithNullMarker_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding observed = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, observed), MakeFullyCanonical(Final, observed), layout);

            AssertBindingWithNullMarkerRejected(snapshot, observed, "_stagingInitialization");
            AssertBindingWithNullMarkerRejected(snapshot, observed, "_finalInitialization");
            AssertBindingWithNullMarkerRejected(snapshot, observed, "_stagingReady");
            AssertBindingWithNullMarkerRejected(snapshot, observed, "_finalReady");
        }

        [Test]
        public void Decision_BindingWithUninitializedMarkerValues_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding observed = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, observed), MakeFullyCanonical(Final, observed), layout);

            CaptureRunInitializationMarker forgedInit = (CaptureRunInitializationMarker)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationMarker));

            CaptureRunMarkerBinding forged = (CaptureRunMarkerBinding)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerBinding));
            SetField(forged, "_stagingInitialization", forgedInit);
            SetField(forged, "_finalInitialization", observed.FinalInitialization);
            SetField(forged, "_stagingReady", observed.StagingReady);
            SetField(forged, "_finalReady", observed.FinalReady);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, forged));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }

        [Test]
        public void Decision_IsValid_False_WhenExpectedBindingUninitialized()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout);

            CaptureRunInitializationRecoveryDecision decision = (CaptureRunInitializationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryDecision));
            SetField(decision, "_snapshot", snapshot);
            SetField(decision, "_disposition", CaptureRunInitializationRecoveryDisposition.AlreadyInitialized);
            SetField(decision, "_expectedBinding", (CaptureRunMarkerBinding)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerBinding)));

            Assert.That(decision.IsValid, Is.False);
        }

        [Test]
        public void Decision_IsValid_True_ForValidDecisions()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);

            CaptureRunInitializationRecoveryDecision fresh = new CaptureRunInitializationRecoveryDecision(
                MakeSnapshot(MakeAbsent(Staging), MakeAbsent(Final), layout),
                CaptureRunInitializationRecoveryDisposition.StartFresh,
                null);
            Assert.That(fresh.IsValid, Is.True);

            CaptureRunInitializationRecoveryDecision already = new CaptureRunInitializationRecoveryDecision(
                MakeSnapshot(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), layout),
                CaptureRunInitializationRecoveryDisposition.AlreadyInitialized,
                binding);
            Assert.That(already.IsValid, Is.True);
        }

        [Test]
        public void Decision_IsValid_False_WhenForged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(MakeAbsent(Staging), MakeAbsent(Final), layout);

            CaptureRunInitializationRecoveryDecision none = (CaptureRunInitializationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryDecision));
            SetField(none, "_snapshot", snapshot);
            SetField(none, "_disposition", CaptureRunInitializationRecoveryDisposition.None);
            SetField(none, "_expectedBinding", null);
            Assert.That(none.IsValid, Is.False);

            CaptureRunInitializationRecoveryDecision freshWithBinding = (CaptureRunInitializationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryDecision));
            SetField(freshWithBinding, "_snapshot", snapshot);
            SetField(freshWithBinding, "_disposition", CaptureRunInitializationRecoveryDisposition.StartFresh);
            SetField(freshWithBinding, "_expectedBinding", MakeBinding(layout));
            Assert.That(freshWithBinding.IsValid, Is.False);

            CaptureRunInitializationRecoveryDecision completionNoBinding = (CaptureRunInitializationRecoveryDecision)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationRecoveryDecision));
            SetField(completionNoBinding, "_snapshot", snapshot);
            SetField(completionNoBinding, "_disposition", CaptureRunInitializationRecoveryDisposition.AlreadyInitialized);
            SetField(completionNoBinding, "_expectedBinding", null);
            Assert.That(completionNoBinding.IsValid, Is.False);
        }

        // ---- Non-mutation / non-dispose ----

        [Test]
        public void Classify_DoesNotDisposeLeaseOrMutateInputs()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRootObservation staging = MakeFullyCanonical(Staging, binding);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = MakeSnapshot(staging, final, layout, disposeLog);

            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(snapshot);

            Assert.That(disposeLog, Is.Empty, "The classifier must not dispose the lock lease.");
            Assert.That(snapshot.Staging, Is.SameAs(staging));
            Assert.That(snapshot.Final, Is.SameAs(final));
            Assert.That(decision.Snapshot, Is.SameAs(snapshot));
            Assert.That(staging.InitializationMarker, Is.SameAs(binding.StagingInitialization));
            Assert.That(staging.ReadyMarker, Is.SameAs(binding.StagingReady));
            Assert.That(final.InitializationMarker, Is.SameAs(binding.FinalInitialization));
            Assert.That(final.ReadyMarker, Is.SameAs(binding.FinalReady));
        }

        // ---- Shape ----

        [Test]
        public void Classifier_NoFields_NoMutableStaticState()
        {
            Type type = typeof(CaptureRunInitializationRecoveryClassifier);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Decision_Shape_SealedNoPublicCtorNotDisposableNotUnityObject()
        {
            Type type = typeof(CaptureRunInitializationRecoveryDecision);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryDisposition.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryDecision.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationRecoveryClassifier.cs"
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
            }
        }

        // ---- Assertion helpers ----

        private static void AssertCollision(CaptureRunInitializationRecoveryInspectionSnapshot snapshot)
        {
            CaptureRunInitializationRecoveryDecision decision = CaptureRunInitializationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(CaptureRunInitializationRecoveryDisposition.RunRootCollision));
            Assert.That(decision.ExpectedBinding, Is.Null);
            Assert.That(decision.RunInitializationId, Is.Null);
            Assert.That(decision.IsValid, Is.True);
        }

        private static void AssertInitEqual(CaptureRunInitializationMarker observed, CaptureRunInitializationMarker expected)
        {
            Assert.That(observed.SchemaVersion, Is.EqualTo(expected.SchemaVersion));
            Assert.That(observed.TestRunId, Is.EqualTo(expected.TestRunId));
            Assert.That(observed.RunInitializationId, Is.EqualTo(expected.RunInitializationId));
            Assert.That(observed.RootRole, Is.EqualTo(expected.RootRole));
            Assert.That(observed.StagingRunRootSha256, Is.EqualTo(expected.StagingRunRootSha256));
            Assert.That(observed.FinalRunRootSha256, Is.EqualTo(expected.FinalRunRootSha256));
        }

        private static void AssertReadyEqual(CaptureRunReadyMarker observed, CaptureRunReadyMarker expected)
        {
            Assert.That(observed.SchemaVersion, Is.EqualTo(expected.SchemaVersion));
            Assert.That(observed.TestRunId, Is.EqualTo(expected.TestRunId));
            Assert.That(observed.RunInitializationId, Is.EqualTo(expected.RunInitializationId));
            Assert.That(observed.StagingInitSha256, Is.EqualTo(expected.StagingInitSha256));
            Assert.That(observed.FinalInitSha256, Is.EqualTo(expected.FinalInitSha256));
        }

        private static void AssertBindingWithNullMarkerRejected(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot,
            CaptureRunMarkerBinding template,
            string fieldName)
        {
            CaptureRunMarkerBinding forged = (CaptureRunMarkerBinding)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerBinding));
            SetField(forged, "_stagingInitialization", template.StagingInitialization);
            SetField(forged, "_finalInitialization", template.FinalInitialization);
            SetField(forged, "_stagingReady", template.StagingReady);
            SetField(forged, "_finalReady", template.FinalReady);
            SetField(forged, fieldName, null);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunInitializationRecoveryDecision(
                    snapshot, CaptureRunInitializationRecoveryDisposition.AlreadyInitialized, forged));

            Assert.That(ex.ParamName, Is.EqualTo("expectedBinding"));
        }
    }
}
