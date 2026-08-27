namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of completing the FIFO head of a pending artifact queue by
    /// publishing its sidecar. Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFramePngArtifactCompletionStatus : int
    {
        /// <summary>The artifact queue was empty and there was nothing to complete.</summary>
        None = 0,

        /// <summary>The sidecar was published and the head artifact was dequeued.</summary>
        Completed = 1
    }
}
