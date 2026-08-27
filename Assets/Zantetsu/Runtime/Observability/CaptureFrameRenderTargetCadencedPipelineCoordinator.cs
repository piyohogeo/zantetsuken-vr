using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Integrates the split, lease-aware pipeline API with cadenced submission
    /// so that, for the current frame, the submitted record, the registered
    /// render target lease, and the started readback always refer to the same
    /// request. The per-frame order is fixed: advance completed work, submit
    /// the current frame through the cadence gate with the caller's render
    /// target lease, then start a readback for exactly the submitted request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CaptureFrameRenderTargetPipelineCoordinator.AdvancePendingWork"/>
    /// is called exactly once first. If the request queue still holds a
    /// previously unstarted request after advancing, this coordinator fails
    /// closed with <see cref="InvalidOperationException"/> without running
    /// cadence selection, submission, lease registration, or starting a
    /// readback, so the current frame's image is never associated with a stale
    /// request. The advance's side effects are not rolled back, and the
    /// submitted lease is never validated or registered, so it stays owned by
    /// the caller.
    /// </para>
    /// <para>
    /// When the queue is empty the current frame is submitted once through
    /// <see cref="CaptureFrameRenderTargetCadencedSubmissionCoordinator.TrySubmit"/>,
    /// and no readback is started unless the frame is submitted. On
    /// <see cref="CaptureFrameCadencedSubmissionStatus.NotSelected"/> the lease
    /// is never validated or registered and stays owned by the caller. On
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Backpressured"/> the
    /// lease is validated and, depending on where the backpressure occurs, may
    /// be temporarily registered before being rolled back; when it was
    /// registered, a successful rollback returns it to the caller, and on an
    /// ordinary <c>Backpressured</c> return the lease is always caller-owned. A
    /// rollback invariant violation fails closed and the lease is never guessed
    /// or returned.
    /// </para>
    /// <para>
    /// On <see cref="CaptureFrameCadencedSubmissionStatus.Submitted"/> the
    /// accepted record must be non-null, the queue must hold exactly one
    /// request, that request must be identical to the accepted record's
    /// request, the lease registry must hold the accepted request, and the
    /// registered lease must be identical to the submitted lease; otherwise
    /// this coordinator fails closed. Only after these checks does it call
    /// <see cref="CaptureFrameRenderTargetPipelineCoordinator.TryStartNextReadback"/>
    /// once. If the readback starts, the queue must no longer hold the request.
    /// </para>
    /// <para>
    /// On <c>Submitted</c> the lease's ownership has transferred to the lease
    /// registry and the caller must not return it. The lease stays registered
    /// and rented until the readback completes and the pipeline removes and
    /// returns it; this coordinator never removes or returns a lease.
    /// </para>
    /// <para>
    /// When the readback cannot start (dispatcher or buffer pool backpressure)
    /// the record stays in the registry, the request stays in the queue, the
    /// lease stays registered, the cadence state and the issued capture frame
    /// ID are not rolled back, and no drop trace is added. The caller must
    /// retry <see cref="CaptureFrameRenderTargetPipelineCoordinator.TryStartNextReadback"/>
    /// before passing the next frame here; the render target contents are
    /// preserved for that retry.
    /// </para>
    /// <para>
    /// The caller must complete all drawing into the render target backing the
    /// submitted lease <b>before</b> calling this method. This coordinator
    /// performs no drawing, blit, or copy, and does not read or mutate the
    /// render target.
    /// </para>
    /// <para>
    /// This operation is <b>not</b> a transaction: if a later stage throws, the
    /// exception is not translated and the advance, cadence selection, record
    /// registration, request enqueue, lease registration, capture frame ID
    /// issuance, and started GPU readback are not rolled back. Fail-closed
    /// contract violations throw <see cref="InvalidOperationException"/>
    /// without compensating (no file deletion, queue clear, registry
    /// re-register, lease removal, lease return, or GPU request cancellation).
    /// </para>
    /// <para>
    /// Owns, disposes, clears, releases, and returns nothing: not the pipeline
    /// or submission coordinators, the queue, registries, dispatcher, pool,
    /// logger, record, lease, render texture, artifact, or receipt.
    /// Main-thread only and <b>not</b> thread-safe. Not a MonoBehaviour or
    /// singleton, and performs no Unity static API access, logging, or trace.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRenderTargetCadencedPipelineCoordinator
    {
        private readonly CaptureFrameRenderTargetPipelineCoordinator _pipelineCoordinator;
        private readonly CaptureFrameRenderTargetCadencedSubmissionCoordinator _submissionCoordinator;
        private readonly CaptureFrameRequestQueue _requestQueue;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;

        public CaptureFrameRenderTargetCadencedPipelineCoordinator(
            CaptureFrameRenderTargetPipelineCoordinator pipelineCoordinator,
            CaptureFrameRenderTargetCadencedSubmissionCoordinator submissionCoordinator,
            CaptureFrameRequestQueue requestQueue,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry)
        {
            if (pipelineCoordinator == null)
            {
                throw new ArgumentNullException(nameof(pipelineCoordinator));
            }

            if (submissionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(submissionCoordinator));
            }

            if (requestQueue == null)
            {
                throw new ArgumentNullException(nameof(requestQueue));
            }

            if (leaseRegistry == null)
            {
                throw new ArgumentNullException(nameof(leaseRegistry));
            }

            _pipelineCoordinator = pipelineCoordinator;
            _submissionCoordinator = submissionCoordinator;
            _requestQueue = requestQueue;
            _leaseRegistry = leaseRegistry;
        }

        public CaptureFrameCadencedPipelineResult TrySubmit(
            long timestamp,
            long unityFrameId,
            long fixedStepId,
            int threadId,
            long openXRFrameId,
            long slashId,
            long frontEdgeId,
            long objectId,
            uint objectGeneration,
            long taskId,
            in CaptureFrameTiming timing,
            in CapturePoseSample headPose,
            in CapturePoseSample leftControllerPose,
            in CapturePoseSample rightControllerPose,
            int commitPathId,
            in CaptureFrameRenderTargetLease lease)
        {
            // 1. Advance completed work exactly once, first.
            CaptureFramePipelineAdvanceResult advance = _pipelineCoordinator.AdvancePendingWork();

            // 2. Fail closed if a previously unstarted request remains.
            if (_requestQueue.Count != 0)
            {
                throw new InvalidOperationException("The request queue still contains a previously unstarted request after advancing.");
            }

            // 3. Submit the current frame once through the cadence gate.
            CaptureFrameCadencedSubmissionStatus status = _submissionCoordinator.TrySubmit(
                timestamp,
                unityFrameId,
                fixedStepId,
                threadId,
                openXRFrameId,
                slashId,
                frontEdgeId,
                objectId,
                objectGeneration,
                taskId,
                timing,
                headPose,
                leftControllerPose,
                rightControllerPose,
                commitPathId,
                lease,
                out CaptureFrameRecord acceptedRecord);

            if (status != CaptureFrameCadencedSubmissionStatus.Submitted)
            {
                return new CaptureFrameCadencedPipelineResult(advance, status, false, null);
            }

            // 4. Validate the accepted record and queue head before starting.
            if (acceptedRecord == null)
            {
                throw new InvalidOperationException("Submission succeeded but the accepted record is null.");
            }

            if (_requestQueue.Count != 1)
            {
                throw new InvalidOperationException("Submission succeeded but the request queue does not hold exactly one request.");
            }

            if (!_requestQueue.TryPeek(out CaptureFrameRequest head))
            {
                throw new InvalidOperationException("The request queue head could not be peeked.");
            }

            if (!head.IdenticalTo(acceptedRecord.Request))
            {
                throw new InvalidOperationException("The request queue head does not match the accepted record's request.");
            }

            if (!_leaseRegistry.TryGet(acceptedRecord.Request, out CaptureFrameRenderTargetLease registeredLease))
            {
                throw new InvalidOperationException("Submission succeeded but no render target lease is registered for the accepted record's request.");
            }

            if (!registeredLease.IdenticalTo(lease))
            {
                throw new InvalidOperationException("The registered render target lease does not match the submitted lease.");
            }

            // 5. Start the readback exactly once.
            bool started = _pipelineCoordinator.TryStartNextReadback();

            // 6. If the readback started, the queue must no longer hold it.
            if (started && _requestQueue.Count != 0)
            {
                throw new InvalidOperationException("The request queue still holds a request after the readback started.");
            }

            return new CaptureFrameCadencedPipelineResult(advance, status, started, acceptedRecord);
        }
    }
}
