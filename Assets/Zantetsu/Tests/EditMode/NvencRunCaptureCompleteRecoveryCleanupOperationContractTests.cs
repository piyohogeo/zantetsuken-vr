using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC recovery CaptureComplete
    /// cleanup operation: what it accepts, what it forwards from the accepted
    /// CaptureComplete graph, and how it follows the OS lock into invalidity.
    /// </summary>
    /// <remarks>
    /// Everything is a small in-memory graph, built through the ordinary
    /// orchestrations over fakes. The cleanup classification and the deletions
    /// themselves belong to later units and are not tested here.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryCleanupOperationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

        private const NvencRunCaptureIndexObservationStatus Matches =
            NvencRunCaptureIndexObservationStatus.MatchesAuthoritative;

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
        public void Create_NullReceipt_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryCleanupOperation.Create(null));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void Create_ReceiptWhoseLockWasReleased_Rejected()
        {
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteExistingFinal(Absent);

            ReleaseAllLocks();
            Assert.That(receipt.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
        }

        // ---- The two arrival paths ----

        [Test]
        public void ExistingFinalIndex_IsAcceptedAndCarriesNoCommitReceipt()
        {
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteExistingFinal(Absent);

            NvencRunCaptureCompleteRecoveryCleanupOperation operation =
                NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(ReferenceEquals(operation.CaptureCompleteReceipt, receipt), Is.True);
            Assert.That(operation.HasCommitReceipt, Is.False);
            Assert.That(operation.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(operation.CaptureIndexRecoveryDecision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));
            AssertForwardsTheGraphOf(operation, receipt);
        }

        [Test]
        public void CommittedIndex_IsAcceptedAndCarriesTheExactCommitReceipt()
        {
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteCommitted(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);

            NvencRunCaptureCompleteRecoveryCleanupOperation operation =
                NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(operation.CaptureIndexRecoveryDecision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            AssertForwardsTheGraphOf(operation, receipt);
        }

        [Test]
        public void ExistingFinalIndex_KeepsEveryObservedTemporaryStatusOnTheSnapshot()
        {
            // Absent, authoritative, and unusable temporaries all pass through
            // as the observation they were; none becomes a cleanup mode here.
            foreach (NvencRunCaptureIndexObservationStatus temporary in
                new[] { Absent, Matches, Invalid })
            {
                NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteExistingFinal(temporary);

                NvencRunCaptureCompleteRecoveryCleanupOperation operation =
                    NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt);

                Assert.That(operation.IsValid, Is.True, temporary.ToString());
                Assert.That(
                    operation.CaptureIndexRecoverySnapshot.TemporaryIndexStatus,
                    Is.EqualTo(temporary));
                Assert.That(
                    operation.CaptureIndexRecoverySnapshot.FinalIndexStatus,
                    Is.EqualTo(Matches));
                Assert.That(ReferenceEquals(
                        operation.CaptureIndexRecoverySnapshot,
                        receipt.CaptureIndexRecoveryDecision.Snapshot),
                    Is.True);
            }
        }

        // ---- Lock and purity ----

        [Test]
        public void Operation_AfterLockRelease_BecomesInvalid()
        {
            NvencRunCaptureCompleteRecoveryCleanupOperation fromExistingFinal =
                NvencRunCaptureCompleteRecoveryCleanupOperation.Create(
                    CompleteExistingFinal(Absent));
            NvencRunCaptureCompleteRecoveryCleanupOperation fromCommit =
                NvencRunCaptureCompleteRecoveryCleanupOperation.Create(CompleteCommitted(out _));

            Assert.That(fromExistingFinal.IsValid, Is.True);
            Assert.That(fromCommit.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(fromExistingFinal.IsValid, Is.False);
            Assert.That(fromCommit.IsValid, Is.False);
            Assert.That(fromCommit.CaptureCompleteOperation.IsValid, Is.False);
        }

        [Test]
        public void Issuance_ChangesNothingItWasGiven()
        {
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteCommitted(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            NvencRunCaptureIndexRecoveryDecision decision = receipt.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = receipt.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(commitReceipt.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(receipt.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // Nothing is deleted or even looked at on disk here.
            Assert.That(Directory.Exists(receipt.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(receipt.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Operation_IsSealedNonDisposableAndHoldsOnlyTheReceipt()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryCleanupOperation);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].FieldType,
                Is.EqualTo(typeof(NvencRunCaptureCompleteRecoveryReceipt)));

            // Issuance goes through the factory only.
            Assert.That(
                type.GetConstructors(BindingFlags.Instance | BindingFlags.Public),
                Is.Empty);
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheGraphOf(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation,
            NvencRunCaptureCompleteRecoveryReceipt receipt)
        {
            Assert.That(ReferenceEquals(
                    operation.CaptureCompleteOperation, receipt.Operation),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoveryDecision,
                    receipt.CaptureIndexRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoverySnapshot,
                    receipt.CaptureIndexRecoverySnapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.PublicationRecoveryDecision,
                    receipt.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(operation.AuthoritativePlan, receipt.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, receipt.RootLayout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(receipt.TestRunId));
            Assert.That(ReferenceEquals(
                    operation.RunInitializationId, receipt.RunInitializationId),
                Is.True);
            Assert.That(operation.HasCommitReceipt, Is.EqualTo(receipt.HasCommitReceipt));
        }

        /// <summary>
        /// Runs the ordinary CaptureComplete orchestration over fakes for a Run
        /// whose final Capture Index was already authoritative.
        /// </summary>
        private NvencRunCaptureCompleteRecoveryReceipt CompleteExistingFinal(
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            return MakeOrchestration().Execute(Classify(Matches, temporaryIndex));
        }

        /// <summary>
        /// Runs the ordinary CaptureComplete orchestration over fakes for a Run
        /// that committed its Capture Index first.
        /// </summary>
        private NvencRunCaptureCompleteRecoveryReceipt CompleteCommitted(
            out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt)
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureCompleteRecoveryReceipt receipt =
                new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCompleter()))
                    .Execute(Classify(Absent, Absent));

            commitReceipt = receipt.CaptureIndexRecoveryCommitReceipt;
            return receipt;
        }

        private static NvencRunCaptureCompleteRecoveryOrchestrationCoordinator MakeOrchestration()
        {
            return new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                    new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                        new FakeCommitter())),
                new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                    new NvencRunCaptureCompleteRecoveryCompleter()));
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

        /// <summary>Mints the ordinary commit receipt and touches no file.</summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
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
