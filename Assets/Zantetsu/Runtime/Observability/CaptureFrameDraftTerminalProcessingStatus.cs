namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of one <see cref="CaptureFrameDraftTerminalCoordinator"/> step.
    /// Explicitly valued and append-only; numeric values are fixed and must
    /// never be reused or reordered.
    /// </summary>
    internal enum CaptureFrameDraftTerminalProcessingStatus : int
    {
        /// <summary>The queue was empty; no intent was processed.</summary>
        None = 0,

        /// <summary>The draft was moved to Staged via the staging store.</summary>
        Staged = 1,

        /// <summary>The draft was moved to Dropped with one of the normal drop reasons.</summary>
        Dropped = 2,

        /// <summary>The intent was a loser because its draft was already terminal; it was discarded.</summary>
        DiscardedAlreadyTerminal = 3,
    }
}
