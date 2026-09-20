using System;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The screen-space arithmetic shared by the projection conflict tests: a world shape taken to one eye's clip space
    /// and divided into normalized device coordinates, and the separating-axis test of two convex shapes grown by a
    /// margin rectangle. It answers "apart" only when that is shown; every doubt is "may overlap". Nothing is kept
    /// between calls, nothing is allocated, and the caller owns every span.
    /// <para>
    /// **Projection.** Every clip coordinate is checked to be finite first; any value that is not finite settles the
    /// shape as <see cref="Projection.NotFinite"/>. A shape whose points are all outside the near plane, or all
    /// outside the far plane, is clipped away (<see cref="Projection.Nothing"/>). When any point has <c>w &lt;= 0</c>
    /// and the shape is not wholly past the near plane, no division is done and the shape may be anywhere
    /// (<see cref="Projection.Everywhere"/>). Otherwise the points are divided, off-screen values kept, and the shape is
    /// nothing only when its rectangle grown by the margin is still wholly off the screen square <c>[-1, 1]²</c>.
    /// </para>
    /// <para>
    /// **Offset.** A shape can be given with an offset that is added to each point, once, as the point is read, so
    /// that a caller holding a shape in one place and drawing it in another projects it where it is drawn without
    /// copying it. The cut display gives none: its cap vertices are already where they are drawn.
    /// </para>
    /// <para>
    /// **Margin.** In normalized device coordinates per axis; every shape is grown by <c>[-x, x] × [-y, y]</c> on each
    /// side, and two shapes touching exactly are not apart.
    /// </para>
    /// </summary>
    internal static class VpScreenProjection
    {
        internal enum Projection
        {
            /// <summary>
            /// Clipped away by the near or the far plane, or projected but off the screen even when grown by the margin:
            /// nothing of it reaches what is drawn.
            /// </summary>
            Nothing,

            /// <summary>Every point in front of the eye's plane: the projected points bound it.</summary>
            Points,

            /// <summary>
            /// Finite, but it reaches the eye's plane or behind it without being wholly past the near plane: its place on
            /// the screen cannot be bounded, so it may be anywhere.
            /// </summary>
            Everywhere,

            /// <summary>A value or a result was not finite: nothing about this shape is settled, and the eye may overlap.</summary>
            NotFinite,
        }

        /// <summary>
        /// Projects <paramref name="points"/>, each moved by <paramref name="offset"/>, into <paramref name="ndc"/> (as
        /// long as the points), and says what the projection is.
        /// </summary>
        internal static Projection Project(
            ReadOnlySpan<Vector3> points, in Matrix4x4 worldToClip, Vector2 margin, Span<Vector2> ndc)
        {
            // Every clip coordinate is finite before anything is decided from any of them.
            bool allBeforeNear = true;
            bool allBeyondFar = true;
            bool allInFront = true;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 p = points[i];
                if (!IsFinite(p.x) || !IsFinite(p.y) || !IsFinite(p.z))
                {
                    return Projection.NotFinite;
                }

                Vector4 c = worldToClip * new Vector4(p.x, p.y, p.z, 1f);
                if (!IsFinite(c.x) || !IsFinite(c.y) || !IsFinite(c.z) || !IsFinite(c.w))
                {
                    return Projection.NotFinite;
                }

                allBeforeNear &= c.z < -c.w;
                allBeyondFar &= c.z > c.w;
                allInFront &= c.w > 0f;
                if (c.w > 0f)
                {
                    ndc[i] = new Vector2(c.x / c.w, c.y / c.w);
                }
            }

            // Clipped away in depth, which no margin on the screen brings back.
            if (allBeforeNear || allBeyondFar)
            {
                return Projection.Nothing;
            }

            if (!allInFront)
            {
                return Projection.Everywhere;
            }

            for (int i = 0; i < points.Length; i++)
            {
                if (!IsFinite(ndc[i].x) || !IsFinite(ndc[i].y))
                {
                    return Projection.NotFinite;
                }
            }

            // Off the screen only if it stays off when grown by the margin.
            Bound(ndc.Slice(0, points.Length), out Vector2 min, out Vector2 max);
            if (Gap(min.x - 1f, margin.x)
                || Gap(-1f - max.x, margin.x)
                || Gap(min.y - 1f, margin.y)
                || Gap(-1f - max.y, margin.y))
            {
                return Projection.Nothing;
            }

            return Projection.Points;
        }

        /// <summary>The bounding rectangles, each grown by the margin, are strictly apart on x or on y.</summary>
        internal static bool RectanglesApart(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, Vector2 margin)
        {
            Bound(a, out Vector2 minA, out Vector2 maxA);
            Bound(b, out Vector2 minB, out Vector2 maxB);
            return Gap(minB.x - maxA.x, 2f * margin.x)
                || Gap(minA.x - maxB.x, 2f * margin.x)
                || Gap(minB.y - maxA.y, 2f * margin.y)
                || Gap(minA.y - maxB.y, 2f * margin.y);
        }

        internal static void Bound(ReadOnlySpan<Vector2> points, out Vector2 min, out Vector2 max)
        {
            min = points[0];
            max = points[0];
            for (int i = 1; i < points.Length; i++)
            {
                min = Vector2.Min(min, points[i]);
                max = Vector2.Max(max, points[i]);
            }
        }

        /// <summary>
        /// Separating axes for two convex polygons grown by the margin rectangle: the screen axes and every edge normal
        /// of both. An edge of no length gives no axis, and fewer axes can only find fewer separations.
        /// </summary>
        internal static bool PolygonsApart(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, Vector2 margin)
        {
            if (ApartAlong(new Vector2(1f, 0f), a, b, margin) || ApartAlong(new Vector2(0f, 1f), a, b, margin))
            {
                return true;
            }

            return ApartAlongEdges(a, a, b, margin) || ApartAlongEdges(b, a, b, margin);
        }

        private static bool ApartAlongEdges(
            ReadOnlySpan<Vector2> edges, ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, Vector2 margin)
        {
            for (int i = 0; i < edges.Length; i++)
            {
                Vector2 e = edges[(i + 1) % edges.Length] - edges[i];
                var axis = new Vector2(-e.y, e.x);
                if (axis.x == 0f && axis.y == 0f)
                {
                    continue;
                }

                if (ApartAlong(axis, a, b, margin))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Apart along <paramref name="axis"/>, which need not be of unit length: the margin rectangle reaches
        /// <c>x·|axis.x| + y·|axis.y|</c> along it, on each shape.
        /// </summary>
        private static bool ApartAlong(Vector2 axis, ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, Vector2 margin)
        {
            Extent(axis, a, out float minA, out float maxA);
            Extent(axis, b, out float minB, out float maxB);
            float reach = (margin.x * Math.Abs(axis.x)) + (margin.y * Math.Abs(axis.y));
            return Gap(minB - maxA, 2f * reach) || Gap(minA - maxB, 2f * reach);
        }

        private static void Extent(Vector2 axis, ReadOnlySpan<Vector2> points, out float min, out float max)
        {
            min = Vector2.Dot(axis, points[0]);
            max = min;
            for (int i = 1; i < points.Length; i++)
            {
                float d = Vector2.Dot(axis, points[i]);
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }
        }

        /// <summary>A gap wider than what the margins take up. Anything not finite is no gap.</summary>
        internal static bool Gap(float gap, float taken)
        {
            return IsFinite(gap) && IsFinite(taken) && gap > taken;
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static bool IsFinite(in Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++)
            {
                if (!IsFinite(m[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
