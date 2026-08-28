using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects cadenced draft submission to the live-capture start side: copy
    /// the source image into the render target registered for the submitted
    /// draft's request, then start the readback for exactly that request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-frame order is fixed. The request queue must be empty before any
    /// dependency is touched; otherwise this coordinator fails closed with
    /// <see cref="InvalidOperationException"/> without running cadence
    /// selection, submission, ID issuance, source validation, copy, or readback
    /// start, so the current frame's image is never associated with a
    /// previously unstarted request.
    /// </para>
    /// <para>
    /// The frame is then submitted exactly once through
    /// <see cref="CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator.TrySubmit"/>,
    /// passing this coordinator's own <c>out</c> draft straight through so a
    /// scheduler exception still exposes the admitted draft to the caller.
    /// </para>
    /// <para>
    /// On <see cref="CaptureFrameDraftCadencedSubmissionStatus.NotSelected"/>
    /// and <c>AdmissionRejected</c> the draft must be null, the source is not
    /// validated, and no copy or readback start is performed. On
    /// <c>SchedulingBackpressured</c> the draft must be non-null, the source is
    /// not validated, and the lease stays owned by the caller.
    /// </para>
    /// <para>
    /// On <see cref="CaptureFrameDraftCadencedSubmissionStatus.Scheduled"/> the
    /// draft must be non-null, the queue must hold exactly one request, that
    /// request must be identical to the draft's request, the lease registry must
    /// hold the draft's request, and the registered lease must be identical to
    /// the submitted lease; otherwise this coordinator fails closed. Only after
    /// those checks does it call
    /// <see cref="CaptureFrameRenderTargetCopyPump.TryCopyNext"/> exactly once;
    /// a <c>false</c> return is an invariant violation and fails closed, while
    /// a GPU copy or source validation exception is never translated.
    /// </para>
    /// <para>
    /// After the copy it calls
    /// <see cref="CaptureFrameRenderTargetReadbackPump.TryStartNext"/> exactly
    /// once. When the readback starts the queue must be empty. When it cannot
    /// start the queue must still hold exactly the draft's request, the lease
    /// registration and pool rent are maintained, and the copied target
    /// contents are preserved for a direct retry through the existing readback
    /// pump.
    /// </para>
    /// <para>
    /// Ownership: on <c>NotSelected</c>, <c>AdmissionRejected</c>, and
    /// <c>SchedulingBackpressured</c> the lease is caller-owned. On
    /// <c>Scheduled</c> the lease is owned by the lease registry, and it stays
    /// registry-owned even when the copy or readback start fails after
    /// submission. This coordinator never removes, returns, releases, destroys,
    /// disposes, or clears anything.
    /// </para>
    /// <para>
    /// This operation is <b>not</b> a transaction: cadence selection, ID
    /// issuance, admission, scheduling, lease registration, the GPU copy, and a
    /// started readback are not rolled back when a later stage throws or fails.
    /// The caller retries a failed copy or start directly through the existing
    /// copy or readback pump with the same draft; it must never re-admit.
    /// </para>
    /// <para>
    /// The coordinator performs no drawing, blit, or copy of its own: it fully
    /// delegates the copy to <see cref="CaptureFrameRenderTargetCopyPump"/> and
    /// the readback to <see cref="CaptureFrameRenderTargetReadbackPump"/>, and
    /// never calls <c>Graphics.CopyTexture</c> directly. It holds no source,
    /// draft, or lease in fields, and does not implement
    /// <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. It is not a MonoBehaviour
    /// or a singleton, and performs no Unity static API access, logging, or
    /// trace recording.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameRenderTargetDraftCadencedPipelineCoordinator
    {
        private readonly CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator _submissionCoordinator;
        private readonly CaptureFrameRenderTargetCopyPump _copyPump;
        private readonly CaptureFrameRenderTargetReadbackPump _readbackPump;
        private readonly CaptureFrameRequestQueue _requestQueue;
        private readonly CaptureFrameRenderTargetLeaseRegistry _leaseRegistry;

        internal CaptureFrameRenderTargetDraftCadencedPipelineCoordinator(
            CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator submissionCoordinator,
            CaptureFrameRenderTargetCopyPump copyPump,
            CaptureFrameRenderTargetReadbackPump readbackPump,
            CaptureFrameRequestQueue requestQueue,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry)
        {
            if (submissionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(submissionCoordinator));
            }

            if (copyPump == null)
            {
                throw new ArgumentNullException(nameof(copyPump));
            }

            if (readbackPump == null)
            {
                throw new ArgumentNullException(nameof(readbackPump));
            }

            if (requestQueue == null)
            {
                throw new ArgumentNullException(nameof(requestQueue));
            }

            if (leaseRegistry == null)
            {
                throw new ArgumentNullException(nameof(leaseRegistry));
            }

            _submissionCoordinator = submissionCoordinator;
            _copyPump = copyPump;
            _readbackPump = readbackPump;
            _requestQueue = requestQueue;
            _leaseRegistry = leaseRegistry;
        }

        internal CaptureFrameDraftCadencedPipelineResult TrySubmitCopyAndStart(
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
            RenderTexture source,
            in CaptureFrameRenderTargetLease lease,
            out CaptureFrameDraft draft)
        {
            draft = null;

            // 1. Fail closed if a previously unstarted request is still pending,
            // before cadence, submission, or the source are touched.
            if (_requestQueue.Count != 0)
            {
                throw new InvalidOperationException("The request queue still contains a previously unstarted request.");
            }

            // 2. Submit the current frame exactly once through the cadence gate,
            // passing this coordinator's own out draft straight through.
            CaptureFrameDraftCadencedSubmissionStatus status = _submissionCoordinator.TrySubmit(
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
                out draft);

            switch (status)
            {
                case CaptureFrameDraftCadencedSubmissionStatus.NotSelected:
                    if (draft != null)
                    {
                        throw new InvalidOperationException("NotSelected but a draft was produced.");
                    }

                    return new CaptureFrameDraftCadencedPipelineResult(status, false, false);

                case CaptureFrameDraftCadencedSubmissionStatus.AdmissionRejected:
                    if (draft != null)
                    {
                        throw new InvalidOperationException("Admission rejected but a draft was produced.");
                    }

                    return new CaptureFrameDraftCadencedPipelineResult(status, false, false);

                case CaptureFrameDraftCadencedSubmissionStatus.SchedulingBackpressured:
                    if (draft == null)
                    {
                        throw new InvalidOperationException("Scheduling backpressured but no draft was produced.");
                    }

                    return new CaptureFrameDraftCadencedPipelineResult(status, false, false);

                case CaptureFrameDraftCadencedSubmissionStatus.Scheduled:
                    break;

                default:
                    throw new InvalidOperationException("Unexpected submission status.");
            }

            // 3. Scheduled: validate the draft, queue head, and registered lease.
            if (draft == null)
            {
                throw new InvalidOperationException("Scheduled but no draft was produced.");
            }

            if (_requestQueue.Count != 1)
            {
                throw new InvalidOperationException("Scheduled but the request queue does not hold exactly one request.");
            }

            if (!_requestQueue.TryPeek(out CaptureFrameRequest head))
            {
                throw new InvalidOperationException("Scheduled but the request queue head could not be peeked.");
            }

            if (!head.IdenticalTo(draft.Request))
            {
                throw new InvalidOperationException("Scheduled but the request queue head does not match the draft's request.");
            }

            if (!_leaseRegistry.TryGet(draft.Request, out CaptureFrameRenderTargetLease registeredLease))
            {
                throw new InvalidOperationException("Scheduled but no render target lease is registered for the draft's request.");
            }

            if (!registeredLease.IdenticalTo(lease))
            {
                throw new InvalidOperationException("Scheduled but the registered render target lease does not match the submitted lease.");
            }

            // 4. Copy the source into the registered target exactly once. A
            // false return is an invariant violation; copy/source exceptions
            // are never translated.
            if (!_copyPump.TryCopyNext(source))
            {
                throw new InvalidOperationException("Scheduled but the copy pump refused the queue head.");
            }

            // 5. Start the readback exactly once.
            bool readbackStarted = _readbackPump.TryStartNext();

            // 6. If the readback started, the queue must be empty.
            if (readbackStarted && _requestQueue.Count != 0)
            {
                throw new InvalidOperationException("The request queue still holds a request after the readback started.");
            }

            // 7. If the readback did not start, the queue, lease registration,
            // and pool rent must be intact.
            if (!readbackStarted)
            {
                if (_requestQueue.Count != 1)
                {
                    throw new InvalidOperationException("The readback did not start but the request queue does not hold exactly one request.");
                }

                if (!_requestQueue.TryPeek(out CaptureFrameRequest remainingHead)
                    || !remainingHead.IdenticalTo(draft.Request))
                {
                    throw new InvalidOperationException("The readback did not start but the request queue head no longer matches the draft's request.");
                }

                if (!_leaseRegistry.TryGet(draft.Request, out CaptureFrameRenderTargetLease remainingLease)
                    || !remainingLease.IdenticalTo(lease))
                {
                    throw new InvalidOperationException("The readback did not start but the render target lease is no longer registered for the draft's request.");
                }
            }

            return new CaptureFrameDraftCadencedPipelineResult(status, true, readbackStarted);
        }
    }
}
