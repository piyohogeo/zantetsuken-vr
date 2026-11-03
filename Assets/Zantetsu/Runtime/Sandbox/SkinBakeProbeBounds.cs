using System;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A conservative box of a skinned shape, without the shape (DESIGN 4.5.14, SKIN-BAKE-PROBE-2): the mesh's own
    /// bounds (its bind-pose vertices) taken through every palette matrix, the eight corners each, their union, with
    /// the origin, widened by a margin. Every skinned position is sum of w_b (M_b p) with p inside the mesh bounds and
    /// 0 &lt;= w_b, sum of w_b &lt;= 1: a convex combination of points of the union and of the origin, so it lies in the
    /// box. The cost is the bones, not the vertices: the same for the CPU and the GPU bake, taken apart from either.
    /// </summary>
    public static class SkinBakeProbeBounds
    {
        /// <summary>The box; <paramref name="boneCount"/> palette entries are used (at least one).</summary>
        public static Bounds Conservative(Bounds meshBounds, float4x4[] palette, int boneCount, float margin)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            if (boneCount < 1 || boneCount > palette.Length) throw new ArgumentOutOfRangeException(nameof(boneCount));
            float3 lo = meshBounds.min, hi = meshBounds.max;
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

        /// <summary>True where every position of <paramref name="vertices"/> (the valid ones) lies inside <paramref name="box"/>; the count outside.</summary>
        public static bool Contains(Bounds box, Rendering.VpRenderVertex[] vertices, out int outside)
        {
            outside = 0;
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!vertices[i].HasValidAttributes) continue;
                if (!box.Contains(vertices[i].position)) outside++;
            }

            return outside == 0;
        }
    }
}
