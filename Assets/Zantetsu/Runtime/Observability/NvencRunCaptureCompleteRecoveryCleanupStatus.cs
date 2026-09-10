namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal shape of one Phase 0.11 NVENC recovery CaptureComplete cleanup
    /// attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// shape. <see cref="Cleaned"/> means one synchronous call finished the
    /// cleanup, so a receipt may be issued. <see cref="Failed"/> means it did
    /// not finish, and carries no receipt.
    /// </para>
    /// <para>
    /// Cleanup can delete several things, so a failure may leave part of that
    /// work done. That is deliberately not described here: there is no
    /// per-target result, partial-progress ledger, unknown outcome, or
    /// retryable flag. What actually remains on disk after a failure is
    /// answered by the next recovery re-observing the filesystem under the OS
    /// lock this process still holds, not by this status.
    /// </para>
    /// <para>
    /// Values are explicit and append-only.
    /// </para>
    /// </remarks>
    internal enum NvencRunCaptureCompleteRecoveryCleanupStatus : int
    {
        None = 0,
        Cleaned = 1,
        Failed = 2
    }
}
