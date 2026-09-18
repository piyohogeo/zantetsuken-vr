using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>What one eye's projections of two targets settled.</summary>
    public enum VpCapProjectionOverlap
    {
        /// <summary>Not asked: the two targets are compatible, so they may share a stencil whatever they overlap.</summary>
        NotEvaluated = 0,

        /// <summary>The body boxes' projected rectangles, grown by the margin, are apart: nothing further is asked.</summary>
        ApartByBounds = 1,

        /// <summary>The rectangles meet, but no visible cap of one meets a visible cap of the other, grown by the margin.</summary>
        ApartByCaps = 2,

        /// <summary>Apart could not be shown: the two may meet in this eye.</summary>
        MayOverlap = 3,
    }

    /// <summary>
    /// One target as the projection conflict test reads it: its cut conditions (which also carry the separation it is
    /// drawn at), its body box in its own frame and the placement of that box, the caps of it that the visibility test
    /// kept, as world-space polygons with the separation already in them, and whether those caps are all of its caps.
    /// </summary>
    public readonly struct VpCapProjectionTarget
    {
        public VpCapProjectionTarget(
            VpCapCompatibilityTarget conditions,
            Bounds localBounds,
            Matrix4x4 objectToWorld,
            IReadOnlyList<Vector3[]> visibleCaps,
            bool capsComplete)
        {
            this.conditions = conditions;
            this.localBounds = localBounds;
            this.objectToWorld = objectToWorld;
            this.visibleCaps = visibleCaps;
            this.capsComplete = capsComplete;
        }

        public readonly VpCapCompatibilityTarget conditions;

        /// <summary>The body's box in its own frame. The drawn side lies inside it, so it bounds the side from outside.</summary>
        public readonly Bounds localBounds;

        /// <summary>The placement of the box. The separation is <c>conditions.offset</c>, added after it.</summary>
        public readonly Matrix4x4 objectToWorld;

        /// <summary>
        /// The caps the visibility test kept, each a convex polygon of world-space vertices with the separation in it.
        /// Empty when none was kept.
        /// </summary>
        public readonly IReadOnlyList<Vector3[]> visibleCaps;

        /// <summary>
        /// Whether <see cref="visibleCaps"/> is every cap bound of this target — none left out by the visibility test
        /// or anything else — so that the caps step may stand for it. When false, a cap was omitted, and a missing cap
        /// says nothing about whether this target's volume leaves stencil under the other's cap: only the box can then
        /// show the two apart. That the volume is not drawn at all is not something this input says.
        /// </summary>
        public readonly bool capsComplete;
    }

    /// <summary>What the projection conflict test decided for one pair of targets.</summary>
    public readonly struct VpCapProjectionVerdict
    {
        internal VpCapProjectionVerdict(bool compatible, VpCapProjectionOverlap left, VpCapProjectionOverlap right)
        {
            this.compatible = compatible;
            this.left = left;
            this.right = right;
        }

        /// <summary>The two are under the same cut conditions, so they may share a stencil and neither eye is asked.</summary>
        public readonly bool compatible;

        public readonly VpCapProjectionOverlap left;
        public readonly VpCapProjectionOverlap right;

        /// <summary>
        /// Whether the two must not share an ordinary colour: they are incompatible and at least one eye could not show
        /// them apart.
        /// </summary>
        public bool MustSeparate =>
            !compatible && (left == VpCapProjectionOverlap.MayOverlap || right == VpCapProjectionOverlap.MayOverlap);
    }

    /// <summary>
    /// A **conservative conflict candidate** test between two stencil targets, from their bounds (DESIGN 5.6, T-066):
    /// whether two incompatible targets might put stencil where the other's cap reads it, in either eye, so that they
    /// must not share an ordinary colour.
    /// <para>
    /// **It does not reconstruct any Residual Stencil Support.** Nothing reads the stencil, a triangle, an edge or a
    /// pixel. It answers only from boxes and cap bounds polygons, and it answers "apart" only when that is shown; every
    /// doubt is "may overlap".
    /// </para>
    /// <para>
    /// **Order.** Compatible targets are not asked at all: they may share a stencil however they overlap. For two
    /// incompatible targets, each eye is asked in turn: first the projected rectangle of each body box — its eight
    /// corners placed, moved by the separation, and projected — grown by the margin; only if those meet, every visible
    /// cap of one against every visible cap of the other, each projected and grown by the margin, by separating axes.
    /// The caps step is taken only when both targets say their caps are complete and each has at least one: a cap
    /// left out by the visibility test, or no cap at all, is no evidence that the target's volume stays clear of the
    /// other's cap, so such a pair is "may overlap" unless the boxes already showed it apart. The pair must be
    /// separated when either eye leaves them possibly overlapping; one eye is enough. Geometry sign is not an input,
    /// so a target is never left out for having a negative contribution.
    /// </para>
    /// <para>
    /// **Projection.** Each shape is taken to clip space through the eye's <see cref="VpCapEye.worldToClip"/> (Unity's
    /// non-GPU convention). Every clip coordinate, z included, is checked first: any value or result that is not
    /// finite — an input, a clip coordinate, a quotient, in a box or in any cap of either target — decides "may overlap"
    /// for that eye, and nothing later in that eye turns it back into "apart", not even the other shape projecting to
    /// nothing. This is kept apart from a finite shape that merely cannot be bounded on the screen (below), which still
    /// goes on to the caps step. A shape whose points are all outside the near plane, or all outside the far plane, is clipped away
    /// and projects to nothing. Being outside a side of the field is **not** decided that way, because the margin may
    /// bring it back: when every point has <c>w &gt; 0</c> the shape is divided into normalized device coordinates,
    /// off-screen values kept — its projection lies inside the hull of the projected points, even where it crosses the
    /// near plane — and it projects to nothing only if its rectangle, grown by the margin, is still wholly off the
    /// screen square <c>[-1, 1]²</c>. When any point has <c>w &lt;= 0</c> and the shape is not wholly past the near
    /// plane, no division is done and the shape is taken to cover every place.
    /// </para>
    /// <para>
    /// **Margin.** <c>ndcMargin</c> is in normalized device coordinates, per axis (x across, y up; the screen spans two
    /// units on each), and every projected shape is grown by the rectangle <c>[-x, x] × [-y, y]</c> on each side before
    /// the two are compared — the rectangles in the first step and the cap polygons in the second. Two shapes touching
    /// exactly are not apart. The margin stands for rasterization, MSAA and head movement and is the caller's: no
    /// product value exists (DESIGN O-034), and the values in the tests are test values. Pixels become NDC per axis as
    /// <c>2 / width</c> and <c>2 / height</c>.
    /// </para>
    /// <para>
    /// Each call is judged from its input alone; nothing is kept. This builds no graph and assigns no colour.
    /// </para>
    /// </summary>
    public static class VpCapProjectionConflict
    {
        private const int BoxCorners = 8;

        /// <summary>
        /// Judges one pair of targets for the two eyes given. Compatibility is <see cref="VpCapCompatibility"/>'s, with
        /// the epsilons given for it.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A margin component is negative or not finite.</exception>
        public static VpCapProjectionVerdict Judge(
            in VpCapProjectionTarget a,
            in VpCapProjectionTarget b,
            in VpCapEye left,
            in VpCapEye right,
            Vector2 ndcMargin,
            float planeEpsilon,
            float offsetEpsilon)
        {
            if (!IsFinite(ndcMargin.x) || !IsFinite(ndcMargin.y) || ndcMargin.x < 0f || ndcMargin.y < 0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ndcMargin), ndcMargin, "The margin is finite and zero or more on each axis.");
            }

            if (VpCapCompatibility.AreCompatible(a.conditions, b.conditions, planeEpsilon, offsetEpsilon))
            {
                return new VpCapProjectionVerdict(true, VpCapProjectionOverlap.NotEvaluated, VpCapProjectionOverlap.NotEvaluated);
            }

            return new VpCapProjectionVerdict(
                false, InOneEye(a, b, left.worldToClip, ndcMargin), InOneEye(a, b, right.worldToClip, ndcMargin));
        }

        private static VpCapProjectionOverlap InOneEye(
            in VpCapProjectionTarget a, in VpCapProjectionTarget b, in Matrix4x4 worldToClip, Vector2 margin)
        {
            if (!IsFinite(worldToClip))
            {
                return VpCapProjectionOverlap.MayOverlap;
            }

            // 1. The body boxes.
            Span<Vector3> boxA = stackalloc Vector3[BoxCorners];
            Span<Vector3> boxB = stackalloc Vector3[BoxCorners];
            if (!TryCorners(a, boxA) || !TryCorners(b, boxB))
            {
                return VpCapProjectionOverlap.MayOverlap;
            }

            Span<Vector2> projectedA = stackalloc Vector2[BoxCorners];
            Span<Vector2> projectedB = stackalloc Vector2[BoxCorners];
            Projection boxAProjection = Project(boxA, worldToClip, margin, projectedA);
            Projection boxBProjection = Project(boxB, worldToClip, margin, projectedB);

            // A value that is not finite settles this eye before anything can call the pair apart.
            if (boxAProjection == Projection.NotFinite || boxBProjection == Projection.NotFinite)
            {
                return VpCapProjectionOverlap.MayOverlap;
            }

            if (boxAProjection == Projection.Nothing || boxBProjection == Projection.Nothing)
            {
                return VpCapProjectionOverlap.ApartByBounds;
            }

            if (boxAProjection == Projection.Points
                && boxBProjection == Projection.Points
                && RectanglesApart(projectedA, projectedB, margin))
            {
                return VpCapProjectionOverlap.ApartByBounds;
            }

            // 2. Every visible cap of one against every visible cap of the other — only when both lists are all of
            //    their target's caps and neither is empty. Otherwise the boxes, which meet, are all there is to go on.
            IReadOnlyList<Vector3[]> capsA = a.visibleCaps;
            IReadOnlyList<Vector3[]> capsB = b.visibleCaps;
            if (!a.capsComplete || !b.capsComplete || capsA == null || capsB == null || capsA.Count == 0 || capsB.Count == 0)
            {
                return VpCapProjectionOverlap.MayOverlap;
            }

            Span<Vector2> capA = stackalloc Vector2[VpCapBoundsPolygon.MaxVertices];
            Span<Vector2> capB = stackalloc Vector2[VpCapBoundsPolygon.MaxVertices];

            // Every cap of both is looked at once first, so that one which is not finite is never skipped over because
            // the cap it would have been compared with projects to nothing.
            if (AnyNotFinite(capsA, worldToClip, margin, capA) || AnyNotFinite(capsB, worldToClip, margin, capB))
            {
                return VpCapProjectionOverlap.MayOverlap;
            }

            for (int i = 0; i < capsA.Count; i++)
            {
                Vector3[] polygonA = capsA[i];
                if (polygonA == null || polygonA.Length < 1 || polygonA.Length > VpCapBoundsPolygon.MaxVertices)
                {
                    return VpCapProjectionOverlap.MayOverlap;
                }

                Projection projectionA = Project(polygonA, worldToClip, margin, capA);
                if (projectionA == Projection.Nothing)
                {
                    continue;
                }

                for (int j = 0; j < capsB.Count; j++)
                {
                    Vector3[] polygonB = capsB[j];
                    if (polygonB == null || polygonB.Length < 1 || polygonB.Length > VpCapBoundsPolygon.MaxVertices)
                    {
                        return VpCapProjectionOverlap.MayOverlap;
                    }

                    Projection projectionB = Project(polygonB, worldToClip, margin, capB);
                    if (projectionB == Projection.Nothing)
                    {
                        continue;
                    }

                    if (projectionA == Projection.Everywhere
                        || projectionB == Projection.Everywhere
                        || projectionA == Projection.NotFinite
                        || projectionB == Projection.NotFinite
                        || !PolygonsApart(capA.Slice(0, polygonA.Length), capB.Slice(0, polygonB.Length), margin))
                    {
                        return VpCapProjectionOverlap.MayOverlap;
                    }
                }
            }

            return VpCapProjectionOverlap.ApartByCaps;
        }

        /// <summary>Whether a cap is malformed or projects to a value that is not finite.</summary>
        private static bool AnyNotFinite(
            IReadOnlyList<Vector3[]> caps, in Matrix4x4 worldToClip, Vector2 margin, Span<Vector2> scratch)
        {
            for (int i = 0; i < caps.Count; i++)
            {
                Vector3[] polygon = caps[i];
                if (polygon == null || polygon.Length < 1 || polygon.Length > VpCapBoundsPolygon.MaxVertices
                    || Project(polygon, worldToClip, margin, scratch) == Projection.NotFinite)
                {
                    return true;
                }
            }

            return false;
        }

        private enum Projection
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

        /// <summary>The eight corners of the box, placed and moved by the separation. False when a corner is not finite.</summary>
        private static bool TryCorners(in VpCapProjectionTarget target, Span<Vector3> corners)
        {
            Vector3 min = target.localBounds.min;
            Vector3 max = target.localBounds.max;
            Vector3 offset = target.conditions.offset;
            for (int c = 0; c < BoxCorners; c++)
            {
                var local = new Vector3(
                    (c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
                Vector3 world = target.objectToWorld.MultiplyPoint3x4(local) + offset;
                if (!IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
                {
                    return false;
                }

                corners[c] = world;
            }

            return true;
        }

        private static Projection Project(
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
        private static bool RectanglesApart(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, Vector2 margin)
        {
            Bound(a, out Vector2 minA, out Vector2 maxA);
            Bound(b, out Vector2 minB, out Vector2 maxB);
            return Gap(minB.x - maxA.x, 2f * margin.x)
                || Gap(minA.x - maxB.x, 2f * margin.x)
                || Gap(minB.y - maxA.y, 2f * margin.y)
                || Gap(minA.y - maxB.y, 2f * margin.y);
        }

        private static void Bound(ReadOnlySpan<Vector2> points, out Vector2 min, out Vector2 max)
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
        private static bool PolygonsApart(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, Vector2 margin)
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
        private static bool Gap(float gap, float taken)
        {
            return IsFinite(gap) && IsFinite(taken) && gap > taken;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(in Matrix4x4 m)
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

        // ----- the current single-cut display ---------------------------------------------------------------------

        /// <summary>
        /// The projection target of one prepared cap of <paramref name="display"/>, for the single-cut display as it is
        /// now: the conditions from <see cref="VpCapCompatibility.TryGetSingleCutTarget"/>, the body's box and the
        /// placement the cap polygon was made from, and this cap's polygon as its one visible cap when
        /// <see cref="VpCapVisibility"/> keeps it for these eyes. When it does not, the target has no visible cap and
        /// its caps are **not complete**: the omitted cap leaves only the box to decide by.
        /// </summary>
        public static bool TryGetSingleCutTarget(
            VpLogicalCutDisplay display,
            int capIndex,
            in VpCapEye left,
            in VpCapEye right,
            float facingEpsilon,
            out VpCapProjectionTarget target)
        {
            target = default;
            if (!VpCapCompatibility.TryGetSingleCutTarget(display, capIndex, out VpCapCompatibilityTarget conditions)
                || !display.TryGetCapBody(capIndex, out Bounds localBounds, out Matrix4x4 objectToWorld)
                || !VpCapVisibility.TryClassify(display, capIndex, left, right, facingEpsilon, out VpCapVisibilityVerdict seen)
                || !display.TryGetCapRecord(capIndex, out LogicalCutCapRecord record))
            {
                return false;
            }

            Vector3[] visible = seen.Keep ? new Vector3[record.vertexCount] : null;
            if (visible != null)
            {
                for (int i = 0; i < visible.Length; i++)
                {
                    if (!display.TryGetCapVertex(capIndex, i, out visible[i]))
                    {
                        return false;
                    }
                }
            }

            target = new VpCapProjectionTarget(
                conditions, localBounds, objectToWorld,
                visible != null ? new[] { visible } : Array.Empty<Vector3[]>(), capsComplete: visible != null);
            return true;
        }
    }
}
