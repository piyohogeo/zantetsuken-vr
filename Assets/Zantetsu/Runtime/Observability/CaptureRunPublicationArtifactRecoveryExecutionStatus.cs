namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal status carried by a Capture Run publication artifact recovery
    /// result. Values are fixed, explicitly numbered, and append-only;
    /// existing values must never be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value except <see cref="Deferred"/> mirrors a completed
    /// <c>PngJsonCapturePublicationArtifactRecoveryExecutionResult</c>.
    /// <see cref="Deferred"/> is a pre-inspection terminal: the inspection
    /// verification buffer could not be rented for one attempt, so no snapshot,
    /// decision, plan, batch, or execution result exists and the corresponding
    /// orchestration result carries <c>Deferred</c> directly.
    /// </para>
    /// </remarks>
    internal enum CaptureRunPublicationArtifactRecoveryExecutionStatus : int
    {
        None = 0,
        ReinspectionRequired = 1,
        CaptureCompleteCleanupRequired = 2,
        OrphanedPreTrace = 3,
        ArtifactSourceMissing = 4,
        PublishedArtifactMissing = 5,
        RunRootCollision = 6,
        Deferred = 7
    }
}
