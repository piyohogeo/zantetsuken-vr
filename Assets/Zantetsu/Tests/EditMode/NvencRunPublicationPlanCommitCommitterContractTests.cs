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
    /// Contract tests for the Phase 0.11 Publication Plan Committer boundary:
    /// the single synchronous committer interface, the append-only attempt
    /// status, the immutable commit receipt, and the immutable attempt result.
    /// Uses the finalized-and-frozen Run pipeline with a fake committer; no
    /// real GPU, NVENC, filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunPublicationPlanCommitCommitterContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Interface and status shape ----

        [Test]
        public void CommitterInterface_SingleSynchronousMethod_InternalNonInheriting()
        {
            Type type = typeof(INvencRunPublicationPlanCommitter);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.GetInterfaces(), Is.Empty);

            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(methods, Has.Length.EqualTo(1));

            MethodInfo method = methods[0];
            Assert.That(method.Name, Is.EqualTo("Commit"));
            Assert.That(method.ReturnType, Is.EqualTo(typeof(NvencRunPublicationPlanCommitAttemptResult)));

            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(NvencRunPublicationPlanCommitOperation)));
        }

        [Test]
        public void Status_AppendOnlyValues_Fixed()
        {
            Assert.That((int)NvencRunPublicationPlanCommitStatus.None, Is.EqualTo(0));
            Assert.That((int)NvencRunPublicationPlanCommitStatus.Committed, Is.EqualTo(1));
            Assert.That((int)NvencRunPublicationPlanCommitStatus.FailedBeforeRename, Is.EqualTo(2));
            Assert.That((int)NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown, Is.EqualTo(3));
        }

        [Test]
        public void AttemptResult_DefaultIsNone_NotVisibleAsTerminal()
        {
            NvencRunPublicationPlanCommitAttemptResult result = default;

            Assert.That(result.IsNone, Is.True);
            Assert.That(result.IsCommitted, Is.False);
            Assert.That(result.IsFailedBeforeRename, Is.False);
            Assert.That(result.IsCommitOutcomeUnknown, Is.False);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void AttemptResult_ThreeVariants_ExclusiveShapes()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();

                NvencRunPublicationPlanCommitAttemptResult committed =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(committer, operation);
                Assert.That(committed.IsCommitted, Is.True);
                Assert.That(committed.IsFailedBeforeRename, Is.False);
                Assert.That(committed.IsCommitOutcomeUnknown, Is.False);
                Assert.That(committed.IsNone, Is.False);
                Assert.That(committed.IsValid, Is.True);
                Assert.That(committed.Receipt, Is.Not.Null);
                Assert.That(committed.Receipt.IsIssuedFor(committer, operation), Is.True);

                NvencRunPublicationPlanCommitAttemptResult failed =
                    NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(committer, operation);
                Assert.That(failed.IsFailedBeforeRename, Is.True);
                Assert.That(failed.IsCommitted, Is.False);
                Assert.That(failed.IsCommitOutcomeUnknown, Is.False);
                Assert.That(failed.IsValid, Is.True);
                Assert.That(failed.Receipt, Is.Null);

                NvencRunPublicationPlanCommitAttemptResult unknown =
                    NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(committer, operation);
                Assert.That(unknown.IsCommitOutcomeUnknown, Is.True);
                Assert.That(unknown.IsCommitted, Is.False);
                Assert.That(unknown.IsFailedBeforeRename, Is.False);
                Assert.That(unknown.IsValid, Is.True);
                Assert.That(unknown.Receipt, Is.Null);
            }
        }

        [Test]
        public void Committer_RejectsNullAndInvalidOperation_BeforeSideEffect()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();

                // Null operation: rejected before any side effect.
                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(() => committer.Commit(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(committer.CallCount, Is.EqualTo(0));

                // A directly reconstructed operation is not the retained
                // operation, so its IsValid is false: rejected before any side
                // effect.
                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, operation.TraceFreezeReceipt, operation.FinalizationResult, operation.Plan);
                Assert.That(reconstructed.IsValid, Is.False);

                ArgumentException invalidEx = Assert.Throws<ArgumentException>(() => committer.Commit(reconstructed));
                Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
                Assert.That(committer.CallCount, Is.EqualTo(0));

                // A valid operation is accepted once.
                NvencRunPublicationPlanCommitAttemptResult result = committer.Commit(operation);
                Assert.That(committer.CallCount, Is.EqualTo(1));
                Assert.That(result.IsCommitted, Is.True);
            }
        }

        [Test]
        public void ReceiptAndResult_ForeignCommitterOrOperation_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitAttemptResult committed =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(committer, operation);
                NvencRunPublicationPlanCommitReceipt receipt = committed.Receipt;
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsIssuedFor(committer, operation), Is.True);

                // A foreign committer is rejected.
                FakeCommitter otherCommitter = new FakeCommitter();
                Assert.That(receipt.IsIssuedFor(otherCommitter, operation), Is.False);
                Assert.That(committed.IsIssuedFor(otherCommitter, operation), Is.False);

                // A different operation is rejected.
                using (Harness h2 = Harness.Create())
                {
                    FinalizeAndFreeze(h2, 1);
                    Assert.That(
                        h2.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation otherOperation),
                        Is.True);

                    Assert.That(receipt.IsIssuedFor(committer, otherOperation), Is.False);
                    Assert.That(committed.IsIssuedFor(committer, otherOperation), Is.False);
                }

                // A reconstructed-equal operation can never mint a receipt or a
                // Committed result.
                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, operation.TraceFreezeReceipt, operation.FinalizationResult, operation.Plan);

                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanCommitReceipt.Create(committer, reconstructed));
                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanCommitAttemptResult.Committed(committer, reconstructed));
            }
        }

        [Test]
        public void ReceiptAndCommittedResult_SurviveSlotCommit()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitAttemptResult committed =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(committer, operation);
                NvencRunPublicationPlanCommitReceipt receipt = committed.Receipt;

                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(committed.IsCommitted, Is.True);

                // Advance the Registry Slot to Committed: the post-commit
                // binding survives, while the pre-commit validity no longer
                // holds.
                Assert.That(h.Slot.TryCommit(h.Context, result), Is.True);

                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(committed.IsCommitted, Is.True);
                Assert.That(receipt.IsIssuedFor(committer, operation), Is.True);
            }
        }

        [Test]
        public void ReceiptAndCommittedResult_InvalidatedByLeaseRelease()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                NvencRunPublicationPlanCommitReceipt receipt = IssueCommitted(h, out NvencRunPublicationPlanCommitOperation operation);
                NvencRunPublicationPlanCommitAttemptResult committed =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(new FakeCommitter(), operation);
                Assert.That(receipt.IsValid, Is.True);

                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(receipt.IsValid, Is.False);
                Assert.That(committed.IsCommitted, Is.False);
            }
        }

        [Test]
        public void ReceiptAndCommittedResult_InvalidatedByTraceReceiptCorruption()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                NvencRunPublicationPlanCommitReceipt receipt = IssueCommitted(h, out NvencRunPublicationPlanCommitOperation operation);
                NvencRunPublicationPlanCommitAttemptResult committed =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(new FakeCommitter(), operation);
                Assert.That(receipt.IsValid, Is.True);

                SetField(h.RunCoordinator, "_traceFreezeReceipt", null);

                Assert.That(receipt.IsValid, Is.False);
                Assert.That(committed.IsCommitted, Is.False);
            }
        }

        [Test]
        public void ReceiptAndCommittedResult_InvalidatedByPlanOrDescriptorOrRelationCorruption()
        {
            // Plan manifest hash corruption.
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                NvencRunPublicationPlanCommitReceipt receipt = IssueCommitted(h, out NvencRunPublicationPlanCommitOperation operation);
                Assert.That(receipt.IsValid, Is.True);

                SetField(operation.Plan, "_runManifestContentHash", "zz");
                Assert.That(receipt.IsValid, Is.False);
            }

            // Descriptor corruption.
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                NvencRunPublicationPlanCommitReceipt receipt = IssueCommitted(h, out NvencRunPublicationPlanCommitOperation operation);
                Assert.That(receipt.IsValid, Is.True);

                SetBackingField(operation.FinalizationResult.Descriptor, "ArtifactKind", CaptureArtifactKind.None);
                Assert.That(receipt.IsValid, Is.False);
            }

            // Relation corruption.
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                NvencRunPublicationPlanCommitReceipt receipt = IssueCommitted(h, out NvencRunPublicationPlanCommitOperation operation);
                Assert.That(receipt.IsValid, Is.True);

                SetField(operation.FinalizationResult.FrameRelation, "_captureFrameIds", new long[] { 1, 1 });
                Assert.That(receipt.IsValid, Is.False);
            }
        }

        [Test]
        public void AttemptResult_FailedAndUnknown_RequireBindingIntact()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();

                NvencRunPublicationPlanCommitAttemptResult failed =
                    NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(committer, operation);
                NvencRunPublicationPlanCommitAttemptResult unknown =
                    NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(committer, operation);
                Assert.That(failed.IsFailedBeforeRename, Is.True);
                Assert.That(unknown.IsCommitOutcomeUnknown, Is.True);

                // A directly reconstructed operation has no intact binding and
                // must be rejected by both factories.
                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, operation.TraceFreezeReceipt, operation.FinalizationResult, operation.Plan);

                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(committer, reconstructed));
                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(committer, reconstructed));

                // Releasing the lease invalidates the already-issued Failed and
                // Unknown results.
                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(failed.IsFailedBeforeRename, Is.False);
                Assert.That(unknown.IsCommitOutcomeUnknown, Is.False);
                Assert.That(failed.IsValid, Is.False);
                Assert.That(unknown.IsValid, Is.False);
            }
        }

        [Test]
        public void ReceiptAndCommittedResult_InvalidatedByForeignResultGraphSwap()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                NvencRunPublicationPlanCommitReceipt receipt = IssueCommitted(h, out NvencRunPublicationPlanCommitOperation operation);
                NvencRunPublicationPlanCommitAttemptResult committed =
                    NvencRunPublicationPlanCommitAttemptResult.Committed(new FakeCommitter(), operation);
                Assert.That(receipt.IsValid, Is.True);

                // Swap the operation's finalization result and plan to a
                // different, valid same-Run sink/result graph: the post-commit
                // binding must fail closed.
                NvencChunkFinalizationResult foreignResult = MakeForeignResult("chunk/foreign");
                CapturePublicationPlan foreignPlan = NvencRunPublicationPlanBuilder.Build(
                    foreignResult, h.SessionIssue, Hash64);

                SetField(operation, "_finalizationResult", foreignResult);
                SetField(operation, "_plan", foreignPlan);

                Assert.That(receipt.IsValid, Is.False);
                Assert.That(committed.IsCommitted, Is.False);
            }
        }

        // ---- Shape ----

        [Test]
        public void Receipt_SealedTwoReadonlyFields_NoPublicCtor()
        {
            Type type = typeof(NvencRunPublicationPlanCommitReceipt);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(INvencRunPublicationPlanCommitter)));
            Assert.That(fields[1].FieldType, Is.EqualTo(typeof(NvencRunPublicationPlanCommitOperation)));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void AttemptResult_ReadonlyStruct_FourFields_NoPublicCtor()
        {
            Type type = typeof(NvencRunPublicationPlanCommitAttemptResult);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(4));

            Type[] expected =
            {
                typeof(INvencRunPublicationPlanCommitter),
                typeof(NvencRunPublicationPlanCommitOperation),
                typeof(NvencRunPublicationPlanCommitReceipt),
                typeof(NvencRunPublicationPlanCommitStatus),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }
        }

        [Test]
        public void CommitterBoundary_Sources_NoFilesystemWorkerQueueThreadTaskLeaseDisposition()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitStatus.cs")) +
                File.ReadAllText(Path.Combine(directory, "INvencRunPublicationPlanCommitter.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitReceipt.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitAttemptResult.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "Path.", "Flush(", "Move(", "Close(",
                "Worker", "Queue", "new Thread", "ThreadPool", "Task", "SpinWait",
                ".Dispose(", "OwnershipLease", "Disposition", "TryCommit", "TryDiscardRegistered",
                "JsonUtility", "ComputeHash", "HashAlgorithm", "IncrementalHash", "SHA256", "SHA384", "SHA512",
                "MD5", "System.Security.Cryptography", "DllImport", "IntPtr", "SafeHandle",
                "UnityEngine", "Application.", "Retry", "Rollback", "re-read", "Cleanup",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "committer boundary source must not contain: " + word);
            }
        }

        // ---- Helpers ----

        private static NvencRunPublicationPlanCommitReceipt IssueCommitted(
            Harness h,
            out NvencRunPublicationPlanCommitOperation operation)
        {
            Assert.That(
                h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out operation), Is.True);
            FakeCommitter committer = new FakeCommitter();
            NvencRunPublicationPlanCommitAttemptResult committed =
                NvencRunPublicationPlanCommitAttemptResult.Committed(committer, operation);
            Assert.That(committed.IsCommitted, Is.True);
            Assert.That(committed.Receipt, Is.Not.Null);
            return committed.Receipt;
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

        private static NvencChunkFinalizationResult MakeForeignResult(string artifactId)
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);
            FakeWriter writer = new FakeWriter();
            NvencRunChunkSink sink = new NvencRunChunkSink(state, buffer, writer);
            NvencRunChunkFinalizationCoordinator coordinator = new NvencRunChunkFinalizationCoordinator(writer);

            CaptureFrameWorkToken token = MakeToken(1);
            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new PatternSource(64, Seed), out _),
                Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
            Assert.That(sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation = NvencRunChunkFinalizationOperation.Create(sink, evidence, artifactId);
            return coordinator.Execute(operation);
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
            CaptureRunInitializationDocumentSet documents = new CaptureRunInitializationDocumentSet(layout, InitId);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(documents);
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

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!operation.IsValid)
                {
                    throw new ArgumentException("Operation is not valid.", nameof(operation));
                }

                CallCount++;

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
