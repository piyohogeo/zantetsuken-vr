using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The Cap Bounds Polygon of DESIGN 5.2 cut down by the other boundaries selected for its fragment: the convex
    /// polygon <see cref="VpCapBoundsPolygon"/> made of the box and the cap's own face, clipped by the half-space of
    /// every other boundary the at-most-eight selection (<see cref="VpClipCandidates.Select"/>) chose. CPU geometry
    /// only; nothing is drawn from it yet.
    /// <para>
    /// **One frame for everything.** The polygon, and the plane given for each candidate, must be in one and the same
    /// frame, chosen by the caller. The plane a candidate carries is its source fragment's own and is NOT that frame;
    /// the caller converts it (and accounts for any separation) and passes the converted plane alongside, index for
    /// index. The product's conversion of frames and offsets is not connected here.
    /// </para>
    /// <para>
    /// **Which planes cut.** Exactly those whose selection state is <see cref="VpClipSelectionState.Selected"/>, except
    /// the cap's own face, recognised by identity -- whatever its side and whatever plane was passed for it. An Ignored
    /// boundary never cuts, and the cap's own boundary need not be selected for the others to apply. Fixed or free,
    /// visible or not, colour: none of these is an input.
    /// </para>
    /// <para>
    /// **The cut.** Each half-space keeps <c>side * (dot(n, x) + d) &gt;= 0</c>, decided by the sign of the distance
    /// alone: a vertex on the plane is kept, one past it is not, and no epsilon widens a half-space or brings back an
    /// area that was cut away. Where an edge crosses the plane the crossing point is added. The order of the vertices is
    /// kept, so the winding and the cap's plane are the input's. The epsilon has the one job it has in
    /// <see cref="VpCapBoundsPolygon"/>: two neighbouring vertices within it are one vertex. It is not an area threshold:
    /// a thin polygon whose vertices are further apart than the epsilon keeps its area, however small.
    /// </para>
    /// <para>
    /// **Arithmetic.** Distances, crossing points, the vertex merge and the area are computed in double precision, where
    /// no product or sum of finite single-precision values overflows; a finite input far past what single precision
    /// could square is clipped, not lost. Should any of these still not come out finite, the call is refused -- false,
    /// nothing written -- and never reported as a polygon with nothing left.
    /// </para>
    /// <para>
    /// **How many vertices.** Cutting a convex polygon by one half-space takes away every vertex past the plane and
    /// adds at most two crossing points, and it adds them only when at least one vertex was taken away, so each cut adds
    /// at most one vertex. A box and one plane give at most <see cref="VpCapBoundsPolygon.MaxVertices"/> (6); at most
    /// <see cref="VpClipCandidates.Capacity"/> (8) other boundaries can cut -- eight when the cap's own boundary is not
    /// among the selected, as when it is Ignored itself -- so a clipped cap has at most
    /// <see cref="MaxVertices"/> = 6 + 8 = 14. The output must have room for the input's count plus the number of
    /// planes that cut; less is refused, never cut short and never turned into an empty success.
    /// </para>
    /// <para>
    /// **Empty is normal.** A polygon cut away entirely, or down to a point or a segment -- fewer than three vertices
    /// left, or vertices whose area vector is exactly zero -- is a successful result with a count of zero. That is a statement about this polygon only: it neither removes nor
    /// changes the cap's record, and it is a different thing from the cap's boundary being Ignored.
    /// </para>
    /// <para>
    /// Nothing is written but the output: not the input polygon, the candidates, the states or the planes, the
    /// ledger, any record, any work. Nothing is kept between calls.
    /// </para>
    /// </summary>
    public static class VpCapPolygonClip
    {
        /// <summary>
        /// The most vertices a clipped cap can have: the box-and-plane polygon's
        /// <see cref="VpCapBoundsPolygon.MaxVertices"/> plus one for each of at most
        /// <see cref="VpClipCandidates.Capacity"/> cutting boundaries.
        /// </summary>
        public const int MaxVertices = VpCapBoundsPolygon.MaxVertices + VpClipCandidates.Capacity;

        /// <summary>
        /// Clips <paramref name="polygon"/> (its first <paramref name="count"/> vertices) by the selected boundaries other
        /// than <paramref name="own"/>, writing the result into <paramref name="output"/>. True with
        /// <paramref name="clippedCount"/> 0 when nothing of area is left. False, writing nothing, for malformed input,
        /// an output with too little room, or arithmetic that did not come out finite.
        /// </summary>
        /// <param name="planes">
        /// Each candidate's plane in the polygon's own frame, as <c>(n.xyz, d)</c>, at the candidate's index. Only the
        /// entries of the planes that cut are read.
        /// </param>
        public static bool TryClip(
            IReadOnlyList<Vector3> polygon,
            int count,
            VpClipBoundary own,
            IReadOnlyList<VpClipCandidate> candidates,
            IReadOnlyList<VpClipSelectionState> states,
            IReadOnlyList<float4> planes,
            float epsilon,
            Vector3[] output,
            out int clippedCount)
        {
            clippedCount = 0;
            if (polygon == null || candidates == null || states == null || planes == null || output == null
                || count < 0 || count > polygon.Count
                || states.Count < candidates.Count || planes.Count < candidates.Count
                || !(epsilon >= 0f) || float.IsInfinity(epsilon))
            {
                return false;
            }

            // The planes that cut, and whether each is usable, before anything is written.
            int cutting = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!Cuts(candidates[i], states[i], own))
                {
                    continue;
                }

                float4 plane = planes[i];
                if (!math.all(math.isfinite(plane)) || (plane.x == 0f && plane.y == 0f && plane.z == 0f))
                {
                    return false;
                }

                cutting++;
            }

            for (int i = 0; i < count; i++)
            {
                Vector3 p = polygon[i];
                if (!IsFinite(p))
                {
                    return false;
                }
            }

            if (output.Length < count + cutting)
            {
                return false;
            }

            // Ping-pong between two scratch lists, then dedupe and judge the area.
            var current = new List<Vector3>(count + cutting);
            for (int i = 0; i < count; i++)
            {
                current.Add(polygon[i]);
            }

            var next = new List<Vector3>(count + cutting);
            for (int i = 0; i < candidates.Count && current.Count > 0; i++)
            {
                if (!Cuts(candidates[i], states[i], own))
                {
                    continue;
                }

                if (!ClipOnce(current, planes[i], candidates[i].boundary.side, next))
                {
                    return false;
                }

                List<Vector3> swap = current;
                current = next;
                next = swap;
            }

            if (!Dedupe(current, epsilon))
            {
                return false;
            }

            if (current.Count < 3)
            {
                return true;
            }

            if (!TryHasArea(current, out bool hasArea))
            {
                return false;
            }

            if (!hasArea)
            {
                return true;
            }

            for (int i = 0; i < current.Count; i++)
            {
                output[i] = current[i];
            }

            clippedCount = current.Count;
            return true;
        }

        /// <summary>Whether a candidate's boundary cuts this cap: selected, and not the cap's own face.</summary>
        private static bool Cuts(VpClipCandidate candidate, VpClipSelectionState state, VpClipBoundary own)
        {
            return state == VpClipSelectionState.Selected && candidate.boundary.face != own.face;
        }

        /// <summary>
        /// One half-space, Sutherland-Hodgman style over a convex polygon: keeps every vertex with
        /// <c>side * distance &gt;= 0</c> and adds the crossing point of every edge whose ends are strictly on
        /// opposite sides. The order is kept. Computed in double precision; false if a distance or a crossing point does
        /// not come out finite.
        /// </summary>
        private static bool ClipOnce(List<Vector3> input, float4 plane, float side, List<Vector3> output)
        {
            output.Clear();
            int n = input.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = input[i];
                Vector3 b = input[(i + 1) % n];
                double da = side * Distance(plane, a);
                double db = side * Distance(plane, b);
                if (!IsFinite(da) || !IsFinite(db))
                {
                    return false;
                }

                if (da >= 0d)
                {
                    output.Add(a);
                }

                if ((da > 0d && db < 0d) || (da < 0d && db > 0d))
                {
                    double t = da / (da - db);
                    var crossing = new Vector3(
                        (float)(a.x + ((double)b.x - a.x) * t),
                        (float)(a.y + ((double)b.y - a.y) * t),
                        (float)(a.z + ((double)b.z - a.z) * t));
                    if (!IsFinite(crossing))
                    {
                        return false;
                    }

                    output.Add(crossing);
                }
            }

            return true;
        }

        private static double Distance(float4 plane, Vector3 p)
        {
            return ((double)plane.x * p.x) + ((double)plane.y * p.y) + ((double)plane.z * p.z) + plane.w;
        }

        /// <summary>
        /// The existing rule: two vertices within the epsilon are one. Neighbours only, the ring closed. The distance is
        /// computed in double precision; false if it does not come out finite.
        /// </summary>
        private static bool Dedupe(List<Vector3> ring, float epsilon)
        {
            double epsilonSquared = (double)epsilon * epsilon;
            for (int i = ring.Count - 1; i >= 0 && ring.Count > 1; i--)
            {
                int previous = (i - 1 + ring.Count) % ring.Count;
                if (i == previous)
                {
                    continue;
                }

                double dx = (double)ring[i].x - ring[previous].x;
                double dy = (double)ring[i].y - ring[previous].y;
                double dz = (double)ring[i].z - ring[previous].z;
                double distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
                if (!IsFinite(distanceSquared))
                {
                    return false;
                }

                if (distanceSquared <= epsilonSquared)
                {
                    ring.RemoveAt(i);
                }
            }

            return true;
        }

        /// <summary>
        /// Whether the ring encloses an area: its doubled area vector, in double precision, is not zero. No threshold --
        /// a point or a segment, however many vertices it is written with, has exactly none, and a thin polygon has a
        /// little. False if the area vector does not come out finite.
        /// </summary>
        private static bool TryHasArea(List<Vector3> ring, out bool hasArea)
        {
            hasArea = false;
            Vector3 origin = ring[0];
            double x = 0d;
            double y = 0d;
            double z = 0d;
            for (int i = 1; i + 1 < ring.Count; i++)
            {
                double ux = (double)ring[i].x - origin.x;
                double uy = (double)ring[i].y - origin.y;
                double uz = (double)ring[i].z - origin.z;
                double vx = (double)ring[i + 1].x - origin.x;
                double vy = (double)ring[i + 1].y - origin.y;
                double vz = (double)ring[i + 1].z - origin.z;
                x += (uy * vz) - (uz * vy);
                y += (uz * vx) - (ux * vz);
                z += (ux * vy) - (uy * vx);
            }

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                return false;
            }

            hasArea = x != 0d || y != 0d || z != 0d;
            return true;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
    }
}
