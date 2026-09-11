namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal shape of one Phase 0.11 NVENC publication recovery
    /// incomplete/orphan cleanup attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// shape. <see cref="Cleaned"/> means one synchronous call finished the
    /// orphan cleanup, so a receipt may be issued. <see cref="Failed"/> means
    /// it did not finish, and carries no receipt.
    /// </para>
    /// <para>
    /// <see cref="Failed"/> does not mean nothing was deleted. An orphan
    /// cleanup removes more than one thing, so a failure may leave part of that
    /// work already done, and this status deliberately does not describe how
    /// far it got: there is no partial-progress ledger, per-target status,
    /// unknown outcome, or retryable flag. A result of this attempt is
    /// therefore not an authority on progress. What actually remains on disk is
    /// answered only by a later recovery re-observing the real filesystem while
    /// newly holding the Run's lock.
    /// </para>
    /// <para>
    /// Values are explicit and append-only.
    /// </para>
    /// </remarks>
    internal enum NvencRunPublicationRecoveryIncompleteCleanupStatus : int
    {
        None = 0,
        Cleaned = 1,
        Failed = 2
    }
}
