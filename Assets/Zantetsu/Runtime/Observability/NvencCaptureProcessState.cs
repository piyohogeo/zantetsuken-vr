using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-static, process-wide fail-stop authority shared by the Phase 0.11
    /// NVENC Backend, Run Coordinator, and Publication Service. It owns no
    /// NVENC session, resource handle, texture, queue, pool, work slot,
    /// exception, OS lock, artifact, context, registry, or cleanup ledger; it
    /// holds only the one-way control state. A single small atomic state
    /// machine advances Running to Draining or to PoisonedUntilProcessRestart
    /// and never moves back, and poison always wins over a concurrent drain.
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
    /// <see cref="TryBeginDrain"/> never sets it. Reads perform no allocation,
    /// transitions are idempotent and exception-safe, and this type is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject. The
    /// Composition Root, not this type, creates exactly one instance per
    /// process; no singleton or static Current is forced here.
    /// </remarks>
    internal sealed class NvencCaptureProcessState
    {
        private int _state = (int)NvencCaptureProcessStatus.Running;

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
                Monitor.Exit(_admissionGate);
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
            Monitor.Exit(_admissionGate);
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
                Monitor.Exit(_admissionGate);
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
            Monitor.Exit(_admissionGate);
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
                Monitor.Exit(_admissionGate);
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
            Monitor.Exit(_admissionGate);
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
                Monitor.Exit(_admissionGate);
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
            Monitor.Exit(_admissionGate);
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
                Monitor.Exit(_admissionGate);
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
                Monitor.Exit(_admissionGate);
            }
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
                Monitor.Exit(_admissionGate);
            }
        }
    }
}
