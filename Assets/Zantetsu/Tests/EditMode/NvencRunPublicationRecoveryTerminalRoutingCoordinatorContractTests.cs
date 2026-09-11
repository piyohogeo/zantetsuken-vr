using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the terminal routing boundary: one branch chosen from
    /// the decision's disposition at construction, only that branch's
    /// collaborators ever entered, and no later failure sending the same Run
    /// down another branch.
    /// </summary>
    /// <remarks>
    /// Every collaborator is a counting fake that touches no file, and the lock
    /// handles are the counting seam the contract fixtures use; partial
    /// releases come from the ordinary API with a handle that fails its first
    /// release. The classification table, the commit modes, the cleanup
    /// deletion orders, and each receipt's own refusals belong to the fixtures
    /// that own them.
    /// </remarks>
    public class NvencRunPublicationRecoveryTerminalRoutingCoordinatorContractTests
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
        public void Constructor_NullArguments_Rejected()
        {
            Harness h = MakeHarness();

            foreach (string argument in new[]
            {
                "decision",
                "ownershipLease",
                "captureIndexRecovery",
                "captureComplete",
                "captureCompleteCleanup",
                "captureCompleteReleaseExecution",
                "incompleteCleanup",
                "incompleteReleaseExecution",
                "stopReleaseExecution",
            })
            {
                ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                    () => h.Route(h.Recoverable(), nullArgument: argument));
                Assert.That(ex.ParamName, Is.EqualTo(argument));
            }

            h.AssertNothingEntered("after every rejected construction");
        }

        [Test]
        public void Constructor_InvalidDecision_RejectedBeforeAnyBranchIsBuilt()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision recoverable = h.Recoverable();

            ReleaseAllLocks();
            Assert.That(recoverable.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => h.Route(recoverable));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));

            // No branch was entered. The handles were released by this test
            // itself to invalidate the decision, so they are not asserted
            // here.
            h.AssertNoCollaboratorEntered("after a refused decision");
        }

        // ---- One branch per disposition ----

        [Test]
        public void Route_PublicationRecoveryRequired_TakesTheCaptureCompleteBranchAlone()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryTerminalResult terminal =
                h.Route(h.Recoverable()).Execute();

            Assert.That(terminal.IsCaptureCompleted, Is.True);
            Assert.That(terminal.IsIncompleteReleased, Is.False);
            Assert.That(terminal.IsStopped, Is.False);

            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.CaptureCompleteReleaser.CallCount, Is.EqualTo(1));

            // No other branch was entered.
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Route_Incomplete_TakesTheOrphanCleanupBranchAlone()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryTerminalResult terminal =
                h.Route(h.Incomplete()).Execute();

            Assert.That(terminal.IsIncompleteReleased, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.False);
            Assert.That(terminal.IsStopped, Is.False);

            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(1));

            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Route_Collision_TakesTheStoppingBranchAlone()
        {
            AssertStops(deferred: false,
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void Route_DeferredVerification_TakesTheSameStoppingBranch()
        {
            AssertStops(deferred: true, NvencRunPublicationRecoveryDisposition.Deferred);
        }

        // ---- A failure never changes the branch ----

        [Test]
        public void Execute_AfterAPartialRelease_RetriesOnlyTheSelectedBranchsRelease()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                h.Route(h.Incomplete());

            AggregateException failure = Assert.Throws<AggregateException>(
                () => routing.Execute());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(routing.IsComplete, Is.False);
            Assert.That(routing.TerminalResult.IsValid, Is.False);

            NvencRunPublicationRecoveryTerminalResult terminal = routing.Execute();

            Assert.That(terminal.IsIncompleteReleased, Is.True);
            Assert.That(routing.IsComplete, Is.True);

            // The cleanup stayed at one call and only the release was retried.
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(2));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            // Nothing of the other branches was touched by the retry.
            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_WhenTheSelectedBranchStageThrows_NeverFallsBackToAnother()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("cleanup faulted");
            h.IncompleteCleaner.Throw = failure;
            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                h.Route(h.Incomplete());

            IOException thrown = Assert.Throws<IOException>(() => routing.Execute());

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(routing.IsComplete, Is.False);

            // The next call follows that branch's own refusal, and no other
            // branch is reached.
            h.IncompleteCleaner.Throw = null;
            Assert.Throws<InvalidOperationException>(() => routing.Execute());

            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_AfterTheTerminalValue_ChangesNothing()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                h.Route(h.Recoverable());

            NvencRunPublicationRecoveryTerminalResult first = routing.Execute();
            NvencRunPublicationRecoveryTerminalResult second = routing.Execute();

            Assert.That(
                ReferenceEquals(second.CaptureCompleteRelease, first.CaptureCompleteRelease),
                Is.True);
            Assert.That(routing.IsComplete, Is.True);
            Assert.That(
                ReferenceEquals(
                    routing.TerminalResult.CaptureCompleteRelease, first.CaptureCompleteRelease),
                Is.True);

            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.CaptureCompleteReleaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            // This boundary is filesystem-free.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        // ---- Shape ----

        [Test]
        public void Routing_HoldsOnlyReadonlyDecisionLeaseAndBranchReferences()
        {
            Type type = typeof(NvencRunPublicationRecoveryTerminalRoutingCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "routing holds no mutable state.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunPublicationRecoveryDecision),
                typeof(CaptureRunInitializationSessionOwnershipLease),
                typeof(NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator),
                typeof(NvencRunPublicationRecoveryIncompleteTerminalCoordinator),
                typeof(NvencRunPublicationRecoveryStopTerminalCoordinator),
            }));
        }

        // ---- Fixture helpers ----

        private void AssertStops(
            bool deferred, NvencRunPublicationRecoveryDisposition expected)
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision decision =
                deferred ? h.Deferred() : h.Collision();
            Assert.That(decision.Disposition, Is.EqualTo(expected));

            NvencRunPublicationRecoveryTerminalResult terminal = h.Route(decision).Execute();

            Assert.That(terminal.IsStopped, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.False);
            Assert.That(terminal.IsIncompleteReleased, Is.False);

            // The stopping disposition is carried, never converted.
            Assert.That(terminal.StopRelease.Disposition, Is.EqualTo(expected));

            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            // Nothing cleaned up, committed, or completed.
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(0));
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

            return new Harness(
                layout,
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
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
        /// One Run holding its lease, every branch's collaborators as counting
        /// fakes, and each classification this routing can be handed.
        /// </summary>
        private sealed class Harness
        {
            private readonly NvencRunPublicationRecoveryInspectionOperation _inspection;

            internal Harness(
                CaptureRunRootLayout layout,
                NvencRunPublicationRecoveryInspectionOperation inspection,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                _inspection = inspection;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

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

            /// <summary>
            /// The routing coordinator for one classification, optionally with
            /// one argument nulled out.
            /// </summary>
            internal NvencRunPublicationRecoveryTerminalRoutingCoordinator Route(
                NvencRunPublicationRecoveryDecision decision, string nullArgument = null)
            {
                return new NvencRunPublicationRecoveryTerminalRoutingCoordinator(
                    nullArgument == "decision" ? null : decision,
                    nullArgument == "ownershipLease" ? null : Owner,
                    nullArgument == "captureIndexRecovery"
                        ? null
                        : new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
                                CaptureIndexInspector)),
                    nullArgument == "captureComplete"
                        ? null
                        : new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                    Committer)),
                            new NvencRunCaptureCompleteRecoveryExecutionCoordinator(Completer)),
                    nullArgument == "captureCompleteCleanup"
                        ? null
                        : new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                            new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                                CaptureCompleteCleaner)),
                    nullArgument == "captureCompleteReleaseExecution"
                        ? null
                        : new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                            CaptureCompleteReleaser),
                    nullArgument == "incompleteCleanup"
                        ? null
                        : new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                            new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                                IncompleteCleaner)),
                    nullArgument == "incompleteReleaseExecution"
                        ? null
                        : new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                            IncompleteReleaser),
                    nullArgument == "stopReleaseExecution"
                        ? null
                        : new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                            StopReleaser));
            }

            internal void AssertNothingEntered(string message)
            {
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

            /// <summary>A canonical plan whose chunk verified.</summary>
            internal NvencRunPublicationRecoveryDecision Recoverable()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength)));
            }

            /// <summary>No finished plan, with the precommit temporary present.</summary>
            internal NvencRunPublicationRecoveryDecision Incomplete()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default));
            }

            /// <summary>A finished plan next to the precommit temporary.</summary>
            internal NvencRunPublicationRecoveryDecision Collision()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        MakePlan(_inspection),
                        true,
                        default));
            }

            /// <summary>A canonical plan whose chunk could not be verified.</summary>
            internal NvencRunPublicationRecoveryDecision Deferred()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Deferred,
                            CaptureArtifactVerificationStatus.None,
                            CaptureArtifactVerificationFailureReason.BufferUnavailable,
                            0)));
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

        /// <summary>
        /// Reports an already authoritative final index and touches no file.
        /// </summary>
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

            public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
            {
                CallCount++;
                operation.OwnershipLease.Dispose();

                return NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
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
