using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The bounds of the vertices a submesh's indices name, as the geometry's producer measured them where it wrote
    /// them: a minimum and a maximum corner, in the geometry's own coordinates. Recorded beside the submesh so that
    /// whoever draws the geometry need not walk its indices to find them.
    /// </summary>
    public readonly struct VpGeometryBounds
    {
        public readonly Vector3 min;
        public readonly Vector3 max;

        public VpGeometryBounds(Vector3 min, Vector3 max)
        {
            this.min = min;
            this.max = max;
        }

        public Bounds ToBounds()
        {
            return new Bounds((min + max) * 0.5f, max - min);
        }
    }
}
