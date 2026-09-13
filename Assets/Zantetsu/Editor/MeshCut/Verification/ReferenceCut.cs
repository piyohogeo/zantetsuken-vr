using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>
    /// Independent double-precision reference of the classification (DESIGN 6.4): one signed distance per topology
    /// vertex from its canonical position, the exact-sign side (d &gt;= 0 positive, no epsilon), the crossing triangles
    /// and the parameter of every crossed topology edge from its lower-id endpoint. Also the float distance exactly as
    /// the kernel evaluates it, so side membership can be checked exactly rather than within a tolerance.
    /// </summary>
    public sealed class ReferenceCut
    {
        public readonly Dictionary<int, double> Distance = new Dictionary<int, double>();
        public readonly Dictionary<int, float> DistanceFloat = new Dictionary<int, float>();
        public readonly Dictionary<int, float3> CanonicalPosition = new Dictionary<int, float3>();
        public readonly List<string> Problems = new List<string>();
        /// <summary>Parameter along each crossed topology edge, keyed like the kernel's node keys.</summary>
        public readonly Dictionary<long, double> EdgeParams = new Dictionary<long, double>();
        public readonly Dictionary<long, float> EdgeParamsFloat = new Dictionary<long, float>();
        public int CrossingTriangles;
        public int PositiveSurface, NegativeSurface;
        public readonly HashSet<int> OnPlaneVertices = new HashSet<int>();
        public double MaxExtent;

        public static ReferenceCut Compute(CutGeometry g, float4 plane)
        {
            var r = new ReferenceCut();
            var (mn, mx) = g.Bounds();
            r.MaxExtent = math.cmax(mx - mn);
            double3 n = plane.xyz; double w = plane.w;

            // canonical positions: every render vertex of a topology vertex must carry the same position bits (6.2)
            foreach (var (a, b, c, _) in g.Triangles())
                foreach (uint v in new[] { a, b, c })
                {
                    int t = g.TopologyOf(v);
                    if (t < 0) { r.Problems.Add("render vertex " + v + " is not in the topology map"); continue; }
                    float3 p = g.Vertices[v].position;
                    if (r.CanonicalPosition.TryGetValue(t, out float3 existing))
                    {
                        if (!existing.Equals(p)) r.Problems.Add("topology vertex " + t + " has two positions (render " + v + ")");
                        continue;
                    }
                    r.CanonicalPosition.Add(t, p);
                    double d = math.dot(n, (double3)p) + w;
                    r.Distance.Add(t, d);
                    r.DistanceFloat.Add(t, math.dot(plane.xyz, p) + plane.w);
                    if (r.DistanceFloat[t] == 0f) r.OnPlaneVertices.Add(t);
                }

            foreach (var (a, b, c, _) in g.Triangles())
            {
                int ta = g.TopologyOf(a), tb = g.TopologyOf(b), tc = g.TopologyOf(c);
                if (ta < 0 || tb < 0 || tc < 0) continue;
                bool na = r.DistanceFloat[ta] < 0f, nb = r.DistanceFloat[tb] < 0f, nc = r.DistanceFloat[tc] < 0f;
                int negatives = (na ? 1 : 0) + (nb ? 1 : 0) + (nc ? 1 : 0);
                if (negatives == 0) { r.PositiveSurface++; continue; }
                if (negatives == 3) { r.NegativeSurface++; continue; }
                r.CrossingTriangles++;
                if (negatives == 1) { r.PositiveSurface += 2; r.NegativeSurface += 1; } else { r.PositiveSurface += 1; r.NegativeSurface += 2; }
                r.AddEdge(ta, tb); r.AddEdge(tb, tc); r.AddEdge(tc, ta);
            }
            return r;
        }

        void AddEdge(int u, int w)
        {
            bool nu = DistanceFloat[u] < 0f, nw = DistanceFloat[w] < 0f;
            if (nu == nw) return;
            long key = LogicalTopology.EdgeKey(u, w);
            if (EdgeParams.ContainsKey(key)) return;
            int lo = Math.Min(u, w), hi = Math.Max(u, w);
            EdgeParams.Add(key, Distance[lo] / (Distance[lo] - Distance[hi]));
            float dLo = DistanceFloat[lo], dHi = DistanceFloat[hi];
            EdgeParamsFloat.Add(key, (float)(dLo / (double)(dLo - dHi)));
        }

        public double3 EdgePoint(long key, double param)
        {
            int lo = (int)(key >> 32), hi = (int)(key & 0xFFFFFFFF);
            double3 pLo = CanonicalPosition[lo], pHi = CanonicalPosition[hi];
            return pLo + (pHi - pLo) * param;
        }

        public bool IsNegative(int topology) => DistanceFloat[topology] < 0f;
    }
}
