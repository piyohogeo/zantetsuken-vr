using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the production Phase 0.11 NVENC recovery
    /// CaptureComplete completer: admission, the receipt it returns on each of
    /// the two arrival paths, and the fact that it changes nothing.
    /// </summary>
    /// <remarks>
    /// Everything is a small in-memory graph. Exactly-once execution, foreign
    /// receipt refusal, and exception propagation are already fixed by the
    /// execution coordinator's fixture and are not repeated here.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryCompleterContractTests
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
        public void Complete_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryCompleter().Complete(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Complete_OperationWhoseLockWasReleased_Rejected()
        {
            NvencRunCaptureCompleteRecoveryCompleter completer =
                new NvencRunCaptureCompleteRecoveryCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeExistingFinalOperation();

            ReleaseAllLocks();
            Assert.That(operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => completer.Complete(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        // ---- The two arrival paths ----

        [Test]
        public void Complete_ExistingFinalIndex_ReturnsAReceiptWithNoCommitReceipt()
        {
            NvencRunCaptureCompleteRecoveryCompleter completer =
                new NvencRunCaptureCompleteRecoveryCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeExistingFinalOperation();

            NvencRunCaptureCompleteRecoveryReceipt receipt = completer.Complete(operation);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(completer, operation), Is.True);
            Assert.That(receipt.HasCommitReceipt, Is.False);
            Assert.That(receipt.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(receipt.CaptureIndexRecoveryDecision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));
            AssertForwardsTheGraphOf(receipt, completer, operation);
        }

        [Test]
        public void Complete_CommittedIndex_ForwardsTheExactCommitReceipt()
        {
            NvencRunCaptureCompleteRecoveryCompleter completer =
                new NvencRunCaptureCompleteRecoveryCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeCommittedOperation(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);

            NvencRunCaptureCompleteRecoveryReceipt receipt = completer.Complete(operation);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(completer, operation), Is.True);
            Assert.That(receipt.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(receipt.CaptureIndexRecoveryDecision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            AssertForwardsTheGraphOf(receipt, completer, operation);
        }

        // ---- Purity and shape ----

        [Test]
        public void Complete_ChangesNothingItWasGiven()
        {
            NvencRunCaptureCompleteRecoveryOperation operation = MakeCommittedOperation(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            NvencRunCaptureIndexRecoveryDecision decision = operation.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = operation.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            new NvencRunCaptureCompleteRecoveryCompleter().Complete(operation);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(commitReceipt.IsValid, Is.True);
            Assert.That(ReferenceEquals(
                    operation.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(operation.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // Accepting a reached state is filesystem-free.
            Assert.That(Directory.Exists(operation.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(operation.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Completer_IsSealedStatelessAndNonDisposable()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryCompleter);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            Assert.That(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                Is.Empty,
                "the completer must hold no instance state.");

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(
                    field.IsLiteral || field.IsInitOnly,
                    Is.True,
                    "the completer must hold no mutable static state.");
            }
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheGraphOf(
            NvencRunCaptureCompleteRecoveryReceipt receipt,
            NvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            Assert.That(ReferenceEquals(receipt.Completer, completer), Is.True);
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoveryDecision,
                    operation.CaptureIndexRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoverySnapshot,
                    operation.CaptureIndexRecoverySnapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    receipt.PublicationRecoveryDecision,
                    operation.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(receipt.AuthoritativePlan, operation.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(receipt.RootLayout, operation.RootLayout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, operation.RunInitializationId),
                Is.True);
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private NvencRunCaptureCompleteRecoveryOperation MakeExistingFinalOperation()
        {
            return NvencRunCaptureCompleteRecoveryOperation.FromCaptureCompleteRequired(
                Classify(Matches, Absent));
        }

        /// <summary>
        /// Runs the ordinary commit orchestration over a fake committer, so the
        /// commit receipt behind the operation is the real thing.
        /// </summary>
        private NvencRunCaptureCompleteRecoveryOperation MakeCommittedOperation(
            out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt)
        {
            commitReceipt = new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                    new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                        new FakeCommitter()))
                .Execute(Classify(Absent, Absent));

            return NvencRunCaptureCompleteRecoveryOperation.FromCommittedIndex(commitReceipt);
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
