using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the top-level recovery coordinator: one entry, one
    /// retained routing, and every later call resuming inside the branch that
    /// was already chosen.
    /// </summary>
    /// <remarks>
    /// The publication recovery inspector and every branch collaborator are
    /// counting fakes that touch no file, and the lock handles are the counting
    /// seam the contract fixtures use; partial releases come from the ordinary
    /// API with a handle that fails its first release. The classification
    /// table, each branch's stages, and each receipt's own refusals belong to
    /// the fixtures that own them.
    /// </remarks>
    public class NvencRunPublicationRecoveryCoordinatorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

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
        public void Constructor_NullArguments_RejectedWithoutSideEffects()
        {
            Harness h = MakeHarness();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryCoordinator(
                        null, h.OpenOutcome, h.Owner)).ParamName,
                Is.EqualTo("entry"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryCoordinator(
                        h.Entry(), null, h.Owner)).ParamName,
                Is.EqualTo("openOutcome"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryCoordinator(
                        h.Entry(), h.OpenOutcome, null)).ParamName,
                Is.EqualTo("ownershipLease"));

            h.AssertNothingEntered("after every rejected construction");
        }

        [Test]
        public void Constructor_TouchesNeitherTheInspectionNorTheLease()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            Assert.That(ReferenceEquals(coordinator.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(coordinator.OwnershipLease, h.Owner), Is.True);
            Assert.That(coordinator.RoutingCoordinator, Is.Null);
            Assert.That(coordinator.IsRoutingPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);

            h.AssertNothingEntered("after construction");
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
        }

        // ---- The entry's admission still guards the first call ----

        [Test]
        public void Execute_AnotherLeaseOverTheSameLayout_RejectedBeforeTheInspection()
        {
            Harness h = MakeHarness();

            // A second live lease over this Run's own layout: a different path
            // set instance, so it is not the lock this outcome was opened
            // under.
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(h.Layout);
            CountingHandle foreignFirst = new CountingHandle(pathSet.FirstLockPath);
            CountingHandle foreignSecond = new CountingHandle(pathSet.SecondLockPath);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet, foreignFirst, foreignSecond);
            CaptureRunInitializationSessionOwnershipLease foreignOwner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(foreignOwner);

            NvencRunPublicationRecoveryCoordinator coordinator =
                new NvencRunPublicationRecoveryCoordinator(
                    h.Entry(), h.OpenOutcome, foreignOwner);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.Execute());

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));
            h.AssertNothingEntered("after a foreign lease");
            Assert.That(foreignFirst.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignSecond.DisposeCallCount, Is.EqualTo(0));
            Assert.That(coordinator.IsRoutingPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);
        }

        // ---- Every classification reaches the common terminal value ----

        [Test]
        public void Execute_EachDisposition_ReachesItsTerminalValue()
        {
            AssertReaches(Shape.Recoverable, terminal => terminal.IsCaptureCompleted);
            AssertReaches(Shape.Incomplete, terminal => terminal.IsIncompleteReleased);
            AssertReaches(Shape.Collision, terminal => terminal.IsStopped);
            AssertReaches(Shape.Deferred, terminal => terminal.IsStopped);
        }

        // ---- The entry runs at most once ----

        [Test]
        public void Execute_InspectionException_PropagatesThenRefusesASecondInspection()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("inspection faulted");
            h.Inspector.Throw = failure;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Execute());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsRoutingPrepared, Is.False);
            Assert.That(coordinator.IsComplete, Is.False);

            // One coordinator is one inspection attempt: the failed
            // observation is not re-read and re-classified under this lock.
            h.Inspector.Throw = null;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            h.AssertNoCollaboratorEntered("after a refused second entry");
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_AfterABranchStageException_DoesNotGoBackToTheEntry()
        {
            Harness h = MakeHarness();
            h.Inspector.Shape = Shape.Incomplete;
            IOException failure = new IOException("cleanup faulted");
            h.IncompleteCleaner.Throw = failure;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            IOException thrown = Assert.Throws<IOException>(() => coordinator.Execute());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsRoutingPrepared, Is.True, "the branch was already chosen.");
            Assert.That(coordinator.IsComplete, Is.False);

            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                coordinator.RoutingCoordinator;

            // The next call resumes inside that branch, which refuses to rerun
            // its own cleanup; the entry is not entered again either way.
            h.IncompleteCleaner.Throw = null;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(ReferenceEquals(coordinator.RoutingCoordinator, routing), Is.True);
        }

        // ---- Only the release is retried ----

        [Test]
        public void Execute_AfterAPartialRelease_RetriesTheSameBranchAndSucceeds()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            h.Inspector.Shape = Shape.Incomplete;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Execute());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);

            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                coordinator.RoutingCoordinator;

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsIncompleteReleased, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);

            // One inspection, one cleanup, and only the release retried,
            // through the routing that was already retained.
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(2));
            Assert.That(ReferenceEquals(coordinator.RoutingCoordinator, routing), Is.True);

            // Only the handle that had not been released is disposed again.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
        }

        [Test]
        public void Execute_WhenAnUnusableReceiptFollowsACompletedRelease_StaysUnfinished()
        {
            Harness h = MakeHarness(foreignStopReceipt: true);
            h.Inspector.Shape = Shape.Collision;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            // The lease really is released by that call; only the receipt is
            // unusable.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);

            // No success is inferred, and neither the entry nor the releaser is
            // reached again.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsComplete, Is.False);
        }

        // ---- After the terminal value ----

        [Test]
        public void Execute_AfterTheTerminalValue_ReturnsItWithoutReenteringAnything()
        {
            Harness h = MakeHarness();
            h.Inspector.Shape = Shape.Collision;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            NvencRunPublicationRecoveryTerminalResult first = coordinator.Execute();
            NvencRunPublicationRecoveryTerminalResult second = coordinator.Execute();

            Assert.That(ReferenceEquals(second.StopRelease, first.StopRelease), Is.True);
            Assert.That(
                ReferenceEquals(coordinator.TerminalResult.StopRelease, first.StopRelease),
                Is.True);
            Assert.That(coordinator.IsComplete, Is.True);

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            h.AssertOnlyStoppingBranchEntered();
        }

        [Test]
        public void Execute_ChangesNothingOfTheOutcomeOrDecisionGraphItself()
        {
            Harness h = MakeHarness();
            h.Inspector.Shape = Shape.Collision;
            CaptureRunLockIdentityEvidence evidence = h.OpenOutcome.LockIdentityEvidence;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(h.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(h.OpenOutcome.Session, Is.Null);
            Assert.That(
                ReferenceEquals(h.OpenOutcome.LockIdentityEvidence, evidence), Is.True);
            Assert.That(ReferenceEquals(h.OpenOutcome.RootLayout, h.Layout), Is.True);

            Assert.That(
                ReferenceEquals(
                    terminal.PublicationRecoveryDecision.Snapshot, h.Inspector.LastSnapshot),
                Is.True);
            Assert.That(
                ReferenceEquals(
                    terminal.PublicationRecoveryDecision.Snapshot.Operation.OpenOutcome,
                    h.OpenOutcome),
                Is.True);

            // This stopping branch changes no file, and this layer never
            // touches one itself.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        // ---- Fixture helpers ----

        private enum Shape
        {
            Recoverable,
            Incomplete,
            Collision,
            Deferred,
        }

        private void AssertReaches(
            Shape shape, Func<NvencRunPublicationRecoveryTerminalResult, bool> isExpectedBranch)
        {
            Harness h = MakeHarness();
            h.Inspector.Shape = shape;
            NvencRunPublicationRecoveryCoordinator coordinator = h.Coordinator();

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsValid, Is.True, shape.ToString());
            Assert.That(isExpectedBranch(terminal), Is.True, shape.ToString());
            Assert.That(coordinator.IsComplete, Is.True, shape.ToString());
            Assert.That(coordinator.IsRoutingPrepared, Is.True, shape.ToString());
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1), shape.ToString());

            // The terminal value carries this Run's exact inputs.
            Assert.That(
                ReferenceEquals(terminal.OpenOutcome, h.OpenOutcome), Is.True, shape.ToString());
            Assert.That(
                ReferenceEquals(terminal.OwnershipLease, h.Owner), Is.True, shape.ToString());
            Assert.That(h.Owner.IsReleaseComplete, Is.True, shape.ToString());
        }

        private Harness MakeHarness(
            bool throwingFirstRelease = false, bool foreignStopReceipt = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            return new Harness(
                layout, openOutcome, owner, firstHandle, secondHandle, foreignStopReceipt);
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
        /// One Run holding its lease, with the entry's inspector and every
        /// branch collaborator as counting fakes.
        /// </summary>
        private sealed class Harness
        {
            private readonly bool _foreignStopReceipt;

            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle,
                bool foreignStopReceipt)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                _foreignStopReceipt = foreignStopReceipt;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakePublicationRecoveryInspector Inspector { get; } =
                new FakePublicationRecoveryInspector();

            internal FakeCaptureIndexInspector CaptureIndexInspector { get; } =
                new FakeCaptureIndexInspector();

            internal FakeCommitter Committer { get; } = new FakeCommitter();

            internal FakeCompleter Completer { get; } = new FakeCompleter();

            internal FakeCaptureCompleteCleaner CaptureCompleteCleaner { get; } =
                new FakeCaptureCompleteCleaner();

            internal FakeCaptureCompleteReleaser CaptureCompleteReleaser { get; } =
                new FakeCaptureCompleteReleaser();

            internal FakeIncompleteCleaner IncompleteCleaner { get; } =
                new FakeIncompleteCleaner();

            internal FakeIncompleteReleaser IncompleteReleaser { get; } =
                new FakeIncompleteReleaser();

            internal FakeStopReleaser StopReleaser { get; } = new FakeStopReleaser();

            internal NvencRunPublicationRecoveryEntryCoordinator Entry()
            {
                if (_foreignStopReceipt)
                {
                    FakeStopReleaser foreign = new FakeStopReleaser();
                    StopReleaser.Forge = (self, operation) =>
                        NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                            foreign, operation);
                }

                return new NvencRunPublicationRecoveryEntryCoordinator(
                    new NvencRunPublicationRecoveryOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryInspectionExecutionCoordinator(Inspector)),
                    new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
                            CaptureIndexInspector)),
                    new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                Committer)),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(Completer)),
                    new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                        new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                            CaptureCompleteCleaner)),
                    new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                        CaptureCompleteReleaser),
                    new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                        new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                            IncompleteCleaner)),
                    new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                        IncompleteReleaser),
                    new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                        StopReleaser));
            }

            internal NvencRunPublicationRecoveryCoordinator Coordinator()
            {
                return new NvencRunPublicationRecoveryCoordinator(Entry(), OpenOutcome, Owner);
            }

            internal void AssertNothingEntered(string message)
            {
                Assert.That(Inspector.CallCount, Is.EqualTo(0), message);
                AssertNoCollaboratorEntered(message);
                Assert.That(FirstHandle.DisposeCallCount, Is.EqualTo(0), message);
                Assert.That(SecondHandle.DisposeCallCount, Is.EqualTo(0), message);
            }

            internal void AssertNoCollaboratorEntered(string message)
            {
                Assert.That(CaptureIndexInspector.CallCount, Is.EqualTo(0), message);
                Assert.That(Committer.CallCount, Is.EqualTo(0), message);
                Assert.That(Completer.CallCount, Is.EqualTo(0), message);
                Assert.That(CaptureCompleteCleaner.CallCount, Is.EqualTo(0), message);
                Assert.That(CaptureCompleteReleaser.CallCount, Is.EqualTo(0), message);
                Assert.That(IncompleteCleaner.CallCount, Is.EqualTo(0), message);
                Assert.That(IncompleteReleaser.CallCount, Is.EqualTo(0), message);
                Assert.That(StopReleaser.CallCount, Is.EqualTo(0), message);
            }

            internal void AssertOnlyStoppingBranchEntered()
            {
                Assert.That(CaptureIndexInspector.CallCount, Is.EqualTo(0));
                Assert.That(Committer.CallCount, Is.EqualTo(0));
                Assert.That(Completer.CallCount, Is.EqualTo(0));
                Assert.That(CaptureCompleteCleaner.CallCount, Is.EqualTo(0));
                Assert.That(CaptureCompleteReleaser.CallCount, Is.EqualTo(0));
                Assert.That(IncompleteCleaner.CallCount, Is.EqualTo(0));
                Assert.That(IncompleteReleaser.CallCount, Is.EqualTo(0));
            }
        }

        /// <summary>
        /// Reports one classification shape for this Run and touches no file.
        /// </summary>
        private sealed class FakePublicationRecoveryInspector
            : INvencRunPublicationRecoveryInspector
        {
            internal int CallCount { get; private set; }

            internal Exception Throw { get; set; }

            internal Shape Shape { get; set; } = Shape.Recoverable;

            internal NvencRunPublicationRecoveryInspectionSnapshot LastSnapshot
            {
                get;
                private set;
            }

            public NvencRunPublicationRecoveryInspectionSnapshot Inspect(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                LastSnapshot = MakeSnapshot(operation);
                return LastSnapshot;
            }

            private NvencRunPublicationRecoveryInspectionSnapshot MakeSnapshot(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                if (Shape == Shape.Incomplete)
                {
                    return new NvencRunPublicationRecoveryInspectionSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default);
                }

                CapturePublicationPlan plan = MakePlan(operation);

                if (Shape == Shape.Collision)
                {
                    return new NvencRunPublicationRecoveryInspectionSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        true,
                        default);
                }

                return new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan,
                    false,
                    Shape == Shape.Deferred
                        ? new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Deferred,
                            CaptureArtifactVerificationStatus.None,
                            CaptureArtifactVerificationFailureReason.BufferUnavailable,
                            0)
                        : new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength));
            }

            private static CapturePublicationPlan MakePlan(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CaptureArtifactDescriptor[] artifacts = new[]
                {
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        ArtifactId, ChunkByteLength, Hash64),
                };

                CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
                }

                return new CapturePublicationPlan(
                    operation.TestRunId, operation.RunInitializationId, Hash64, artifacts, entries);
            }
        }

        private sealed class FakeCaptureIndexInspector : INvencRunCaptureIndexRecoveryInspector
        {
            internal int CallCount { get; private set; }

            public NvencRunCaptureIndexRecoveryInspectionSnapshot Inspect(
                NvencRunCaptureIndexRecoveryInspectionOperation operation)
            {
                CallCount++;

                return new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation,
                    NvencRunCaptureIndexObservationStatus.MatchesAuthoritative,
                    NvencRunCaptureIndexObservationStatus.Absent);
            }
        }

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

        private sealed class FakeCompleter : INvencRunCaptureCompleteRecoveryCompleter
        {
            internal int CallCount { get; private set; }

            public NvencRunCaptureCompleteRecoveryReceipt Complete(
                NvencRunCaptureCompleteRecoveryOperation operation)
            {
                CallCount++;

                return NvencRunCaptureCompleteRecoveryReceipt.Completed(this, operation);
            }
        }

        private sealed class FakeCaptureCompleteCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            internal int CallCount { get; private set; }

            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                CallCount++;

                return NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(
                    this, operation);
            }
        }

        private sealed class FakeIncompleteCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            internal int CallCount { get; private set; }

            internal Exception Throw { get; set; }

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                    this, operation);
            }
        }

        private sealed class FakeCaptureCompleteReleaser
            : INvencRunCaptureCompleteRecoveryOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            public NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release(
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
            {
                CallCount++;
                operation.OwnershipLease.Dispose();

                return NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(
                    this, operation);
            }
        }

        private sealed class FakeIncompleteReleaser
            : INvencRunPublicationRecoveryIncompleteOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            public NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
            {
                CallCount++;
                operation.OwnershipLease.Dispose();

                return NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                    this, operation);
            }
        }

        private sealed class FakeStopReleaser : INvencRunPublicationRecoveryStopOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Func<
                FakeStopReleaser,
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation,
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt> Forge
            { get; set; }

            public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
            {
                CallCount++;
                operation.OwnershipLease.Dispose();

                return Forge != null
                    ? Forge(this, operation)
                    : NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
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
