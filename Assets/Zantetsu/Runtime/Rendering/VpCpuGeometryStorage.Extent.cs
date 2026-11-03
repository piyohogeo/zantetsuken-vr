using UnityEngine;

namespace Zantetsu.Rendering
{
    public sealed partial class VpCpuGeometryStorage
    {
        /// <summary>
        /// Replaces the recorded bounds of a published geometry with <paramref name="localBounds"/>, the producer's
        /// own conservative box, keeping the referenced vertex span as recorded. For a geometry whose vertices are
        /// written on the GPU after the append (the bake probe of DESIGN 4.5.14: a placeholder is appended, the shape
        /// is baked into the same region by a compute dispatch), so that the draw's culling takes a box that contains
        /// the shape instead of the measure of the placeholder. Every submesh's bounds become the box. False where the
        /// geometry has no readable record or extent, or the box is not a finite, non-inverted box; nothing changes then.
        /// The bounds of a geometry the storage measured itself are never replaced by the product's paths.
        /// </summary>
        public bool TryReplaceExtentBounds(VpStoredGeometry geometry, Bounds localBounds)
        {
            ThrowIfDisposed();
            Vector3 min = localBounds.min, max = localBounds.max;
            if (!(min.x <= max.x && min.y <= max.y && min.z <= max.z)
                || float.IsInfinity(min.x) || float.IsInfinity(min.y) || float.IsInfinity(min.z)
                || float.IsInfinity(max.x) || float.IsInfinity(max.y) || float.IsInfinity(max.z))
            {
                return false;   // a NaN fails the first test
            }

            if (!IsMetadataReadable(geometry))
            {
                return false;
            }

            AppendRecord record = _appendOfDescriptor[geometry.indexRange.descriptor];
            if (record.generation != geometry.indexRange.generation || !record.hasExtent)
            {
                return false;
            }

            var box = new VpGeometryBounds(min, max);
            for (int s = 0; s < geometry.submeshCount; s++)
            {
                _submeshBounds[geometry.submeshStart + s] = box;
            }

            record.boundsMin = min;
            record.boundsMax = max;
            _appendOfDescriptor[geometry.indexRange.descriptor] = record;
            return true;
        }
    }
}
