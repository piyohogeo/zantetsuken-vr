using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the pure Phase 0.11 NVENC publication recovery
    /// classification: the inspection operation's issuance conditions, the
    /// observation snapshot's consistency rules, and the fixed classification
    /// cascade over Incomplete, PublicationRecoveryRequired,
    /// PublicationRecoveryCollision, and Deferred. Everything is a small
    /// in-memory graph; no real filesystem, sleep, probabilistic repetition, or
    /// reflection over production private state is used.
    /// </summary>
    public class NvencRunPublicationRecoveryClassifierContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string ForeignInitId = "fedcba9876543210fedcba9876543210";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string OtherHash64 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

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
        public void Operation_NullArguments_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();

            Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryInspectionOperation(null, layout));
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryInspectionOperation(
                    MakeRecoveryOutcome(layout), null));
        }

        [Test]
        public void Operation_ForeignRootLayout_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationOpenOutcome outcome = MakeRecoveryOutcome(layout);

            // A content-identical but distinct layout is a foreign Run.
            Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryInspectionOperation(outcome, MakeLayout()));
        }

        [Test]
        public void Operation_RecoveryOutcomeWithHeldLock_IsValidAndTouchesNothing()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationOpenOutcome outcome = MakeRecoveryOutcome(layout);

            NvencRunPublicationRecoveryInspectionOperation operation =
                new NvencRunPublicationRecoveryInspectionOperation(outcome, layout);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(ReferenceEquals(operation.OpenOutcome, outcome), Is.True);
            Assert.That(ReferenceEquals(operation.RootLayout, layout), Is.True);
            Assert.That(operation.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(operation.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(operation.LockIdentityEvidence, Is.Not.Null);
            Assert.That(operation.LockIdentityEvidence.IsValid, Is.True);

            // Nothing was created on disk: this boundary only reads references.
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Operation_AfterLockRelease_BecomesInvalid()
        {
            CaptureRunRootLayout layout = MakeLayout();
            NvencRunPublicationRecoveryInspectionOperation operation =
                MakeOperation(layout, out CaptureRunInitializationSessionOwnershipLease owner);
            Assert.That(operation.IsValid, Is.True);

            owner.Dispose();

            Assert.That(operation.IsValid, Is.False);
        }

        // ---- Incomplete ----

        [Test]
        public void NoPlanAndNoTemporary_IsIncomplete()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            AssertDisposition(
                MakeSnapshot(operation, CaptureRunPublicationDocumentObservationStatus.Absent),
                NvencRunPublicationRecoveryDisposition.Incomplete);
        }

        [Test]
        public void OnlyTheNvencTemporary_IsIncomplete()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Absent,
                    temporaryPresent: true),
                NvencRunPublicationRecoveryDisposition.Incomplete);
        }

        // ---- PublicationRecoveryRequired ----

        [Test]
        public void CanonicalPlanAndMatchingChunk_IsPublicationRecoveryRequired_AndPublishesTheSamePlan()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            NvencRunPublicationRecoveryDecision decision = NvencRunPublicationRecoveryClassifier.Classify(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: MatchesExpected(plan)));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));
            Assert.That(decision.IsValid, Is.True);

            // The very same reference the snapshot held: nothing is copied or
            // re-serialized.
            Assert.That(ReferenceEquals(decision.AuthoritativePlan, plan), Is.True);
            Assert.That(ReferenceEquals(decision.Snapshot.PublicationPlan, plan), Is.True);
            Assert.That(decision.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(decision.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
        }

        // ---- PublicationRecoveryCollision ----

        [Test]
        public void CanonicalPlanAndTemporaryTogether_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    temporaryPresent: true,
                    chunkVerification: MatchesExpected(plan)),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void CanonicalPlanWithAbsentChunk_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: new CaptureArtifactVerificationResult(
                        plan.GetArtifact(0),
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.Absent,
                        CaptureArtifactVerificationFailureReason.FileAbsent,
                        0)),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void CanonicalPlanWithLengthMismatch_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: new CaptureArtifactVerificationResult(
                        plan.GetArtifact(0),
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.Mismatch,
                        CaptureArtifactVerificationFailureReason.ShorterThanDeclared,
                        ChunkByteLength - 1)),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void CanonicalPlanWithHashMismatch_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: new CaptureArtifactVerificationResult(
                        plan.GetArtifact(0),
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.Mismatch,
                        CaptureArtifactVerificationFailureReason.HashMismatch,
                        ChunkByteLength)),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void CanonicalPlanWithInvalidChunkResult_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            foreach (CaptureArtifactVerificationFailureReason reason in new[]
            {
                CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind,
                CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch,
                CaptureArtifactVerificationFailureReason.ReadIoFailure,
            })
            {
                AssertDisposition(
                    MakeSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan: plan,
                        chunkVerification: new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.Invalid,
                            reason,
                            0)),
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
            }
        }

        [Test]
        public void InvalidOrLimitExceededPlan_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            foreach (CaptureRunPublicationDocumentObservationStatus status in new[]
            {
                CaptureRunPublicationDocumentObservationStatus.Invalid,
                CaptureRunPublicationDocumentObservationStatus.LimitExceeded,
            })
            {
                AssertDisposition(
                    MakeSnapshot(operation, status),
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);

                // With the temporary present as well, it is still a collision.
                AssertDisposition(
                    MakeSnapshot(operation, status, temporaryPresent: true),
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
            }
        }

        [Test]
        public void ForeignRunPlan_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            CapturePublicationPlan otherRunId = MakePlan(operation, testRunId: operation.TestRunId + 1);
            CapturePublicationPlan otherInitId = MakePlan(operation, runInitializationId: ForeignInitId);

            foreach (CapturePublicationPlan plan in new[] { otherRunId, otherInitId })
            {
                AssertDisposition(
                    MakeSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan: plan,
                        chunkVerification: MatchesExpected(plan)),
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
            }
        }

        [Test]
        public void PlanOutsideTheFixedPhase011Graph_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            // A second artifact, a foreign format, a foreign final path, and a
            // frame relation that names an artifact the plan does not declare
            // are each outside the fixed graph.
            CaptureArtifactDescriptor chunk = NvencRunChunkArtifactDescriptorFactory.Create(
                ArtifactId, ChunkByteLength, Hash64);
            CaptureArtifactDescriptor second = new CaptureArtifactDescriptor(
                "nvenc-chunk-1",
                CaptureArtifactKind.FrameSequence,
                NvencRunChunkArtifactDescriptorFactory.FormatId,
                NvencRunChunkArtifactDescriptorFactory.FormatVersion,
                "chunks/chunk-1.nvenc-idr-chunk-v1.h264",
                "chunks/chunk-1.nvenc-idr-chunk-v1.h264",
                ChunkByteLength,
                OtherHash64);
            CaptureArtifactDescriptor foreignFormat = new CaptureArtifactDescriptor(
                ArtifactId,
                CaptureArtifactKind.FrameSequence,
                "PngFrameSequence",
                1,
                NvencRunChunkArtifactDescriptorFactory.StagingRelativePath,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath,
                ChunkByteLength,
                Hash64);
            CaptureArtifactDescriptor foreignPath = new CaptureArtifactDescriptor(
                ArtifactId,
                CaptureArtifactKind.FrameSequence,
                NvencRunChunkArtifactDescriptorFactory.FormatId,
                NvencRunChunkArtifactDescriptorFactory.FormatVersion,
                NvencRunChunkArtifactDescriptorFactory.StagingRelativePath,
                "chunks/chunk-0.other.h264",
                ChunkByteLength,
                Hash64);

            CapturePublicationPlan twoArtifacts = MakePlan(
                operation,
                descriptors: new[] { chunk, second },
                artifactIdsPerFrame: new[] { chunk.ArtifactId, second.ArtifactId });
            CapturePublicationPlan wrongFormat = MakePlan(operation, descriptors: new[] { foreignFormat });
            CapturePublicationPlan wrongPath = MakePlan(operation, descriptors: new[] { foreignPath });
            CapturePublicationPlan noFrames = MakePlan(operation, frameCount: 0);

            foreach (CapturePublicationPlan plan in new[]
            {
                twoArtifacts, wrongFormat, wrongPath, noFrames,
            })
            {
                AssertDisposition(
                    MakeSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan: plan,
                        chunkVerification: MatchesExpected(plan)),
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
            }
        }

        [Test]
        public void CanonicalPlanWithNoChunkVerification_IsRefusedNotClassified()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            // Only this Run's own chunk decides such an observation, so a
            // missing verification means the inspection did not finish. That is
            // not an observed fact about the disk and must never be recorded as
            // a collision.
            Assert.Throws<ArgumentException>(() => MakeSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Canonical,
                plan: plan));
        }

        [Test]
        public void ObservationsDecidedWithoutTheChunk_NeedNoVerification()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);
            CapturePublicationPlan foreign = MakePlan(operation, runInitializationId: ForeignInitId);

            // A competing temporary and a plan outside the fixed graph are both
            // decided without ever looking at a chunk.
            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    temporaryPresent: true),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: foreign),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void ChunkVerificationOfAnotherDescriptor_IsCollision()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            CaptureArtifactDescriptor other = NvencRunChunkArtifactDescriptorFactory.Create(
                ArtifactId, ChunkByteLength, OtherHash64);
            Assert.That(ReferenceEquals(other, plan.GetArtifact(0)), Is.False);

            AssertDisposition(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: MatchesExpected(other)),
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision);
        }

        [Test]
        public void UndefinedObservationStatus_IsRejectedBySnapshotAndNeverFailsOpen()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            Assert.Throws<ArgumentException>(() => MakeSnapshot(
                operation, (CaptureRunPublicationDocumentObservationStatus)99));
        }

        // ---- Deferred ----

        [Test]
        public void CanonicalPlanWithDeferredChunkVerification_IsDeferred()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            NvencRunPublicationRecoveryDecision decision = NvencRunPublicationRecoveryClassifier.Classify(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: new CaptureArtifactVerificationResult(
                        plan.GetArtifact(0),
                        CaptureArtifactVerificationExecutionDisposition.Deferred,
                        CaptureArtifactVerificationStatus.None,
                        CaptureArtifactVerificationFailureReason.BufferUnavailable,
                        0)));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Deferred));

            // Only a recoverable Run publishes the plan.
            Assert.That(decision.AuthoritativePlan, Is.Null);
        }

        // ---- Snapshot consistency ----

        [Test]
        public void Snapshot_PlanAndStatusMustAgree()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();
            CapturePublicationPlan plan = MakePlan(operation);

            // Canonical without the plan that was read.
            Assert.Throws<ArgumentException>(() => new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Canonical,
                null,
                false,
                default));

            // A plan under any other status.
            Assert.Throws<ArgumentException>(() => new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Absent,
                plan,
                false,
                default));

            // A chunk verification without a canonical plan.
            Assert.Throws<ArgumentException>(() => new NvencRunPublicationRecoveryInspectionSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Invalid,
                null,
                false,
                MatchesExpected(plan)));
        }

        [Test]
        public void Snapshot_ObservesTheTemporaryByExistenceOnly()
        {
            NvencRunPublicationRecoveryInspectionOperation operation = MakeOperation();

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                operation,
                CaptureRunPublicationDocumentObservationStatus.Absent,
                temporaryPresent: true);

            // Existence is the whole observation, and it is all the snapshot
            // needs: nothing about the temporary's content is ever supplied.
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void Classifier_NullOrInvalidSnapshot_Rejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => NvencRunPublicationRecoveryClassifier.Classify(null));

            CaptureRunRootLayout layout = MakeLayout();
            NvencRunPublicationRecoveryInspectionOperation operation =
                MakeOperation(layout, out CaptureRunInitializationSessionOwnershipLease owner);
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = MakeSnapshot(
                operation, CaptureRunPublicationDocumentObservationStatus.Absent);

            owner.Dispose();

            Assert.That(snapshot.IsValid, Is.False);
            Assert.Throws<ArgumentException>(
                () => NvencRunPublicationRecoveryClassifier.Classify(snapshot));
        }

        // ---- Non-contact ----

        [Test]
        public void Classification_TouchesNoFileLockOrRunState()
        {
            CaptureRunRootLayout layout = MakeLayout();
            NvencRunPublicationRecoveryInspectionOperation operation =
                MakeOperation(layout, out CaptureRunInitializationSessionOwnershipLease owner);
            CapturePublicationPlan plan = MakePlan(operation);

            bool leaseCreated = owner.IsCreated;
            bool leaseCanRelease = owner.CanRelease;
            bool leaseReleaseComplete = owner.IsReleaseComplete;

            NvencRunPublicationRecoveryDecision decision = NvencRunPublicationRecoveryClassifier.Classify(
                MakeSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan: plan,
                    chunkVerification: MatchesExpected(plan)));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));

            // No file was created or read, and the held lock is untouched.
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
            Assert.That(owner.IsCreated, Is.EqualTo(leaseCreated));
            Assert.That(owner.CanRelease, Is.EqualTo(leaseCanRelease));
            Assert.That(owner.IsReleaseComplete, Is.EqualTo(leaseReleaseComplete));

            // Re-classifying the same snapshot is stable and still publishes the
            // same plan reference.
            NvencRunPublicationRecoveryDecision again =
                NvencRunPublicationRecoveryClassifier.Classify(decision.Snapshot);
            Assert.That(again.Disposition, Is.EqualTo(decision.Disposition));
            Assert.That(ReferenceEquals(again.AuthoritativePlan, plan), Is.True);
        }

        // ---- Fixture helpers ----

        private static void AssertDisposition(
            NvencRunPublicationRecoveryInspectionSnapshot snapshot,
            NvencRunPublicationRecoveryDisposition expected)
        {
            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(expected));
            Assert.That(decision.IsValid, Is.True);
            Assert.That(ReferenceEquals(decision.Snapshot, snapshot), Is.True);

            if (expected != NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired)
            {
                Assert.That(decision.AuthoritativePlan, Is.Null);
            }
        }

        private static CaptureArtifactVerificationResult MatchesExpected(CapturePublicationPlan plan)
        {
            return MatchesExpected(plan.GetArtifact(0));
        }

        private static CaptureArtifactVerificationResult MatchesExpected(
            CaptureArtifactDescriptor descriptor)
        {
            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Completed,
                CaptureArtifactVerificationStatus.MatchesExpected,
                CaptureArtifactVerificationFailureReason.None,
                descriptor.ByteLength);
        }

        private static NvencRunPublicationRecoveryInspectionSnapshot MakeSnapshot(
            NvencRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservationStatus status,
            CapturePublicationPlan plan = null,
            bool temporaryPresent = false,
            CaptureArtifactVerificationResult chunkVerification = default)
        {
            return new NvencRunPublicationRecoveryInspectionSnapshot(
                operation, status, plan, temporaryPresent, chunkVerification);
        }

        private static CapturePublicationPlan MakePlan(
            NvencRunPublicationRecoveryInspectionOperation operation,
            long testRunId = 0,
            string runInitializationId = null,
            CaptureArtifactDescriptor[] descriptors = null,
            string[] artifactIdsPerFrame = null,
            int frameCount = 3)
        {
            CaptureArtifactDescriptor[] artifacts = descriptors
                ?? new[]
                {
                    NvencRunChunkArtifactDescriptorFactory.Create(ArtifactId, ChunkByteLength, Hash64),
                };

            string[] ids = artifactIdsPerFrame ?? new[] { artifacts[0].ArtifactId };
            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, ids);
            }

            return new CapturePublicationPlan(
                testRunId == 0 ? operation.TestRunId : testRunId,
                runInitializationId ?? operation.RunInitializationId,
                Hash64,
                artifacts,
                entries);
        }

        private NvencRunPublicationRecoveryInspectionOperation MakeOperation()
        {
            return MakeOperation(MakeLayout(), out _);
        }

        private NvencRunPublicationRecoveryInspectionOperation MakeOperation(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            return new NvencRunPublicationRecoveryInspectionOperation(
                MakeRecoveryOutcome(layout, out owner), layout);
        }

        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(CaptureRunRootLayout layout)
        {
            return MakeRecoveryOutcome(layout, out _);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            // A canonical, fully published pair with a leftover non-marker entry
            // in staging is what the existing classifier resolves to
            // "requires publication recovery".
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
                    new FakeInspector(staging, final),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
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
                role,
                true,
                false,
                CaptureRunMarkerObservationStatus.Canonical,
                init,
                false,
                CaptureRunMarkerObservationStatus.Canonical,
                ready,
                hasNonMarkerEntry,
                false,
                false);
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

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            internal FakeInspector(
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
