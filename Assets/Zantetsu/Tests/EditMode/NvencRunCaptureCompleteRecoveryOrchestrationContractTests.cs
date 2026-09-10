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
    /// orchestration boundary: which classifications reach a collaborator, the
    /// commit-then-complete order, and what the returned receipt carries on
    /// each branch.
    /// </summary>
    /// <remarks>
    /// The committer and the completer are small fakes that mint the ordinary
    /// success receipts and share one call log. The production filesystem
    /// work per commit mode, the foreign receipt refusals, and the
    /// classification table are fixed by their own fixtures and are not
    /// repeated here.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryOrchestrationContractTests
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
        public void Constructor_NullDependencies_Rejected()
        {
            Harness h = MakeHarness();

            ArgumentNullException commitEx = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                    null, h.CaptureCompleteExecution));
            ArgumentNullException completeEx = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                    h.CommitOrchestration, null));

            Assert.That(commitEx.ParamName, Is.EqualTo("captureIndexCommitOrchestration"));
            Assert.That(completeEx.ParamName, Is.EqualTo("captureCompleteExecution"));
        }

        [Test]
        public void Execute_NullDecision_RejectedWithoutTouchingEitherCollaborator()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => h.Coordinator.Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
            Assert.That(h.Calls, Is.Empty);
        }

        [Test]
        public void Execute_Collision_RejectedWithoutTouchingEitherCollaborator()
        {
            Harness h = MakeHarness();
            NvencRunCaptureIndexRecoveryDecision collision = Classify(Matches, Mismatch);

            Assert.That(collision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Coordinator.Execute(collision));

            Assert.That(ex.ParamName, Is.EqualTo("decision"));
            Assert.That(h.Calls, Is.Empty);
        }

        [Test]
        public void Execute_DecisionWhoseLockWasReleased_RejectedOnBothBranches()
        {
            foreach (NvencRunCaptureIndexObservationStatus finalIndex in new[] { Absent, Matches })
            {
                Harness h = MakeHarness();
                NvencRunCaptureIndexRecoveryDecision decision = Classify(finalIndex, Absent);

                ReleaseAllLocks();

                Assert.Throws<ArgumentException>(
                    () => h.Coordinator.Execute(decision), finalIndex.ToString());
                Assert.That(h.Calls, Is.Empty, finalIndex.ToString());
            }
        }

        // ---- The already authoritative branch ----

        [Test]
        public void Execute_CaptureCompleteRequired_CompletesWithoutCommitting()
        {
            Harness h = MakeHarness();
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Matches, Absent);

            NvencRunCaptureCompleteRecoveryReceipt receipt = h.Coordinator.Execute(decision);

            Assert.That(h.Calls, Is.EqualTo(new List<string> { "Complete" }));
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));

            // Nothing was committed, so no commit receipt is invented.
            Assert.That(receipt.HasCommitReceipt, Is.False);
            Assert.That(receipt.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(receipt.IsIssuedFor(h.Completer, h.Completer.LastOperation), Is.True);
            AssertForwardsTheGraphOf(receipt, decision);
        }

        // ---- The commit branch ----

        [Test]
        public void Execute_CommitRequired_CommitsThenCompletesEachOnce()
        {
            Harness h = MakeHarness();
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Absent);

            NvencRunCaptureCompleteRecoveryReceipt receipt = h.Coordinator.Execute(decision);

            Assert.That(h.Calls, Is.EqualTo(new List<string> { "Commit", "Complete" }));
            Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));

            // The commit's own receipt is what the CaptureComplete carries.
            Assert.That(receipt.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoveryCommitReceipt, h.Committer.LastReceipt),
                Is.True);
            Assert.That(ReferenceEquals(
                    h.Committer.LastOperation.CaptureIndexRecoveryDecision, decision),
                Is.True);
            Assert.That(receipt.IsIssuedFor(h.Completer, h.Completer.LastOperation), Is.True);
            AssertForwardsTheGraphOf(receipt, decision);
        }

        // ---- Failure paths ----

        [Test]
        public void Execute_CommitException_PropagatesAndNeverCompletes()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("commit failed");
            h.Committer.ExceptionToThrow = failure;

            IOException thrown = Assert.Throws<IOException>(
                () => h.Coordinator.Execute(Classify(Absent, Absent)));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Calls, Is.EqualTo(new List<string> { "Commit" }));
            Assert.That(h.Completer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_CompleterException_OnTheAlreadyAuthoritativeBranch_Propagates()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("completion failed");
            h.Completer.ExceptionToThrow = failure;

            IOException thrown = Assert.Throws<IOException>(
                () => h.Coordinator.Execute(Classify(Matches, Absent)));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_CompleterExceptionAfterACommit_RetriesNeither()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("completion failed");
            h.Completer.ExceptionToThrow = failure;

            IOException thrown = Assert.Throws<IOException>(
                () => h.Coordinator.Execute(Classify(Absent, Absent)));

            // The Capture Index may now be committed on disk; this call does
            // not commit again, and does not complete again either.
            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.Calls, Is.EqualTo(new List<string> { "Commit", "Complete" }));
            Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            Assert.That(h.Completer.CallCount, Is.EqualTo(1));
        }

        // ---- Purity and shape ----

        [Test]
        public void Execute_LeavesTheDecisionGraphAsTheEarlierObservation()
        {
            Harness h = MakeHarness();
            NvencRunCaptureIndexRecoveryDecision decision = Classify(Absent, Absent);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            CapturePublicationPlan plan = decision.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            h.Coordinator.Execute(decision);

            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            Assert.That(decision.CommitMode,
                Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(decision.Operation.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);
        }

        [Test]
        public void Coordinator_HoldsOnlyItsTwoDependencies()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryOrchestrationCoordinator);

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
                typeof(NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator),
                typeof(NvencRunCaptureCompleteRecoveryExecutionCoordinator),
            }));
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheGraphOf(
            NvencRunCaptureCompleteRecoveryReceipt receipt,
            NvencRunCaptureIndexRecoveryDecision decision)
        {
            Assert.That(ReferenceEquals(receipt.CaptureIndexRecoveryDecision, decision), Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoverySnapshot, decision.Snapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    receipt.PublicationRecoveryDecision,
                    decision.Operation.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(receipt.AuthoritativePlan, decision.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(receipt.RootLayout, decision.RootLayout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(decision.TestRunId));
            Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, decision.RunInitializationId),
                Is.True);
            Assert.That(receipt.IsValid, Is.True);
        }

        private Harness MakeHarness()
        {
            List<string> calls = new List<string>();
            FakeCommitter committer = new FakeCommitter(calls);
            FakeCompleter completer = new FakeCompleter(calls);

            NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator commitOrchestration =
                new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                    new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(committer));
            NvencRunCaptureCompleteRecoveryExecutionCoordinator captureCompleteExecution =
                new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer);

            return new Harness(
                calls,
                committer,
                completer,
                commitOrchestration,
                captureCompleteExecution,
                new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                    commitOrchestration, captureCompleteExecution));
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

        /// <summary>The two fakes, their shared call log, and the coordinator.</summary>
        private sealed class Harness
        {
            internal Harness(
                List<string> calls,
                FakeCommitter committer,
                FakeCompleter completer,
                NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator commitOrchestration,
                NvencRunCaptureCompleteRecoveryExecutionCoordinator captureCompleteExecution,
                NvencRunCaptureCompleteRecoveryOrchestrationCoordinator coordinator)
            {
                Calls = calls;
                Committer = committer;
                Completer = completer;
                CommitOrchestration = commitOrchestration;
                CaptureCompleteExecution = captureCompleteExecution;
                Coordinator = coordinator;
            }

            internal List<string> Calls { get; }

            internal FakeCommitter Committer { get; }

            internal FakeCompleter Completer { get; }

            internal NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator
                CommitOrchestration { get; }

            internal NvencRunCaptureCompleteRecoveryExecutionCoordinator
                CaptureCompleteExecution { get; }

            internal NvencRunCaptureCompleteRecoveryOrchestrationCoordinator Coordinator { get; }
        }

        /// <summary>Mints the ordinary commit receipt and touches no file.</summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            private readonly List<string> _calls;

            internal FakeCommitter(List<string> calls)
            {
                _calls = calls;
            }

            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunCaptureIndexRecoveryCommitOperation LastOperation
            {
                get;
                private set;
            }

            internal NvencRunCaptureIndexRecoveryCommitReceipt LastReceipt { get; private set; }

            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                _calls.Add("Commit");
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                LastReceipt = NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
                return LastReceipt;
            }
        }

        /// <summary>Mints the ordinary CaptureComplete receipt and touches no file.</summary>
        private sealed class FakeCompleter : INvencRunCaptureCompleteRecoveryCompleter
        {
            private readonly List<string> _calls;

            internal FakeCompleter(List<string> calls)
            {
                _calls = calls;
            }

            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunCaptureCompleteRecoveryOperation LastOperation { get; private set; }

            public NvencRunCaptureCompleteRecoveryReceipt Complete(
                NvencRunCaptureCompleteRecoveryOperation operation)
            {
                _calls.Add("Complete");
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return NvencRunCaptureCompleteRecoveryReceipt.Completed(this, operation);
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
