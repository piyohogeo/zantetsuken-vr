using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One command of a <see cref="VpIndirectDrawBatch"/>: a geometry range drawn at <see cref="instanceCount"/>
    /// consecutive instance transforms, with the range's local bounds for the batch's culling bounds.
    /// </summary>
    public readonly struct VpIndirectCommand
    {
        public readonly VpGeometryRange range;
        public readonly Bounds localBounds;
        public readonly int instanceCount;

        public VpIndirectCommand(VpGeometryRange range, Bounds localBounds, int instanceCount)
        {
            this.range = range;
            this.localBounds = localBounds;
            this.instanceCount = instanceCount;
        }
    }
}
