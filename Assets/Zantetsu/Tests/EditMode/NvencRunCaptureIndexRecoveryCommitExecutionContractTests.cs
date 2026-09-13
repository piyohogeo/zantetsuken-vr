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
    /// execution boundary: exactly one commit attempt per valid operation, a
    /// receipt only for a known success, and the correlation that receipt must
    /// carry.
    /// </summary>
    /// <remarks>
    /// The committer is a small recording fake; the three commit modes' actual
    /// filesystem work belongs to the production committer in a later unit and
    /// is deliberately not simulated or re-specified here. Foreign receipts are
    /// minted through the ordinary success factory from another valid committer
    /// and operation, never by rewriting private state.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryCommitExecutionContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

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
        public void Coordinator_NullCommitter_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("committer"));
        }

        [Test]
        public void Execute_NullOperation_RejectedWithoutCommitterContact()
        {
            FakeCommitter committer = new FakeCommitter();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(committer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_OperationWhoseLockWasReleased_RejectedWithoutCommitterContact()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();

            ReleaseAllLocks();
            Assert.That(operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(committer.CallCount, Is.EqualTo(0));
        }

        // ---- Success ----

        [Test]
        public void Execute_Success_CommitsExactlyOnceAndReturnsTheExactReceipt()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();

            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(operation);

            Assert.That(committer.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(committer.LastOperation, operation), Is.True);
            Assert.That(ReferenceEquals(receipt, committer.LastReceipt), Is.True);
            Assert.That(ReferenceEquals(receipt.Committer, committer), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(committer, operation), Is.True);
        }

        [Test]
        public void Receipt_ForwardsTheExactOperationGraph()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();

            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(operation);

            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoveryDecision, operation.CaptureIndexRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoverySnapshot, operation.CaptureIndexRecoverySnapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    receipt.PublicationRecoveryDecision, operation.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(receipt.AuthoritativePlan, operation.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(receipt.RootLayout, operation.RootLayout), Is.True);
            Assert.That(receipt.CommitMode, Is.EqualTo(operation.CommitMode));
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, operation.RunInitializationId),
                Is.True);
        }

        [Test]
        public void Receipt_IsIssuedForOnlyItsOwnCommitterAndOperation()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();
            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(operation);

            Assert.That(receipt.IsIssuedFor(new FakeCommitter(), operation), Is.False);
            Assert.That(receipt.IsIssuedFor(committer, MakeOperation()), Is.False);
            Assert.That(receipt.IsIssuedFor(null, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(committer, null), Is.False);
        }

        // ---- Failure paths ----

        [Test]
        public void Execute_CommitterException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("commit failed");
            FakeCommitter committer = new FakeCommitter { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(MakeOperation()));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(committer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullReceipt_Rejected()
        {
            FakeCommitter committer = new FakeCommitter { Forge = (self, operation) => null };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(MakeOperation()));
            Assert.That(committer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherCommitter_Rejected()
        {
            FakeCommitter other = new FakeCommitter();
            FakeCommitter committer = new FakeCommitter
            {
                // A perfectly ordinary receipt, minted by someone else.
                Forge = (self, operation) =>
                    NvencRunCaptureIndexRecoveryCommitReceipt.Committed(other, operation),
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(MakeOperation()));
            Assert.That(committer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherOperation_Rejected()
        {
            NvencRunCaptureIndexRecoveryCommitOperation otherOperation = MakeOperation();
            FakeCommitter committer = new FakeCommitter
            {
                Forge = (self, operation) =>
                    NvencRunCaptureIndexRecoveryCommitReceipt.Committed(self, otherOperation),
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(MakeOperation()));
            Assert.That(committer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Receipt_FactoryRejectsNullOrInvalidArguments()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();

            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureIndexRecoveryCommitReceipt.Committed(null, operation));
            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureIndexRecoveryCommitReceipt.Committed(committer, null));

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureIndexRecoveryCommitReceipt.Committed(committer, operation));
        }

        [Test]
        public void Receipt_AfterLockRelease_BecomesInvalid()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();
            NvencRunCaptureIndexRecoveryCommitReceipt receipt =
                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                    .Execute(operation);

            Assert.That(receipt.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(committer, operation), Is.False);

            // It still carries the same graph, and released nothing itself.
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
        }

        // ---- Purity ----

        [Test]
        public void Execute_ChangesNothingItWasGiven()
        {
            FakeCommitter committer = new FakeCommitter();
            NvencRunCaptureIndexRecoveryCommitOperation operation = MakeOperation();
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            NvencRunCaptureIndexRecoveryDecision decision = operation.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = operation.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;
            CaptureRunCaptureIndexCommitMode mode = operation.CommitMode;

            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer)
                .Execute(operation);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.CommitMode, Is.EqualTo(mode));
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(operation.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // This boundary owns no filesystem work of its own.
            Assert.That(Directory.Exists(operation.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(operation.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Shapes_ReceiptHoldsTwoReferencesAndTheCoordinatorOne()
        {
            AssertReadonlyFields(
                typeof(NvencRunCaptureIndexRecoveryCommitReceipt),
                typeof(INvencRunCaptureIndexRecoveryCommitter),
                typeof(NvencRunCaptureIndexRecoveryCommitOperation));
            AssertReadonlyFields(
                typeof(NvencRunCaptureIndexRecoveryCommitExecutionCoordinator),
                typeof(INvencRunCaptureIndexRecoveryCommitter));

            Assert.That(
                typeof(IDisposable).IsAssignableFrom(
                    typeof(NvencRunCaptureIndexRecoveryCommitReceipt)),
                Is.False);
        }

        // ---- Fixture helpers ----

        private static void AssertReadonlyFields(Type type, params Type[] expected)
        {
            Assert.That(type.IsSealed, Is.True);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(expected.Length));

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held reference must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(expected));
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private NvencRunCaptureIndexRecoveryCommitOperation MakeOperation()
        {
            return new NvencRunCaptureIndexRecoveryCommitOperation(
                NvencRunCaptureIndexRecoveryClassifier.Classify(
                    new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                        new NvencRunCaptureIndexRecoveryInspectionOperation(MakeRecoveryDecision()),
                        Absent,
                        Absent)));
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
        /// Records the operation it was given and the receipt it returned. It
        /// performs no filesystem work: the three commit modes belong to the
        /// production committer, not to this fixture.
        /// </summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal Func<
                FakeCommitter,
                NvencRunCaptureIndexRecoveryCommitOperation,
                NvencRunCaptureIndexRecoveryCommitReceipt> Forge
            { get; set; }

            internal NvencRunCaptureIndexRecoveryCommitOperation LastOperation
            {
                get;
                private set;
            }

            internal NvencRunCaptureIndexRecoveryCommitReceipt LastReceipt { get; private set; }

            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                LastReceipt = Forge != null
                    ? Forge(this, operation)
                    : NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
                return LastReceipt;
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
