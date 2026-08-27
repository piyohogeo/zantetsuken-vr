namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of preparing the FIFO head of a capture PNG queue into a pending
    /// artifact queue. Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFramePngArtifactPreparationStatus : int
    {
        /// <summary>The PNG queue was empty and there was nothing to prepare.</summary>
        None = 0,

        /// <summary>The PNG was saved and its prepared artifact was enqueued.</summary>
        Queued = 1,

        /// <summary>The artifact queue was full; preparation paused without saving.</summary>
        Backpressured = 2
    }
}
