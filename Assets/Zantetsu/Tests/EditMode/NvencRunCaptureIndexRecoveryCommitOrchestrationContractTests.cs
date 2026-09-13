using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC Capture Index recovery commit
    /// orchestration boundary: which classifications reach a committer at all,
    /// exactly one commit attempt, and the receipt correlation it returns.
    /// </summary>
    /// <remarks>
    /// The committer is a small fake that only mints the ordinary success
    /// receipt. The three commit modes' filesystem work belongs to the
    /// production committer's fixture, and the null, foreign, and invalid
    /// receipt refusals to the execution coordinator's; neither is repeated
    /// here.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryCommitOrchestrationContractTests
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

        // ---- Admission ----

        [Test]
        public void Constructor_NullCommitExecution_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("commitExecution"));
        }

        [Test]
        public void Execute_NullDecision_RejectedWithoutCommitterContact()
        {
            FakeCommitter committer = new FakeCommitter();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(committer).Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
            Assert.That(committer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_CaptureCompleteOrCollisionDecision_RejectedWithoutCommitterContact()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator coordinator =
                MakeCoordinator(committer);

            NvencRunCaptureIndexRecoveryDecision captureComplete = Classify(Matches, Absent);
            NvencRunCaptureIndexRecoveryDecision collision = Classify(Matches, Mismatch);

            Assert.That(captureComplete.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));
            Assert.That(collision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision));

            Assert.Throws<ArgumentException>(() => coordinator.Execute(captureComplete));
            Assert.Throws<ArgumentException>(() => coordinator.Execute(collision));
            Assert.That(committer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_DecisionWhoseLockWasReleased_RejectedWithoutCommitterContact()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Absent);

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(() => MakeCoordinator(committer).Execute(decision));
            Assert.That(committer.CallCount, Is.EqualTo(0));
        }

        // ---- Success ----

        [Test]
        public void Execute_CommitRequired_CommitsOnceAndReturnsTheCorrelatedReceipt()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitExecutionCoordinator execution =
                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer);
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Absent);

            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(execution)
                    .Execute(decision);

            Assert.That(committer.CallCount, Is.EqualTo(1));

            // The operation the committer was handed carries the exact input
            // decision, and the receipt belongs to that pair.
            NvencRunCaptureIndexRecoveryCommitOperation operation = committer.LastOperation;
            Assert.That(ReferenceEquals(operation.CaptureIndexRecoveryDecision, decision), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(committer, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.CaptureIndexRecoveryDecision, decision), Is.True);
            Assert.That(ReferenceEquals(receipt.Committer, execution.Committer), Is.True);
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Execute_ForwardsEachCommitModeUnchanged()
        {
            foreach (var expected in new[]
            {
                (Temporary: Absent,
                    Mode: CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit),
                (Temporary: Matches,
                    Mode: CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit),
                (Temporary: Invalid,
                    Mode: CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit),
            })
            {
                FakeCommitter committer = new FakeCommitter();
                NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, expected.Temporary);

                NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                    MakeCoordinator(committer).Execute(decision);

                Assert.That(decision.CommitMode, Is.EqualTo(expected.Mode));
                Assert.That(committer.LastOperation.CommitMode, Is.EqualTo(expected.Mode));
                Assert.That(receipt.CommitMode, Is.EqualTo(expected.Mode));
            }
        }

        // ---- Failure ----

        [Test]
        public void Execute_CommitterException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("commit failed");
            FakeCommitter committer = new FakeCommitter { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => MakeCoordinator(committer).Execute(Classify(Absent, Absent)));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(committer.CallCount, Is.EqualTo(1));
        }

        // ---- Purity and shape ----

        [Test]
        public void Execute_LeavesTheDecisionGraphAsTheEarlierObservation()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Invalid);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            CapturePublicationPlan plan = decision.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            MakeCoordinator(committer).Execute(decision);

            // A committed Capture Index does not rewrite what the inspection
            // saw; the snapshot stays the earlier observation.
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Invalid));
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(decision.CommitMode, Is.EqualTo(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit));
            Assert.That(decision.IsValid, Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(decision.Operation.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);
        }

        [Test]
        public void Coordinator_HoldsOnlyTheCommitExecutionCoordinator()
        {
            Type type = typeof(NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].FieldType,
                Is.EqualTo(typeof(NvencRunCaptureIndexRecoveryCommitExecutionCoordinator)));
        }

        // ---- Fixture helpers ----

        private static NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator MakeCoordinator(
            FakeCommitter committer)
        {
            return new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer));
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private NvencRunCaptureIndexRecoveryDecision Classify(
            NvencRunCaptureIndexObservationStatus finalIndex,
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            return NvencRunCaptureIndexRecoveryClassifier.Classify(
                new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    new NvencRunCaptureIndexRecoveryInspectionOperation(MakeRecoveryDecision()),
                    finalIndex,
                    temporaryIndex));
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
            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
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

        /// <summary>
        /// Records the operation it was handed and mints the ordinary success
        /// receipt. It performs no filesystem work of any kind.
        /// </summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunCaptureIndexRecoveryCommitOperation LastOperation
            {
                get;
                private set;
            }

            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
            }
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
