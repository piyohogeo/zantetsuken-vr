namespace Zantetsu.Observability
{
    /// <summary>
    /// One-way, process-wide fail-stop state for the Phase 0.11 NVENC capture
    /// path. Numeric values are fixed. The state only ever advances from
    /// <see cref="Running"/> toward <see cref="Draining"/> or
    /// <see cref="PoisonedUntilProcessRestart"/> and never moves back.
    /// </summary>
    internal enum NvencCaptureProcessStatus : int
    {
        Running = 0,
        Draining = 1,
        PoisonedUntilProcessRestart = 2,
    }
}
