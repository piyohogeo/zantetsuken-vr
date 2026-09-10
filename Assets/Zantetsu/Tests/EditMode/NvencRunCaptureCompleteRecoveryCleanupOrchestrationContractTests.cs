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
    /// cleanup orchestration boundary: which receipts reach a cleaner, exactly
    /// one attempt, and the result it hands back on each path.
    /// </summary>
    /// <remarks>
    /// The cleaner is a small fake that only mints the ordinary Cleaned and
    /// Failed results. The production cleaner's deletion order, temporary
    /// classification, flushes, and no-follow handling belong to its own
    /// fixture, and the refusal of default, foreign, and corrupt results to the
    /// execution coordinator's; neither is repeated here.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryCleanupOrchestrationContractTests
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
                owner.Dispose();
            }

            _owners.Clear();
        }

        // ---- Admission ----

        [Test]
        public void Constructor_NullCleanupExecution_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("cleanupExecution"));
        }

        [Test]
        public void Execute_NullReceipt_RejectedWithoutCleanerContact()
        {
            FakeCleaner cleaner = new FakeCleaner();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(cleaner).Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
            Assert.That(cleaner.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_ReceiptWhoseLockWasReleased_RejectedWithoutCleanerContact()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteExistingFinal();

            ReleaseAllLocks();
            Assert.That(receipt.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => MakeCoordinator(cleaner).Execute(receipt));

            Assert.That(ex.ParamName, Is.EqualTo("receipt"));
            Assert.That(cleaner.CallCount, Is.EqualTo(0));
        }

        // ---- The two arrival paths ----

        [Test]
        public void Execute_ExistingFinalIndex_CleansOnceAndReturnsTheExactResult()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteExistingFinal();

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                MakeCoordinator(cleaner).Execute(receipt);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(result.IsCleaned, Is.True);

            // The operation the cleaner was handed carries this exact receipt,
            // and the result and its own receipt name that operation.
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = cleaner.LastOperation;
            Assert.That(ReferenceEquals(operation.CaptureCompleteReceipt, receipt), Is.True);
            Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);
            Assert.That(result.Receipt, Is.Not.Null);
            Assert.That(result.Receipt.IsIssuedFor(cleaner, operation), Is.True);

            // Nothing was committed on this path, so no commit receipt exists.
            Assert.That(result.HasCommitReceipt, Is.False);
            Assert.That(result.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(result.Receipt.CaptureIndexRecoveryCommitReceipt, Is.Null);
        }

        [Test]
        public void Execute_CommittedIndex_ForwardsTheExactCommitReceipt()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteCommitted(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                MakeCoordinator(cleaner).Execute(receipt);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(result.IsCleaned, Is.True);
            Assert.That(result.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(
                    cleaner.LastOperation.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.Receipt.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
        }

        // ---- Failure is an ordinary result ----

        [Test]
        public void Execute_FailedCleanup_IsReturnedAsItStandsAndNotRetried()
        {
            FakeCleaner cleaner = new FakeCleaner
            {
                Status = NvencRunCaptureCompleteRecoveryCleanupStatus.Failed,
            };
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteExistingFinal();

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                MakeCoordinator(cleaner).Execute(receipt);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(result.IsFailed, Is.True);
            Assert.That(result.Receipt, Is.Null);
            Assert.That(result.IsIssuedFor(cleaner, cleaner.LastOperation), Is.True);
            Assert.That(ReferenceEquals(result.Operation, cleaner.LastOperation), Is.True);
        }

        [Test]
        public void Execute_CleanerException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("cleanup failed");
            FakeCleaner cleaner = new FakeCleaner { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => MakeCoordinator(cleaner).Execute(CompleteExistingFinal()));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(cleaner.CallCount, Is.EqualTo(1));
        }

        // ---- Purity and shape ----

        [Test]
        public void Execute_ChangesNothingItWasGiven()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryReceipt receipt = CompleteCommitted(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            NvencRunCaptureIndexRecoveryDecision decision = receipt.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = receipt.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            MakeCoordinator(cleaner).Execute(receipt);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(commitReceipt.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(receipt.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // This layer owns no filesystem work of its own.
            Assert.That(Directory.Exists(receipt.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(receipt.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Coordinator_HoldsOnlyTheCleanupExecutionCoordinator()
        {
            Type type =
                typeof(NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held reference must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator),
            }));
        }

        // ---- Fixture helpers ----

        private static NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator MakeCoordinator(
            FakeCleaner cleaner)
        {
            return new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner));
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// The accepted CaptureComplete of a Run whose final Capture Index was
        /// already authoritative, through the ordinary orchestration.
        /// </summary>
        private NvencRunCaptureCompleteRecoveryReceipt CompleteExistingFinal()
        {
            return MakeOrchestration().Execute(Classify(Matches, Absent));
        }

        /// <summary>
        /// The accepted CaptureComplete of a Run that committed its Capture
        /// Index first, through the ordinary orchestration.
        /// </summary>
        private NvencRunCaptureCompleteRecoveryReceipt CompleteCommitted(
            out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt)
        {
            NvencRunCaptureCompleteRecoveryReceipt receipt =
                MakeOrchestration().Execute(Classify(Absent, Absent));

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

        /// <summary>
        /// Records the operation it was handed and returns the ordinary result
        /// the test asked for. It deletes nothing.
        /// </summary>
        private sealed class FakeCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunCaptureCompleteRecoveryCleanupStatus Status { get; set; } =
                NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned;

            internal NvencRunCaptureCompleteRecoveryCleanupOperation LastOperation
            {
                get;
                private set;
            }

            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return Status == NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned
                    ? NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(this, operation)
                    : NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(this, operation);
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
