namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free cleanup action of one Capture Run publication
    /// capture-complete cleanup step. Values are fixed, explicitly numbered,
    /// and append-only; existing values must never be renumbered, removed, or
    /// reused, because durable cleanup plans depend on the stable meaning of
    /// each value.
    /// </summary>
    internal enum CaptureRunPublicationCaptureCompleteCleanupAction : int
    {
        None = 0,
        DeletePublicationPlanTemporary = 1,
        DeleteCaptureIndexTemporary = 2,
        DeleteStagingArtifact = 3,
        RemoveStagingFramesRoot = 4,
        DeletePublicationPlan = 5,
        DeleteStagingReadyMarker = 6,
        DeleteStagingInitializationMarker = 7,
        RemoveStagingRunRoot = 8,
        CaptureCompleteReady = 9
    }
}
