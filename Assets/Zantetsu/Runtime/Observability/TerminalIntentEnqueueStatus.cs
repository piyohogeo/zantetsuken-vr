namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of enqueueing a capture frame draft terminal intent into the
    /// future terminal intent queue. Explicitly valued and append-only; numeric
    /// values are fixed and must never be reused or reordered.
    /// </summary>
    internal enum TerminalIntentEnqueueStatus : int
    {
        /// <summary>The intent was accepted and its logical ownership transferred.</summary>
        Accepted = 0,

        /// <summary>The queue could not accept the intent right now; the producer keeps ownership.</summary>
        Backpressured = 1,

        /// <summary>The draft is already terminal (staged or dropped).</summary>
        DraftAlreadyTerminal = 2,

        /// <summary>The per-draft intent limit was exceeded.</summary>
        IntentLimitExceeded = 3,

        /// <summary>The run is no longer accepting terminal intents.</summary>
        RunNotAccepting = 4,

        /// <summary>The intent is invalid.</summary>
        InvalidIntent = 5,
    }
}
