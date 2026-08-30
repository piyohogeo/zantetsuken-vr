using System;

namespace Zantetsu.Observability
{
    internal sealed class CaptureArtifactWriteReceipt : ICaptureArtifactStorageReceipt
    {
        internal CaptureArtifactWriteReceipt(ICaptureArtifactStore issuedBy, CaptureArtifactDescriptor descriptor, string absolutePath)
        {
            IssuedBy = issuedBy ?? throw new ArgumentNullException(nameof(issuedBy));
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            AbsolutePath = absolutePath ?? throw new ArgumentNullException(nameof(absolutePath));
        }

        internal ICaptureArtifactStore IssuedBy { get; }
        public CaptureArtifactDescriptor Descriptor { get; }
        internal string AbsolutePath { get; }
        internal bool IsIssuedFor(ICaptureArtifactStore store, CaptureArtifactDescriptor descriptor) =>
            ReferenceEquals(IssuedBy, store) && ReferenceEquals(Descriptor, descriptor);
    }
}
