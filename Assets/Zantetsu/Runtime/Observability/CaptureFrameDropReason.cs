namespace Zantetsu.Observability
{
    /// <summary>
    /// Reasons a capture frame request may be dropped from scheduling.
    /// Explicitly valued and append-only.
    /// </summary>
    public enum CaptureFrameDropReason : int
    {
        /// <summary>No drop. Default sentinel.</summary>
        None = 0,

        /// <summary>The capture frame request queue was full.</summary>
        RequestQueueFull = 1,

        /// <summary>The GPU readback completed with an error.</summary>
        ReadbackFailed = 2
    }
}
