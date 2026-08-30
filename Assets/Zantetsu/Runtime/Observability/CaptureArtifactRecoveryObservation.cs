using System;

namespace Zantetsu.Observability
{
    internal sealed class CaptureArtifactRecoveryObservation
    {
        internal CaptureArtifactRecoveryObservation(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationResult staging,
            CaptureArtifactVerificationResult final)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            if (!descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            if (!staging.IsValid || !ReferenceEquals(staging.Descriptor, descriptor)) throw new ArgumentException("Staging observation must match descriptor.", nameof(staging));
            if (!final.IsValid || !ReferenceEquals(final.Descriptor, descriptor)) throw new ArgumentException("Final observation must match descriptor.", nameof(final));
            Staging = staging;
            Final = final;
        }

        internal CaptureArtifactDescriptor Descriptor { get; }
        internal CaptureArtifactVerificationResult Staging { get; }
        internal CaptureArtifactVerificationResult Final { get; }
        internal bool IsValid => Descriptor != null
            && Descriptor.IsValid
            && Staging.IsValid
            && Final.IsValid
            && ReferenceEquals(Staging.Descriptor, Descriptor)
            && ReferenceEquals(Final.Descriptor, Descriptor);
    }
}
