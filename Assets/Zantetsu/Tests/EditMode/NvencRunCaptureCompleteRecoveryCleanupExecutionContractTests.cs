using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC recovery CaptureComplete cleanup
    /// execution boundary: exactly one attempt per valid operation, the two
    /// terminal shapes and their receipt rule, and the correlation the result
    /// must carry.
    /// </summary>
    /// <remarks>
    /// The cleaner is a small fake that deletes nothing: what a cleanup targets
    /// and in which order belongs to the production cleaner's own unit.
    /// Corrupt results are built through the result's private constructor, the
    /// same limited technique the existing cleanup fixtures use, and each
    /// forged result is first asserted to name the cleaner and operation under
    /// test so the correlation checks cannot pass vacuously.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryCleanupExecutionContractTests
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
        public void Coordinator_NullCleaner_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("cleaner"));
        }

        [Test]
        public void Execute_NullOperation_RejectedWithoutCleanerContact()
        {
            FakeCleaner cleaner = new FakeCleaner();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(cleaner.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_OperationWhoseLockWasReleased_RejectedWithoutCleanerContact()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);

            ReleaseAllLocks();
            Assert.That(operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(cleaner.CallCount, Is.EqualTo(0));
        }

        // ---- The two terminal shapes ----

        [Test]
        public void Execute_Cleaned_CleansOnceAndCarriesTheExactReceipt()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(cleaner.LastOperation, operation), Is.True);
            Assert.That(result.Status,
                Is.EqualTo(NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned));
            Assert.That(result.IsCleaned, Is.True);
            Assert.That(result.IsFailed, Is.False);
            Assert.That(result.IsNone, Is.False);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);
            Assert.That(result.Receipt, Is.Not.Null);
            Assert.That(result.Receipt.IsIssuedFor(cleaner, operation), Is.True);
            Assert.That(ReferenceEquals(result.Receipt.Cleaner, cleaner), Is.True);
            Assert.That(ReferenceEquals(result.Receipt.Operation, operation), Is.True);
        }

        [Test]
        public void Execute_Failed_CarriesNoReceipt()
        {
            FakeCleaner cleaner = new FakeCleaner
            {
                Status = NvencRunCaptureCompleteRecoveryCleanupStatus.Failed,
            };
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(result.Status,
                Is.EqualTo(NvencRunCaptureCompleteRecoveryCleanupStatus.Failed));
            Assert.That(result.IsFailed, Is.True);
            Assert.That(result.IsCleaned, Is.False);
            Assert.That(result.Receipt, Is.Null);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);
        }

        [Test]
        public void Execute_CleanerException_PropagatesSameReferenceAndIsNotRetried()
        {
            IOException failure = new IOException("cleanup failed");
            FakeCleaner cleaner = new FakeCleaner { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(MakeOperation(Matches)));

            // A thrown cleanup is not silently turned into Failed.
            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(cleaner.CallCount, Is.EqualTo(1));
        }

        // ---- Corrupt results ----

        [Test]
        public void Execute_DefaultResult_Rejected()
        {
            FakeCleaner cleaner = new FakeCleaner
            {
                Forge = (self, operation) => default,
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(MakeOperation(Matches)));
            Assert.That(cleaner.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ResultOfAnotherCleanerOrOperation_Rejected()
        {
            FakeCleaner other = new FakeCleaner();

            // Another cleaner's ordinary result.
            AssertRejected(
                (self, operation) =>
                    NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(other, operation),
                (result, cleaner, operation) =>
                {
                    Assert.That(ReferenceEquals(result.Cleaner, other), Is.True);
                    Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                });

            // This cleaner's ordinary result for another operation.
            NvencRunCaptureCompleteRecoveryCleanupOperation otherOperation = MakeOperation(Matches);
            AssertRejected(
                (self, operation) =>
                    NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(
                        self, otherOperation),
                (result, cleaner, operation) =>
                {
                    Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                    Assert.That(ReferenceEquals(result.Operation, otherOperation), Is.True);
                });
        }

        [Test]
        public void Execute_CleanedResultWhoseReceiptIsForeign_Rejected()
        {
            FakeCleaner other = new FakeCleaner();

            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    NvencRunCaptureCompleteRecoveryCleanupReceipt.Cleaned(other, operation),
                    NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned),
                (result, cleaner, operation) =>
                {
                    // The result names the right pair; only its receipt is
                    // someone else's.
                    Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                    Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                    Assert.That(ReferenceEquals(result.Receipt.Cleaner, other), Is.True);
                });
        }

        [Test]
        public void Execute_CleanedResultWithoutAReceipt_Rejected()
        {
            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    null,
                    NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned),
                (result, cleaner, operation) =>
                {
                    Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                    Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                    Assert.That(result.Receipt, Is.Null);
                });
        }

        [Test]
        public void Execute_FailedResultCarryingAReceipt_Rejected()
        {
            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    NvencRunCaptureCompleteRecoveryCleanupReceipt.Cleaned(self, operation),
                    NvencRunCaptureCompleteRecoveryCleanupStatus.Failed),
                (result, cleaner, operation) =>
                {
                    Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                    Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                    Assert.That(result.Receipt, Is.Not.Null);
                });
        }

        [Test]
        public void Execute_NoneOrUndefinedStatus_Rejected()
        {
            foreach (NvencRunCaptureCompleteRecoveryCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteRecoveryCleanupStatus.None,
                (NvencRunCaptureCompleteRecoveryCleanupStatus)99,
            })
            {
                AssertRejected(
                    (self, operation) => MakeResult(self, operation, null, status),
                    (result, cleaner, operation) =>
                    {
                        Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                        Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                        Assert.That(result.Status, Is.EqualTo(status));
                    });
            }
        }

        // ---- Forwarding ----

        [Test]
        public void ResultAndReceipt_ForwardTheGraph_OnTheAlreadyAuthoritativePath()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(result.HasCommitReceipt, Is.False);
            Assert.That(result.CaptureIndexRecoveryCommitReceipt, Is.Null);
            Assert.That(result.Receipt.HasCommitReceipt, Is.False);
            Assert.That(result.Receipt.CaptureIndexRecoveryCommitReceipt, Is.Null);
            AssertForwardsTheGraphOf(result, operation);
        }

        [Test]
        public void ResultAndReceipt_ForwardTheGraph_OnTheCommittedPath()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeCommittedOperation(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(result.HasCommitReceipt, Is.True);
            Assert.That(ReferenceEquals(
                    result.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.Receipt.CaptureIndexRecoveryCommitReceipt, commitReceipt),
                Is.True);
            AssertForwardsTheGraphOf(result, operation);
        }

        // ---- Lock, purity, shape ----

        [Test]
        public void ResultAndReceipt_AfterLockRelease_BecomeInvalid()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Receipt.IsValid, Is.True);

            ReleaseAllLocks();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.IsCleaned, Is.False);
            Assert.That(result.Receipt.IsValid, Is.False);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.False);

            // The graph is still the same; it simply no longer claims validity.
            Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
        }

        [Test]
        public void Factories_NullOrInvalidArguments_Rejected()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);

            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(null, operation));
            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(cleaner, null));
            Assert.Throws<ArgumentNullException>(
                () => NvencRunCaptureCompleteRecoveryCleanupReceipt.Cleaned(null, operation));

            ReleaseAllLocks();

            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(
                    cleaner, operation));
            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(
                    cleaner, operation));
            Assert.Throws<ArgumentException>(
                () => NvencRunCaptureCompleteRecoveryCleanupReceipt.Cleaned(cleaner, operation));
        }

        [Test]
        public void Execute_ChangesNothingItWasGiven()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeCommittedOperation(
                out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt);
            CaptureRunInitializationSessionOwnershipLease owner = _owners[_owners.Count - 1];

            NvencRunCaptureIndexRecoveryDecision decision = operation.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = operation.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                .Execute(operation);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(operation.CaptureCompleteReceipt.IsValid, Is.True);
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(commitReceipt.IsValid, Is.True);
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(owner.IsCreated, Is.True);
            Assert.That(owner.IsReleaseComplete, Is.False);

            // This layer owns no filesystem work of its own.
            Assert.That(Directory.Exists(operation.RootLayout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(operation.RootLayout.FinalRunRoot), Is.False);
        }

        [Test]
        public void Shapes_ReceiptResultAndCoordinatorHoldOnlyWhatTheyMust()
        {
            Type receipt = typeof(NvencRunCaptureCompleteRecoveryCleanupReceipt);
            Assert.That(receipt.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(receipt), Is.False);
            AssertReadonlyFields(
                receipt,
                typeof(INvencRunCaptureCompleteRecoveryCleaner),
                typeof(NvencRunCaptureCompleteRecoveryCleanupOperation));
            Assert.That(
                receipt.GetConstructors(BindingFlags.Instance | BindingFlags.Public), Is.Empty);

            Type result = typeof(NvencRunCaptureCompleteRecoveryCleanupAttemptResult);
            Assert.That(result.IsValueType, Is.True);
            AssertReadonlyFields(
                result,
                typeof(INvencRunCaptureCompleteRecoveryCleaner),
                typeof(NvencRunCaptureCompleteRecoveryCleanupOperation),
                typeof(NvencRunCaptureCompleteRecoveryCleanupReceipt),
                typeof(NvencRunCaptureCompleteRecoveryCleanupStatus));

            AssertReadonlyFields(
                typeof(NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator),
                typeof(INvencRunCaptureCompleteRecoveryCleaner));
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsTheGraphOf(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(
                    result.CaptureCompleteReceipt, operation.CaptureCompleteReceipt),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.CaptureCompleteOperation, operation.CaptureCompleteOperation),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.CaptureIndexRecoveryDecision,
                    operation.CaptureIndexRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.CaptureIndexRecoverySnapshot,
                    operation.CaptureIndexRecoverySnapshot),
                Is.True);
            Assert.That(ReferenceEquals(
                    result.PublicationRecoveryDecision,
                    operation.PublicationRecoveryDecision),
                Is.True);
            Assert.That(ReferenceEquals(result.AuthoritativePlan, operation.AuthoritativePlan),
                Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(ReferenceEquals(
                    result.RunInitializationId, operation.RunInitializationId),
                Is.True);

            NvencRunCaptureCompleteRecoveryCleanupReceipt receipt = result.Receipt;
            Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureCompleteReceipt, operation.CaptureCompleteReceipt),
                Is.True);
            Assert.That(ReferenceEquals(
                    receipt.CaptureCompleteOperation, operation.CaptureCompleteOperation),
                Is.True);
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

        private static void AssertReadonlyFields(Type type, params Type[] expected)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(expected.Length), type.Name);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held value must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(expected), type.Name);
        }

        /// <summary>
        /// Runs a forged result through the coordinator, after first confirming
        /// what that result actually names, so a correlation check cannot pass
        /// for the wrong reason.
        /// </summary>
        private void AssertRejected(
            Func<
                FakeCleaner,
                NvencRunCaptureCompleteRecoveryCleanupOperation,
                NvencRunCaptureCompleteRecoveryCleanupAttemptResult> forge,
            Action<
                NvencRunCaptureCompleteRecoveryCleanupAttemptResult,
                FakeCleaner,
                NvencRunCaptureCompleteRecoveryCleanupOperation> assertShape)
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(Matches);
            cleaner.Forge = (self, given) =>
            {
                NvencRunCaptureCompleteRecoveryCleanupAttemptResult forged = forge(self, given);
                assertShape(forged, self, given);
                return forged;
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(cleaner)
                    .Execute(operation));
            Assert.That(cleaner.CallCount, Is.EqualTo(1));
        }

        /// <summary>
        /// Builds a result through its private constructor, the same limited
        /// technique the existing cleanup fixtures use for corrupt shapes.
        /// </summary>
        private static NvencRunCaptureCompleteRecoveryCleanupAttemptResult MakeResult(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation,
            NvencRunCaptureCompleteRecoveryCleanupReceipt receipt,
            NvencRunCaptureCompleteRecoveryCleanupStatus status)
        {
            ConstructorInfo ctor =
                typeof(NvencRunCaptureCompleteRecoveryCleanupAttemptResult).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(INvencRunCaptureCompleteRecoveryCleaner),
                        typeof(NvencRunCaptureCompleteRecoveryCleanupOperation),
                        typeof(NvencRunCaptureCompleteRecoveryCleanupReceipt),
                        typeof(NvencRunCaptureCompleteRecoveryCleanupStatus),
                    },
                    null);

            Assert.That(ctor, Is.Not.Null, "attempt result constructor not found.");

            return (NvencRunCaptureCompleteRecoveryCleanupAttemptResult)ctor.Invoke(
                new object[] { cleaner, operation, receipt, status });
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// A cleanup operation for a Run whose final Capture Index was already
        /// authoritative, with the given temporary observation.
        /// </summary>
        private NvencRunCaptureCompleteRecoveryCleanupOperation MakeOperation(
            NvencRunCaptureIndexObservationStatus finalIndex)
        {
            return NvencRunCaptureCompleteRecoveryCleanupOperation.Create(
                MakeOrchestration().Execute(Classify(finalIndex, Absent)));
        }

        /// <summary>A cleanup operation for a Run that committed its Capture Index.</summary>
        private NvencRunCaptureCompleteRecoveryCleanupOperation MakeCommittedOperation(
            out NvencRunCaptureIndexRecoveryCommitReceipt commitReceipt)
        {
            NvencRunCaptureCompleteRecoveryReceipt receipt =
                MakeOrchestration().Execute(Classify(Absent, Absent));

            commitReceipt = receipt.CaptureIndexRecoveryCommitReceipt;
            return NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt);
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
        /// Records the operation it was given and returns the shape the test
        /// asked for. It deletes nothing: cleanup targets and order belong to
        /// the production cleaner.
        /// </summary>
        private sealed class FakeCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal NvencRunCaptureCompleteRecoveryCleanupStatus Status { get; set; } =
                NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned;

            internal Func<
                FakeCleaner,
                NvencRunCaptureCompleteRecoveryCleanupOperation,
                NvencRunCaptureCompleteRecoveryCleanupAttemptResult> Forge
            { get; set; }

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

                if (Forge != null)
                {
                    return Forge(this, operation);
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
