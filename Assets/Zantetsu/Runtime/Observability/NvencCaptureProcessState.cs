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
    /// The state is read and advanced with Interlocked/Volatile only; no
    /// blocking lock, wait handle, event, busy spin, callback, or task is
    /// used. Reads perform no allocation, transitions are idempotent and
    /// exception-safe, and this type is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject. The Composition Root, not this
    /// type, creates exactly one instance per process; no singleton or static
    /// Current is forced here.
    /// </remarks>
    internal sealed class NvencCaptureProcessState
    {
        private int _state = (int)NvencCaptureProcessStatus.Running;

        internal NvencCaptureProcessState()
        {
        }

        internal NvencCaptureProcessStatus State =>
            (NvencCaptureProcessStatus)Volatile.Read(ref _state);

        internal bool IsAccepting => State == NvencCaptureProcessStatus.Running;

        internal bool IsDraining => State == NvencCaptureProcessStatus.Draining;

        internal bool IsPoisoned =>
            State == NvencCaptureProcessStatus.PoisonedUntilProcessRestart;

        internal bool TryBeginDrain()
        {
            return Interlocked.CompareExchange(
                ref _state,
                (int)NvencCaptureProcessStatus.Draining,
                (int)NvencCaptureProcessStatus.Running) == (int)NvencCaptureProcessStatus.Running;
        }

        internal bool TryPoison()
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

        /// <summary>
        /// Atomic admission linearization point. It is a full-fence
        /// read-modify-write on the control state, totally ordered with
        /// <see cref="TryBeginDrain"/> and <see cref="TryPoison"/>: it
        /// succeeds only while the state is Running, so an admission that
        /// succeeds here is guaranteed to precede any later drain or poison
        /// transition, and an admission attempted after such a transition
        /// observes the new state and fails. It performs no state transition
        /// and no allocation.
        /// </summary>
        internal bool TryAdmit()
        {
            return Interlocked.CompareExchange(
                ref _state,
                (int)NvencCaptureProcessStatus.Running,
                (int)NvencCaptureProcessStatus.Running) == (int)NvencCaptureProcessStatus.Running;
        }
    }
}
