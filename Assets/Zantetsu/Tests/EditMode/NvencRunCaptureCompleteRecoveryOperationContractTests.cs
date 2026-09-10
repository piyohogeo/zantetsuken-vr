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
    /// operation: the two authorities it accepts, their exclusivity, what it
    /// forwards from each graph, and how it follows the OS lock into
    /// invalidity.
    /// </summary>
    /// <remarks>
    /// Everything is a small in-memory graph; no real filesystem, sleep, or
    /// probabilistic repetition is used. The Capture Index classification
    /// table, the committer's per-mode filesystem work, and the receipt
    /// refusals are already fixed by their own fixtures and are not repeated
    /// here.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryOperationContractTests
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
        public void BothFactories_NullArgument_Rejected()
        {
            ArgumentNullException decisionEx = Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(null));
            ArgumentNullException receiptEx = Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(null));

            Assert.That(decisionEx.ParamName, Is.EqualTo("decision"));
            Assert.That(receiptEx.ParamName, Is.EqualTo("receipt"));
        }

        [Test]
        public void FromCaptureCompleteRequired_RejectsCommitRequiredAndCollision()
        {
            NvencRunCaptureIndexRecoveryDecision commitRequired = Classify(Absent, Absent);
            NvencRunCaptureIndexRecoveryDecision collision = Classify(Matches, Mismatch);

            Assert.That(commitRequired.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(collision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision));

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                    commitRequired));
            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                    collision));
        }

        [Test]
        public void FromCaptureCompleteRequired_DecisionWhoseLockWasReleased_Rejected()
        {
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Matches, Absent);

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                    decision));
        }

        [Test]
        public void FromCommittedIndex_ReceiptWhoseLockWasReleased_Rejected()
        {
            NvencRunCaptureIndexRecoveryCommitReceipt receipt = Commit(out _);

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(receipt));
        }

        // ---- The existing-final path ----

        [Test]
        public void ExistingFinalIndex_IsAcceptedAndCarriesNoReceipt()
        {
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Matches, Absent);

            NvencRunCaptureCompleteRecoveryOperation operation =
                NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(decision);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.HasCommitReceipt, Is.False);
            Assert.That(operation.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(ReferenceEquals(operation.CaptureIndexRecoveryDecision, decision), Is.True);
            AssertForwardsTheGraphOf(operation, decision);
        }

        // ---- The committed path ----

        [Test]
        public void CommittedIndex_IsAcceptedAndCarriesTheExactReceipt()
        {
            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                Commit(out NvencRunCaptureIndexRecoveryDecision decision);

            NvencRunCaptureCompleteRecoveryOperation operation =
                NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(receipt);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(operation.CaptureIndexRecoveryCommitReceipt, receipt),
                Is.True);

            // The classification is reached through the receipt rather than
            // stored a second time.
            Assert.That(ReferenceEquals(operation.CaptureIndexRecoveryDecision, decision), Is.True);
            Assert.That(operation.CaptureIndexRecoveryDecision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            AssertForwardsTheGraphOf(operation, decision);
        }

        [Test]
        public void EachPath_HoldsExactlyOneAuthority()
        {
            NvencRunCaptureIndexRecoveryDecision existingFinal = Classify(Matches, Absent);
            NvencRunCaptureCompleteRecoveryOperation fromExistingFinal =
                NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(existingFinal);

            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                Commit(out NvencRunCaptureIndexRecoveryDecision committed);
            NvencRunCaptureCompleteRecoveryOperation fromCommit =
                NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(receipt);

            // An already authoritative final index is never dressed up as a
            // commit, and a commit never loses its receipt.
            Assert.That(fromExistingFinal.HasCommitReceipt, Is.False);
            Assert.That(fromExistingFinal.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(fromExistingFinal.CaptureIndexRecoveryDecision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));

            Assert.That(fromCommit.HasCommitReceipt, Is.True);
            Assert.That(fromCommit.CaptureIndexRecoveryCommitReceipt, Is.Not.Null);
            Assert.That(ReferenceEquals(fromCommit.CaptureIndexRecoveryDecision, committed),
                Is.True);
            Assert.That(ReferenceEquals(fromCommit.CaptureIndexRecoveryDecision, existingFinal),
                Is.False);
        }

        // ---- Lock and purity ----

        [Test]
        public void BothPaths_AfterLockRelease_BecomeInvalid()
        {
            NvencRunCaptureCompleteRecoveryOperation fromExistingFinal =
                NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                    Classify(Matches, Absent));
            NvencRunCaptureCompleteRecoveryOperation fromCommit =
                NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(Commit(out _));

            Assert.That(fromExistingFinal.IsValid, Is.True);
            Assert.That(fromCommit.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(fromExistingFinal.IsValid, Is.False);
            Assert.That(fromCommit.IsValid, Is.False);
        }

        [Test]
        public void Issuance_ChangesNothingItWasGiven()
        {
            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                Commit(out NvencRunCaptureIndexRecoveryDecision decision);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            CapturePublicationPlan plan = decision.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(receipt);
            NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                Classify(Matches, Absent));

            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // This boundary is filesystem-free.
            Assert.That(Directory.Exists(decision.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(decision.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Operation_IsSealedNonDisposableAndHoldsTwoReadonlyReferences()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryOperation);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(2));

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held reference must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunCaptureIndexRecoveryDecision),
                typeof(NvencRunCaptureIndexRecoveryCommitReceipt),
            }));

            // Issuance goes through the two named factories only.
            Assert.That(
                type.GetConstructors(BindingFlags.Instance | BindingFlags.Public),
                Is.Empty);
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheGraphOf(
            NvencRunCaptureCompleteRecoveryOperation operation,
            NvencRunCaptureIndexRecoveryDecision decision)
        {
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoverySnapshot, decision.Snapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.PublicationRecoveryDecision,
                    decision.Operation.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(operation.AuthoritativePlan, decision.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, decision.RootLayout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(decision.TestRunId));
            Assert.That(ReferenceEquals(
                    operation.RunInitializationId, decision.RunInitializationId),
                Is.True);
        }

        /// <summary>
        /// Runs the ordinary commit orchestration over a fake committer, so the
        /// receipt is the real thing rather than a forged one.
        /// </summary>
        private NvencRunCaptureIndexRecoveryCommitReceipt Commit(
            out NvencRunCaptureIndexRecoveryDecision decision)
        {
            decision = Classify(Absent, Absent);

            return new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                    new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                        new FakeCommitter()))
                .Execute(decision);
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

        /// <summary>Mints the ordinary success receipt and touches no file.</summary>
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
