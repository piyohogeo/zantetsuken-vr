using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the production releaser of a stopped publication
    /// recovery: admission before the lease is touched, one disposal per call,
    /// the receipt of a completed release, and a real partial release that
    /// stays retryable through the same operation.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state.
    /// Exactly-once routing and the refusal of null, foreign, and unreleased
    /// receipts belong to the execution coordinator's fixture and are not
    /// repeated here.
    /// </remarks>
    public class NvencRunPublicationRecoveryStopOwnershipReleaserContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

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

        // ---- Admission before the lease is touched ----

        [Test]
        public void Release_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaser().Release(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Release_AlreadyFullyReleasedOperation_RejectedWithoutTouchingTheHandles()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();

            h.Owner.Dispose();
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryStopOwnershipReleaser().Release(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        // ---- Both stopping shapes release the same way ----

        [Test]
        public void Release_Collision_ReleasesTheLeaseOnceAndAttestsIt()
        {
            AssertReleases(
                h => h.IssueCollision(),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void Release_DeferredVerification_ReleasesTheLeaseOnceAndAttestsIt()
        {
            AssertReleases(
                h => h.IssueDeferred(), NvencRunPublicationRecoveryDisposition.Deferred);
        }

        [Test]
        public void Receipt_ForwardsTheOperationGraph()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryStopOwnershipReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueDeferred();

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            Assert.That(ReferenceEquals(receipt.Releaser, releaser), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.Decision, operation.Decision), Is.True);
            Assert.That(ReferenceEquals(receipt.Snapshot, operation.Snapshot), Is.True);
            Assert.That(ReferenceEquals(receipt.OwnershipLease, h.Owner), Is.True);
            Assert.That(ReferenceEquals(receipt.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(
                    receipt.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(receipt.Disposition, Is.EqualTo(operation.Disposition));
            Assert.That(ReferenceEquals(receipt.RootLayout, h.Layout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, operation.RunInitializationId),
                Is.True);
        }

        // ---- A real partial release ----

        [Test]
        public void Release_PartialFailure_PropagatesUnwrappedWithoutAReceipt()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryStopOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryStopOwnershipReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();

            // The lease's own failure surfaces as it is: not caught, not
            // wrapped again, and no receipt.
            AggregateException failure = Assert.Throws<AggregateException>(
                () => releaser.Release(operation));

            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsCreated, Is.False);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);
            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.True);
            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void Release_AfterAPartialFailure_RetriesOnlyTheFailedHandle()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunPublicationRecoveryStopOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryStopOwnershipReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueDeferred();

            Assert.Throws<AggregateException>(() => releaser.Release(operation));

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
        }

        // ---- Purity and shape ----

        [Test]
        public void Release_ChangesNothingButTheLease()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = h.IssueCollision();
            NvencRunPublicationRecoveryDecision decision = operation.Decision;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = operation.Snapshot;

            new NvencRunPublicationRecoveryStopOwnershipReleaser().Release(operation);

            Assert.That(ReferenceEquals(operation.Decision, decision), Is.True);
            Assert.That(ReferenceEquals(operation.Snapshot, snapshot), Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(operation.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(operation.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);
            Assert.That(operation.LockIdentityEvidence.IsBoundTo(h.Owner), Is.True);

            // The release is process-local: nothing was created on disk.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Releaser_IsSealedStatelessAndNonDisposable()
        {
            Type type = typeof(NvencRunPublicationRecoveryStopOwnershipReleaser);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            Assert.That(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                Is.Empty,
                "the releaser must hold no instance state.");

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(
                    field.IsLiteral || field.IsInitOnly,
                    Is.True,
                    "the releaser must hold no mutable static state.");
            }
        }

        // ---- Fixture helpers ----

        private void AssertReleases(
            Func<Harness, NvencRunPublicationRecoveryStopOwnershipReleaseOperation> issue,
            NvencRunPublicationRecoveryDisposition expected)
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryStopOwnershipReleaser releaser =
                new NvencRunPublicationRecoveryStopOwnershipReleaser();
            NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation = issue(h);

            Assert.That(operation.Disposition, Is.EqualTo(expected));

            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(h.Owner.CanRelease, Is.False);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(receipt.Disposition, Is.EqualTo(expected));

            // A completed release ends the operation's admission validity while
            // its binding stays.
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.False);
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

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                CaptureRunRootRole.Staging,
                binding.StagingInitialization,
                binding.StagingReady,
                hasNonMarkerEntry: true);
            CaptureRunInitializationRootObservation final = MakeRootObservation(
                CaptureRunRootRole.Final,
                binding.FinalInitialization,
                binding.FinalReady,
                hasNonMarkerEntry: false);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(staging, final),
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

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        /// <summary>One Run holding its lease, able to stop in either shape.</summary>
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

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
                _inspection.LockIdentityEvidence;

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            /// <summary>A finished plan next to the NVENC precommit temporary.</summary>
            internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation IssueCollision()
            {
                return NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryClassifier.Classify(
                        new NvencRunPublicationRecoveryInspectionSnapshot(
                            _inspection,
                            CaptureRunPublicationDocumentObservationStatus.Canonical,
                            MakePlan(_inspection),
                            true,
                            default)),
                    Owner);
            }

            /// <summary>A canonical plan whose chunk could not be verified.</summary>
            internal NvencRunPublicationRecoveryStopOwnershipReleaseOperation IssueDeferred()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryStopOwnershipReleaseOperation.Create(
                    NvencRunPublicationRecoveryClassifier.Classify(
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
                                0))),
                    Owner);
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
