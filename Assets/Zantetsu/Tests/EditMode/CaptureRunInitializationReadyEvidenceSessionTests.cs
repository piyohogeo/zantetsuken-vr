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
    public class CaptureRunInitializationReadyEvidenceSessionTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

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

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeWriter());
            return executionCoordinator.Execute(batch);
        }

        private static CaptureRunInitializationRecoveryOrchestrationResult MakeRecoveryResult(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            CaptureRunLockIdentityEvidence identity)
        {
            CaptureRunRootLayout layout = identity.RootLayout;
            FakeInspector inspector = MakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator executionCoordinator = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationReadyEvidenceSessionTests).Assembly.Location);
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

        // ---- Fresh evidence ----

        [Test]
        public void FromFresh_ForwardsAllValues()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);

            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);

            Assert.That(evidence.FreshExecutionReceipt, Is.SameAs(receipt));
            Assert.That(evidence.RecoveryOrchestrationResult, Is.Null);
            Assert.That(evidence.IsRecovery, Is.False);
            Assert.That(evidence.RootLayout, Is.SameAs(receipt.RootLayout));
            Assert.That(evidence.TestRunId, Is.EqualTo(receipt.TestRunId));
            Assert.That(evidence.RunInitializationId, Is.EqualTo(receipt.RunInitializationId));
            Assert.That(evidence.IsValid, Is.True);
        }

        [Test]
        public void FromFresh_NullOrInvalid_Rejected()
        {
            ArgumentNullException exNull = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationReadyEvidence.FromFresh(null));
            Assert.That(exNull.ParamName, Is.EqualTo("receipt"));

            CaptureRunInitializationExecutionReceipt invalid =
                (CaptureRunInitializationExecutionReceipt)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationExecutionReceipt));
            ArgumentException exInvalid = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationReadyEvidence.FromFresh(invalid));
            Assert.That(exInvalid.ParamName, Is.EqualTo("receipt"));
        }

        // ---- Recovery evidence ----

        [Test]
        public void FromRecovery_AcceptsThreeInitializationReadyDispositions()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);

            CaptureRunInitializationRecoveryOrchestrationResult[] results =
            {
                MakeRecoveryResult(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), identity), // CompleteMissingPeer
                MakeRecoveryResult(MakeCanonicalInit(Staging, binding.StagingInitialization), MakeCanonicalInit(Final, binding.FinalInitialization), identity), // CompleteReadyMarkers
                MakeRecoveryResult(MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), identity) // AlreadyInitialized
            };

            foreach (CaptureRunInitializationRecoveryOrchestrationResult result in results)
            {
                CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(result);

                Assert.That(evidence.RecoveryOrchestrationResult, Is.SameAs(result));
                Assert.That(evidence.FreshExecutionReceipt, Is.Null);
                Assert.That(evidence.IsRecovery, Is.True);
                Assert.That(evidence.RootLayout, Is.SameAs(result.RootLayout));
                Assert.That(evidence.TestRunId, Is.EqualTo(result.TestRunId));
                Assert.That(evidence.RunInitializationId, Is.EqualTo(result.RunInitializationId));
                Assert.That(evidence.IsValid, Is.True);
            }

            owner.Dispose();
        }

        [Test]
        public void FromRecovery_RejectsStartFreshLikePublicationAndCollision()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);

            CaptureRunInitializationRecoveryOrchestrationResult[] rejected =
            {
                MakeRecoveryResult(MakeAbsent(Staging), MakeAbsent(Final), identity), // StartFresh
                MakeRecoveryResult(MakeObservation(Staging, true, Absent, null, Absent, null, hasInitTmp: true), MakeAbsent(Final), identity), // CleanupTemporaryAndStartFresh
                MakeRecoveryResult(MakeObservation(Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true), MakeFullyCanonical(Final, binding), identity), // RequiresPublicationRecovery
                MakeRecoveryResult(MakeObservation(Staging, true, Absent, null, Absent, null, hasUnknown: true), MakeAbsent(Final), identity) // RunRootCollision
            };

            foreach (CaptureRunInitializationRecoveryOrchestrationResult result in rejected)
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => CaptureRunInitializationReadyEvidence.FromRecovery(result));
                Assert.That(ex.ParamName, Is.EqualTo("result"));
            }

            owner.Dispose();
        }

        [Test]
        public void FromRecovery_NullOrInvalid_Rejected()
        {
            ArgumentNullException exNull = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationReadyEvidence.FromRecovery(null));
            Assert.That(exNull.ParamName, Is.EqualTo("result"));

            CaptureRunInitializationRecoveryOrchestrationResult invalid =
                (CaptureRunInitializationRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationRecoveryOrchestrationResult));
            ArgumentException exInvalid = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationReadyEvidence.FromRecovery(invalid));
            Assert.That(exInvalid.ParamName, Is.EqualTo("result"));
        }

        // ---- Evidence exclusivity and forged IsValid ----

        [Test]
        public void Evidence_CannotHoldBothPaths()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationRecoveryOrchestrationResult result = MakeRecoveryResult(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), identity);

            CaptureRunInitializationReadyEvidence forged = (CaptureRunInitializationReadyEvidence)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationReadyEvidence));
            SetField(forged, "_freshExecutionReceipt", receipt);
            SetField(forged, "_recoveryOrchestrationResult", result);
            Assert.That(forged.IsValid, Is.False);

            CaptureRunInitializationReadyEvidence empty = (CaptureRunInitializationReadyEvidence)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationReadyEvidence));
            SetField(empty, "_freshExecutionReceipt", null);
            SetField(empty, "_recoveryOrchestrationResult", null);
            Assert.That(empty.IsValid, Is.False);

            owner.Dispose();
        }

        [Test]
        public void Evidence_IsValidFalse_ForBrokenNestedValues_WithoutException()
        {
            // Fresh evidence with a forged invalid receipt.
            CaptureRunInitializationReadyEvidence freshBroken = (CaptureRunInitializationReadyEvidence)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationReadyEvidence));
            SetField(freshBroken, "_freshExecutionReceipt",
                (CaptureRunInitializationExecutionReceipt)FormatterServices.GetUninitializedObject(typeof(CaptureRunInitializationExecutionReceipt)));
            SetField(freshBroken, "_recoveryOrchestrationResult", null);
            Assert.That(freshBroken.IsValid, Is.False);

            // Recovery evidence whose nested result is invalid.
            CaptureRunInitializationReadyEvidence recoveryBroken = (CaptureRunInitializationReadyEvidence)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationReadyEvidence));
            SetField(recoveryBroken, "_freshExecutionReceipt", null);
            SetField(recoveryBroken, "_recoveryOrchestrationResult",
                (CaptureRunInitializationRecoveryOrchestrationResult)FormatterServices.GetUninitializedObject(typeof(CaptureRunInitializationRecoveryOrchestrationResult)));
            Assert.That(recoveryBroken.IsValid, Is.False);
        }

        // ---- Session factory ----

        [Test]
        public void Factory_Success_ReturnsIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);

            Assert.That(issue, Is.Not.Null);
            Assert.That(issue.IsValid, Is.True);
            Assert.That(issue.OwnershipLease, Is.SameAs(owner));
            Assert.That(issue.LockIdentityEvidence, Is.SameAs(identity));
            Assert.That(issue.Session.ReadyEvidence, Is.SameAs(evidence));

            owner.Dispose();
        }

        [Test]
        public void Factory_ForeignRootLayout_Rejected()
        {
            CaptureRunRootLayout layoutA = MakeLayout(1);
            CaptureRunRootLayout layoutB = MakeLayout(2);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layoutA, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layoutB));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSessionFactory.Create(owner, identity, evidence));

            Assert.That(ex.ParamName, Is.EqualTo("evidence"));
            Assert.That(owner.IsCreated, Is.True);

            owner.Dispose();
        }

        [Test]
        public void Factory_NullEvidence_Rejected()
        {
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(MakeLayout(), null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationSessionFactory.Create(owner, identity, null));

            Assert.That(ex.ParamName, Is.EqualTo("evidence"));

            owner.Dispose();
        }

        [Test]
        public void Factory_NullOwnershipLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationSessionFactory.Create(null, null, evidence));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
        }

        [Test]
        public void Factory_DisposedOwnershipLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            owner.Dispose();
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSessionFactory.Create(owner, identity, evidence));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
        }

        [Test]
        public void Factory_RecoveryMismatchedIdentity_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease evidenceOwner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence evidenceIdentity = MakeIdentityEvidence(evidenceOwner);
            CaptureRunInitializationRecoveryOrchestrationResult result = MakeRecoveryResult(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), evidenceIdentity);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(result);

            CaptureRunInitializationSessionOwnershipLease otherOwner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence otherIdentity = MakeIdentityEvidence(otherOwner);
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSessionFactory.Create(otherOwner, otherIdentity, evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
            Assert.That(otherOwner.IsCreated, Is.True);

            evidenceOwner.Dispose();
            otherOwner.Dispose();
        }

        // ---- Recovery session ----

        [Test]
        public void SessionIssue_Recovery_ForwardsAndOwnerDisposesSecondThenFirst()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult result = MakeRecoveryResult(
                MakeCanonicalInit(Staging, binding.StagingInitialization), MakeAbsent(Final), identity);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(result);

            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
            CaptureRunInitializationSession session = issue.Session;

            Assert.That(session.ReadyEvidence, Is.SameAs(evidence));
            Assert.That(session.RecoveryOrchestrationResult, Is.SameAs(result));
            Assert.That(session.ExecutionReceipt, Is.Null);
            Assert.That(session.RootLayout, Is.SameAs(result.RootLayout));
            Assert.That(session.TestRunId, Is.EqualTo(result.TestRunId));
            Assert.That(session.RunInitializationId, Is.EqualTo(result.RunInitializationId));

            owner.Dispose();
            Assert.That(disposeLog, Is.EqualTo(new[] { owner.LockPathSet.SecondLockPath, owner.LockPathSet.FirstLockPath }));
            Assert.That(session.ReadyEvidence, Is.SameAs(evidence));
            Assert.That(session.RecoveryOrchestrationResult, Is.SameAs(result));
        }

        [Test]
        public void Session_Recovery_MismatchedIdentity_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease evidenceOwner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence evidenceIdentity = MakeIdentityEvidence(evidenceOwner);
            CaptureRunInitializationRecoveryOrchestrationResult result = MakeRecoveryResult(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), evidenceIdentity);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(result);

            CaptureRunInitializationSessionOwnershipLease otherOwner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence otherIdentity = MakeIdentityEvidence(otherOwner);
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSessionFactory.Create(otherOwner, otherIdentity, evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));

            evidenceOwner.Dispose();
            otherOwner.Dispose();
        }

        [Test]
        public void Session_Fresh_ForwardsExecutionReceipt()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);

            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSessionFactory.Create(
                owner, identity, CaptureRunInitializationReadyEvidence.FromFresh(receipt));
            CaptureRunInitializationSession session = issue.Session;

            Assert.That(session.ExecutionReceipt, Is.SameAs(receipt));
            Assert.That(session.ReadyEvidence.FreshExecutionReceipt, Is.SameAs(receipt));
            Assert.That(session.ReadyEvidence.IsRecovery, Is.False);
            Assert.That(session.RecoveryOrchestrationResult, Is.Null);
            Assert.That(session.RootLayout, Is.SameAs(receipt.RootLayout));

            owner.Dispose();
        }

        [Test]
        public void Session_Recovery_OwnerDispose_Idempotent_And_RetryAfterFailure()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerBinding binding = MakeBinding(layout);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null, out FakeHandle first, out FakeHandle second);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationRecoveryOrchestrationResult result = MakeRecoveryResult(
                MakeFullyCanonical(Staging, binding), MakeFullyCanonical(Final, binding), identity);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(result);
            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);

            second.ThrowOnDispose = true;
            Assert.Throws<AggregateException>(() => owner.Dispose());
            Assert.That(owner.IsCreated, Is.False);

            second.ThrowOnDispose = false;
            owner.Dispose();
            owner.Dispose();

            Assert.That(owner.IsCreated, Is.False);
            Assert.That(second.DisposeCount, Is.EqualTo(2));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
            Assert.That(issue.Session.ReadyEvidence, Is.SameAs(evidence));
            Assert.That(issue.Session.RecoveryOrchestrationResult, Is.SameAs(result));
        }

        // ---- Shape ----

        [Test]
        public void Evidence_Shape_TwoReadonlyFields_NoPublicConstructor()
        {
            Type type = typeof(CaptureRunInitializationReadyEvidence);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(2));
            Assert.That(fields.All(f => f.IsInitOnly), Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Factory_Shape_Stateless()
        {
            Type type = typeof(CaptureRunInitializationSessionFactory);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly), Is.Empty);
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string[] relativePaths =
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationReadyEvidence.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationSessionFactory.cs"
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
                Assert.That(source, Does.Not.Contain("System.Threading"));
                Assert.That(source, Does.Not.Contain("Task"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("Bootstrap"));
                Assert.That(source, Does.Not.Contain("CapturePublication"));
                Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
                Assert.That(source, Does.Not.Contain("ICaptureRunInitializationIdSource"));
            }
        }
    }
}
