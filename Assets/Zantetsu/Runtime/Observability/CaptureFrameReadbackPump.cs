using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Drives queued capture frame requests into the GPU readback dispatcher by
    /// peeking the queue head, starting the readback, and dequeuing only after
    /// the readback was successfully started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only. The queue and dispatcher are both main-thread only, so
    /// it is assumed that neither can change between the peek and the dequeue;
    /// the dequeued request is therefore always identical to the peeked one.
    /// </para>
    /// <para>
    /// Does not own or dispose the queue, dispatcher, pool, or any render
    /// texture. It records no trace events and does not generate or mutate
    /// capture frame IDs.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameReadbackPump
    {
        private readonly CaptureFrameRequestQueue _queue;
        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;

        public CaptureFrameReadbackPump(
            CaptureFrameRequestQueue queue,
            UnityRenderTextureReadbackDispatcher dispatcher)
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }

            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            _queue = queue;
            _dispatcher = dispatcher;
        }

        public int PendingCount => _queue.Count;

        public int ActiveReadbackCount => _dispatcher.ActiveCount;

        /// <summary>
        /// Peeks the queue head and, if a readback can be started, dequeues the
        /// head. Returns false without touching the dispatcher or source when
        /// the queue is empty, and without changing the queue when the
        /// dispatcher cannot start the readback.
        /// </summary>
        public bool TryStartNext(RenderTexture source)
        {
            if (!_queue.TryPeek(out CaptureFrameRequest request))
            {
                return false;
            }

            if (!_dispatcher.TryStart(request, source))
            {
                return false;
            }

            if (!_queue.TryDequeue(out CaptureFrameRequest dequeued))
            {
                throw new InvalidOperationException("Queue became empty between peek and dequeue.");
            }

            if (!request.IdenticalTo(dequeued))
            {
                throw new InvalidOperationException("Queue head changed between peek and dequeue.");
            }

            return true;
        }
    }
}
