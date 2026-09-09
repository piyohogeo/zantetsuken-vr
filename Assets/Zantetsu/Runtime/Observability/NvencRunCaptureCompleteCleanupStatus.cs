namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only status of one NVENC Run CaptureComplete cleanup attempt.
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// attempt outcome. <see cref="Cleaned"/> means the cleanup finished in one
    /// synchronous call and a receipt can be issued; <see cref="Failed"/> means
    /// it did not finish.
    /// </summary>
    /// <remarks>
    /// Failed deliberately carries no partial-progress shape, no per-target
    /// breakdown, no outcome-unknown case, and no retryable hint. Cleanup runs
    /// only after the Run's published evidence is already settled, so an
    /// unfinished cleanup changes nothing a finer status could act on; what it
    /// means for the Run is the later Coordinator's decision.
    /// </remarks>
    internal enum NvencRunCaptureCompleteCleanupStatus
    {
        None = 0,
        Cleaned = 1,
        Failed = 2,
    }
}
