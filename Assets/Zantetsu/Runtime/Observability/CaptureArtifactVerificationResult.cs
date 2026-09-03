using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal result of one artifact verification attempt. The execution
    /// disposition separates a completed content classification from a
    /// deferred attempt that could not acquire a verification buffer; the
    /// failure reason further discriminates the terminal state. The valid
    /// disposition / status / reason / observed length combinations are
    /// enforced by the shared predicate used by both the constructor and
    /// <see cref="IsValid"/>.
    /// </summary>
    internal readonly struct CaptureArtifactVerificationResult
    {
        internal CaptureArtifactVerificationResult(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationExecutionDisposition executionDisposition,
            CaptureArtifactVerificationStatus status,
            CaptureArtifactVerificationFailureReason failureReason,
            long observedByteLength)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            if (!descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));

            if (!IsValidCombination(descriptor, executionDisposition, status, failureReason, observedByteLength))
            {
                throw new ArgumentException("Verification result combination is invalid.");
            }

            ExecutionDisposition = executionDisposition;
            Status = status;
            FailureReason = failureReason;
            ObservedByteLength = observedByteLength;
        }

        internal CaptureArtifactDescriptor Descriptor { get; }
        internal CaptureArtifactVerificationExecutionDisposition ExecutionDisposition { get; }
        internal CaptureArtifactVerificationStatus Status { get; }
        internal CaptureArtifactVerificationFailureReason FailureReason { get; }
        internal long ObservedByteLength { get; }

        internal bool IsValid => IsValidCombination(Descriptor, ExecutionDisposition, Status, FailureReason, ObservedByteLength);

        private static bool IsValidCombination(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationExecutionDisposition disposition,
            CaptureArtifactVerificationStatus status,
            CaptureArtifactVerificationFailureReason reason,
            long observedByteLength)
        {
            if (descriptor == null || !descriptor.IsValid) return false;
            if (observedByteLength < 0) return false;

            switch (disposition)
            {
                case CaptureArtifactVerificationExecutionDisposition.Completed:
                    switch (status)
                    {
                        case CaptureArtifactVerificationStatus.MatchesExpected:
                            return reason == CaptureArtifactVerificationFailureReason.None
                                && observedByteLength == descriptor.ByteLength;
                        case CaptureArtifactVerificationStatus.Absent:
                            return reason == CaptureArtifactVerificationFailureReason.FileAbsent
                                && observedByteLength == 0;
                        case CaptureArtifactVerificationStatus.Mismatch:
                            return reason == CaptureArtifactVerificationFailureReason.ShorterThanDeclared
                                || reason == CaptureArtifactVerificationFailureReason.LongerThanDeclared
                                || reason == CaptureArtifactVerificationFailureReason.HashMismatch;
                        case CaptureArtifactVerificationStatus.Invalid:
                            return reason == CaptureArtifactVerificationFailureReason.ReadIoFailure
                                || reason == CaptureArtifactVerificationFailureReason.CheckedLengthOverflow
                                || reason == CaptureArtifactVerificationFailureReason.FileChangedDuringRead
                                || reason == CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind
                                || reason == CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch
                                || reason == CaptureArtifactVerificationFailureReason.Cancelled
                                || reason == CaptureArtifactVerificationFailureReason.NoFollowUnavailable;
                        default:
                            return false;
                    }
                case CaptureArtifactVerificationExecutionDisposition.Deferred:
                    return status == CaptureArtifactVerificationStatus.None
                        && reason == CaptureArtifactVerificationFailureReason.BufferUnavailable
                        && observedByteLength == 0;
                default:
                    return false;
            }
        }
    }
}
