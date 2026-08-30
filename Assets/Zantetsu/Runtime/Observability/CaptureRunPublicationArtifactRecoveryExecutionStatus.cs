namespace Zantetsu.Observability
{
    /// <summary>
    /// Execution status reported by a Capture Run publication artifact recovery
    /// execution. Values are fixed, explicitly numbered, and append-only;
    /// existing values must never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunPublicationArtifactRecoveryExecutionStatus : int
    {
        None = 0,
        ReinspectionRequired = 1,
        CaptureCompleteCleanupRequired = 2,
        OrphanedPreTrace = 3,
        ArtifactSourceMissing = 4,
        PublishedArtifactMissing = 5,
        RunRootCollision = 6
    }
}
