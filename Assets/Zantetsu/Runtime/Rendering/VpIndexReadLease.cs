namespace Zantetsu.Rendering
{
    /// <summary>
    /// Opaque token of one read lease on a published index range (DESIGN 4.5.3): the range handle it was taken
    /// through and the individual loan. Every acquisition is a separate loan that is returned exactly once.
    /// </summary>
    public readonly struct VpIndexReadLease
    {
        internal readonly VpIndexRangeHandle range;
        internal readonly int slot;
        internal readonly uint slotGeneration;

        internal VpIndexReadLease(VpIndexRangeHandle range, int slot, uint slotGeneration)
        {
            this.range = range;
            this.slot = slot;
            this.slotGeneration = slotGeneration;
        }
    }
}
