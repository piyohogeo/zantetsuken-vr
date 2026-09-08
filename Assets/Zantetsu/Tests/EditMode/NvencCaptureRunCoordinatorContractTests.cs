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
                // foreign Run must be rejected by the verification. Only the
                // teardown's own normal return constructs a receipt, so this
                // corruption simulates a broken reference directly.
                NvencMainThreadTextureTeardownReceipt receipt = new NvencMainThreadTextureTeardownReceipt(
                    h.MainThreadTeardown, h.Context);
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
        public void BackendJoin_SubmitWorkerNotStopped_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h, disposeSubmitWorker: false);

                // The receipt is obtained only by running the exact teardown,
                // so no side-effect-free path forges it.
                NvencMainThreadTextureTeardownReceipt receipt = h.MainThreadTeardown.TearDown();

                Assert.That(h.SubmitWorker.IsStopped, Is.False);
                Assert.That(h.BackendJoin.TryJoin(receipt), Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());

                // Publishing the stop evidence lets the same boundary join.
                h.SubmitWorker.Dispose();
                Assert.That(h.BackendJoin.TryJoin(receipt), Is.True);
                Assert.That(h.BackendJoin.Joined, Is.True);
            }
        }

        [Test]
        public void BackendJoin_OutputWorkerNotStopped_Refuses()
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

                ManualResetEventSlim entered = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
                h.Teardown.Entered = entered;
                h.Teardown.Release = release;

                h.SettledEvent.Reset();
                Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "worker teardown did not enter");

                h.SubmitWorker.Dispose();

                NvencMainThreadTextureTeardownReceipt receipt = h.MainThreadTeardown.TearDown();

                Assert.That(h.Worker.IsStopped, Is.False);
                Assert.That(h.BackendJoin.TryJoin(receipt), Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);

                release.Set();
                WaitSettled(h.SettledEvent, "worker did not complete the teardown after release");
                h.WaitForPhysicalStop("worker did not physically exit");
                Assert.That(h.Worker.TeardownCompleted, Is.True);

                Assert.That(h.BackendJoin.TryJoin(receipt), Is.True);
                Assert.That(h.BackendJoin.Joined, Is.True);
            }
        }

        [Test]
        public void BackendJoin_Poisoned_RefusesNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);

                NvencMainThreadTextureTeardownReceipt receipt = h.MainThreadTeardown.TearDown();

                h.State.TryPoison();
                Assert.That(h.State.IsPoisoned, Is.True);

                Assert.That(h.BackendJoin.TryJoin(receipt), Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());
            }
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
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, other.BackendJoin));
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
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, foreignTeardownJoin));
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
        public void BackendJoin_NullOrForeignReceipt_RefusesNoDispose()
        {
            using (Harness h = Harness.Create())
            {
                StopFinalizedBackend(h);

                // A null receipt is refused without touching either worker.
                Assert.That(h.BackendJoin.TryJoin(null), Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());

                // A receipt produced by a foreign teardown bound to the same
                // context is refused: the exact issuer is part of the join
                // precondition.
                FakeMainThreadTeardown foreign = new FakeMainThreadTeardown { BoundContext = h.Context };
                NvencMainThreadTextureTeardownReceipt foreignReceipt = foreign.TearDown();
                Assert.That(foreignReceipt.IsIssuedFor(foreign, h.Context), Is.True);

                Assert.That(h.BackendJoin.TryJoin(foreignReceipt), Is.False);
                Assert.That(h.BackendJoin.Joined, Is.False);
                Assert.DoesNotThrow(() => h.Worker.Notify());
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

        // ---- Constructor correlation ----

        [Test]
        public void Constructor_ForeignProcessState_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    new NvencCaptureProcessState(), h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
            }
        }

        [Test]
        public void Constructor_ForeignSubmitWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, BuildSubmitWorker(new NvencCaptureProcessState()), h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
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
                    h.State, BuildSubmitWorker(h.State), h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
            }
        }

        [Test]
        public void Constructor_ForeignOutputWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            using (Harness other = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, other.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
            }
        }

        [Test]
        public void Constructor_ForeignContext_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, MakeContext(new NvencCaptureProcessState()), h.Slot, h.MainThreadTeardown, h.BackendJoin));
            }
        }

        [Test]
        public void Constructor_ForeignRegistrySlot_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunChunkContext foreign = MakeContext(new NvencCaptureProcessState());
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, new NvencRunLocalRegistrySlot(foreign), h.MainThreadTeardown, h.BackendJoin));
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
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, foreignTeardown, h.BackendJoin));
            }
        }

        [Test]
        public void Constructor_NullDependency_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    null, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, null, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, null, h.Context, h.Slot, h.MainThreadTeardown, h.BackendJoin));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, null, h.Slot, h.MainThreadTeardown, h.BackendJoin));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, null, h.MainThreadTeardown, h.BackendJoin));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, null, h.BackendJoin));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, h.Slot, h.MainThreadTeardown, null));
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
                Context = new NvencRunChunkContext(MakeIssue(), Sink, FinalizationCoordinator, "chunk/0");
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
                RunCoordinator = new NvencCaptureRunCoordinator(State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin);

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
            }
        }
    }
}
