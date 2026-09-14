namespace Zantetsu.Rendering
{
    /// <summary>
    /// Opaque token of one display instance's reference to a geometry in a <see cref="VpGeometryReferenceTable"/>: the
    /// table, the slot and the generation. It carries the reference's lifetime only, with no transform, colour, bounds or
    /// draw request. The default token, a token of another table and a token of a retired or earlier instance of the
    /// slot are rejected.
    /// </summary>
    public readonly struct VpDisplayInstanceReference
    {
        internal readonly int tableId;
        internal readonly int slot;
        internal readonly uint generation;

        internal VpDisplayInstanceReference(int tableId, int slot, uint generation)
        {
            this.tableId = tableId;
            this.slot = slot;
            this.generation = generation;
        }
    }
}
