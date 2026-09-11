using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the production orphan cleanup ownership releaser:
    /// what it refuses before touching the lease, that one call disposes the
    /// exact lease once and attests it, and how a partial release stays
    /// retryable through the same operation.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state. The
    /// exactly-once routing and the corrupt-receipt refusals belong to the
    /// execution coordinator's own fixture and are not restated here.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteOwnershipReleaserContractTests
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
        public void Release_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryIncompleteOwnershipReleaser().Release(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Release_AlreadyFullyReleasedOperation_RejectedWithoutTouchingTheHandles()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueCleaned();

            h.Owner.Dispose();
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryIncompleteOwnershipReleaser()
                    .Release(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1), "no further contact.");
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1), "no further contact.");
        }

        // ---- Both cleanup shapes release the same way ----

        [Test]
        public void Release_AfterACleanedCleanup_ReleasesTheLeaseAndAttestsIt()
        {
            AssertReleases(
                h => h.IssueCleaned(),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned);
        }

        [Test]
        public void Release_AfterAFailedCleanup_ReleasesTheLeaseAndAttestsIt()
        {
            AssertReleases(
                h => h.IssueFailed(),
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed);
        }

        // ---- Partial release and retry ----

        [Test]
        public void Release_PartialRelease_PropagatesUnwrappedAndIssuesNoReceipt()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueCleaned();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryIncompleteOwnershipReleaser();

            // The lease's own aggregate, not a wrapper of this boundary's.
            AggregateException failure = Assert.Throws<AggregateException>(
                () => releaser.Release(operation));

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsCreated, Is.False);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);
            Assert.That(operation.CanRelease, Is.True, "still retryable.");

            // The second call finishes it, and the handle that was already
            // released is not disposed again.
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        // ---- What this boundary must leave alone ----

        [Test]
        public void Release_ChangesNothingButTheLease()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation =
                h.IssueFailed();
            NvencRunPublicationRecoveryIncompleteCleanupOperation cleanupOperation =
                operation.CleanupOperation;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = operation.Snapshot;

            new NvencRunPublicationRecoveryIncompleteOwnershipReleaser().Release(operation);

            Assert.That(operation.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
            Assert.That(operation.CleanupResult.Receipt, Is.Null);
            Assert.That(
                ReferenceEquals(operation.CleanupResult.Cleaner, h.Cleaner), Is.True);
            Assert.That(ReferenceEquals(operation.CleanupOperation, cleanupOperation), Is.True);
            Assert.That(ReferenceEquals(operation.Decision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(operation.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Absent));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(operation.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(
                ReferenceEquals(operation.LockIdentityEvidence, h.LockIdentityEvidence), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);

            // The release is process-local: nothing was created on disk.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Releaser_IsSealedStatelessAndNotDisposable()
        {
            Type type = typeof(NvencRunPublicationRecoveryIncompleteOwnershipReleaser);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic),
                Is.Empty);

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(
                    field.IsLiteral || field.IsInitOnly, Is.True,
                    "no mutable static state.");
            }
        }

        // ---- Fixture helpers ----

        private void AssertReleases(
            Func<Harness, NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation> issue,
            NvencRunPublicationRecoveryIncompleteCleanupStatus expected)
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation = issue(h);
            NvencRunPublicationRecoveryIncompleteOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryIncompleteOwnershipReleaser();

            Assert.That(operation.CleanupStatus, Is.EqualTo(expected));

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            // The operation's admission validity ends with the release; its
            // binding does not, and the receipt rests on the binding.
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.False);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.Releaser, releaser), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(receipt.CleanupStatus, Is.EqualTo(expected));
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
        /// One incomplete Run holding its lease, able to issue the release
        /// operation from either terminal cleanup shape.
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

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation IssueCleaned()
            {
                return NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        Cleaner, CleanupOperation),
                    Owner);
            }

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation IssueFailed()
            {
                return NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        Cleaner, CleanupOperation),
                    Owner);
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
