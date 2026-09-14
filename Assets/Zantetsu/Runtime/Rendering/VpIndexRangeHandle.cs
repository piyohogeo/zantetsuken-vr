namespace Zantetsu.Rendering
{
    /// <summary>
    /// Opaque reference to one registration of an index range in a <see cref="VpIndexRangeLifecycleTable"/>: the table,
    /// the descriptor and the registration generation. The default handle, a handle of another table and a handle of
    /// an earlier registration of the same descriptor are rejected.
    /// </summary>
    public readonly struct VpIndexRangeHandle
    {
        internal readonly int tableId;
        internal readonly int descriptor;
        internal readonly uint generation;

        internal VpIndexRangeHandle(int tableId, int descriptor, uint generation)
        {
            this.tableId = tableId;
            this.descriptor = descriptor;
            this.generation = generation;
        }
    }
}
