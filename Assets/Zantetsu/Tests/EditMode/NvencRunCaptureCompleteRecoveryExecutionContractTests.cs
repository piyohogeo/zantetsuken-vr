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
    /// execution boundary: exactly one attempt per valid operation, a receipt
    /// only for a success, and the correlation and authority graph that receipt
    /// carries on each of the two paths.
    /// </summary>
    /// <remarks>
    /// The completer is a small recording fake. Only the two arrival paths are
    /// exercised here - an already authoritative final Capture Index and a
    /// recovery-committed one - since the classification table and the
    /// committer's per-mode work are fixed by their own fixtures. Foreign
    /// receipts are minted through the ordinary success factory from another
    /// valid completer and operation, never by rewriting private state.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryExecutionContractTests
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
        public void Coordinator_NullCompleter_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("completer"));
        }

        [Test]
        public void Execute_NullOperation_RejectedWithoutCompleterContact()
        {
            FakeCompleter completer = new FakeCompleter();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(completer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_OperationWhoseLockWasReleased_RejectedWithoutCompleterContact()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeExistingFinalOperation();

            ReleaseAllLocks();
            Assert.That(operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(completer.CallCount, Is.EqualTo(0));
        }

        // ---- The existing-final path ----

        [Test]
        public void Execute_ExistingFinalIndex_CompletesOnceAndCarriesNoCommitReceipt()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeExistingFinalOperation();

            NvencRunCaptureCompleteRecoveryReceipt receipt =
                new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(operation);

            Assert.That(completer.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(completer.LastOperation, operation), Is.True);
            Assert.That(ReferenceEquals(receipt, completer.LastReceipt), Is.True);
            Assert.That(receipt.IsIssuedFor(completer, operation), Is.True);

            Assert.That(receipt.HasCommitReceipt, Is.False);
            Assert.That(receipt.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(receipt.CaptureIndexRecoveryDecision.Disposition, Is.EqualTo(
                NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired));
            AssertForwardsTheGraphOf(receipt, operation);
        }

        // ---- The recovery-committed path ----

        [Test]
        public void Execute_CommittedIndex_CompletesOnceAndCarriesTheExactCommitReceipt()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeCommittedOperation(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);

            NvencRunCaptureCompleteRecoveryReceipt receipt =
                new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(operation);

            Assert.That(completer.CallCount, Is.EqualTo(1));
            Assert.That(receipt.IsIssuedFor(completer, operation), Is.True);

            Assert.That(receipt.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(receipt.CaptureIndexRecoveryDecision.Disposition,
                Is.EqualTo(NvencRunCaptureIndexRecoveryDisposition.CommitRequired));
            AssertForwardsTheGraphOf(receipt, operation);
        }

        [Test]
        public void Receipt_IsIssuedForOnlyItsOwnCompleterAndOperation()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeExistingFinalOperation();
            NvencRunCaptureCompleteRecoveryReceipt receipt =
                new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(operation);

            Assert.That(receipt.IsIssuedFor(new FakeCompleter(), operation), Is.False);
            Assert.That(receipt.IsIssuedFor(completer, MakeExistingFinalOperation()), Is.False);
            Assert.That(receipt.IsIssuedFor(null, operation), Is.False);
            Assert.That(receipt.IsIssuedFor(completer, null), Is.False);
        }

        // ---- Failure paths ----

        [Test]
        public void Execute_CompleterException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("completion failed");
            FakeCompleter completer = new FakeCompleter { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(MakeExistingFinalOperation()));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(completer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullReceipt_Rejected()
        {
            FakeCompleter completer = new FakeCompleter { Forge = (self, operation) => null };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(MakeExistingFinalOperation()));
            Assert.That(completer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherCompleter_Rejected()
        {
            FakeCompleter other = new FakeCompleter();
            FakeCompleter completer = new FakeCompleter
            {
                // A perfectly ordinary receipt, minted by someone else.
                Forge = (self, operation) =>
                    NvencRunCaptureCompleteRecoveryReceipt.Completed(other, operation),
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(MakeExistingFinalOperation()));
            Assert.That(completer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ReceiptOfAnotherOperation_Rejected()
        {
            NvencRunCaptureCompleteRecoveryOperation otherOperation = MakeExistingFinalOperation();
            FakeCompleter completer = new FakeCompleter
            {
                Forge = (self, operation) =>
                    NvencRunCaptureCompleteRecoveryReceipt.Completed(self, otherOperation),
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(MakeExistingFinalOperation()));
            Assert.That(completer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Receipt_FactoryRejectsNullOrInvalidArguments()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeExistingFinalOperation();

            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryReceipt.Completed(null, operation));
            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryReceipt.Completed(completer, null));

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryReceipt.Completed(completer, operation));
        }

        [Test]
        public void Receipt_AfterLockRelease_BecomesInvalid()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeCommittedOperation(out _);
            NvencRunCaptureCompleteRecoveryReceipt receipt =
                new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer)
                    .Execute(operation);

            Assert.That(receipt.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(completer, operation), Is.False);

            // It still carries the same graph, and released nothing itself.
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
        }

        // ---- Purity and shape ----

        [Test]
        public void Execute_ChangesNothingItWasGiven()
        {
            FakeCompleter completer = new FakeCompleter();
            NvencRunCaptureCompleteRecoveryOperation operation = MakeCommittedOperation(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            NvencRunCaptureIndexRecoveryDecision decision = operation.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = operation.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            new NvencRunCaptureCompleteRecoveryExecutionCoordinator(completer).Execute(operation);

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
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // This boundary is filesystem-free.
            Assert.That(Directory.Exists(operation.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(operation.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Shapes_ReceiptHoldsTwoReferencesAndTheCoordinatorOne()
        {
            AssertReadonlyFields(
                typeof(NvencRunCaptureCompleteRecoveryReceipt),
                typeof(INvencRunCaptureCompleteRecoveryCompleter),
                typeof(NvencRunCaptureCompleteRecoveryOperation));
            AssertReadonlyFields(
                typeof(NvencRunCaptureCompleteRecoveryExecutionCoordinator),
                typeof(INvencRunCaptureCompleteRecoveryCompleter));

            Assert.That(
                typeof(IDisposable).IsAssignableFrom(
                    typeof(NvencRunCaptureCompleteRecoveryReceipt)),
                Is.False);
            Assert.That(
                typeof(NvencRunCaptureCompleteRecoveryReceipt).GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public),
                Is.Empty);
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheGraphOf(
            NvencRunCaptureCompleteRecoveryReceipt receipt,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
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
            Assert.That(receipt.HasCommitReceipt, Is.EqualTo(operation.HasCommitReceipt));
            Assert.That(receipt.IsValid, Is.True);
        }

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
        /// Records the operation it was given and the receipt it returned. It
        /// touches no file: this boundary accepts a state, it does not create
        /// one.
        /// </summary>
        private sealed class FakeCompleter : INvencRunCaptureCompleteRecoveryCompleter
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal Func<
                FakeCompleter,
                NvencRunCaptureCompleteRecoveryOperation,
                NvencRunCaptureCompleteRecoveryReceipt> Forge
            { get; set; }

            internal NvencRunCaptureCompleteRecoveryOperation LastOperation { get; private set; }

            internal NvencRunCaptureCompleteRecoveryReceipt LastReceipt { get; private set; }

            public NvencRunCaptureCompleteRecoveryReceipt Complete(
                NvencRunCaptureCompleteRecoveryOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                LastReceipt = Forge != null
                    ? Forge(this, operation)
                    : NvencRunCaptureCompleteRecoveryReceipt.Completed(this, operation);
                return LastReceipt;
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
