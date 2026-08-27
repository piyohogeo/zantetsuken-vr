namespace Zantetsu.Observability
{
    /// <summary>
    /// Seal states of a capture run <see cref="TraceLogger"/>.
    /// </summary>
    /// <remarks>
    /// Values are fixed and must never be reused or reordered: the gate encodes
    /// this state in a shared native slot and compares it atomically.
    /// </remarks>
    internal enum TraceRunSealState : int
    {
        /// <summary>Accepting new trace events.</summary>
        Open = 0,

        /// <summary>Seal in progress; events are rejected and drained.</summary>
        Sealing = 1,

        /// <summary>Seal complete; events are rejected.</summary>
        Sealed = 2,
    }
}
