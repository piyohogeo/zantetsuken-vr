namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed one-slot terminal request boundary state for the Phase 0.11 Run
    /// chunk. A request is accepted only into <c>None</c>; the worker advances
    /// it to <c>Completed</c> exactly once; and the Main Thread collects it
    /// exactly once into <c>Collected</c>. There is no reserved or released
    /// state.
    /// </summary>
    internal enum NvencRunChunkTerminalRequestState
    {
        None = 0,
        FinalizeRequested = 1,
        AbandonRequested = 2,
        Completed = 3,
        Collected = 4,
    }
}
