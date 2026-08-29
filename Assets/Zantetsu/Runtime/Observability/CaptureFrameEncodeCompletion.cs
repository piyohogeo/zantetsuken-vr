using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completion metadata produced by one accepted encode operation.
    /// Owned raw and encoded buffers remain in the service's fixed slot until
    /// main-thread application acknowledges this token. The completion contains
    /// no mutable buffer, Draft, Registry, Trace, logger, or Unity object
    /// reference.
    /// </summary>
    internal readonly struct CaptureFrameEncodeCompletion
    {
        internal CaptureFrameWorkToken WorkToken { get; }

        internal CaptureFrameRequest FrameRequest { get; }

        internal CaptureFrameEncodeCompletionStatus Status { get; }

        internal int EncodedByteCount { get; }

        internal double ElapsedMilliseconds { get; }

        internal ExceptionDispatchInfo Failure { get; }

        internal bool IsValid =>
            WorkToken.IsValid &&
            FrameRequest.IsValid &&
            FrameRequest.TraceContext.TestRunId == WorkToken.TestRunId &&
            FrameRequest.TraceContext.CaptureFrameId == WorkToken.CaptureFrameId &&
            ((Status == CaptureFrameEncodeCompletionStatus.Succeeded &&
              EncodedByteCount > 0 &&
              Failure == null &&
              ElapsedMilliseconds >= 0.0 &&
              !double.IsNaN(ElapsedMilliseconds) &&
              !double.IsInfinity(ElapsedMilliseconds)) ||
             (Status == CaptureFrameEncodeCompletionStatus.Failed &&
              EncodedByteCount == 0 &&
              Failure != null &&
              ElapsedMilliseconds == 0.0) ||
             (Status == CaptureFrameEncodeCompletionStatus.Cancelled &&
              EncodedByteCount == 0 &&
              Failure == null &&
              ElapsedMilliseconds == 0.0));

        internal CaptureFrameEncodeCompletion(
            in CaptureFrameWorkToken workToken,
            in CaptureFrameRequest frameRequest,
            CaptureFrameEncodeCompletionStatus status,
            int encodedByteCount,
            double elapsedMilliseconds,
            ExceptionDispatchInfo failure)
        {
            if (!workToken.IsValid)
            {
                throw new ArgumentException("Work token must be valid.", nameof(workToken));
            }

            if (!frameRequest.IsValid ||
                frameRequest.TraceContext.TestRunId != workToken.TestRunId ||
                frameRequest.TraceContext.CaptureFrameId != workToken.CaptureFrameId)
            {
                throw new ArgumentException("Frame request must match the work token.", nameof(frameRequest));
            }

            if (status != CaptureFrameEncodeCompletionStatus.Succeeded &&
                status != CaptureFrameEncodeCompletionStatus.Failed &&
                status != CaptureFrameEncodeCompletionStatus.Cancelled)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            if (status == CaptureFrameEncodeCompletionStatus.Succeeded)
            {
                if (encodedByteCount <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(encodedByteCount));
                }

                if (failure != null)
                {
                    throw new ArgumentException("Successful completion cannot contain a failure.", nameof(failure));
                }

                if (elapsedMilliseconds < 0.0 || double.IsNaN(elapsedMilliseconds) || double.IsInfinity(elapsedMilliseconds))
                {
                    throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
                }
            }
            else
            {
                if (elapsedMilliseconds != 0.0)
                {
                    throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
                }

                if (encodedByteCount != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(encodedByteCount));
                }

                if (status == CaptureFrameEncodeCompletionStatus.Failed && failure == null)
                {
                    throw new ArgumentNullException(nameof(failure));
                }

                if (status == CaptureFrameEncodeCompletionStatus.Cancelled && failure != null)
                {
                    throw new ArgumentException("Cancelled completion cannot contain a failure.", nameof(failure));
                }
            }

            WorkToken = workToken;
            FrameRequest = frameRequest;
            Status = status;
            EncodedByteCount = encodedByteCount;
            ElapsedMilliseconds = elapsedMilliseconds;
            Failure = failure;
        }
    }
}
