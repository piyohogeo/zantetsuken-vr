using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the recoverable-Run terminalization boundary: one
    /// CaptureComplete, one cleanup, a release retried through the single
    /// retention coordinator that cleanup produced, and the terminal result as
    /// the only completion latch.
    /// </summary>
    /// <remarks>
    /// The committer and the cleaner touch no file and the lock handles are the
    /// counting seam the contract fixtures use; partial releases come from the
    /// ordinary API with a handle that fails its first release, never from
    /// rewriting private state. The commit modes, the Capture Index
    /// classification, the cleanup's deletion order, and malformed intermediate
    /// results belong to the fixtures that own them.
    /// </remarks>
    public class NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinatorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

        private const NvencRunCaptureIndexObservationStatus Matches =
            NvencRunCaptureIndexObservationStatus.MatchesAuthoritative;

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
                Assert.Throws<ArgumentNullException>(() => h.Terminal(nullArgument: "decision"))
                    .ParamName,
                Is.EqualTo("decision"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => h.Terminal(nullArgument: "ownershipLease")).ParamName,
                Is.EqualTo("ownershipLease"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => h.Terminal(nullArgument: "captureIndexRecovery")).ParamName,
                Is.EqualTo("captureIndexRecovery"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => h.Terminal(nullArgument: "captureComplete")).ParamName,
                Is.EqualTo("captureComplete"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() => h.Terminal(nullArgument: "cleanup"))
                    .ParamName,
                Is.EqualTo("cleanup"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => h.Terminal(nullArgument: "releaseExecution")).ParamName,
                Is.EqualTo("releaseExecution"));
        }

        [Test]
        public void Constructor_TouchesNoStageAndNotTheLease()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            Assert.That(ReferenceEquals(coordinator.Decision, h.Decision), Is.True);
            Assert.That(ReferenceEquals(coordinator.OwnershipLease, h.Owner), Is.True);
            Assert.That(coordinator.CaptureCompleteReceipt, Is.Null);
            Assert.That(coordinator.ReleaseCoordinator, Is.Null);
            Assert.That(coordinator.IsCaptureCompletePrepared, Is.False);
            Assert.That(coordinator.IsCleanupPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.CleanupResult.IsValid, Is.False);

            Assert.That(h.Inspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
        }

        [Test]
        public void Constructor_DecisionInvalidatedByAReleasedLock_Rejected()
        {
            Harness h = MakeHarness();

            ReleaseAllLocks();
            Assert.That(h.Decision.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => h.Terminal());

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
            Assert.That(h.Inspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_AnotherLeaseOverTheSameLayout_RejectedBeforeAnySideEffect()
        {
            Harness h = MakeHarness();

            // A second lease over this Run's own layout: a different path set
            // instance, so it is not the lock this classification was made
            // under.
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(h.Layout);
            CountingHandle foreignFirst = new CountingHandle(pathSet.FirstLockPath);
            CountingHandle foreignSecond = new CountingHandle(pathSet.SecondLockPath);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet, foreignFirst, foreignSecond);
            CaptureRunInitializationSessionOwnershipLease foreignOwner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(foreignOwner);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Terminal(foreignOwnershipLease: foreignOwner));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));

            // No stage was entered at all.
            Assert.That(h.Inspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));

            // Neither lease was touched.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignFirst.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignSecond.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(foreignOwner.IsCreated, Is.True);

            // This boundary is filesystem-free, and the classification stands.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
            Assert.That(h.Decision.IsValid, Is.True);
        }

        // ---- Both Capture Index arrivals reach the terminal value ----

        [Test]
        public void Execute_WhenTheIndexIsAlreadyAuthoritative_ReachesTheTerminalValue()
        {
            Harness h = MakeHarness(finalIndex: Matches);

            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();
            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.True);

            // Nothing had to be committed on this arrival.
            Assert.That(coordinator.CaptureCompleteReceipt.HasCommitReceipt, Is.False);
            Assert.That(coordinator.CaptureCompleteReceipt.CaptureIndexRecoveryCommitReceipt,
                Is.Null);
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_WhenTheIndexHasToBeCommitted_KeepsThatCommitReceiptInTheGraph()
        {
            Harness h = MakeHarness(finalIndex: Absent);

            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();
            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsCaptureCompleted, Is.True);
            Assert.That(h.Committer.CallCount, Is.EqualTo(1));

            NvencRunCaptureIndexRecoveryCommitReceipt commit =
                coordinator.CaptureCompleteReceipt.CaptureIndexRecoveryCommitReceipt;
            Assert.That(commit, Is.Not.Null);
            Assert.That(coordinator.CaptureCompleteReceipt.HasCommitReceipt, Is.True);

            // The receipt names that exact committer. Its own validity rests
            // on the upstream operation, which the completed release has
            // already ended, so identity is what is pinned here.
            Assert.That(ReferenceEquals(commit.Committer, h.Committer), Is.True);
            Assert.That(
                ReferenceEquals(commit.PublicationRecoveryDecision, h.Decision), Is.True);

            // The same commit receipt is still the one the release operation's
            // graph carries.
            Assert.That(
                ReferenceEquals(
                    coordinator.ReleaseCoordinator.Operation.CleanupOperation
                        .CaptureIndexRecoveryCommitReceipt,
                    commit),
                Is.True);
        }

        [Test]
        public void Execute_WithEitherCleanupOutcome_ReleasesTheLeaseAndTerminalizes()
        {
            foreach (bool cleanupFailed in new[] { false, true })
            {
                Harness h = MakeHarness();
                h.Cleaner.Fail = cleanupFailed;

                NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                    h.Terminal();
                NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

                string because = cleanupFailed ? "failed cleanup" : "cleaned";
                Assert.That(terminal.IsValid, Is.True, because);
                Assert.That(terminal.IsCaptureCompleted, Is.True, because);

                // The cleanup's own status is carried, never converted.
                Assert.That(coordinator.CleanupResult.Status, Is.EqualTo(
                    cleanupFailed
                        ? NvencRunCaptureCompleteRecoveryCleanupStatus.Failed
                        : NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned),
                    because);
                Assert.That(terminal.CaptureCompleteRelease.CleanupStatus,
                    Is.EqualTo(coordinator.CleanupResult.Status), because);

                Assert.That(h.Owner.IsReleaseComplete, Is.True, because);
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1), because);
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1), because);
            }
        }

        [Test]
        public void TerminalResult_CarriesTheRetainedReceiptAndForwardsItsGraph()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                coordinator.ReleaseCoordinator.Receipt;
            Assert.That(receipt, Is.Not.Null);
            Assert.That(ReferenceEquals(terminal.CaptureCompleteRelease, receipt), Is.True);
            Assert.That(terminal.IncompleteRelease, Is.Null);
            Assert.That(terminal.StopRelease, Is.Null);

            // The cleanup result is read back through the release operation,
            // not from a second copy.
            Assert.That(
                ReferenceEquals(
                    coordinator.CleanupResult.CaptureCompleteReceipt,
                    coordinator.CaptureCompleteReceipt),
                Is.True);

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
        public void Execute_OnTheNormalPath_RunsEveryStageOnce()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            coordinator.Execute();

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsCaptureCompletePrepared, Is.True);
            Assert.That(coordinator.IsCleanupPrepared, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);
        }

        [Test]
        public void Execute_AfterTheTerminalValue_TouchesNoStageAgain()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            NvencRunPublicationRecoveryTerminalResult first = coordinator.Execute();
            NvencRunPublicationRecoveryTerminalResult second = coordinator.Execute();

            Assert.That(
                ReferenceEquals(second.CaptureCompleteRelease, first.CaptureCompleteRelease),
                Is.True);
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        // ---- A stage that threw is never repeated ----

        [Test]
        public void Execute_AfterACaptureCompleteException_RepeatsNeitherStage()
        {
            // The arrival that has to commit the Capture Index first, so the
            // completer's failure genuinely follows a committed index.
            Harness h = MakeHarness(finalIndex: Absent);
            IOException failure = new IOException("completer faulted");
            h.Completer.Throw = failure;
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Execute());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Committer.CallCount, Is.EqualTo(1),
                "the index was committed before the completer threw.");
            Assert.That(coordinator.CaptureCompleteReceipt, Is.Null);
            Assert.That(coordinator.IsCaptureCompletePrepared, Is.False);
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0), "the cleanup was never reached.");

            // That commit already happened, so the stage is not repeated: no
            // second inspection, no second commit, no second completion.
            h.Completer.Throw = null;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(coordinator.IsComplete, Is.False);
        }

        [Test]
        public void Execute_AfterACleanupException_RepeatsNeitherStage()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("cleanup faulted");
            h.Cleaner.Throw = failure;
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Execute());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));

            // The accepted CaptureComplete is kept; the release is not
            // prepared.
            Assert.That(coordinator.CaptureCompleteReceipt, Is.Not.Null);
            Assert.That(coordinator.IsCaptureCompletePrepared, Is.True);
            Assert.That(coordinator.IsCleanupPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);

            h.Cleaner.Throw = null;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        // ---- Only the release is retried ----

        [Test]
        public void Execute_AfterAPartialRelease_RetriesTheReleaseAloneAndSucceeds()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Execute());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.IsCleanupPrepared, Is.True);

            NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator releaseCoordinator =
                coordinator.ReleaseCoordinator;
            NvencRunCaptureCompleteRecoveryReceipt captureComplete =
                coordinator.CaptureCompleteReceipt;

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsCaptureCompleted, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);

            // Both upstream stages stayed at one call, through the same
            // retained objects.
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(2));
            Assert.That(
                ReferenceEquals(coordinator.ReleaseCoordinator, releaseCoordinator), Is.True);
            Assert.That(
                ReferenceEquals(coordinator.CaptureCompleteReceipt, captureComplete), Is.True);

            // Only the handle that had not been released is disposed again.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Execute_WhenAnUnusableReceiptFollowsACompletedRelease_StaysUnfinished()
        {
            Harness h = MakeHarness(foreignReceipt: true);
            NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator coordinator =
                h.Terminal();

            // The lease really is released by that call; only the receipt is
            // unusable.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);

            // No success is inferred, and no stage is entered again.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.Cleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsComplete, Is.False);
        }

        // ---- Fixture helpers ----

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private Harness MakeHarness(
            NvencRunCaptureIndexObservationStatus finalIndex = Matches,
            bool throwingFirstRelease = false,
            bool foreignReceipt = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            NvencRunPublicationRecoveryInspectionOperation inspection =
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout);
            CapturePublicationPlan plan = MakePlan(inspection);

            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength)));

            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));

            FakeReleaser releaser = new FakeReleaser();
            if (foreignReceipt)
            {
                FakeReleaser foreign = new FakeReleaser();
                releaser.Forge = (self, operation) =>
                    NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(
                        foreign, operation);
            }

            return new Harness(
                layout, openOutcome, decision, owner, firstHandle, secondHandle, finalIndex,
                releaser);
        }

        private static CapturePublicationPlan MakePlan(
            NvencRunPublicationRecoveryInspectionOperation operation)
        {
            CaptureArtifactDescriptor[] artifacts = new[]
            {
                NvencRunChunkArtifactDescriptorFactory.Create(ArtifactId, ChunkByteLength, Hash64),
            };

            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
            }

            return new CapturePublicationPlan(
                operation.TestRunId, operation.RunInitializationId, Hash64, artifacts, entries);
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
                            hasNonMarkerEntry: true)),
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
        /// One recoverable Run holding its lease, with every stage this
        /// terminalization is wired to.
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
                NvencRunCaptureIndexObservationStatus finalIndex,
                FakeReleaser releaser)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Decision = decision;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                Releaser = releaser;

                Inspector = new FakeCaptureIndexInspector(finalIndex);
                CaptureIndexRecovery = new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                    new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(Inspector));
                CaptureComplete = new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                    new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(Committer)),
                    new NvencRunCaptureCompleteRecoveryExecutionCoordinator(Completer));
                Cleanup = new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                    new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(Cleaner));
                ReleaseExecution =
                    new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                        releaser);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal NvencRunPublicationRecoveryDecision Decision { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakeCaptureIndexInspector Inspector { get; }

            internal FakeCommitter Committer { get; } = new FakeCommitter();

            internal FakeCompleter Completer { get; } = new FakeCompleter();

            internal FakeCleaner Cleaner { get; } = new FakeCleaner();

            internal FakeReleaser Releaser { get; }

            internal NvencRunCaptureIndexRecoveryOrchestrationCoordinator
                CaptureIndexRecovery { get; }

            internal NvencRunCaptureCompleteRecoveryOrchestrationCoordinator
                CaptureComplete { get; }

            internal NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator
                Cleanup { get; }

            internal NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
                ReleaseExecution { get; }

            /// <summary>
            /// The terminalization coordinator, optionally with one argument
            /// nulled out.
            /// </summary>
            internal NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator Terminal(
                string nullArgument = null,
                CaptureRunInitializationSessionOwnershipLease foreignOwnershipLease = null)
            {
                return new NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator(
                    nullArgument == "decision" ? null : Decision,
                    nullArgument == "ownershipLease" ? null : foreignOwnershipLease ?? Owner,
                    nullArgument == "captureIndexRecovery" ? null : CaptureIndexRecovery,
                    nullArgument == "captureComplete" ? null : CaptureComplete,
                    nullArgument == "cleanup" ? null : Cleanup,
                    nullArgument == "releaseExecution" ? null : ReleaseExecution);
            }
        }

        /// <summary>
        /// Reports the asked-for Capture Index observation and touches no file.
        /// </summary>
        private sealed class FakeCaptureIndexInspector : INvencRunCaptureIndexRecoveryInspector
        {
            private readonly NvencRunCaptureIndexObservationStatus _finalIndex;

            internal FakeCaptureIndexInspector(NvencRunCaptureIndexObservationStatus finalIndex)
            {
                _finalIndex = finalIndex;
            }

            internal int CallCount { get; private set; }

            public NvencRunCaptureIndexRecoveryInspectionSnapshot Inspect(
                NvencRunCaptureIndexRecoveryInspectionOperation operation)
            {
                CallCount++;

                return new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation, _finalIndex, Absent);
            }
        }

        /// <summary>Mints the ordinary commit receipt and touches no file.</summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            internal int CallCount { get; private set; }

            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                CallCount++;

                return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
            }
        }

        /// <summary>
        /// The ordinary CaptureComplete acceptance, with the option to throw
        /// the way a completer can after a commit already happened.
        /// </summary>
        private sealed class FakeCompleter : INvencRunCaptureCompleteRecoveryCompleter
        {
            internal int CallCount { get; private set; }

            internal Exception Throw { get; set; }

            public NvencRunCaptureCompleteRecoveryReceipt Complete(
                NvencRunCaptureCompleteRecoveryOperation operation)
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                return NvencRunCaptureCompleteRecoveryReceipt.Completed(this, operation);
            }
        }

        /// <summary>
        /// A cleaner that deletes nothing: it counts its calls, can report
        /// Failed, and can throw.
        /// </summary>
        private sealed class FakeCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            internal int CallCount { get; private set; }

            internal bool Fail { get; set; }

            internal Exception Throw { get; set; }

            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                return Fail
                    ? NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(this, operation)
                    : NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        /// <summary>
        /// A releaser that disposes the exact lease its operation carries once
        /// per call, and can hand back someone else's receipt.
        /// </summary>
        private sealed class FakeReleaser : INvencRunCaptureCompleteRecoveryOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Func<
                FakeReleaser,
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation,
                NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt> Forge
            { get; set; }

            public NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release(
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
            {
                CallCount++;

                // The lease's own disposal, exactly once per call; a partial
                // failure propagates from here.
                operation.OwnershipLease.Dispose();

                return Forge != null
                    ? Forge(this, operation)
                    : NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(
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
