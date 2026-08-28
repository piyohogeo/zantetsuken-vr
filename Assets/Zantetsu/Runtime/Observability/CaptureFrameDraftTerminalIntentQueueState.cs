namespace Zantetsu.Observability
{
    /// <summary>
    /// Lifecycle state of a capture frame draft terminal intent queue.
    /// Explicitly valued and append-only; numeric values are fixed and must
    /// never be reused or reordered.
    /// </summary>
    internal enum CaptureFrameDraftTerminalIntentQueueState : int
    {
        /// <summary>The queue accepts new terminal intents.</summary>
        Accepting = 0,

        /// <summary>Producers are draining; new intents are still accepted.</summary>
        ProducerDraining = 1,

        /// <summary>The queue no longer accepts new terminal intents.</summary>
        Closed = 2,
    }
}
