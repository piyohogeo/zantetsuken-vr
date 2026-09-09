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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);

                // A background thread holds the shared process-state gate,
                // exactly as a concurrent Poison transition would; every entry
                // must refuse without advancing any state.
                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();

                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                try
                {
                    Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.False);
                    Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.False);
                    Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.False);
                    Assert.That(h.RunCoordinator.TryBeginDrain(out NvencRunAcceptedFrameSnapshot blocked), Is.False);
                    Assert.That(blocked, Is.Null);
                }
                finally
                {
                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
                }

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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

                // Park the Output Worker inside its teardown so it is
                // deterministically not yet stopped.
                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();

                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                try
                {
                    Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.False);
                    Assert.That(h.MainThreadTeardown.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
                }
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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCompletion(MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
                Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not complete the teardown");
                h.WaitForPhysicalStop("worker did not physically exit after the teardown");

                h.SubmitWorker.Dispose();

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
                ManualResetEventSlim poisonStarted = new ManualResetEventSlim(false);
                ManualResetEventSlim poisonDone = new ManualResetEventSlim(false);
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
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, other.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
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
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, foreignTeardownJoin, h.SessionIssue, h.TraceFreeze, h.Service));
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
            using (Harness h = Harness.Create())
            {
                CommitAndCollect(h, NvencRunPublicationPlanCommitStatus.Committed);

                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();
                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                try
                {
                    Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                        out NvencRunArtifactPublicationOperation operation), Is.False);
                    Assert.That(operation, Is.Null);
                }
                finally
                {
                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
                }

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
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");

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
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");
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
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);

                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();
                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                try
                {
                    Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.False);
                    Assert.That(h.Publisher.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
                }

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");
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
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");

                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsPublished, Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(h.Publisher, operation), Is.True);

                // Registry and disposition stay Committed; the Plan and chunk are
                // never touched.
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.SessionIssue.IsValid, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
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
                WaitForServiceStop(h, "publication worker did not stop after the failed artifact");

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
            using (Harness h = Harness.Create())
            {
                PrepareArtifactSubmission(h);

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
                h.Publisher.Entered = entered;
                h.Publisher.Release = release;

                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "publisher did not enter");

                // A poll while the Worker is still executing must not collect and
                // must not clear the slot.
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.False);

                release.Set();
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");

                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult result), Is.True);
                Assert.That(result.IsPublished, Is.True);

                entered.Dispose();
                release.Dispose();
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
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");

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

        // ---- Capture index commit preparation ----

        private static NvencRunArtifactPublicationAttemptResult PublishAndCollectArtifact(
            Harness h,
            NvencRunArtifactPublicationStatus status)
        {
            PrepareArtifactSubmission(h);
            h.Publisher.Status = status;

            Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
            WaitForServiceStop(h, "publication worker did not stop after the artifact publication");
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
                WaitForServiceStop(h, "publication worker did not stop after the artifact publication");

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
            using (Harness h = Harness.Create())
            {
                PublishAndCollectArtifact(h, NvencRunArtifactPublicationStatus.Published);

                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();
                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                try
                {
                    Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                        out NvencRunCaptureIndexCommitOperation contended), Is.False);
                    Assert.That(contended, Is.Null);
                    Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Committed));
                    Assert.That(h.State.IsPoisoned, Is.False);
                }
                finally
                {
                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
                }

                gateHeld.Dispose();
                release.Dispose();

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
                    new NvencRunArtifactPublicationExecutionCoordinator(new FakePublisher()));

                try
                {
                    Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                        h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown,
                        h.BackendJoin, h.SessionIssue, h.TraceFreeze, foreignService));
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
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);

                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                holder.Start();
                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "holder did not acquire the gate");
                try
                {
                    Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.False);
                    Assert.That(h.Committer.CallCount, Is.EqualTo(0));
                }
                finally
                {
                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
                }

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
            using (Harness h = Harness.Create())
            {
                FinalizeAndPrepareCommit(h);
                h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
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

                entered.Dispose();
                release.Dispose();
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
                    new NvencCaptureProcessState(), h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
            }
        }

        [Test]
        public void Constructor_ForeignSubmitWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, BuildSubmitWorker(new NvencCaptureProcessState()), h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
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
                    h.State, BuildSubmitWorker(h.State), h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
            }
        }

        [Test]
        public void Constructor_ForeignOutputWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            using (Harness other = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, other.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
            }
        }

        [Test]
        public void Constructor_ForeignContext_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, MakeContext(new NvencCaptureProcessState()), h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
            }
        }

        [Test]
        public void Constructor_ForeignRegistrySlot_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunChunkContext foreign = MakeContext(new NvencCaptureProcessState());
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, new NvencRunLocalRegistrySlot(foreign), h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
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
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, foreignTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
            }
        }

        [Test]
        public void Constructor_NullDependency_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    null, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, null, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, null, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, null, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, null, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, null, h.BackendJoin, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, null, h.SessionIssue, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, null, h.TraceFreeze, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, null, h.Service));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin, h.SessionIssue, h.TraceFreeze, null));
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
            WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

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
            WaitSettled(h.SettledEvent, "worker did not converge the abandon request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
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
            return new NvencRunChunkContext(MakeIssue(), sink, coordinator, "chunk/foreign");
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
            internal NvencRunPublicationService Service;

            internal CaptureRunInitializationSessionIssue SessionIssue;
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
                Service = new NvencRunPublicationService(State, commitCoordinator, artifactCoordinator);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze, Service);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

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
