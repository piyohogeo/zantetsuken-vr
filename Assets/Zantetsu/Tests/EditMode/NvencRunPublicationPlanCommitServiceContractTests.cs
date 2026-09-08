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
    /// Contract tests for the Phase 0.11 Publication Plan Commit Service: the
    /// fixed single-request-slot, one-shot Worker boundary that separates the
    /// publication plan commit execution from the Main/Render threads. Uses the
    /// finalized-and-frozen Run pipeline with a fake committer; no real GPU,
    /// NVENC, filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunPublicationPlanCommitServiceContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void SubmitCollect_ThreeStatuses_ExecutedOnceOnWorkerThread_NotReclassified()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                int mainThreadId = Thread.CurrentThread.ManagedThreadId;

                VerifyStatus(h, operation, NvencRunPublicationPlanCommitStatus.Committed, true, mainThreadId);
                VerifyStatus(h, operation, NvencRunPublicationPlanCommitStatus.FailedBeforeRename, false, mainThreadId);
                VerifyStatus(h, operation, NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown, false, mainThreadId);
            }
        }

        [Test]
        public void Capacity1_SecondSubmitRejectedInQueuedExecutingCompleted()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                // Executing: hold the worker inside Execute.
                {
                    ManualResetEventSlim entered = new ManualResetEventSlim(false);
                    ManualResetEventSlim release = new ManualResetEventSlim(false);
                    FakeCommitter committer = new FakeCommitter { Entered = entered, Release = release };
                    ManualResetEventSlim settled = new ManualResetEventSlim(false);
                    NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                    Assert.That(service.TrySubmit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");
                    Assert.That(service.State, Is.EqualTo(NvencRunPublicationPlanCommitServiceState.Executing));
                    Assert.That(service.TrySubmit(operation), Is.False);

                    release.Set();
                    WaitSettled(settled, "service did not settle after release");
                    WaitForServiceStop(service, "executing worker did not stop");
                    Assert.That(service.TryCollect(out _), Is.True);

                    service.Dispose();
                    settled.Dispose();
                    entered.Dispose();
                    release.Dispose();
                }

                // Completed: after the normal terminal is published.
                {
                    FakeCommitter committer = new FakeCommitter();
                    ManualResetEventSlim settled = new ManualResetEventSlim(false);
                    NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                    Assert.That(service.TrySubmit(operation), Is.True);
                    WaitSettled(settled, "service did not settle");
                    WaitForServiceStop(service, "completed worker did not stop");
                    Assert.That(service.State, Is.EqualTo(NvencRunPublicationPlanCommitServiceState.Completed));
                    Assert.That(service.TrySubmit(operation), Is.False);

                    Assert.That(service.TryCollect(out _), Is.True);
                    service.Dispose();
                    settled.Dispose();
                }

                // Queued: reject a second submission while the slot is occupied.
                // Run last because it poisons the shared process state.
                {
                    FakeCommitter committer = new FakeCommitter();
                    ManualResetEventSlim settled = new ManualResetEventSlim(false);
                    NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                    // Pin the slot into Queued together with the exact operation
                    // so the Worker never observes an inconsistent empty slot,
                    // even if it races ahead of the reflection freeze.
                    SetField(service, "_operation", operation);
                    SetField(service, "_state", (int)NvencRunPublicationPlanCommitServiceState.Queued);

                    Assert.That(service.TrySubmit(operation), Is.False);

                    Assert.That(h.State.TryPoison(), Is.True);
                    service.Notify();
                    WaitForServiceStop(service, "queued-slot worker did not stop");

                    service.Dispose();
                    settled.Dispose();
                }
            }
        }

        [Test]
        public void ForeignProcess_InvalidOperation_DoubleSubmit_NoCoordinatorContact()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                // A null operation is rejected before any side effect.
                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(() => service.TrySubmit(null));
                Assert.That(nullEx.ParamName, Is.EqualTo("operation"));

                // A reconstructed (invalid) operation is rejected.
                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, operation.TraceFreezeReceipt, operation.FinalizationResult, operation.Plan);
                Assert.That(reconstructed.IsValid, Is.False);
                Assert.That(service.TrySubmit(reconstructed), Is.False);

                // A foreign-process operation is rejected.
                using (Harness h2 = Harness.Create())
                {
                    FinalizeAndFreeze(h2, 1);
                    Assert.That(
                        h2.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation foreign),
                        Is.True);
                    Assert.That(service.TrySubmit(foreign), Is.False);
                }

                // The exact operation is accepted once; a second submission is rejected.
                Assert.That(service.TrySubmit(operation), Is.True);
                Assert.That(service.TrySubmit(operation), Is.False);

                WaitSettled(settled, "service did not settle after the accepted submission");
                WaitForServiceStop(service, "worker did not stop after the accepted submission");

                Assert.That(committer.CallCount, Is.EqualTo(1));
                Assert.That(service.TryCollect(out _), Is.True);

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void CoordinatorException_SameReferenceHeld_Poisoned_NoRetry_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                InvalidOperationException boom = new InvalidOperationException("boom");
                FakeCommitter committer = new FakeCommitter { ExceptionToThrow = boom };
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "service did not settle after the committer exception");
                WaitForServiceStop(service, "worker did not stop after the committer exception");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(service.TryGetFailure(out Exception failure), Is.True);
                Assert.That(ReferenceEquals(failure, boom), Is.True);
                Assert.That(committer.CallCount, Is.EqualTo(1));
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);
                Assert.That(service.IsStopped, Is.True);

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void Result_CorruptAttemptAndForeignOperation_PoisonsNoResult()
        {
            // Corrupt attempt result: the coordinator rejects it and the Service
            // must publish no normal terminal.
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                committer.UseOverride = true;
                committer.OverrideResult = MakeAttempt(
                    committer, operation, null, NvencRunPublicationPlanCommitStatus.Committed);

                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "service did not settle after the corrupt attempt result");
                WaitForServiceStop(service, "worker did not stop after the corrupt attempt result");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(service.TryGetFailure(out Exception failure), Is.True);
                Assert.That(failure, Is.TypeOf<InvalidOperationException>());
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);

                service.Dispose();
                settled.Dispose();
            }

            // Foreign result: the slot is swapped to a different operation while
            // the worker is inside Execute, so the returned result no longer
            // correlates to the exact submitted operation.
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                using (Harness h2 = Harness.Create())
                {
                    FinalizeAndFreeze(h2, 1);
                    Assert.That(
                        h2.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation foreign),
                        Is.True);

                    ManualResetEventSlim entered = new ManualResetEventSlim(false);
                    ManualResetEventSlim release = new ManualResetEventSlim(false);
                    FakeCommitter committer = new FakeCommitter { Entered = entered, Release = release };
                    ManualResetEventSlim settled = new ManualResetEventSlim(false);
                    NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                    Assert.That(service.TrySubmit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");
                    SetField(service, "_operation", foreign);
                    release.Set();

                    WaitSettled(settled, "service did not settle after the foreign result");
                    WaitForServiceStop(service, "worker did not stop after the foreign result");

                    Assert.That(h.State.IsPoisoned, Is.True);
                    Assert.That(service.TryGetFailure(out Exception failure), Is.True);
                    Assert.That(failure, Is.TypeOf<InvalidOperationException>());
                    Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                    Assert.That(result, Is.Null);

                    service.Dispose();
                    settled.Dispose();
                    entered.Dispose();
                    release.Dispose();
                }
            }
        }

        [Test]
        public void PoisonBeforeSubmit_NoExecution_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                Assert.That(h.State.TryPoison(), Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.False);
                Assert.That(committer.CallCount, Is.EqualTo(0));
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);

                service.Notify();
                WaitForServiceStop(service, "worker did not stop after the pre-submit poison");

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void PoisonDuringExecution_NoNormalResultPublished()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
                FakeCommitter committer = new FakeCommitter { Entered = entered, Release = release };
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                Assert.That(h.State.TryPoison(), Is.True);
                release.Set();

                WaitSettled(settled, "service did not settle after the mid-execution poison");
                WaitForServiceStop(service, "worker did not stop after the mid-execution poison");

                // The committer returned a valid Committed attempt, but the
                // Poison linearized during Execute: no normal terminal.
                Assert.That(committer.CallCount, Is.EqualTo(1));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);
                Assert.That(service.IsStopped, Is.True);

                service.Dispose();
                settled.Dispose();
                entered.Dispose();
                release.Dispose();
            }
        }

        [Test]
        public void CompletedResult_ExactlyOnceCollect_ReferencesCleared()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "service did not settle");
                WaitForServiceStop(service, "worker did not stop");

                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));

                // The slot is empty before Collected is published.
                Assert.That(GetField(service, "_operation"), Is.Null);
                Assert.That(GetField(service, "_result"), Is.Null);
                Assert.That(service.State, Is.EqualTo(NvencRunPublicationPlanCommitServiceState.Collected));

                // A second collection is false with a null result.
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult second), Is.False);
                Assert.That(second, Is.Null);

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void EarlyNotify_ThenSubmit_StillConverges()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                // An early notification while Accepting must not terminate the
                // Worker; a later submission must still converge.
                service.Notify();

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "service did not converge after early notify + submit");
                WaitForServiceStop(service, "worker did not stop after early notify + submit");

                Assert.That(committer.CallCount, Is.EqualTo(1));
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result, Is.Not.Null);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void Claim_LinearizedWithPoison_NoCoordinatorContact()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                // Freeze the slot to Queued so the Worker attempts to claim it.
                SetField(service, "_state", (int)NvencRunPublicationPlanCommitServiceState.Queued);
                SetField(service, "_operation", operation);

                // Hold the shared process-state gate, then poison and release:
                // the Worker's Queued -> Executing claim is serialized with the
                // Poison on the same gate, so the Poison that linearized first
                // means the coordinator is never contacted.
                Assert.That(h.State.TryBeginSubmitStep(), Is.True);
                service.Notify();
                Assert.That(h.State.TryPoison(), Is.True);
                h.State.EndSubmitStep();

                WaitSettled(settled, "worker did not stop after the claim/poison race");
                WaitForServiceStop(service, "worker did not stop after the claim/poison race");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(committer.CallCount, Is.EqualTo(0));
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void TryCollect_ConcurrentCallers_ExactlyOneSucceeds()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "service did not settle");
                WaitForServiceStop(service, "worker did not stop");

                int successes = 0;
                int nullResults = 0;
                ManualResetEventSlim start = new ManualResetEventSlim(false);
                Thread[] threads = new Thread[2];
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        start.Wait(WatchdogTimeoutMs);
                        if (service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult r))
                        {
                            Interlocked.Increment(ref successes);
                            if (r == null)
                            {
                                Interlocked.Increment(ref nullResults);
                            }
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
                Assert.That(Volatile.Read(ref nullResults), Is.EqualTo(0));
                Assert.That(service.State, Is.EqualTo(NvencRunPublicationPlanCommitServiceState.Collected));

                service.Dispose();
                settled.Dispose();
                start.Dispose();
            }
        }

        [Test]
        public void TryCollect_AfterExternalPoison_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                FakeCommitter committer = new FakeCommitter();
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "service did not settle");
                WaitForServiceStop(service, "worker did not stop");
                Assert.That(service.State, Is.EqualTo(NvencRunPublicationPlanCommitServiceState.Completed));

                // An external Poison after publication still prevents a normal
                // result from being collected.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void Notification_Deterministic_NoLostCompletion()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                int settledCount = 0;
                ManualResetEventSlim settled = new ManualResetEventSlim(false);
                FakeCommitter committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator coordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(committer);
                NvencRunPublicationPlanCommitService service =
                    new NvencRunPublicationPlanCommitService(h.State, coordinator);

                // Subscribe before any pump or submission so no completion is lost.
                service.Settled += () =>
                {
                    Interlocked.Increment(ref settledCount);
                    settled.Set();
                };

                Assert.That(service.TrySubmit(operation), Is.True);
                WaitSettled(settled, "no completion notification was delivered");
                WaitForServiceStop(service, "worker did not stop");

                Assert.That(Volatile.Read(ref settledCount), Is.EqualTo(1));
                Assert.That(service.TryCollect(out _), Is.True);

                service.Dispose();
                settled.Dispose();
            }
        }

        [Test]
        public void Dispose_IdempotentAfterStop_RejectsWhileRunning()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                // Dispose while running is rejected and never force-stops.
                {
                    ManualResetEventSlim entered = new ManualResetEventSlim(false);
                    ManualResetEventSlim release = new ManualResetEventSlim(false);
                    FakeCommitter committer = new FakeCommitter { Entered = entered, Release = release };
                    ManualResetEventSlim settled = new ManualResetEventSlim(false);
                    NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                    Assert.That(service.TrySubmit(operation), Is.True);
                    Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");
                    Assert.Throws<InvalidOperationException>(() => service.Dispose());
                    Assert.That(service.IsStopped, Is.False);

                    release.Set();
                    WaitSettled(settled, "service did not settle");
                    WaitForServiceStop(service, "worker did not stop");
                    Assert.That(service.TryCollect(out _), Is.True);
                    service.Dispose();
                    settled.Dispose();
                    entered.Dispose();
                    release.Dispose();
                }

                // Dispose after stop is idempotent.
                {
                    FakeCommitter committer = new FakeCommitter();
                    ManualResetEventSlim settled = new ManualResetEventSlim(false);
                    NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

                    Assert.That(service.TrySubmit(operation), Is.True);
                    WaitSettled(settled, "service did not settle");
                    WaitForServiceStop(service, "worker did not stop");
                    Assert.That(service.TryCollect(out _), Is.True);

                    service.Dispose();
                    service.Dispose();
                    Assert.That(service.IsStopped, Is.True);
                    settled.Dispose();
                }
            }
        }

        [Test]
        public void Operation_IsBoundToProcessState_ExactReferenceOnly()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                Assert.That(operation.IsBoundToProcessState(h.State), Is.True);
                Assert.That(operation.IsBoundToProcessState(new NvencCaptureProcessState()), Is.False);
                Assert.That(operation.IsBoundToProcessState(null), Is.False);
            }
        }

        [Test]
        public void Service_Shape_Sealed_IDisposable_InternalEnum_NoContainerFields()
        {
            Type type = typeof(NvencRunPublicationPlanCommitService);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (FieldInfo field in fields)
            {
                Type fieldType = field.FieldType;
                string name = fieldType.Name;
                Assert.That(name, Does.Not.Contain("Queue"), field.Name + " must not be a queue.");
                Assert.That(name, Does.Not.Contain("List"), field.Name + " must not be a list.");
                Assert.That(name, Does.Not.Contain("Dictionary"), field.Name + " must not be a dictionary.");
                Assert.That(name, Does.Not.Contain("Task"), field.Name + " must not be a task.");
                Assert.That(name, Does.Not.Contain("Timer"), field.Name + " must not be a timer.");
            }

            Type enumType = typeof(NvencRunPublicationPlanCommitServiceState);
            Assert.That(enumType.IsEnum, Is.True);
            Assert.That(enumType.IsPublic, Is.False);
        }

        [Test]
        public void Source_NoThreadPoolTaskQueueRetryFilesystemRegistryLease()
        {
            string directory = RuntimeDirectory();
            string source = File.ReadAllText(
                Path.Combine(directory, "NvencRunPublicationPlanCommitService.cs"));

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
                "ArtifactPublication", "CaptureComplete", "Abort", "Recovery", "Scheduler", "journal", "nonce",
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
                Path.Combine(RuntimeDirectory(), "NvencRunPublicationPlanCommitService.cs"));

            // The wake primitive must be an auto-reset event so each wait
            // consumes exactly one notification: a signaled event can never spin
            // the Worker, and an early notification is re-parked without loss.
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

        private static void VerifyStatus(
            Harness h,
            NvencRunPublicationPlanCommitOperation operation,
            NvencRunPublicationPlanCommitStatus status,
            bool expectReceipt,
            int mainThreadId)
        {
            FakeCommitter committer = new FakeCommitter { Status = status };
            ManualResetEventSlim settled = new ManualResetEventSlim(false);
            NvencRunPublicationPlanCommitService service = CreateService(h, committer, settled);

            Assert.That(service.TrySubmit(operation), Is.True);
            WaitSettled(settled, "service did not settle");
            WaitForServiceStop(service, "worker did not stop");

            Assert.That(committer.CallCount, Is.EqualTo(1));
            Assert.That(committer.ExecutingThreadName, Is.EqualTo(NvencRunPublicationPlanCommitService.WorkerThreadName));
            Assert.That(committer.ExecutingManagedThreadId, Is.Not.EqualTo(mainThreadId));

            Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Status, Is.EqualTo(status));
            Assert.That(result.Receipt != null, Is.EqualTo(expectReceipt));

            Assert.That(service.TryCollect(out NvencRunPublicationPlanCommitExecutionResult second), Is.False);
            Assert.That(second, Is.Null);

            service.Dispose();
            settled.Dispose();
        }

        private static NvencRunPublicationPlanCommitService CreateService(
            Harness h,
            FakeCommitter committer,
            ManualResetEventSlim settled)
        {
            NvencRunPublicationPlanCommitExecutionCoordinator coordinator =
                new NvencRunPublicationPlanCommitExecutionCoordinator(committer);
            NvencRunPublicationPlanCommitService service =
                new NvencRunPublicationPlanCommitService(h.State, coordinator);
            service.Settled += () => settled.Set();
            return service;
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void WaitForServiceStop(
            NvencRunPublicationPlanCommitService service,
            string message)
        {
            FieldInfo field = typeof(NvencRunPublicationPlanCommitService).GetField(
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
            internal bool UseOverride;
            internal NvencRunPublicationPlanCommitAttemptResult OverrideResult;
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

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze);

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
            }
        }
    }
}
