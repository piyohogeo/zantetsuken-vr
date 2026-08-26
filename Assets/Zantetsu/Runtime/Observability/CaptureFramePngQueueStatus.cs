namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of collecting, encoding, and enqueueing a capture frame readback.
    /// Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFramePngQueueStatus : int
    {
        /// <summary>No completed readback was available.</summary>
        None = 0,

        /// <summary>The PNG was encoded, traced, and enqueued successfully.</summary>
        Queued = 1,

        /// <summary>The readback failed, or the encoded PNG was dropped on a full queue.</summary>
        Dropped = 2
    }
}
