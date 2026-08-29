namespace Zantetsu.Observability
{
    /// <summary>
    /// Identifies which of the two Capture Run roots a Capture Run
    /// Initialization Marker describes. Values are append-only and fixed;
    /// marker construction rejects <see cref="None"/> and undefined values.
    /// </summary>
    internal enum CaptureRunRootRole : int
    {
        None = 0,
        Staging = 1,
        Final = 2
    }
}
