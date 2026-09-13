namespace Zantetsu.Observability
{
    /// <summary>
    /// Format-neutral durable staging boundary. A successful write must be
    /// non-overwriting and content-verified, and staging data is flushed
    /// before its receipt is returned. Platform-level no-follow and
    /// directory-metadata durability remain capabilities of the selected store
    /// implementation rather than assumptions in capture code.
    /// </summary>
    internal interface ICaptureArtifactStore
    {
        CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request);
        CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor);
    }
}
