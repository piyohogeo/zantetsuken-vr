using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, single-producer/single-consumer handoff that transfers a
    /// completed, evidenced source surface release from the Submit Worker to
    /// the Main Thread. The worker is the only producer; the main thread is the
    /// only consumer and applies the release under the resource-resolution gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enqueue and dequeue perform no allocation, waiting, or I/O. A full
    /// boundary fails enqueue immediately, so the producer must retry later.
    /// The boundary itself never touches the render target pool, a texture, a
    /// fence, a query, a command buffer, or any native resource; it only
    /// carries the handoff reference from one thread to the other. A handoff
    /// that has been fetched but not yet applied is retained, so it is never
    /// lost even when the apply attempt fails.
    /// </para>
    /// </remarks>
    internal sealed class NvencSourceSurfaceReturnBoundary
    {
        private readonly NvencFixedSpscQueue<NvencSourceSurfaceReleaseHandoff> _queue;
        private NvencSourceSurfaceReleaseHandoff _pending;
        private bool _hasPending;

        internal NvencSourceSurfaceReturnBoundary()
        {
            _queue = new NvencFixedSpscQueue<NvencSourceSurfaceReleaseHandoff>();
        }

        internal int Capacity => _queue.Capacity;

        /// <summary>
        /// Producer-side O(1) capacity query. True while the next
        /// <see cref="TryEnqueue"/> is guaranteed to succeed under the strict
        /// single-producer contract. It performs no wait and no allocation.
        /// </summary>
        internal bool CanEnqueue => _queue.CanEnqueue;

        /// <summary>
        /// True while a previously fetched handoff is retained and not yet
        /// applied. Diagnostic only.
        /// </summary>
        internal bool HasPending => _hasPending;

        /// <summary>
        /// Producer-side (Submit Worker) non-waiting handoff of one completed,
        /// evidenced source surface release. Returns false without enqueuing
        /// when the boundary is full.
        /// </summary>
        internal bool TryEnqueue(in NvencSourceSurfaceReleaseHandoff handoff)
        {
            return _queue.TryEnqueue(handoff);
        }

        /// <summary>
        /// Consumer-side (Main Thread) non-waiting fetch of the next handoff to
        /// apply. A fetched handoff stays retained until
        /// <see cref="CompletePending"/> is called, so a failed apply never
        /// loses it. Returns false with a default handoff when nothing is
        /// pending.
        /// </summary>
        internal bool TryGetPending(out NvencSourceSurfaceReleaseHandoff handoff)
        {
            if (_hasPending)
            {
                handoff = _pending;
                return true;
            }

            if (_queue.TryDequeue(out handoff))
            {
                _pending = handoff;
                _hasPending = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Consumer-side (Main Thread) acknowledgement that the retained handoff
        /// was fully applied, releasing it so the next one can be fetched.
        /// </summary>
        internal void CompletePending()
        {
            _hasPending = false;
            _pending = default;
        }
    }
}
