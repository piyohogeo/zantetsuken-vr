namespace Zantetsu.Trace
{
    /// <summary>
    /// Integrity state of a capture run recorded on a
    /// <see cref="TraceEventType.TraceIntegritySummary"/> event. Append-only;
    /// existing values must never be reordered or reused.
    /// </summary>
    public enum TraceIntegrityState : int
    {
        /// <summary>The run completed with no observed sealed write failure or capture overflow.</summary>
        Complete = 0,

        /// <summary>A sealed write failure or a capture overflow was observed for the run.</summary>
        Incomplete = 1,
    }
}
