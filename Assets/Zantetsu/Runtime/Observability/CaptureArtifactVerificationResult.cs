using System;

namespace Zantetsu.Observability
{
    internal readonly struct CaptureArtifactVerificationResult
    {
        internal CaptureArtifactVerificationResult(CaptureArtifactDescriptor descriptor, CaptureArtifactVerificationStatus status, long observedByteLength)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            if (status != CaptureArtifactVerificationStatus.Absent
                && status != CaptureArtifactVerificationStatus.MatchesExpected
                && status != CaptureArtifactVerificationStatus.Mismatch
                && status != CaptureArtifactVerificationStatus.Invalid)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            if (observedByteLength < 0 || (status == CaptureArtifactVerificationStatus.Absent && observedByteLength != 0))
            {
                throw new ArgumentOutOfRangeException(nameof(observedByteLength));
            }

            Status = status;
            ObservedByteLength = observedByteLength;
        }

        internal CaptureArtifactDescriptor Descriptor { get; }
        internal CaptureArtifactVerificationStatus Status { get; }
        internal long ObservedByteLength { get; }

        internal bool IsValid => Descriptor != null
            && Descriptor.IsValid
            && (Status == CaptureArtifactVerificationStatus.Absent
                || Status == CaptureArtifactVerificationStatus.MatchesExpected
                || Status == CaptureArtifactVerificationStatus.Mismatch
                || Status == CaptureArtifactVerificationStatus.Invalid)
            && ObservedByteLength >= 0
            && (Status != CaptureArtifactVerificationStatus.Absent || ObservedByteLength == 0);
    }
}
