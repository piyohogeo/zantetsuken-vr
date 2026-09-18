using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The finite Cap Bounds Polygon of DESIGN 5.2: the convex cross-section of a geometry's **local bounds box** with
    /// one adopted cut plane, given in world space and wound once about the world plane's normal.
    /// <para>
    /// **This is not the cut's real outline.** It is the box's cross-section, which is why DESIGN 5.2 draws it only
    /// through the stencil: the concave parts, the holes and the separate component outlines of the real surface are
    /// what the stencil restricts it to, and a polygon from here drawn opaquely on its own would show a cap far larger
    /// than the body. Nothing here reads a triangle, an index or a topology, and nothing here re-checks the closedness
    /// or the winding of the input geometry — DESIGN 5.7 settles that once, upstream, and the drawing side does not
    /// revisit it.
    /// </para>
    /// <para>
    /// **Spaces.** The bounds and the plane arrive in the geometry's own coordinates and the placement is the caller's
    /// snapshot, as everywhere else in this lane. The plane is <c>dot(n, x) + d = 0</c> carried as <c>(n.xyz, d)</c>,
    /// and the positive side is <c>dot(n, x) + d &gt; 0</c>. The points are found in the geometry's own frame, where the
    /// box's edges are, and are placed in world before they are ordered.
    /// </para>
    /// <para>
    /// **What a vertex is.** Every vertex this returns is a corner of the box that lies in the plane, or the point
    /// where one of the box's twelve edges crosses it. Which side a corner is on is decided by the sign of its own
    /// distance and by nothing else, so a plane that misses the box misses it however narrowly, and nothing is ever
    /// moved to meet a plane it does not reach. The vertices are therefore inside the box and in the plane, to within
    /// the ordinary error of the division, the interpolation and the matrix multiply that produced them — this is
    /// float arithmetic and it is not claimed to be exact.
    /// </para>
    /// <para>
    /// **Epsilon.** The plane is normalized first — n and d divided by the **same positive** length, so the plane
    /// keeps its place and both of its sides, and no sign is ever flipped — and only then is <c>dot(n, x) + d</c> a
    /// signed distance. The epsilon is a **length in the geometry's own local units**, the same units as the bounds,
    /// and it has one job: two points that came out in the same place are one vertex. It is not a band around the
    /// plane and it decides nothing about sides. Where it matters is that a plane passing very near a corner crosses
    /// the three edges meeting there within a hair of that corner; those three points are the same point, and a cap
    /// smaller across than this length is not told apart from a touch. <see cref="EpsilonFor"/> is the usual way to
    /// choose one and follows this project's existing rule — a dimensionless constant times the largest extent of
    /// the body being measured. No near-zero result is rescued into an area, and no normal is flipped to make one.
    /// </para>
    /// <para>
    /// **An empty result is a normal one.** A plane that misses the box, touches one corner, or lies along one edge has
    /// no cap: <see cref="TryBuild"/> succeeds with a count of zero and writes nothing. A plane just past a face of the
    /// box is past it and has no cap; a plane just inside that face has the cross-section that is just inside it. A
    /// plane lying in a face does have a cap, and that face's four corners are its vertices with no vertex repeated,
    /// because an edge with an end in the plane is not two-sided and adds nothing of its own. Only malformed input
    /// — a plane with no normal, a placement that is not affine or cannot be inverted, a bounds with negative size,
    /// anything not finite — is refused, and then nothing is written either.
    /// </para>
    /// <para>
    /// **Winding.** The vertices come back ordered so that, for any three consecutive of them,
    /// <c>Cross(p1 - p0, p2 - p0)</c> points along the world plane's normal — the ordinary way this project takes a
    /// triangle's normal — so a cap whose outward normal is that normal is wound as it comes, and the cap on the other
    /// side of the same plane is the same polygon read backwards. Because the ordering is done in world space, a
    /// placement that mirrors or scales unevenly is answered by the order the vertices come out in, not by a correction
    /// afterwards.
    /// </para>
    /// <para>
    /// One instance carries the scratch for one build and is not thread safe; the display that owns it is main thread
    /// only. Nothing is kept between calls that a later call reads.
    /// </para>
    /// </summary>
    public sealed class VpCapBoundsPolygon
    {
        /// <summary>A box and a plane cross in at most six points, so a cap polygon has at most six vertices.</summary>
        public const int MaxVertices = 6;

        /// <summary>
        /// The dimensionless part of the usual epsilon, as the cut kernels of this project already choose theirs:
        /// scaled by the largest extent of the body being measured to a length in that body's own units.
        /// </summary>
        public const float RelativeEpsilon = 1e-6f;

        // Corner c takes min on an axis whose bit is 0 in c and max where it is 1: bit 0 is x, bit 1 is y, bit 2 is z.
        // The twelve edges are the pairs that differ in exactly one bit, grouped by the axis they run along.
        private static readonly int[] k_edges =
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7,
        };

        private readonly float3[] _corners = new float3[8];
        private readonly float[] _distances = new float[8];

        // Corners lying in the plane and edge crossings both contribute, and a degenerate box repeats its corners, so
        // the gathering buffer is larger than a cap polygon can be. What survives the merge is what is counted.
        private readonly float3[] _gathered = new float3[8 + 12];
        private readonly float3[] _kept = new float3[8 + 12];
        private readonly Vector3[] _ordered = new Vector3[MaxVertices];
        private readonly float[] _angles = new float[MaxVertices];

        /// <summary>
        /// The usual epsilon for a body of this size: a length in the geometry's own local units, being
        /// <see cref="RelativeEpsilon"/> times the largest extent of <paramref name="localBounds"/>. A body with no
        /// extent at all gets zero, which is exact comparison and still answers correctly — such a box has no cap.
        /// </summary>
        public static float EpsilonFor(Bounds localBounds)
        {
            Vector3 size = localBounds.size;
            float largest = math.cmax(math.abs(new float3(size.x, size.y, size.z)));
            return math.isfinite(largest) ? RelativeEpsilon * largest : 0f;
        }

        /// <summary>
        /// The cap polygon of <paramref name="localBounds"/> cut by <paramref name="localPlane"/>, written in world
        /// space into <paramref name="worldVertices"/> from <paramref name="start"/> and wound about
        /// <paramref name="worldPlane"/>'s normal.
        /// <para>
        /// Returns true with <paramref name="count"/> zero for a plane that misses the box or meets it in a single
        /// point or along one edge, writing nothing. Returns false, having written nothing, for malformed input.
        /// </para>
        /// </summary>
        public bool TryBuild(
            Bounds localBounds,
            float4 localPlane,
            Matrix4x4 geometryLocalToWorld,
            float epsilon,
            Vector3[] worldVertices,
            int start,
            out int count,
            out float4 worldPlane)
        {
            count = 0;
            worldPlane = default;
            if (worldVertices == null
                || start < 0
                || start > worldVertices.Length - MaxVertices
                || !(epsilon >= 0f)
                || float.IsInfinity(epsilon))
            {
                return false;
            }

            // The world plane is what the cap lies in, and the conversion is the one the clip already uses: a plane
            // goes by the transpose of the placement's inverse, with the sign never flipped. It settles for us that
            // the placement is finite, affine and invertible, and hands back a normalized normal.
            if (!VpCutPlane.TryGeometryLocalToWorld(localPlane, geometryLocalToWorld, out float4 world)
                || !TryNormalizePlane(localPlane, out float4 plane)
                || !TryCorners(localBounds))
            {
                return false;
            }

            // 1. Where the box actually meets the plane: a corner whose own distance is zero lies in it, and an
            //    edge whose two ends are strictly on opposite sides crosses it once, between them. Sides are decided
            //    by the sign of each corner's own distance and by nothing else, so a plane that misses the box misses
            //    it however narrowly, and nothing is moved to meet it. An edge with an end in the plane is not
            //    two-sided and contributes nothing of its own — that end already is the meeting point — which is what
            //    keeps a face lying in the plane to its own four corners.
            int gathered = 0;
            for (int c = 0; c < 8; c++)
            {
                _distances[c] = math.dot(plane.xyz, _corners[c]) + plane.w;
                if (_distances[c] == 0f)
                {
                    _gathered[gathered++] = _corners[c];
                }
            }

            for (int e = 0; e < k_edges.Length; e += 2)
            {
                float da = _distances[k_edges[e]];
                float db = _distances[k_edges[e + 1]];
                if (!(da > 0f && db < 0f) && !(da < 0f && db > 0f))
                {
                    continue;
                }

                // A point between the two ends: da and db have opposite signs, so the fraction is inside (0, 1) and
                // the point is on the edge, in the box and in the plane, to within the error of doing this in float.
                float3 a = _corners[k_edges[e]];
                _gathered[gathered++] = a + ((_corners[k_edges[e + 1]] - a) * (da / (da - db)));
            }

            // 2. Two points that came out in the same place are one vertex. This is what the epsilon is for, and its
            //    only job: a plane passing very near a corner crosses the three edges meeting there within a hair of
            //    that corner, and those are not three vertices. The comparison is a distance between points, in the
            //    same local units as the bounds.
            int kept = 0;
            float epsilonSquared = epsilon * epsilon;
            for (int g = 0; g < gathered; g++)
            {
                bool repeated = false;
                for (int k = 0; k < kept; k++)
                {
                    if (math.distancesq(_kept[k], _gathered[g]) <= epsilonSquared)
                    {
                        repeated = true;
                        break;
                    }
                }

                if (!repeated)
                {
                    _kept[kept++] = _gathered[g];
                }
            }

            // 3. Fewer than three points is a miss, a corner touch, an edge touch, or a cap too small to be told from
            //    one: a normal empty result, and no polygon is invented for it. More than six cannot come out of a box
            //    and a plane.
            if (kept < 3)
            {
                worldPlane = world;
                return true;
            }

            if (kept > MaxVertices)
            {
                return false;
            }

            // 4. World first and the ordering after, so that a placement which mirrors or scales unevenly is answered
            //    by the order the vertices come out in rather than by a correction applied to them.
            for (int k = 0; k < kept; k++)
            {
                _ordered[k] = geometryLocalToWorld.MultiplyPoint3x4((Vector3)_kept[k]);
                if (!IsFinite(_ordered[k]))
                {
                    return false;
                }
            }

            if (!TryOrder(kept, world.xyz))
            {
                return false;
            }

            for (int k = 0; k < kept; k++)
            {
                worldVertices[start + k] = _ordered[k];
            }

            count = kept;
            worldPlane = world;
            return true;
        }

        /// <summary>
        /// Puts the vertices round the polygon once, in the direction that makes <c>Cross(p1 - p0, p2 - p0)</c> point
        /// along <paramref name="axis"/>. They are sorted by their angle in a basis built on that axis — an ordering,
        /// not a hull: a box's cross-section is convex already, and nothing here is asked to make it so.
        /// </summary>
        private bool TryOrder(int count, float3 axis)
        {
            var centre = float3.zero;
            for (int i = 0; i < count; i++)
            {
                centre += (float3)_ordered[i];
            }

            centre /= count;

            // The same seed the cut kernel uses for a plane basis. The second direction is taken so that
            // cross(u, v) is the axis itself, which is what makes an ascending angle the winding asked for.
            float3 seed = math.abs(axis.x) < 0.9f ? new float3(1f, 0f, 0f) : new float3(0f, 1f, 0f);
            float3 u = math.normalizesafe(math.cross(seed, axis), float3.zero);
            if (math.lengthsq(u) <= 0f)
            {
                return false;
            }

            float3 v = math.cross(axis, u);
            for (int i = 0; i < count; i++)
            {
                float3 offset = (float3)_ordered[i] - centre;
                _angles[i] = math.atan2(math.dot(offset, v), math.dot(offset, u));
                if (float.IsNaN(_angles[i]))
                {
                    return false;
                }
            }

            for (int i = 1; i < count; i++)
            {
                float angle = _angles[i];
                Vector3 vertex = _ordered[i];
                int j = i - 1;
                while (j >= 0 && _angles[j] > angle)
                {
                    _angles[j + 1] = _angles[j];
                    _ordered[j + 1] = _ordered[j];
                    j--;
                }

                _angles[j + 1] = angle;
                _ordered[j + 1] = vertex;
            }

            return true;
        }

        private bool TryCorners(Bounds localBounds)
        {
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            if (!IsFinite(min) || !IsFinite(max) || max.x < min.x || max.y < min.y || max.z < min.z)
            {
                return false;
            }

            for (int c = 0; c < 8; c++)
            {
                _corners[c] = new float3(
                    (c & 1) == 0 ? min.x : max.x,
                    (c & 2) == 0 ? min.y : max.y,
                    (c & 4) == 0 ? min.z : max.z);
            }

            return true;
        }

        /// <summary>
        /// The plane with n and d divided by the same positive length, so that the side of every point is unchanged and
        /// <c>dot(n, x) + d</c> is a distance in the bounds' own units — which is what lets one epsilon mean one thing
        /// for the corners and for the dedup alike.
        /// </summary>
        private static bool TryNormalizePlane(float4 plane, out float4 normalized)
        {
            normalized = default;
            if (!math.all(math.isfinite(plane)))
            {
                return false;
            }

            float length = math.length(plane.xyz);
            if (!(length > 0f) || float.IsInfinity(length))
            {
                return false;
            }

            var candidate = new float4(plane.xyz / length, plane.w / length);
            if (!math.all(math.isfinite(candidate)))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return math.all(math.isfinite(new float3(value.x, value.y, value.z)));
        }
    }
}
