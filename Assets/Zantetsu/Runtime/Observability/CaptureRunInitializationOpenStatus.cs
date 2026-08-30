namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal status of an opened Capture Run. Values are fixed, explicitly
    /// numbered, and append-only; existing values must never be renumbered or
    /// removed.
    /// </summary>
    internal enum CaptureRunInitializationOpenStatus : int
    {
        None = 0,
        SessionReady = 1,
        PublicationRecoveryRequired = 2,
        RunRootCollision = 3
    }
}
