namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only terminal state of a Phase 0.11 Run chunk context. The only
    /// transitions are <c>Open -&gt; Finalized</c> and
    /// <c>Open -&gt; Abandoned</c>; both are exclusive and exactly once. There
    /// is no externally observable finalizing state.
    /// </summary>
    internal enum NvencRunChunkContextState
    {
        None = 0,
        Open = 1,
        Finalized = 2,
        Abandoned = 3,
    }
}
