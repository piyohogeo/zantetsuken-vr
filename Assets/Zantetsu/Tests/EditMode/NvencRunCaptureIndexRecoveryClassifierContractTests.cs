using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the pure Phase 0.11 NVENC Capture Index recovery
    /// boundary: which publication recovery decisions may authorize a Capture
    /// Index inspection at all, what an observation snapshot may hold, and the
    /// fixed classification over CommitRequired, CaptureCompleteRequired, and
    /// PublicationRecoveryCollision.
    /// </summary>
    /// <remarks>
    /// Everything is a small in-memory graph; no real filesystem, sleep,
    /// probabilistic repetition, or reflection over production private state is
    /// used. The table is fixed by representative rows rather than by a
    /// cartesian product of both statuses.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryClassifierContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

        private const NvencRunCaptureIndexObservationStatus Matches =
            NvencRunCaptureIndexObservationStatus.MatchesAuthoritative;

        private const NvencRunCaptureIndexObservationStatus Mismatch =
            NvencRunCaptureIndexObservationStatus.CanonicalMismatch;

        private const NvencRunCaptureIndexObservationStatus Invalid =
            NvencRunCaptureIndexObservationStatus.Invalid;

        private const NvencRunCaptureIndexObservationStatus LimitExceeded =
            NvencRunCaptureIndexObservationStatus.LimitExceeded;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }

            _owners.Clear();
        }

        // ---- Operation issuance ----

        [Test]
        public void Operation_ForwardsTheExactRecoveryDecision()
        {
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();

            NvencRunCaptureIndexRecoveryInspectionOperation operation =
                new NvencRunCaptureIndexRecoveryInspectionOperation(recovery);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(ReferenceEquals(operation.PublicationRecoveryDecision, recovery), Is.True);
            Assert.That(ReferenceEquals(operation.PublicationRecoverySnapshot, recovery.Snapshot),
                Is.True);

            // The plan is the very reference whose chunk was verified: nothing
            // is copied or re-serialized here.
            Assert.That(ReferenceEquals(operation.AuthoritativePlan, recovery.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.AuthoritativePlan, recovery.Snapshot.PublicationPlan),
                Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, recovery.RootLayout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(recovery.TestRunId));
            Assert.That(ReferenceEquals(
                    operation.RunInitializationId, recovery.RunInitializationId),
                Is.True);
        }

        [Test]
        public void Operation_NullDecision_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryInspectionOperation(null));

            Assert.That(ex.ParamName, Is.EqualTo("publicationRecoveryDecision"));
        }

        [Test]
        public void Operation_IncompleteDeferredOrCollisionDecision_Rejected()
        {
            foreach (NvencRunPublicationRecoveryDecision recovery in new[]
            {
                MakeIncompleteRecoveryDecision(),
                MakeDeferredRecoveryDecision(),
                MakeCollisionRecoveryDecision(),
            })
            {
                Assert.That(recovery.Disposition,
                    Is.Not.EqualTo(NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));

                Assert.Throws<ArgumentException>(
                    () => new NvencRunCaptureIndexRecoveryInspectionOperation(recovery),
                    "only a recoverable publication may reach the Capture Index.");
            }
        }

        [Test]
        public void OperationSnapshotAndDecision_BecomeInvalidAfterLockRelease()
        {
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Absent);
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            NvencRunCaptureIndexRecoveryInspectionOperation operation = snapshot.Operation;

            Assert.That(operation.IsValid, Is.True);
            Assert.That(snapshot.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(operation.IsValid, Is.False);
            Assert.That(snapshot.IsValid, Is.False);
            Assert.That(decision.IsValid, Is.False);
        }

        // ---- The classification table ----

        [Test]
        public void NoFinalIndexAndNoTemporary_CommitsByCreatingTheTemporary()
        {
            AssertCommit(
                Classify(Absent, Absent),
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit);
        }

        [Test]
        public void NoFinalIndexAndAuthoritativeTemporary_CommitsByReusingIt()
        {
            AssertCommit(
                Classify(Absent, Matches),
                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit);
        }

        [Test]
        public void NoFinalIndexAndInvalidTemporary_CommitsByReplacingIt()
        {
            AssertCommit(
                Classify(Absent, Invalid),
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit);
        }

        [Test]
        public void NoFinalIndexAndForeignOrOversizedTemporary_IsCollision()
        {
            AssertCollision(Classify(Absent, Mismatch));
            AssertCollision(Classify(Absent, LimitExceeded));
        }

        [Test]
        public void AuthoritativeFinalIndex_RequiresCaptureCompleteWithNoCommitMode()
        {
            foreach (NvencRunCaptureIndexObservationStatus temporary in
                new[] { Absent, Matches, Invalid })
            {
                NvencRunCaptureIndexRecoveryDecision decision = Classify(Matches, temporary);

                Assert.That(decision.Disposition, Is.EqualTo(
                    NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));

                // Whether a temporary is still lying around is already an
                // observed fact; it is never duplicated as a mode here.
                Assert.That(decision.CommitMode,
                    Is.EqualTo(CaptureRunCaptureIndexCommitMode.None));
                Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(temporary));
                Assert.That(decision.IsValid, Is.True);
            }
        }

        [Test]
        public void AuthoritativeFinalIndexWithForeignOrOversizedTemporary_IsCollision()
        {
            AssertCollision(Classify(Matches, Mismatch));
            AssertCollision(Classify(Matches, LimitExceeded));
        }

        [Test]
        public void ForeignInvalidOrOversizedFinalIndex_IsCollisionWhateverTheTemporary()
        {
            foreach (NvencRunCaptureIndexObservationStatus final in
                new[] { Mismatch, Invalid, LimitExceeded })
            {
                foreach (NvencRunCaptureIndexObservationStatus temporary in
                    new[] { Absent, Matches, Mismatch, Invalid, LimitExceeded })
                {
                    AssertCollision(Classify(final, temporary));
                }
            }
        }

        // ---- Snapshot and decision refusals ----

        [Test]
        public void Snapshot_NullOperationOrUnobservedStatus_Rejected()
        {
            NvencRunCaptureIndexRecoveryInspectionOperation operation = MakeOperation();

            Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryInspectionSnapshot(null, Absent, Absent));

            // None is "never observed", which is not a fact.
            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation, NvencRunCaptureIndexObservationStatus.None, Absent));
            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation, Absent, NvencRunCaptureIndexObservationStatus.None));

            // An undefined value is not an observation either.
            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation, (NvencRunCaptureIndexObservationStatus)99, Absent));
            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation, Absent, (NvencRunCaptureIndexObservationStatus)99));

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation, Absent, Absent));
        }

        [Test]
        public void Classify_NullOrInvalidSnapshot_Rejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureIndexRecoveryClassifier.Classify(null));

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                MakeSnapshot(Absent, Absent);

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureIndexRecoveryClassifier.Classify(snapshot));
        }

        // ---- Purity ----

        [Test]
        public void Classification_ChangesNothingAndRepeatsIdentically()
        {
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    new NvencRunCaptureIndexRecoveryInspectionOperation(recovery), Absent, Matches);

            CapturePublicationPlan plan = recovery.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;
            long chunkLength = chunk.ByteLength;

            NvencRunCaptureIndexRecoveryDecision first =
                NvencRunCaptureIndexRecoveryClassifier.Classify(snapshot);
            NvencRunCaptureIndexRecoveryDecision second =
                NvencRunCaptureIndexRecoveryClassifier.Classify(snapshot);

            // The same observation classifies the same way, every time.
            Assert.That(second.Disposition, Is.EqualTo(first.Disposition));
            Assert.That(second.CommitMode, Is.EqualTo(first.CommitMode));
            Assert.That(ReferenceEquals(second.Snapshot, snapshot), Is.True);

            // The plan, its chunk, the observed statuses, and the lock are all
            // exactly as they were: this boundary reads nothing and writes
            // nothing.
            Assert.That(ReferenceEquals(first.AuthoritativePlan, plan), Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(chunk.ByteLength, Is.EqualTo(chunkLength));
            Assert.That(snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(snapshot.TemporaryIndexStatus, Is.EqualTo(Matches));
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);
            Assert.That(recovery.IsValid, Is.True);

            // Nothing was created on disk, in either Run root.
            Assert.That(Directory.Exists(recovery.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(recovery.RootLayout.FinalRunRoot), Is.False);
        }

        // ---- Fixture helpers ----

        private static void AssertCommit(
            NvencRunCaptureIndexRecoveryDecision decision,
            CaptureRunCaptureIndexCommitMode expected)
        {
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(decision.CommitMode, Is.EqualTo(expected));
            Assert.That(decision.IsValid, Is.True);
        }

        private static void AssertCollision(NvencRunCaptureIndexRecoveryDecision decision)
        {
            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(decision.CommitMode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.None));
            Assert.That(decision.IsValid, Is.True);
        }

        private NvencRunCaptureIndexRecoveryDecision Classify(
            NvencRunCaptureIndexObservationStatus finalIndex,
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            return NvencRunCaptureIndexRecoveryClassifier.Classify(
                MakeSnapshot(finalIndex, temporaryIndex));
        }

        private NvencRunCaptureIndexRecoveryInspectionSnapshot MakeSnapshot(
            NvencRunCaptureIndexObservationStatus finalIndex,
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            return new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                MakeOperation(), finalIndex, temporaryIndex);
        }

        private NvencRunCaptureIndexRecoveryInspectionOperation MakeOperation()
        {
            return new NvencRunCaptureIndexRecoveryInspectionOperation(MakeRecoveryDecision());
        }

        private NvencRunPublicationRecoveryDecision MakeRecoveryDecision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeRecoveryOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            return NvencRunPublicationRecoveryClassifier.Classify(
                new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation,
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

        private NvencRunPublicationRecoveryDecision MakeIncompleteRecoveryDecision()
        {
            return NvencRunPublicationRecoveryClassifier.Classify(
                new NvencRunPublicationRecoveryInspectionSnapshot(
                    MakeRecoveryOperation(),
                    CaptureRunPublicationDocumentObservationStatus.Absent,
                    null,
                    false,
                    default));
        }

        private NvencRunPublicationRecoveryDecision MakeDeferredRecoveryDecision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeRecoveryOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            return NvencRunPublicationRecoveryClassifier.Classify(
                new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation,
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

        private NvencRunPublicationRecoveryDecision MakeCollisionRecoveryDecision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeRecoveryOperation();

            // A finished plan next to the NVENC precommit temporary.
            return NvencRunPublicationRecoveryClassifier.Classify(
                new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    MakePlan(operation),
                    true,
                    default));
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

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private NvencRunPublicationRecoveryInspectionOperation MakeRecoveryOperation()
        {
            CaptureRunRootLayout layout = MakeLayout();
            return new NvencRunPublicationRecoveryInspectionOperation(
                MakeRecoveryOutcome(layout), layout);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(CaptureRunRootLayout layout)
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
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
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

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            internal FakeHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
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
