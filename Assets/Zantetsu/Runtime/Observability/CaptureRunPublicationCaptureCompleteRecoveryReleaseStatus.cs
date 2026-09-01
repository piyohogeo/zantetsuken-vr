namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed, append-only terminal status of one capture-complete recovery
    /// owner release.
    /// </summary>
    /// <remarks>
    /// Values are explicitly fixed and must only ever be appended; existing
    /// values must never be renumbered or removed.
    /// </remarks>
    internal enum CaptureRunPublicationCaptureCompleteRecoveryReleaseStatus : int
    {
        None = 0,
        RecoveryOwnerReleased = 1,
    }
}
