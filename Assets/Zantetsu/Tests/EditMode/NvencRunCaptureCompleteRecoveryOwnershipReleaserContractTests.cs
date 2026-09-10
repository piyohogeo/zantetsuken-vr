using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the production Phase 0.11 NVENC recovery ownership
    /// releaser: admission before the lease is touched, one disposal per call,
    /// the receipt of a completed release, and a real partial release that
    /// stays retryable.
    /// </summary>
    /// <remarks>
    /// Partial releases come from the ordinary API with a lock handle that
    /// fails its first release, never from rewriting private state.
    /// Exactly-once routing and the refusal of null, foreign, and unreleased
    /// receipts are fixed by the execution coordinator's fixture and are not
    /// repeated here.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryOwnershipReleaserContractTests
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
                () => new NvencRunCaptureCompleteRecoveryOwnershipReleaser().Release(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Release_AlreadyFullyReleasedOperation_RejectedWithoutTouchingTheHandles()
        {
            Harness h = MakeHarness();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            h.Owner.Dispose();
            Assert.That(h.Owner.IsReleaseComplete, Is.True);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureCompleteRecoveryOwnershipReleaser().Release(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
        }

        // ---- A first release ----

        [Test]
        public void Release_FirstAttempt_ReleasesTheLeaseOnceAndAttestsIt()
        {
            Harness h = MakeHarness();
            NvencRunCaptureCompleteRecoveryOwnershipReleaser releaser =
                new NvencRunCaptureCompleteRecoveryOwnershipReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(h.Owner.CanRelease, Is.False);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.Releaser, releaser), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);

            // A completed release ends the operation's admission validity while
            // its binding stays.
            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.IsBindingIntact, Is.True);
            Assert.That(operation.CanRelease, Is.False);
        }

        // ---- A real partial release ----

        [Test]
        public void Release_PartialFailure_PropagatesThenTheSameOperationRetriesOnce()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunCaptureCompleteRecoveryOwnershipReleaser releaser =
                new NvencRunCaptureCompleteRecoveryOwnershipReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            // The lease's own failure surfaces as it is, with no receipt.
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
        public void Release_AfterAPartialFailure_TheSameOperationSucceedsAndRetriesOnlyTheFailedHandle()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            NvencRunCaptureCompleteRecoveryOwnershipReleaser releaser =
                new NvencRunCaptureCompleteRecoveryOwnershipReleaser();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            Assert.Throws<AggregateException>(() => releaser.Release(operation));

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                releaser.Release(operation);

            // Only the handle that failed is touched again.
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
            NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation = h.Issue();

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult =
                operation.CleanupResult;
            NvencRunCaptureCompleteRecoveryCleanupOperation cleanupOperation =
                operation.CleanupOperation;

            new NvencRunCaptureCompleteRecoveryOwnershipReleaser().Release(operation);

            Assert.That(cleanupResult.Status, Is.EqualTo(h.CleanupResult.Status));
            Assert.That(ReferenceEquals(cleanupResult.Operation, h.CleanupResult.Operation),
                Is.True);
            Assert.That(ReferenceEquals(operation.CleanupOperation, cleanupOperation), Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True);
            Assert.That(operation.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
            Assert.That(operation.OpenOutcome.Session, Is.Null);
            Assert.That(ReferenceEquals(
                    operation.LockIdentityEvidence, h.LockIdentityEvidence),
                Is.True);
            Assert.That(operation.LockIdentityEvidence.IsBoundTo(h.Owner), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, h.Layout), Is.True);

            // The release is process-local: nothing was created on disk.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Releaser_IsSealedStatelessAndNonDisposable()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryOwnershipReleaser);

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

        private Harness MakeHarness(bool throwingFirstRelease = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            NvencRunPublicationRecoveryInspectionOperation recoveryOperation =
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout);
            CapturePublicationPlan plan = MakePlan(recoveryOperation);

            NvencRunPublicationRecoveryDecision recovery =
                NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        recoveryOperation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength)));

            NvencRunCaptureCompleteRecoveryReceipt captureComplete =
                new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                new FakeCommitter())),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCompleter()))
                    .Execute(NvencRunCaptureIndexRecoveryClassifier.Classify(
                        new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                            new NvencRunCaptureIndexRecoveryInspectionOperation(recovery),
                            Matches,
                            Absent)));

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult =
                new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                        new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                            new FakeCleaner()))
                    .Execute(captureComplete);

            return new Harness(
                layout,
                openOutcome,
                recoveryOperation.LockIdentityEvidence,
                cleanupResult,
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

        /// <summary>One recovered Run's cleanup result and the lease it holds.</summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunLockIdentityEvidence lockIdentityEvidence,
                NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                LockIdentityEvidence = lockIdentityEvidence;
                CleanupResult = cleanupResult;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunLockIdentityEvidence LockIdentityEvidence { get; }

            internal NvencRunCaptureCompleteRecoveryCleanupAttemptResult CleanupResult { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation Issue()
            {
                return NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation.Create(
                    CleanupResult, Owner);
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

        /// <summary>Returns the ordinary cleaned cleanup result.</summary>
        private sealed class FakeCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                return NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        /// <summary>Mints the ordinary commit receipt and touches no file.</summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
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
