namespace Zantetsu.Observability
{
    /// <summary>
    /// Pixel formats supported for capture frames. Explicitly valued and
    /// append-only.
    /// </summary>
    public enum CapturePixelFormat : int
    {
        /// <summary>No format. Default sentinel.</summary>
        None = 0,

        /// <summary>8 bits per channel, 32-bit RGBA.</summary>
        Rgba32 = 1
    }
}
