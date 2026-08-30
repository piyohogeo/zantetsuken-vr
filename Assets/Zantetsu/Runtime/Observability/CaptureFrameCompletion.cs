using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>Exactly-once notification that backend input ownership ended.</summary>
    internal readonly struct CaptureFrameCompletion
    {
        internal CaptureFrameCompletion(
            in CaptureFrameWorkToken workToken,
            long captureFrameId,
            CaptureFrameCompletionStatus status,
            bool inputCanBeReleased,
            int producedArtifactCount,
            ExceptionDispatchInfo failure)
        {
            if (!workToken.IsValid)
            {
                throw new ArgumentException("Work token must be valid.", nameof(workToken));
            }

            if (captureFrameId <= 0 || captureFrameId != workToken.CaptureFrameId)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            }

            if (status != CaptureFrameCompletionStatus.Succeeded
                && status != CaptureFrameCompletionStatus.Failed
                && status != CaptureFrameCompletionStatus.Cancelled)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            if (!inputCanBeReleased)
            {
                throw new ArgumentException("A published frame completion must release its input.", nameof(inputCanBeReleased));
            }

            if (producedArtifactCount < 0 || (status != CaptureFrameCompletionStatus.Succeeded && producedArtifactCount != 0))
            {
                throw new ArgumentOutOfRangeException(nameof(producedArtifactCount));
            }

            if ((status == CaptureFrameCompletionStatus.Failed) != (failure != null))
            {
                throw new ArgumentException("Only failed completion carries a failure.", nameof(failure));
            }

            WorkToken = workToken;
            CaptureFrameId = captureFrameId;
            Status = status;
            InputCanBeReleased = inputCanBeReleased;
            ProducedArtifactCount = producedArtifactCount;
            Failure = failure;
        }

        internal CaptureFrameWorkToken WorkToken { get; }
        internal long CaptureFrameId { get; }
        internal CaptureFrameCompletionStatus Status { get; }
        internal bool InputCanBeReleased { get; }
        internal int ProducedArtifactCount { get; }
        internal ExceptionDispatchInfo Failure { get; }
        internal bool IsValid => WorkToken.IsValid
            && CaptureFrameId == WorkToken.CaptureFrameId
            && InputCanBeReleased
            && ProducedArtifactCount >= 0
            && (Status == CaptureFrameCompletionStatus.Succeeded
                || Status == CaptureFrameCompletionStatus.Failed
                || Status == CaptureFrameCompletionStatus.Cancelled)
            && (Status == CaptureFrameCompletionStatus.Succeeded || ProducedArtifactCount == 0)
            && ((Status == CaptureFrameCompletionStatus.Failed) == (Failure != null));
    }
}
