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
    /// Contract tests for the Phase 0.11 production Fresh NVENC Session
    /// Ownership Lease releaser: the admission it shares with the execution
    /// coordinator, the single delegation to the operation's exact lease, and
    /// the receipt it issues only after a normal return. Uses the fully
    /// published, CaptureComplete-ed, cleaned-up Run pipeline to mint a valid
    /// release operation, and a lock handle that fails its first release to
    /// produce a real partial release; no real GPU, NVENC, filesystem, sleep,
    /// or short negative wait is used. The lease's own reverse-order release is
    /// contracted by its own fixture and is not restated here.
    /// </summary>
    public class NvencRunSessionOwnershipReleaserContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Rejection before any side effect ----

        [Test]
        public void Release_NullOperation_ArgumentNullException()
        {
            NvencRunSessionOwnershipReleaser releaser = new NvencRunSessionOwnershipReleaser();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => releaser.Release(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Release_PoisonedBeforeFirstAttempt_ArgumentException_LeaseNotTouched()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareRelease(h);

                // A first attempt needs the operation's admission validity,
                // which a Poison revokes; the still-held lease must not be
                // released on CanRelease alone.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.CanRelease, Is.True);

                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => new NvencRunSessionOwnershipReleaser().Release(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));

                AssertHandlesUntouched(h);
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.True);
            }
        }

        [Test]
        public void Release_AfterCompleteRelease_ArgumentException_HandlesNotTouchedAgain()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareRelease(h);
                NvencRunSessionOwnershipReleaser releaser = new NvencRunSessionOwnershipReleaser();

                Assert.That(releaser.Release(operation), Is.Not.Null);
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

                // The shared admission predicate refuses a completed release
                // before any side effect.
                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => releaser.Release(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));

                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
            }
        }

        // ---- Success ----

        [Test]
        public void Release_FirstAttempt_ReleasesBothHandlesOnce_ReturnsCorrelatedReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareRelease(h);

                NvencRunSessionOwnershipReleaser releaser = new NvencRunSessionOwnershipReleaser();
                NvencRunSessionOwnershipReleaseReceipt receipt = releaser.Release(operation);

                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.False);
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.False);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
                Assert.That(ReferenceEquals(receipt.Releaser, releaser), Is.True);
                Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);

                // The forwarded values are the operation graph's own references.
                Assert.That(ReferenceEquals(
                    receipt.CleanupOperation, operation.CleanupOperation), Is.True);
                Assert.That(ReferenceEquals(receipt.RootLayout, operation.RootLayout), Is.True);
                Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
                Assert.That(ReferenceEquals(
                    receipt.RunInitializationId, operation.RunInitializationId), Is.True);
                Assert.That(ReferenceEquals(
                    receipt.CleanupResult.Operation, operation.CleanupResult.Operation), Is.True);
                Assert.That(receipt.CleanupResult.Status,
                    Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Cleaned));
            }
        }

        [Test]
        public void Release_PartialFailure_PropagatesUnwrapped_ThenTheSameOperationRetries()
        {
            using (Harness h = Harness.Create(throwingFirstRelease: true))
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareRelease(h);
                NvencRunSessionOwnershipReleaser releaser = new NvencRunSessionOwnershipReleaser();

                // The lease's own failure surfaces as it is: not caught, not
                // wrapped, and no receipt.
                AggregateException failure = Assert.Throws<AggregateException>(
                    () => releaser.Release(operation));
                Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
                Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());

                // A partial release: one handle released, the other not, and the
                // operation still correlated and retryable even though its
                // admission validity is necessarily gone.
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.False);
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.False);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.CanRelease, Is.True);

                // The second call succeeds, and only the handle that failed is
                // retried: the already released one is not touched again.
                NvencRunSessionOwnershipReleaseReceipt receipt = releaser.Release(operation);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsIssuedFor(releaser, operation), Is.True);
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.False);
            }
        }

        // ---- Run state ----

        [Test]
        public void Release_ChangesOnlyTheOwnershipLeaseState()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareRelease(h);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                NvencRunChunkContextState contextState = h.Context.State;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                bool serviceStopped = h.Service.IsStopped;
                int cleanerCalls = h.CleanupCleaner.CallCount;
                int publisherCalls = h.Publisher.CallCount;
                int committerCalls = h.Committer.CallCount;
                int indexCommitterCalls = h.IndexCommitter.CallCount;
                int completerCalls = h.RunCompleter.CallCount;
                NvencRunCaptureCompleteCleanupOperation cleanupOperation = operation.CleanupOperation;
                NvencRunCaptureCompleteCleanupReceipt cleanupReceipt = operation.CleanupResult.Receipt;

                Assert.That(new NvencRunSessionOwnershipReleaser().Release(operation), Is.Not.Null);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                Assert.That(h.State.IsPoisoned, Is.False);

                // No earlier collaborator is contacted again and the reflected
                // cleanup result is the same graph.
                Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(cleanerCalls));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));
                Assert.That(ReferenceEquals(operation.CleanupOperation, cleanupOperation), Is.True);
                Assert.That(ReferenceEquals(
                    operation.CleanupResult.Receipt, cleanupReceipt), Is.True);
            }
        }

        // ---- Shape ----

        [Test]
        public void Releaser_SealedInternal_NotDisposable_NoInstanceOrMutableStaticState()
        {
            Type type = typeof(NvencRunSessionOwnershipReleaser);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(INvencRunSessionOwnershipReleaser).IsAssignableFrom(type), Is.True);

            Assert.That(
                type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly),
                Is.Empty,
                "the releaser must hold no instance state.");

            Assert.That(
                type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsLiteral && !field.IsInitOnly),
                Is.Empty,
                "the releaser must hold no mutable static state.");
        }

        // ---- Releaser fixture helpers ----

        private static void AssertHandlesUntouched(Harness h)
        {
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
        }

        /// <summary>
        /// Drives the Run to a prepared Session Ownership Lease release
        /// operation over a Cleaned cleanup.
        /// </summary>
        private static NvencRunSessionOwnershipReleaseOperation PrepareRelease(Harness h)
        {
            NvencRunCaptureCompleteCleanupOperation cleanup = PrepareCleanup(h);

            NvencRunCaptureCompleteCleanupAttemptResult cleanupResult =
                h.CleanupExecution.Execute(cleanup);
            Assert.That(cleanupResult.IsCleaned, Is.True);
            Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(cleanupResult), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                out NvencRunSessionOwnershipReleaseOperation operation), Is.True);
            return operation;
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

        private static CaptureRunInitializationSessionIssue MakeIssue(
            bool throwingFirstRelease,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath, throwingFirstRelease);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
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
            CaptureRunInitializationDocumentSet documents =
                new CaptureRunInitializationDocumentSet(layout, InitId);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
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

        /// <summary>
        /// A lock handle that counts its own releases, and optionally fails the
        /// first one. As a lease's first handle the failing variant produces the
        /// ordinary API's partial release: the second handle is released, the
        /// disposal throws, and the Ownership Lease is left no longer fully
        /// retained but still releasable.
        /// </summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private readonly bool _throwFirstRelease;
            private int _disposeCalls;

            internal CountingHandle(string lockPath, bool throwFirstRelease = false)
            {
                LockPath = lockPath;
                _throwFirstRelease = throwFirstRelease;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;

                if (_throwFirstRelease && _disposeCalls == 1)
                {
                    throw new InvalidOperationException("First release fails.");
                }
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
            internal CountingHandle FirstHandle;
            internal CountingHandle SecondHandle;
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

            internal Harness(bool throwingFirstRelease = false)
            {
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue(
                    throwingFirstRelease,
                    out CountingHandle firstHandle,
                    out CountingHandle secondHandle);
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
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

            internal static Harness Create(bool throwingFirstRelease = false)
            {
                Harness h = new Harness(throwingFirstRelease);

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
