using System;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A conservative box of a skinned shape, without the shape (DESIGN 4.5.14/4.5.15): the mesh's bind-pose bounds
    /// taken through every palette matrix, the eight corners each, their union with the origin, widened by a margin.
    /// Every skinned position is a sum of w_b (M_b p) with p inside the bind bounds, w_b non-negative and summing to at
    /// most one: a convex combination of points of that union and of the origin, so it lies in the box. The cost is the
    /// bones, not the vertices. It is the box the display culls a GPU-baked region by until the CPU geometry is ready.
    /// </summary>
    public static class VpSkinConservativeBounds
    {
        /// <summary>The margin the box is widened by on every side, in the model's units (the skinning's rounding).</summary>
        public const float Margin = 0.01f;

        /// <summary>The box of <paramref name="boneCount"/> palette entries (at least one) over <paramref name="bindBounds"/>.</summary>
        public static unsafe Bounds Of(Bounds bindBounds, float4x4* palette, int boneCount, float margin = Margin)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (boneCount < 1) throw new ArgumentOutOfRangeException(nameof(boneCount));
            float3 lo = bindBounds.min, hi = bindBounds.max;
            float3 min = float3.zero, max = float3.zero;   // the origin is inside
            for (int b = 0; b < boneCount; b++)
            {
                float4x4 m = palette[b];
                for (int corner = 0; corner < 8; corner++)
                {
                    var p = new float4((corner & 1) != 0 ? hi.x : lo.x, (corner & 2) != 0 ? hi.y : lo.y, (corner & 4) != 0 ? hi.z : lo.z, 1f);
                    float3 q = math.mul(m, p).xyz;
                    min = math.min(min, q);
                    max = math.max(max, q);
                }
            }

            min -= margin;
            max += margin;
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }

        /// <summary>The same over a managed palette.</summary>
        public static unsafe Bounds Of(Bounds bindBounds, float4x4[] palette, int boneCount, float margin = Margin)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (boneCount > palette.Length) throw new ArgumentOutOfRangeException(nameof(boneCount));
            fixed (float4x4* p = palette) return Of(bindBounds, p, boneCount, margin);
        }

        /// <summary>Whether <paramref name="inner"/> lies inside <paramref name="outer"/> (every axis).</summary>
        public static bool Contains(Bounds outer, Bounds inner)
        {
            Vector3 omin = outer.min, omax = outer.max, imin = inner.min, imax = inner.max;
            return imin.x >= omin.x && imin.y >= omin.y && imin.z >= omin.z && imax.x <= omax.x && imax.y <= omax.y && imax.z <= omax.z;
        }
    }
}
