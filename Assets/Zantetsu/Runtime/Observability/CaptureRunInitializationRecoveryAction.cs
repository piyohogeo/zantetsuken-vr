namespace Zantetsu.Observability
{
    /// <summary>
    /// Single recovery action in a Capture Run initialization recovery plan.
    /// Values are fixed, explicitly numbered, and append-only; existing values
    /// must never be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is never a valid plan step and is rejected by the
    /// step constructor. The plan builder is the only source of steps.
    /// </para>
    /// </remarks>
    internal enum CaptureRunInitializationRecoveryAction : int
    {
        None = 0,
        DeleteMarkerTemporary = 1,
        RemoveEmptyRoot = 2,
        ProvisionRoot = 3,
        WriteMarker = 4,
        StartFreshInitialization = 5,
        InitializationReady = 6,
        ContinuePublicationRecovery = 7,
        StopRunRootCollision = 8
    }
}
