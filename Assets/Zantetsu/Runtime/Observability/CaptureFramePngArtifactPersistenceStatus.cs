namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of advancing the capture artifact persistence pipeline by one
    /// step. Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFramePngArtifactPersistenceStatus : int
    {
        /// <summary>Both queues were empty and there was nothing to advance.</summary>
        None = 0,

        /// <summary>A PNG was saved and prepared into the pending artifact queue.</summary>
        PngPrepared = 1,

        /// <summary>A pending artifact's sidecar was published and dequeued.</summary>
        SidecarCompleted = 2
    }
}
