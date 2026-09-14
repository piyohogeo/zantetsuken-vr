namespace Zantetsu.Rendering
{
    /// <summary>
    /// Opaque token of one geometry registration in a <see cref="VpGeometryReferenceTable"/>: the table, the slot and the
    /// registration generation. The default token, a token of another table and a token of an ended or earlier
    /// registration of the slot are rejected.
    /// </summary>
    public readonly struct VpGeometryReference
    {
        internal readonly int tableId;
        internal readonly int slot;
        internal readonly uint generation;

        internal VpGeometryReference(int tableId, int slot, uint generation)
        {
            this.tableId = tableId;
            this.slot = slot;
            this.generation = generation;
        }
    }
}
