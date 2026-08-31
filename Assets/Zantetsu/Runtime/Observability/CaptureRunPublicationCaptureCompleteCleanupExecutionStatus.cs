namespace Zantetsu.Observability
{
    /// <summary>
    /// Execution status reported by a Capture Run publication capture-complete
    /// cleanup execution. Values are fixed, explicitly numbered, and
    /// append-only; existing values must never be renumbered, removed, or
    /// reused, because durable cleanup plans and receipts depend on the stable
    /// meaning of each value.
    /// </summary>
    internal enum CaptureRunPublicationCaptureCompleteCleanupExecutionStatus : int
    {
        None = 0,
        CaptureCompleteReady = 1
    }
}
