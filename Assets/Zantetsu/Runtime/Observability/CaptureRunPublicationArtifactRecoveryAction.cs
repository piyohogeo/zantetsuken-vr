namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free recovery action of one artifact recovery step. Values
    /// are fixed, explicitly numbered, and append-only; existing values must
    /// never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunPublicationArtifactRecoveryAction : int
    {
        None = 0,
        PublishArtifact = 1,
        ReinspectArtifacts = 2,
        CommitCaptureIndex = 3,
        ContinueCaptureCompleteCleanup = 4,
        StopOrphanedPreTrace = 5,
        StopArtifactSourceMissing = 6,
        StopPublishedArtifactMissing = 7,
        StopRunRootCollision = 8
    }
}
