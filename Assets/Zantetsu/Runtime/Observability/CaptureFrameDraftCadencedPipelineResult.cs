using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Summary of one cadenced capture frame draft pipeline submission: the
    /// cadenced submission outcome, whether the source image was copied into
    /// the registered render target, and whether the readback was started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a value type with no public constructor; instances are created
    /// only by <see cref="CaptureFrameRenderTargetDraftCadencedPipelineCoordinator"/>.
    /// It deliberately holds no reference to the draft: the draft is returned
    /// through the coordinator's <c>out</c> parameter so the caller keeps it
    /// even when a later stage throws.
    /// </para>
    /// <para>
    /// Invariants:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="SubmissionStatus"/> is <c>None</c>, <c>NotSelected</c>,
    /// <c>AdmissionRejected</c>, or <c>SchedulingBackpressured</c> exactly when
    /// <see cref="CopyCompleted"/> is <c>false</c> and
    /// <see cref="ReadbackStarted"/> is <c>false</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="SubmissionStatus"/> is <c>Scheduled</c> only with
    /// <see cref="CopyCompleted"/> <c>true</c>; <see cref="ReadbackStarted"/>
    /// may then be either value.
    /// </description></item>
    /// <item><description>
    /// <see cref="ReadbackStarted"/> is <c>true</c> only when
    /// <see cref="CopyCompleted"/> is <c>true</c>.
    /// </description></item>
    /// <item><description>
    /// An undefined status value throws <see cref="ArgumentException"/>.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <c>default</c> is a valid not-run state reporting
    /// <see cref="CaptureFrameDraftCadencedSubmissionStatus.None"/> with both
    /// flags <c>false</c>.
    /// </para>
    /// </remarks>
    internal readonly struct CaptureFrameDraftCadencedPipelineResult
    {
        public CaptureFrameDraftCadencedSubmissionStatus SubmissionStatus { get; }

        public bool CopyCompleted { get; }

        public bool ReadbackStarted { get; }

        internal CaptureFrameDraftCadencedPipelineResult(
            CaptureFrameDraftCadencedSubmissionStatus submissionStatus,
            bool copyCompleted,
            bool readbackStarted)
        {
            if (submissionStatus != CaptureFrameDraftCadencedSubmissionStatus.None
                && submissionStatus != CaptureFrameDraftCadencedSubmissionStatus.NotSelected
                && submissionStatus != CaptureFrameDraftCadencedSubmissionStatus.AdmissionRejected
                && submissionStatus != CaptureFrameDraftCadencedSubmissionStatus.Scheduled
                && submissionStatus != CaptureFrameDraftCadencedSubmissionStatus.SchedulingBackpressured)
            {
                throw new ArgumentException("Undefined submission status.", nameof(submissionStatus));
            }

            if (submissionStatus == CaptureFrameDraftCadencedSubmissionStatus.Scheduled)
            {
                if (!copyCompleted)
                {
                    throw new ArgumentException("CopyCompleted must be true when the submission status is Scheduled.", nameof(copyCompleted));
                }
            }
            else
            {
                if (copyCompleted)
                {
                    throw new ArgumentException("CopyCompleted must be false unless the submission status is Scheduled.", nameof(copyCompleted));
                }

                if (readbackStarted)
                {
                    throw new ArgumentException("ReadbackStarted must be false unless the submission status is Scheduled.", nameof(readbackStarted));
                }
            }

            SubmissionStatus = submissionStatus;
            CopyCompleted = copyCompleted;
            ReadbackStarted = readbackStarted;
        }
    }
}
