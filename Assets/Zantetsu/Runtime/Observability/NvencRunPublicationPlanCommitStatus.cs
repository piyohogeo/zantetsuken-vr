namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only status of one NVENC Run publication plan commit attempt.
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// attempt outcome. <see cref="Committed"/> means the final-name rename is
    /// known to have succeeded; <see cref="FailedBeforeRename"/> means the
    /// failure was determined before the final-name rename began; and
    /// <see cref="CommitOutcomeUnknown"/> means the rename was invoked but its
    /// outcome cannot be determined. Unknown is never guessed into committed
    /// or failed.
    /// </summary>
    internal enum NvencRunPublicationPlanCommitStatus
    {
        None = 0,
        Committed = 1,
        FailedBeforeRename = 2,
        CommitOutcomeUnknown = 3,
    }
}
