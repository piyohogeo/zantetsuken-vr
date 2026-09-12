using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-static, process-wide fail-stop authority shared by the Phase 0.11
    /// NVENC Backend, Run Coordinator, and Publication Service. It owns no
    /// NVENC session, resource handle, texture, queue, pool, work slot,
    /// exception, OS lock, artifact, context, registry, or cleanup ledger; it
    /// holds only the lifecycle control state. A single small atomic state
    /// machine advances Running to Draining or to PoisonedUntilProcessRestart,
    /// and poison always wins over a concurrent drain. Draining returns to
    /// Running only on a normal or controlled Run completion, through
    /// <see cref="TryCompleteRunWhileResourceResolutionHeld"/>, which the Run
    /// Coordinator calls while holding the shared gate;
    /// PoisonedUntilProcessRestart is irreversible until the process restarts
    /// and no reset or unpoison entry exists.
    /// </summary>
    /// <remarks>
    /// The state is read and advanced with Interlocked/Volatile only, and a
    /// short private gate orders each submission admission and each source
    /// resource resolution against the Drain, Poison, and Run Abandoned
    /// transitions. Admission and resource resolution never wait for the gate;
    /// they acquire non-waiting and fail if the gate is held. The lifecycle
    /// transitions wait only for the preceding short critical section. Run
    /// Abandoned is a monotonic flag recorded inside the same gate that stops
    /// new admission by advancing Running to Draining; a normal
    /// <see cref="TryBeginDrain"/> never sets it, and a Run completion clears
    /// it before republishing Running so the next Run never observes the
    /// previous Run's abandonment. Reads perform no allocation,
    /// transitions are idempotent and exception-safe, and this type is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject. The
    /// Composition Root, not this type, creates exactly one instance per
    /// process; no singleton or static Current is forced here. One Output
    /// Worker may be bound to be woken whenever the shared gate is released,
    /// because a collector that could not take it keeps its record and parks
    /// with nothing else able to ask it to try again; the wake is a coalescing
    /// hint and this type still owns no worker, queue, or record.
    /// </remarks>
    internal sealed class NvencCaptureProcessState
    {
        private int _state = (int)NvencCaptureProcessStatus.Running;

        // The one Output Worker woken when this shared gate is released. An
        // Output Worker that failed to take the gate holds its record and
        // parks, and nothing else would ever ask it to try again.
        private volatile NvencOrderedOutputWorkerService _gateReleaseWaiter;

        // Monotonic Run Abandoned flag, recorded only inside the shared gate.
        private int _runAbandoned;

        // Short private gate serializing a submission admission with the Drain
        // and Poison transitions. Admission uses non-waiting TryEnter; the
        // lifecycle transitions use a short blocking Enter.
        private readonly object _admissionGate = new object();

        internal NvencCaptureProcessState()
        {
        }

        internal NvencCaptureProcessStatus State =>
            (NvencCaptureProcessStatus)Volatile.Read(ref _state);

        internal bool IsAccepting => State == NvencCaptureProcessStatus.Running;

        internal bool IsDraining => State == NvencCaptureProcessStatus.Draining;

        internal bool IsPoisoned =>
            State == NvencCaptureProcessStatus.PoisonedUntilProcessRestart;

        internal bool IsRunAbandoned =>
            Volatile.Read(ref _runAbandoned) != 0;

        /// <summary>
        /// Acquires the short admission gate without waiting and succeeds only
        /// while the state is Running. On success the gate remains held until
        /// <see cref="EndAdmission"/> is called; the caller must complete its
        /// surface transfer and Submission Queue enqueue within the gate and
        /// then release it. On failure the gate is not held and the caller must
        /// not enqueue. The gate is shared with <see cref="TryBeginDrain"/> and
        /// <see cref="TryPoison"/>, which wait for it, so an admission that
        /// holds it is ordered before any later transition, and a transition
        /// that holds it has already advanced the state.
        /// </summary>
        internal bool TryBeginAdmission()
        {
            bool lockTaken = false;
            Monitor.TryEnter(_admissionGate, ref lockTaken);

            if (!lockTaken)
            {
                return false;
            }

            if (Volatile.Read(ref _state) != (int)NvencCaptureProcessStatus.Running)
            {
                ReleaseSharedGate();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Releases the admission gate acquired by a successful
        /// <see cref="TryBeginAdmission"/>.
        /// </summary>
        internal void EndAdmission()
        {
            ReleaseSharedGate();
        }

        /// <summary>
        /// Acquires the short resource-resolution gate without waiting and
        /// succeeds while the state is Running or Draining. It fails while the
        /// state is PoisonedUntilProcessRestart or when the gate is already
        /// held by another thread; in either case the gate is not held on
        /// return and the caller must change nothing and may retry later. On
        /// success the gate remains held until
        /// <see cref="EndResourceResolution"/> is called. The gate is the same
        /// one shared with <see cref="TryBeginAdmission"/>,
        /// <see cref="TryBeginDrain"/>, and <see cref="TryPoison"/>, so the
        /// resource release is serialized with those operations.
        /// </summary>
        internal bool TryBeginResourceResolution()
        {
            bool lockTaken = false;
            Monitor.TryEnter(_admissionGate, ref lockTaken);

            if (!lockTaken)
            {
                return false;
            }

            if (Volatile.Read(ref _state) == (int)NvencCaptureProcessStatus.PoisonedUntilProcessRestart)
            {
                ReleaseSharedGate();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Releases the resource-resolution gate acquired by a successful
        /// <see cref="TryBeginResourceResolution"/>.
        /// </summary>
        internal void EndResourceResolution()
        {
            ReleaseSharedGate();
        }

        /// <summary>
        /// Binds the one Output Worker woken whenever this shared gate is
        /// released. Exactly once, at composition.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A collector that has already taken a record from the queue can be
        /// stopped by nothing more than this gate being momentarily held by
        /// someone else: it keeps the record and parks, and no enqueue, drain,
        /// or completion is coming to wake it. The gate's own release is the
        /// only event that can say "try again", which is why the binding lives
        /// here rather than at either worker.
        /// </para>
        /// <para>
        /// It is closed to one worker: there is no list, no registry, and no
        /// way to re-point it. Without a binding this type behaves exactly as
        /// before and wakes nobody.
        /// </para>
        /// </remarks>
        internal void BindResourceResolutionReleaseNotification(
            NvencOrderedOutputWorkerService outputWorker)
        {
            if (outputWorker == null)
            {
                throw new ArgumentNullException(nameof(outputWorker));
            }

            if (_gateReleaseWaiter != null)
            {
                throw new InvalidOperationException(
                    "This process state already wakes an Output Worker when the shared gate is released; the binding is made once.");
            }

            _gateReleaseWaiter = outputWorker;
        }

        /// <summary>
        /// Releases the binding, and only once the exact bound Output Worker
        /// has physically stopped.
        /// </summary>
        /// <remarks>
        /// Unbinding a worker that is still running would drop the one wake a
        /// record it is holding still needs, so a running worker is refused
        /// here rather than trusted to the caller's ordering. A foreign worker
        /// is refused too.
        /// </remarks>
        internal void UnbindResourceResolutionReleaseNotification(
            NvencOrderedOutputWorkerService outputWorker)
        {
            if (outputWorker == null)
            {
                throw new ArgumentNullException(nameof(outputWorker));
            }

            if (!ReferenceEquals(_gateReleaseWaiter, outputWorker))
            {
                throw new InvalidOperationException(
                    "Only the Output Worker this process state is bound to can release the binding.");
            }

            if (!outputWorker.IsStopped)
            {
                throw new InvalidOperationException(
                    "The binding is released only after the Output Worker has physically stopped; releasing it earlier would drop the wake a held record still needs.");
            }

            _gateReleaseWaiter = null;
        }

        /// <summary>
        /// Releases the one shared gate and then tells the bound Output Worker
        /// that it is free.
        /// </summary>
        /// <remarks>
        /// The hint is the same coalescing state-change hint the workers
        /// already use: it carries no count, acknowledgement, or reason, and
        /// many releases may collapse into one wake. It is delivered after the
        /// monitor is released and never before, so a worker it wakes can
        /// actually take the gate. A worker caught between its failed
        /// acquisition and its park loses nothing either: it resets its signal
        /// and re-checks for progress before waiting, and by then the gate is
        /// free.
        /// </remarks>
        private void ReleaseSharedGate()
        {
            Monitor.Exit(_admissionGate);

            NvencOrderedOutputWorkerService waiter = _gateReleaseWaiter;
            if (waiter == null)
            {
                return;
            }

            try
            {
                waiter.Notify();
            }
            catch (ObjectDisposedException)
            {
                // Reachable only for a worker disposed between the read above
                // and this call, which has physically stopped and has nothing
                // left to wake. This release runs inside callers' finally
                // blocks, so the hint is dropped rather than replacing the
                // failure those callers are already reporting.
            }
        }

        /// <summary>
        /// Acquires the short submit-step gate without waiting and succeeds
        /// while the state is Running or Draining. It fails while the state is
        /// PoisonedUntilProcessRestart or when the gate is already held by
        /// another thread; in either case the gate is not held on return and
        /// the caller must change nothing and may retry later. On success the
        /// gate remains held until <see cref="EndSubmitStep"/> is called, so
        /// the source handoff, the submit call, the output record build, and
        /// the output enqueue are serialized with the poison transition, whose
        /// blocking acquisition waits for this short critical section.
        /// </summary>
        internal bool TryBeginSubmitStep()
        {
            bool lockTaken = false;
            Monitor.TryEnter(_admissionGate, ref lockTaken);

            if (!lockTaken)
            {
                return false;
            }

            if (Volatile.Read(ref _state) == (int)NvencCaptureProcessStatus.PoisonedUntilProcessRestart)
            {
                ReleaseSharedGate();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Releases the submit-step gate acquired by a successful
        /// <see cref="TryBeginSubmitStep"/>.
        /// </summary>
        internal void EndSubmitStep()
        {
            ReleaseSharedGate();
        }

        /// <summary>
        /// Blocking settlement entry used only by the Output Worker to
        /// serialize the post-teardown Receipt verification and normal-stop
        /// evidence publication with a concurrent Poison transition. Unlike
        /// <see cref="TryBeginSubmitStep"/>, it blocks on the shared gate until
        /// the current critical section finishes (a transient gate holder is
        /// waited on, never spun on), then succeeds while Running or Draining
        /// and fails while Poisoned. On success the gate remains held until
        /// <see cref="EndSettlement"/>; on failure the gate is not held.
        /// </summary>
        internal bool TryBeginSettlement()
        {
            Monitor.Enter(_admissionGate);

            if (Volatile.Read(ref _state) == (int)NvencCaptureProcessStatus.PoisonedUntilProcessRestart)
            {
                ReleaseSharedGate();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Releases the settlement gate acquired by a successful
        /// <see cref="TryBeginSettlement"/>.
        /// </summary>
        internal void EndSettlement()
        {
            ReleaseSharedGate();
        }

        internal bool TryBeginDrain()
        {
            Monitor.Enter(_admissionGate);
            try
            {
                return Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencCaptureProcessStatus.Draining,
                    (int)NvencCaptureProcessStatus.Running) == (int)NvencCaptureProcessStatus.Running;
            }
            finally
            {
                ReleaseSharedGate();
            }
        }

        /// <summary>
        /// Atomically records Run Abandoned and stops new admission inside the
        /// shared gate. While Running the state advances to Draining; while
        /// already Draining only the abandoned flag is confirmed. Poison
        /// performs no progress. The call is monotonic and idempotent, and a
        /// normal <see cref="TryBeginDrain"/> never sets the flag.
        /// </summary>
        internal bool TryBeginRunAbandoned()
        {
            Monitor.Enter(_admissionGate);
            try
            {
                if (Volatile.Read(ref _state) == (int)NvencCaptureProcessStatus.PoisonedUntilProcessRestart)
                {
                    return false;
                }

                Volatile.Write(ref _runAbandoned, 1);

                Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencCaptureProcessStatus.Draining,
                    (int)NvencCaptureProcessStatus.Running);

                return true;
            }
            finally
            {
                ReleaseSharedGate();
            }
        }

        /// <summary>
        /// Returns Draining to Running when a Run has finished normally or in a
        /// controlled failure, so the next Run can be admitted. It is not an
        /// unpoison: PoisonedUntilProcessRestart is irreversible until the
        /// process restarts, and Running is left alone.
        /// </summary>
        /// <remarks>
        /// The caller must already hold the shared gate through
        /// <see cref="TryBeginResourceResolution"/>, so the completion is
        /// ordered against admission, drain, and poison exactly like every
        /// other lifecycle transition; calling it without the gate is a
        /// programming error and throws
        /// <see cref="InvalidOperationException"/>. The Run Abandoned flag is
        /// cleared before Running is published, so a Run admitted right after
        /// this never observes the previous Run's abandonment. This type
        /// verifies no Worker, Service, lease, Registry, or disposition: the
        /// caller owns that evidence.
        /// </remarks>
        internal bool TryCompleteRunWhileResourceResolutionHeld()
        {
            if (!Monitor.IsEntered(_admissionGate))
            {
                throw new InvalidOperationException(
                    "Run completion requires the shared resource-resolution gate.");
            }

            if (Volatile.Read(ref _state) != (int)NvencCaptureProcessStatus.Draining)
            {
                return false;
            }

            // Cleared before Running is published, never after.
            Volatile.Write(ref _runAbandoned, 0);

            return Interlocked.CompareExchange(
                ref _state,
                (int)NvencCaptureProcessStatus.Running,
                (int)NvencCaptureProcessStatus.Draining) == (int)NvencCaptureProcessStatus.Draining;
        }

        internal bool TryPoison()
        {
            Monitor.Enter(_admissionGate);
            try
            {
                if (Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencCaptureProcessStatus.PoisonedUntilProcessRestart,
                    (int)NvencCaptureProcessStatus.Running) == (int)NvencCaptureProcessStatus.Running)
                {
                    return true;
                }

                return Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencCaptureProcessStatus.PoisonedUntilProcessRestart,
                    (int)NvencCaptureProcessStatus.Draining) == (int)NvencCaptureProcessStatus.Draining;
            }
            finally
            {
                ReleaseSharedGate();
            }
        }
    }
}
