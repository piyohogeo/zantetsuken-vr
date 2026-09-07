namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only terminal state of the single Run chunk local registry slot.
    /// The only transitions are <c>Empty -&gt; Registered</c>,
    /// <c>Registered -&gt; Committed</c>, and the pre-commit abort
    /// <c>Registered -&gt; Empty</c>. There is no reserved, released, or
    /// consumed state.
    /// </summary>
    internal enum NvencRunLocalRegistrySlotState
    {
        Empty = 0,
        Registered = 1,
        Committed = 2,
    }
}
