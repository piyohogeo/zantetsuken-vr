using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Main-thread-only pump that starts a GPU readback for the FIFO head of a
    /// <see cref="CaptureFrameRequestQueue"/>, resolving the source render
    /// texture from the request's registered lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryStartNext"/> peeks the queue head, matches it against
    /// <see cref="CaptureFrameRenderTargetLeaseRegistry"/>, obtains the
    /// non-owning render texture through
    /// <see cref="CaptureFrameRenderTargetPool.GetRenderTexture"/>, and starts
    /// the readback via <see cref="UnityRenderTextureReadbackDispatcher"/>.
    /// </para>
    /// <para>
    /// Starting a readback is non-transactional: if dequeuing fails or the
    /// dequeued request does not match the peeked request after the readback
    /// has already started, an <see cref="InvalidOperationException"/> is
    /// thrown and the already-started readback is <b>not</b> rolled back. The
    /// lease is not removed from the registry and is not returned to the pool.
    /// The completion contract is unchanged:
    /// <c>buffer use → Dispatcher.Release → LeaseRegistry.TryRemove →
    /// RenderTargetPool.Return</c>.
    /// </para>
    /// <para>
    /// The pump owns, disposes, releases, destroys, or clears none of its
    /// dependencies, leases, or render textures. It performs no drawing, blit,
    /// trace recording, ID generation, or file I/O. It is main-thread only and
    /// not thread-safe; the hot path performs no LINQ, enumeration, logging,
    /// string generation, or managed allocation.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetReadbackPump
    {
        private readonly CaptureFrameRequestQueue _requestQueue;
        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;
        private readonly CaptureFrameRenderTargetPool _renderTargetPool;

        public CaptureFrameRenderTargetReadbackPump(
            CaptureFrameRequestQueue requestQueue,
            UnityRenderTextureReadbackDispatcher dispatcher,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry,
            CaptureFrameRenderTargetPool renderTargetPool)
        {
            if (requestQueue == null)
            {
                throw new ArgumentNullException(nameof(requestQueue));
            }

            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (leaseRegistry == null)
            {
                throw new ArgumentNullException(nameof(leaseRegistry));
            }

            if (renderTargetPool == null)
            {
                throw new ArgumentNullException(nameof(renderTargetPool));
            }

            _requestQueue = requestQueue;
            _dispatcher = dispatcher;
            _leaseRegistry = leaseRegistry;
            _renderTargetPool = renderTargetPool;
        }

        public int PendingCount => _requestQueue.Count;

        public int ActiveReadbackCount => _dispatcher.ActiveCount;

        /// <summary>
        /// Starts one readback for the queue head if possible. Returns false
        /// when the queue is empty or when the dispatcher/readback buffers are
        /// full, leaving the queue, registry, and pool state unchanged.
        /// </summary>
        public bool TryStartNext()
        {
            if (!_requestQueue.TryPeek(out CaptureFrameRequest request))
            {
                return false;
            }

            if (!_leaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease lease))
            {
                throw new InvalidOperationException("The queue head request has no registered render target lease.");
            }

            RenderTexture renderTexture = _renderTargetPool.GetRenderTexture(lease);

            if (!_dispatcher.TryStart(request, renderTexture))
            {
                return false;
            }

            if (!_requestQueue.TryDequeue(out CaptureFrameRequest dequeued))
            {
                throw new InvalidOperationException("The queue head could not be dequeued after the readback started.");
            }

            if (!dequeued.IdenticalTo(request))
            {
                throw new InvalidOperationException("The dequeued request does not match the peeked request.");
            }

            return true;
        }
    }
}
