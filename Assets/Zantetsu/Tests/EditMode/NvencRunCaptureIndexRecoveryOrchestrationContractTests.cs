using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC Capture Index recovery
    /// orchestration boundary: which publication recovery decisions are
    /// admitted, exactly one inspection and one classification of that exact
    /// snapshot, and the correlation the returned classification carries.
    /// </summary>
    /// <remarks>
    /// The inspector is a small recording fake returning in-memory snapshots,
    /// so the three dispositions are reached without a filesystem. The
    /// classification table is already fixed by its own fixture and is not
    /// repeated here: one representative path per disposition plus the
    /// orchestration correlation is the whole subject.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryOrchestrationContractTests
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
        public void Constructor_NullInspectionExecution_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("inspectionExecution"));
        }

        [Test]
        public void Execute_NullDecision_RejectedWithoutInspectorContact()
        {
            FakeInspector inspector = new FakeInspector();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(inspector).Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("publicationRecoveryDecision"));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_IncompleteDeferredOrCollidingDecision_RejectedWithoutInspectorContact()
        {
            FakeInspector inspector = new FakeInspector();
            NvencRunCaptureIndexRecoveryOrchestrationCoordinator coordinator =
                MakeCoordinator(inspector);

            foreach (NvencRunPublicationRecoveryDecision recovery in new[]
            {
                MakeIncompleteRecoveryDecision(),
                MakeDeferredRecoveryDecision(),
                MakeCollidingRecoveryDecision(),
            })
            {
                Assert.That(recovery.Disposition, Is.Not.EqualTo(
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));
                Assert.Throws<ArgumentException>(() => coordinator.Execute(recovery));
            }

            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_DecisionWhoseLockWasReleased_RejectedWithoutInspectorContact()
        {
            FakeInspector inspector = new FakeInspector();
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(() => MakeCoordinator(inspector).Execute(recovery));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        // ---- The three dispositions ----

        [Test]
        public void Execute_CommitRequired_InspectsOnceAndForwardsTheExactGraph()
        {
            FakeInspector inspector = new FakeInspector(Absent, Absent);
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();

            NvencRunCaptureIndexRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(recovery);

            Assert.That(inspector.CallCount, Is.EqualTo(1));
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(decision.CommitMode,
                Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(decision.IsValid, Is.True);

            AssertForwardsTheExactGraph(decision, inspector, recovery);
        }

        [Test]
        public void Execute_CaptureCompleteRequired_CarriesNoCommitMode()
        {
            FakeInspector inspector = new FakeInspector(Matches, Absent);
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();

            NvencRunCaptureIndexRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(recovery);

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));
            Assert.That(decision.CommitMode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.None));

            AssertForwardsTheExactGraph(decision, inspector, recovery);
        }

        [Test]
        public void Execute_Collision_CarriesNoCommitMode()
        {
            FakeInspector inspector = new FakeInspector(Matches, Mismatch);
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();

            NvencRunCaptureIndexRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(recovery);

            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(decision.CommitMode, Is.EqualTo(CaptureRunCaptureIndexCommitMode.None));

            AssertForwardsTheExactGraph(decision, inspector, recovery);
        }

        // ---- Failure paths ----

        [Test]
        public void Execute_InspectorException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("inspection failed");
            FakeInspector inspector = new FakeInspector { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => MakeCoordinator(inspector).Execute(MakeRecoveryDecision()));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(inspector.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullForeignOrInvalidSnapshot_ProducesNoDecision()
        {
            // Null.
            FakeInspector nulls = new FakeInspector((operation, fixture) => null);
            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(nulls).Execute(MakeRecoveryDecision()));
            Assert.That(nulls.CallCount, Is.EqualTo(1));

            // A snapshot describing another Run's operation.
            FakeInspector foreign = new FakeInspector(
                (operation, fixture) => new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    new NvencRunCaptureIndexRecoveryInspectionOperation(
                        fixture.MakeRecoveryDecision()),
                    Absent,
                    Absent));
            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(foreign).Execute(MakeRecoveryDecision()));
            Assert.That(foreign.CallCount, Is.EqualTo(1));

            // A snapshot whose operation lost its lock while it was built.
            FakeInspector invalid = new FakeInspector(
                (operation, fixture) =>
                {
                    NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                        new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                            operation, Absent, Absent);
                    fixture.ReleaseAllLocks();
                    return snapshot;
                });
            Assert.Throws<InvalidOperationException>(
                () => MakeCoordinator(invalid).Execute(MakeRecoveryDecision()));
            Assert.That(invalid.CallCount, Is.EqualTo(1));
        }

        // ---- The classification ----

        [Test]
        public void Classification_AfterLockRelease_BecomesInvalid()
        {
            FakeInspector inspector = new FakeInspector(Absent, Matches);
            NvencRunCaptureIndexRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(MakeRecoveryDecision());

            Assert.That(decision.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(decision.IsValid, Is.False);
            Assert.That(decision.Snapshot.IsValid, Is.False);
            Assert.That(decision.Operation.IsValid, Is.False);

            // It still carries the same graph; it simply no longer claims to be
            // valid, and it released nothing itself.
            Assert.That(ReferenceEquals(decision.Snapshot, inspector.LastSnapshot), Is.True);
        }

        [Test]
        public void Execute_ChangesNothingItWasGiven()
        {
            FakeInspector inspector = new FakeInspector(Absent, Absent);
            NvencRunPublicationRecoveryDecision recovery = MakeRecoveryDecision();
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            CapturePublicationPlan plan = recovery.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;
            long chunkLength = chunk.ByteLength;

            NvencRunCaptureIndexRecoveryDecision decision =
                MakeCoordinator(inspector).Execute(recovery);

            // The publication recovery decision, the plan, and its chunk are
            // untouched, and so is the lock.
            Assert.That(recovery.IsValid, Is.True);
            Assert.That(recovery.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));
            Assert.That(ReferenceEquals(recovery.AuthoritativePlan, plan), Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(chunk.ByteLength, Is.EqualTo(chunkLength));
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // The observed statuses are exactly what the inspection reported;
            // nothing here re-observes or rewrites them.
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));

            // Nothing was created on disk, in either Run root.
            Assert.That(Directory.Exists(recovery.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(recovery.RootLayout.FinalRunRoot), Is.False);
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheExactGraph(
            NvencRunCaptureIndexRecoveryDecision decision,
            FakeInspector inspector,
            NvencRunPublicationRecoveryDecision recovery)
        {
            Assert.That(ReferenceEquals(decision.Snapshot, inspector.LastSnapshot), Is.True);
            Assert.That(ReferenceEquals(decision.Operation, inspector.LastOperation), Is.True);
            Assert.That(ReferenceEquals(decision.Operation, decision.Snapshot.Operation), Is.True);
            Assert.That(ReferenceEquals(
                    decision.Operation.PublicationRecoveryDecision, recovery),
                Is.True);
            Assert.That(ReferenceEquals(
                    decision.AuthoritativePlan, recovery.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(decision.RootLayout, recovery.RootLayout), Is.True);
            Assert.That(decision.TestRunId, Is.EqualTo(recovery.TestRunId));
            Assert.That(ReferenceEquals(
                    decision.RunInitializationId, recovery.RunInitializationId),
                Is.True);
            Assert.That(decision.IsValid, Is.True);
        }

        private NvencRunCaptureIndexRecoveryOrchestrationCoordinator MakeCoordinator(
            FakeInspector inspector)
        {
            inspector.Fixture = this;
            return new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(inspector));
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

        private NvencRunPublicationRecoveryDecision MakeCollidingRecoveryDecision()
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
        /// Records the operation it was given and the snapshot it produced, and
        /// produces exactly what the test asked for - including a null, foreign,
        /// or invalidated snapshot.
        /// </summary>
        private sealed class FakeInspector : INvencRunCaptureIndexRecoveryInspector
        {
            private readonly Func<
                NvencRunCaptureIndexRecoveryInspectionOperation,
                NvencRunCaptureIndexRecoveryOrchestrationContractTests,
                NvencRunCaptureIndexRecoveryInspectionSnapshot> _observe;

            internal FakeInspector()
                : this((operation, fixture) => null)
            {
            }

            internal FakeInspector(
                NvencRunCaptureIndexObservationStatus finalIndex,
                NvencRunCaptureIndexObservationStatus temporaryIndex)
                : this((operation, fixture) =>
                    new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                        operation, finalIndex, temporaryIndex))
            {
            }

            internal FakeInspector(
                Func<
                    NvencRunCaptureIndexRecoveryInspectionOperation,
                    NvencRunCaptureIndexRecoveryOrchestrationContractTests,
                    NvencRunCaptureIndexRecoveryInspectionSnapshot> observe)
            {
                _observe = observe;
            }

            internal NvencRunCaptureIndexRecoveryOrchestrationContractTests Fixture { get; set; }

            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunCaptureIndexRecoveryInspectionOperation LastOperation
            {
                get;
                private set;
            }

            internal NvencRunCaptureIndexRecoveryInspectionSnapshot LastSnapshot
            {
                get;
                private set;
            }

            public NvencRunCaptureIndexRecoveryInspectionSnapshot Inspect(
                NvencRunCaptureIndexRecoveryInspectionOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                LastSnapshot = _observe(operation, Fixture);
                return LastSnapshot;
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
