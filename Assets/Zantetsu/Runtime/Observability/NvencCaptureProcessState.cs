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
    /// short admission guard serializes each submission admission with the
    /// Drain and Poison transitions so an in-flight enqueue is never overtaken
    /// by a later transition. Reads perform no allocation, transitions are
    /// idempotent and exception-safe, and this type is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject. The
    /// Composition Root, not this type, creates exactly one instance per
    /// process; no singleton or static Current is forced here.
    /// </remarks>
    internal sealed class NvencCaptureProcessState
    {
        private int _state = (int)NvencCaptureProcessStatus.Running;

        // 0 = free, 1 = held by an in-flight admission or transition. The
        // guarded regions are short and non-allocating, so holding this is
        // brief.
        private int _admissionGate;

        internal NvencCaptureProcessState()
        {
        }

        internal NvencCaptureProcessStatus State =>
            (NvencCaptureProcessStatus)Volatile.Read(ref _state);

        internal bool IsAccepting => State == NvencCaptureProcessStatus.Running;

        internal bool IsDraining => State == NvencCaptureProcessStatus.Draining;

        internal bool IsPoisoned =>
            State == NvencCaptureProcessStatus.PoisonedUntilProcessRestart;

        /// <summary>
        /// Acquires the short admission guard and succeeds only while the state
        /// is Running. On success the guard remains held until
        /// <see cref="EndAdmission"/> is called; the caller must complete its
        /// surface transfer and Submission Queue enqueue within the guard and
        /// then release it. On failure the guard is not held and the caller
        /// must not enqueue. The guard is shared with
        /// <see cref="TryBeginDrain"/> and <see cref="TryPoison"/>, so an
        /// admission that holds it is ordered before any later transition, and
        /// a transition that held it earlier has already advanced the state.
        /// </summary>
        internal bool TryBeginAdmission()
        {
            EnterGuard();

            if (Volatile.Read(ref _state) != (int)NvencCaptureProcessStatus.Running)
            {
                ExitGuard();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Releases the admission guard acquired by a successful
        /// <see cref="TryBeginAdmission"/>.
        /// </summary>
        internal void EndAdmission()
        {
            ExitGuard();
        }

        internal bool TryBeginDrain()
        {
            EnterGuard();
            try
            {
                return Interlocked.CompareExchange(
                    ref _state,
                    (int)NvencCaptureProcessStatus.Draining,
                    (int)NvencCaptureProcessStatus.Running) == (int)NvencCaptureProcessStatus.Running;
            }
            finally
            {
                ExitGuard();
            }
        }

        internal bool TryPoison()
        {
            EnterGuard();
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
                ExitGuard();
            }
        }

        private void EnterGuard()
        {
            while (Interlocked.CompareExchange(ref _admissionGate, 1, 0) != 0)
            {
                // The guarded regions are short and non-allocating; the only
                // waiter is a Drain or Poison transition or the admission
                // itself, so this wait is brief.
            }
        }

        private void ExitGuard()
        {
            Volatile.Write(ref _admissionGate, 0);
        }
    }
}
