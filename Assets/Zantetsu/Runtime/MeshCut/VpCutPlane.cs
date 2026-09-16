using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The conversion between a cut plane given in world space and the same plane in a geometry's own coordinates,
    /// which is what <see cref="VpIndirectCutDisplay.TryRequestCut(Zantetsu.Rendering.VpStoredGeometry, float4, in VpStorageCutOptions)"/>
    /// and the storage cut take. It holds no state and owns nothing: the geometry, its vertices, its indices and the
    /// transform itself are all left exactly as they are, and the local-plane API is unchanged.
    /// <para>
    /// A plane is <c>dot(n, x) + d = 0</c>, carried as <c>(n.xyz, d)</c>. For a column-vector transform T taking the
    /// geometry's coordinates to world, a plane transforms by the transpose — <c>πlocal = transpose(T) · πworld</c> —
    /// and not as a point or a direction (DESIGN 19.5.1). The result's normal and d are then divided by the **same
    /// positive** length, so the plane keeps its position and both of its sides.
    /// </para>
    /// <para>
    /// **The sign is never flipped.** Whatever was on the positive side in world is on the positive side here, so a
    /// caller's positive and negative keep their meaning through the conversion and the cut that follows (DESIGN
    /// 19.5.1: 符号を任意反転して子IDを交換しない). The signed distance itself is not preserved — it comes out
    /// divided by the transformed normal's length — but the plane and the side each point falls on are.
    /// </para>
    /// <para>
    /// **The transform is the caller's snapshot.** This asks for nothing of its own: it does not read a display's held
    /// transform, a later pose or anything else. A caller settles on a transform, converts once, and asks for the cut
    /// with the plane that came out; a transform changed afterwards does not re-make the plane. Prediction, hit
    /// selection and the physics of DESIGN 19.5.1 are no part of this.
    /// </para>
    /// </summary>
    public static class VpCutPlane
    {
        /// <summary>
        /// The plane <paramref name="worldPlane"/> in the coordinates of a geometry placed by
        /// <paramref name="geometryLocalToWorld"/>. Ordinary translation, rotation and uniform or non-uniform scale are
        /// all handled, and the world plane need not arrive normalized.
        /// <para>
        /// Returns false with a default plane, having changed nothing, when:
        /// </para>
        /// <list type="bullet">
        /// <item>a coefficient of the plane or of the matrix is not finite;</item>
        /// <item>the world normal is zero, so there is no plane to convert;</item>
        /// <item>the matrix is not affine — its last row is not exactly (0, 0, 0, 1), compared value by value, with no
        /// tolerance. The transpose carries homogeneous planes through a projective matrix perfectly well; that is not
        /// the reason. This API is for geometry placed the ordinary way, and for the positive and negative sides a cut
        /// means in Euclidean space, so a projective placement is refused here rather than taken on;</item>
        /// <item>the transform is degenerate **in this normal's direction**, so that the transposed normal comes out
        /// zero or not finite, and there is no plane left to hand back. This is decided by the normal, not by the
        /// transform alone: a transform that flattens one axis still converts a plane whose normal it does not flatten,
        /// and only the normals it does are refused. No substitute plane is invented either way.</item>
        /// </list>
        /// Nothing is replaced by an identity or by some other plane on failure: the caller is told, and decides.
        /// </summary>
        public static bool TryWorldToGeometryLocal(float4 worldPlane, Matrix4x4 geometryLocalToWorld, out float4 localPlane)
        {
            localPlane = default;
            if (!IsFinite(worldPlane) || math.lengthsq(worldPlane.xyz) <= 0f || !IsFinite(geometryLocalToWorld))
            {
                return false;
            }

            // Affine placement only, and "exactly (0, 0, 0, 1)" means exactly: compared value by value, because
            // Vector4's own equality is approximate and would let a row that is merely close - (0, 0, 1e-6, 1 + 1e-6),
            // say - through as if it were the identity's.
            if (geometryLocalToWorld.m30 != 0f
                || geometryLocalToWorld.m31 != 0f
                || geometryLocalToWorld.m32 != 0f
                || geometryLocalToWorld.m33 != 1f)
            {
                return false;
            }

            // The plane goes through the transpose, as a plane and not as a point (DESIGN 19.5.1).
            Vector4 transformed = geometryLocalToWorld.transpose * new Vector4(worldPlane.x, worldPlane.y, worldPlane.z, worldPlane.w);
            var normal = new float3(transformed.x, transformed.y, transformed.z);
            float length = math.length(normal);
            if (!(length > 0f) || float.IsInfinity(length) || float.IsNaN(length))
            {
                return false;
            }

            // The same positive length for both, so the plane keeps its place and its sides.
            var candidate = new float4(normal / length, transformed.w / length);
            if (!IsFinite(candidate))
            {
                return false;
            }

            localPlane = candidate;
            return true;
        }

        private static bool IsFinite(float4 value)
        {
            return math.all(math.isfinite(value));
        }

        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int row = 0; row < 4; row++)
            {
                Vector4 line = matrix.GetRow(row);
                if (!math.all(math.isfinite(new float4(line.x, line.y, line.z, line.w))))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
