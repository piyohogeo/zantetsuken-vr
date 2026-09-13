namespace Zantetsu.Trace
{
    /// <summary>
    /// Integrity state of a capture run. The value is written into stored
    /// formats, so it is append-only: existing values must never be reordered
    /// or reused.
    /// </summary>
    public enum TraceIntegrityState : int
    {
        /// <summary>The run kept everything it was given.</summary>
        Complete = 0,

        /// <summary>Something the run was given was not kept.</summary>
        Incomplete = 1,
    }
}
