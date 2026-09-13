using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the stopping recovery terminalization boundary: one
    /// release driven to the common terminal value, that value as the only
    /// completion latch, and a retry through the same retained release after a
    /// partial one.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state. The
    /// retention coordinator's and the terminal result's own rules stay with
    /// the fixtures that own them, and the filesystem untouchedness of these
    /// paths is fixed by the managed end-to-end test, so no real tree is built
    /// here.
    /// </remarks>
    public class NvencRunPublicationRecoveryStopTerminalCoordinatorContractTests
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
        public void Constructor_NullReleaseCoordinator_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryStopTerminalCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("releaseCoordinator"));
        }

        [Test]
        public void Constructor_TouchesNeitherTheReleaserNorTheLease()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryStopTerminalCoordinator coordinator =
                new NvencRunPublicationRecoveryStopTerminalCoordinator(h.ReleaseCoordinator(false));

            Assert.That(
                ReferenceEquals(coordinator.ReleaseCoordinator, h.LastReleaseCoordinator),
                Is.True);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);
            Assert.That(coordinator.TerminalResult.StopRelease, Is.Null);

            Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
        }

        // ---- Both stopping shapes terminalize the same way ----

        [Test]
        public void Execute_Collision_ReturnsAStoppedTerminalResult()
        {
            AssertTerminalizes(
                deferred: false,
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void Execute_DeferredVerification_ReturnsAStoppedTerminalResult()
        {
            AssertTerminalizes(
                deferred: true, NvencRunPublicationRecoveryDisposition.Deferred);
        }

        [Test]
        public void TerminalResult_CarriesTheExactReceiptAndForwardsItsGraph()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopTerminalCoordinator coordinator =
                new NvencRunPublicationRecoveryStopTerminalCoordinator(h.ReleaseCoordinator(false));

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                coordinator.ReleaseCoordinator.Receipt;
            Assert.That(receipt, Is.Not.Null);
            Assert.That(ReferenceEquals(terminal.StopRelease, receipt), Is.True);
            Assert.That(terminal.CaptureCompleteRelease, Is.Null);
            Assert.That(terminal.IncompleteRelease, Is.Null);

            // One graph, read through that receipt.
            Assert.That(
                ReferenceEquals(terminal.PublicationRecoveryDecision, h.LastDecision), Is.True);
            Assert.That(
                ReferenceEquals(terminal.Snapshot, h.LastDecision.Snapshot), Is.True);
            Assert.That(ReferenceEquals(terminal.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(terminal.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(terminal.RootLayout, h.Layout), Is.True);
            Assert.That(terminal.TestRunId, Is.EqualTo(h.Layout.TestRunId));
            Assert.That(terminal.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Execute_AfterTheTerminalValue_ReturnsItWithoutTouchingTheReleaser()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopTerminalCoordinator coordinator =
                new NvencRunPublicationRecoveryStopTerminalCoordinator(h.ReleaseCoordinator(false));

            NvencRunPublicationRecoveryTerminalResult first = coordinator.Execute();
            NvencRunPublicationRecoveryTerminalResult second = coordinator.Execute();

            Assert.That(
                ReferenceEquals(second.StopRelease, first.StopRelease), Is.True);
            Assert.That(second.IsStopped, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);

            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        // ---- Retry after a partial release ----

        [Test]
        public void Execute_AfterAPartialRelease_PropagatesThenSucceedsOnTheNextCall()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryStopTerminalCoordinator coordinator =
                new NvencRunPublicationRecoveryStopTerminalCoordinator(h.ReleaseCoordinator(false));

            AggregateException failure = Assert.Throws<AggregateException>(
                () => coordinator.Execute());

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);
            Assert.That(coordinator.TerminalResult.StopRelease, Is.Null);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsStopped, Is.True);
            Assert.That(coordinator.IsComplete, Is.True);
            Assert.That(h.Releaser.CallCount, Is.EqualTo(2));

            // Only the handle that had not been released is disposed again.
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
        }

        // ---- A released lock is not a terminal value on its own ----

        [Test]
        public void Execute_WhenAnUnusableReceiptFollowsACompletedRelease_StaysUnfinished()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopTerminalCoordinator coordinator =
                new NvencRunPublicationRecoveryStopTerminalCoordinator(h.ReleaseCoordinator(true));

            // The lease really is released by that call; only the receipt is
            // unusable.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(coordinator.IsComplete, Is.False);
            Assert.That(coordinator.TerminalResult.IsValid, Is.False);
            Assert.That(coordinator.TerminalResult.StopRelease, Is.Null);

            // The next call infers no success from the released lock, and the
            // releaser is never reached again.
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(coordinator.IsComplete, Is.False);
        }

        // ---- Fixture helpers ----

        private void AssertTerminalizes(
            bool deferred, NvencRunPublicationRecoveryDisposition expected)
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopTerminalCoordinator coordinator =
                new NvencRunPublicationRecoveryStopTerminalCoordinator(
                    h.ReleaseCoordinator(false, deferred));

            NvencRunPublicationRecoveryTerminalResult terminal = coordinator.Execute();

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsStopped, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.False);
            Assert.That(terminal.IsIncompleteReleased, Is.False);
            Assert.That(terminal.StopRelease.Disposition, Is.EqualTo(expected));

            Assert.That(coordinator.IsComplete, Is.True);
            Assert.That(
                ReferenceEquals(coordinator.TerminalResult.StopRelease, terminal.StopRelease),
                Is.True);

            Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
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
                openOutcome,
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
        /// One stopped Run holding its lease, able to build the retention
        /// coordinator this boundary terminalizes.
        /// </summary>
        private sealed class Harness
        {
            private readonly NvencRunPublicationRecoveryInspectionOperation _inspection;

            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryInspectionOperation inspection,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                _inspection = inspection;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakeReleaser Releaser { get; } = new FakeReleaser();

            internal NvencRunPublicationRecoveryDecision LastDecision { get; private set; }

            internal NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator
                LastReleaseCoordinator { get; private set; }

            /// <summary>
            /// The retention coordinator for this Run, optionally wired to a
            /// releaser whose receipt is another releaser's.
            /// </summary>
            internal NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator ReleaseCoordinator(
                bool forgeForeignReceipt, bool deferred = false)
            {
                if (forgeForeignReceipt)
                {
                    FakeReleaser foreign = new FakeReleaser();
                    Releaser.Forge = (self, operation) =>
                        NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                            foreign, operation);
                }

                LastDecision = deferred ? Deferred() : Collision();
                LastReleaseCoordinator =
                    new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                        LastDecision,
                        Owner,
                        new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                            Releaser));

                return LastReleaseCoordinator;
            }

            /// <summary>A finished plan next to the NVENC precommit temporary.</summary>
            private NvencRunPublicationRecoveryDecision Collision()
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
            private NvencRunPublicationRecoveryDecision Deferred()
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
        /// A releaser that disposes the exact lease its operation carries once
        /// per call, and can hand back someone else's receipt.
        /// </summary>
        private sealed class FakeReleaser : INvencRunPublicationRecoveryStopOwnershipReleaser
        {
            internal int CallCount { get; private set; }

            internal Func<
                FakeReleaser,
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation,
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt> Forge
            { get; set; }

            public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
            {
                CallCount++;

                // The lease's own disposal, exactly once per call; a partial
                // failure propagates from here.
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
