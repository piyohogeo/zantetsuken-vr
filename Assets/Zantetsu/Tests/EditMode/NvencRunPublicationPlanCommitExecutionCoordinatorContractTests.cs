using System;
using System.IO;
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
    /// Contract tests for the Phase 0.11 Publication Plan Commit Execution
    /// Coordinator and Execution Result boundary: the single synchronous
    /// Execute entry, the coordinator-specific private-gated issuance proof,
    /// and the immutable execution result. Uses the finalized-and-frozen Run
    /// pipeline with a fake committer; no real GPU, NVENC, filesystem, sleep,
    /// or short negative wait is used.
    /// </summary>
    public class NvencRunPublicationPlanCommitExecutionCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Execute ----

        [Test]
        public void Execute_ThreeNormalVariants_CommitterCalledExactlyOnce()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committedCommitter = new FakeCommitter
                {
                    Status = NvencRunPublicationPlanCommitStatus.Committed,
                };
                NvencRunPublicationPlanCommitExecutionCoordinator committedCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committedCommitter);
                NvencRunPublicationPlanCommitExecutionResult committedResult =
                    committedCoordinator.Execute(operation);
                Assert.That(committedCommitter.CallCount, Is.EqualTo(1));
                Assert.That(committedResult.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));
                Assert.That(committedResult.Receipt, Is.Not.Null);

                FakeCommitter failedCommitter = new FakeCommitter
                {
                    Status = NvencRunPublicationPlanCommitStatus.FailedBeforeRename,
                };
                NvencRunPublicationPlanCommitExecutionResult failedResult =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(failedCommitter).Execute(operation);
                Assert.That(failedCommitter.CallCount, Is.EqualTo(1));
                Assert.That(failedResult.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.FailedBeforeRename));
                Assert.That(failedResult.Receipt, Is.Null);

                FakeCommitter unknownCommitter = new FakeCommitter
                {
                    Status = NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown,
                };
                NvencRunPublicationPlanCommitExecutionResult unknownResult =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(unknownCommitter).Execute(operation);
                Assert.That(unknownCommitter.CallCount, Is.EqualTo(1));
                Assert.That(unknownResult.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown));
                Assert.That(unknownResult.Receipt, Is.Null);
            }
        }

        [Test]
        public void Execute_NullOrInvalidOperation_CommitterNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer);

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(committer.CallCount, Is.EqualTo(0));

                // A reconstructed operation is not the retained operation, so
                // IsValid is false: rejected before the committer is contacted.
                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, operation.TraceFreezeReceipt, operation.FinalizationResult, operation.Plan);
                Assert.That(reconstructed.IsValid, Is.False);

                ArgumentException invalidEx = Assert.Throws<ArgumentException>(() => coordinator.Execute(reconstructed));
                Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
                Assert.That(committer.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Execute_CommitterException_PropagatesSameInstance_NoRetry()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                InvalidOperationException boom = new InvalidOperationException("boom");
                FakeCommitter committer = new FakeCommitter { ExceptionToThrow = boom };
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer);

                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                    () => coordinator.Execute(operation));
                Assert.That(ReferenceEquals(thrown, boom), Is.True);
                Assert.That(committer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Execute_DefaultResult_ForeignCommitter_DifferentOperation_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                // Default (None) attempt result.
                FakeCommitter defaultCommitter = new FakeCommitter
                {
                    UseOverride = true,
                    OverrideResult = default,
                };
                NvencRunPublicationPlanCommitExecutionCoordinator defaultCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(defaultCommitter);
                Assert.Throws<InvalidOperationException>(() => defaultCoordinator.Execute(operation));
                Assert.That(defaultCommitter.CallCount, Is.EqualTo(1));

                // A foreign committer's attempt result.
                FakeCommitter foreignCommitter = new FakeCommitter();
                NvencRunPublicationPlanCommitAttemptResult foreignAttempt =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(foreignCommitter, operation);
                FakeCommitter lyingCommitter = new FakeCommitter
                {
                    UseOverride = true,
                    OverrideResult = foreignAttempt,
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunPublicationPlanCommitExecutionCoordinator(lyingCommitter).Execute(operation));
                Assert.That(lyingCommitter.CallCount, Is.EqualTo(1));

                // A different operation's attempt result.
                using (Harness h2 = Harness.Create())
                {
                    FinalizeAndFreeze(h2, 1);
                    Assert.That(
                        h2.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation otherOperation),
                        Is.True);

                    FakeCommitter otherCommitter = new FakeCommitter();
                    NvencRunPublicationPlanCommitAttemptResult otherAttempt =
                        NvencRunPublicationPlanCommitAttemptResult.Committed(otherCommitter, otherOperation);
                    FakeCommitter lyingCommitter2 = new FakeCommitter
                    {
                        UseOverride = true,
                        OverrideResult = otherAttempt,
                    };
                    Assert.Throws<InvalidOperationException>(() =>
                        new NvencRunPublicationPlanCommitExecutionCoordinator(lyingCommitter2).Execute(operation));
                    Assert.That(lyingCommitter2.CallCount, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void Execute_CorruptedReceiptOrUndefinedStatus_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                // Committed status with a null receipt.
                FakeCommitter nullReceiptCommitter = new FakeCommitter
                {
                    UseOverride = true,
                    OverrideResult = MakeAttempt(
                        null, operation, null, NvencRunPublicationPlanCommitStatus.Committed),
                };
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunPublicationPlanCommitExecutionCoordinator(nullReceiptCommitter).Execute(operation));

                // Committed status with a foreign receipt.
                FakeCommitter foreignReceiptCommitter = new FakeCommitter();
                NvencRunPublicationPlanCommitReceipt foreignReceipt =
                    NvencRunPublicationPlanCommitReceipt.Create(foreignReceiptCommitter, operation);
                FakeCommitter badReceiptCommitter = new FakeCommitter();
                badReceiptCommitter.UseOverride = true;
                badReceiptCommitter.OverrideResult = MakeAttempt(
                    badReceiptCommitter, operation, foreignReceipt, NvencRunPublicationPlanCommitStatus.Committed);
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunPublicationPlanCommitExecutionCoordinator(badReceiptCommitter).Execute(operation));

                // Undefined status value.
                FakeCommitter undefinedCommitter = new FakeCommitter();
                undefinedCommitter.UseOverride = true;
                undefinedCommitter.OverrideResult = MakeAttempt(
                    undefinedCommitter, operation, null, (NvencRunPublicationPlanCommitStatus)99);
                Assert.Throws<InvalidOperationException>(() =>
                    new NvencRunPublicationPlanCommitExecutionCoordinator(undefinedCommitter).Execute(operation));
            }
        }

        // ---- Proof and result issuance ----

        [Test]
        public void Proof_PrivateGatedMint_ExactBinding()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer);
                NvencRunPublicationPlanCommitAttemptResult attempt =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(committer, operation);

                // A foreign gate cannot mint.
                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof.Mint(
                        new object(), coordinator, committer, operation, attempt));

                // The proof type has no public or internal constructor and no
                // public members other than IsMintedFor.
                Type proofType = typeof(NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof);
                foreach (ConstructorInfo ctor in proofType.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(ctor.IsPrivate, Is.True, "proof constructor must be private.");
                }
                Assert.That(proofType.GetFields(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
                Assert.That(proofType.GetProperties(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

                // Executing yields a proof that binds the exact constituents.
                NvencRunPublicationPlanCommitExecutionResult result = coordinator.Execute(operation);
                FieldInfo proofField = typeof(NvencRunPublicationPlanCommitExecutionResult).GetField(
                    "_proof", BindingFlags.Instance | BindingFlags.NonPublic);
                NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof proof =
                    (NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof)proofField.GetValue(result);

                Assert.That(proof.IsMintedFor(coordinator, committer, operation, result.Attempt), Is.True);
                Assert.That(proof.IsMintedFor(
                    new NvencRunPublicationPlanCommitExecutionCoordinator(new FakeCommitter()),
                    committer, operation, result.Attempt), Is.False);
                Assert.That(proof.IsMintedFor(coordinator, new FakeCommitter(), operation, result.Attempt), Is.False);
            }
        }

        [Test]
        public void Result_CannotBeIssuedFromDirectAttemptAlone()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer);
                NvencRunPublicationPlanCommitAttemptResult attempt =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(committer, operation);

                // A null proof cannot produce a result.
                Assert.Throws<ArgumentNullException>(() =>
                    NvencRunPublicationPlanCommitExecutionResult.Create(coordinator, null, operation, attempt));

                // A directly minted attempt (without a proof) cannot produce a
                // result: the proof is private-gated and only Execute mints it.
                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof.Mint(
                        new object(), coordinator, committer, operation, attempt));
            }
        }

        [Test]
        public void Result_ForwardsAllGraphValues()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult result =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);

                Assert.That(result.IsValid, Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(committer, operation), Is.True);
                Assert.That(ReferenceEquals(result.Plan, operation.Plan), Is.True);
                Assert.That(ReferenceEquals(result.FinalizationResult, operation.FinalizationResult), Is.True);
                Assert.That(ReferenceEquals(result.TraceFreezeReceipt, operation.TraceFreezeReceipt), Is.True);
                Assert.That(result.TestRunId, Is.EqualTo(operation.TestRunId));
                Assert.That(result.RunInitializationId, Is.EqualTo(operation.RunInitializationId));
                Assert.That(result.RunManifestContentHash, Is.EqualTo(operation.RunManifestContentHash));
                Assert.That(ReferenceEquals(result.RootLayout, operation.RootLayout), Is.True);
            }
        }

        [Test]
        public void CommittedExecutionResult_SurvivesSlotCommit()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult execution =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);
                Assert.That(execution.IsValid, Is.True);

                Assert.That(h.Slot.TryCommit(h.Context, result), Is.True);

                Assert.That(execution.IsValid, Is.True);
                Assert.That(execution.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));
            }
        }

        [Test]
        public void ExecutionResult_InvalidatedByLeaseRelease()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult execution =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);
                Assert.That(execution.IsValid, Is.True);

                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(execution.IsValid, Is.False);
            }
        }

        [Test]
        public void ExecutionResult_InvalidatedByPlanOrDescriptorOrRelationOrTraceReceiptCorruption()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult execution =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);
                Assert.That(execution.IsValid, Is.True);

                SetField(operation.Plan, "_runManifestContentHash", "zz");
                Assert.That(execution.IsValid, Is.False);
            }

            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult execution =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);
                Assert.That(execution.IsValid, Is.True);

                SetBackingField(operation.FinalizationResult.Descriptor, "ArtifactKind", CaptureArtifactKind.None);
                Assert.That(execution.IsValid, Is.False);
            }

            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult execution =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);
                Assert.That(execution.IsValid, Is.True);

                SetField(operation.FinalizationResult.FrameRelation, "_captureFrameIds", new long[] { 1, 1 });
                Assert.That(execution.IsValid, Is.False);
            }

            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionResult execution =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer).Execute(operation);
                Assert.That(execution.IsValid, Is.True);

                SetField(h.RunCoordinator, "_traceFreezeReceipt", null);
                Assert.That(execution.IsValid, Is.False);
            }
        }

        // ---- Shape ----

        [Test]
        public void Coordinator_SingleCommitterField_NotDisposable()
        {
            Type type = typeof(NvencRunPublicationPlanCommitExecutionCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(INvencRunPublicationPlanCommitter)));
            Assert.That(fields[0].IsInitOnly, Is.True);
        }

        [Test]
        public void Result_SealedFourReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(NvencRunPublicationPlanCommitExecutionResult);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(4));

            Type[] expected =
            {
                typeof(NvencRunPublicationPlanCommitExecutionCoordinator),
                typeof(NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof),
                typeof(NvencRunPublicationPlanCommitOperation),
                typeof(NvencRunPublicationPlanCommitAttemptResult),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }
        }

        [Test]
        public void CoordinatorAndResult_Sources_NoFilesystemWorkerQueueThreadTaskRetryCleanupDispositionRegistryLease()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitExecutionCoordinator.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitExecutionResult.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "Path.", "Flush(", "Move(", "Close(",
                "Worker", "Queue", "new Thread", "ThreadPool", "Task", "SpinWait",
                ".Dispose(", "OwnershipLease", "Disposition", "TryCommit", "TryDiscardRegistered", "TryPoison",
                "Retry", "Rollback", "Cleanup", "re-read",
                "JsonUtility", "ComputeHash", "HashAlgorithm", "IncrementalHash", "SHA256", "SHA384", "SHA512",
                "MD5", "System.Security.Cryptography", "DllImport", "IntPtr", "SafeHandle",
                "UnityEngine", "Application.",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "coordinator/result source must not contain: " + word);
            }
        }

        // ---- Helpers ----

        private static NvencRunPublicationPlanCommitAttemptResult MakeAttempt(
            INvencRunPublicationPlanCommitter committer,
            NvencRunPublicationPlanCommitOperation operation,
            NvencRunPublicationPlanCommitReceipt receipt,
            NvencRunPublicationPlanCommitStatus status)
        {
            ConstructorInfo ctor = typeof(NvencRunPublicationPlanCommitAttemptResult).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(INvencRunPublicationPlanCommitter),
                    typeof(NvencRunPublicationPlanCommitOperation),
                    typeof(NvencRunPublicationPlanCommitReceipt),
                    typeof(NvencRunPublicationPlanCommitStatus),
                },
                null);
            Assert.That(ctor, Is.Not.Null, "attempt result constructor not found.");
            return (NvencRunPublicationPlanCommitAttemptResult)ctor.Invoke(
                new object[] { committer, operation, receipt, status });
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

        private static void StopFinalizedBackend(Harness h, int frameCount)
        {
            for (long id = 1; id <= frameCount; id++)
            {
                h.AcceptAndAppendChunk(id, 64, Seed);
            }

            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            for (long id = 1; id <= frameCount; id++)
            {
                Assert.That(h.RunCoordinator.TryReflectCompletion(
                    MakeCompletion(id, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            }
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

        private static void CompleteTraceFreeze(Harness h)
        {
            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

            ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
            FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
        }

        private static NvencChunkFinalizationResult FinalizeAndFreeze(Harness h, int frameCount)
        {
            StopFinalizedBackend(h, frameCount);
            CompleteTraceFreeze(h);

            Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult result), Is.True);
            return result;
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status,
            int producedArtifactCount = 0,
            long testRunId = 1)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, testRunId, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, producedArtifactCount, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
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
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
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

        private static void SetBackingField(object target, string propertyName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                "<" + propertyName + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, propertyName + " backing field not found.");
            field.SetValue(target, value);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
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

        private static NvencOrderedSubmitWorkerService BuildSubmitWorker(NvencCaptureProcessState state)
        {
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samples = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool sync = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitCredits = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCredits = new NvencFrameCompletionCreditPool(state);
            NvencSourceResourceReleaseCoordinator release = new NvencSourceResourceReleaseCoordinator(
                state, work, samples, sync, submitCredits, frameCredits,
                new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
            NvencOrderedSubmitProcessor processor = new NvencOrderedSubmitProcessor(
                state,
                new NvencFixedSpscQueue<NvencSubmissionRecord>(),
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>(),
                work, samples, release, new FakeSubmitter());
            return new NvencOrderedSubmitWorkerService(state, processor);
        }

        // ---- Fakes ----

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            internal int CallCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunPublicationPlanCommitAttemptResult OverrideResult;

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                CallCount++;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

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
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunArtifactPublicationAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
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
            private int _finalizeCount;

            internal int CallCount => Volatile.Read(ref _finalizeCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);

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
            internal bool Result = true;
            internal int ResultLength = 1024;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = ResultLength;
                return Result;
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
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
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

        private sealed class FakeIndexCommitter : INvencRunCaptureIndexCommitter
        {
            private int _callCount;
            internal NvencRunCaptureIndexCommitStatus Status = NvencRunCaptureIndexCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureIndexCommitAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexCommitAttemptResult Commit(
                NvencRunCaptureIndexCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                Entered?.Set();
                Release?.Wait(WatchdogTimeoutMs);

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

        private sealed class FakeRunCompleter : INvencRunCaptureCompleter
        {
            private int _callCount;
            internal NvencRunCaptureCompleteStatus Status = NvencRunCaptureCompleteStatus.Completed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureCompleteAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteAttemptResult Complete(
                NvencRunCaptureCompleteOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                Entered?.Set();
                Release?.Wait(WatchdogTimeoutMs);

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

        /// <summary>
        /// Identity-only CaptureComplete cleaner: this fixture never runs a
        /// cleanup, but the Run Coordinator requires the exact cleanup
        /// Execution Coordinator its results must come from.
        /// </summary>
        private sealed class FakeCleanupCleaner : INvencRunCaptureCompleteCleaner
        {
            public NvencRunCaptureCompleteCleanupAttemptResult Clean(
                NvencRunCaptureCompleteCleanupOperation operation)
            {
                return NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(this, operation);
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
            internal FakeWriter Writer = new FakeWriter();
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

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            internal FakeCommitter Committer;
            internal FakePublisher Publisher;
            internal FakeIndexCommitter IndexCommitter;
            internal FakeRunCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal FakeCleanupCleaner CleanupCleaner;
            internal NvencRunCaptureCompleteCleanupExecutionCoordinator CleanupExecution;
            internal FakeSessionOwnershipReleaser Releaser;
            internal NvencRunSessionOwnershipReleaseExecutionCoordinator ReleaseExecution;

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
                IndexCommitter = new FakeIndexCommitter();
                NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCoordinator =
                    new NvencRunCaptureIndexCommitExecutionCoordinator(IndexCommitter);
                RunCompleter = new FakeRunCompleter();
                NvencRunCaptureCompleteExecutionCoordinator captureCompleteCoordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(RunCompleter);
                Service = new NvencRunPublicationService(
                    State, commitCoordinator, artifactCoordinator, captureIndexCoordinator,
                    captureCompleteCoordinator);

                CleanupCleaner = new FakeCleanupCleaner();
                CleanupExecution =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(CleanupCleaner);

                Releaser = new FakeSessionOwnershipReleaser();
                ReleaseExecution =
                    new NvencRunSessionOwnershipReleaseExecutionCoordinator(Releaser);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze, Service, CleanupExecution, ReleaseExecution);

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
                AppendChunk(frameId, length, seed);
            }

            internal void AppendChunk(long frameId, int length, byte seed)
            {
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
