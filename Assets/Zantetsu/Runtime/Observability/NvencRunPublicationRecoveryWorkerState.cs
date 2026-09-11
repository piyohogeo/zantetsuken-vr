namespace Zantetsu.Observability
{
    /// <summary>
    /// Lifecycle of the dedicated single-Run Phase 0.11 NVENC recovery worker,
    /// as this process observes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is in-memory observation only: nothing here is written to disk,
    /// restored from a previous process, or used as a recovery authority. What
    /// happened to the Run is the terminal result's and the lease's to say.
    /// </para>
    /// <para>
    /// <see cref="NotStarted"/> is before the thread exists.
    /// <see cref="Running"/> is a worker with work in progress.
    /// <see cref="AwaitingReleaseRetry"/> is the one parked state: the lease was
    /// partially released, so the same recovery can be attempted again when a
    /// retry is requested - never on its own. <see cref="Completed"/> carries a
    /// verified terminal result that has not been collected yet,
    /// <see cref="Collected"/> that it has, and <see cref="Faulted"/> a worker
    /// that stopped on an exception it retained.
    /// </para>
    /// <para>
    /// Values are explicit and append-only.
    /// </para>
    /// </remarks>
    internal enum NvencRunPublicationRecoveryWorkerState : int
    {
        NotStarted = 0,
        Running = 1,
        AwaitingReleaseRetry = 2,
        Completed = 3,
        Collected = 4,
        Faulted = 5
    }
}
