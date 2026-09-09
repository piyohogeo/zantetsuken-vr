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
    /// Contract tests for the Phase 0.11 Fresh NVENC CaptureComplete
    /// synchronous boundary: the completer interface, the immutable receipt,
    /// the readonly attempt result, and the single-call execution coordinator.
    /// Uses the committed, published, and capture-index-committed Run pipeline
    /// to mint a valid CaptureComplete operation; no real GPU, NVENC,
    /// filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunCaptureCompleteExecutionCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Execute ----

        [Test]
        public void Constructor_NullCompleter_Throws()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteExecutionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("completer"));
        }

        [Test]
        public void Execute_NullOrInvalidOperation_CompleterNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter();
                NvencRunCaptureCompleteExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer);

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => coordinator.Execute(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(completer.CallCount, Is.EqualTo(0));

                // A poisoned process invalidates the operation, so the call is
                // refused before the completer is contacted.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(operation.IsValid, Is.False);

                ArgumentException invalidEx = Assert.Throws<ArgumentException>(
                    () => coordinator.Execute(operation));
                Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
                Assert.That(completer.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Execute_Completed_ExactCompleterOperationReceiptCorrelation()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter();
                NvencRunCaptureCompleteAttemptResult result =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer).Execute(operation);

                Assert.That(completer.CallCount, Is.EqualTo(1));
                Assert.That(result.IsCompleted, Is.True);
                Assert.That(result.IsFailed, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureCompleteStatus.Completed));
                Assert.That(ReferenceEquals(result.Completer, completer), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.IsIssuedFor(completer, operation), Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsValid, Is.True);
                Assert.That(result.Receipt.IsIssuedFor(completer, operation), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Completer, completer), Is.True);
                Assert.That(ReferenceEquals(result.Receipt.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Execute_Failed_NoReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter
                {
                    Status = NvencRunCaptureCompleteStatus.Failed,
                };
                NvencRunCaptureCompleteAttemptResult result =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer).Execute(operation);

                Assert.That(completer.CallCount, Is.EqualTo(1));
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.IsCompleted, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureCompleteStatus.Failed));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Completer, completer), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Execute_CallsCompleteExactlyOnce()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter();
                NvencRunCaptureCompleteExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer);

                coordinator.Execute(operation);
                Assert.That(completer.CallCount, Is.EqualTo(1));

                // A second execution is the caller's decision, never the
                // coordinator's retry.
                coordinator.Execute(operation);
                Assert.That(completer.CallCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Execute_CompleterException_PropagatesSameInstance_NoRetry()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                FakeCompleter completer = new FakeCompleter { ExceptionToThrow = boom };
                NvencRunCaptureCompleteExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer);

                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => coordinator.Execute(operation));
                Assert.That(ReferenceEquals(thrown, boom), Is.True);
                Assert.That(completer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_DefaultResult_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter
                {
                    UseOverride = true,
                    OverrideResult = default,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunCaptureCompleteExecutionCoordinator(completer).Execute(operation));
                Assert.That(completer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_ForeignCompleterOrOperationResult_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                // A foreign completer's attempt result.
                FakeCompleter foreignCompleter = new FakeCompleter();
                NvencRunCaptureCompleteAttemptResult foreignAttempt =
                    NvencRunCaptureCompleteAttemptResult.Completed(foreignCompleter, operation);
                FakeCompleter lyingCompleter = new FakeCompleter
                {
                    UseOverride = true,
                    OverrideResult = foreignAttempt,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunCaptureCompleteExecutionCoordinator(lyingCompleter).Execute(operation));
                Assert.That(lyingCompleter.CallCount, Is.EqualTo(1));

                // A different operation's attempt result.
                using (Harness h2 = Harness.Create())
                {
                    NvencRunCaptureCompleteOperation otherOperation = PrepareCaptureComplete(h2);
                    NvencRunCaptureCompleteAttemptResult otherAttempt =
                        NvencRunCaptureCompleteAttemptResult.Completed(
                            new FakeCompleter(), otherOperation);
                    FakeCompleter lyingCompleter2 = new FakeCompleter
                    {
                        UseOverride = true,
                        OverrideResult = otherAttempt,
                    };
                    Assert.Throws<InvalidOperationException>(() =>
                        new NvencRunCaptureCompleteExecutionCoordinator(lyingCompleter2).Execute(operation));
                    Assert.That(lyingCompleter2.CallCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void Execute_ForeignReceipt_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter();
                FakeCompleter otherCompleter = new FakeCompleter();

                // A Completed result whose receipt was minted for a different
                // completer must be refused even though the result's own
                // completer and operation match.
                NvencRunCaptureCompleteReceipt foreignReceipt =
                    NvencRunCaptureCompleteReceipt.Create(otherCompleter, operation);

                AssertRejected(
                    completer,
                    operation,
                    MakeAttempt(completer, operation, foreignReceipt, NvencRunCaptureCompleteStatus.Completed),
                    "Completed carrying another completer's receipt");
            }
        }

        [Test]
        public void Execute_StatusAndReceiptMismatchOrUndefinedStatus_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter();
                NvencRunCaptureCompleteReceipt receipt =
                    NvencRunCaptureCompleteReceipt.Create(completer, operation);

                // Every forged result below names this exact completer and this
                // exact operation, so only the status/receipt disagreement is
                // left to reject it.

                AssertRejected(
                    completer,
                    operation,
                    MakeAttempt(completer, operation, null, NvencRunCaptureCompleteStatus.Completed),
                    "Completed without a receipt");

                AssertRejected(
                    completer,
                    operation,
                    MakeAttempt(completer, operation, receipt, NvencRunCaptureCompleteStatus.Failed),
                    "Failed carrying a receipt");

                AssertRejected(
                    completer,
                    operation,
                    MakeAttempt(completer, operation, null, NvencRunCaptureCompleteStatus.None),
                    "None status");

                AssertRejected(
                    completer,
                    operation,
                    MakeAttempt(completer, operation, receipt, (NvencRunCaptureCompleteStatus)7),
                    "undefined status");
            }
        }

        // ---- Forwarding and shape ----

        [Test]
        public void ResultAndReceipt_ForwardTheOperationGraphWithoutDuplicateFields()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

                FakeCompleter completer = new FakeCompleter();
                NvencRunCaptureCompleteAttemptResult result =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer).Execute(operation);
                NvencRunCaptureCompleteReceipt receipt = result.Receipt;

                // Every forwarded value is the operation's exact reference, so
                // nothing is copied into a field of its own.
                Assert.That(ReferenceEquals(
                    result.CaptureIndexCommitReceipt, operation.CaptureIndexCommitReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    result.CaptureIndexCommitOperation, operation.CaptureIndexCommitOperation), Is.True);
                Assert.That(ReferenceEquals(
                    result.ArtifactPublicationReceipt, operation.ArtifactPublicationReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    result.ArtifactPublicationOperation, operation.ArtifactPublicationOperation), Is.True);
                Assert.That(ReferenceEquals(result.Plan, operation.Plan), Is.True);
                Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
                Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
                Assert.That(ReferenceEquals(
                    result.RunInitializationId, operation.RunInitializationId), Is.True);

                Assert.That(ReferenceEquals(
                    receipt.CaptureIndexCommitReceipt, operation.CaptureIndexCommitReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.CaptureIndexCommitOperation, operation.CaptureIndexCommitOperation), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.ArtifactPublicationReceipt, operation.ArtifactPublicationReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.ArtifactPublicationOperation, operation.ArtifactPublicationOperation), Is.True);
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
            Type type = typeof(NvencRunCaptureCompleteReceipt);

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
                    typeof(INvencRunCaptureCompleter),
                    typeof(NvencRunCaptureCompleteOperation),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void AttemptResult_FourReadonlyFields_ReadonlyStruct()
        {
            Type type = typeof(NvencRunCaptureCompleteAttemptResult);

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
                    typeof(INvencRunCaptureCompleter),
                    typeof(NvencRunCaptureCompleteOperation),
                    typeof(NvencRunCaptureCompleteReceipt),
                    typeof(NvencRunCaptureCompleteStatus),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Coordinator_SingleCompleterField_NotDisposable()
        {
            Type type = typeof(NvencRunCaptureCompleteExecutionCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(1));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[] { typeof(INvencRunCaptureCompleter) }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Status_AppendOnlyExplicitValues()
        {
            Assert.That((int)NvencRunCaptureCompleteStatus.None, Is.EqualTo(0));
            Assert.That((int)NvencRunCaptureCompleteStatus.Completed, Is.EqualTo(1));
            Assert.That((int)NvencRunCaptureCompleteStatus.Failed, Is.EqualTo(2));

            // No failure sub-classification and no failure reason.
            Assert.That(Enum.GetNames(typeof(NvencRunCaptureCompleteStatus)), Has.Length.EqualTo(3));
        }

        // ---- Run state ----

        [Test]
        public void Execute_LeavesRunAndServiceStateUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureComplete(h);

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

                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult before), Is.True);

                FakeCompleter completer = new FakeCompleter();
                NvencRunCaptureCompleteExecutionCoordinator coordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(completer);

                coordinator.Execute(operation);
                AssertUnchanged();

                completer.Status = NvencRunCaptureCompleteStatus.Failed;
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

                    // No publisher or committer is contacted again, and no
                    // filesystem collaborator exists on this path at all.
                    Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                    Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                    Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));

                    // The retained capture index result is reused, never
                    // replaced.
                    Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                        out NvencRunCaptureIndexCommitAttemptResult after), Is.True);
                    Assert.That(ReferenceEquals(after.Operation, before.Operation), Is.True);
                    Assert.That(ReferenceEquals(after.Receipt, before.Receipt), Is.True);
                    Assert.That(operation.IsValid, Is.True);
                }
            }
        }

        // ---- Fixture helpers ----

        /// <summary>
        /// Executes the forged result through the exact completer it names, so
        /// the corrupt part under test is the only thing that can reject it. A
        /// helper that used a fresh completer would be rejected by the foreign
        /// completer check first and would pass even if the status and receipt
        /// verification regressed.
        /// </summary>
        private static void AssertRejected(
            FakeCompleter completer,
            NvencRunCaptureCompleteOperation operation,
            NvencRunCaptureCompleteAttemptResult forged,
            string message)
        {
            Assert.That(ReferenceEquals(forged.Completer, completer), Is.True,
                message + ": the forged result must name the executing completer.");
            Assert.That(ReferenceEquals(forged.Operation, operation), Is.True,
                message + ": the forged result must name the executed operation.");

            int before = completer.CallCount;
            completer.UseOverride = true;
            completer.OverrideResult = forged;

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureCompleteExecutionCoordinator(completer).Execute(operation),
                message);
            Assert.That(completer.CallCount, Is.EqualTo(before + 1), message);
        }

        private static NvencRunCaptureCompleteAttemptResult MakeAttempt(
            INvencRunCaptureCompleter completer,
            NvencRunCaptureCompleteOperation operation,
            NvencRunCaptureCompleteReceipt receipt,
            NvencRunCaptureCompleteStatus status)
        {
            ConstructorInfo ctor = typeof(NvencRunCaptureCompleteAttemptResult).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(INvencRunCaptureCompleter),
                    typeof(NvencRunCaptureCompleteOperation),
                    typeof(NvencRunCaptureCompleteReceipt),
                    typeof(NvencRunCaptureCompleteStatus),
                },
                null);
            Assert.That(ctor, Is.Not.Null, "attempt result constructor not found.");
            return (NvencRunCaptureCompleteAttemptResult)ctor.Invoke(
                new object[] { completer, operation, receipt, status });
        }

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
            WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
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
            CaptureRunInitializationDocumentSet documents =
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
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
            internal NvencRunPublicationService Service;

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
                Service = new NvencRunPublicationService(
                    State, commitCoordinator, artifactCoordinator, captureIndexCoordinator);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin,
                    SessionIssue, TraceFreeze, Service);

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
