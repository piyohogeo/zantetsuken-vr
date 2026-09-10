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
    /// operation: which classifications may authorize a commit, what the
    /// operation forwards from that classification's graph, and how it follows
    /// the OS lock into invalidity.
    /// </summary>
    /// <remarks>
    /// Everything is a small in-memory graph; no real filesystem, sleep, or
    /// probabilistic repetition is used. The classification table itself is
    /// already fixed by the classifier's own fixture: the three commit modes
    /// are reached here through ordinary observations rather than repeated as a
    /// product.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryCommitOperationContractTests
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
        public void NullDecision_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryCommitOperation(null));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void CaptureCompleteOrCollisionDecision_CannotAuthorizeACommit()
        {
            NvencRunCaptureIndexRecoveryDecision captureComplete = Classify(Matches, Absent);
            NvencRunCaptureIndexRecoveryDecision collision = Classify(Matches, Mismatch);

            Assert.That(captureComplete.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));
            Assert.That(collision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision));

            ArgumentException captureCompleteEx = Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryCommitOperation(captureComplete));
            ArgumentException collisionEx = Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryCommitOperation(collision));

            Assert.That(captureCompleteEx.ParamName, Is.EqualTo("decision"));
            Assert.That(collisionEx.ParamName, Is.EqualTo("decision"));
        }

        [Test]
        public void DecisionWhoseLockWasReleased_CannotAuthorizeACommit()
        {
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Absent);

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryCommitOperation(decision));
        }

        // ---- The three commit modes ----

        [Test]
        public void NoFinalIndexAndNoTemporary_AuthorizesCreateTemporaryAndCommit()
        {
            AssertAuthorizes(
                Classify(Absent, Absent),
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit);
        }

        [Test]
        public void NoFinalIndexAndAuthoritativeTemporary_AuthorizesReuse()
        {
            AssertAuthorizes(
                Classify(Absent, Matches),
                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit);
        }

        [Test]
        public void NoFinalIndexAndInvalidTemporary_AuthorizesReplace()
        {
            AssertAuthorizes(
                Classify(Absent, Invalid),
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit);
        }

        // ---- Forwarding ----

        [Test]
        public void Operation_ForwardsTheExactDecisionGraph()
        {
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Matches);

            NvencRunCaptureIndexRecoveryCommitOperation operation =
                new NvencRunCaptureIndexRecoveryCommitOperation(decision);

            Assert.That(ReferenceEquals(operation.CaptureIndexRecoveryDecision, decision), Is.True);
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoverySnapshot, decision.Snapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoveryInspectionOperation, decision.Operation),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.PublicationRecoveryDecision,
                    decision.Operation.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(
                    operation.AuthoritativePlan, decision.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, decision.RootLayout), Is.True);
            Assert.That(operation.CommitMode, Is.EqualTo(decision.CommitMode));
            Assert.That(operation.TestRunId, Is.EqualTo(decision.TestRunId));
            Assert.That(ReferenceEquals(
                    operation.RunInitializationId, decision.RunInitializationId),
                Is.True);
            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void Issuance_ChangesNothingItWasGiven()
        {
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Invalid);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];
            NvencRunPublicationRecoveryDecision recovery =
                decision.Operation.PublicationRecoveryDecision;

            CapturePublicationPlan plan = decision.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;
            long chunkLength = chunk.ByteLength;

            new NvencRunCaptureIndexRecoveryCommitOperation(decision);

            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(decision.CommitMode, Is.EqualTo(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit));
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Invalid));
            Assert.That(recovery.IsValid, Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(chunk.ByteLength, Is.EqualTo(chunkLength));
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // Nothing was created on disk, in either Run root.
            Assert.That(Directory.Exists(decision.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(decision.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Operation_AfterLockRelease_BecomesInvalid()
        {
            NvencRunCaptureIndexRecoveryCommitOperation operation =
                new NvencRunCaptureIndexRecoveryCommitOperation(Classify(Absent, Absent));

            Assert.That(operation.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(operation.IsValid, Is.False);
            Assert.That(operation.CaptureIndexRecoveryDecision.IsValid, Is.False);
        }

        [Test]
        public void Operation_IsSealedNonDisposableAndHoldsOnlyTheDecision()
        {
            Type type = typeof(NvencRunCaptureIndexRecoveryCommitOperation);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].FieldType,
                Is.EqualTo(typeof(NvencRunCaptureIndexRecoveryDecision)));
        }

        // ---- Fixture helpers ----

        private static void AssertAuthorizes(
            NvencRunCaptureIndexRecoveryDecision decision,
            CaptureRunCaptureIndexCommitMode expected)
        {
            Assert.That(decision.CommitMode, Is.EqualTo(expected));

            NvencRunCaptureIndexRecoveryCommitOperation operation =
                new NvencRunCaptureIndexRecoveryCommitOperation(decision);

            Assert.That(operation.CommitMode, Is.EqualTo(expected));
            Assert.That(ReferenceEquals(operation.CaptureIndexRecoveryDecision, decision), Is.True);
            Assert.That(operation.IsValid, Is.True);
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

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
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
