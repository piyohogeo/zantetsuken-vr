using System;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Integrates the split pipeline API with cadenced submission so that, for
    /// the current frame, the submitted record and the started readback always
    /// refer to the same request. The per-frame order is fixed: advance
    /// completed work, submit the current frame through the cadence gate, then
    /// start a readback for exactly the submitted request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PngJsonCaptureFramePipelineCoordinator.AdvancePendingWork"/> is
    /// called exactly once first. If the request queue still holds a previously
    /// unstarted request after advancing, this coordinator fails closed with
    /// <see cref="InvalidOperationException"/> without running cadence
    /// selection, submission, or starting a readback, so the current frame's
    /// image is never associated with a stale request. The advance's side
    /// effects are not rolled back.
    /// </para>
    /// <para>
    /// When the queue is empty the current frame is submitted once through
    /// <see cref="CaptureFrameCadencedSubmissionCoordinator.TrySubmit"/>. On
    /// <see cref="CaptureFrameCadencedSubmissionStatus.NotSelected"/> or
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Backpressured"/> the
    /// source is neither validated nor used and no readback is started.
    /// </para>
    /// <para>
    /// On <see cref="CaptureFrameCadencedSubmissionStatus.Submitted"/> the
    /// accepted record must be non-null, the queue must hold exactly one
    /// request, and that request must be identical to the accepted record's
    /// request; otherwise this coordinator fails closed. Only after these
    /// checks does it call
    /// <see cref="PngJsonCaptureFramePipelineCoordinator.TryStartNextReadback"/> once.
    /// If the readback starts, the queue must no longer hold the request.
    /// </para>
    /// <para>
    /// When the readback cannot start (dispatcher or buffer pool backpressure)
    /// the record stays in the registry, the request stays in the queue, the
    /// cadence state and the issued capture frame ID are not rolled back, and
    /// no drop trace is added. The caller must keep the same
    /// <paramref name="source"/> unchanged and retry
    /// <see cref="PngJsonCaptureFramePipelineCoordinator.TryStartNextReadback"/> with
    /// it before passing the next frame here; this coordinator never releases
    /// or destroys the source.
    /// </para>
    /// <para>
    /// This operation is <b>not</b> a transaction: if start throws, the
    /// exception is not translated and the advance, cadence selection, record
    /// registration, and ID issuance are not rolled back. Fail-closed contract
    /// violations throw <see cref="InvalidOperationException"/> without
    /// compensating (no file deletion, queue clear, or registry re-register).
    /// </para>
    /// <para>
    /// Owns, disposes, clears, and releases nothing: not the pipeline or
    /// submission coordinators, the queue, registry, dispatcher, pool, logger,
    /// record, render texture, artifact, or receipt. Main-thread only and
    /// <b>not</b> thread-safe. Not a MonoBehaviour or singleton, and performs
    /// no Unity static API access, logging, or trace.
    /// </para>
    /// </remarks>
    public sealed class PngJsonCaptureFrameCadencedPipelineCoordinator
    {
        private readonly PngJsonCaptureFramePipelineCoordinator _pipelineCoordinator;
        private readonly CaptureFrameCadencedSubmissionCoordinator _submissionCoordinator;
        private readonly CaptureFrameRequestQueue _requestQueue;

        public PngJsonCaptureFrameCadencedPipelineCoordinator(
            PngJsonCaptureFramePipelineCoordinator pipelineCoordinator,
            CaptureFrameCadencedSubmissionCoordinator submissionCoordinator,
            CaptureFrameRequestQueue requestQueue)
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

            _pipelineCoordinator = pipelineCoordinator;
            _submissionCoordinator = submissionCoordinator;
            _requestQueue = requestQueue;
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
            RenderTexture source)
        {
            // 1. Advance completed work exactly once, first.
            PngJsonCaptureFramePipelineAdvanceResult advance = _pipelineCoordinator.AdvancePendingWork();

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

            // 5. Start the readback exactly once.
            bool started = _pipelineCoordinator.TryStartNextReadback(source);

            // 6. If the readback started, the queue must no longer hold it.
            if (started && _requestQueue.Count != 0)
            {
                throw new InvalidOperationException("The request queue still holds a request after the readback started.");
            }

            return new CaptureFrameCadencedPipelineResult(advance, status, started, acceptedRecord);
        }
    }
}
