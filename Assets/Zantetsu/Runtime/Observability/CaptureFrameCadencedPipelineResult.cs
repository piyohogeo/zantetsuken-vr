using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Summary of one cadenced pipeline submission: the advance outcome, the
    /// cadence submission outcome, whether the current frame's readback was
    /// started, and the accepted record when the frame was submitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a value type with no public constructor; instances are created
    /// only by <see cref="PngJsonCaptureFrameCadencedPipelineCoordinator"/> and
    /// <see cref="PngJsonCaptureFrameRenderTargetCadencedPipelineCoordinator"/>. It
    /// owns neither the advance result's artifact/receipt nor the accepted
    /// record.
    /// </para>
    /// <para>
    /// Invariant: <see cref="HasAcceptedRecord"/> is true exactly when
    /// <see cref="SubmissionStatus"/> is
    /// <see cref="CaptureFrameCadencedSubmissionStatus.Submitted"/>; in that
    /// case <see cref="AcceptedRecord"/> is non-null, and otherwise it is null.
    /// <see cref="ReadbackStarted"/> may be true only when the submission status
    /// is <see cref="CaptureFrameCadencedSubmissionStatus.Submitted"/>. There is
    /// no independent boolean field; <see cref="HasAcceptedRecord"/> is computed
    /// from <see cref="SubmissionStatus"/>.
    /// </para>
    /// <para>
    /// <c>default</c> is a valid not-run state reporting
    /// <see cref="CaptureFrameCadencedSubmissionStatus.None"/> with a default
    /// advance result, <see cref="ReadbackStarted"/> false, and a null record.
    /// </para>
    /// </remarks>
    public readonly struct CaptureFrameCadencedPipelineResult
    {
        public PngJsonCaptureFramePipelineAdvanceResult AdvanceResult { get; }

        public CaptureFrameCadencedSubmissionStatus SubmissionStatus { get; }

        public bool ReadbackStarted { get; }

        public CaptureFrameRecord AcceptedRecord { get; }

        public bool HasAcceptedRecord =>
            SubmissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted;

        internal CaptureFrameCadencedPipelineResult(
            PngJsonCaptureFramePipelineAdvanceResult advanceResult,
            CaptureFrameCadencedSubmissionStatus submissionStatus,
            bool readbackStarted,
            CaptureFrameRecord acceptedRecord)
        {
            if (submissionStatus != CaptureFrameCadencedSubmissionStatus.None
                && submissionStatus != CaptureFrameCadencedSubmissionStatus.NotSelected
                && submissionStatus != CaptureFrameCadencedSubmissionStatus.Submitted
                && submissionStatus != CaptureFrameCadencedSubmissionStatus.Backpressured)
            {
                throw new ArgumentException("Undefined submission status.", nameof(submissionStatus));
            }

            if (submissionStatus == CaptureFrameCadencedSubmissionStatus.Submitted)
            {
                if (acceptedRecord == null)
                {
                    throw new ArgumentNullException(nameof(acceptedRecord));
                }
            }
            else
            {
                if (acceptedRecord != null)
                {
                    throw new ArgumentException("Accepted record must be null unless the submission status is Submitted.", nameof(acceptedRecord));
                }
            }

            if (readbackStarted && submissionStatus != CaptureFrameCadencedSubmissionStatus.Submitted)
            {
                throw new ArgumentException("ReadbackStarted can only be true when the submission status is Submitted.", nameof(readbackStarted));
            }

            AdvanceResult = advanceResult;
            SubmissionStatus = submissionStatus;
            ReadbackStarted = readbackStarted;
            AcceptedRecord = acceptedRecord;
        }
    }
}
