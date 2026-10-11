using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One command of a <see cref="VpIndirectDrawBatch"/>: a geometry range drawn at <see cref="instanceCount"/>
    /// consecutive instance transforms, with the range's local bounds for the batch's culling bounds.
    /// <para>
    /// <see cref="clipRoute"/> is the command's draw route in a batch that selects on the GPU and whose owner draws
    /// clipped instances apart (DESIGN 4.5.9): true says that every instance of the command is drawn by the owner's
    /// clip route, so the selection keeps none of them and its own draws draw none. It is the owner's to say, with
    /// the command, and nothing else reads it: a batch that does not select on the GPU draws the command as it is.
    /// </para>
    /// </summary>
    public readonly struct VpIndirectCommand
    {
        public readonly VpGeometryRange range;
        public readonly Bounds localBounds;
        public readonly int instanceCount;
        public readonly bool clipRoute;

        public VpIndirectCommand(VpGeometryRange range, Bounds localBounds, int instanceCount)
            : this(range, localBounds, instanceCount, false)
        {
        }

        public VpIndirectCommand(VpGeometryRange range, Bounds localBounds, int instanceCount, bool clipRoute)
        {
            this.range = range;
            this.localBounds = localBounds;
            this.instanceCount = instanceCount;
            this.clipRoute = clipRoute;
        }
    }
}
