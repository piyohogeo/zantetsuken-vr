namespace Zantetsu.Observability
{
    /// <summary>
    /// Format-neutral durable staging boundary. A write is non-overwriting,
    /// its payload is hash-checked against the descriptor before a byte
    /// reaches the disk, and the bytes are flushed and renamed into place
    /// before the receipt is returned, so a partially written file is never
    /// visible under the artifact's own name.
    /// </summary>
    internal interface ICaptureArtifactStore
    {
        CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request);
    }
}
