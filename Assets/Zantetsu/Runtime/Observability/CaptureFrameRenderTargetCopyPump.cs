using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Copies the selected image rectangle of a caller-drawn Unity
    /// <see cref="RenderTexture"/> into the render target registered for the
    /// request queue head, so a frame can be drawn to a working texture and then
    /// transferred to the capture target before the readback starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryCopyNext"/> peeks the request queue head exactly once and
    /// returns <c>false</c> without touching the source, the registries, the
    /// pool, the profiler, or the GPU when the queue is empty. When the queue is
    /// non-empty the head request is the source of truth: its registered lease
    /// is resolved through
    /// <see cref="CaptureFrameRenderTargetLeaseRegistry.TryGet"/> (failing
    /// closed with <see cref="InvalidOperationException"/> when no lease is
    /// registered, and preserving the registry's existing exception when the
    /// same capture frame ID is registered with a different request), its render
    /// texture is resolved through
    /// <see cref="CaptureFrameRenderTargetPool.GetRenderTexture"/> (delegating
    /// lease validation to the pool), and its
    /// <see cref="CaptureFrameRequest.ImageRect"/> region is copied from the
    /// source to the target at the same coordinates with no scaling, resolve, or
    /// color conversion.
    /// </para>
    /// <para>
    /// The source is validated only when the queue is non-empty and only before
    /// any GPU operation: it must be non-null and created, a
    /// <see cref="TextureDimension.Tex2D"/> texture with
    /// <see cref="RenderTexture.volumeDepth"/> 1 and
    /// <see cref="RenderTexture.antiAliasing"/> 1, large enough to contain the
    /// request's image rectangle, matching the target's
    /// <see cref="RenderTexture.graphicsFormat"/>, and not the target itself.
    /// The registered target must also fully contain the image rectangle;
    /// otherwise the request/pool mismatch fails closed with
    /// <see cref="InvalidOperationException"/> before any GPU operation.
    /// </para>
    /// <para>
    /// Ownership and state are unchanged by a copy: the queue head is never
    /// dequeued, the lease stays registered, the render target stays rented, and
    /// the source and target are never released, destroyed, disposed, or
    /// cleared. A successful copy is followed by
    /// <see cref="CaptureFrameRenderTargetPipelineCoordinator.TryStartNextReadback"/>,
    /// which dequeues the head and starts the readback.
    /// </para>
    /// <para>
    /// A GPU copy exception is never translated and the queue, the request, and
    /// the lease registration are not rolled back. The target's contents are
    /// unspecified after such an exception, so the caller must re-copy the full
    /// image rectangle from a valid source before starting the readback. Once
    /// the readback has started the queue head is gone and the copy cannot be
    /// retried; the caller must not re-render the target between copy and GPU
    /// completion/collection.
    /// </para>
    /// <para>
    /// This pump owns nothing: not the queue, registry, pool, lease, render
    /// texture, or dispatcher. It is main-thread only and <b>not</b>
    /// thread-safe, and performs no dequeue, remove, return, release, destroy,
    /// dispose, clear, LINQ, enumeration, logging, or string generation on the
    /// hot path. It is not a MonoBehaviour, singleton, or
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetCopyPump
    {
        private readonly CaptureFrameRequestQueue _requestQueue;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;
        private readonly CaptureFrameRenderTargetPool _renderTargetPool;

        public CaptureFrameRenderTargetCopyPump(
            CaptureFrameRequestQueue requestQueue,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry,
            CaptureFrameRenderTargetPool renderTargetPool)
        {
            if (requestQueue == null)
            {
                throw new ArgumentNullException(nameof(requestQueue));
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
            _leaseRegistry = leaseRegistry;
            _renderTargetPool = renderTargetPool;
        }

        /// <summary>
        /// Copies the queue head request's image rectangle from
        /// <paramref name="source"/> into the render target registered for that
        /// request. Returns <c>false</c> without touching anything when the
        /// queue is empty.
        /// </summary>
        public bool TryCopyNext(RenderTexture source)
        {
            if (!_requestQueue.TryPeek(out CaptureFrameRequest request))
            {
                return false;
            }

            if (!_leaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease lease))
            {
                throw new InvalidOperationException("The queue head request has no registered render target lease.");
            }

            RenderTexture target = _renderTargetPool.GetRenderTexture(lease);

            ValidateSource(source, target, request.ImageRect);

            using (ZantetsuProfilerMarkers.CaptureCopy.Auto())
            {
                Graphics.CopyTexture(
                    source, 0, 0, request.ImageRect.X, request.ImageRect.Y, request.ImageRect.Width, request.ImageRect.Height,
                    target, 0, 0, request.ImageRect.X, request.ImageRect.Y);
            }

            return true;
        }

        private static void ValidateSource(RenderTexture source, RenderTexture target, CaptureImageRect rect)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (!source.IsCreated())
            {
                throw new ArgumentException("Source render texture is not created.", nameof(source));
            }

            if (source.dimension != TextureDimension.Tex2D)
            {
                throw new ArgumentException("Source must be a Tex2D render texture.", nameof(source));
            }

            if (source.volumeDepth != 1)
            {
                throw new ArgumentException("Source volume depth must be 1.", nameof(source));
            }

            if (source.antiAliasing != 1)
            {
                throw new ArgumentException("Source must not use MSAA.", nameof(source));
            }

            if (source.width < rect.X + rect.Width || source.height < rect.Y + rect.Height)
            {
                throw new ArgumentException("The image rectangle must be fully contained within the source.", nameof(source));
            }

            if (target.width < rect.X + rect.Width || target.height < rect.Y + rect.Height)
            {
                throw new InvalidOperationException("The image rectangle must be fully contained within the registered render target.");
            }

            if (source.graphicsFormat != target.graphicsFormat)
            {
                throw new ArgumentException("Source and target graphics formats must match.", nameof(source));
            }

            if (ReferenceEquals(source, target))
            {
                throw new ArgumentException("Source and target must be different render textures.", nameof(source));
            }
        }
    }
}
