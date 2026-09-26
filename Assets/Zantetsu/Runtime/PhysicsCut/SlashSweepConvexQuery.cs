using Unity.Mathematics;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The Gameplay hit test of one wave update against one adopted convex (DESIGN 19.1.7): whether the closed convex
    /// hull of the update's four segment points, <c>Q = conv{A_(n-1), B_(n-1), A_n, B_n}</c>, meets the closed convex
    /// B-rep. Both are given in the convex's own numerical frame, together with the wave's fixed plane, which every
    /// point of <c>Q</c> lies in.
    /// <para>
    /// **How.** <c>Q</c> lies in the plane, so it meets the convex exactly where it meets the convex's section by that
    /// plane. The section is the hull of the vertices on the plane and the points where edges cross it; it is empty
    /// when the convex lies on one side, and then there is no hit. Otherwise the two in-plane hulls are tested by
    /// separating axes: the in-plane part of every face normal (the section's own edge normals), the in-plane normal
    /// and the direction of every difference of two points of <c>Q</c> (its edges, including those of a degenerate
    /// <c>Q</c>), and the in-plane part of every convex edge (a section that is itself only an edge or a vertex). An
    /// axis that separates is a proof that nothing is shared; when none does, the closed hulls meet.
    /// </para>
    /// <para>
    /// **One query for every shape of <c>Q</c>.** The latch update's segment (all four points on two), a
    /// parallelogram, a trapezoid of a growing span, a triangle and a line of parallel or antiparallel axes are all the
    /// same call; nothing is corrected, clipped or refused for being degenerate. There is no thickness, no end sphere
    /// and no tolerance of this query's own: touching counts, and a point of <c>Q</c> a rounding away from the plane
    /// is read by its in-plane position only.
    /// </para>
    /// </summary>
    public static unsafe class SlashSweepConvexQuery
    {
        /// <summary>
        /// Whether <c>Q</c> and the convex share a point. <paramref name="section"/> is the caller's scratch for the
        /// section points: it is grown here when a convex needs more, and never shrunk.
        /// </summary>
        /// <param name="plane">The wave's plane in the convex's frame, xyz its normal and w its offset
        /// (<c>dot(n, x) + w = 0</c>). The normal need not be of unit length but must not be zero.</param>
        public static bool Intersects(
            float4 plane,
            float3 previousA,
            float3 previousB,
            float3 currentA,
            float3 currentB,
            in ConvexBrepBank bank,
            in ConvexBrepRange range,
            ref float3[] section)
        {
            BrepBuffer brep = bank.View(in range);
            int needed = brep.V + brep.E;
            if (section == null || section.Length < needed)
            {
                section = new float3[math.max(needed, section == null ? 16 : section.Length * 2)];
            }

            float3 n = plane.xyz;

            // The section: every vertex on the plane, and every edge whose ends lie strictly on opposite sides, at the
            // point it crosses. An edge with an end on the plane gives that end as a vertex already.
            int count = 0;
            for (int v = 0; v < brep.V; v++)
            {
                if (math.dot(n, brep.v[v]) + plane.w == 0f)
                {
                    section[count++] = brep.v[v];
                }
            }

            for (int e = 0; e < brep.E; e++)
            {
                float3 p0 = brep.v[brep.edges[e].v0];
                float3 p1 = brep.v[brep.edges[e].v1];
                float d0 = math.dot(n, p0) + plane.w;
                float d1 = math.dot(n, p1) + plane.w;
                if ((d0 < 0f && d1 > 0f) || (d0 > 0f && d1 < 0f))
                {
                    section[count++] = p0 + (p1 - p0) * (d0 / (d0 - d1));
                }
            }

            if (count == 0)
            {
                return false;
            }

            float3* q = stackalloc float3[4];
            q[0] = previousA;
            q[1] = previousB;
            q[2] = currentA;
            q[3] = currentB;

            // The section's own edges: the in-plane part of each face's normal.
            for (int f = 0; f < brep.F; f++)
            {
                float3 faceNormal = FaceNormal(in brep, f);
                if (Separates(InPlane(faceNormal, n), q, section, count))
                {
                    return false;
                }
            }

            // The edges of Q, degenerate or not: every difference of two of its points, its normal in the plane and
            // its own direction.
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    float3 d = q[j] - q[i];
                    if (Separates(math.cross(n, d), q, section, count) || Separates(InPlane(d, n), q, section, count))
                    {
                        return false;
                    }
                }
            }

            // A section that is only an edge or a vertex: the convex's edges, as they lie in the plane.
            for (int e = 0; e < brep.E; e++)
            {
                float3 d = brep.v[brep.edges[e].v1] - brep.v[brep.edges[e].v0];
                if (Separates(InPlane(d, n), q, section, count))
                {
                    return false;
                }
            }

            return true;
        }

        private static float3 InPlane(float3 vector, float3 normal)
        {
            return vector - normal * (math.dot(vector, normal) / math.dot(normal, normal));
        }

        // Newell's normal of one face loop: of any length and outward for the B-rep's winding. Only its direction is
        // used, and only through its in-plane part.
        private static float3 FaceNormal(in BrepBuffer brep, int face)
        {
            int from = brep.faceOff[face];
            int to = brep.faceOff[face + 1];
            float3 normal = float3.zero;
            for (int k = from; k < to; k++)
            {
                float3 a = brep.v[brep.faceIdx[k]];
                float3 b = brep.v[brep.faceIdx[k + 1 < to ? k + 1 : from]];
                normal.x += (a.y - b.y) * (a.z + b.z);
                normal.y += (a.z - b.z) * (a.x + b.x);
                normal.z += (a.x - b.x) * (a.y + b.y);
            }

            return normal;
        }

        // Closed: projections that only touch do not separate. A zero axis projects everything to one point and so
        // separates nothing, which is what an axis that does not exist should do.
        private static bool Separates(float3 axis, float3* q, float3[] section, int count)
        {
            float qLo = float.PositiveInfinity;
            float qHi = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                float t = math.dot(axis, q[i]);
                qLo = math.min(qLo, t);
                qHi = math.max(qHi, t);
            }

            float sLo = float.PositiveInfinity;
            float sHi = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                float t = math.dot(axis, section[i]);
                sLo = math.min(sLo, t);
                sHi = math.max(sHi, t);
            }

            return qHi < sLo || sHi < qLo;
        }
    }
}
