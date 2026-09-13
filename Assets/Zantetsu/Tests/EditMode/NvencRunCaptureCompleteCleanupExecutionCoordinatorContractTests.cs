using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Fresh NVENC CaptureComplete cleanup
    /// synchronous boundary: the cleaner interface, the immutable receipt, the
    /// readonly attempt result, and the single-call execution coordinator. Uses
    /// the fully published and CaptureComplete-ed Run pipeline to mint a valid
    /// cleanup operation; no real GPU, NVENC, filesystem, sleep, or short
    /// negative wait is used.
    /// </summary>
    public class NvencRunCaptureCompleteCleanupExecutionCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Execute ----

        [Test]
        public void Constructor_NullCleaner_Throws()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteCleanupExecutionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("cleaner"));
        }

        [Test]
        public void Execute_NullOrInvalidOperation_CleanerNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner);

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => coordinator.Execute(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(cleaner.CallCount, Is.EqualTo(0));

                // A poisoned process invalidates the operation through the
                // ordinary API.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(operation.IsValid, Is.False);

                ArgumentException invalidEx = Assert.Throws<ArgumentException>(
                    () => coordinator.Execute(operation));
                Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
                Assert.That(cleaner.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Execute_Cleaned_ExactCleanerOperationReceiptCorrelation()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner).Execute(operation);

                Assert.That(cleaner.CallCount, Is.EqualTo(1));
                Assert.That(result.IsCleaned, Is.True);
                Assert.That(result.IsFailed, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Cleaned));
                Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsValid, Is.True);
                Assert.That(result.Receipt.IsIssuedFor(cleaner, operation), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Cleaner, cleaner), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Execute_Failed_NoReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner
                {
                    Status = NvencRunCaptureCompleteCleanupStatus.Failed,
                };
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner).Execute(operation);

                Assert.That(cleaner.CallCount, Is.EqualTo(1));
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.IsCleaned, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Failed));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Execute_CallsCleanExactlyOnce()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner);

                coordinator.Execute(operation);
                Assert.That(cleaner.CallCount, Is.EqualTo(1));

                // A second execution is the caller's decision, never the
                // coordinator's retry.
                coordinator.Execute(operation);
                Assert.That(cleaner.CallCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Execute_CleanerException_PropagatesSameInstance_NoRetry()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                FakeCleaner cleaner = new FakeCleaner { ExceptionToThrow = boom };
                NvencRunCaptureCompleteCleanupExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner);

                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => coordinator.Execute(operation));
                Assert.That(ReferenceEquals(thrown, boom), Is.True);
                Assert.That(cleaner.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_DefaultResult_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner
                {
                    UseOverride = true,
                    OverrideResult = default,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner).Execute(operation));
                Assert.That(cleaner.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_ForeignCleanerOrOperationResult_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                // A foreign cleaner's attempt result.
                FakeCleaner foreignCleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupAttemptResult foreignAttempt =
                    NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(foreignCleaner, operation);
                FakeCleaner lyingCleaner = new FakeCleaner
                {
                    UseOverride = true,
                    OverrideResult = foreignAttempt,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(lyingCleaner).Execute(operation));
                Assert.That(lyingCleaner.CallCount, Is.EqualTo(1));

                // A different operation's attempt result.
                using (Harness h2 = Harness.Create())
                {
                    NvencRunCaptureCompleteCleanupOperation otherOperation = PrepareCleanup(h2);
                    NvencRunCaptureCompleteCleanupAttemptResult otherAttempt =
                        NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(
                            new FakeCleaner(), otherOperation);
                    FakeCleaner lyingCleaner2 = new FakeCleaner
                    {
                        UseOverride = true,
                        OverrideResult = otherAttempt,
                    };
                    Assert.Throws<InvalidOperationException>(() =>
                        new NvencRunCaptureCompleteCleanupExecutionCoordinator(lyingCleaner2).Execute(operation));
                    Assert.That(lyingCleaner2.CallCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void Execute_ForeignReceipt_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner();
                FakeCleaner otherCleaner = new FakeCleaner();

                // A Cleaned result whose receipt was minted for a different
                // cleaner must be refused even though the result's own cleaner
                // and operation match.
                NvencRunCaptureCompleteCleanupReceipt foreignReceipt =
                    NvencRunCaptureCompleteCleanupReceipt.Create(otherCleaner, operation);

                AssertRejected(
                    cleaner,
                    operation,
                    MakeAttempt(
                        cleaner, operation, foreignReceipt,
                        NvencRunCaptureCompleteCleanupStatus.Cleaned),
                    "Cleaned carrying another cleaner's receipt");
            }
        }

        [Test]
        public void Execute_StatusAndReceiptMismatchOrUndefinedStatus_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupReceipt receipt =
                    NvencRunCaptureCompleteCleanupReceipt.Create(cleaner, operation);

                // Every forged result below names this exact cleaner and this
                // exact operation, so only the status/receipt disagreement is
                // left to reject it.

                AssertRejected(
                    cleaner,
                    operation,
                    MakeAttempt(cleaner, operation, null, NvencRunCaptureCompleteCleanupStatus.Cleaned),
                    "Cleaned without a receipt");

                AssertRejected(
                    cleaner,
                    operation,
                    MakeAttempt(cleaner, operation, receipt, NvencRunCaptureCompleteCleanupStatus.Failed),
                    "Failed carrying a receipt");

                AssertRejected(
                    cleaner,
                    operation,
                    MakeAttempt(cleaner, operation, null, NvencRunCaptureCompleteCleanupStatus.None),
                    "None status");

                AssertRejected(
                    cleaner,
                    operation,
                    MakeAttempt(
                        cleaner, operation, receipt, (NvencRunCaptureCompleteCleanupStatus)7),
                    "undefined status");
            }
        }

        // ---- Forwarding and shape ----

        [Test]
        public void ResultAndReceipt_ForwardTheOperationGraphWithoutDuplicateFields()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                FakeCleaner cleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner).Execute(operation);
                NvencRunCaptureCompleteCleanupReceipt receipt = result.Receipt;

                // Every forwarded value is the operation's exact reference, so
                // nothing is copied into a field of its own.
                Assert.That(ReferenceEquals(
                    result.CaptureCompleteReceipt, operation.CaptureCompleteReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    result.CaptureCompleteOperation, operation.CaptureCompleteOperation), Is.True);
                Assert.That(ReferenceEquals(
                    result.CaptureIndexCommitReceipt, operation.CaptureIndexCommitReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    result.ArtifactPublicationReceipt, operation.ArtifactPublicationReceipt), Is.True);
                Assert.That(ReferenceEquals(result.Plan, operation.Plan), Is.True);
                Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
                Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
                Assert.That(ReferenceEquals(
                    result.RunInitializationId, operation.RunInitializationId), Is.True);

                Assert.That(ReferenceEquals(
                    receipt.CaptureCompleteReceipt, operation.CaptureCompleteReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.CaptureCompleteOperation, operation.CaptureCompleteOperation), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.CaptureIndexCommitReceipt, operation.CaptureIndexCommitReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.ArtifactPublicationReceipt, operation.ArtifactPublicationReceipt), Is.True);
                Assert.That(ReferenceEquals(receipt.Plan, operation.Plan), Is.True);
                Assert.That(ReferenceEquals(receipt.RootLayout, operation.RootLayout), Is.True);
                Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
                Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, operation.RunInitializationId), Is.True);
            }
        }

        [Test]
        public void Receipt_TwoReadonlyFields_SealedInternal_NotDisposable()
        {
            Type type = typeof(NvencRunCaptureCompleteCleanupReceipt);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));

            // Verify by field-type set, never by reflection return order or
            // private field names: a harmless rename must not break this test.
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(INvencRunCaptureCompleteCleaner),
                    typeof(NvencRunCaptureCompleteCleanupOperation),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void AttemptResult_FourReadonlyFields_ReadonlyStruct()
        {
            Type type = typeof(NvencRunCaptureCompleteCleanupAttemptResult);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(4));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(INvencRunCaptureCompleteCleaner),
                    typeof(NvencRunCaptureCompleteCleanupOperation),
                    typeof(NvencRunCaptureCompleteCleanupReceipt),
                    typeof(NvencRunCaptureCompleteCleanupStatus),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Coordinator_SingleCleanerField_NotDisposable()
        {
            Type type = typeof(NvencRunCaptureCompleteCleanupExecutionCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(1));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[] { typeof(INvencRunCaptureCompleteCleaner) }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Status_AppendOnlyExplicitValues()
        {
            Assert.That((int)NvencRunCaptureCompleteCleanupStatus.None, Is.EqualTo(0));
            Assert.That((int)NvencRunCaptureCompleteCleanupStatus.Cleaned, Is.EqualTo(1));
            Assert.That((int)NvencRunCaptureCompleteCleanupStatus.Failed, Is.EqualTo(2));

            // No partial-progress, per-target, outcome-unknown, or retryable
            // shape.
            Assert.That(
                Enum.GetNames(typeof(NvencRunCaptureCompleteCleanupStatus)), Has.Length.EqualTo(3));
        }

        // ---- Run state ----

        [Test]
        public void Execute_LeavesRunAndServiceStateUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                NvencRunChunkContextState contextState = h.Context.State;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                bool serviceStopped = h.Service.IsStopped;
                bool leaseCreated = h.SessionIssue.OwnershipLease.IsCreated;
                bool leaseValid = h.SessionIssue.IsValid;
                int publisherCalls = h.Publisher.CallCount;
                int committerCalls = h.Committer.CallCount;
                int indexCommitterCalls = h.IndexCommitter.CallCount;
                int completerCalls = h.RunCompleter.CallCount;

                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult before), Is.True);

                FakeCleaner cleaner = new FakeCleaner();
                NvencRunCaptureCompleteCleanupExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner);

                coordinator.Execute(operation);
                AssertUnchanged();

                cleaner.Status = NvencRunCaptureCompleteCleanupStatus.Failed;
                coordinator.Execute(operation);
                AssertUnchanged();

                void AssertUnchanged()
                {
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                    Assert.That(h.Slot.State, Is.EqualTo(slotState));
                    Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                    Assert.That(h.Context.State, Is.EqualTo(contextState));
                    Assert.That(h.Service.State, Is.EqualTo(serviceState));
                    Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                    Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                    Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                    Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                    Assert.That(h.State.IsPoisoned, Is.False);

                    // No earlier collaborator is contacted again.
                    Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                    Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                    Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                    Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));

                    // The retained CaptureComplete result is reused, never
                    // replaced.
                    Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                        out NvencRunCaptureCompleteAttemptResult after), Is.True);
                    Assert.That(ReferenceEquals(after.Operation, before.Operation), Is.True);
                    Assert.That(ReferenceEquals(after.Receipt, before.Receipt), Is.True);
                    Assert.That(operation.IsValid, Is.True);
                }
            }
        }

        // ---- Cleanup fixture helpers ----

        /// <summary>
        /// Executes the forged result through the exact cleaner it names, so
        /// the corrupt part under test is the only thing that can reject it. A
        /// helper that used a fresh cleaner would be rejected by the foreign
        /// cleaner check first and would pass even if the status and receipt
        /// verification regressed.
        /// </summary>
        private static void AssertRejected(
            FakeCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation,
            NvencRunCaptureCompleteCleanupAttemptResult forged,
            string message)
        {
            Assert.That(ReferenceEquals(forged.Cleaner, cleaner), Is.True,
                message + ": the forged result must name the executing cleaner.");
            Assert.That(ReferenceEquals(forged.Operation, operation), Is.True,
                message + ": the forged result must name the executed operation.");

            int before = cleaner.CallCount;
            cleaner.UseOverride = true;
            cleaner.OverrideResult = forged;

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteCleanupExecutionCoordinator(cleaner).Execute(operation),
                message);
            Assert.That(cleaner.CallCount, Is.EqualTo(before + 1), message);
        }

        private static NvencRunCaptureCompleteCleanupAttemptResult MakeAttempt(
            INvencRunCaptureCompleteCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation,
            NvencRunCaptureCompleteCleanupReceipt receipt,
            NvencRunCaptureCompleteCleanupStatus status)
        {
            ConstructorInfo ctor = typeof(NvencRunCaptureCompleteCleanupAttemptResult).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(INvencRunCaptureCompleteCleaner),
                    typeof(NvencRunCaptureCompleteCleanupOperation),
                    typeof(NvencRunCaptureCompleteCleanupReceipt),
                    typeof(NvencRunCaptureCompleteCleanupStatus),
                },
                null);
            Assert.That(ctor, Is.Not.Null, "attempt result constructor not found.");
            return (NvencRunCaptureCompleteCleanupAttemptResult)ctor.Invoke(
                new object[] { cleaner, operation, receipt, status });
        }

        private static NvencRunCaptureCompleteCleanupOperation PrepareCleanup(Harness h)
        {
            NvencRunCaptureCompleteOperation captureComplete = PrepareCaptureComplete(h);
            Assert.That(captureComplete, Is.Not.Null);

            h.RunCompleter.Status = NvencRunCaptureCompleteStatus.Completed;
            Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
            WaitForServiceStop(h, "publication worker did not stop after CaptureComplete");
            Assert.That(h.RunCoordinator.TryCollectCaptureComplete(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                out NvencRunCaptureCompleteCleanupOperation operation), Is.True);
            return operation;
        }

        private sealed class FakeCleaner : INvencRunCaptureCompleteCleaner
        {
            private int _callCount;
            internal NvencRunCaptureCompleteCleanupStatus Status =
                NvencRunCaptureCompleteCleanupStatus.Cleaned;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureCompleteCleanupAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteCleanupAttemptResult Clean(
                NvencRunCaptureCompleteCleanupOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureCompleteCleanupStatus.Failed)
                {
                    return NvencRunCaptureCompleteCleanupAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        // ---- Fixture helpers ----

        private static NvencRunCaptureCompleteOperation PrepareCaptureComplete(Harness h)
        {
            PrepareCaptureIndexCommit(h);
            h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
            WaitForServiceState(h, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                "service did not publish the committed capture index terminal");
            Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                out NvencRunCaptureCompleteOperation operation), Is.True);
            return operation;
        }

        private sealed class FakeCompleter : INvencRunCaptureCompleter
        {
            private int _callCount;
            internal NvencRunCaptureCompleteStatus Status = NvencRunCaptureCompleteStatus.Completed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureCompleteAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteAttemptResult Complete(
                NvencRunCaptureCompleteOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureCompleteStatus.Failed)
                {
                    return NvencRunCaptureCompleteAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureCompleteAttemptResult.Completed(this, operation);
            }
        }

        // ---- Helpers ----

        private static NvencRunCaptureIndexCommitOperation PrepareCaptureIndexCommit(Harness h)
        {
            FinalizeOnly(h);
            Assert.That(h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out _), Is.True);
            h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
            WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                "service did not publish the committed plan terminal");
            Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(out _), Is.True);
            h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
            Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
            WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");
            Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                out NvencRunCaptureIndexCommitOperation operation), Is.True);
            return operation;
        }

        /// <summary>
        /// Waits for the artifact terminal. A Published result keeps the same
        /// Worker parked for the Capture Index phase, so only a Failed result
        /// also stops it, and the Service refuses to collect a Failed terminal
        /// until the Worker has physically stopped.
        /// </summary>
        private static void WaitForArtifactTerminal(Harness h, string message)
        {
            SpinWait.SpinUntil(
                () => h.Service.State == NvencRunPublicationServiceState.ArtifactPublicationCompleted,
                WatchdogTimeoutMs);
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.ArtifactPublicationCompleted), message);

            if (h.Publisher.Status != NvencRunArtifactPublicationStatus.Published)
            {
                WaitForServiceStop(h, message);
            }
        }

        private static void WaitForServiceStop(Harness h, string message)
        {
            FieldInfo field = typeof(NvencRunPublicationService).GetField(
                "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
            Thread worker = (Thread)field?.GetValue(h.Service);
            if (worker != null)
            {
                Assert.That(worker.Join(WatchdogTimeoutMs), Is.True, message);
            }

            Assert.That(h.Service.IsStopped, Is.True, message);
        }

        private static void WaitForServiceState(
            Harness h,
            NvencRunPublicationServiceState expected,
            string message)
        {
            SpinWait.SpinUntil(() => h.Service.State == expected, WatchdogTimeoutMs);
            Assert.That(h.Service.State, Is.EqualTo(expected), message);
        }

        /// <summary>
        /// Collects the requested Run chunk terminal by confirming the real
        /// condition inside a watchdog. A settle observed after the request may
        /// be a raise that was already in flight when the request was accepted,
        /// so it is used only as a wake hint; see TerminalConvergence.
        /// </summary>
        private static NvencRunChunkTerminalOutcome CollectTerminal(Harness h, string message)
        {
            return TerminalConvergence.Collect(
                h.RunCoordinator.TryCollectTerminal, h.Worker, h.SettledEvent, WatchdogTimeoutMs, message);
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void FinalizeOnly(Harness h)
        {
            StopFinalizedBackend(h);
            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);
            ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
            FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
        }

        private static void StopFinalizedBackend(Harness h)
        {
            h.AcceptAndAppendChunk(1, 64, Seed);
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            Assert.That(h.RunCoordinator.TryReflectCompletion(
                MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            CollectTerminal(h, "worker did not converge the finalize request");

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            Assert.That(h.Worker.TeardownCompleted, Is.True);
            Assert.That(h.Worker.IsStopped, Is.True);

            h.SubmitWorker.Dispose();
        }

        private static ForcedDropFrameIdSet MakeForcedDropSet(Harness h)
        {
            h.DraftQueue.BeginProducerDrain();
            h.DraftQueue.CloseAfterProducerJoin();
            TerminalIntentOwnershipSnapshot snapshot = h.DraftQueue.CreateOwnershipSnapshot(0);
            return h.DraftRegistry.ForceDropPendingForFreeze(h.DraftQueue, snapshot);
        }

        private static FreezeTerminalCheckpoint MakeCheckpoint(Harness h)
        {
            return new FreezeTerminalCheckpoint(
                1000, 1, 1, Thread.CurrentThread.ManagedThreadId, h.Context.TestRunId);
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, 0, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(layout, InitId);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        // ---- Fakes ----

        private sealed class FakeCaptureIndexCommitter : INvencRunCaptureIndexCommitter
        {
            private int _callCount;
            internal NvencRunCaptureIndexCommitStatus Status = NvencRunCaptureIndexCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureIndexCommitAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexCommitAttemptResult Commit(
                NvencRunCaptureIndexCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureIndexCommitStatus.Failed)
                {
                    return NvencRunCaptureIndexCommitAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureIndexCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            private int _callCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Status == NvencRunPublicationPlanCommitStatus.FailedBeforeRename)
                {
                    return NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(this, operation);
                }

                if (Status == NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown)
                {
                    return NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(this, operation);
                }

                return NvencRunPublicationPlanCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakePublisher : INvencRunArtifactPublisher
        {
            private int _callCount;
            internal NvencRunArtifactPublicationStatus Status = NvencRunArtifactPublicationStatus.Published;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Status == NvencRunArtifactPublicationStatus.Failed)
                {
                    return NvencRunArtifactPublicationAttemptResult.Failed(this, operation);
                }

                return NvencRunArtifactPublicationAttemptResult.Published(this, operation);
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
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

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        private sealed class PatternSource : INvencOutputBitstreamSource
        {
            private readonly int _length;
            private readonly byte _seed;

            internal PatternSource(int length, byte seed)
            {
                _length = length;
                _seed = seed;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = 1024;
                return true;
            }
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            public bool TryGetEvidence(in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                evidence = default;
                return false;
            }
        }

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter
        {
            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                return true;
            }
        }

        private sealed class FakeTeardown : INvencOutputWorkerTeardown
        {
            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class FakeMainThreadTeardown : INvencMainThreadTextureTeardown
        {
            internal NvencRunChunkContext BoundContext;

            public bool IsBoundTo(NvencRunChunkContext context)
            {
                return BoundContext != null && context != null && ReferenceEquals(BoundContext, context);
            }

            public NvencMainThreadTextureTeardownReceipt TearDown()
            {
                return new NvencMainThreadTextureTeardownReceipt(this, BoundContext);
            }
        }

        /// <summary>
        /// Identity-only Session Ownership Lease releaser: this fixture never
        /// releases a lease, but the Run Coordinator requires the exact release
        /// Execution Coordinator its receipts must come from.
        /// </summary>
        private sealed class FakeSessionOwnershipReleaser : INvencRunSessionOwnershipReleaser
        {
            public NvencRunSessionOwnershipReleaseReceipt Release(
                NvencRunSessionOwnershipReleaseOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!NvencRunSessionOwnershipReleaseAdmission.IsAdmissible(operation))
                {
                    throw new ArgumentException(
                        "The operation cannot start a release attempt.", nameof(operation));
                }

                operation.OwnershipLease.Dispose();
                return NvencRunSessionOwnershipReleaseReceipt.Create(this, operation);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer;
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator FinalizationCoordinator;
            internal NvencRunChunkContext Context;
            internal NvencRunLocalRegistrySlot Slot;

            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal FakeOutputSource Source;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal FakeTeardown Teardown;
            internal FakeMainThreadTeardown MainThreadTeardown;

            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencCaptureRunCoordinator RunCoordinator;
            internal NvencCaptureBackendJoinCoordinator BackendJoin;
            internal ManualResetEventSlim SettledEvent;

            internal FakeCommitter Committer;
            internal FakePublisher Publisher;
            internal FakeCaptureIndexCommitter IndexCommitter;
            internal FakeCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal FakeCleaner CleanupCleaner;
            internal NvencRunCaptureCompleteCleanupExecutionCoordinator CleanupExecution;
            internal FakeSessionOwnershipReleaser Releaser;
            internal NvencRunSessionOwnershipReleaseExecutionCoordinator ReleaseExecution;

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            private readonly Action _settledHandler;

            internal bool SubmitDrained
            {
                set => SetField(SubmitWorker, "_drainCompleted", value);
            }

            internal Harness()
            {
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue();
                Context = new NvencRunChunkContext(SessionIssue, Sink, FinalizationCoordinator, "chunk/0");
                Slot = new NvencRunLocalRegistrySlot(Context);

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Source = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);

                SubmitSyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitSubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                SubmitReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitSyncSlots,
                    SubmitToOutputCredits, FrameCompletionCredits,
                    new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmitSubmissionQueue, OutputQueue,
                    WorkSlots, SampleSlots, SubmitReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                MainThreadTeardown = new FakeMainThreadTeardown { BoundContext = Context };
                BackendJoin = new NvencCaptureBackendJoinCoordinator(
                    State, SubmitWorker, Worker, Context,
                    WorkSlots, SampleSlots, SubmitSyncSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer, Processor, MainThreadTeardown);

                TraceLogger = new TraceLogger(16, Context.TestRunId);
                TraceRecorder = new TraceFlightRecorder(TraceLogger, 16, 2);
                TraceRunContext traceRunContext = new TraceRunContext(
                    Context.TestRunId, 1000, "build-1", "6000.3.22f1", Hash64, "scene-1", 12345, 0.02, 3, "High", 1,
                    new Vector3(0f, -4.9f, 0f));
                CaptureDraftRunContext draftRun = new CaptureDraftRunContext(traceRunContext, 100, 5);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(5, 4096, 2, 4);
                DraftRegistry = new CaptureFrameDraftRegistry(draftRun, traceProfile);
                DraftQueue = new CaptureFrameDraftTerminalIntentQueue(DraftRegistry, traceProfile);
                FreezeTerminalTraceBufferBuilder freezeBuilder = new FreezeTerminalTraceBufferBuilder(DraftRegistry);
                FreezeTerminalCoordinator = new CaptureFrameFreezeTerminalCoordinator(TraceRecorder, freezeBuilder);
                TraceFreeze = new NvencTraceFreezeCoordinator(
                    TraceLogger, TraceRecorder, FreezeTerminalCoordinator, Context, SessionIssue);

                Committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator commitCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(Committer);
                Publisher = new FakePublisher();
                NvencRunArtifactPublicationExecutionCoordinator artifactCoordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(Publisher);
                IndexCommitter = new FakeCaptureIndexCommitter();
                NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCoordinator =
                    new NvencRunCaptureIndexCommitExecutionCoordinator(IndexCommitter);
                RunCompleter = new FakeCompleter();
                NvencRunCaptureCompleteExecutionCoordinator captureCompleteCoordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(RunCompleter);
                Service = new NvencRunPublicationService(
                    State, commitCoordinator, artifactCoordinator, captureIndexCoordinator,
                    captureCompleteCoordinator);

                CleanupCleaner = new FakeCleaner();
                CleanupExecution =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(CleanupCleaner);

                Releaser = new FakeSessionOwnershipReleaser();
                ReleaseExecution =
                    new NvencRunSessionOwnershipReleaseExecutionCoordinator(Releaser);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin,
                    SessionIssue, TraceFreeze, Service, CleanupExecution, ReleaseExecution);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }

            internal void WaitForPhysicalStop(string message)
            {
                FieldInfo field = typeof(NvencOrderedOutputWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread workerThread = (Thread)field?.GetValue(Worker);
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }

            public void Dispose()
            {
                if (!Worker.IsStopped)
                {
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;
                SettledEvent.Dispose();

                if (!Service.IsStopped)
                {
                    if (!Service.TryStopWithoutRequest())
                    {
                        State.TryPoison();
                        Service.Notify();
                    }
                }

                FieldInfo serviceField = typeof(NvencRunPublicationService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread serviceThread = (Thread)serviceField?.GetValue(Service);
                if (serviceThread != null)
                {
                    Assert.That(serviceThread.Join(WatchdogTimeoutMs), Is.True,
                        "publication service worker did not physically exit during teardown");
                }

                Service.Dispose();
            }
        }
    }
}
