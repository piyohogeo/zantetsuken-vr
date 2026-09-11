using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC publication recovery
    /// incomplete/orphan cleanup ownership release operation: which cleanup
    /// results may release the Run's lease, what it forwards, and how its three
    /// predicates behave across a partial and a completed release.
    /// </summary>
    /// <remarks>
    /// Cleanup results come from the ordinary attempt-result factories with a
    /// cleaner that deletes nothing, and partial releases come from the
    /// ordinary API with a lock handle that fails its first release, never from
    /// rewriting private state. Nothing here runs a cleanup, touches the
    /// filesystem, or releases the lease through the operation.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private int _runIds;

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                try
                {
                    owner.Dispose();
                }
                catch (AggregateException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            _owners.Clear();
        }

        // ---- Admission ----

        [Test]
        public void Create_NullOwnershipLease_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    h.Cleaned(), null));
            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
        }

        [Test]
        public void Create_DefaultCleanupResult_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    default, h.Owner));
            Assert.That(ex.ParamName, Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void Create_CleanupResultInvalidatedByAReleasedLock_Rejected()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleaned = h.Cleaned();

            ReleaseAllLocks();
            Assert.That(cleaned.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    cleaned, h.Owner));
            Assert.That(ex.ParamName, Is.EqualTo("cleanupResult"));
        }

        [Test]
        public void Create_FromACleanedAttempt_Issues()
        {
            Harness h = MakeHarness();

            AssertIssues(h, h.Cleaned(),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned);
        }

        [Test]
        public void Create_FromAFailedAttempt_Issues()
        {
            // A failed orphan cleanup may have discarded part of the Run, and
            // the lock is returned all the same: a later recovery re-observes
            // the filesystem under its own lock.
            Harness h = MakeHarness();

            AssertIssues(h, h.Failed(),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed);
        }

        [Test]
        public void Create_ForeignOwnershipLease_Rejected()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleaned = h.Cleaned();

            // Another Run's lease.
            Harness other = MakeHarness();
            ArgumentException foreignRun = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    cleaned, other.Owner));
            Assert.That(foreignRun.ParamName, Is.EqualTo("ownershipLease"));

            // A second lease over this Run's own layout: a different path set
            // instance, so it is not the lock this graph was issued for.
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(h.Layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new CountingHandle(pathSet.FirstLockPath),
                new CountingHandle(pathSet.SecondLockPath));
            CaptureRunInitializationSessionOwnershipLease second =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(second);

            ArgumentException otherPathSet = Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    cleaned, second));
            Assert.That(otherPathSet.ParamName, Is.EqualTo("ownershipLease"));
        }

        [Test]
        public void Create_ReleasesAndChangesNothing()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleaned = h.Cleaned();
            NvencRunPublicationRecoveryDecision decision = h.Decision;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;

            AssertRetainedAndUntouched(h, "before the factory");

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    cleaned, h.Owner);

            Assert.That(operation, Is.Not.Null);
            AssertRetainedAndUntouched(h, "after the factory");

            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(ReferenceEquals(decision.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(ReferenceEquals(snapshot.Operation.OpenOutcome, h.OpenOutcome), Is.True);
        }

        // ---- The three predicates across the lease's release states ----

        [Test]
        public void Operation_BeforeAnyRelease_IsBoundReleasableAndValid()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    h.Cleaned(), h.Owner);

            AssertPredicates(operation, true, true, true, "before any release");
        }

        [Test]
        public void Operation_AcrossReleaseStates_BehavesTheSameForBothCleanupStatuses()
        {
            Harness cleaned = MakeHarness(throwingFirstRelease: true);
            Harness failed = MakeHarness(throwingFirstRelease: true);

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation fromCleaned =
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    cleaned.Cleaned(), cleaned.Owner);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation fromFailed =
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    failed.Failed(), failed.Owner);

            AssertPredicates(fromCleaned, true, true, true, "before any release");
            AssertPredicates(fromFailed, true, true, true, "before any release");

            // The ordinary API's partial release: the second handle is
            // released, the first one throws, and the lease stays retryable.
            Assert.Throws<AggregateException>(() => cleaned.Owner.Dispose());
            Assert.Throws<AggregateException>(() => failed.Owner.Dispose());

            Assert.That(cleaned.Owner.IsCreated, Is.False);
            Assert.That(cleaned.Owner.IsReleaseComplete, Is.False);
            AssertPredicates(fromCleaned, true, true, false, "after a partial release");
            AssertPredicates(fromFailed, true, true, false, "after a partial release");

            cleaned.Owner.Dispose();
            failed.Owner.Dispose();

            Assert.That(cleaned.Owner.IsReleaseComplete, Is.True);
            Assert.That(failed.Owner.IsReleaseComplete, Is.True);
            AssertPredicates(fromCleaned, true, false, false, "after a completed release");
            AssertPredicates(fromFailed, true, false, false, "after a completed release");

            // The only difference between the two is the status they carry.
            Assert.That(fromCleaned.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned));
            Assert.That(fromFailed.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
            Assert.That(fromCleaned.CleanupResult.Receipt, Is.Not.Null);
            Assert.That(fromFailed.CleanupResult.Receipt, Is.Null);
        }

        // ---- Shape ----

        [Test]
        public void Operation_IsSealedNonDisposableAndHoldsTwoReadonlyValues()
        {
            Type type =
                typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held value must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunPublicationRecoveryIncompleteCleanupAttemptResult),
                typeof(CaptureRunInitializationSessionOwnershipLease),
            }));

            Assert.That(
                type.GetConstructors(BindingFlags.Instance | BindingFlags.Public), Is.Empty);
        }

        // ---- Fixture helpers ----

        private static void AssertIssues(
            Harness h,
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanupResult,
            NvencRunPublicationRecoveryIncompleteCleanupStatus expected)
        {
            Assert.That(cleanupResult.Status, Is.EqualTo(expected));

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    cleanupResult, h.Owner);

            Assert.That(operation.CleanupStatus, Is.EqualTo(expected));
            Assert.That(operation.IsValid, Is.True);

            Assert.That(operation.CleanupResult.IsIssuedFor(h.Cleaner, h.CleanupOperation),
                Is.True);
            Assert.That(ReferenceEquals(operation.CleanupOperation, h.CleanupOperation), Is.True);
            Assert.That(ReferenceEquals(operation.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(operation.Decision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(operation.Snapshot, h.Decision.Snapshot), Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(operation.LockIdentityEvidence, h.LockIdentityEvidence), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(h.Layout.TestRunId));
            Assert.That(
                ReferenceEquals(operation.RunInitializationId, h.Decision.RunInitializationId),
                Is.True);
        }

        private static void AssertPredicates(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation,
            bool binding,
            bool canRelease,
            bool valid,
            string message)
        {
            Assert.That(operation.IsBindingIntact, Is.EqualTo(binding), message);
            Assert.That(operation.CanRelease, Is.EqualTo(canRelease), message);
            Assert.That(operation.IsValid, Is.EqualTo(valid), message);
        }

        private static void AssertRetainedAndUntouched(Harness h, string message)
        {
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0), message);
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0), message);
            Assert.That(h.Owner.IsCreated, Is.True, message);
            Assert.That(h.Owner.CanRelease, Is.True, message);
            Assert.That(h.Owner.IsReleaseComplete, Is.False, message);

            // This boundary is filesystem-free.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False, message);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False, message);
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private Harness MakeHarness(bool throwingFirstRelease = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));

            return new Harness(
                layout,
                openOutcome,
                decision,
                NvencRunPublicationRecoveryIncompleteCleanupOperation.Create(decision, owner),
                owner,
                firstHandle,
                secondHandle);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            bool throwingFirstRelease,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(
                        MakeRootObservation(
                            CaptureRunRootRole.Staging,
                            binding.StagingInitialization,
                            binding.StagingReady,
                            hasNonMarkerEntry: true),
                        MakeRootObservation(
                            CaptureRunRootRole.Final,
                            binding.FinalInitialization,
                            binding.FinalReady,
                            hasNonMarkerEntry: false)),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath, throwingFirstRelease);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);

            CaptureRunLockIdentityEvidence identity =
                CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4));

            Assert.That(result.Status,
                Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired),
                "the fixture must reach a publication-recovery outcome.");

            return new CaptureRunInitializationOpenOutcome(result, null, identity);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker init,
            CaptureRunReadyMarker ready,
            bool hasNonMarkerEntry)
        {
            return new CaptureRunInitializationRootObservation(
                role, true, false, CaptureRunMarkerObservationStatus.Canonical, init,
                false, CaptureRunMarkerObservationStatus.Canonical, ready,
                hasNonMarkerEntry, false, false);
        }

        /// <summary>
        /// A distinct layout per call, so another Run's lease is genuinely
        /// another Run rather than the same paths again.
        /// </summary>
        private CaptureRunRootLayout MakeLayout()
        {
            string root = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-fake"
                : "/zantetsuken-fake";
            _runIds++;

            return new CaptureRunRootLayout(
                Path.Combine(root, "staging-" + _runIds),
                Path.Combine(root, "final-" + _runIds),
                1);
        }

        /// <summary>
        /// One incomplete Run holding its lease, with its cleanup operation
        /// already issued and a cleaner that deletes nothing.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryDecision decision,
                NvencRunPublicationRecoveryIncompleteCleanupOperation cleanupOperation,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Decision = decision;
                CleanupOperation = cleanupOperation;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal NvencRunPublicationRecoveryDecision Decision { get; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOperation CleanupOperation { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
                Decision.Snapshot.Operation.LockIdentityEvidence;

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakeCleaner Cleaner { get; } = new FakeCleaner();

            internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Cleaned()
            {
                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                    Cleaner, CleanupOperation);
            }

            internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Failed()
            {
                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                    Cleaner, CleanupOperation);
            }
        }

        /// <summary>
        /// A cleaner that never runs: it only names the attempt the ordinary
        /// result factories mint here.
        /// </summary>
        private sealed class FakeCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                throw new NotSupportedException("This fixture never runs a cleanup.");
            }
        }

        /// <summary>
        /// A lock handle that counts its own releases and can fail the first
        /// one, producing the ordinary API's partial release.
        /// </summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private readonly bool _throwFirstRelease;
            private int _disposeCalls;

            internal CountingHandle(string lockPath, bool throwFirstRelease = false)
            {
                LockPath = lockPath;
                _throwFirstRelease = throwFirstRelease;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;

                if (_throwFirstRelease && _disposeCalls == 1)
                {
                    throw new InvalidOperationException("First release fails.");
                }
            }
        }

        private sealed class FakeRecoveryInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            internal FakeRecoveryInspector(
                CaptureRunInitializationRootObservation staging,
                CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
                CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }
    }
}
