using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Applies a capture cadence selector in front of the draft submission
    /// coordinator so unselected frames skip admission, ID issuance, and
    /// scheduling entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cadence selector is invoked exactly once per submission. When it
    /// returns <c>false</c> no other dependency is touched and
    /// <see cref="CaptureFrameDraftCadencedSubmissionStatus.NotSelected"/> is
    /// returned with a null draft. Only a selected frame is forwarded unchanged
    /// to the submission coordinator, and its status is mapped to the cadenced
    /// status. Re-entering an already-selected timestamp is never re-selected,
    /// so no extra ID or admission trace is produced; resuming requires the
    /// caller to reset the selector explicitly.
    /// </para>
    /// <para>
    /// The submission coordinator's own <c>out</c> draft is passed straight
    /// through, so a scheduler exception still exposes the admitted draft to
    /// the caller. On selector or admission exceptions the draft is null and
    /// the lease remains caller-owned; on scheduler exceptions the lease
    /// ownership follows the scheduler's existing rollback contract and this
    /// coordinator never guesses a pool return.
    /// </para>
    /// <para>
    /// This coordinator never rolls back a selected timestamp, an issued ID, or
    /// any cadence, admission, or scheduling side effect. It performs no pool
    /// return, registry or queue operation, trace generation, ID issuance,
    /// disposal, or clear, and holds no draft or lease. It is main-thread only,
    /// not thread-safe, and does not implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator
    {
        private readonly CaptureFrameCadenceSelector _cadenceSelector;
        private readonly CaptureFrameRenderTargetDraftSubmissionCoordinator _submissionCoordinator;

        internal CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator(
            CaptureFrameCadenceSelector cadenceSelector,
            CaptureFrameRenderTargetDraftSubmissionCoordinator submissionCoordinator)
        {
            if (cadenceSelector == null)
            {
                throw new ArgumentNullException(nameof(cadenceSelector));
            }

            if (submissionCoordinator == null)
            {
                throw new ArgumentNullException(nameof(submissionCoordinator));
            }

            _cadenceSelector = cadenceSelector;
            _submissionCoordinator = submissionCoordinator;
        }

        internal CaptureFrameDraftCadencedSubmissionStatus TrySubmit(
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
            in CaptureFrameRenderTargetLease lease,
            out CaptureFrameDraft draft)
        {
            draft = null;

            if (!_cadenceSelector.TrySelect(timing))
            {
                return CaptureFrameDraftCadencedSubmissionStatus.NotSelected;
            }

            CaptureFrameDraftSubmissionStatus status = _submissionCoordinator.TrySubmit(
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
                case CaptureFrameDraftSubmissionStatus.AdmissionRejected:
                    if (draft != null)
                    {
                        throw new InvalidOperationException("Admission rejected but a draft was produced.");
                    }

                    return CaptureFrameDraftCadencedSubmissionStatus.AdmissionRejected;

                case CaptureFrameDraftSubmissionStatus.Scheduled:
                    if (draft == null)
                    {
                        throw new InvalidOperationException("Scheduled but no draft was produced.");
                    }

                    return CaptureFrameDraftCadencedSubmissionStatus.Scheduled;

                case CaptureFrameDraftSubmissionStatus.SchedulingBackpressured:
                    if (draft == null)
                    {
                        throw new InvalidOperationException("Scheduling backpressured but no draft was produced.");
                    }

                    return CaptureFrameDraftCadencedSubmissionStatus.SchedulingBackpressured;

                default:
                    throw new InvalidOperationException("Unexpected submission status.");
            }
        }
    }
}
