using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the incomplete recovery terminalization boundary: one
    /// orphan cleanup at most, a release retried through the one retention
    /// coordinator that cleanup produced, and the terminal result as the only
    /// completion latch.
    /// </summary>
    /// <remarks>
    /// The cleaner deletes nothing and the lock handles are the counting seam
    /// the contract fixtures use; partial releases come from the ordinary API
    /// with a handle that fails its first release, never from rewriting private
    /// state. The cleanup shapes, malformed attempt results, and release
    /// receipt correlation belong to the fixtures that own them.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteTerminalCoordinatorContractTests
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

        // ---- Construction ----

        [Test]
        public void Constructor_NullArguments_Rejected()
        {
            Harness h = MakeHarness();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
                        null, h.Owner, h.Cleanup, h.ReleaseExecution)).ParamName,
                Is.EqualTo("decision"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
                        h.Decision, null, h.Cleanup, h.ReleaseExecution)).ParamName,
                Is.EqualTo("ownershipLease"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
                        h.Decision, h.Owner, null, h.ReleaseExecution)).ParamName,
                Is.EqualTo("cleanup"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
                        h.Decision, h.Owner, h.Cleanup, null)).ParamName,
                Is.EqualTo("releaseExecution"));
        }

        [Test]
        public void Constructor_TouchesNeitherTheCleanupTheReleaserNorTheLease()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            Assert.That(ReferenceEquals(coordinator.Decision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(coordinator.OwnershipLease, h.Owner), Is.True);
            Assert.That(coordinator.ReleaseCoordinator, Is.Null);
            Assert.That(coordinator.IsCleanupPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.CleanupResult.IsValid, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
        }

        // ---- Both cleanup shapes reach the same terminal value ----

        [Test]
        public void Execute_AfterACleanedCleanup_ReachesTheTerminalValue()
        {
            AssertTerminalizes(
                cleanupFailed: false,
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned);
        }

        [Test]
        public void Execute_AfterAFailedCleanup_ReachesTheTerminalValueWithoutConvertingTheStatus()
        {
            AssertTerminalizes(
                cleanupFailed: true,
                NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed);
        }

        [Test]
        public void TerminalResult_CarriesTheRetainedReceiptAndForwardsItsGraph()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                coordinator.ReleaseCoordinator.Receipt;
            Assert.That(receipt, Is.Not.Null);
            Assert.That(ReferenceEquals(terminal.IncompleteRelease, receipt), Is.True);
            Assert.That(terminal.CaptureCompleteRelease, Is.Null);
            Assert.That(terminal.StopRelease, Is.Null);

            // The cleanup result is read back through the release operation,
            // not from a second copy.
            Assert.That(
                ReferenceEquals(
                    coordinator.CleanupResult.CleanupOperation,
                    coordinator.ReleaseCoordinator.Operation.CleanupOperation),
                Is.True);
            Assert.That(ReferenceEquals(coordinator.CleanupResult.Cleaner, h.Cleaner), Is.True);

            // One graph, read through that receipt.
            Assert.That(
                ReferenceEquals(terminal.PublicationRecoveryDecision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(terminal.Snapshot, h.Decision.Snapshot), Is.True);
            Assert.That(ReferenceEquals(terminal.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(terminal.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(terminal.RootLayout, h.Layout), Is.True);
            Assert.That(terminal.TestRunId, Is.EqualTo(h.Layout.TestRunId));
            Assert.That(terminal.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Execute_OnTheNormalPath_CleansOnceAndReleasesOnce()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            coordinator.Execute();

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(coordinator.IsCleanupPrepared, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);
        }

        [Test]
        public void Execute_AfterTheTerminalValue_TouchesNeitherTheCleanupNorTheRelease()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            NvencRunPublicationRecoveryTerminalResult first = coordinator.Execute();
            NvencRunPublicationRecoveryTerminalResult second = coordinator.Execute();

            Assert.That(
                ReferenceEquals(second.IncompleteRelease, first.IncompleteRelease), Is.True);
            Assert.That(second.IsIncompleteReleased, Is.True);

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        // ---- Retries never re-run the cleanup ----

        [Test]
        public void Execute_AfterAPartialRelease_RetriesTheReleaseAloneAndSucceeds()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Execute());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IncompleteRelease, Is.Null);
            Assert.That(coordinator.IsCleanupPrepared, Is.True);
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator releaseCoordinator =
                coordinator.ReleaseCoordinator;

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsIncompleteReleased, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);

            // The cleanup stayed at one call and the same retention
            // coordinator finished the release.
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(2));
            Assert.That(
                ReferenceEquals(coordinator.ReleaseCoordinator, releaseCoordinator), Is.True);

            // Only the handle that had not been released is disposed again.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Execute_AfterACleanupException_DoesNotRunTheCleanupAgain()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("cleanup faulted");
            h.Cleaner.Throw = failure;
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Execute());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsCleanupPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);

            // What that cleanup did on disk is unknown, so it is not repeated.
            h.Cleaner.Throw = null;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(coordinator.IsComplete, Is.False);
        }

        [Test]
        public void Execute_WhenAnUnusableReceiptFollowsACompletedRelease_StaysUnfinished()
        {
            Harness h = MakeHarness(foreignReceipt: true);
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            // The lease really is released by that call; only the receipt is
            // unusable.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);

            // No success is inferred, and neither the cleanup nor the releaser
            // is entered again.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsComplete, Is.False);
        }

        // ---- Fixture helpers ----

        private void AssertTerminalizes(
            bool cleanupFailed, NvencRunPublicationRecoveryIncompleteCleanupStatus expected)
        {
            Harness h = MakeHarness();
            h.Cleaner.Fail = cleanupFailed;
            NvencRunPublicationRecoveryIncompleteTerminalCoordinator coordinator = h.Terminal();

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsIncompleteReleased, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.False);
            Assert.That(terminal.IsStopped, Is.False);

            // The cleanup's own status is carried, never converted.
            Assert.That(terminal.IncompleteRelease.CleanupStatus, Is.EqualTo(expected));
            Assert.That(coordinator.CleanupResult.Status, Is.EqualTo(expected));
            Assert.That(
                coordinator.CleanupResult.Receipt,
                cleanupFailed ? Is.Null : Is.Not.Null);

            Assert.That(coordinator.IsComplete, Is.True);
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
        }

        private Harness MakeHarness(
            bool throwingFirstRelease = false, bool foreignReceipt = false)
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

            FakeCleaner cleaner = new FakeCleaner();
            FakeReleaser releaser = new FakeReleaser();
            if (foreignReceipt)
            {
                FakeReleaser foreign = new FakeReleaser();
                releaser.Forge = (self, operation) =>
                    NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                        foreign, operation);
            }

            return new Harness(
                layout, openOutcome, decision, owner, firstHandle, secondHandle, cleaner,
                releaser);
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
            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
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
        /// One incomplete Run holding its lease, with the cleanup and release
        /// boundaries this terminalization is wired to.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryDecision decision,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle,
                FakeCleaner cleaner,
                FakeReleaser releaser)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Decision = decision;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                Cleaner = cleaner;
                Releaser = releaser;

                Cleanup = new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                    new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner));
                ReleaseExecution =
                    new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                        releaser);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal NvencRunPublicationRecoveryDecision Decision { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakeCleaner Cleaner { get; }

            internal FakeReleaser Releaser { get; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator
                Cleanup { get; }

            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
                ReleaseExecution { get; }

            internal NvencRunPublicationRecoveryIncompleteTerminalCoordinator Terminal()
            {
                return new NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
                    Decision, Owner, Cleanup, ReleaseExecution);
            }
        }

        /// <summary>
        /// A cleaner that deletes nothing: it counts its calls, can report
        /// Failed, and can throw.
        /// </summary>
        private sealed class FakeCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            internal int CallCount { get; private set; }

            internal bool Fail { get; set; }

            internal Exception Throw { get; set; }

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                return Fail
                    ? NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        this, operation)
                    : NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        this, operation);
            }
        }

        /// <summary>
        /// A releaser that disposes the exact lease its operation carries once
        /// per call, and can hand back someone else's receipt.
        /// </summary>
        private sealed class FakeReleaser : INvencRunPublicationRecoveryIncompleteOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Func<
                FakeReleaser,
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation,
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt> Forge
            { get; set; }

            public NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
            {
                CallCount++;

                // The lease's own disposal, exactly once per call; a partial
                // failure propagates from here.
                operation.OwnershipLease.Dispose();

                return Forge != null
                    ? Forge(this, operation)
                    : NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                        this, operation);
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
