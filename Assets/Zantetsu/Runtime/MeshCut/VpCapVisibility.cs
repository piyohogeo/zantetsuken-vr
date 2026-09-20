using System;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// One eye as the cap visibility test reads it: where the eye is, and the matrix that takes a world point to that
    /// eye's clip space.
    /// <para>
    /// <see cref="worldToClip"/> is Unity's **non-GPU** convention, the one <c>Camera.projectionMatrix</c> and
    /// <c>Camera.GetStereoProjectionMatrix</c> give, multiplied by the matching world-to-camera matrix: a point is
    /// inside when <c>-w &lt;= x, y, z &lt;= w</c>. <see cref="position"/> is the same eye's centre of projection in
    /// world space. Nothing here checks that the two agree; that is the caller's to supply.
    /// </para>
    /// </summary>
    public readonly struct VpCapEye
    {
        public VpCapEye(Vector3 position, Matrix4x4 worldToClip)
        {
            this.position = position;
            this.worldToClip = worldToClip;
        }

        /// <summary>The eye's centre of projection, in world space.</summary>
        public readonly Vector3 position;

        /// <summary>World to this eye's clip space, with the inside being <c>-w &lt;= x, y, z &lt;= w</c>.</summary>
        public readonly Matrix4x4 worldToClip;
    }

    /// <summary>
    /// What the cap visibility test decided for one cap in one call. The two rules are reported apart; either one
    /// excludes the cap, and each needs both eyes to agree before it does.
    /// </summary>
    public readonly struct VpCapVisibilityVerdict
    {
        internal VpCapVisibilityVerdict(bool backFacingInBothEyes, bool outsideBothFrustums)
        {
            this.backFacingInBothEyes = backFacingInBothEyes;
            this.outsideBothFrustums = outsideBothFrustums;
        }

        /// <summary>Both eyes are more than the facing epsilon behind the cap's outward side.</summary>
        public readonly bool backFacingInBothEyes;

        /// <summary>The cap's polygon is wholly outside the left eye's frustum and wholly outside the right eye's.</summary>
        public readonly bool outsideBothFrustums;

        /// <summary>Whether the cap stays a drawing candidate: excluded by neither rule.</summary>
        public bool Keep => !backFacingInBothEyes && !outsideBothFrustums;
    }

    /// <summary>
    /// The both-eye Frustum and Facing test of DESIGN 5.7 / T-068 for one prepared cap of a
    /// <see cref="VpLogicalCutDisplay"/>: whether that cap stays a drawing candidate this frame.
    /// <para>
    /// **What it reads.** The cap's own record and its polygon, as the display prepared them — world-space vertices
    /// of the box section cut by the render fragment's other selected half-spaces, with the render fragment's own
    /// placement applied, and the outward normal of the side the cap closes. No cap is built again here and no other representation of one is made; the transform is
    /// the one that collection used. Nothing reads a triangle, a topology, the stencil or any occlusion.
    /// </para>
    /// <para>
    /// **Facing.** For each eye, <c>d = dot(outwardNormal, eye - capPoint)</c>, the cap point being the polygon's
    /// first vertex (the polygon is flat, so any of its vertices lies in the same plane). The cap is excluded only when
    /// <c>d &lt; -facingEpsilon</c> in **both** eyes. Equality, anything inside the epsilon band, and a cap one eye
    /// faces are all kept. Each cap is judged with its own outward normal only: the positive and the negative cap of
    /// one cut are two separate questions, a fixed side is asked like any other, and neither cap is hidden because of
    /// the other.
    /// </para>
    /// <para>
    /// **Frustum.** For each eye, every vertex is taken to clip space with no perspective division, and the eye rejects
    /// the cap only when all its vertices are strictly outside the **same** one of the six planes
    /// <c>-w &lt;= x, y, z &lt;= w</c>. Those are half-spaces of the world, so a convex polygon with every vertex
    /// outside one of them is wholly outside it. A vertex on a plane is not outside it, and a polygon whose corners are
    /// each outside a different plane is not rejected. Crossing the near plane, or reaching behind the eye where
    /// <c>w &lt; 0</c>, is never by itself a reason to reject — but a polygon that does so is still rejected when all of
    /// its vertices are established to be outside one other plane in common. The cap is excluded only when both eyes
    /// reject it.
    /// </para>
    /// <para>
    /// **What "conservative" covers.** Only the geometric rule, for finite input that keeps the conventions above and
    /// taken as exact arithmetic: it may keep a cap no eye sees, and it does not reject a polygon that meets the
    /// frustum. The arithmetic here is float and no numerical margin is applied yet (DESIGN O-034), so within rounding
    /// of a plane the answer can go either way. Nothing here speaks for rasterization, MSAA coverage or what the
    /// stencil passes finally draw.
    /// </para>
    /// <para>
    /// **Mono.** Without XR there is one camera, and <see cref="TryClassifyMono"/> asks with that camera as both eyes:
    /// the cap is excluded when that one camera is behind it, or when the polygon is outside that one frustum.
    /// </para>
    /// <para>
    /// **Epsilon.** The facing epsilon is a world-space length and the caller's. No product value is chosen here:
    /// DESIGN O-034 leaves it to be decided, and the vertex-merge epsilon of the Cap Bounds Polygon is a different
    /// quantity.
    /// </para>
    /// <para>
    /// **Not finite.** A cap whose polygon or outward normal is not finite is kept, whatever the eyes are. An eye whose
    /// position or matrix is not finite confirms neither rule, so the other eye alone never excludes the cap. A facing
    /// distance or a clip coordinate that comes out not finite from finite input — an overflow — confirms nothing
    /// either. No value is repaired or replaced.
    /// </para>
    /// <para>
    /// Each call is judged from what it is given and nothing else: no earlier frame, verdict or eye is kept, and the
    /// display is only read. What is drawn is not changed by this; the body and its ShadowCaster are never its to
    /// remove.
    /// </para>
    /// </summary>
    public static class VpCapVisibility
    {
        /// <summary>
        /// Judges the prepared cap <paramref name="capIndex"/> of <paramref name="display"/>'s settled collection for
        /// the two eyes given, reading the record's outward normal and its vertices -- up to
        /// <see cref="VpCapPolygonClip.MaxVertices"/> -- and asking the same test the display's own preparation asks.
        /// False, with a default verdict, when there is no such cap or it is empty: an empty cap has nothing to judge,
        /// is never drawn, and is not an omission.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="display"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="facingEpsilon"/> is negative or not finite.</exception>
        public static bool TryClassify(
            VpLogicalCutDisplay display,
            int capIndex,
            in VpCapEye left,
            in VpCapEye right,
            float facingEpsilon,
            out VpCapVisibilityVerdict verdict)
        {
            if (display == null)
            {
                throw new ArgumentNullException(nameof(display));
            }

            CheckEpsilon(facingEpsilon);

            verdict = default;
            if (!display.TryGetCapRecord(capIndex, out LogicalCutCapRecord record)
                || record.vertexCount < 1
                || record.vertexCount > VpCapPolygonClip.MaxVertices)
            {
                return false;
            }

            Span<Vector3> polygon = stackalloc Vector3[VpCapPolygonClip.MaxVertices];
            for (int i = 0; i < record.vertexCount; i++)
            {
                if (!display.TryGetCapVertex(capIndex, i, out polygon[i]))
                {
                    return false;
                }
            }

            verdict = Classify(polygon.Slice(0, record.vertexCount), record.outwardNormal, left, right, facingEpsilon);
            return true;
        }

        /// <summary>The same test without XR: the one camera stands for both eyes.</summary>
        public static bool TryClassifyMono(
            VpLogicalCutDisplay display,
            int capIndex,
            in VpCapEye eye,
            float facingEpsilon,
            out VpCapVisibilityVerdict verdict)
        {
            return TryClassify(display, capIndex, eye, eye, facingEpsilon, out verdict);
        }

        /// <summary>
        /// The test itself, over a polygon given directly: world-space vertices, wound or not, and the outward normal of
        /// the side it closes.
        /// </summary>
        internal static VpCapVisibilityVerdict Classify(
            ReadOnlySpan<Vector3> polygon,
            Vector3 outwardNormal,
            in VpCapEye left,
            in VpCapEye right,
            float facingEpsilon)
        {
            CheckEpsilon(facingEpsilon);
            if (polygon.Length < 1)
            {
                throw new ArgumentException("A cap has at least one vertex.", nameof(polygon));
            }

            // A cap that is not finite is not judged at all: it is kept.
            if (!IsFinite(outwardNormal))
            {
                return default;
            }

            for (int i = 0; i < polygon.Length; i++)
            {
                if (!IsFinite(polygon[i]))
                {
                    return default;
                }
            }

            // An eye that is not finite confirms neither rule, so the other eye alone never excludes.
            bool leftUsable = IsUsable(left);
            bool rightUsable = IsUsable(right);
            bool backFacing = leftUsable && rightUsable
                && IsBackFacing(polygon[0], outwardNormal, left.position, facingEpsilon)
                && IsBackFacing(polygon[0], outwardNormal, right.position, facingEpsilon);
            bool outside = leftUsable && rightUsable
                && IsOutsideFrustum(polygon, left.worldToClip)
                && IsOutsideFrustum(polygon, right.worldToClip);
            return new VpCapVisibilityVerdict(backFacing, outside);
        }

        private static void CheckEpsilon(float facingEpsilon)
        {
            if (!IsFinite(facingEpsilon) || facingEpsilon < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(facingEpsilon), facingEpsilon, "The facing epsilon is a finite length, zero or more.");
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsUsable(in VpCapEye eye)
        {
            if (!IsFinite(eye.position))
            {
                return false;
            }

            for (int i = 0; i < 16; i++)
            {
                if (!IsFinite(eye.worldToClip[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Strictly past the band. A distance that came out not finite — finite inputs can still overflow — confirms
        /// nothing and keeps the cap.
        /// </summary>
        private static bool IsBackFacing(Vector3 capPoint, Vector3 outwardNormal, Vector3 eye, float facingEpsilon)
        {
            float d = Vector3.Dot(outwardNormal, eye - capPoint);
            return IsFinite(d) && d < -facingEpsilon;
        }

        /// <summary>
        /// Whether every vertex is strictly outside one and the same clip plane. Each vertex gives the set of planes it
        /// is outside of, and those sets are intersected; a vertex on a plane is outside none of them. A vertex whose
        /// clip coordinates came out not finite settles nothing, so this eye does not reject the cap.
        /// </summary>
        private static bool IsOutsideFrustum(ReadOnlySpan<Vector3> polygon, in Matrix4x4 worldToClip)
        {
            const int all = 0x3f;
            int common = all;
            for (int i = 0; i < polygon.Length && common != 0; i++)
            {
                Vector3 p = polygon[i];
                Vector4 c = worldToClip * new Vector4(p.x, p.y, p.z, 1f);
                if (!IsFinite(c.x) || !IsFinite(c.y) || !IsFinite(c.z) || !IsFinite(c.w))
                {
                    return false;
                }

                int outsideOf = 0;
                if (c.x < -c.w)
                {
                    outsideOf |= 0x01;
                }
                if (c.x > c.w)
                {
                    outsideOf |= 0x02;
                }
                if (c.y < -c.w)
                {
                    outsideOf |= 0x04;
                }
                if (c.y > c.w)
                {
                    outsideOf |= 0x08;
                }
                if (c.z < -c.w)
                {
                    outsideOf |= 0x10;
                }
                if (c.z > c.w)
                {
                    outsideOf |= 0x20;
                }
                common &= outsideOf;
            }

            return common != 0;
        }
    }
}
