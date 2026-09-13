using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>
    /// Cut contours reconstructed independently of the kernel from the input's own topology (the probe's plan 9.2
    /// reference): a node per crossed topology edge, joined when one crossing triangle carries both, walked into chains
    /// and cycles per side. Used to explain a kernel failure (which loop, how degenerate) and to cross-check loop counts.
    /// </summary>
    public sealed class ReferenceContours
    {
        public sealed class Loop
        {
            public readonly List<long> Keys = new List<long>();
            public readonly List<double3> Positions = new List<double3>();
            public bool Closed;
            public int Side;
        }

        public readonly List<Loop> Loops = new List<Loop>();

        public static ReferenceContours Build(CutGeometry g, float4 plane, ReferenceCut reference)
        {
            var result = new ReferenceContours();
            for (int side = 0; side < 2; side++)
            {
                bool positive = side == 0;
                // directed segments per side, exactly as the kernel's rule: lone side walks P -> Q, the other Q -> P
                var next = new Dictionary<long, long>();
                var hasPrev = new HashSet<long>();
                var degreeProblem = new List<string>();
                foreach (var (a, b, c, _) in g.Triangles())
                {
                    int ta = g.TopologyOf(a), tb = g.TopologyOf(b), tc = g.TopologyOf(c);
                    bool na = reference.IsNegative(ta), nb = reference.IsNegative(tb), nc = reference.IsNegative(tc);
                    int negatives = (na ? 1 : 0) + (nb ? 1 : 0) + (nc ? 1 : 0);
                    if (negatives == 0 || negatives == 3) continue;
                    int lone = negatives == 1 ? (na ? 0 : nb ? 1 : 2) : (na ? (nb ? 2 : 1) : 0);
                    int[] t = { ta, tb, tc };
                    int va = t[lone], vb = t[(lone + 1) % 3], vc = t[(lone + 2) % 3];
                    long p = LogicalTopology.EdgeKey(va, vb), q = LogicalTopology.EdgeKey(vc, va);
                    bool lonePositive = negatives == 2;
                    long from = lonePositive == positive ? p : q, to = lonePositive == positive ? q : p;
                    if (next.ContainsKey(from)) degreeProblem.Add("node on edge " + from + " has two outgoing segments");
                    next[from] = to;
                    hasPrev.Add(to);
                }
                var visited = new HashSet<long>();
                foreach (var start in new List<long>(next.Keys))
                {
                    if (visited.Contains(start) || hasPrev.Contains(start)) continue;
                    result.Loops.Add(Walk(start, false, next, visited, reference, positive));
                }
                foreach (var start in new List<long>(next.Keys))
                {
                    if (visited.Contains(start)) continue;
                    result.Loops.Add(Walk(start, true, next, visited, reference, positive));
                }
            }
            return result;
        }

        static Loop Walk(long start, bool closed, Dictionary<long, long> next, HashSet<long> visited, ReferenceCut reference, bool positive)
        {
            var loop = new Loop { Closed = closed, Side = positive ? 1 : -1 };
            long cur = start;
            while (!visited.Contains(cur))
            {
                visited.Add(cur);
                loop.Keys.Add(cur);
                loop.Positions.Add(reference.EdgePoint(cur, reference.EdgeParams.TryGetValue(cur, out double p) ? p : 0.0));
                if (!next.TryGetValue(cur, out cur)) break;
            }
            return loop;
        }

        /// <summary>Human-readable dump: per loop its side, length, projected (u, v) coordinates in the plane basis and the node keys.</summary>
        public string Describe(float4 plane, int maxLoops = 8, int maxNodes = 64)
        {
            float3 n = plane.xyz;
            float3 seed = math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            double3 axisU = math.normalize(math.cross(seed, n));
            double3 axisV = math.cross((double3)n, axisU);
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var loop in Loops)
            {
                if (shown++ >= maxLoops) { sb.AppendLine("..."); break; }
                sb.Append(loop.Side > 0 ? "positive " : "negative ").Append(loop.Closed ? "cycle" : "chain").Append(" k=").Append(loop.Keys.Count).AppendLine(":");
                for (int i = 0; i < loop.Keys.Count && i < maxNodes; i++)
                {
                    long key = loop.Keys[i];
                    double3 p = loop.Positions[i];
                    sb.Append("  (").Append((key >> 32)).Append(',').Append((int)(key & 0xFFFFFFFF)).Append(") uv=(")
                      .Append(math.dot(p, axisU).ToString("R", CultureInfo.InvariantCulture)).Append(", ").Append(math.dot(p, axisV).ToString("R", CultureInfo.InvariantCulture)).AppendLine(")");
                }
            }
            return sb.ToString();
        }
    }
}
