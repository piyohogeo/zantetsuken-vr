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
    }
}
