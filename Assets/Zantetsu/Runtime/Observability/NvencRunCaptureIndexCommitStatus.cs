namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only status of one NVENC Run capture index commit attempt.
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// attempt outcome. <see cref="Committed"/> means the capture index commit
    /// completed in one synchronous call and a receipt can be issued;
    /// <see cref="Failed"/> means it did not complete.
    /// </summary>
    /// <remarks>
    /// Failed deliberately does not distinguish before-rename from
    /// outcome-unknown, and carries no failure reason. Once the artifact has
    /// been published, every unsuccessful capture index commit is handed to
    /// Publication Recovery the same way, so a finer split adds no new
    /// decision. Failed is in particular not evidence that no final name
    /// exists, and not permission to retry in this process.
    /// </remarks>
    internal enum NvencRunCaptureIndexCommitStatus
    {
        None = 0,
        Committed = 1,
        Failed = 2,
    }
}
