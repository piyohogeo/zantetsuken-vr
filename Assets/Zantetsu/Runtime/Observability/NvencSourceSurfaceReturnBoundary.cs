using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, single-producer/single-consumer handoff that transfers a
    /// completed source surface release from the Submit Worker to the Main
    /// Thread. The worker is the only producer; the main thread is the only
    /// consumer and performs the actual main-thread-only render target return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enqueue and dequeue perform no allocation, waiting, or I/O. A full
    /// boundary fails enqueue immediately, so the producer must retry later.
    /// The boundary itself never touches the render target pool, a texture, a
    /// fence, a query, a command buffer, or any native resource; it only
    /// carries the record reference from one thread to the other.
    /// </para>
    /// </remarks>
    internal sealed class NvencSourceSurfaceReturnBoundary
    {
        private readonly NvencFixedSpscQueue<NvencSubmissionRecord> _queue;

        internal NvencSourceSurfaceReturnBoundary()
        {
            _queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
        }

        internal int Capacity => _queue.Capacity;

        /// <summary>
        /// Producer-side O(1) capacity query. True while the next
        /// <see cref="TryEnqueue"/> is guaranteed to succeed under the strict
        /// single-producer contract. It performs no wait and no allocation.
        /// </summary>
        internal bool CanEnqueue => _queue.CanEnqueue;

        /// <summary>
        /// Producer-side (Submit Worker) non-waiting handoff of one completed
        /// source surface. Returns false without enqueuing when the boundary is
        /// full.
        /// </summary>
        internal bool TryEnqueue(in NvencSubmissionRecord record)
        {
            return _queue.TryEnqueue(record);
        }

        /// <summary>
        /// Consumer-side (Main Thread) non-waiting dequeue of one handed-off
        /// record. Returns false with a default record when empty.
        /// </summary>
        internal bool TryDequeue(out NvencSubmissionRecord record)
        {
            return _queue.TryDequeue(out record);
        }

        /// <summary>
        /// Main-thread drain: releases every handed-off source surface back to
        /// its main-thread-only render target pool. Must only be invoked on the
        /// Main Thread.
        /// </summary>
        internal void DrainAll(Guid backendOwner)
        {
            while (_queue.TryDequeue(out NvencSubmissionRecord record))
            {
                record.Surface.ReleaseFromBackend(backendOwner, record.WorkToken);
            }
        }
    }
}
