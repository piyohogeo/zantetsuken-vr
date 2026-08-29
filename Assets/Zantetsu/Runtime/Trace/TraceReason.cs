namespace Zantetsu.Trace
{
    /// <summary>
    /// Rejection or failure reason recorded on a trace event. Values are
    /// append-only.
    /// </summary>
    public enum TraceReason : int
    {
        None = 0,

        /// <summary>A sealed trace enqueue failure was observed for the run.</summary>
        TraceWriteFailureObserved = 1,

        /// <summary>A trace capture overflow was observed for the run.</summary>
        TraceCaptureOverflowObserved = 2,
    }
}
