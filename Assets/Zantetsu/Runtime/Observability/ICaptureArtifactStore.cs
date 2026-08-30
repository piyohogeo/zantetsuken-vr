namespace Zantetsu.Observability
{
    /// <summary>
    /// Format-neutral durable staging and publication boundary. Successful
    /// writes and publications must be non-overwriting and content-verified;
    /// staging data is flushed before its receipt is returned. Platform-level
    /// no-follow and directory-metadata durability remain capabilities of the
    /// selected store implementation rather than assumptions in capture code.
    /// </summary>
    internal interface ICaptureArtifactStore
    {
        CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request);
        CaptureArtifactPublishReceipt Publish(CaptureArtifactDescriptor descriptor);
        CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor);
        CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor);
    }
}
