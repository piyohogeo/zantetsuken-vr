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
    /// Contract tests for the Phase 0.11 Main Thread Run coordinator: the
    /// drain entry, Accepted Snapshot fix, Main Thread Frame Completion
    /// reflection, terminal request, and terminal collection into the local
    /// Registry Slot. Uses a parked Output Worker and a never-started Submit
    /// Worker whose drain evidence is published deterministically; no real
    /// GPU, NVENC, filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencCaptureRunCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Drain and reflection ----

        [Test]
        public void Finalize_SingleSucceeded_ReflectedAndRegistered()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);

                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot snapshot), Is.True);
                Assert.That(snapshot.Count, Is.EqualTo(1));

                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);

                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the finalize request");
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(outcome.Result, Is.Not.Null);
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
                Assert.That(h.Slot.HasRegisteredEntry, Is.True);

                // The finalized result was registered exactly once into the
                // exact context; a second collection is empty.
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Finalize_120Succeeded_ReflectedInAcceptedOrder()
        {
            using (Harness h = Harness.Create())
            {
                for (long id = 1; id <= 120; id++)
                {
                    h.AcceptAndAppendChunk(id, 64, Seed);
                }

                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot snapshot), Is.True);
                Assert.That(snapshot.Count, Is.EqualTo(120));

                for (long id = 1; id <= 120; id++)
                {
                    Assert.That(
                        h.RunCoordinator.TryReflectCompletion(MakeCompletion(id, CaptureFrameCompletionStatus.Succeeded)),
                        Is.True);
                }

                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the finalize request");
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(h.Slot.HasRegisteredEntry, Is.True);
            }
        }

        [Test]
        public void Terminal_NotRequested_WhileCompletionUnreflected()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                // The Submit Worker reports drained, but the accepted frame's
                // Completion has not been reflected: no terminal request.
                h.SubmitDrained = true;
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.False);
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));

                // Reflecting the missing completion allows the request.
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            }
        }

        [Test]
        public void ZeroAccepted_RequestsAbandon_NoRegistryEntry()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot snapshot), Is.True);
                Assert.That(snapshot.Count, Is.EqualTo(0));

                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(outcome.Result, Is.Null);
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void FailedCompletion_RequestsAbandon()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Failed)), Is.True);

                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
            }
        }

        [Test]
        public void CancelledCompletion_RequestsAbandon()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Cancelled)), Is.True);

                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
                Assert.That(outcome.IsAbandoned, Is.True);
            }
        }

        [Test]
        public void BeginDrain_RunAlreadyAbandoned_FreezesAndConverges()
        {
            using (Harness h = Harness.Create())
            {
                // A controlled Output-side failure already advanced the
                // process to Run-Abandoned + Draining before the Main Thread
                // coordinator drains.
                Assert.That(h.State.TryBeginRunAbandoned(), Is.True);

                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot snapshot), Is.True);
                Assert.That(snapshot.Count, Is.EqualTo(0));

                // Idempotent: re-drain returns the exact same snapshot.
                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot again), Is.True);
                Assert.That(ReferenceEquals(snapshot, again), Is.True);

                h.SubmitDrained = true;
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            }
        }

        [Test]
        public void BeginDrain_Idempotent_SameSnapshotReference()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);

                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot first), Is.True);
                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot second), Is.True);
                Assert.That(ReferenceEquals(first, second), Is.True);
                Assert.That(first.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void BeginDrain_SubmitWorkerDisposed_PoisonsAndNoSuccessState()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);

                // Dispose the Submit Worker so BeginDrain throws after the
                // snapshot freeze; the coordinator must poison and must not
                // publish a success state that a re-entry could reuse.
                h.SubmitWorker.Dispose();

                Assert.Throws<ObjectDisposedException>(() => h.RunCoordinator.TryBeginDrain(out _));
                Assert.That(h.State.IsPoisoned, Is.True);

                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot after), Is.False);
                Assert.That(after, Is.Null);
            }
        }

        [Test]
        public void Reflect_BeforeDrain_RejectedWithoutChange()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);

                // Before the drain the reflection entry rejects without change
                // and without poisoning.
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void Reflect_Duplicate_PoisonsNoOutcome()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Reflect_Backward_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                h.AcceptAndAppendChunk(2, 48, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                // The second snapshot element is frame 2; presenting frame 2
                // first is a backward/out-of-order reflection.
                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(MakeCompletion(2, CaptureFrameCompletionStatus.Succeeded)));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Reflect_ForeignFrameId_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(MakeCompletion(5, CaptureFrameCompletionStatus.Succeeded)));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Reflect_NonZeroArtifactCount_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded, 1)));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Reflect_OverCapacity_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);

                // The snapshot holds one frame; a second reflection exceeds it.
                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(MakeCompletion(2, CaptureFrameCompletionStatus.Succeeded)));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Reflect_ForeignRunCompletion_PoisonsAndDoesNotAdvance()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                // A valid completion with the same frame id but a different
                // Run must poison and must not advance reflection or terminal.
                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(
                        MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded, testRunId: 2)));
                Assert.That(h.State.IsPoisoned, Is.True);

                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.False);
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.False);
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Terminal_TransientUnready_FalseThenRetryableAfterNotify()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);

                // Submit Worker not yet drained: non-waiting false, no request.
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.False);
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));

                // Progress publishes the drain evidence; retry now succeeds.
                h.SubmitDrained = true;
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            }
        }

        [Test]
        public void Collect_ForeignResult_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                // Corrupt the context's held result so the collected result no
                // longer correlates: the coordinator must poison, not convert.
                SetField(h.Context, "_finalizationResult", null);

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCollectTerminal(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Collect_RegistrationFailure_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                // Force the Registry Slot registration latch to reject.
                SetField(h.Slot, "_registrationEntered", true);

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCollectTerminal(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Collect_VariantMismatch_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                // The request was Abandon; a Finalized-claiming coordinator must
                // poison instead of accepting the mismatched outcome.
                SetField(h.RunCoordinator, "_requestedFinalize", true);

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCollectTerminal(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Poisoned_RefusesAllProgressEntries()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot drained), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);

                // Corrupt the reflection to poison the process.
                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)));
                Assert.That(h.State.IsPoisoned, Is.True);

                // Every progress entry now refuses without side effect, and a
                // drain re-entry must not report the previously-fixed state.
                Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot after), Is.False);
                Assert.That(after, Is.Null);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.False);
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.False);
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Collect_AfterPoison_DoesNotConsumeOutcome()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                // Poison after the outcome is published; the coordinator must
                // refuse collection without consuming the published outcome.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);

                // The published outcome is still available to the exact worker.
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome direct), Is.True);
                Assert.That(direct.IsFinalized, Is.True);
            }
        }

        [Test]
        public void Entry_GateBusy_RefusesWithoutChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                // A background thread holds the shared process-state gate,
                // exactly as a concurrent Poison transition would; every entry
                // must refuse without advancing any state.
                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.False);
                    Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.False);
                    Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
                    Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot blocked), Is.False);
                    Assert.That(blocked, Is.Null);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                // Once the gate is released the reflection proceeds again.
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            }
        }

        // ---- Output Worker teardown request ----

        [Test]
        public void Teardown_Finalized_ExecutesOnceThenStopsNormally()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the finalize request");
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(h.Slot.HasRegisteredEntry, Is.True);

                // The teardown is admitted only after collection + registration.
                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                Assert.That(h.Worker.TeardownCompleted, Is.True);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
                Assert.That(h.Teardown.ExecutingThreadName, Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));

                // At-most-once: no second request, collection, or registration.
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.False);
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Teardown_Abandoned_ExecutesOnceThenStops_NoRegistryEntry()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                Assert.That(h.Worker.TeardownCompleted, Is.True);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(1));
                Assert.That(h.Slot.HasRegisteredEntry, Is.False);
            }
        }

        [Test]
        public void Teardown_BeforeTerminalCollected_RefusesNoContact()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                // Terminal requested but not yet collected: teardown refused.
                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.False);
                Assert.That(h.Teardown.CallCount, Is.EqualTo(0));
                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.False);
            }
        }

        [Test]
        public void Teardown_AfterStop_TerminalAndTeardownRejected()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");
                Assert.That(h.Worker.IsStopped, Is.True);

                // After the physical stop no further terminal or teardown
                // request is admitted.
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.False);
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
            }
        }

        // ---- Main Thread NV12 Texture teardown ----

        [Test]
        public void MainThreadTeardown_Finalized_ExecutesOnceOnCallerThread()
        {
            using (Harness h = Harness.Create())
            {
                int mainThreadId = Thread.CurrentThread.ManagedThreadId;

                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
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

                // Publish the Submit Worker stop evidence.
                h.SubmitWorker.Dispose();

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.MainThreadTextureTeardownCompleted, Is.True);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(1));
                Assert.That(h.MainThreadTeardown.ExecutingThreadId, Is.EqualTo(mainThreadId));
                Assert.That(h.MainThreadTeardown.ExecutingThreadName, Is.Not.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));

                // Idempotent: the same completed result, with no second destroy.
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void MainThreadTeardown_Abandoned_ExecutesOnce()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
                Assert.That(outcome.IsAbandoned, Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(1));
                Assert.That(h.Slot.HasRegisteredEntry, Is.False);
            }
        }

        [Test]
        public void MainThreadTeardown_BeforeTerminalCollected_RefusesNoContact()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                h.SubmitWorker.Dispose();

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void MainThreadTeardown_BeforeTeardownRequested_RefusesNoContact()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SubmitWorker.Dispose();

                // Terminal collected but Output Worker teardown not requested.
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void MainThreadTeardown_SubmitWorkerNotStopped_RefusesNoContact()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                // The Submit Worker was never started and is not disposed:
                // its stop evidence is absent.
                Assert.That(h.SubmitWorker.IsStopped, Is.False);

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void MainThreadTeardown_OutputWorkerNotStopped_RefusesNoContact()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                // Park the Output Worker inside its teardown so it is
                // deterministically not yet stopped.
                h.Teardown.Entered = entered;
                h.Teardown.Release = release;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "worker teardown did not enter");

                h.SubmitWorker.Dispose();

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));

                release.Set();
                WaitSettled(h.SettledEvent, "worker did not complete the teardown after release");
                h.WaitForPhysicalStop("worker did not physically exit");
            }
        }

        [Test]
        public void MainThreadTeardown_OutputWorkerFatalStop_RefusesNoContact()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                InvalidOperationException boom = new InvalidOperationException("teardown boom");
                h.Teardown.ExceptionToThrow = boom;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the fatal teardown");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.Worker.TeardownCompleted, Is.False);
                Assert.That(h.Worker.IsStopped, Is.True);
                Assert.That(h.State.IsPoisoned, Is.True);

                h.SubmitWorker.Dispose();

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void MainThreadTeardown_GateBusy_RefusesNonBlocking()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                    Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");
            }
        }

        [Test]
        public void MainThreadTeardown_Exception_PropagatesPoisonsNoCompletion()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                InvalidOperationException boom = new InvalidOperationException("texture teardown boom");
                h.MainThreadTeardown.ExceptionToThrow = boom;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryCompleteMainThreadTextureTeardown());
                Assert.That(ReferenceEquals(ex, boom), Is.True);
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.MainThreadTextureTeardownCompleted, Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void MainThreadTeardown_NullReceipt_PoisonsNoCompletion()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                h.MainThreadTeardown.ReturnNull = true;

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCompleteMainThreadTextureTeardown());
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.MainThreadTextureTeardownCompleted, Is.False);
            }
        }

        [Test]
        public void MainThreadTeardown_ForeignReceipt_PoisonsNoCompletion()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                // A valid receipt whose bound Context field was corrupted to a
                // foreign Run must be rejected by the verification. The receipt
                // is obtained by running the exact teardown, then corrupted
                // directly.
                NvencMainThreadTextureTeardownReceipt receipt = h.MainThreadTeardown.TearDown();
                FieldInfo contextField = typeof(NvencMainThreadTextureTeardownReceipt).GetField(
                    "_context", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(contextField, Is.Not.Null, "_context field not found.");
                contextField.SetValue(receipt, MakeContext(h.State));
                h.MainThreadTeardown.ReceiptToReturn = receipt;

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCompleteMainThreadTextureTeardown());
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.MainThreadTextureTeardownCompleted, Is.False);
            }
        }

        [Test]
        public void MainThreadTeardown_Receipt_Construction_RejectsForeignContextBinding()
        {
            using (Harness h = Harness.Create())
            {
                // The constructor must require the implementation to actually
                // be bound to the supplied context, not just non-null.
                FakeMainThreadTeardown foreign = new FakeMainThreadTeardown { BoundContext = MakeContext(h.State) };
                Assert.Throws<ArgumentException>(() => new NvencMainThreadTextureTeardownReceipt(foreign, h.Context));

                Assert.Throws<ArgumentNullException>(() => new NvencMainThreadTextureTeardownReceipt(null, h.Context));
                Assert.Throws<ArgumentNullException>(() => new NvencMainThreadTextureTeardownReceipt(foreign, null));
            }
        }

        [Test]
        public void MainThreadTeardown_BindingSwappedAfterConstruction_RefusesNoContact()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                // Swap the teardown's internal Context binding after
                // construction: the side-effect-time re-check must refuse
                // without contacting the teardown.
                h.MainThreadTeardown.BoundContext = MakeContext(h.State);

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCompleteMainThreadTextureTeardown());
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.MainThreadTextureTeardownCompleted, Is.False);
            }
        }

        [Test]
        public void MainThreadTeardown_PoisonDuringTeardown_LinearizesAfterCompletion()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (ManualResetEventSlim poisonStarted = new ManualResetEventSlim(false))
            using (ManualResetEventSlim poisonDone = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                CollectTerminal(h, "worker did not converge the finalize request");

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                h.MainThreadTeardown.Entered = entered;
                h.MainThreadTeardown.Release = release;

                bool coordinatorResult = false;
                Exception coordinatorError = null;
                Thread coordinatorThread = new Thread(() =>
                {
                    try
                    {
                        coordinatorResult = h.RunCoordinator.TryCompleteMainThreadTextureTeardown();
                    }
                    catch (Exception ex)
                    {
                        coordinatorError = ex;
                    }
                })
                {
                    IsBackground = true,
                };
                coordinatorThread.Start();

                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "main thread teardown did not enter");

                Thread poisonThread = new Thread(() =>
                {
                    poisonStarted.Set();
                    h.State.TryPoison();
                    poisonDone.Set();
                })
                {
                    IsBackground = true,
                };
                poisonThread.Start();
                Assert.That(poisonStarted.Wait(WatchdogTimeoutMs), Is.True, "poison thread did not start");

                // The coordinator holds the gate during the texture teardown,
                // so the poison must still be blocked.
                Assert.That(poisonDone.IsSet, Is.False);

                release.Set();

                Assert.That(coordinatorThread.Join(WatchdogTimeoutMs), Is.True, "coordinator thread did not exit");
                Assert.That(coordinatorError, Is.Null);
                Assert.That(coordinatorResult, Is.True);
                Assert.That(poisonDone.Wait(WatchdogTimeoutMs), Is.True, "poison did not linearize");

                // The completion was published before the poison linearized.
                Assert.That(h.RunCoordinator.MainThreadTextureTeardownCompleted, Is.True);
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void MainThreadTeardown_Receipt_TypeShape_ExactIssuerOnly()
        {
            Type type = typeof(NvencMainThreadTextureTeardownReceipt);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(INvencMainThreadTextureTeardown)));
            Assert.That(fields[1].FieldType, Is.EqualTo(typeof(NvencRunChunkContext)));

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencMainThreadTextureTeardownReceipt.cs"));
            string[] forbidden =
            {
                "IntPtr", "DllImport", "byte[", "Texture2D", "RenderTexture",
                "Task", "ThreadPool", "new Thread",
            };
            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "receipt source must not contain: " + word);
            }
        }

        [Test]
        public void MainThreadTeardown_Receipt_IsIssuedFor_RejectsNullAndForeign()
        {
            using (Harness h = Harness.Create())
            {
                FakeMainThreadTeardown issued = h.MainThreadTeardown;
                NvencMainThreadTextureTeardownReceipt receipt = issued.TearDown();

                Assert.That(receipt.IsIssuedFor(issued, h.Context), Is.True);

                // Null issuer or null context are rejected without throwing.
                Assert.That(receipt.IsIssuedFor(null, h.Context), Is.False);
                Assert.That(receipt.IsIssuedFor(issued, null), Is.False);

                // A foreign issuer bound to the same context is rejected.
                FakeMainThreadTeardown foreignIssuer = new FakeMainThreadTeardown { BoundContext = h.Context };
                Assert.That(receipt.IsIssuedFor(foreignIssuer, h.Context), Is.False);

                // The same issuer bound to a foreign context is rejected.
                Assert.That(receipt.IsIssuedFor(issued, MakeContext(h.State)), Is.False);
            }
        }

        [Test]
        public void MainThreadTeardown_Coordinator_NoPlanPublicationTraceRegistryLeaseTryJoin()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencCaptureRunCoordinator.cs"));

            int start = source.IndexOf("internal bool TryCompleteMainThreadTextureTeardown", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "teardown entry not found");

            // Scan only the teardown entry, up to the following backend join
            // entry, so a later independent entry never pollutes the teardown
            // contract.
            int nextEntry = source.IndexOf("internal bool TryCompleteBackendJoin", start + 1, StringComparison.Ordinal);
            string entry = nextEntry >= 0
                ? source.Substring(start, nextEntry - start)
                : source.Substring(start);

            string[] forbidden =
            {
                "Plan", "Publication", "Trace", "Registry", "Lease", "TryJoin",
                "NvEnc", "IntPtr", "DllImport", "byte[",
            };
            foreach (string word in forbidden)
            {
                Assert.That(entry, Does.Not.Contain(word), "teardown entry must not contain: " + word);
            }
        }

        // ---- Backend Join ----

        [Test]
        public void BackendJoin_Finalized_JoinsOnceAndDisposesWorkers()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.RunCoordinator.BackendJoined, Is.True);
                Assert.That(h.BackendJoin.Joined, Is.True);

                // Idempotent: a second join publishes the same completed result
                // with no second dispose; the disposed worker rejects a later
                // notification, proving the dispose actually ran exactly once.
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.RunCoordinator.BackendJoined, Is.True);
                Assert.Throws<ObjectDisposedException>(() => h.Worker.Notify());
            }
        }

        [Test]
        public void BackendJoin_Abandoned_JoinsOnce()
        {
            using (Harness h = Harness.Create())
            {
                StopAbandonedBackend(h);

                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.RunCoordinator.BackendJoined, Is.True);
                Assert.That(h.BackendJoin.Joined, Is.True);
                Assert.That(h.Slot.HasRegisteredEntry, Is.False);
            }
        }

        [Test]
        public void BackendJoin_BeforeTextureTeardown_RefusesNoBackendContact()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);

                // The Main Thread NV12 Texture teardown has not completed:
                // the backend boundary is never contacted and no worker is
                // disposed.
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.False);
                Assert.That(h.RunCoordinator.BackendJoined, Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
                Assert.DoesNotThrow(() => h.Worker.Notify());
            }
        }

        [Test]
        public void BackendJoin_BufferNotFree_RefusesNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                // Re-claim the Access Unit after the run fully drained: the
                // owned region is no longer Free, so the join is refused.
                Assert.That(h.Buffer.TryBeginWrite(MakeToken(99), out _), Is.True);
                Assert.That(h.Buffer.Phase, Is.Not.EqualTo(NvencAccessUnitPhase.Free));

                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.False);
                Assert.That(h.RunCoordinator.BackendJoined, Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());
            }
        }

        [Test]
        public void BackendJoin_PoolOccupied_RefusesNoDispose()
        {
            Action<Harness>[] renters =
            {
                h => Assert.That(h.WorkSlots.TryRent(out _), Is.True),
                h => Assert.That(h.SampleSlots.TryRent(out _), Is.True),
                h => Assert.That(h.SubmitSyncSlots.TryRent(out _), Is.True),
                h => Assert.That(h.SubmitToOutputCredits.TryRent(out _), Is.True),
                h => Assert.That(h.FrameCompletionCredits.TryRent(out _), Is.True),
            };

            foreach (Action<Harness> rent in renters)
            {
                using (Harness h = Harness.Create())
                {
                    rent(h);
                    StopFinalizedBackend(h);
                    Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                    Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.False);
                    Assert.That(h.RunCoordinator.BackendJoined, Is.False);
                    Assert.That(h.BackendJoin.Joined, Is.False);
                    Assert.DoesNotThrow(() => h.Worker.Notify());
                }
            }
        }

        [Test]
        public void BackendJoin_ProcessorPending_RefusesNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                FieldInfo stageField = typeof(NvencOrderedOutputProcessor).GetField(
                    "_stage", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(stageField, Is.Not.Null, "_stage field not found.");
                stageField.SetValue(h.Processor, Enum.Parse(stageField.FieldType, "SubmittedCollect"));
                Assert.That(h.Processor.HasPendingWork, Is.True);

                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.False);
                Assert.That(h.RunCoordinator.BackendJoined, Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());
            }
        }

        [Test]
        public void BackendJoin_NullOrForeignObjectProof_RefusesNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);

                // The proof can only be minted by the Run Coordinator: a null
                // token or a plain object is refused at construction, so it can
                // never reach the join boundary.
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator.BackendJoinProof(null));
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator.BackendJoinProof(new object()));

                // A null proof is refused without touching either worker.
                Assert.That(h.BackendJoin.TryJoin(null), Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());
            }
        }

        [Test]
        public void BackendJoin_Proof_PrivateGatedNoPublicConstructor()
        {
            Type proofType = typeof(NvencCaptureRunCoordinator).GetNestedType(
                "BackendJoinProof", BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(proofType, Is.Not.Null, "proof type not found.");

            Assert.That(proofType.IsClass, Is.True);
            Assert.That(proofType.IsSealed, Is.True);
            Assert.That(proofType.IsPublic, Is.False);

            // The single constructor demands the private mint token, so only
            // the Run Coordinator can construct the proof and no other code can
            // forge the join authority.
            ConstructorInfo[] constructors = proofType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(constructors, Has.Length.EqualTo(1));

            ParameterInfo[] parameters = constructors[0].GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(object)),
                "the proof constructor must take an opaque token object.");

            Type tokenType = typeof(NvencCaptureRunCoordinator).GetNestedType(
                "TextureTeardownMintToken", BindingFlags.NonPublic);
            Assert.That(tokenType, Is.Not.Null, "mint token type not found.");
            Assert.That(tokenType.IsNestedPrivate, Is.True, "the mint token must be private.");

            Assert.That(proofType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                Is.Empty, "the proof must hold no state.");
        }

        [Test]
        public void BackendJoin_DisposeException_PoisonsNoJoinNoRetry()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                // Corrupt the Output Worker's wait primitive so its dispose
                // throws after the lifecycle transition; the IsStopped
                // precondition still holds, so the catch path is exercised.
                SetField(h.Worker, "_signal", null);

                Assert.Throws<NullReferenceException>(() => h.RunCoordinator.TryCompleteBackendJoin());
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.BackendJoined, Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);

                // A poisoned retry never partially re-enters the dispose path.
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.False);
            }
        }

        [Test]
        public void BackendJoin_BindingSwappedAfterConstruction_PoisonsNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                // Swap the join boundary's internal Run context after
                // construction: the side-effect-time re-check must refuse
                // without disposing either worker.
                SetField(h.BackendJoin, "_context", MakeContext(h.State));

                Assert.Throws<InvalidOperationException>(() => h.RunCoordinator.TryCompleteBackendJoin());
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.BackendJoined, Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());
            }
        }

        [Test]
        public void Constructor_ForeignBackendJoin_Rejected()
        {
            using (Harness h = Harness.Create())
            using (Harness other = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, other.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_ForeignTeardownBackendJoin_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                // A Backend Join bound to the exact same context but a
                // different teardown implementation must be rejected at
                // construction: otherwise teardown A's valid receipt is
                // refused by teardown B and the Run becomes permanently
                // unjoinable.
                NvencCaptureBackendJoinCoordinator foreignTeardownJoin = new NvencCaptureBackendJoinCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context,
                    h.WorkSlots, h.SampleSlots, h.SubmitSyncSlots, h.SubmitToOutputCredits, h.FrameCompletionCredits,
                    h.Buffer, h.Processor, new FakeMainThreadTeardown { BoundContext = h.Context });

                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, foreignTeardownJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void BackendJoin_SealedNotDisposable_FieldShapeClean()
        {
            Type type = typeof(NvencCaptureBackendJoinCoordinator);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            Type[] forbiddenFieldTypes =
            {
                typeof(Thread), typeof(System.Threading.Timer), typeof(Stream), typeof(byte[]),
            };

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(13));
            foreach (FieldInfo field in fields)
            {
                Assert.That(Array.IndexOf(forbiddenFieldTypes, field.FieldType), Is.LessThan(0),
                    field.Name + " must not hold a forbidden type.");
                // Every collaborator reference is readonly; only the joined
                // latch is a mutable bool.
                Assert.That(field.IsInitOnly || field.FieldType == typeof(bool), Is.True,
                    field.Name + " must be readonly or the bool joined latch.");
            }
        }

        [Test]
        public void BackendJoin_NoWaitNoSleepNoFilesystemNoContextSideContact()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencCaptureBackendJoinCoordinator.cs"));

            string[] forbidden =
            {
                "Thread.Sleep", "new Thread", "ThreadPool", "Task", "SpinWait", "WaitHandle",
                "ManualResetEvent", "AutoResetEvent", "Timer", "Monitor", "File.", "Directory.",
                "FileStream", "NvEnc", "UnityEngine", "DllImport", "new []", "new List",
                "new Dictionary", "new Queue", "Guid.NewGuid", "Enumerable",
                "Terminal", "Registry", "Trace", "Plan", "Publication", "Disposition",
                ".Select(", ".Where(", ".ToList(", ".ToArray(",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "backend join source must not contain: " + word);
            }
        }

        [Test]
        public void BackendJoinConstructor_ForeignSubmitResources_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                // A foreign empty Work Slot pool passed as the Submit
                // pipeline's pool must be rejected at construction: an empty
                // stand-in can never mask a reservation in the real pipeline.
                NvencCaptureWorkSlotPool foreignWork = new NvencCaptureWorkSlotPool(h.State);
                Assert.Throws<ArgumentException>(() => new NvencCaptureBackendJoinCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context,
                    foreignWork, h.SampleSlots, h.SubmitSyncSlots, h.SubmitToOutputCredits, h.FrameCompletionCredits,
                    h.Buffer, h.Processor, h.MainThreadTeardown));

                // A foreign empty Frame Completion credit pool is equally
                // rejected on the Submit pipeline.
                NvencFrameCompletionCreditPool foreignCredits = new NvencFrameCompletionCreditPool(h.State);
                Assert.Throws<ArgumentException>(() => new NvencCaptureBackendJoinCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context,
                    h.WorkSlots, h.SampleSlots, h.SubmitSyncSlots, h.SubmitToOutputCredits, foreignCredits,
                    h.Buffer, h.Processor, h.MainThreadTeardown));
            }
        }

        [Test]
        public void BackendJoinConstructor_ForeignOutputResources_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                // A foreign Owned Access Unit buffer passed as the Output
                // pipeline's buffer must be rejected at construction.
                NvencOwnedAccessUnitBuffer foreignBuffer = new NvencOwnedAccessUnitBuffer(h.State);
                Assert.Throws<ArgumentException>(() => new NvencCaptureBackendJoinCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context,
                    h.WorkSlots, h.SampleSlots, h.SubmitSyncSlots, h.SubmitToOutputCredits, h.FrameCompletionCredits,
                    foreignBuffer, h.Processor, h.MainThreadTeardown));

                // A foreign Encode Sample Slot pool is equally rejected on the
                // Output pipeline.
                NvencEncodeSampleSlotPool foreignSamples = new NvencEncodeSampleSlotPool(h.State);
                Assert.Throws<ArgumentException>(() => new NvencCaptureBackendJoinCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context,
                    h.WorkSlots, foreignSamples, h.SubmitSyncSlots, h.SubmitToOutputCredits, h.FrameCompletionCredits,
                    h.Buffer, h.Processor, h.MainThreadTeardown));
            }
        }

        // ---- Trace Freeze and disposition ----

        [Test]
        public void TraceFreeze_FinalizedRegistered_FreezesAndFinalizes()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);

                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out NvencTraceFreezeReceipt receipt), Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(h.TraceRecorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                // Finalized keeps the Registry slot Registered; it is not
                // advanced to Committed here.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));

                // Idempotent: a re-call returns the same receipt and never
                // re-seals or re-appends the trace.
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out NvencTraceFreezeReceipt again), Is.True);
                Assert.That(ReferenceEquals(receipt, again), Is.True);
            }
        }

        [Test]
        public void TraceFreeze_AbandonedEmpty_FreezesAndIncomplete()
        {
            using (Harness h = Harness.Create())
            {
                StopAbandonedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);

                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out NvencTraceFreezeReceipt receipt), Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(h.TraceRecorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));

                // Abandoned keeps the Registry slot Empty.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
            }
        }

        [Test]
        public void Incomplete_NoPlanSubmitted_ServiceStopsNormally_ProcessStaysDraining()
        {
            using (Harness h = Harness.Create())
            {
                StopAbandonedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);

                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));

                // No Plan was ever prepared or submitted: the unused Service
                // stops normally without poisoning while the process stays
                // Draining.
                Assert.That(h.RunCoordinator.TryStopPublicationService(), Is.True);

                WaitForServiceStop(h, "service worker did not stop normally");

                Assert.That(h.State.IsDraining, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.StoppedWithoutRequest));

                // The worker is physically stopped; the non-waiting completion
                // entry releases the wait handle exactly once, and a re-call is
                // idempotent.
                Assert.That(h.RunCoordinator.TryCompletePublicationServiceStop(), Is.True);
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That((int)GetField(h.Service, "_lifecycleState"), Is.EqualTo(1));
                Assert.That(h.RunCoordinator.TryCompletePublicationServiceStop(), Is.True);
            }
        }

        [Test]
        public void StopService_Running_Rejected_ServiceUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                // Run start: not Draining, no disposition, no operation. The
                // stop request must be rejected without locking the Service.
                Assert.That(h.RunCoordinator.TryStopPublicationService(), Is.False);

                Assert.That(h.State.IsAccepting, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.None));
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingPlanCommit));
            }
        }

        [Test]
        public void StopService_Finalized_NoPlan_Rejected_ServiceUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeOnly(h);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
                Assert.That(h.RunCoordinator.TryStopPublicationService(), Is.False);

                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingPlanCommit));
            }
        }

        [Test]
        public void StopService_PreparedOperation_Rejected_ServiceStillSubmittable()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
                Assert.That(h.RunCoordinator.TryStopPublicationService(), Is.False);
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingPlanCommit));

                // The prepared operation is untouched: the Service is still
                // usable and the submission still proceeds.
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void StopService_Committed_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                Assert.That(h.RunCoordinator.TryStopPublicationService(), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void StopService_CommitOutcomeUnknown_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown;
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceStop(h, "service worker did not stop");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.CommitOutcomeUnknown));

                Assert.That(h.RunCoordinator.TryStopPublicationService(), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void CompleteServiceStop_BeforeWorkerStop_ReturnsFalseNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                // The Service is still parked Accepting: its Worker is alive,
                // so the non-waiting completion entry must neither dispose nor
                // publish release evidence.
                Assert.That(h.RunCoordinator.TryCompletePublicationServiceStop(), Is.False);
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
                Assert.That((int)GetField(h.Service, "_lifecycleState"), Is.EqualTo(0));
            }
        }

        [Test]
        public void CompleteServiceStop_SubmittedCompletedUncollected_Rejected_ResultCollectable()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");

                // The Worker reached Completed with the commit Result still
                // uncollected: this is a commit terminal, not a normal
                // StoppedWithoutRequest release. The completion entry must not
                // dispose the Service or publish release evidence.
                Assert.That(h.RunCoordinator.TryCompletePublicationServiceStop(), Is.False);
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
                Assert.That((int)GetField(h.Service, "_lifecycleState"), Is.EqualTo(0));

                // The commit Result is still normally collectable afterwards.
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result, Is.Not.Null);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            }
        }

        // ---- Artifact publication preparation ----

        [Test]
        public void PrepareArtifactPublication_Committed_ForwardsExactReferences()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitExecutionResult result =
                    CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation operation), Is.True);

                // The operation holds the exact retained commit result and
                // forwards the rest of the graph without copying.
                Assert.That(operation.PlanCommitResult, Is.SameAs(result));
                Assert.That(operation.Plan, Is.SameAs(result.Plan));
                Assert.That(operation.FinalizationResult, Is.SameAs(result.FinalizationResult));
                Assert.That(operation.Descriptor, Is.SameAs(result.FinalizationResult.Descriptor));
                Assert.That(operation.FrameRelation, Is.SameAs(result.FinalizationResult.FrameRelation));
                Assert.That(operation.RootLayout, Is.SameAs(result.RootLayout));
                Assert.That(operation.TestRunId, Is.EqualTo(h.Context.TestRunId));
                Assert.That(operation.RunInitializationId, Is.EqualTo(result.RunInitializationId));

                // Paths, length, and hash match the exact descriptor, and never
                // the pending (.partial) path.
                CaptureArtifactDescriptor descriptor = result.FinalizationResult.Descriptor;
                Assert.That(operation.StagingRelativePath, Is.EqualTo(descriptor.StagingRelativePath));
                Assert.That(operation.FinalRelativePath, Is.EqualTo(descriptor.FinalRelativePath));
                Assert.That(operation.ExpectedByteLength, Is.EqualTo(descriptor.ByteLength));
                Assert.That(operation.ExpectedContentHash, Is.EqualTo(descriptor.ContentHash));
                Assert.That(operation.StagingRelativePath,
                    Is.Not.EqualTo(NvencRunChunkArtifactDescriptorFactory.PendingRelativePath));
                Assert.That(operation.FinalRelativePath,
                    Is.Not.EqualTo(NvencRunChunkArtifactDescriptorFactory.PendingRelativePath));
            }
        }

        [Test]
        public void PrepareArtifactPublication_Idempotent_ReturnsSameReference()
        {
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation first), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation again), Is.True);

                Assert.That(ReferenceEquals(first, again), Is.True);
            }
        }

        [Test]
        public void PrepareArtifactPublication_FinalizedNotCollected_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation operation), Is.False);
                Assert.That(operation, Is.Null);
            }
        }

        [Test]
        public void PrepareArtifactPublication_SubmittedNotCollected_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");

                // Still Finalized: the commit result has not been collected.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(out _), Is.False);
            }
        }

        [Test]
        public void PrepareArtifactPublication_FailedBeforeRename_Incomplete_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.FailedBeforeRename);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(out _), Is.False);
            }
        }

        [Test]
        public void PrepareArtifactPublication_CommitOutcomeUnknown_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.CommitOutcomeUnknown));

                // Refuses before inspecting any file, chunk, or temporary.
                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(out _), Is.False);
            }
        }

        [Test]
        public void PrepareArtifactPublication_ExternalPoisonFirst_ReturnsFalseNoOperation()
        {
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation operation), Is.False);
                Assert.That(operation, Is.Null);
            }
        }

        [Test]
        public void PrepareArtifactPublication_GateContention_ReturnsFalseNoChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                        out NvencRunArtifactPublicationOperation operation), Is.False);
                    Assert.That(operation, Is.Null);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                // Once the gate is released the preparation proceeds.
                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation prepared), Is.True);
                Assert.That(prepared, Is.Not.Null);
            }
        }

        [Test]
        public void PrepareArtifactPublication_CorruptRetainedResult_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
                SetField(h.RunCoordinator, "_publicationPlanCommitResult", null);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareArtifactPublication(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void PrepareArtifactPublication_CorruptRegistryCorrelation_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
                SetField(h.Slot, "_result", null);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareArtifactPublication(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void PrepareArtifactPublication_DoesNotChangeDispositionRegistryPlanChunkLease()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunPublicationPlanCommitExecutionResult result =
                    CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
                CapturePublicationPlan planBefore = result.Plan;
                NvencChunkFinalizationResult finalizationBefore = result.FinalizationResult;
                Assert.That(h.SessionIssue.IsValid, Is.True);

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation operation), Is.True);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(operation.Plan, Is.SameAs(planBefore));
                Assert.That(operation.FinalizationResult, Is.SameAs(finalizationBefore));
                Assert.That(h.SessionIssue.IsValid, Is.True);
            }
        }

        [Test]
        public void PrepareArtifactPublication_InitialNoneDisposition_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                // Fresh Run: process Running and disposition None. Refused
                // without change, without reaching any Committed correlation.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.None));

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.None));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareArtifactPublication_AfterFailedPublicationCollected_RefusesWithoutPoison()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Failed;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the artifact terminal");
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                int publisherCalls = h.Publisher.CallCount;

                // The operation stays retained while its Committed-only
                // validity is false, which is the normal Recovery terminal and
                // not corruption: refuse with no change and no exception.
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsBindingIntact, Is.True);

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation again), Is.False);
                Assert.That(again, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));

                // The retained Failed result is still collectible, unchanged
                // and still correlated.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult retained), Is.True);
                Assert.That(ReferenceEquals(retained.Operation, operation), Is.True);
                Assert.That(retained.Status, Is.EqualTo(NvencRunArtifactPublicationStatus.Failed));
                Assert.That(retained.Receipt, Is.Null);
                Assert.That(retained.IsValid, Is.True);
            }
        }

        [Test]
        public void PrepareArtifactPublication_PoisonAfterPrepare_InvalidatesOperationAndRefuses()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PrepareArtifactSubmission(h);
                Assert.That(operation.IsValid, Is.True);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;

                Assert.That(h.State.TryPoison(), Is.True);

                // Poison outranks the retained operation: the already-issued
                // one stops being usable and the re-call refuses without an
                // exception.
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.False);

                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation again), Is.False);
                Assert.That(again, Is.Null);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
            }
        }

        [Test]
        public void PrepareArtifactPublication_CommittedWithBrokenRetainedOperation_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PrepareArtifactSubmission(h);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                // Releasing the Session Ownership Lease breaks the retained
                // graph while the published disposition is still Committed.
                // That is corruption, so the normal-terminal refusal above must
                // not swallow it.
                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsBindingIntact, Is.False);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareArtifactPublication(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        // ---- Artifact publication submit and reflection ----

        private static NvencRunArtifactPublicationOperation PrepareArtifactSubmission(Harness h)
        {
            CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
            Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                out NvencRunArtifactPublicationOperation operation), Is.True);
            return operation;
        }

        [Test]
        public void SubmitArtifactPublication_Committed_ForwardsExactOperationOnce()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PrepareArtifactSubmission(h);

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");

                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsPublished, Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.Receipt.IsIssuedFor(h.Publisher, operation), Is.True);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void SubmitArtifactPublication_BeforePrepare_PlanUncollected_Poisoned_GateBusy_NoPublisherContact()
        {
            // No retained operation: refused before the publisher is contacted.
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);
                SetField(h.RunCoordinator, "_artifactPublicationOperation", null);
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(0));
            }

            // Plan commit result not yet collected: refused.
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan terminal");
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(0));
            }

            // Double submission: the retained operation is handed over exactly
            // once.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
            }

            // Poisoned: the process-state gate refuses before any Service contact.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                Assert.That(h.Publisher.CallCount, Is.EqualTo(0));
            }

            // Gate contention: a background holder keeps the shared gate busy.
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                    Assert.That(h.Publisher.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CollectArtifactPublication_Published_KeepsCommittedAndReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");

                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsPublished, Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(h.Publisher, operation), Is.True);

                // Registry and disposition stay Committed; the Plan and chunk are
                // never touched.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.SessionIssue.IsValid, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                // A Published artifact hands the same Service and the same
                // Worker to the Capture Index phase: nothing is released and
                // the Worker is still alive.
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureIndexCommit));
            }
        }

        [Test]
        public void CollectArtifactPublication_Failed_AdvancesDispositionOnly()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationOperation operation = PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Failed;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the failed artifact");

                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // Registry stays Committed; only the disposition advances.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                // The retained result and its binding stay intact after the
                // Failed reflection.
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.IsFailed, Is.True);

                // Re-call returns the same retained result without re-collecting,
                // re-disposing, or re-transitioning.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult again), Is.True);
                Assert.That(again.Status, Is.EqualTo(NvencRunArtifactPublicationStatus.Failed));
                Assert.That(ReferenceEquals(again.Operation, operation), Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
            }
        }

        [Test]
        public void CollectArtifactPublication_PollBeforeStop_RetrySucceeds()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);

                h.Publisher.Entered = entered;
                h.Publisher.Release = release;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "publisher did not enter");

                // A poll while the Worker is still executing must not collect and
                // must not clear the slot.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.False);

                release.Set();
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");

                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsPublished, Is.True);

            }
        }

        [Test]
        public void CollectArtifactPublication_ExternalPoisonFirst_NoReflection()
        {
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");

                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);

                // The uncollected result is never reflected.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
            }
        }

        [Test]
        public void CollectArtifactPublication_CorruptPublisherResult_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);

                // A default (None) attempt result is corrupt: the Service Worker
                // rejects it and poisons the process.
                h.Publisher.UseOverride = true;
                h.Publisher.OverrideResult = default;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForServiceStop(h, "publication worker did not stop after the corrupt artifact result");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out _), Is.True);
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
            }
        }

        [Test]
        public void ArtifactPublicationOperation_TwoReadonlyFields_SealedInternal()
        {
            Type type = typeof(NvencRunArtifactPublicationOperation);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));

            // Verify by field-type set, never by reflection return order or
            // private field names: a harmless rename must not break this test.
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(NvencCaptureRunCoordinator),
                    typeof(NvencRunPublicationPlanCommitExecutionResult),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void ArtifactPublicationOperation_Source_NoConcreteIoThreadingNativeDependency()
        {
            string source = File.ReadAllText(
                Path.Combine(RuntimeDirectory(), "NvencRunArtifactPublicationOperation.cs"));

            // Only concrete dependency indicators; harmless identifiers and
            // comments are intentionally not scanned.
            string[] forbidden =
            {
                "System.IO", "System.Threading", "DllImport",
                "Microsoft.Win32.SafeHandles", "System.Security.Cryptography",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "operation source must not depend on: " + word);
            }
        }

        // ---- Capture index commit submission and reflection ----

        /// <summary>
        /// Drives the Run to a Published, collected artifact publication and
        /// mints the capture index commit operation. The same Service and the
        /// same Worker stay alive throughout.
        /// </summary>
        private static NvencRunCaptureIndexCommitOperation PrepareCaptureIndexSubmission(Harness h)
        {
            PrepareArtifactSubmission(h);
            h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
            Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
            WaitForArtifactTerminal(h, "publication worker did not reach the artifact terminal");
            Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);
            Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                out NvencRunCaptureIndexCommitOperation operation), Is.True);
            return operation;
        }

        private static void WaitForCaptureIndexTerminal(Harness h, string message)
        {
            SpinWait.SpinUntil(
                () => h.Service.State == NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                WatchdogTimeoutMs);
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.CaptureIndexCommitCompleted), message);

            if (h.IndexCommitter.Status != NvencRunCaptureIndexCommitStatus.Committed)
            {
                WaitForServiceStop(h, message);
            }
        }

        [Test]
        public void SubmitCaptureIndexCommit_Committed_ForwardsExactOperationOnce()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexSubmission(h);

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsCommitted, Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.Receipt.IsIssuedFor(h.IndexCommitter, operation), Is.True);
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void SubmitCaptureIndexCommit_BeforePrepare_ArtifactUncollected_Double_Poisoned_GateBusy_NoCommitterContact()
        {
            // No retained capture index operation: refused before the committer
            // is contacted.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the artifact terminal");
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.False);
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));
            }

            // Artifact publication not yet collected: refused.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the artifact terminal");

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.False);
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));
            }

            // Double submission: the retained operation is handed over exactly
            // once.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.False);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }

            // Poisoned: the process-state gate refuses before any Service
            // contact.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.False);
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));
            }

            // Gate contention: a background holder keeps the shared gate busy.
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginSubmitStep())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndSubmitStep();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.False);
                    Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CollectCaptureIndexCommit_Committed_KeepsCommittedReceiptAndService()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexSubmission(h);
                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsCommitted, Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(h.IndexCommitter, operation), Is.True);

                // Registry and disposition stay Committed, and the same Service
                // and Worker are kept for the later CaptureComplete phase.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureComplete));
                Assert.That(h.SessionIssue.IsValid, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                // Re-collection returns the same retained result without
                // re-collecting or re-transitioning.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult again), Is.True);
                Assert.That(ReferenceEquals(again.Operation, operation), Is.True);
                Assert.That(ReferenceEquals(again.Receipt, result.Receipt), Is.True);
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureComplete));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CollectCaptureIndexCommit_Failed_AdvancesDispositionAndReleasesService()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexSubmission(h);
                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Failed;

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // Registry, Plan, and chunk stay unchanged; only the
                // disposition advances, and the Service is released.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                // The issued result stays valid after the Recovery reflection.
                Assert.That(result.IsValid, Is.True);
                Assert.That(operation.IsBindingIntact, Is.True);

                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult again), Is.True);
                Assert.That(ReferenceEquals(again.Operation, operation), Is.True);
                Assert.That(again.Status, Is.EqualTo(NvencRunCaptureIndexCommitStatus.Failed));
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
            }
        }

        [Test]
        public void CollectCaptureIndexCommit_FailedPollBeforeStop_RetrySucceeds()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);

                h.IndexCommitter.Entered = entered;
                h.IndexCommitter.Release = release;
                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Failed;

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                // A poll while the Worker is still executing must not collect
                // and must not clear the slot.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(out _), Is.False);

                release.Set();
                WaitForServiceStop(h, "publication worker did not stop after the failed capture index commit");

                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);

            }
        }

        [Test]
        public void CollectCaptureIndexCommit_ExternalPoisonFirst_NoReflection()
        {
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);
                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);

                // The uncollected result is never reflected.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
            }
        }

        [Test]
        public void CollectCaptureIndexCommit_CorruptCommitterResult_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);

                // A default (None) attempt result is corrupt: the Service
                // Worker rejects it and poisons the process.
                h.IndexCommitter.UseOverride = true;
                h.IndexCommitter.OverrideResult = default;

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForServiceStop(h, "publication worker did not stop after the corrupt capture index result");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out _), Is.True);
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
            }
        }

        [Test]
        public void CaptureIndexCommit_ThreePhasesShareOneServiceAndWorker()
        {
            using (Harness h = Harness.Create())
            {
                Thread workerBefore = (Thread)GetPrivateField(h.Service, "_workerThread");

                PrepareCaptureIndexSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(out _), Is.True);

                Thread workerAfter = (Thread)GetPrivateField(h.Service, "_workerThread");
                Assert.That(ReferenceEquals(workerBefore, workerAfter), Is.True);
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(1));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(1));
            }
        }

        private static object GetPrivateField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        [Test]
        public void PrepareCaptureIndexCommit_AfterFailedCommitCollected_RefusesWithoutPoison()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexSubmission(h);
                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Failed;

                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                int committerCalls = h.IndexCommitter.CallCount;

                // The operation stays retained while its Committed-only
                // validity is false, which is the normal Recovery terminal and
                // not corruption: refuse with no change and no exception.
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsBindingIntact, Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation again), Is.False);
                Assert.That(again, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(committerCalls));

                // The retained result is still collectible and unchanged.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult retained), Is.True);
                Assert.That(ReferenceEquals(retained.Operation, operation), Is.True);
                Assert.That(retained.Status, Is.EqualTo(NvencRunCaptureIndexCommitStatus.Failed));
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_CommittedWithBrokenRetainedOperation_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexSubmission(h);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                // Releasing the Session Ownership Lease breaks the retained
                // graph while the published disposition is still Committed.
                // That is corruption, so the normal-terminal refusal above must
                // not swallow it.
                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsBindingIntact, Is.False);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareCaptureIndexCommit(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        // ---- CaptureComplete submission and reflection ----

        /// <summary>
        /// Drives the Run to a Committed, collected capture index commit and
        /// mints the CaptureComplete operation.
        /// </summary>
        private static NvencRunCaptureCompleteOperation PrepareCaptureCompleteSubmission(Harness h)
        {
            CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);
            Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                out NvencRunCaptureCompleteOperation operation), Is.True);
            return operation;
        }

        private static void WaitForCaptureCompleteTerminal(Harness h, string message)
        {
            // CaptureComplete is the final phase: the Worker always stops.
            WaitForServiceStop(h, message);
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteCompleted), message);
        }

        [Test]
        public void SubmitCaptureComplete_BeforePrepare_IndexUncollected_Double_Poisoned_GateBusy_NoCompleterContact()
        {
            // No retained CaptureComplete operation: refused before the
            // completer is contacted.
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.False);
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
            }

            // Capture index commit not yet collected: refused.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.False);
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
            }

            // Double submission: the retained operation is handed over exactly
            // once.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.False);
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));
            }

            // Poisoned: the process-state gate refuses before any Service
            // contact.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.False);
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
            }

            // Gate contention: a background holder keeps the shared gate busy.
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginSubmitStep())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndSubmitStep();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.False);
                    Assert.That(h.RunCompleter.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CollectCaptureComplete_Completed_AdvancesDispositionAndReleasesService()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureCompleteSubmission(h);
                h.RunCompleter.Status = NvencRunCaptureCompleteStatus.Completed;

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");

                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.True);
                Assert.That(result.IsCompleted, Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(h.RunCompleter, operation), Is.True);

                // The Registry stays Committed, the disposition advances to the
                // terminal CaptureComplete, and the Service is released.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                // Re-collection returns the same retained result without
                // re-collecting, re-disposing, or re-transitioning.
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult again), Is.True);
                Assert.That(ReferenceEquals(again.Operation, operation), Is.True);
                Assert.That(ReferenceEquals(again.Receipt, result.Receipt), Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CollectCaptureComplete_Failed_AdvancesToRecoveryAndReleasesService()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteOperation operation = PrepareCaptureCompleteSubmission(h);
                h.RunCompleter.Status = NvencRunCaptureCompleteStatus.Failed;

                CapturePublicationPlan plan = operation.Plan;

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");

                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.True);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // Registry, Plan, and chunk stay unchanged; only the
                // disposition advances, and the Service is released.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(ReferenceEquals(operation.Plan, plan), Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult again), Is.True);
                Assert.That(ReferenceEquals(again.Operation, operation), Is.True);
                Assert.That(again.Status, Is.EqualTo(NvencRunCaptureCompleteStatus.Failed));
            }
        }

        [Test]
        public void CollectCaptureComplete_PollBeforeStop_RetrySucceeds()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);

                h.RunCompleter.Entered = entered;
                h.RunCompleter.Release = release;

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "completer did not enter");

                // A poll while the Worker is still executing must not collect
                // and must not clear the slot.
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(out _), Is.False);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                release.Set();
                WaitForServiceStop(h, "publication worker did not stop after CaptureComplete");

                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.True);
                Assert.That(result.IsCompleted, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));

            }
        }

        [Test]
        public void CollectCaptureComplete_ExternalPoisonFirst_NoReflection()
        {
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");

                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);

                // The uncollected result is never reflected.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
            }
        }

        [Test]
        public void CollectCaptureComplete_CorruptCompleterResult_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);

                // A default (None) attempt result is corrupt: the Service
                // Worker rejects it and poisons the process.
                h.RunCompleter.UseOverride = true;
                h.RunCompleter.OverrideResult = default;

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForServiceStop(h, "publication worker did not stop after the corrupt CaptureComplete result");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Service.TryGetFailure(out _), Is.True);
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
            }
        }

        [Test]
        public void PrepareCaptureComplete_AfterReflectedTerminal_RefusesWithoutPoison()
        {
            foreach (NvencRunCaptureCompleteStatus status in new[]
            {
                NvencRunCaptureCompleteStatus.Completed,
                NvencRunCaptureCompleteStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteOperation operation = PrepareCaptureCompleteSubmission(h);
                    h.RunCompleter.Status = status;

                    Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                    WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");
                    Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                        out NvencRunCaptureCompleteAttemptResult result), Is.True);

                    NvencRunEvidenceDisposition expected =
                        status == NvencRunCaptureCompleteStatus.Completed
                            ? NvencRunEvidenceDisposition.CaptureComplete
                            : NvencRunEvidenceDisposition.PublicationRecoveryRequired;
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(expected));

                    // Both reflected terminals keep the operation retained while
                    // its admission validity is false by design: a normal
                    // terminal, not corruption.
                    Assert.That(operation.IsValid, Is.False);
                    Assert.That(operation.IsBindingIntact, Is.True);
                    Assert.That(result.IsValid, Is.True);

                    Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                        out NvencRunCaptureCompleteOperation again), Is.False);
                    Assert.That(again, Is.Null);
                    Assert.That(h.State.IsPoisoned, Is.False);
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(expected));
                    Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                }
            }
        }

        [Test]
        public void CollectCaptureIndexCommit_AfterExternalPoison_KeepsTheCollectedResultValid()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitAttemptResult collected =
                    CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);
                Assert.That(collected.IsCommitted, Is.True);
                NvencRunCaptureIndexCommitOperation operation = collected.Operation;

                Assert.That(h.State.TryPoison(), Is.True);

                // A Poison revokes admission to start later capture index work,
                // so the operation is no longer valid to run and the shared
                // process-state gate refuses the entry point outright. It is
                // not a break in the already-issued graph, though: the
                // collected result and its receipt stay verifiable, so nothing
                // treats the Poison as retained-state corruption.
                Assert.That(operation.IsValid, Is.False);

                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(collected.IsValid, Is.True);
                Assert.That(collected.IsCommitted, Is.True);
                Assert.That(collected.Receipt.IsValid, Is.True);
                Assert.That(collected.Receipt.IsIssuedFor(h.IndexCommitter, operation), Is.True);

                // The gate refuses while poisoned, and it refuses plainly:
                // no corruption failure is raised for the retained result.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult again), Is.False);
                Assert.That(again.IsNone, Is.True);
            }
        }

        [Test]
        public void CollectCaptureIndexCommit_UncollectedAtPoison_StaysUnreflected()
        {
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);
                h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.State.TryPoison(), Is.True);

                // Never collected before the Poison: the result stays
                // unreflected, exactly as before.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult result), Is.False);
                Assert.That(result.IsNone, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
            }
        }

        [Test]
        public void PrepareEarlierPhases_AfterCaptureCompleteTerminal_RefuseWithoutPoison()
        {
            foreach (NvencRunCaptureCompleteStatus status in new[]
            {
                NvencRunCaptureCompleteStatus.Completed,
                NvencRunCaptureCompleteStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteOperation captureComplete =
                        PrepareCaptureCompleteSubmission(h);
                    h.RunCompleter.Status = status;

                    Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                    WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");
                    Assert.That(h.RunCoordinator.TryCollectCaptureComplete(out _), Is.True);

                    NvencRunEvidenceDisposition expected =
                        status == NvencRunCaptureCompleteStatus.Completed
                            ? NvencRunEvidenceDisposition.CaptureComplete
                            : NvencRunEvidenceDisposition.PublicationRecoveryRequired;
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(expected));

                    string message = "after a " + status + " CaptureComplete";

                    // Every earlier phase keeps its operation retained across
                    // the Run's terminal, where the operation's Committed-only
                    // validity is false by design. Re-preparing any of them is
                    // an ordinary refusal, never corruption.
                    Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                        out NvencRunArtifactPublicationOperation artifact), Is.False, message);
                    Assert.That(artifact, Is.Null, message);
                    Assert.That(h.State.IsPoisoned, Is.False, message);

                    Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                        out NvencRunCaptureIndexCommitOperation index), Is.False, message);
                    Assert.That(index, Is.Null, message);
                    Assert.That(h.State.IsPoisoned, Is.False, message);

                    Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                        out NvencRunCaptureCompleteOperation again), Is.False, message);
                    Assert.That(again, Is.Null, message);
                    Assert.That(h.State.IsPoisoned, Is.False, message);

                    // Nothing was disturbed by the refusals.
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(expected), message);
                    Assert.That(h.Slot.State,
                        Is.EqualTo(NvencRunLocalRegistrySlotState.Committed), message);
                    Assert.That(captureComplete.IsBindingIntact, Is.True, message);
                }
            }
        }

        // ---- CaptureComplete cleanup preparation ----

        /// <summary>
        /// Drives the Run to a collected CaptureComplete of the given status.
        /// </summary>
        private static NvencRunCaptureCompleteAttemptResult CompleteCaptureAndCollect(
            Harness h,
            NvencRunCaptureCompleteStatus status)
        {
            PrepareCaptureCompleteSubmission(h);
            h.RunCompleter.Status = status;
            Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
            WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");
            Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                out NvencRunCaptureCompleteAttemptResult result), Is.True);
            return result;
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_CompletedCollected_ForwardsExactReferences()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteAttemptResult captureComplete =
                    CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);
                Assert.That(captureComplete.IsCompleted, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.True);

                Assert.That(operation, Is.Not.Null);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.True);
                Assert.That(operation.IsIssuedFor(null), Is.False);

                // Every forwarded value is the existing graph's exact
                // reference.
                Assert.That(ReferenceEquals(
                    operation.CaptureCompleteReceipt, captureComplete.Receipt), Is.True);
                Assert.That(ReferenceEquals(
                    operation.CaptureCompleteOperation, captureComplete.Operation), Is.True);
                Assert.That(ReferenceEquals(
                    operation.CaptureIndexCommitReceipt,
                    captureComplete.Operation.CaptureIndexCommitReceipt), Is.True);
                Assert.That(ReferenceEquals(
                    operation.ArtifactPublicationReceipt,
                    captureComplete.Operation.ArtifactPublicationReceipt), Is.True);
                Assert.That(ReferenceEquals(operation.Plan, captureComplete.Plan), Is.True);
                Assert.That(ReferenceEquals(operation.RootLayout, captureComplete.RootLayout), Is.True);
                Assert.That(operation.TestRunId, Is.EqualTo(captureComplete.TestRunId));
                Assert.That(operation.TestRunId, Is.EqualTo(h.Context.TestRunId));
                Assert.That(ReferenceEquals(
                    operation.RunInitializationId, captureComplete.RunInitializationId), Is.True);

                // The artifact publication receipt is still the one publication
                // issued, never re-issued for cleanup.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult publication), Is.True);
                Assert.That(ReferenceEquals(
                    operation.ArtifactPublicationReceipt, publication.Receipt), Is.True);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_Idempotent_ReturnsSameReference()
        {
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation first), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation again), Is.True);

                Assert.That(ReferenceEquals(first, again), Is.True);
                Assert.That(first.IsValid, Is.True);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_BeforeSubmitOrCollect_ReturnsFalse()
        {
            // Prepared but not submitted.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Submitted with a terminal published, but not collected: the
            // Service is stopped yet still unreleased.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");

                Assert.That(h.Service.IsStopped, Is.True);
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);

                // Collecting the Completed result is what admits the
                // preparation.
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(out _), Is.True);
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.True);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_MidExecutionBeforeServiceStop_ReturnsFalse()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareCaptureCompleteSubmission(h);

                h.RunCompleter.Entered = entered;
                h.RunCompleter.Release = release;

                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "completer did not enter");

                // The Worker is still running and nothing is released.
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);

                release.Set();
                WaitForCaptureCompleteTerminal(h, "publication worker did not reach the CaptureComplete terminal");
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(out _), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.True);

            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_FailedOrEarlierDisposition_ReturnsFalseWithoutPoison()
        {
            // A Failed CaptureComplete publishes PublicationRecoveryRequired.
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteAttemptResult failed =
                    CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Failed);
                Assert.That(failed.IsFailed, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }

            // A Failed capture index commit also stops short of CaptureComplete.
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Failed);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // A still-Committed Run has not reached the successful terminal.
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // A Finalized Run, long before any publication.
            using (Harness h = Harness.Create())
            {
                FinalizeOnly(h);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_ExternalPoisonFirst_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.False);
                Assert.That(operation, Is.Null);

                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_GateContention_ReturnsFalseNoChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                        out NvencRunCaptureCompleteCleanupOperation contended), Is.False);
                    Assert.That(contended, Is.Null);
                    Assert.That(h.RunCoordinator.Disposition,
                        Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                // The refusal left nothing behind.
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation prepared), Is.True);
                Assert.That(prepared, Is.Not.Null);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_BrokenPublishedCorrelation_Poisons()
        {
            // Broken before any cleanup operation is minted.
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

                // Releasing the Session Ownership Lease breaks the published
                // graph through the ordinary ownership API.
                h.SessionIssue.OwnershipLease.Dispose();

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }

            // Broken after the cleanup operation was minted.
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation prepared), Is.True);
                Assert.That(prepared.IsValid, Is.True);

                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(prepared.IsValid, Is.False);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_ChangesNoRunOrServiceState()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteAttemptResult captureComplete =
                    CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

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

                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.True);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));
                Assert.That(h.State.IsPoisoned, Is.False);

                // The retained CaptureComplete result and receipt are reused,
                // never replaced.
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult again), Is.True);
                Assert.That(ReferenceEquals(again.Operation, captureComplete.Operation), Is.True);
                Assert.That(ReferenceEquals(again.Receipt, captureComplete.Receipt), Is.True);
                Assert.That(ReferenceEquals(
                    operation.CaptureCompleteReceipt, again.Receipt), Is.True);
            }
        }

        [Test]
        public void CaptureCompleteCleanupOperation_TwoReadonlyFields_SealedInternal()
        {
            Type type = typeof(NvencRunCaptureCompleteCleanupOperation);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));

            // Verify by field-type set, never by reflection return order or
            // private field names: a harmless rename must not break this test.
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(NvencCaptureRunCoordinator),
                    typeof(NvencRunCaptureCompleteReceipt),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        // ---- CaptureComplete cleanup result reflection ----

        [Test]
        public void ReflectCaptureCompleteCleanup_Cleaned_KeepsCaptureCompleteDisposition()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult result = ExecuteCleanup(
                    h, operation, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.True);

                // A successful cleanup leaves the Run on its successful
                // terminal: no new disposition is introduced.
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.State.IsPoisoned, Is.False);

                // The issued result and receipt stay correlated afterwards.
                Assert.That(result.IsCleaned, Is.True);
                Assert.That(result.IsValid, Is.True);
                Assert.That(ReferenceEquals(result.Cleaner, h.CleanupCleaner), Is.True);
                Assert.That(result.Receipt.IsIssuedFor(h.CleanupCleaner, operation), Is.True);
                Assert.That(operation.IsBindingIntact, Is.True);

                // The reflection is not a cleanup: the one call the Execution
                // Coordinator made is still the only one.
                Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_Failed_PublishesPublicationRecoveryRequired()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult result = ExecuteCleanup(
                    h, operation, NvencRunCaptureCompleteCleanupStatus.Failed);

                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.True);

                // The failure hands the Run to Recovery through the existing
                // disposition, published last.
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.State.IsPoisoned, Is.False);

                // The issued Failed result survives the disposition it caused,
                // and admission validity is correctly gone.
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Cleaner, h.CleanupCleaner), Is.True);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(operation.IsValid, Is.False);

                Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_ReflectedResultAndReceipt_SurviveLaterPoison()
        {
            foreach (NvencRunCaptureCompleteCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteCleanupStatus.Cleaned,
                NvencRunCaptureCompleteCleanupStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                    NvencRunCaptureCompleteCleanupAttemptResult result = ExecuteCleanup(h, operation, status);

                    Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.True);

                    // A Poison closes every gate but never rewrites history: the
                    // already issued result and receipt stay correlated, while
                    // admission validity is gone.
                    Assert.That(h.State.TryPoison(), Is.True);

                    Assert.That(result.IsValid, Is.True);
                    Assert.That(result.IsIssuedFor(h.CleanupCleaner, operation), Is.True);
                    Assert.That(operation.IsBindingIntact, Is.True);
                    Assert.That(operation.IsValid, Is.False);

                    if (status == NvencRunCaptureCompleteCleanupStatus.Cleaned)
                    {
                        Assert.That(result.Receipt.IsValid, Is.True);
                        Assert.That(result.Receipt.IsIssuedFor(h.CleanupCleaner, operation), Is.True);
                    }
                }
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_NotPreparedOrEarlierDisposition_ReturnsFalse()
        {
            // Never prepared: a normal not-ready shape, not corruption. The
            // result is executed against another Run's prepared operation so
            // this Run has nothing retained at all.
            using (Harness source = Harness.Create())
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupAttemptResult foreign = ExecuteCleanup(
                    source,
                    PrepareCleanupOperation(source),
                    NvencRunCaptureCompleteCleanupStatus.Cleaned);

                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(foreign), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(0));

                // The refusal left nothing behind: this Run's own cleanup can
                // still be prepared, executed, and reflected.
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation operation), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(
                    ExecuteCleanup(h, operation, NvencRunCaptureCompleteCleanupStatus.Cleaned)), Is.True);
            }

            // A Failed CaptureComplete never prepares a cleanup, so there is
            // nothing to reflect either.
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Failed);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(default), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_ForeignOperation_Poisons()
        {
            using (Harness source = Harness.Create())
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupAttemptResult foreign = ExecuteCleanup(
                    source,
                    PrepareCleanupOperation(source),
                    NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(PrepareCleanupOperation(h), Is.Not.Null);

                // Another Run's cleanup result reaching a normally prepared Run
                // is corruption, not a not-ready shape.
                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCaptureCompleteCleanup(foreign));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(source.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_ForeignCleanupExecutionCoordinator_Poisons()
        {
            foreach (NvencRunCaptureCompleteCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteCleanupStatus.Cleaned,
                NvencRunCaptureCompleteCleanupStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);

                    // A cleaner that is not this Run's configured one, driven
                    // through its own Execution Coordinator, produces a fully
                    // self-consistent result for the exact operation. It is
                    // still evidence of a cleanup this Run never ran, so it must
                    // be refused as corruption rather than accepted.
                    FakeCleaner foreignCleaner = new FakeCleaner { Status = status };
                    NvencRunCaptureCompleteCleanupAttemptResult foreign =
                        new NvencRunCaptureCompleteCleanupExecutionCoordinator(foreignCleaner)
                            .Execute(operation);

                    Assert.That(foreign.IsValid, Is.True);
                    Assert.That(foreign.Status, Is.EqualTo(status));
                    Assert.That(ReferenceEquals(foreign.Operation, operation), Is.True);
                    Assert.That(foreignCleaner.CallCount, Is.EqualTo(1));
                    Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(0));

                    Assert.Throws<InvalidOperationException>(
                        () => h.RunCoordinator.TryReflectCaptureCompleteCleanup(foreign));
                    Assert.That(h.State.IsPoisoned, Is.True);
                    Assert.That(h.RunCoordinator.Disposition,
                        Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                }
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_DefaultOrBrokenShape_Poisons()
        {
            AssertReflectionPoisons(
                (h, operation) => default,
                "a default result must poison");

            AssertReflectionPoisons(
                (h, operation) => MakeCleanupAttempt(
                    h.CleanupCleaner, operation, null, NvencRunCaptureCompleteCleanupStatus.Cleaned),
                "a Cleaned result without a receipt must poison");

            AssertReflectionPoisons(
                (h, operation) => MakeCleanupAttempt(
                    h.CleanupCleaner,
                    operation,
                    NvencRunCaptureCompleteCleanupReceipt.Create(h.CleanupCleaner, operation),
                    NvencRunCaptureCompleteCleanupStatus.Failed),
                "a Failed result carrying a receipt must poison");

            AssertReflectionPoisons(
                (h, operation) => MakeCleanupAttempt(
                    h.CleanupCleaner,
                    operation,
                    NvencRunCaptureCompleteCleanupReceipt.Create(h.CleanupCleaner, operation),
                    NvencRunCaptureCompleteCleanupStatus.None),
                "a receipt-carrying None result must poison");

            // The named cleaner and the receipt's cleaner must be the same
            // instance: a receipt issued to another cleaner is not this
            // result's evidence.
            AssertReflectionPoisons(
                (h, operation) => MakeCleanupAttempt(
                    h.CleanupCleaner,
                    operation,
                    NvencRunCaptureCompleteCleanupReceipt.Create(new FakeCleaner(), operation),
                    NvencRunCaptureCompleteCleanupStatus.Cleaned),
                "a Cleaned receipt issued to a foreign cleaner must poison");

            AssertReflectionPoisons(
                (h, operation) => MakeCleanupAttempt(
                    null,
                    operation,
                    NvencRunCaptureCompleteCleanupReceipt.Create(h.CleanupCleaner, operation),
                    NvencRunCaptureCompleteCleanupStatus.Cleaned),
                "a Cleaned result without a cleaner must poison");
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_ExternalPoisonFirst_ReturnsFalseNoChange()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult result = ExecuteCleanup(
                    h, operation, NvencRunCaptureCompleteCleanupStatus.Failed);

                Assert.That(h.State.TryPoison(), Is.True);

                // Poison outranks the retained shape: a normal refusal with no
                // change, never a corruption report.
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_GateContention_ReturnsFalseNoChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult result = ExecuteCleanup(
                    h, operation, NvencRunCaptureCompleteCleanupStatus.Failed);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.False);
                    Assert.That(h.RunCoordinator.Disposition,
                        Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                // The refusal left nothing behind.
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_Twice_SecondReturnsFalseWithNoChange()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult cleaned = ExecuteCleanup(
                    h, operation, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(cleaned), Is.True);

                // One cleanup outcome per Run: neither the same result nor an
                // opposite one from a second execution is reflected again.
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(cleaned), Is.False);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(
                    ExecuteCleanup(h, operation, NvencRunCaptureCompleteCleanupStatus.Cleaned)), Is.False);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(default), Is.False);

                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_AfterReflection_ReturnsFalseWithoutPoison()
        {
            foreach (NvencRunCaptureCompleteCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteCleanupStatus.Cleaned,
                NvencRunCaptureCompleteCleanupStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                    Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(
                        ExecuteCleanup(h, operation, status)), Is.True);

                    // One Run prepares one cleanup: after either outcome a
                    // re-prepare is an ordinary refusal, never corruption and
                    // never a second operation.
                    Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                        out NvencRunCaptureCompleteCleanupOperation again), Is.False);
                    Assert.That(again, Is.Null);
                    Assert.That(h.State.IsPoisoned, Is.False);

                    // Repeating it stays a refusal.
                    Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _), Is.False);
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
            }
        }

        [Test]
        public void PrepareCaptureCompleteCleanup_AfterReflectionWithBrokenBinding_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(
                    ExecuteCleanup(h, operation, NvencRunCaptureCompleteCleanupStatus.Cleaned)), Is.True);

                // Releasing the Session Ownership Lease breaks the published
                // graph through the ordinary ownership API, so the reflected
                // history no longer correlates.
                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(operation.IsBindingIntact, Is.False);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareCaptureCompleteCleanup(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void ReflectCaptureCompleteCleanup_ChangesNoRunRegistryContextServiceOrLease()
        {
            foreach (NvencRunCaptureCompleteCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteCleanupStatus.Cleaned,
                NvencRunCaptureCompleteCleanupStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteAttemptResult captureComplete =
                        CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);
                    Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                        out NvencRunCaptureCompleteCleanupOperation operation), Is.True);
                    NvencRunCaptureCompleteCleanupAttemptResult result =
                        ExecuteCleanup(h, operation, status);

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
                    CapturePublicationPlan plan = operation.Plan;

                    Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.True);

                    // Only the disposition may move, and only on a failure.
                    Assert.That(h.Slot.State, Is.EqualTo(slotState));
                    Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                    Assert.That(h.Context.State, Is.EqualTo(contextState));
                    Assert.That(h.Service.State, Is.EqualTo(serviceState));
                    Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                    Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                    Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                    Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                    Assert.That(h.State.IsPoisoned, Is.False);

                    // The cleanup is not re-run and no earlier collaborator is
                    // called again: nothing is retried or rolled back.
                    Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(1));
                    Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                    Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                    Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                    Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));

                    // The retained plan and the CaptureComplete result and
                    // receipt are the same references as before.
                    Assert.That(ReferenceEquals(operation.Plan, plan), Is.True);
                    Assert.That(ReferenceEquals(
                        operation.CaptureCompleteReceipt, captureComplete.Receipt), Is.True);
                    Assert.That(ReferenceEquals(
                        operation.CaptureCompleteOperation, captureComplete.Operation), Is.True);
                    Assert.That(captureComplete.IsValid, Is.True);
                }
            }
        }

        // ---- CaptureComplete cleanup reflection helpers ----

        private static NvencRunCaptureCompleteCleanupOperation PrepareCleanupOperation(Harness h)
        {
            CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);
            Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                out NvencRunCaptureCompleteCleanupOperation operation), Is.True);
            return operation;
        }

        /// <summary>
        /// Runs the Run's own configured cleanup Execution Coordinator exactly
        /// once and returns its result, so every reflected result is evidence
        /// of a cleanup this Run actually performed rather than a hand-built
        /// value.
        /// </summary>
        private static NvencRunCaptureCompleteCleanupAttemptResult ExecuteCleanup(
            Harness h,
            NvencRunCaptureCompleteCleanupOperation operation,
            NvencRunCaptureCompleteCleanupStatus status)
        {
            h.CleanupCleaner.Status = status;
            int before = h.CleanupCleaner.CallCount;

            NvencRunCaptureCompleteCleanupAttemptResult result = h.CleanupExecution.Execute(operation);

            Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(before + 1),
                "the Execution Coordinator must call the cleaner exactly once.");
            Assert.That(result.Status, Is.EqualTo(status));
            Assert.That(ReferenceEquals(result.Cleaner, h.CleanupCleaner), Is.True);
            return result;
        }

        /// <summary>
        /// Reflects the forged result into a Run that is otherwise normally
        /// prepared, and whose own configured cleaner is the one the forgery
        /// names, so the corrupt part under test is the only thing that can
        /// reject it. It requires the corruption report rather than a refusal.
        /// </summary>
        private static void AssertReflectionPoisons(
            Func<Harness, NvencRunCaptureCompleteCleanupOperation,
                NvencRunCaptureCompleteCleanupAttemptResult> forge,
            string message)
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult forged = forge(h, operation);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReflectCaptureCompleteCleanup(forged), message);
                Assert.That(h.State.IsPoisoned, Is.True, message);
                Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(0), message);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete), message);
            }
        }

        private static NvencRunCaptureCompleteCleanupAttemptResult MakeCleanupAttempt(
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

        private sealed class FakeCleaner : INvencRunCaptureCompleteCleaner
        {
            private int _callCount;

            internal NvencRunCaptureCompleteCleanupStatus Status =
                NvencRunCaptureCompleteCleanupStatus.Cleaned;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteCleanupAttemptResult Clean(
                NvencRunCaptureCompleteCleanupOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Status == NvencRunCaptureCompleteCleanupStatus.Failed)
                {
                    return NvencRunCaptureCompleteCleanupAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        // ---- Session Ownership Lease release preparation ----

        [Test]
        public void PrepareSessionOwnershipRelease_AfterCleanedReflection_ForwardsExactReferences()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation cleanup = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult cleanupResult = ExecuteCleanup(
                    h, cleanup, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(cleanupResult), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.True);

                Assert.That(operation, Is.Not.Null);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(operation.CanRelease, Is.True);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.True);
                Assert.That(operation.IsIssuedFor(null), Is.False);

                // Every forwarded value is the existing graph's exact
                // reference; nothing is copied and no lock is released.
                Assert.That(ReferenceEquals(operation.CleanupOperation, cleanup), Is.True);
                Assert.That(ReferenceEquals(
                    operation.OwnershipLease, h.SessionIssue.OwnershipLease), Is.True);
                Assert.That(ReferenceEquals(operation.RootLayout, cleanup.RootLayout), Is.True);
                Assert.That(operation.TestRunId, Is.EqualTo(cleanup.TestRunId));
                Assert.That(ReferenceEquals(
                    operation.RunInitializationId, cleanup.RunInitializationId), Is.True);

                NvencRunCaptureCompleteCleanupAttemptResult forwarded = operation.CleanupResult;
                Assert.That(forwarded.Status, Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Cleaned));
                Assert.That(ReferenceEquals(forwarded.Cleaner, h.CleanupCleaner), Is.True);
                Assert.That(ReferenceEquals(forwarded.Operation, cleanup), Is.True);
                Assert.That(ReferenceEquals(forwarded.Receipt, cleanupResult.Receipt), Is.True);

                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_AfterFailedReflection_StillPrepares()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation cleanup = PrepareCleanupOperation(h);
                NvencRunCaptureCompleteCleanupAttemptResult cleanupResult = ExecuteCleanup(
                    h, cleanup, NvencRunCaptureCompleteCleanupStatus.Failed);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(cleanupResult), Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                // A failed cleanup does not suppress the lock release.
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.True);

                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.CleanupResult.Status,
                    Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Failed));
                Assert.That(operation.CleanupResult.Receipt, Is.Null);
                Assert.That(ReferenceEquals(operation.CleanupOperation, cleanup), Is.True);
                Assert.That(ReferenceEquals(
                    operation.OwnershipLease, h.SessionIssue.OwnershipLease), Is.True);

                // The recovery disposition is kept, not rewritten.
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_Idempotent_ReturnsSameReference()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation first), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation second), Is.True);

                Assert.That(ReferenceEquals(first, second), Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_BeforeCleanupPrepareOrReflection_ReturnsFalse()
        {
            // The cleanup was never prepared.
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Completed);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Prepared but never reflected.
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupOperation cleanup = PrepareCleanupOperation(h);
                Assert.That(cleanup, Is.Not.Null);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);

                // Executing the cleanup without reflecting it is still not
                // enough: the outcome must be reflected first.
                ExecuteCleanup(h, cleanup, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // A Failed CaptureComplete never prepares a cleanup at all.
            using (Harness h = Harness.Create())
            {
                CompleteCaptureAndCollect(h, NvencRunCaptureCompleteStatus.Failed);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_BeforeServiceReleasedAndStopped_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                // The capture index phase leaves the Service running and
                // unreleased, so no cleanup exists and no release is prepared.
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Once the Service is released and stopped and the cleanup outcome
            // is reflected, the same entry succeeds.
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.Service.IsStopped, Is.True);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.True);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_ExternalPoisonFirst_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.True);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_GateContention_ReturnsFalseNoChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                        out NvencRunSessionOwnershipReleaseOperation contended), Is.False);
                    Assert.That(contended, Is.Null);
                    Assert.That(h.RunCoordinator.Disposition,
                        Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                // The refusal left nothing behind.
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation prepared), Is.True);
                Assert.That(prepared, Is.Not.Null);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_ChangesNoRunRegistryContextServiceOrLease()
        {
            foreach (NvencRunCaptureCompleteCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteCleanupStatus.Cleaned,
                NvencRunCaptureCompleteCleanupStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    NvencRunCaptureCompleteCleanupAttemptResult cleanupResult =
                        PrepareReflectedCleanup(h, status);

                    NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                    NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                    bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                    NvencRunChunkContextState contextState = h.Context.State;
                    NvencRunPublicationServiceState serviceState = h.Service.State;
                    bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                    bool serviceStopped = h.Service.IsStopped;
                    bool leaseCreated = h.SessionIssue.OwnershipLease.IsCreated;
                    bool leaseCanRelease = h.SessionIssue.OwnershipLease.CanRelease;
                    bool leaseReleaseComplete = h.SessionIssue.OwnershipLease.IsReleaseComplete;
                    int cleanerCalls = h.CleanupCleaner.CallCount;
                    int publisherCalls = h.Publisher.CallCount;
                    int committerCalls = h.Committer.CallCount;
                    int indexCommitterCalls = h.IndexCommitter.CallCount;
                    int completerCalls = h.RunCompleter.CallCount;

                    Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                        out NvencRunSessionOwnershipReleaseOperation operation), Is.True);

                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                    Assert.That(h.Slot.State, Is.EqualTo(slotState));
                    Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                    Assert.That(h.Context.State, Is.EqualTo(contextState));
                    Assert.That(h.Service.State, Is.EqualTo(serviceState));
                    Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                    Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                    Assert.That(h.State.IsPoisoned, Is.False);

                    // Nothing is released here: the lease is exactly as it was.
                    Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                    Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.EqualTo(leaseCanRelease));
                    Assert.That(
                        h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.EqualTo(leaseReleaseComplete));

                    // No collaborator is contacted again.
                    Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(cleanerCalls));
                    Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                    Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                    Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                    Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));

                    // The reflected cleanup result is reused, never replaced.
                    Assert.That(ReferenceEquals(
                        operation.CleanupOperation, cleanupResult.Operation), Is.True);
                    Assert.That(ReferenceEquals(
                        operation.CleanupResult.Receipt, cleanupResult.Receipt), Is.True);
                }
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_LeaseAlreadyReleased_ReturnsFalseWithoutPoison()
        {
            // Released through the ordinary ownership API before any release
            // operation was prepared: there is nothing left to release, which is
            // a normal shape and never corruption. The lock is not re-acquired.
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.False);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Released after the operation was prepared: the re-prepare is an
            // ordinary refusal, and the reference correlation survives while
            // only the release-state predicates change.
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation prepared), Is.True);

                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(prepared.IsBindingIntact, Is.True);
                Assert.That(prepared.CanRelease, Is.False);
                Assert.That(prepared.IsValid, Is.False);
                Assert.That(prepared.IsIssuedFor(h.RunCoordinator), Is.False);

                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation again), Is.False);
                Assert.That(again, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_Predicates_AreDistinct()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupAttemptResult cleanupResult =
                    PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation operation), Is.True);

                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(operation.CanRelease, Is.True);
                Assert.That(operation.IsValid, Is.True);

                // A Poison closes admission but never rewrites history: the
                // reference correlation and the lease's own releasability are
                // unaffected, so a later release attempt is not foreclosed by
                // this predicate.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.False);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(operation.CanRelease, Is.True);

                // The binding is reference correlation only: it does not read
                // the disposition, the process state, or the lease's release
                // state, and the forwarded graph stays readable.
                Assert.That(ReferenceEquals(
                    operation.CleanupOperation, cleanupResult.Operation), Is.True);
                Assert.That(ReferenceEquals(
                    operation.OwnershipLease, h.SessionIssue.OwnershipLease), Is.True);
            }
        }

        [Test]
        public void PrepareSessionOwnershipRelease_ForeignCleanupResultOrLease_NotCorrelated()
        {
            using (Harness source = Harness.Create())
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureCompleteCleanupAttemptResult foreignResult =
                    PrepareReflectedCleanup(source, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                NvencRunCaptureCompleteCleanupAttemptResult ownResult =
                    PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                // Another Run's reflected result, and another Run's lease, are
                // both refused by the binding and the admission predicates.
                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseBindingIntact(
                    foreignResult, h.SessionIssue.OwnershipLease), Is.False);
                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseCorrelated(
                    foreignResult, h.SessionIssue.OwnershipLease), Is.False);
                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseBindingIntact(
                    ownResult, source.SessionIssue.OwnershipLease), Is.False);
                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseCorrelated(
                    ownResult, source.SessionIssue.OwnershipLease), Is.False);

                // A default result is refused too, and none of this poisons.
                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseBindingIntact(
                    default, h.SessionIssue.OwnershipLease), Is.False);
                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseCorrelated(
                    ownResult, null), Is.False);

                Assert.That(h.RunCoordinator.IsSessionOwnershipReleaseBindingIntact(
                    ownResult, h.SessionIssue.OwnershipLease), Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(source.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void SessionOwnershipReleaseOperation_SealedInternal_ThreeReadonlyReferenceFields()
        {
            Type type = typeof(NvencRunSessionOwnershipReleaseOperation);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(3));

            // Verify by field-type set, never by reflection return order or
            // private field names: a harmless rename must not break this test.
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(NvencCaptureRunCoordinator),
                    typeof(NvencRunCaptureCompleteCleanupAttemptResult),
                    typeof(CaptureRunInitializationSessionOwnershipLease),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(
                type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsLiteral && !field.IsInitOnly),
                Is.Empty,
                "the operation must hold no mutable static state.");
        }

        [Test]
        public void PrepareSessionOwnershipRelease_AfterPartialReleaseFailure_ReturnsSameOperation()
        {
            // The Run's first lock handle fails its first release, so the
            // ordinary Dispose releases the second handle and then throws. The
            // lease is left no longer fully retained but still releasable, which
            // is exactly the state a retry exists for.
            using (Harness h = Harness.Create(throwingFirstRelease: true))
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation prepared), Is.True);

                Assert.Throws<AggregateException>(() => h.SessionIssue.OwnershipLease.Dispose());

                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.False);
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.True);
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.False);

                // Reference correlation and releasability survive; only
                // admission validity, which requires a fully retained lease, is
                // gone.
                Assert.That(prepared.IsBindingIntact, Is.True);
                Assert.That(prepared.CanRelease, Is.True);
                Assert.That(prepared.IsValid, Is.False);

                // A re-prepare hands back the same operation without poisoning:
                // the partial failure must not foreclose the retry.
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation again), Is.True);
                Assert.That(ReferenceEquals(again, prepared), Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);

                // The retry completes the release, and only then does the entry
                // refuse - still without poisoning.
                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.False);
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);

                Assert.That(prepared.IsBindingIntact, Is.True);
                Assert.That(prepared.CanRelease, Is.False);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation afterRelease), Is.False);
                Assert.That(afterRelease, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        // ---- Session Ownership Lease release execution ----

        [Test]
        public void ReleaseSessionOwnership_BeforePrepare_ReturnsFalse_ReleaserNotContacted()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                // Prepared cleanup but no release operation yet.
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.False);
                Assert.That(receipt, Is.Null);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_Prepared_ReleasesOnce_RetainsExactReceipt()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareReleaseOperation(h);

                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.True);

                Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(receipt.IsIssuedFor(h.Releaser, operation), Is.True);
                Assert.That(ReferenceEquals(receipt.Releaser, h.Releaser), Is.True);
                Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);

                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_Idempotent_ReturnsSameReceipt_NoSecondRelease()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReleaseOperation(h);

                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt first), Is.True);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt second), Is.True);

                Assert.That(ReferenceEquals(first, second), Is.True);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_ExternalPoisonFirst_ReturnsFalse_LeaseKept()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReleaseOperation(h);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.False);
                Assert.That(receipt, Is.Null);

                // The releaser is never contacted after a Poison, and the lock
                // is still held.
                Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.True);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_GateContention_ReturnsFalseNoContact_ThenSucceeds()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareReleaseOperation(h);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                        out NvencRunSessionOwnershipReleaseReceipt contended), Is.False);
                    Assert.That(contended, Is.Null);
                    Assert.That(h.Releaser.CallCount, Is.EqualTo(0));
                    Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.True);
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                // The refusal left nothing behind.
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ReleaseSessionOwnership_HoldingTheGate_DefersAConcurrentPoisonUntilTheAttemptEnds()
        {
            // Both events outlive the Harness: the release thread parks inside
            // the fake releaser, so they are released only after both threads
            // have been joined and the Harness has torn down.
            using (ManualResetEventSlim insideRelease = new ManualResetEventSlim(false))
            using (ManualResetEventSlim proceed = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareReleaseOperation(h);

                // The releaser parks inside the attempt, which runs inside the
                // resource-resolution gate.
                h.Releaser.Entered = insideRelease;
                h.Releaser.Proceed = proceed;

                bool poisoned = false;
                Thread poisoner = new Thread(() => poisoned = h.State.TryPoison())
                {
                    IsBackground = true,
                };

                NvencRunSessionOwnershipReleaseReceipt receipt = null;
                bool released = false;
                Thread releaser = new Thread(
                    () => released = h.RunCoordinator.TryReleaseSessionOwnership(out receipt))
                {
                    IsBackground = true,
                };

                bool poisonerStarted = false;
                bool releaserJoined = false;
                bool poisonerJoined = true;

                releaser.Start();
                try
                {
                    Assert.That(insideRelease.Wait(WatchdogTimeoutMs), Is.True,
                        "the releaser did not enter its attempt");

                    // While the attempt is parked, this test thread cannot take
                    // the shared resource-resolution gate. That is the same gate
                    // a Poison must acquire, so the attempt provably holds it -
                    // no timing assumption is involved.
                    bool tookGate = h.State.TryBeginResourceResolution();
                    if (tookGate)
                    {
                        h.State.EndResourceResolution();
                    }

                    Assert.That(tookGate, Is.False,
                        "the release attempt must hold the shared resource-resolution gate");

                    poisoner.Start();
                    poisonerStarted = true;

                    // The Poison cannot have taken effect: it must acquire the
                    // gate the parked attempt is still holding.
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    // Never leave the release thread parked, whatever failed
                    // above, and join both threads before the events go away.
                    proceed.Set();
                    releaserJoined = releaser.Join(WatchdogTimeoutMs);
                    if (poisonerStarted)
                    {
                        poisonerJoined = poisoner.Join(WatchdogTimeoutMs);
                    }
                }

                Assert.That(releaserJoined, Is.True, "the releaser did not finish");
                Assert.That(poisonerJoined, Is.True, "the poisoner did not exit");

                // The one attempt completed and was retained; the Poison took
                // effect only afterwards.
                Assert.That(released, Is.True);
                Assert.That(receipt, Is.Not.Null);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(poisoned, Is.True);
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_PartialFailure_PropagatesWithoutPoison_ThenRetrySucceeds()
        {
            using (Harness h = Harness.Create(throwingFirstRelease: true))
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareReleaseOperation(h);

                AggregateException failure = Assert.Throws<AggregateException>(
                    () => h.RunCoordinator.TryReleaseSessionOwnership(out _));
                Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
                Assert.That(failure.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());

                // The lease can still be released, so the failure is retryable:
                // no receipt, no latch, no Poison.
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(operation.CanRelease, Is.True);
                Assert.That(operation.IsBindingIntact, Is.True);
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.False);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1));

                // The same retained operation retries and succeeds; the handle
                // released by the first attempt is not touched again.
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.True);

                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsIssuedFor(h.Releaser, operation), Is.True);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(2));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_PoisonBeforeRetry_KeepsTheLeaseUnreleased()
        {
            using (Harness h = Harness.Create(throwingFirstRelease: true))
            {
                PrepareReleaseOperation(h);

                Assert.Throws<AggregateException>(
                    () => h.RunCoordinator.TryReleaseSessionOwnership(out _));
                Assert.That(h.State.IsPoisoned, Is.False);

                Assert.That(h.State.TryPoison(), Is.True);

                // The retry is refused, so the partially released lease is kept
                // rather than released under a poisoned process.
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.False);
                Assert.That(receipt, Is.Null);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.False);
            }
        }

        [Test]
        public void ReleaseSessionOwnership_ReleasedButNoValidReceipt_Poisons()
        {
            // A releaser that really releases the lease and then hands back
            // nothing usable. The lease can no longer be released, so this is
            // unretryable and must poison while the original exception
            // propagates.
            AssertReleasedWithoutReceiptPoisons(
                (releaser, operation) => null,
                "a null receipt after a completed release must poison");

            AssertReleasedWithoutReceiptPoisons(
                (releaser, operation) => NvencRunSessionOwnershipReleaseReceipt.Create(
                    new FakeSessionOwnershipReleaser(), operation),
                "a foreign-releaser receipt after a completed release must poison");
        }

        [Test]
        public void ReleaseSessionOwnership_ChangesOnlyTheRetainedReceiptAndLease()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareReleaseOperation(h);

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

                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                Assert.That(h.State.IsPoisoned, Is.False);

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

        [Test]
        public void PrepareSessionOwnershipRelease_AfterRelease_ReturnsFalseWithoutPoison()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReleaseOperation(h);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);

                // The existing preparation rule stands: a completed release
                // leaves nothing to prepare, and that is not corruption.
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation again), Is.False);
                Assert.That(again, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        // ---- Run completion ----

        [Test]
        public void CompleteRun_BeforeReleaseSucceeds_ReturnsFalse_StaysDraining()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReleaseOperation(h);

                // Prepared but not released: nothing proves the Run finished.
                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.False);
                Assert.That(h.State.IsDraining, Is.True);
                Assert.That(h.State.IsAccepting, Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void CompleteRun_BeforeReleasePrepared_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.False);
                Assert.That(h.State.IsDraining, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void CompleteRun_AfterRelease_PublishesRunningAndReadmits()
        {
            foreach (NvencRunCaptureCompleteCleanupStatus status in new[]
            {
                NvencRunCaptureCompleteCleanupStatus.Cleaned,
                NvencRunCaptureCompleteCleanupStatus.Failed,
            })
            {
                using (Harness h = Harness.Create())
                {
                    PrepareReflectedCleanup(h, status);
                    Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.True);
                    Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);

                    NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                    Assert.That(disposition, Is.EqualTo(
                        status == NvencRunCaptureCompleteCleanupStatus.Cleaned
                            ? NvencRunEvidenceDisposition.CaptureComplete
                            : NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                    // Both terminal dispositions finish on a genuine release
                    // receipt, and the disposition itself is left alone.
                    Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);

                    Assert.That(h.State.IsAccepting, Is.True);
                    Assert.That(h.State.IsDraining, Is.False);
                    Assert.That(h.State.IsRunAbandoned, Is.False);
                    Assert.That(h.State.IsPoisoned, Is.False);
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));

                    // The next Run can be admitted.
                    Assert.That(h.State.TryBeginAdmission(), Is.True);
                    h.State.EndAdmission();
                }
            }
        }

        [Test]
        public void CompleteRun_AfterAbandonedRun_ClearsTheAbandonedFlag()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);

                // Recorded through the ordinary API while already Draining.
                Assert.That(h.State.TryBeginRunAbandoned(), Is.True);
                Assert.That(h.State.IsRunAbandoned, Is.True);

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);

                Assert.That(h.State.IsRunAbandoned, Is.False);
                Assert.That(h.State.IsAccepting, Is.True);
                Assert.That(h.State.TryBeginAdmission(), Is.True);
                h.State.EndAdmission();
            }
        }

        [Test]
        public void CompleteRun_Twice_ReturnsTrue_NoSecondReleaseOrTransition()
        {
            using (Harness h = Harness.Create())
            {
                CompleteReleasedRun(h);

                int releaserCalls = h.Releaser.CallCount;
                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(releaserCalls));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.State.IsAccepting, Is.True);

                // A later Run may already have moved the process on. The old
                // Coordinator must still answer true rather than call that
                // corruption.
                Assert.That(h.State.TryBeginDrain(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);
                Assert.That(h.State.IsDraining, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void CompleteRun_GateContention_ReturnsFalseNoChange_ThenSucceeds()
        {
            // The events outlive the Harness: the holder thread waits on them,
            // so they are released only after it has been joined.
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };

                bool holderJoined = false;
                holder.Start();
                try
                {
                    // Inside the try, so a timeout here still releases the
                    // holder and joins it.
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True,
                        "holder did not acquire the gate");

                    Assert.That(h.RunCoordinator.TryCompleteRun(), Is.False);
                    Assert.That(h.State.IsDraining, Is.True);
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);
                Assert.That(h.State.IsAccepting, Is.True);
            }
        }

        [Test]
        public void CompleteRun_ExternalPoisonFirst_ReturnsFalse_DoesNotUnpoison()
        {
            using (Harness h = Harness.Create())
            {
                PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.False);
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.State.IsAccepting, Is.False);
                Assert.That(h.State.TryBeginAdmission(), Is.False);
            }
        }

        [Test]
        public void CompleteRun_UsesTheRetainedReleaseReceiptAsItsAuthority()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareReleaseOperation(h);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.True);

                // The receipt this Run retained is the exact one the completion
                // rests on: issued by the configured releaser for the exact
                // retained operation, and evidence of a fully released lease.
                Assert.That(ReferenceEquals(receipt.Releaser, h.Releaser), Is.True);
                Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(operation.OwnershipLease.IsReleaseComplete, Is.True);

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);
                Assert.That(h.State.IsAccepting, Is.True);
            }
        }

        [Test]
        public void CompleteRun_ChangesNoRegistryDispositionResultServiceOrLease()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareReleaseOperation(h);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.True);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                NvencRunChunkContextState contextState = h.Context.State;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                bool serviceStopped = h.Service.IsStopped;
                bool leaseCreated = h.SessionIssue.OwnershipLease.IsCreated;
                bool leaseCanRelease = h.SessionIssue.OwnershipLease.CanRelease;
                bool leaseReleaseComplete = h.SessionIssue.OwnershipLease.IsReleaseComplete;
                int releaserCalls = h.Releaser.CallCount;
                int cleanerCalls = h.CleanupCleaner.CallCount;
                int publisherCalls = h.Publisher.CallCount;
                int committerCalls = h.Committer.CallCount;
                int indexCommitterCalls = h.IndexCommitter.CallCount;
                int completerCalls = h.RunCompleter.CallCount;
                NvencRunCaptureCompleteCleanupOperation cleanupOperation = operation.CleanupOperation;

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);

                // Only the process state moved.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));

                // The lock is not touched again in either direction.
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                Assert.That(h.SessionIssue.OwnershipLease.CanRelease, Is.EqualTo(leaseCanRelease));
                Assert.That(
                    h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.EqualTo(leaseReleaseComplete));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

                // No collaborator is contacted again.
                Assert.That(h.Releaser.CallCount, Is.EqualTo(releaserCalls));
                Assert.That(h.CleanupCleaner.CallCount, Is.EqualTo(cleanerCalls));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));

                // The retained release result graph is the same reference.
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt again), Is.True);
                Assert.That(ReferenceEquals(again, receipt), Is.True);
                Assert.That(ReferenceEquals(again.Operation, operation), Is.True);
                Assert.That(ReferenceEquals(operation.CleanupOperation, cleanupOperation), Is.True);
            }
        }

        // ---- Run completion helpers ----

        private static void CompleteReleasedRun(Harness h)
        {
            PrepareReleaseOperation(h);
            Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(out _), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);
            Assert.That(h.State.IsAccepting, Is.True);
        }

        // ---- Session Ownership Lease release execution helpers ----

        private static NvencRunSessionOwnershipReleaseOperation PrepareReleaseOperation(Harness h)
        {
            PrepareReflectedCleanup(h, NvencRunCaptureCompleteCleanupStatus.Cleaned);
            Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                out NvencRunSessionOwnershipReleaseOperation operation), Is.True);
            return operation;
        }

        /// <summary>
        /// Drives a release that really disposes the lease and then returns the
        /// forged receipt, so the completed release is real and the forged
        /// receipt is the only reason the attempt cannot be retained. The forge
        /// runs after the release, with the executing releaser and the executed
        /// operation in hand, so a mismatch cannot be mistaken for some earlier
        /// rejection.
        /// </summary>
        private static void AssertReleasedWithoutReceiptPoisons(
            Func<FakeSessionOwnershipReleaser, NvencRunSessionOwnershipReleaseOperation,
                NvencRunSessionOwnershipReleaseReceipt> forge,
            string message)
        {
            using (Harness h = Harness.Create())
            {
                NvencRunSessionOwnershipReleaseOperation operation = PrepareReleaseOperation(h);

                h.Releaser.OverrideFactory = (executing, executed) =>
                {
                    Assert.That(ReferenceEquals(executing, h.Releaser), Is.True,
                        message + ": the forge must run on the configured releaser.");
                    Assert.That(ReferenceEquals(executed, operation), Is.True,
                        message + ": the forge must run on the prepared operation.");
                    Assert.That(executed.OwnershipLease.IsReleaseComplete, Is.True,
                        message + ": the release must really have completed.");
                    return forge(executing, executed);
                };

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryReleaseSessionOwnership(out _), message);

                Assert.That(h.State.IsPoisoned, Is.True, message);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1), message);
                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True, message);

                // Nothing was retained, and a re-call refuses on the Poison.
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt receipt), Is.False, message);
                Assert.That(receipt, Is.Null, message);
                Assert.That(h.Releaser.CallCount, Is.EqualTo(1), message);
            }
        }

        // ---- Session Ownership Lease release helpers ----

        /// <summary>
        /// Drives the Run to a reflected CaptureComplete cleanup of the given
        /// status and returns the result that was reflected.
        /// </summary>
        private static NvencRunCaptureCompleteCleanupAttemptResult PrepareReflectedCleanup(
            Harness h,
            NvencRunCaptureCompleteCleanupStatus status)
        {
            NvencRunCaptureCompleteCleanupOperation cleanup = PrepareCleanupOperation(h);
            NvencRunCaptureCompleteCleanupAttemptResult result = ExecuteCleanup(h, cleanup, status);
            Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(result), Is.True);
            return result;
        }

        // ---- CaptureComplete preparation ----

        /// <summary>
        /// Drives the Run to a Committed, collected capture index commit. The
        /// same Service and Worker stay alive in the CaptureComplete phase.
        /// </summary>
        private static NvencRunCaptureIndexCommitAttemptResult CommitCaptureIndexAndCollect(
            Harness h,
            NvencRunCaptureIndexCommitStatus status)
        {
            PrepareCaptureIndexSubmission(h);
            h.IndexCommitter.Status = status;
            Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
            WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");
            Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                out NvencRunCaptureIndexCommitAttemptResult result), Is.True);
            return result;
        }

        [Test]
        public void PrepareCaptureComplete_CommittedIndexCollected_ForwardsExactReferences()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitAttemptResult index =
                    CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);
                Assert.That(index.IsCommitted, Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation operation), Is.True);

                Assert.That(operation, Is.Not.Null);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.True);
                Assert.That(operation.IsIssuedFor(null), Is.False);

                // Every forwarded value is the existing graph's exact reference.
                Assert.That(ReferenceEquals(operation.CaptureIndexCommitReceipt, index.Receipt), Is.True);
                Assert.That(ReferenceEquals(operation.CaptureIndexCommitOperation, index.Operation), Is.True);
                Assert.That(ReferenceEquals(operation.Plan, index.Plan), Is.True);
                Assert.That(ReferenceEquals(operation.RootLayout, index.RootLayout), Is.True);
                Assert.That(ReferenceEquals(
                    operation.RunInitializationId, index.RunInitializationId), Is.True);
                Assert.That(operation.TestRunId, Is.EqualTo(index.TestRunId));
                Assert.That(operation.TestRunId, Is.EqualTo(h.Context.TestRunId));

                // The artifact publication receipt CaptureComplete will use is
                // the very reference publication issued: it is neither
                // re-issued nor copied, and the artifact is never re-hashed.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult publication), Is.True);
                Assert.That(ReferenceEquals(
                    operation.ArtifactPublicationReceipt, publication.Receipt), Is.True);
                Assert.That(ReferenceEquals(
                    operation.ArtifactPublicationOperation, publication.Operation), Is.True);
                Assert.That(ReferenceEquals(
                    operation.ArtifactPublicationReceipt, index.ArtifactPublicationReceipt), Is.True);
            }
        }

        [Test]
        public void PrepareCaptureComplete_Idempotent_ReturnsSameReference()
        {
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation first), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation again), Is.True);

                Assert.That(ReferenceEquals(first, again), Is.True);
                Assert.That(first.IsValid, Is.True);
            }
        }

        [Test]
        public void PrepareCaptureComplete_BeforeIndexPrepareSubmitOrCollect_ReturnsFalse()
        {
            // The capture index commit has not been prepared yet.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the artifact terminal");
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Prepared but not submitted.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Submitted with a Committed terminal published, but not collected.
            using (Harness h = Harness.Create())
            {
                PrepareCaptureIndexSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForCaptureIndexTerminal(h, "publication worker did not reach the capture index terminal");

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);

                // Collecting the Committed result is what admits the
                // preparation.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(out _), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.True);
            }
        }

        [Test]
        public void PrepareCaptureComplete_FailedIndex_RecoveryRequired_ReturnsFalseWithoutPoison()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitAttemptResult index =
                    CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Failed);
                Assert.That(index.IsFailed, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation operation), Is.False);
                Assert.That(operation, Is.Null);

                // A Failed capture index commit is a normal not-ready shape,
                // never corruption.
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }
        }

        [Test]
        public void PrepareCaptureComplete_NonCommittedDisposition_ReturnsFalse()
        {
            // Running: still capturing, nothing published.
            using (Harness h = Harness.Create())
            {
                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Finalized: no plan commit has been collected.
            using (Harness h = Harness.Create())
            {
                FinalizeOnly(h);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Incomplete: the plan commit failed before its rename.
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.FailedBeforeRename);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // PublicationRecoveryRequired from a Failed artifact publication:
            // the capture index phase is never entered.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                h.Publisher.Status = NvencRunArtifactPublicationStatus.Failed;
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the artifact terminal");
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareCaptureComplete_ExternalPoisonFirst_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation operation), Is.False);
                Assert.That(operation, Is.Null);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }
        }

        [Test]
        public void PrepareCaptureComplete_PoisonAfterPrepare_InvalidatesOperationAndRefuses()
        {
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation prepared), Is.True);
                Assert.That(prepared.IsValid, Is.True);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;

                Assert.That(h.State.TryPoison(), Is.True);

                // Poison outranks the retained operation: it stops being usable
                // and the re-call refuses without an exception.
                Assert.That(prepared.IsValid, Is.False);
                Assert.That(prepared.IsIssuedFor(h.RunCoordinator), Is.False);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation again), Is.False);
                Assert.That(again, Is.Null);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
            }
        }

        [Test]
        public void PrepareCaptureComplete_FirstPrepareWithBrokenCommittedIndex_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitAttemptResult index =
                    CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);
                Assert.That(index.IsCommitted, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                // Break the published Committed graph through the ordinary
                // ownership API before any CaptureComplete operation is minted.
                h.SessionIssue.OwnershipLease.Dispose();

                // The Run is not merely not-ready here: a published Committed
                // capture index whose correlation is broken can never progress,
                // so the first preparation must poison rather than look like a
                // normal poll.
                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareCaptureComplete(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void PrepareCaptureComplete_BrokenRetainedOperation_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation prepared), Is.True);
                Assert.That(prepared.IsValid, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                // Releasing the Session Ownership Lease breaks the retained
                // graph while the published disposition is still Committed.
                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(prepared.IsValid, Is.False);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryPrepareCaptureComplete(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void PrepareCaptureComplete_GateContention_ReturnsFalseNoChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                        out NvencRunCaptureCompleteOperation contended), Is.False);
                    Assert.That(contended, Is.Null);
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                // The refusal left nothing behind: the first real preparation
                // still mints the operation.
                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation prepared), Is.True);
                Assert.That(prepared, Is.Not.Null);
            }
        }

        [Test]
        public void PrepareCaptureComplete_ChangesNoRunOrServiceState()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitAttemptResult index =
                    CommitCaptureIndexAndCollect(h, NvencRunCaptureIndexCommitStatus.Committed);

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

                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation operation), Is.True);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.AcceptingCaptureComplete));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.Service.IsStopped, Is.EqualTo(serviceStopped));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                Assert.That(h.State.IsPoisoned, Is.False);

                // The retained capture index and publication results are reused,
                // never replaced or re-issued.
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult againIndex), Is.True);
                Assert.That(ReferenceEquals(againIndex.Operation, index.Operation), Is.True);
                Assert.That(ReferenceEquals(againIndex.Receipt, index.Receipt), Is.True);
                Assert.That(ReferenceEquals(
                    operation.CaptureIndexCommitReceipt, againIndex.Receipt), Is.True);
            }
        }

        [Test]
        public void CaptureCompleteOperation_TwoReadonlyFields_SealedInternal()
        {
            Type type = typeof(NvencRunCaptureCompleteOperation);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));

            // Verify by field-type set, never by reflection return order or
            // private field names: a harmless rename must not break this test.
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(NvencCaptureRunCoordinator),
                    typeof(NvencRunCaptureIndexCommitReceipt),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        // ---- Capture index commit preparation ----

        private static NvencRunArtifactPublicationAttemptResult PublishAndCollectArtifact(
            Harness h,
            NvencRunArtifactPublicationStatus status)
        {
            PrepareArtifactSubmission(h);
            h.Publisher.Status = status;

            Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
            WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");
            Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                out NvencRunArtifactPublicationAttemptResult result), Is.True);
            return result;
        }

        [Test]
        public void PrepareCaptureIndexCommit_PublishedCollected_ForwardsExactReferences()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationAttemptResult result =
                    PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);
                Assert.That(result.IsPublished, Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation operation), Is.True);

                Assert.That(operation, Is.Not.Null);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.True);
                Assert.That(operation.IsIssuedFor(null), Is.False);

                // The same Publication Receipt is reused, and every forwarded
                // value comes from that receipt's graph by reference.
                Assert.That(ReferenceEquals(operation.ArtifactPublicationReceipt, result.Receipt), Is.True);
                Assert.That(ReferenceEquals(operation.ArtifactPublicationOperation, result.Operation), Is.True);
                Assert.That(ReferenceEquals(operation.Plan, result.Plan), Is.True);
                Assert.That(ReferenceEquals(operation.RootLayout, result.RootLayout), Is.True);
                Assert.That(ReferenceEquals(operation.Plan, result.Receipt.Plan), Is.True);
                Assert.That(ReferenceEquals(operation.RootLayout, result.Receipt.RootLayout), Is.True);
                Assert.That(
                    ReferenceEquals(operation.RunInitializationId, result.Receipt.RunInitializationId), Is.True);
                Assert.That(operation.TestRunId, Is.EqualTo(result.TestRunId));
                Assert.That(operation.TestRunId, Is.EqualTo(h.Context.TestRunId));
                Assert.That(operation.RunInitializationId,
                    Is.EqualTo(h.SessionIssue.Session.RunInitializationId));
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_Idempotent_ReturnsSameReference()
        {
            using (Harness h = Harness.Create())
            {
                PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation first), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation again), Is.True);

                Assert.That(ReferenceEquals(first, again), Is.True);
                Assert.That(first.IsValid, Is.True);
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_BeforePrepareSubmitOrCollect_ReturnsFalse()
        {
            // The artifact publication has not been prepared yet.
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation operation), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Prepared but not submitted.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Submitted and the Worker stopped, but the result is not collected.
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);

                // Collecting the Published result is what admits the preparation.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);
                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.True);
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_FailedPublication_RecoveryRequired_ReturnsFalse()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationAttemptResult result =
                    PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Failed);
                Assert.That(result.IsFailed, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation operation), Is.False);
                Assert.That(operation, Is.Null);

                // A Failed publication is a normal not-ready shape, never
                // corruption.
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.PublicationRecoveryRequired));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_RunningFinalizedIncompleteOrUnknown_ReturnsFalse()
        {
            // Running: still capturing, nothing published.
            using (Harness h = Harness.Create())
            {
                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Finalized: no plan commit has been collected.
            using (Harness h = Harness.Create())
            {
                FinalizeOnly(h);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // Incomplete: the plan commit failed before its rename.
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.FailedBeforeRename);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }

            // CommitOutcomeUnknown: the plan commit outcome is unresolved.
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CommitOutcomeUnknown));

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(out _), Is.False);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_ExternalPoisonFirst_ReturnsFalseNoOperation()
        {
            using (Harness h = Harness.Create())
            {
                PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation operation), Is.False);
                Assert.That(operation, Is.Null);

                // The published Committed state is untouched by the refusal.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_PoisonAfterPrepare_InvalidatesOperationAndRefuses()
        {
            using (Harness h = Harness.Create())
            {
                PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation prepared), Is.True);
                Assert.That(prepared.IsValid, Is.True);

                Assert.That(h.State.TryPoison(), Is.True);

                // Poison outranks the retained operation: the already-issued
                // one stops being usable and the re-call refuses without an
                // exception, so no later capture index work can start.
                Assert.That(prepared.IsValid, Is.False);
                Assert.That(prepared.IsIssuedFor(h.RunCoordinator), Is.False);

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation again), Is.False);
                Assert.That(again, Is.Null);

                // The published Committed state is untouched by the refusal.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_GateContention_ReturnsFalseNoChange()
        {
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                        out NvencRunCaptureIndexCommitOperation contended), Is.False);
                    Assert.That(contended, Is.Null);
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");


                // The refusal left nothing behind: the first real preparation
                // still mints the operation.
                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation prepared), Is.True);
                Assert.That(prepared, Is.Not.Null);
            }
        }

        [Test]
        public void PrepareCaptureIndexCommit_ContactsNothingAndChangesNoRunState()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunArtifactPublicationAttemptResult result =
                    PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);

                // The Publisher and the Committer are the only collaborators
                // that can reach a filesystem in this graph; their call counts
                // pin that preparation contacts neither them nor the Service.
                int publisherCalls = h.Publisher.CallCount;
                int committerCalls = h.Committer.CallCount;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                NvencRunChunkContextState contextState = h.Context.State;
                bool leaseValid = h.SessionIssue.IsValid;

                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation operation), Is.True);

                Assert.That(h.Publisher.CallCount, Is.EqualTo(publisherCalls));
                Assert.That(h.Committer.CallCount, Is.EqualTo(committerCalls));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                Assert.That(h.State.IsPoisoned, Is.False);

                // The retained publication result and its receipt are reused,
                // never replaced or re-issued.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult again), Is.True);
                Assert.That(ReferenceEquals(again.Operation, result.Operation), Is.True);
                Assert.That(ReferenceEquals(again.Receipt, result.Receipt), Is.True);
                Assert.That(ReferenceEquals(operation.ArtifactPublicationReceipt, again.Receipt), Is.True);
            }
        }

        [Test]
        public void CaptureIndexCommitOperation_TwoReadonlyFields_SealedInternal()
        {
            Type type = typeof(NvencRunCaptureIndexCommitOperation);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));

            // Verify by field-type set, never by reflection return order or
            // private field names: a harmless rename must not break this test.
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(NvencCaptureRunCoordinator),
                    typeof(NvencRunArtifactPublicationReceipt),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void TraceFreeze_BeforeBackendJoin_RefusesNoTraceContact()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);

                // Backend Join has not completed: the trace is never contacted
                // and no disposition is published.
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(MakeForcedDropSet(h), MakeCheckpoint(h), out _), Is.False);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.None));
                Assert.That(h.TraceRecorder.State, Is.Not.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void TraceFreeze_Poisoned_RefusesNoDisposition()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);

                h.State.TryPoison();
                Assert.That(h.State.IsPoisoned, Is.True);

                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(MakeForcedDropSet(h), MakeCheckpoint(h), out _), Is.False);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.None));
            }
        }

        [Test]
        public void TraceFreeze_LeaseLost_RefusesNoTraceContact()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);

                // Release the Ownership Lease: the session issue is no longer
                // valid, so the trace is never contacted.
                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(MakeForcedDropSet(h), MakeCheckpoint(h), out _), Is.False);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.None));
                Assert.That(h.TraceRecorder.State, Is.Not.EqualTo(TraceFlightRecorderState.Frozen));
            }
        }

        [Test]
        public void TraceFreeze_LeaseLostAfterFreeze_IdempotentPathFailsClosed()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                // Release the Ownership Lease: the retained receipt's full
                // correlation no longer holds, so the idempotent path fails
                // closed instead of re-reporting success.
                h.SessionIssue.OwnershipLease.Dispose();

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void TraceFreeze_LeaseLostAfterFreeze_LowerCoordinatorFailsClosed()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);

                // Release the Ownership Lease: the lower freeze coordinator's
                // retained receipt no longer correlates, so directly re-running
                // TryCompleteFreeze must fail closed rather than return true.
                h.SessionIssue.OwnershipLease.Dispose();

                Assert.Throws<InvalidOperationException>(
                    () => h.TraceFreeze.TryCompleteFreeze(forced, checkpoint, out _));
            }
        }

        [Test]
        public void TraceFreezeReceipt_ForeignSealReceipt_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out NvencTraceFreezeReceipt ok), Is.True);

                // A seal receipt issued by a foreign logger for the same run ID
                // must be rejected: it is not the exact seal this coordinator
                // issued.
                TraceLogger foreignLogger = new TraceLogger(16, h.Context.TestRunId);
                TraceFlightRecorder foreignRecorder = new TraceFlightRecorder(foreignLogger, 16, 2);
                TraceRunSealReceipt foreignSeal = new TraceRunSealReceipt(
                    foreignLogger, foreignRecorder, h.Context.TestRunId, 0, 0, 0, 0);

                Assert.Throws<ArgumentException>(() => new NvencTraceFreezeReceipt(
                    h.TraceFreeze, h.Context, h.SessionIssue, foreignSeal, ok.TerminalBuffer));
            }
        }

        [Test]
        public void TraceFreezeReceipt_ForeignTerminalBuffer_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
                FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out NvencTraceFreezeReceipt ok), Is.True);

                // A same-run buffer built independently is not the exact buffer
                // this coordinator appended, and must be rejected.
                FreezeTerminalTraceBufferBuilder foreignBuilder = new FreezeTerminalTraceBufferBuilder(h.DraftRegistry);
                FreezeTerminalTraceBuffer foreignBuffer = foreignBuilder.Build(forced, checkpoint);

                Assert.Throws<ArgumentException>(() => new NvencTraceFreezeReceipt(
                    h.TraceFreeze, h.Context, h.SessionIssue, ok.SealReceipt, foreignBuffer));
            }
        }

        [Test]
        public void TraceFreeze_PostSealValidationFailure_SealRetained()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);
                Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
                Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
                Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

                ForcedDropFrameIdSet forced = MakeForcedDropSet(h);

                // First call: an invalid checkpoint fails the freeze terminal
                // coordinator's post-seal pre-validation, so the terminal
                // append and the AwaitingFreezeTerminal transition never occur.
                FreezeTerminalCheckpoint badCheckpoint = new FreezeTerminalCheckpoint(1000, 1, 1, 1, 999);
                Assert.Throws<ArgumentException>(
                    () => h.RunCoordinator.TryCompleteTraceFreeze(forced, badCheckpoint, out _));

                // The seal was issued exactly once and retained.
                TraceRunSealReceipt sealedOnce = h.TraceLogger.IssuedSealReceipt;
                Assert.That(sealedOnce, Is.Not.Null);

                // A normal retry converges to Frozen reusing the retained seal;
                // the seal is never re-issued.
                Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, MakeCheckpoint(h), out NvencTraceFreezeReceipt receipt), Is.True);
                Assert.That(h.TraceRecorder.State, Is.EqualTo(TraceFlightRecorderState.Frozen));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));

                Assert.That(ReferenceEquals(h.TraceLogger.IssuedSealReceipt, sealedOnce), Is.True);
                Assert.That(ReferenceEquals(receipt.SealReceipt, sealedOnce), Is.True);
            }
        }

        // ---- Publication Plan Commit submission and reflection ----

        [Test]
        public void Constructor_ForeignProcessStateService_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencCaptureProcessState foreign = new NvencCaptureProcessState();
                NvencRunPublicationService foreignService = new NvencRunPublicationService(
                    foreign,
                    new NvencRunPublicationPlanCommitExecutionCoordinator(new FakeCommitter()),
                    new NvencRunArtifactPublicationExecutionCoordinator(new FakePublisher()),
                    new NvencRunCaptureIndexCommitExecutionCoordinator(new FakeIndexCommitter()),
                    new NvencRunCaptureCompleteExecutionCoordinator(new FakeRunCompleter()));

                try
                {
                    Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                        h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown,
                        h.BackendJoin, h.SessionIssue, h.TraceFreeze, foreignService, h.CleanupExecution, h.ReleaseExecution));
                }
                finally
                {
                    if (!foreignService.IsStopped)
                    {
                        foreign.TryPoison();
                        foreignService.Notify();
                    }

                    FieldInfo field = typeof(NvencRunPublicationService).GetField(
                        "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                    Thread worker = (Thread)field?.GetValue(foreignService);
                    if (worker != null)
                    {
                        Assert.That(worker.Join(WatchdogTimeoutMs), Is.True, "foreign service worker did not stop");
                    }

                    foreignService.Dispose();
                }
            }
        }

        [Test]
        public void Submit_BeforePrepare_Poisoned_GateBusy_NoServiceContact()
        {
            // Before preparation: the retained operation does not exist.
            using (Harness h = Harness.Create())
            {
                FinalizeOnly(h);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.False);
                Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            }

            // Poisoned: the process-state gate refuses before any Service contact.
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.False);
                Assert.That(h.Committer.CallCount, Is.EqualTo(0));
            }

            // Gate contention: a background holder keeps the shared gate busy.
            using (ManualResetEventSlim gateHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);

                Thread holder = new Thread(() =>
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        gateHeld.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                })
                {
                    IsBackground = true,
                };
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                    Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.False);
                    Assert.That(h.Committer.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "holder did not exit");

                // Once the gate is released the submission proceeds.
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Submit_DoubleSubmit_SecondRejectedExactlyOnce()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.False);

                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Collect_Committed_AdvancesSlotAndDisposition()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);

                Assert.That(result, Is.Not.Null);
                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Committer.ExecutingThreadName, Is.EqualTo(NvencRunPublicationService.WorkerThreadName));
            }
        }

        [Test]
        public void Collect_FailedBeforeRename_DiscardsAndIncomplete()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.FailedBeforeRename;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceStop(h, "service worker did not stop");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);

                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.FailedBeforeRename));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Collect_CommitOutcomeUnknown_KeepsRegisteredAndUnknown()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceStop(h, "service worker did not stop");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);

                Assert.That(result.Status, Is.EqualTo(NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.CommitOutcomeUnknown));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Collect_Idempotent_ReturnsSameReferenceNoRerun()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult first), Is.True);

                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult again), Is.True);
                Assert.That(ReferenceEquals(first, again), Is.True);

                // No re-run, no re-collection, no re-transition.
                Assert.That(h.Committer.CallCount, Is.EqualTo(1));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

                // A Committed collect keeps the Worker parked for the Artifact
                // phase: the Service is neither stopped nor released.
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
            }
        }

        [Test]
        public void Collect_PollBeforeCompleted_RetrySucceeds()
        {
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                h.Committer.Entered = entered;
                h.Committer.Release = release;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "committer did not enter");

                // A poll while the Worker is still executing must not collect
                // and must not clear the slot.
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.False);

                release.Set();
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan commit terminal");

                // After the terminal is published, the retry collects and
                // reflects.
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
                Assert.That(result, Is.Not.Null);
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));

            }
        }

        [Test]
        public void Collect_ExternalPoisonFirst_NoNormalReflection()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult result), Is.False);
                Assert.That(result, Is.Null);

                // The uncollected result is never reflected into a normal state.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
            }
        }

        [Test]
        public void Collect_CorruptResult_PoisonsNoDisposition()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");

                // Break the issuance binding so the collected result is no longer
                // valid at reflection time.
                h.SessionIssue.OwnershipLease.Dispose();

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryCollectPublicationPlanCommit(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
            }
        }

        [Test]
        public void Collect_RegistryTransitionFailure_PoisonsNoDisposition()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");

                // Corrupt the registered entry so the Commit transition fails.
                SetField(h.Slot, "_result", null);

                Assert.Throws<InvalidOperationException>(
                    () => h.RunCoordinator.TryCollectPublicationPlanCommit(out _));
                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
            }
        }

        [Test]
        public void Collect_Committed_KeepsWorkerParkedAndServiceAlive()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted, "service did not publish the plan commit terminal");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);

                // A Committed collect keeps the Worker parked for the Artifact
                // phase: the Service is neither physically stopped nor released.
                Assert.That(h.Service.IsStopped, Is.False);
                Assert.That((int)GetField(h.Service, "_lifecycleState"), Is.EqualTo(0));
                Assert.That(h.Service.State, Is.EqualTo(NvencRunPublicationServiceState.AcceptingArtifactPublication));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.False);
            }
        }

        [Test]
        public void Reflect_UnknownPath_NoFileCleanupRenameRetry()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencCaptureRunCoordinator.cs"));

            int start = source.IndexOf("private void ReflectPublicationPlanCommit", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "reflect entry not found");
            int next = source.IndexOf("private static bool IsLowerHex", start + 1, StringComparison.Ordinal);
            string entry = next >= 0
                ? source.Substring(start, next - start)
                : source.Substring(start);

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "Path.", "Flush(", "Move(", "Close(",
                "Cleanup", "Abort", "Delete", "Retry", "Rollback", "re-read", "rename",
                "JsonUtility", "ComputeHash", "HashAlgorithm", "IncrementalHash",
                "SHA256", "SHA384", "SHA512", "MD5", "System.Security.Cryptography",
                "OwnershipLease", "Dispose(",
            };
            foreach (string word in forbidden)
            {
                Assert.That(entry, Does.Not.Contain(word), "reflect entry must not contain: " + word);
            }

            // The unknown-outcome branch never guesses Committed, never discards,
            // and never re-transitions the Registry Slot.
            int unknownStart = source.IndexOf(
                "case NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown", StringComparison.Ordinal);
            int defaultStart = source.IndexOf("default:", unknownStart + 1, StringComparison.Ordinal);
            Assert.That(unknownStart, Is.GreaterThanOrEqualTo(0), "unknown-outcome branch not found");
            Assert.That(defaultStart, Is.GreaterThan(unknownStart), "default branch not found after unknown branch");

            string unknown = source.Substring(unknownStart, defaultStart - unknownStart);
            Assert.That(unknown, Does.Not.Contain("TryCommit"));
            Assert.That(unknown, Does.Not.Contain("TryDiscardRegistered"));
            Assert.That(unknown, Does.Not.Contain("TryRegister"));
        }

        // ---- Constructor correlation ----

        [Test]
        public void Constructor_ForeignProcessState_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    new NvencCaptureProcessState(), h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_ForeignSubmitWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, BuildSubmitWorker(new NvencCaptureProcessState()), h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_SubmitWorkerNotBoundToOutput_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                // Same process state but a different Submit Worker instance
                // than the one the Output Worker is bound to.
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, BuildSubmitWorker(h.State), h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_ForeignOutputWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            using (Harness other = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, other.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_ForeignContext_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, MakeContext(new NvencCaptureProcessState()), h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_ForeignRegistrySlot_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunChunkContext foreign = MakeContext(new NvencCaptureProcessState());
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, new NvencRunLocalRegistrySlot(foreign), h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_ForeignContextTextureTeardown_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                // A teardown bound to a foreign Run chunk context, even with
                // the same process state, must be rejected before any side
                // effect: otherwise Run A could destroy Run B's Textures and
                // still validate its own receipt.
                FakeMainThreadTeardown foreignTeardown = new FakeMainThreadTeardown
                {
                    BoundContext = MakeContext(h.State),
                };

                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, foreignTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
            }
        }

        [Test]
        public void Constructor_NullDependency_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    null, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, null, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, null, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, null, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, null, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, null, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, null, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, null, h.TraceFreeze, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, null, h.Service, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, null, h.CleanupExecution, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, null, h.ReleaseExecution));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service, h.CleanupExecution, null));
            }
        }

        // ---- Shape and non-contact ----

        [Test]
        public void Coordinator_SealedNotDisposable_NoFieldCountFixed()
        {
            Type type = typeof(NvencCaptureRunCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            // The exact field count is intentionally not fixed so a later
            // coordinator extension is not blocked; each field must be private
            // (readonly where possible) and must not hold a forbidden type.
            Type[] forbiddenTypes =
            {
                typeof(Thread), typeof(System.Threading.Timer), typeof(Stream), typeof(byte[]),
                typeof(NvencCaptureWorkSlotPool), typeof(NvencEncodeSampleSlotPool),
                typeof(NvencGpuConversionSyncPool), typeof(NvencSubmitToOutputCreditPool),
                typeof(NvencFrameCompletionCreditPool), typeof(NvencOwnedAccessUnitBuffer),
                typeof(INvencRunChunkAppender),
            };

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(Array.IndexOf(forbiddenTypes, field.FieldType), Is.LessThan(0),
                    field.Name + " must not hold a forbidden type.");
            }
        }

        [Test]
        public void Coordinator_NoWaitNoSleepNoFilesystemNoNvEncNoAllocation()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencCaptureRunCoordinator.cs"));

            string[] forbidden =
            {
                "Thread.Sleep", "new Thread", "ThreadPool", "Task", "SpinWait", "WaitHandle",
                "ManualResetEvent", "AutoResetEvent", "Timer", "Monitor", "File.", "Directory.",
                "FileStream", "NvEnc", "UnityEngine", "DllImport", "new []", "new List",
                "new Dictionary", "new Queue", "new byte[", "Guid.NewGuid", "Enumerable",
                ".Select(", ".Where(", ".ToList(", ".ToArray(",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "coordinator source must not contain: " + word);
            }
        }

        // ---- Helpers ----

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

        private static void StopFinalizedBackend(Harness h, bool disposeSubmitWorker = true)
        {
            h.AcceptAndAppendChunk(1, 64, Seed);
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
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

            if (disposeSubmitWorker)
            {
                h.SubmitWorker.Dispose();
            }
        }

        private static void StopAbandonedBackend(Harness h, bool disposeSubmitWorker = true)
        {
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            NvencRunChunkTerminalOutcome outcome = CollectTerminal(h, "worker did not converge the abandon request");
            Assert.That(outcome.IsAbandoned, Is.True);

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            if (disposeSubmitWorker)
            {
                h.SubmitWorker.Dispose();
            }
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

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static NvencRunChunkContext MakeContext(NvencCaptureProcessState state)
        {
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);
            FakeWriter writer = new FakeWriter();
            NvencRunChunkSink sink = new NvencRunChunkSink(state, buffer, writer);
            NvencRunChunkFinalizationCoordinator coordinator = new NvencRunChunkFinalizationCoordinator(writer);
            return new NvencRunChunkContext(
                MakeIssue(false, out _, out _), sink, coordinator, "chunk/foreign");
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

        private static NvencRunPublicationPlanCommitOperation FinalizeAndPrepareCommit(Harness h)
        {
            FinalizeOnly(h);
            Assert.That(
                h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                Is.True);
            return operation;
        }

        private static NvencRunPublicationPlanCommitExecutionResult CommitAndCollect(
            Harness h,
            NvencRunPublicationPlanCommitStatus status)
        {
            FinalizeAndPrepareCommit(h);
            h.Committer.Status = status;
            Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
            if (status == NvencRunPublicationPlanCommitStatus.Committed)
            {
                // A Committed plan keeps the Worker parked; wait only for the
                // published terminal, never for a physical stop.
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the committed plan terminal");
            }
            else
            {
                WaitForServiceStop(h, "service worker did not stop");
            }

            Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                out NvencRunPublicationPlanCommitExecutionResult result), Is.True);
            return result;
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
            private int _callCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal string ExecutingThreadName;
            internal int ExecutingManagedThreadId;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

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
            internal bool UseOverride;
            internal NvencRunArtifactPublicationAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
            {
                Interlocked.Increment(ref _callCount);

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
            private int _finalizeCount;
            internal Exception ExceptionToThrow;
            internal bool BuildReceipt = true;

            internal int CallCount => Volatile.Read(ref _finalizeCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (!BuildReceipt)
                {
                    return null;
                }

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
            internal Exception ExceptionToThrow;
            internal NvencOutputWorkerTeardownReceipt ReceiptToReturn;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal string ExecutingThreadName;

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (Release != null)
                {
                    Release.Wait(WatchdogTimeoutMs);
                }

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class FakeMainThreadTeardown : INvencMainThreadTextureTeardown
        {
            private int _callCount;
            internal NvencRunChunkContext BoundContext;
            internal Exception ExceptionToThrow;
            internal bool ReturnNull;
            internal NvencMainThreadTextureTeardownReceipt ReceiptToReturn;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal int ExecutingThreadId;
            internal string ExecutingThreadName;

            public bool IsBoundTo(NvencRunChunkContext context)
            {
                return BoundContext != null && context != null && ReferenceEquals(BoundContext, context);
            }

            public NvencMainThreadTextureTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadId = Thread.CurrentThread.ManagedThreadId;
                ExecutingThreadName = Thread.CurrentThread.Name;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (Release != null)
                {
                    Release.Wait(WatchdogTimeoutMs);
                }

                if (ReturnNull)
                {
                    return null;
                }

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

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
        /// Session Ownership Lease releaser standing in for the production one:
        /// it shares the same admission predicate, releases the operation's
        /// exact lease once, and issues the ordinary receipt. It can park inside
        /// the attempt, so a test can hold the resource-resolution gate the
        /// attempt runs in, and it can hand back a forged receipt built after
        /// the release really completed.
        /// </summary>
        private sealed class FakeSessionOwnershipReleaser : INvencRunSessionOwnershipReleaser
        {
            private int _callCount;

            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Proceed;

            internal Func<FakeSessionOwnershipReleaser, NvencRunSessionOwnershipReleaseOperation,
                NvencRunSessionOwnershipReleaseReceipt> OverrideFactory;

            internal int CallCount => Volatile.Read(ref _callCount);

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

                Interlocked.Increment(ref _callCount);

                Entered?.Set();
                Proceed?.Wait(WatchdogTimeoutMs);

                operation.OwnershipLease.Dispose();

                if (OverrideFactory != null)
                {
                    return OverrideFactory(this, operation);
                }

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

            internal FakeWriter Finalizer => Writer;

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
                IndexCommitter = new FakeIndexCommitter();
                NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCoordinator =
                    new NvencRunCaptureIndexCommitExecutionCoordinator(IndexCommitter);
                RunCompleter = new FakeRunCompleter();
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
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze, Service, CleanupExecution, ReleaseExecution);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create(bool throwingFirstRelease = false)
            {
                Harness h = new Harness(throwingFirstRelease);

                // Deterministically park the worker once before returning.
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

                // Stop the Publication Plan Commit Service worker if it is still
                // parked (a test that never submitted a commit).
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
