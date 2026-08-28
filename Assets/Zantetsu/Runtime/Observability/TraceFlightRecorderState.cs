namespace Zantetsu.Observability
{
    /// <summary>
    /// Lifecycle states of the trace flight recorder.
    /// </summary>
    public enum TraceFlightRecorderState : int
    {
        /// <summary>Waiting for a trigger; draining normally.</summary>
        Armed = 0,

        /// <summary>Triggered; duplicating post-roll events into the capture.</summary>
        CapturingPostRoll = 1,

        /// <summary>Capture is complete and immutable.</summary>
        Frozen = 2,

        /// <summary>
        /// The logger is sealed and the normal capture FIFO is closed; the
        /// recorder is reserved for the freeze terminal append. Only the
        /// terminal append path may proceed; normal capture APIs are rejected.
        /// </summary>
        AwaitingFreezeTerminal = 3,
    }
}
