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
    /// Contract tests for the Phase 0.11 Publication Service: the fixed
    /// single-request-slot, two-phase Worker boundary that commits the
    /// publication plan and then publishes the Fresh NVENC chunk on the same
    /// dedicated Worker thread. Uses the finalized-and-frozen Run pipeline with
    /// a fake committer and a fake publisher; no real GPU, NVENC, filesystem,
    /// sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunPublicationServiceContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Plan commit phase ----

        [Test]
        public void PlanCommit_Committed_ReParksWorker_CollectsToAcceptingArtifact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                int mainThreadId = Thread.CurrentThread.ManagedThreadId;

                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the committed plan terminal");

                // The Committed plan keeps the Worker parked on the same thread.
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Committer.ExecutingThreadName, Is.EqualTo(NvencRunPublicationService.WorkerThreadName));
                Assert.That(h.Committer.ExecutingManagedThreadId, Is.Not.EqualTo(mainThreadId));

                Assert.That(h.Service.TryCollectPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));

                Assert.That(h.Service.TryCollectPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult second), Is.False);
                Assert.That(second, Is.Null);
            }
        }

        [Test]
        public void PlanCommit_NonCommitted_StopsWorker_NoArtifactPhase()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                // FailedBeforeRename stops the Worker and never enters the
                // Artifact phase.
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.FailedBeforeRename;
                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the failed plan commit");
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.PlanCommitCompleted));
                Assert.That(h.Service.TryCollectPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.FailedBeforeRename));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.PlanCommitCollected));
                Assert.That(h.Service.IsStopped, Is.True);
            }

            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                h.Committer.Status = NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown;
                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the unknown plan commit");
                Assert.That(h.Service.TryCollectPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown));
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.PlanCommitCollected));
            }
        }

        [Test]
        public void PlanCommit_ForeignInvalidDoubleSubmit_NoCoordinatorContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => h.Service.TrySubmitPlanCommit(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));

                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, operation.TraceFreezeReceipt, operation.FinalizationResult, operation.Plan);
                Assert.That(reconstructed.IsValid, Is.False);
                Assert.That(h.Service.TrySubmitPlanCommit(reconstructed), Is.False);

                using (Harness h2 = Harness.Create())
                {
                    NvencRunPublicationPlanCommitOperation foreign = PreparePlanOperation(h2);
                    Assert.That(h.Service.TrySubmitPlanCommit(foreign), Is.False);
                }

                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.False);

                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan terminal");
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Service.TryCollectPlanCommit(out _), Is.True);
            }
        }

        [Test]
        public void PlanCommit_CoordinatorException_Poisons_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                h.Committer.ExceptionToThrow = boom;

                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the committer exception");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out Exception failure), Is.True);
                Assert.That(ReferenceEquals(failure, boom), Is.True);
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Service.TryCollectPlanCommit(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);
            }
        }

        [Test]
        public void PlanCommit_PoisonDuringExecution_NoNormalResult()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                h.Committer.Entered = entered;
                h.Committer.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                    Assert.That(h.State.TryPoison(), Is.True);
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceStop(h.Service, "worker did not stop after the mid-execution poison");

                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryCollectPlanCommit(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);

            }
        }

        [Test]
        public void PlanCommit_ConcurrentCollect_ExactlyOneSucceeds()
        {
            using (ManualResetEventSlim start = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan terminal");

                int successes = 0;
                Thread[] threads = new Thread[2];
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        start.Wait(WatchdogTimeoutMs);
                        if (h.Service.TryCollectPlanCommit(out _))
                        {
                            Interlocked.Increment(ref successes);
                        }
                    })
                    {
                        IsBackground = true,
                    };
                    threads[i].Start();
                }

                start.Set();
                foreach (Thread t in threads)
                {
                    Assert.That(t.Join(WatchdogTimeoutMs), Is.True, "collector thread did not exit");
                }

                Assert.That(Volatile.Read(ref successes), Is.EqualTo(1));
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));

            }
        }

        [Test]
        public void PlanCommit_CollectBeforeCompleted_ReturnsFalseNoClear()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                h.Committer.Entered = entered;
                h.Committer.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                    // A poll while executing must not collect or clear the slot.
                    Assert.That(h.Service.TryCollectPlanCommit(out _), Is.False);
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan terminal");
                Assert.That(h.Service.TryCollectPlanCommit(out _), Is.True);

            }
        }

        [Test]
        public void PlanCommit_DisposeRejectedWhileRunning_IdempotentAfterStop()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation operation = PreparePlanOperation(h);

                h.Committer.Entered = entered;
                h.Committer.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitPlanCommit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");
                    Assert.Throws<InvalidOperationException>(() => h.Service.Dispose());
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan terminal");
                Assert.That(h.Service.TryCollectPlanCommit(out _), Is.True);

                // The Worker is still parked after a Committed collect: dispose
                // while running stays rejected.
                Assert.Throws<InvalidOperationException>(() => h.Service.Dispose());

            }
        }

        [Test]
        public void PlanCommit_SpuriousNotifyDuringExecution_WorkerStillServicesArtifactPhase()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitOperation planOperation = PreparePlanOperation(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                h.Committer.Entered = entered;
                h.Committer.Release = release;

                try
                {
                    Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                    // An extra notification while the Plan is executing must not
                    // stop the Worker after the committed Plan terminal: the Worker
                    // re-parks and the Artifact phase still converges.
                    h.Service.Notify();
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the committed plan terminal");
                Assert.That(h.Service.IsStopped, Is.False);

                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(out _), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);

                WaitForArtifactTerminal(h, "worker did not reach the terminal for the artifact publication");
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));

            }
        }

        // ---- Artifact publication phase ----

        [Test]
        public void Artifact_SameServiceInstanceAndWorkerThread_NoSecondWorker()
        {
            using (Harness h = Harness.Create())
            {
                Thread workerBefore = (Thread)GetField(h.Service, "_workerThread");

                CommitPlanAndCollect(h);

                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));

                // The same Service instance and the same Worker thread are
                // reused for the Artifact phase.
                Thread workerAfter = (Thread)GetField(h.Service, "_workerThread");
                Assert.That(ReferenceEquals(workerBefore, workerAfter), Is.True);

                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);
                Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.True);

                WaitForArtifactTerminal(h, "worker did not reach the terminal for the artifact publication");

                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
                Assert.That(h.Publisher.ExecutingThreadName, Is.EqualTo(NvencRunPublicationService.WorkerThreadName));
                Assert.That(h.Publisher.ExecutingManagedThreadId, Is.EqualTo(h.Committer.ExecutingManagedThreadId));
            }
        }

        [Test]
        public void Artifact_Submit_InvalidForeignDouble_PublisherNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                CommitPlanAndCollect(h);
                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => h.Service.TrySubmitArtifactPublication(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(0));

                // A foreign-process operation is rejected.
                using (Harness h2 = Harness.Create())
                {
                    CommitPlanAndCollect(h2);
                    NvencRunArtifactPublicationOperation foreign = PrepareArtifactOperation(h2);
                    Assert.That(h.Service.TrySubmitArtifactPublication(foreign), Is.False);
                }

                Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.True);
                Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.False);

                WaitForArtifactTerminal(h, "worker did not reach the terminal for the artifact publication");
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Artifact_Submit_BeforePlanCollected_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                // The Plan commit result must be collected before the Artifact
                // phase is accepting.
                PreparePlanOperation(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan terminal");

                // The Service is still in the Plan phase; the coordinator
                // refuses the Artifact submission without contacting the
                // publisher.
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Artifact_Execute_ExactlyOnce_PublishedAndFailed()
        {
            using (Harness h = Harness.Create())
            {
                CommitPlanAndCollect(h);
                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);

                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
                Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.True);
                WaitForArtifactTerminal(h, "worker did not reach the terminal for the published artifact");
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.ArtifactPublicationCompleted));
                Assert.That(h.Service.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult published), Is.True);
                Assert.That(published.IsPublished, Is.True);
                Assert.That(published.Receipt, Is.Not.Null);
                Assert.That(published.Receipt.IsIssuedFor(h.Publisher, operation), Is.True);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
            }

            using (Harness h = Harness.Create())
            {
                CommitPlanAndCollect(h);
                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);

                h.Publisher.Status = NvencRunArtifactPublicationStatus.Failed;
                Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.True);
                WaitForArtifactTerminal(h, "worker did not reach the terminal for the failed artifact");
                Assert.That(h.Service.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult failed), Is.True);
                Assert.That(failed.IsFailed, Is.True);
                Assert.That(failed.Receipt, Is.Null);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Artifact_PublisherException_Poisons_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                CommitPlanAndCollect(h);
                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                h.Publisher.ExceptionToThrow = boom;

                Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the publisher exception");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out Exception failure), Is.True);
                Assert.That(ReferenceEquals(failure, boom), Is.True);
                Assert.That(h.Service.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
            }
        }

        [Test]
        public void Artifact_PoisonDuringExecution_NoNormalResult()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                CommitPlanAndCollect(h);
                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);

                h.Publisher.Entered = entered;
                h.Publisher.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitArtifactPublication(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "publisher did not enter");

                    Assert.That(h.State.TryPoison(), Is.True);
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceStop(h.Service, "worker did not stop after the mid-execution poison");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);

            }
        }

        [Test]
        public void Operation_IsBoundToProcessState_ExactReferenceOnly()
        {
            using (Harness h = Harness.Create())
            {
                CommitPlanAndCollect(h);
                NvencRunArtifactPublicationOperation operation = PrepareArtifactOperation(h);

                Assert.That(operation.IsBoundToProcessState(h.State), Is.True);
                Assert.That(operation.IsBoundToProcessState(new NvencCaptureProcessState()), Is.False);
                Assert.That(operation.IsBoundToProcessState(null), Is.False);
            }
        }

        // ---- Capture index commit phase ----

        [Test]
        public void Constructor_NullCaptureIndexCoordinator_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationService(
                        h.State,
                        new NvencRunPublicationPlanCommitExecutionCoordinator(new FakeCommitter()),
                        new NvencRunArtifactPublicationExecutionCoordinator(new FakePublisher()),
                        null,
                        new NvencRunCaptureCompleteExecutionCoordinator(new FakeRunCompleter())));
                Assert.That(ex.ParamName, Is.EqualTo("captureIndexCommitCoordinator"));
            }
        }

        [Test]
        public void Constructor_NullCaptureCompleteCoordinator_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationService(
                        h.State,
                        new NvencRunPublicationPlanCommitExecutionCoordinator(new FakeCommitter()),
                        new NvencRunArtifactPublicationExecutionCoordinator(new FakePublisher()),
                        new NvencRunCaptureIndexCommitExecutionCoordinator(new FakeIndexCommitter()),
                        null));
                Assert.That(ex.ParamName, Is.EqualTo("captureCompleteCoordinator"));
            }
        }

        [Test]
        public void CaptureIndex_SameServiceInstanceAndWorkerThread_NoSecondWorker()
        {
            using (Harness h = Harness.Create())
            {
                Thread workerBefore = (Thread)GetField(h.Service, "_workerThread");

                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                // All three phases share the one Service instance and the one
                // Worker thread.
                Thread workerAfter = (Thread)GetField(h.Service, "_workerThread");
                Assert.That(ReferenceEquals(workerBefore, workerAfter), Is.True);
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureIndexCommit));

                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                WaitForServiceState(h.Service, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                    "service did not publish the committed capture index terminal");

                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
                Assert.That(h.Committer.ExecutingManagedThreadId,
                    Is.EqualTo(h.Publisher.ExecutingManagedThreadId));
                Assert.That(workerAfter.ManagedThreadId,
                    Is.EqualTo(h.Publisher.ExecutingManagedThreadId));
            }
        }

        [Test]
        public void CaptureIndex_Committed_ReParksWorker_CollectsToAcceptingCaptureComplete()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;
                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                WaitForServiceState(h.Service, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                    "service did not publish the committed capture index terminal");

                // A Committed capture index keeps the Worker parked for the
                // later CaptureComplete phase.
                Assert.That(h.Service.IsStopped, Is.False);

                Assert.That(h.Service.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsCommitted, Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(h.IndexCommitter, operation), Is.True);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureComplete));
                Assert.That(h.Service.IsStopped, Is.False);

                // At-most-once collection.
                Assert.That(h.Service.TryCollectCaptureIndexCommit(out _), Is.False);
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CaptureIndex_Failed_StopsWorker_CollectsAfterStopOnly()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                h.IndexCommitter.Entered = entered;
                h.IndexCommitter.Release = release;

                try
                {
                    h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Failed;

                    Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                    // A poll while the Worker is still executing must not collect.
                    Assert.That(h.Service.TryCollectCaptureIndexCommit(out _), Is.False);
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceStop(h.Service, "worker did not stop after the failed capture index commit");
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.CaptureIndexCommitCompleted));

                Assert.That(h.Service.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.CaptureIndexCommitCollected));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));

            }
        }

        [Test]
        public void CaptureIndex_ExtraNotificationDuringExecution_DoesNotStopTheWorker()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                h.IndexCommitter.Entered = entered;
                h.IndexCommitter.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                    // A stray notification delivered while the capture index commit
                    // executes must not stop the Worker that CaptureComplete needs.
                    h.Service.Notify();
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceState(h.Service, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                    "service did not publish the committed capture index terminal");
                Assert.That(h.Service.IsStopped, Is.False);

                Assert.That(h.Service.TryCollectCaptureIndexCommit(out _), Is.True);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureComplete));

                // A further stray notification in the parked CaptureComplete
                // state also re-parks instead of stopping.
                h.Service.Notify();
                Assert.That(h.Service.IsStopped, Is.False);

            }
        }

        [Test]
        public void CaptureIndex_Submit_NullOrBeforeAccepting_CommitterNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                // Before the artifact publication is collected the Service is
                // not accepting the Capture Index phase.
                CommitPlanAndCollect(h);
                PrepareArtifactOperation(h);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => h.Service.TrySubmitCaptureIndexCommit(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));

                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "service did not reach the artifact terminal");

                // The artifact terminal is published but not collected yet, so
                // the Capture Index phase is still not accepting.
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.ArtifactPublicationCompleted));
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.False);
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void CaptureIndex_Submit_ForeignProcessOrDouble_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                // A foreign-process operation is rejected without contacting
                // the committer.
                using (Harness h2 = Harness.Create())
                {
                    NvencRunCaptureIndexCommitOperation foreign = PublishArtifactAndPrepareCaptureIndex(h2);
                    Assert.That(h.Service.TrySubmitCaptureIndexCommit(foreign), Is.False);
                    Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));
                }

                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.False);

                WaitForServiceState(h.Service, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                    "service did not publish the committed capture index terminal");
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CaptureIndex_CommitterException_Poisons_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                h.IndexCommitter.ExceptionToThrow = boom;

                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the committer exception");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out Exception failure), Is.True);
                Assert.That(ReferenceEquals(failure, boom), Is.True);

                // As in the Plan and Artifact phases, a coordinator exception
                // records the fatal failure and poisons the process; no normal
                // terminal is published and no result can be collected.
                Assert.That(h.Service.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
            }
        }

        [Test]
        public void CaptureIndex_CorruptResult_Poisons_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                // A default (None) attempt result is corrupt.
                h.IndexCommitter.UseOverride = true;
                h.IndexCommitter.OverrideResult = default;

                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the corrupt capture index result");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out _), Is.True);
                Assert.That(h.Service.TryCollectCaptureIndexCommit(out _), Is.False);
            }
        }

        [Test]
        public void CaptureIndex_MidExecutionPoison_NoNormalTerminal()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                h.IndexCommitter.Entered = entered;
                h.IndexCommitter.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                    Assert.That(h.State.TryPoison(), Is.True);
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceStop(h.Service, "worker did not stop after the mid-execution poison");

                // A Poison that linearized during execution fails closed: no
                // normal terminal is published and no result can be collected.
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);

            }
        }

        [Test]
        public void CaptureIndex_IsAttemptIssued_RejectsForeignOrDefault()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PublishArtifactAndPrepareCaptureIndex(h);

                Assert.That(h.Service.TrySubmitCaptureIndexCommit(operation), Is.True);
                WaitForServiceState(h.Service, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                    "service did not publish the committed capture index terminal");
                Assert.That(h.Service.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);

                Assert.That(h.Service.IsCaptureIndexCommitAttemptIssued(result, operation), Is.True);
                Assert.That(h.Service.IsCaptureIndexCommitAttemptIssued(result, null), Is.False);
                Assert.That(h.Service.IsCaptureIndexCommitAttemptIssued(default, operation), Is.False);

                // A result issued by a foreign committer for the same operation
                // is refused.
                FakeIndexCommitter foreignCommitter = new FakeIndexCommitter();
                NvencRunCaptureIndexCommitAttemptResult foreign =
                    NvencRunCaptureIndexCommitAttemptResult.Committed(foreignCommitter, operation);
                Assert.That(h.Service.IsCaptureIndexCommitAttemptIssued(foreign, operation), Is.False);
            }
        }

        // ---- CaptureComplete phase ----

        /// <summary>
        /// Drives the Plan, Artifact, and Capture Index phases to a Committed,
        /// collected capture index through the Run Coordinator, then mints the
        /// CaptureComplete operation. The same Service and Worker stay alive.
        /// </summary>
        private static NvencRunCaptureCompleteOperation CommitCaptureIndexAndPrepareCaptureComplete(Harness h)
        {
            NvencRunCaptureIndexCommitOperation index = PublishArtifactAndPrepareCaptureIndex(h);
            h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
            WaitForServiceState(h.Service, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                "service did not publish the committed capture index terminal");
            Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(out _), Is.True);
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureComplete));
            Assert.That(index, Is.Not.Null);

            Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                out NvencRunCaptureCompleteOperation operation), Is.True);
            return operation;
        }

        [Test]
        public void CaptureComplete_FourPhasesShareOneServiceAndWorker()
        {
            using (Harness h = Harness.Create())
            {
                Thread workerBefore = (Thread)GetField(h.Service, "_workerThread");

                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                Thread workerAfter = (Thread)GetField(h.Service, "_workerThread");
                Assert.That(ReferenceEquals(workerBefore, workerAfter), Is.True);
                Assert.That(h.Service.IsStopped, Is.False);

                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after CaptureComplete");

                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteCompleted));
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));
                Assert.That(workerAfter.ManagedThreadId,
                    Is.EqualTo(h.Publisher.ExecutingManagedThreadId));
            }
        }

        [Test]
        public void CaptureComplete_CompletedAndFailed_BothStopTheWorker_CollectAfterStopOnly()
        {
            foreach (NvencRunCaptureCompleteStatus status in new[]
            {
                NvencRunCaptureCompleteStatus.Completed,
                NvencRunCaptureCompleteStatus.Failed,
            })
            {
                using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
                using (ManualResetEventSlim release = new ManualResetEventSlim(false))
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteOperation operation =
                        CommitCaptureIndexAndPrepareCaptureComplete(h);

                    h.RunCompleter.Entered = entered;
                    h.RunCompleter.Release = release;

                    try
                    {
                        h.RunCompleter.Status = status;

                        Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                        Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "completer did not enter");
                        Assert.That(h.Service.State,
                            Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteExecuting));

                        // A poll while the Worker is still executing must not
                        // collect.
                        Assert.That(h.Service.TryCollectCaptureComplete(out _), Is.False);
                    }
                    finally
                    {
                        release.Set();
                    }

                    WaitForServiceStop(h.Service, "worker did not stop after the final phase");

                    Assert.That(h.Service.TryCollectCaptureComplete(
                        out NvencRunCaptureCompleteAttemptResult result), Is.True);
                    Assert.That(result.Status, Is.EqualTo(status));
                    Assert.That(result.Receipt,
                        status == NvencRunCaptureCompleteStatus.Completed ? Is.Not.Null : Is.Null);
                    Assert.That(h.Service.State,
                        Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteCollected));

                    // At-most-once collection and execution.
                    Assert.That(h.Service.TryCollectCaptureComplete(out _), Is.False);
                    Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));

                }
            }
        }

        [Test]
        public void CaptureComplete_Submit_NullDoubleForeignOrBeforeAccepting_CompleterNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                // Before the capture index commit is collected the Service is
                // not accepting the CaptureComplete phase.
                PublishArtifactAndPrepareCaptureIndex(h);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureIndexCommit));
                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.False);
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));

                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                    () => h.Service.TrySubmitCaptureComplete(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
            }

            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                // A foreign-process operation is rejected without contacting
                // the completer.
                using (Harness h2 = Harness.Create())
                {
                    NvencRunCaptureCompleteOperation foreign =
                        CommitCaptureIndexAndPrepareCaptureComplete(h2);
                    Assert.That(h.Service.TrySubmitCaptureComplete(foreign), Is.False);
                    Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
                }

                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.False);

                WaitForServiceStop(h.Service, "worker did not stop after CaptureComplete");
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CaptureComplete_PoisonBeforeSubmit_CompleterNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.False);
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void CaptureComplete_ExtraNotificationDuringExecution_DoesNotRunTwice()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                h.RunCompleter.Entered = entered;
                h.RunCompleter.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "completer did not enter");

                    // A stray notification delivered while CaptureComplete executes
                    // must not cause a second execution.
                    h.Service.Notify();
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceStop(h.Service, "worker did not stop after CaptureComplete");

                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteCompleted));

            }
        }

        [Test]
        public void CaptureComplete_CompleterException_Poisons_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                InvalidOperationException boom = new InvalidOperationException("boom");
                h.RunCompleter.ExceptionToThrow = boom;

                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the completer exception");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out Exception failure), Is.True);
                Assert.That(ReferenceEquals(failure, boom), Is.True);
                Assert.That(h.Service.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
            }
        }

        [Test]
        public void CaptureComplete_CorruptResult_Poisons_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                // A default (None) attempt result is corrupt.
                h.RunCompleter.UseOverride = true;
                h.RunCompleter.OverrideResult = default;

                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after the corrupt CaptureComplete result");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out _), Is.True);
                Assert.That(h.Service.TryCollectCaptureComplete(out _), Is.False);
            }
        }

        [Test]
        public void CaptureComplete_MidExecutionPoison_NoNormalTerminal()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                h.RunCompleter.Entered = entered;
                h.RunCompleter.Release = release;

                try
                {
                    Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "completer did not enter");

                    Assert.That(h.State.TryPoison(), Is.True);
                }
                finally
                {
                    release.Set();
                }

                WaitForServiceStop(h.Service, "worker did not stop after the mid-execution poison");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);

            }
        }

        [Test]
        public void CaptureComplete_IsAttemptIssued_RejectsForeignOrDefault()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation =
                    CommitCaptureIndexAndPrepareCaptureComplete(h);

                Assert.That(h.Service.TrySubmitCaptureComplete(operation), Is.True);
                WaitForServiceStop(h.Service, "worker did not stop after CaptureComplete");
                Assert.That(h.Service.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.True);

                Assert.That(h.Service.IsCaptureCompleteAttemptIssued(result, operation), Is.True);
                Assert.That(h.Service.IsCaptureCompleteAttemptIssued(result, null), Is.False);
                Assert.That(h.Service.IsCaptureCompleteAttemptIssued(default, operation), Is.False);

                FakeRunCompleter foreignCompleter = new FakeRunCompleter();
                NvencRunCaptureCompleteAttemptResult foreign =
                    NvencRunCaptureCompleteAttemptResult.Completed(foreignCompleter, operation);
                Assert.That(h.Service.IsCaptureCompleteAttemptIssued(foreign, operation), Is.False);
            }
        }

        // ---- Shape and source ----

        [Test]
        public void Service_Shape_Sealed_IDisposable_InternalEnum_NoContainerFields()
        {
            Type type = typeof(NvencRunPublicationService);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (FieldInfo field in fields)
            {
                string name = field.FieldType.Name;
                Assert.That(name, Does.Not.Contain("Queue"), field.Name + " must not be a queue.");
                Assert.That(name, Does.Not.Contain("List"), field.Name + " must not be a list.");
                Assert.That(name, Does.Not.Contain("Dictionary"), field.Name + " must not be a dictionary.");
                Assert.That(name, Does.Not.Contain("Task"), field.Name + " must not be a task.");
                Assert.That(name, Does.Not.Contain("Timer"), field.Name + " must not be a timer.");
            }

            Type enumType = typeof(NvencRunPublicationServiceState);
            Assert.That(enumType.IsEnum, Is.True);
            Assert.That(enumType.IsPublic, Is.False);

            // Append-only: the existing Plan phase values keep 0..6 and the
            // Artifact phase is appended after them.
            Assert.That((int)NvencRunPublicationServiceState.AcceptingPlanCommit, Is.EqualTo(0));
            Assert.That((int)NvencRunPublicationServiceState.PlanCommitQueued, Is.EqualTo(1));
            Assert.That((int)NvencRunPublicationServiceState.PlanCommitExecuting, Is.EqualTo(2));
            Assert.That((int)NvencRunPublicationServiceState.PlanCommitCompleted, Is.EqualTo(3));
            Assert.That((int)NvencRunPublicationServiceState.PlanCommitCollected, Is.EqualTo(4));
            Assert.That((int)NvencRunPublicationServiceState.Poisoned, Is.EqualTo(5));
            Assert.That((int)NvencRunPublicationServiceState.StoppedWithoutRequest, Is.EqualTo(6));
            Assert.That((int)NvencRunPublicationServiceState.AcceptingArtifactPublication, Is.EqualTo(7));
            Assert.That((int)NvencRunPublicationServiceState.ArtifactPublicationQueued, Is.EqualTo(8));
            Assert.That((int)NvencRunPublicationServiceState.ArtifactPublicationExecuting, Is.EqualTo(9));
            Assert.That((int)NvencRunPublicationServiceState.ArtifactPublicationCompleted, Is.EqualTo(10));
            Assert.That((int)NvencRunPublicationServiceState.ArtifactPublicationCollected, Is.EqualTo(11));
        }

        [Test]
        public void Source_NoThreadPoolTaskQueueRetryFilesystemRegistryLease()
        {
            string directory = RuntimeDirectory();
            string source = File.ReadAllText(
                Path.Combine(directory, "NvencRunPublicationService.cs"));

            Assert.That(CountOccurrences(source, "new Thread("), Is.EqualTo(1),
                "exactly one dedicated worker thread is required.");
            Assert.That(source, Does.Contain("IsBackground = true"));
            Assert.That(source, Does.Contain("Name = WorkerThreadName"));
            Assert.That(CountOccurrences(source, "_signal.WaitOne()"), Is.EqualTo(1),
                "exactly one wake primitive wait site is required.");
            Assert.That(source, Does.Contain("AutoResetEvent"),
                "the wake primitive must consume one notification per wait.");

            string[] forbidden =
            {
                "ThreadPool", "Task", "Thread.Sleep", "Timer", "SpinWait",
                "lock (", "Monitor", "new List", "new Dictionary", "new Queue",
                "OrderBy", "Array.Sort", "Peek", "System.Linq",
                "File.", "Directory.", "FileStream", "Stream", "Path.", "Flush(", "Move(", "Close(",
                "Registry", "Disposition", "TryCommit", "TryDiscardRegistered", "OwnershipLease",
                "Retry", "Rollback", "Cleanup", "re-read",
                "Abort", "Recovery", "Scheduler", "journal", "nonce",
                "JsonUtility", "ComputeHash", "HashAlgorithm", "IncrementalHash",
                "SHA256", "SHA384", "SHA512", "MD5", "System.Security.Cryptography",
                "DllImport", "IntPtr", "SafeHandle", "UnityEngine", "Application.",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "service source must not contain: " + word);
            }
        }

        [Test]
        public void Source_AutoReset_NoBusySpin()
        {
            string source = File.ReadAllText(
                Path.Combine(RuntimeDirectory(), "NvencRunPublicationService.cs"));

            Assert.That(source, Does.Contain("new AutoResetEvent(false)"),
                "the wake primitive must be an auto-reset event.");
            Assert.That(source, Does.Not.Contain("new ManualResetEventSlim"),
                "a manual-reset event without a reset-before-recheck would busy spin.");
            Assert.That(CountOccurrences(source, "_signal.WaitOne()"), Is.EqualTo(1),
                "exactly one blocking wait site is required.");
            Assert.That(source, Does.Not.Contain("_signal.Wait()"),
                "the worker must use the consuming WaitOne, never the non-consuming Wait.");
        }

        // ---- Helpers ----

        private static NvencRunPublicationPlanCommitOperation PreparePlanOperation(Harness h)
        {
            FinalizeAndFreeze(h, 1);
            Assert.That(
                h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                Is.True);
            return operation;
        }

        private static void CommitPlanAndCollect(Harness h)
        {
            PreparePlanOperation(h);
            h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
            WaitForServiceState(h.Service, NvencRunPublicationServiceState.PlanCommitCompleted,
                "service did not publish the committed plan terminal");
            Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);
        }

        private static NvencRunArtifactPublicationOperation PrepareArtifactOperation(Harness h)
        {
            Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                out NvencRunArtifactPublicationOperation operation), Is.True);
            return operation;
        }

        /// <summary>
        /// Drives the Plan and Artifact phases to a Published, collected
        /// artifact through the Run Coordinator, then mints the capture index
        /// commit operation. The same Service and Worker stay alive throughout.
        /// </summary>
        private static NvencRunCaptureIndexCommitOperation PublishArtifactAndPrepareCaptureIndex(Harness h)
        {
            CommitPlanAndCollect(h);
            PrepareArtifactOperation(h);
            h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
            Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
            WaitForArtifactTerminal(h, "service did not reach the artifact terminal");
            Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureIndexCommit));

            Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                out NvencRunCaptureIndexCommitOperation operation), Is.True);
            return operation;
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

        private static void WaitForServiceState(
            NvencRunPublicationService service,
            NvencRunPublicationServiceState expected,
            string message)
        {
            SpinWait.SpinUntil(() => service.State == expected, WatchdogTimeoutMs);
            Assert.That(service.State, Is.EqualTo(expected), message);
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
                WaitForServiceStop(h.Service, message);
            }
        }

        private static void WaitForServiceStop(NvencRunPublicationService service, string message)
        {
            FieldInfo field = typeof(NvencRunPublicationService).GetField(
                "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
            Thread worker = (Thread)field?.GetValue(service);
            if (worker != null)
            {
                Assert.That(worker.Join(WatchdogTimeoutMs), Is.True, message);
            }

            Assert.That(service.IsStopped, Is.True, message);
        }

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
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
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
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

        // ---- Fakes ----

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            private int _callCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;
            internal string ExecutingThreadName;
            internal int ExecutingManagedThreadId;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;
                ExecutingManagedThreadId = Thread.CurrentThread.ManagedThreadId;

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (Release != null)
                {
                    Release.Wait(WatchdogTimeoutMs);
                }

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
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
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;
            internal string ExecutingThreadName;
            internal int ExecutingManagedThreadId;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;
                ExecutingManagedThreadId = Thread.CurrentThread.ManagedThreadId;

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (Release != null)
                {
                    Release.Wait(WatchdogTimeoutMs);
                }

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
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

            internal FakeCommitter Committer;
            internal FakePublisher Publisher;
            internal FakeIndexCommitter IndexCommitter;
            internal FakeRunCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal FakeCleanupCleaner CleanupCleaner;
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

                // Stop the Publication Service worker if it is still parked
                // (a Committed plan that never entered the Artifact phase).
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
