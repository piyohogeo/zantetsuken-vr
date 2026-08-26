namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of a capture frame readback collection attempt. Explicitly
    /// valued and append-only.
    /// </summary>
    public enum CaptureFrameReadbackCollectStatus : int
    {
        /// <summary>No completed readback was available.</summary>
        None = 0,

        /// <summary>A successful readback was collected.</summary>
        Succeeded = 1,

        /// <summary>A failed readback was collected, traced, and released.</summary>
        Dropped = 2
    }
}
