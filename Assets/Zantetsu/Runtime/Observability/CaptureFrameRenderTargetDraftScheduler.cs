using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects an accepted, pending capture frame draft to a request queue slot
    /// and a render target lease. Unlike the record path, a full request queue
    /// never terminates a draft as dropped: the draft stays pending and the
    /// caller may retry with the same draft and lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On entry the lease is owned by the caller. On a <c>true</c> result the
    /// lease's logical ownership transfers to the lease registry (and the pool
    /// keeps the slot rented). On a normal <c>false</c> or a rolled-back
    /// exception the lease returns to the caller, who is responsible for
    /// returning it to the pool. On a queue-full rejection the draft remains
    /// <see cref="CaptureFrameDraftStatus.Pending"/> in the registry and no
    /// <c>CaptureFrameDropped</c> event is generated.
    /// </para>
    /// <para>
    /// The draft is owned and registered by the admission coordinator, not by
    /// this scheduler; this scheduler never deletes or removes the draft entry,
    /// never changes its status to dropped, never frees its pending slot, and
    /// never dequeues requests, generates IDs, calls the factory, or reserves
    /// or commits registry capacity. It never calls
    /// <c>CaptureFrameRenderTargetPool.Return</c> and never records admission,
    /// dropped, or encoded trace events.
    /// </para>
    /// <para>
    /// Main-thread only and not thread-safe. The queue is also main-thread
    /// only, so the queue's full/available state is assumed not to change
    /// between the capacity check and the actual enqueue. It does not implement
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameRenderTargetDraftScheduler
    {
        private readonly CaptureFrameDraftRegistry _draftRegistry;
        private readonly CaptureFrameRequestQueue _requestQueue;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;
        private readonly CaptureFrameTraceObserver _traceObserver;

        internal CaptureFrameRenderTargetDraftScheduler(
            CaptureFrameDraftRegistry draftRegistry,
            CaptureFrameRequestQueue requestQueue,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry,
            CaptureFrameTraceObserver traceObserver)
        {
            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (requestQueue == null)
            {
                throw new ArgumentNullException(nameof(requestQueue));
            }

            if (leaseRegistry == null)
            {
                throw new ArgumentNullException(nameof(leaseRegistry));
            }

            if (traceObserver == null)
            {
                throw new ArgumentNullException(nameof(traceObserver));
            }

            _draftRegistry = draftRegistry;
            _requestQueue = requestQueue;
            _leaseRegistry = leaseRegistry;
            _traceObserver = traceObserver;
        }

        /// <summary>The draft registry shared with the scheduling path.</summary>
        internal CaptureFrameDraftRegistry Registry => _draftRegistry;

        internal bool TrySchedule(CaptureFrameDraft draft, in CaptureFrameRenderTargetLease lease)
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            if (!_draftRegistry.TryGet(draft.Request, out CaptureFrameDraft registeredDraft, out CaptureFrameDraftStatus status))
            {
                throw new InvalidOperationException("The draft is not registered in the draft registry.");
            }

            if (!ReferenceEquals(registeredDraft, draft))
            {
                throw new InvalidOperationException("The registered draft is not the supplied draft instance.");
            }

            if (status != CaptureFrameDraftStatus.Pending)
            {
                throw new InvalidOperationException("The draft is not pending.");
            }

            if (!_leaseRegistry.TryRegister(draft.Request, lease))
            {
                // Lease registry full: the request queue and trace observer are
                // not touched and the lease remains owned by the caller.
                return false;
            }

            if (_requestQueue.Count >= _requestQueue.Capacity)
            {
                // Route through the queue's normal full rejection so only its
                // rejected counter increments. No CaptureFrameDropped event is
                // generated and the draft stays pending.
                if (_requestQueue.TryEnqueue(draft.Request))
                {
                    // The queue was reported full but accepted the request:
                    // fail closed without speculative dequeue or lease rollback.
                    throw new InvalidOperationException("The request queue accepted a request it had reported as full.");
                }

                RollbackLease(draft.Request, lease);
                return false;
            }

            bool enqueued = false;
            try
            {
                _traceObserver.RecordQueued(draft.Request.TraceContext);
                enqueued = _requestQueue.TryEnqueue(draft.Request);
            }
            catch (Exception ex)
            {
                // RecordQueued or TryEnqueue threw before the queue took
                // ownership of the request.
                Exception rollbackException = null;
                try
                {
                    RollbackLease(draft.Request, lease);
                }
                catch (Exception re)
                {
                    rollbackException = re;
                }

                if (rollbackException != null)
                {
                    throw new AggregateException(ex, rollbackException);
                }

                ExceptionDispatchInfo.Capture(ex).Throw();
            }

            if (!enqueued)
            {
                // The queue reported an available slot but refused the enqueue.
                // The request is confirmed not enqueued, so the lease rollback
                // is safe.
                RollbackLease(draft.Request, lease);
                throw new InvalidOperationException("The request queue refused an available slot after the queued trace may already have been recorded.");
            }

            return true;
        }

        private void RollbackLease(in CaptureFrameRequest request, in CaptureFrameRenderTargetLease expectedLease)
        {
            if (!_leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removedLease))
            {
                throw new InvalidOperationException("Rollback failed: no render target lease is registered for the scheduled capture frame.");
            }

            if (!removedLease.IdenticalTo(expectedLease))
            {
                throw new InvalidOperationException("Rollback failed: the removed render target lease does not match the registered lease.");
            }
        }
    }
}
