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

        // ---- Constructor correlation ----

        [Test]
        public void Constructor_ForeignProcessState_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    new NvencCaptureProcessState(), h.SubmitWorker, h.Worker, h.Context, h.Slot));
            }
        }

        [Test]
        public void Constructor_ForeignSubmitWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, BuildSubmitWorker(new NvencCaptureProcessState()), h.Worker, h.Context, h.Slot));
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
                    h.State, BuildSubmitWorker(h.State), h.Worker, h.Context, h.Slot));
            }
        }

        [Test]
        public void Constructor_ForeignOutputWorker_Rejected()
        {
            using (Harness h = Harness.Create())
            using (Harness other = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, other.Worker, h.Context, h.Slot));
            }
        }

        [Test]
        public void Constructor_ForeignContext_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, MakeContext(new NvencCaptureProcessState()), h.Slot));
            }
        }

        [Test]
        public void Constructor_ForeignRegistrySlot_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunChunkContext foreign = MakeContext(new NvencCaptureProcessState());
                Assert.Throws<ArgumentException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, new NvencRunLocalRegistrySlot(foreign)));
            }
        }

        [Test]
        public void Constructor_NullDependency_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    null, h.SubmitWorker, h.Worker, h.Context, h.Slot));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, null, h.Worker, h.Context, h.Slot));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, null, h.Context, h.Slot));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, null, h.Slot));
                Assert.Throws<ArgumentNullException>(() => new NvencCaptureRunCoordinator(
                    h.State, h.SubmitWorker, h.Worker, h.Context, null));
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

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

                return NvencOutputWorkerTeardownReceipt.Issue(this);
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

            internal NvencCaptureWorkSlotPool SubmitWorkSlots;
            internal NvencEncodeSampleSlotPool SubmitSampleSlots;
            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCreditPool;
            internal NvencFrameCompletionCreditPool SubmitFrameCompletionCredits;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencCaptureRunCoordinator RunCoordinator;
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

                SubmitWorkSlots = new NvencCaptureWorkSlotPool(State);
                SubmitSampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitSyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitToOutputCreditPool = new NvencSubmitToOutputCreditPool(State);
                SubmitFrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                SubmitSubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                SubmitReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, SubmitWorkSlots, SubmitSampleSlots, SubmitSyncSlots,
                    SubmitToOutputCreditPool, SubmitFrameCompletionCredits,
                    new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmitSubmissionQueue, OutputQueue,
                    SubmitWorkSlots, SubmitSampleSlots, SubmitReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                RunCoordinator = new NvencCaptureRunCoordinator(State, SubmitWorker, Worker, Context, Slot);

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
