namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of saving the FIFO head of a capture PNG queue. Explicitly
    /// valued and append-only.
    /// </summary>
    public enum CaptureFramePngSaveStatus : int
    {
        /// <summary>The queue was empty and there was nothing to save.</summary>
        None = 0,

        /// <summary>The FIFO head was saved, dequeued, and its PNG disposed.</summary>
        Saved = 1
    }
}
