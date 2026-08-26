namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of collecting and PNG-encoding a capture frame readback.
    /// Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFramePngCollectStatus : int
    {
        /// <summary>No completed readback was available.</summary>
        None = 0,

        /// <summary>A successful readback was collected and encoded as PNG.</summary>
        Encoded = 1,

        /// <summary>A failed readback was collected, traced, and released.</summary>
        Dropped = 2
    }
}
