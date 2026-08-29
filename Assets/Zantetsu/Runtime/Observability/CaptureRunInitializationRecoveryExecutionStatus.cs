namespace Zantetsu.Observability
{
    /// <summary>
    /// Execution status reported by a Capture Run recovery execution. Values
    /// are fixed, explicitly numbered, and append-only; existing values must
    /// never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunInitializationRecoveryExecutionStatus : int
    {
        None = 0,
        StartFreshRequired = 1,
        InitializationReady = 2,
        PublicationRecoveryRequired = 3,
        RunRootCollision = 4
    }
}
