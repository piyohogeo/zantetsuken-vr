using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC publication recovery orphan
    /// cleanup execution boundary: exactly one attempt per valid operation, the
    /// two terminal shapes and their receipt rule, and the correlation the
    /// result must carry.
    /// </summary>
    /// <remarks>
    /// The cleaner is a small fake that deletes nothing: what an orphan cleanup
    /// targets and in which order belongs to the production cleaner's own unit.
    /// Corrupt results are built through the result's private constructor, the
    /// same limited technique the existing cleanup fixtures use, and each
    /// forged result is first asserted to name the cleaner and operation under
    /// test so the correlation checks cannot pass vacuously.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteCleanupExecutionContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

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
                () => new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("cleaner"));
        }

        [Test]
        public void Execute_NullOperation_RejectedWithoutTouchingTheCleaner()
        {
            FakeCleaner cleaner = new FakeCleaner();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(cleaner.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_OperationInvalidatedByAReleasedLock_RejectedWithoutTouchingTheCleaner()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            ReleaseAllLocks();
            Assert.That(operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(cleaner.CallCount, Is.EqualTo(0));
        }

        // ---- The two terminal shapes ----

        [Test]
        public void Execute_CleanedAttempt_ReturnsTheExactResultAndReceiptOnce()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(cleaner.LastOperation, operation), Is.True);

            Assert.That(result.Status,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned));
            Assert.That(result.IsCleaned, Is.True);
            Assert.That(result.IsFailed, Is.False);
            Assert.That(result.IsNone, Is.False);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);
            Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
            Assert.That(ReferenceEquals(result.CleanupOperation, operation), Is.True);

            NvencRunPublicationRecoveryIncompleteCleanupReceipt receipt = result.Receipt;
            Assert.That(receipt, Is.Not.Null);
            Assert.That(ReferenceEquals(receipt, cleaner.LastIssuedReceipt), Is.True);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IsIssuedFor(cleaner, operation), Is.True);
            Assert.That(ReferenceEquals(receipt.Cleaner, cleaner), Is.True);
            Assert.That(ReferenceEquals(receipt.CleanupOperation, operation), Is.True);
        }

        [Test]
        public void Execute_FailedAttempt_ReturnsTheExactResultWithNoReceiptOnce()
        {
            FakeCleaner cleaner = new FakeCleaner { Fail = true };
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);

            Assert.That(cleaner.CallCount, Is.EqualTo(1));
            Assert.That(result.Status,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
            Assert.That(result.IsFailed, Is.True);
            Assert.That(result.IsCleaned, Is.False);
            Assert.That(result.IsNone, Is.False);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);
            Assert.That(result.Receipt, Is.Null);
        }

        [Test]
        public void Execute_CleanerException_PropagatesByTheSameReferenceWithoutRepeating()
        {
            InvalidOperationException thrown = new InvalidOperationException("cleanup faulted.");
            FakeCleaner cleaner = new FakeCleaner { Throw = thrown };
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
                () => new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation));

            Assert.That(ReferenceEquals(caught, thrown), Is.True);
            Assert.That(cleaner.CallCount, Is.EqualTo(1));
        }

        // ---- Refused results ----

        [Test]
        public void Execute_DefaultResult_Rejected()
        {
            AssertRejected(
                (self, operation) => default,
                (forged, self, operation) =>
                {
                    Assert.That(forged.IsNone, Is.True);
                    Assert.That(forged.Cleaner, Is.Null);
                    Assert.That(forged.IsValid, Is.False);
                });
        }

        [Test]
        public void Execute_ForeignCleaner_Rejected()
        {
            FakeCleaner foreign = new FakeCleaner();

            AssertRejected(
                (self, operation) =>
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        foreign, operation),
                (forged, self, operation) =>
                {
                    // A well-formed result of the wrong cleaner: only the
                    // cleaner correlation may reject it.
                    Assert.That(forged.IsValid, Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.True);
                    Assert.That(forged.IsIssuedFor(foreign, operation), Is.True);
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.False);
                });
        }

        [Test]
        public void Execute_ForeignOperation_Rejected()
        {
            NvencRunPublicationRecoveryIncompleteCleanupOperation other = MakeOperation();

            AssertRejected(
                (self, operation) =>
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(self, other),
                (forged, self, operation) =>
                {
                    // A well-formed result of another Run's operation.
                    Assert.That(forged.IsValid, Is.True);
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.True);
                    Assert.That(forged.IsIssuedFor(self, other), Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.False);
                });
        }

        [Test]
        public void Execute_ForeignReceipt_Rejected()
        {
            FakeCleaner foreign = new FakeCleaner();

            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    NvencRunPublicationRecoveryIncompleteCleanupReceipt.Cleaned(foreign, operation),
                    NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned),
                (forged, self, operation) =>
                {
                    // The result names this cleaner and this operation; only
                    // its receipt was minted for someone else.
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.True);
                    Assert.That(forged.Receipt, Is.Not.Null);
                    Assert.That(forged.Receipt.IsIssuedFor(foreign, operation), Is.True);
                    Assert.That(forged.Receipt.IsIssuedFor(self, operation), Is.False);
                    Assert.That(forged.IsValid, Is.False);
                });
        }

        [Test]
        public void Execute_CleanedWithoutItsReceipt_Rejected()
        {
            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    null,
                    NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned),
                (forged, self, operation) =>
                {
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.True);
                    Assert.That(forged.Status,
                        Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned));
                    Assert.That(forged.Receipt, Is.Null);
                    Assert.That(forged.IsValid, Is.False);
                });
        }

        [Test]
        public void Execute_FailedCarryingAReceipt_Rejected()
        {
            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    NvencRunPublicationRecoveryIncompleteCleanupReceipt.Cleaned(self, operation),
                    NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed),
                (forged, self, operation) =>
                {
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.True);
                    Assert.That(forged.Status,
                        Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
                    // The receipt itself is well correlated; a failed attempt
                    // simply may not carry one.
                    Assert.That(forged.Receipt.IsIssuedFor(self, operation), Is.True);
                    Assert.That(forged.IsValid, Is.False);
                });
        }

        [Test]
        public void Execute_NoneStatusOnACorrelatedResult_Rejected()
        {
            AssertRejected(
                (self, operation) => MakeResult(
                    self,
                    operation,
                    null,
                    NvencRunPublicationRecoveryIncompleteCleanupStatus.None),
                (forged, self, operation) =>
                {
                    // Correlation is intact, so only the status may reject it.
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.True);
                    Assert.That(forged.CleanupOperation.IsValid, Is.True);
                    Assert.That(forged.IsNone, Is.True);
                    Assert.That(forged.IsValid, Is.False);
                });
        }

        [Test]
        public void Execute_UndefinedStatus_Rejected()
        {
            NvencRunPublicationRecoveryIncompleteCleanupStatus undefined =
                (NvencRunPublicationRecoveryIncompleteCleanupStatus)7;

            AssertRejected(
                (self, operation) => MakeResult(self, operation, null, undefined),
                (forged, self, operation) =>
                {
                    Assert.That(ReferenceEquals(forged.Cleaner, self), Is.True);
                    Assert.That(ReferenceEquals(forged.CleanupOperation, operation), Is.True);
                    Assert.That(forged.CleanupOperation.IsValid, Is.True);
                    Assert.That(forged.IsNone, Is.False, "not the uninitialized default.");
                    Assert.That(forged.Status, Is.EqualTo(undefined));
                    Assert.That(forged.IsValid, Is.False);
                });
        }

        // ---- Forward graph ----

        [Test]
        public void Result_AndItsReceipt_ForwardTheSameOperationGraph()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);
            NvencRunPublicationRecoveryIncompleteCleanupReceipt receipt = result.Receipt;

            Assert.That(ReferenceEquals(result.Decision, operation.Decision), Is.True);
            Assert.That(ReferenceEquals(result.Snapshot, operation.Snapshot), Is.True);
            Assert.That(ReferenceEquals(result.OwnershipLease, operation.OwnershipLease), Is.True);
            Assert.That(ReferenceEquals(result.OpenOutcome, operation.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(result.LockIdentityEvidence, operation.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
            Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
            Assert.That(
                ReferenceEquals(result.RunInitializationId, operation.RunInitializationId),
                Is.True);

            Assert.That(ReferenceEquals(receipt.Decision, result.Decision), Is.True);
            Assert.That(ReferenceEquals(receipt.Snapshot, result.Snapshot), Is.True);
            Assert.That(ReferenceEquals(receipt.OwnershipLease, result.OwnershipLease), Is.True);
            Assert.That(ReferenceEquals(receipt.OpenOutcome, result.OpenOutcome), Is.True);
            Assert.That(
                ReferenceEquals(receipt.LockIdentityEvidence, result.LockIdentityEvidence),
                Is.True);
            Assert.That(ReferenceEquals(receipt.RootLayout, result.RootLayout), Is.True);
            Assert.That(receipt.TestRunId, Is.EqualTo(result.TestRunId));
            Assert.That(
                ReferenceEquals(receipt.RunInitializationId, result.RunInitializationId),
                Is.True);
        }

        [Test]
        public void Result_AndReceipt_BecomeInvalidOnceTheLockIsReleased()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation);
            NvencRunPublicationRecoveryIncompleteCleanupReceipt receipt = result.Receipt;

            ReleaseAllLocks();

            Assert.That(receipt.IsValid, Is.False);
            Assert.That(receipt.IsIssuedFor(cleaner, operation), Is.False);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.IsCleaned, Is.False);
            Assert.That(result.IsIssuedFor(cleaner, operation), Is.False);

            // The status and the forwarded references survive; only validity
            // follows the lock.
            Assert.That(result.Status,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Cleaned));
            Assert.That(ReferenceEquals(receipt.CleanupOperation, operation), Is.True);
        }

        // ---- Factories ----

        [Test]
        public void Factories_NullAndInvalidArguments_Rejected()
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupReceipt.Cleaned(
                        null, operation)).ParamName,
                Is.EqualTo("cleaner"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupReceipt.Cleaned(
                        cleaner, null)).ParamName,
                Is.EqualTo("operation"));

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        null, operation)).ParamName,
                Is.EqualTo("cleaner"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        cleaner, null)).ParamName,
                Is.EqualTo("operation"));

            ReleaseAllLocks();
            Assert.That(operation.IsValid, Is.False);

            Assert.That(
                Assert.Throws<ArgumentException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupReceipt.Cleaned(
                        cleaner, operation)).ParamName,
                Is.EqualTo("operation"));
            Assert.That(
                Assert.Throws<ArgumentException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        cleaner, operation)).ParamName,
                Is.EqualTo("operation"));
            Assert.That(
                Assert.Throws<ArgumentException>(
                    () => NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        cleaner, operation)).ParamName,
                Is.EqualTo("operation"));
        }

        // ---- What this boundary must leave alone ----

        [Test]
        public void Execute_ChangesNothingItWasGiven()
        {
            FakeCleaner cleaner = new FakeCleaner();
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryDecision decision = h.Incomplete();
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation =
                NvencRunPublicationRecoveryIncompleteCleanupOperation.Create(decision, h.Owner);

            AssertNothingTouched(h, operation, decision, snapshot, "before Execute");

            new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                .Execute(operation);

            AssertNothingTouched(h, operation, decision, snapshot, "after Execute");
        }

        // ---- Readonly shape ----

        [Test]
        public void Types_HoldOnlyTheirDeclaredReadonlyReferences()
        {
            AssertReadonlyFields(
                typeof(NvencRunPublicationRecoveryIncompleteCleanupReceipt),
                new[]
                {
                    typeof(INvencRunPublicationRecoveryIncompleteCleaner),
                    typeof(NvencRunPublicationRecoveryIncompleteCleanupOperation),
                });

            AssertReadonlyFields(
                typeof(NvencRunPublicationRecoveryIncompleteCleanupAttemptResult),
                new[]
                {
                    typeof(INvencRunPublicationRecoveryIncompleteCleaner),
                    typeof(NvencRunPublicationRecoveryIncompleteCleanupOperation),
                    typeof(NvencRunPublicationRecoveryIncompleteCleanupReceipt),
                    typeof(NvencRunPublicationRecoveryIncompleteCleanupStatus),
                });

            AssertReadonlyFields(
                typeof(NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator),
                new[] { typeof(INvencRunPublicationRecoveryIncompleteCleaner) });

            Assert.That(
                typeof(NvencRunPublicationRecoveryIncompleteCleanupReceipt).GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public),
                Is.Empty);
            Assert.That(
                typeof(IDisposable).IsAssignableFrom(
                    typeof(NvencRunPublicationRecoveryIncompleteCleanupReceipt)),
                Is.False);
            Assert.That(
                typeof(IDisposable).IsAssignableFrom(
                    typeof(NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator)),
                Is.False);
        }

        // ---- Fixture helpers ----

        private static void AssertReadonlyFields(Type type, Type[] expected)
        {
            Assert.That(type.IsSealed, Is.True, type.Name);

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

        private static void AssertNothingTouched(
            Harness h,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation,
            NvencRunPublicationRecoveryDecision decision,
            NvencRunPublicationRecoveryInspectionSnapshot snapshot,
            string message)
        {
            Assert.That(operation.IsValid, Is.True, message);
            Assert.That(ReferenceEquals(operation.Decision, decision), Is.True, message);
            Assert.That(ReferenceEquals(operation.Snapshot, snapshot), Is.True, message);
            Assert.That(ReferenceEquals(operation.OpenOutcome, h.OpenOutcome), Is.True, message);
            Assert.That(ReferenceEquals(operation.OwnershipLease, h.Owner), Is.True, message);

            Assert.That(decision.IsValid, Is.True, message);
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete), message);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Absent), message);
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True, message);
            Assert.That(h.OpenOutcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired),
                message);
            Assert.That(h.OpenOutcome.Session, Is.Null, message);

            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0), message);
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0), message);
            Assert.That(h.Owner.IsCreated, Is.True, message);
            Assert.That(h.Owner.CanRelease, Is.True, message);
            Assert.That(h.Owner.IsReleaseComplete, Is.False, message);

            // This boundary is filesystem-free.
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False, message);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False, message);
        }

        /// <summary>
        /// Runs a forged result through the coordinator, after first confirming
        /// what that result actually names, so a correlation check cannot pass
        /// for the wrong reason.
        /// </summary>
        private void AssertRejected(
            Func<
                FakeCleaner,
                NvencRunPublicationRecoveryIncompleteCleanupOperation,
                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult> forge,
            Action<
                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult,
                FakeCleaner,
                NvencRunPublicationRecoveryIncompleteCleanupOperation> assertShape)
        {
            FakeCleaner cleaner = new FakeCleaner();
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation = MakeOperation();
            cleaner.Forge = (self, given) =>
            {
                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult forged =
                    forge(self, given);
                assertShape(forged, self, given);
                return forged;
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(cleaner)
                    .Execute(operation));
            Assert.That(cleaner.CallCount, Is.EqualTo(1));
        }

        /// <summary>
        /// Builds a result through its private constructor, the same limited
        /// technique the existing cleanup fixtures use for corrupt shapes.
        /// </summary>
        private static NvencRunPublicationRecoveryIncompleteCleanupAttemptResult MakeResult(
            INvencRunPublicationRecoveryIncompleteCleaner cleaner,
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation,
            NvencRunPublicationRecoveryIncompleteCleanupReceipt receipt,
            NvencRunPublicationRecoveryIncompleteCleanupStatus status)
        {
            ConstructorInfo ctor =
                typeof(NvencRunPublicationRecoveryIncompleteCleanupAttemptResult).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(INvencRunPublicationRecoveryIncompleteCleaner),
                        typeof(NvencRunPublicationRecoveryIncompleteCleanupOperation),
                        typeof(NvencRunPublicationRecoveryIncompleteCleanupReceipt),
                        typeof(NvencRunPublicationRecoveryIncompleteCleanupStatus),
                    },
                    null);

            Assert.That(ctor, Is.Not.Null, "attempt result constructor not found.");

            return (NvencRunPublicationRecoveryIncompleteCleanupAttemptResult)ctor.Invoke(
                new object[] { cleaner, operation, receipt, status });
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private NvencRunPublicationRecoveryIncompleteCleanupOperation MakeOperation()
        {
            Harness h = MakeHarness();

            return NvencRunPublicationRecoveryIncompleteCleanupOperation.Create(
                h.Incomplete(), h.Owner);
        }

        private Harness MakeHarness()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            return new Harness(
                layout,
                openOutcome,
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
                owner,
                firstHandle,
                secondHandle);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
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
            firstHandle = new CountingHandle(pathSet.FirstLockPath);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
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
        /// One Run holding its lease, able to produce the incomplete
        /// publication recovery classification over the same graph.
        /// </summary>
        private sealed class Harness
        {
            private readonly NvencRunPublicationRecoveryInspectionOperation _inspection;

            internal Harness(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryInspectionOperation inspection,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                _inspection = inspection;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            /// <summary>
            /// No finished plan, with the previous process's NVENC precommit
            /// temporary still in place.
            /// </summary>
            internal NvencRunPublicationRecoveryDecision Incomplete()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default));
            }
        }

        /// <summary>
        /// A cleaner that deletes nothing. It reports Cleaned by default, can
        /// report Failed, can throw, and can return a forged result.
        /// </summary>
        private sealed class FakeCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            internal int CallCount { get; private set; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOperation LastOperation
            {
                get;
                private set;
            }

            internal NvencRunPublicationRecoveryIncompleteCleanupReceipt LastIssuedReceipt
            {
                get;
                private set;
            }

            internal bool Fail { get; set; }

            internal Exception Throw { get; set; }

            internal Func<
                FakeCleaner,
                NvencRunPublicationRecoveryIncompleteCleanupOperation,
                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult> Forge
            {
                get;
                set;
            }

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (Throw != null)
                {
                    throw Throw;
                }

                if (Forge != null)
                {
                    return Forge(this, operation);
                }

                if (Fail)
                {
                    return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        this, operation);
                }

                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                    NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        this, operation);
                LastIssuedReceipt = result.Receipt;
                return result;
            }
        }

        /// <summary>A lock handle that counts its own releases.</summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private int _disposeCalls;

            internal CountingHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;
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
