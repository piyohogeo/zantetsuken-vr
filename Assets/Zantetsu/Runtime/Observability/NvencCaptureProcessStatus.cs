namespace Zantetsu.Observability
{
    /// <summary>
    /// Process-wide fail-stop state for the Phase 0.11 NVENC capture path.
    /// Numeric values are fixed. <see cref="Running"/> advances to
    /// <see cref="Draining"/> or to
    /// <see cref="PoisonedUntilProcessRestart"/>, and
    /// <see cref="Draining"/> returns to <see cref="Running"/> only when a Run
    /// completes normally or in a controlled failure, so the next Run can be
    /// admitted. <see cref="PoisonedUntilProcessRestart"/> is the only
    /// irreversible state: nothing leaves it until the process restarts.
    /// </summary>
    internal enum NvencCaptureProcessStatus : int
    {
        Running = 0,
        Draining = 1,
        PoisonedUntilProcessRestart = 2,
    }
}
