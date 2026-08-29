namespace Zantetsu.Observability
{
    /// <summary>
    /// Recovery disposition selected for one observed Capture Run. Values are
    /// fixed, explicitly numbered, and append-only; existing values must never
    /// be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is never a valid classification result and is
    /// rejected by the decision constructor. The classifier is the only source
    /// of dispositions; callers never fabricate one directly.
    /// </para>
    /// </remarks>
    internal enum CaptureRunInitializationRecoveryDisposition : int
    {
        None = 0,
        StartFresh = 1,
        CleanupTemporaryAndStartFresh = 2,
        CompleteMissingPeerInitialization = 3,
        CompleteReadyMarkers = 4,
        AlreadyInitialized = 5,
        RequiresPublicationRecovery = 6,
        RunRootCollision = 7
    }
}
