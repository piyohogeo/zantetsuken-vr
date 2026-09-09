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
    /// breakdown, no outcome-unknown case, and no retryable hint. The cleanup
    /// contract allows several deletions with no rollback, so a Failed cleanup
    /// can leave the staging side partly removed. This status is simply not the
    /// authority on how far it got: a later step re-inspects the actual file
    /// set under the still-held Run lock rather than reading progress out of
    /// the result. What a Failed cleanup means for the Run is the later
    /// Coordinator's decision.
    /// </remarks>
    internal enum NvencRunCaptureCompleteCleanupStatus
    {
        None = 0,
        Cleaned = 1,
        Failed = 2,
    }
}
