using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC publication recovery entry: one
    /// inspection and classification per call, the lease correlation settled
    /// before it, and a routing coordinator handed back without anything
    /// terminal having run.
    /// </summary>
    /// <remarks>
    /// The inspector is a counting fake that reports one classification and
    /// touches no file; the branch collaborators are counting fakes too, so
    /// what this fixture pins is that Begin alone enters none of them. The
    /// classification table and each terminal branch's own retry rules belong
    /// to the fixtures that own them.
    /// </remarks>
    public class NvencRunPublicationRecoveryEntryCoordinatorContractTests
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
        public void Constructor_NullDependencies_Rejected()
        {
            Harness h = MakeHarness();

            foreach (string argument in new[]
            {
                "publicationRecovery",
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
                    () => h.Entry(nullDependency: argument));
                Assert.That(ex.ParamName, Is.EqualTo(argument));
            }

            h.AssertNothingEntered("after every rejected construction");
        }

        // ---- Admission, before the inspection ----

        [Test]
        public void Begin_NullArguments_RejectedWithoutInspecting()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryEntryCoordinator entry = h.Entry();

            Assert.That(
                Assert.Throws<ArgumentNullException>(() => entry.Begin(null, h.Owner)).ParamName,
                Is.EqualTo("openOutcome"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(() => entry.Begin(h.OpenOutcome, null))
                    .ParamName,
                Is.EqualTo("ownershipLease"));

            h.AssertNothingEntered("after null arguments");
        }

        [Test]
        public void Begin_OutcomeInvalidatedByAReleasedLock_RejectedWithoutInspecting()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryEntryCoordinator entry = h.Entry();

            ReleaseAllLocks();
            Assert.That(h.OpenOutcome.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => entry.Begin(h.OpenOutcome, h.Owner));

            Assert.That(ex.ParamName, Is.EqualTo("openOutcome"));
            h.AssertNoCollaboratorEntered("after a refused outcome");
        }

        [Test]
        public void Begin_AnotherLeaseOverTheSameLayout_RejectedBeforeTheInspection()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryEntryCoordinator entry = h.Entry();

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

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => entry.Begin(h.OpenOutcome, foreignOwner));

            Assert.That(ex.ParamName, Is.EqualTo("ownershipLease"));

            // The filesystem was never reached, and no branch was built.
            h.AssertNothingEntered("after a foreign lease");
            Assert.That(foreignFirst.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignSecond.DisposeCallCount, Is.EqualTo(0));
            Assert.That(foreignOwner.IsCreated, Is.True);
        }

        // ---- One inspection, then the right branch ----

        [Test]
        public void Begin_ClassifiesOnceAndRoutesEachDispositionToItsBranch()
        {
            AssertRoutes(Shape.Recoverable, terminal => terminal.IsCaptureCompleted);
            AssertRoutes(Shape.Incomplete, terminal => terminal.IsIncompleteReleased);
            AssertRoutes(Shape.Collision, terminal => terminal.IsStopped);
            AssertRoutes(Shape.Deferred, terminal => terminal.IsStopped);
        }

        [Test]
        public void Begin_AloneRunsNoTerminalWork()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                h.Entry().Begin(h.OpenOutcome, h.Owner);

            Assert.That(routing, Is.Not.Null);
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));

            // The classification happened; nothing terminal did.
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureCompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(0));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.CaptureIndexInspector.CallCount, Is.EqualTo(0));

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
            Assert.That(routing.IsComplete, Is.False);
        }

        [Test]
        public void Begin_InspectionException_PropagatesSameReferenceWithoutReinspecting()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("inspection faulted");
            h.Inspector.Throw = failure;

            IOException thrown = Assert.Throws<IOException>(
                () => h.Entry().Begin(h.OpenOutcome, h.Owner));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            h.AssertNoCollaboratorEntered("after an inspection failure");
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Begin_LeavesItsInputsAndTheTreeAsTheyWere()
        {
            Harness h = MakeHarness();
            CaptureRunLockIdentityEvidence evidence = h.OpenOutcome.LockIdentityEvidence;

            h.Entry().Begin(h.OpenOutcome, h.Owner);

            Assert.That(h.OpenOutcome.IsValid, Is.True);
            Assert.That(h.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(h.OpenOutcome.Session, Is.Null);
            Assert.That(
                ReferenceEquals(h.OpenOutcome.LockIdentityEvidence, evidence), Is.True);
            Assert.That(ReferenceEquals(h.OpenOutcome.RootLayout, h.Layout), Is.True);

            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);

            // The inspector here reads nothing real, and this entry writes
            // nothing: no Run root was created.
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

        private void AssertRoutes(
            Shape shape, Func<NvencRunPublicationRecoveryTerminalResult, bool> isExpectedBranch)
        {
            Harness h = MakeHarness();
            h.Inspector.Shape = shape;

            NvencRunPublicationRecoveryTerminalRoutingCoordinator routing =
                h.Entry().Begin(h.OpenOutcome, h.Owner);

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1), shape.ToString());

            // The classification the routing coordinator holds is the one made
            // from this inspection's exact snapshot.
            Assert.That(
                ReferenceEquals(routing.Decision.Snapshot, h.Inspector.LastSnapshot), Is.True,
                shape.ToString());
            Assert.That(ReferenceEquals(routing.OwnershipLease, h.Owner), Is.True,
                shape.ToString());
            Assert.That(routing.Decision.Disposition, Is.EqualTo(h.Inspector.ExpectedDisposition),
                shape.ToString());

            // Which branch was selected shows in the terminal value it
            // produces; the entry itself ran none of it.
            NvencRunPublicationRecoveryTerminalResult terminal = routing.Execute();

            Assert.That(terminal.IsValid, Is.True, shape.ToString());
            Assert.That(isExpectedBranch(terminal), Is.True, shape.ToString());
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1),
                "the entry's inspection is not repeated by the branch.");
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private Harness MakeHarness()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            return new Harness(layout, openOutcome, owner, firstHandle, secondHandle);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
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
                            hasNonMarkerEntry: true)),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath);
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
        /// One Run holding its lease, with the publication recovery inspector
        /// and every branch collaborator as counting fakes.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
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

            /// <summary>
            /// The entry coordinator, optionally with one dependency nulled
            /// out.
            /// </summary>
            internal NvencRunPublicationRecoveryEntryCoordinator Entry(
                string nullDependency = null)
            {
                return new NvencRunPublicationRecoveryEntryCoordinator(
                    nullDependency == "publicationRecovery"
                        ? null
                        : new NvencRunPublicationRecoveryOrchestrationCoordinator(
                            new NvencRunPublicationRecoveryInspectionExecutionCoordinator(
                                Inspector)),
                    nullDependency == "captureIndexRecovery"
                        ? null
                        : new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
                                CaptureIndexInspector)),
                    nullDependency == "captureComplete"
                        ? null
                        : new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                    Committer)),
                            new NvencRunCaptureCompleteRecoveryExecutionCoordinator(Completer)),
                    nullDependency == "captureCompleteCleanup"
                        ? null
                        : new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                            new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                                CaptureCompleteCleaner)),
                    nullDependency == "captureCompleteReleaseExecution"
                        ? null
                        : new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                            CaptureCompleteReleaser),
                    nullDependency == "incompleteCleanup"
                        ? null
                        : new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                            new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                                IncompleteCleaner)),
                    nullDependency == "incompleteReleaseExecution"
                        ? null
                        : new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                            IncompleteReleaser),
                    nullDependency == "stopReleaseExecution"
                        ? null
                        : new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                            StopReleaser));
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

            internal NvencRunPublicationRecoveryDisposition ExpectedDisposition
            {
                get
                {
                    switch (Shape)
                    {
                        case Shape.Recoverable:
                            return NvencRunPublicationRecoveryDisposition
                                .PublicationRecoveryRequired;

                        case Shape.Incomplete:
                            return NvencRunPublicationRecoveryDisposition.Incomplete;

                        case Shape.Deferred:
                            return NvencRunPublicationRecoveryDisposition.Deferred;

                        default:
                            return NvencRunPublicationRecoveryDisposition
                                .PublicationRecoveryCollision;
                    }
                }
            }

            public NvencRunPublicationRecoveryInspectionSnapshot Inspect(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                // Recorded only so the fixture can confirm this exact
                // observation is the one that reached the routing coordinator.
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

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                CallCount++;

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

        /// <summary>A lock handle that counts its own releases.</summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private int _disposeCalls;

            internal CountingHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;
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
