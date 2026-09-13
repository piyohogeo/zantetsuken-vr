using System;
using System.Collections.Generic;
using System.Text;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>
    /// The logical (topology-vertex) edge table of a geometry, built from the render triangles through the topology
    /// map. Identity is the topology id, never the position: coincident faces expressed over distinct topology are
    /// distinct, opposite coincident faces over the same three topology vertices share three logical edges.
    /// </summary>
    public sealed class LogicalTopology
    {
        public sealed class Edge
        {
            public int V0, V1;                 // V0 < V1
            public readonly List<int> Faces = new List<int>(2);
            public readonly List<int> Directions = new List<int>(2);   // +1: face walks V0->V1, -1: V1->V0
        }

        public readonly List<Edge> Edges = new List<Edge>();
        public readonly Dictionary<long, int> EdgeOf = new Dictionary<long, int>();
        public readonly List<int[]> FaceTopology = new List<int[]>();      // 3 topology ids per face
        public readonly List<uint[]> FaceRender = new List<uint[]>();       // 3 render ids per face
        public readonly Dictionary<int, List<int>> VertexFaces = new Dictionary<int, List<int>>();
        public readonly List<string> Problems = new List<string>();

        public static long EdgeKey(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        public static LogicalTopology Build(CutGeometry g)
        {
            var t = new LogicalTopology();
            foreach (var (a, b, c, _) in g.Triangles())
            {
                int ta = g.TopologyOf(a), tb = g.TopologyOf(b), tc = g.TopologyOf(c);
                int f = t.FaceTopology.Count;
                if (ta < 0 || tb < 0 || tc < 0)
                {
                    t.Problems.Add("face " + f + " has a render vertex without topology mapping (" + a + "," + b + "," + c + ")");
                    ta = Math.Max(ta, 0); tb = Math.Max(tb, 0); tc = Math.Max(tc, 0);
                }
                t.FaceTopology.Add(new[] { ta, tb, tc });
                t.FaceRender.Add(new[] { a, b, c });
                if (ta == tb || tb == tc || ta == tc) t.Problems.Add("face " + f + " repeats a topology vertex (" + ta + "," + tb + "," + tc + ")");
                t.AddEdge(ta, tb, f); t.AddEdge(tb, tc, f); t.AddEdge(tc, ta, f);
                t.Incident(ta).Add(f); t.Incident(tb).Add(f); t.Incident(tc).Add(f);
            }
            return t;
        }

        List<int> Incident(int v)
        {
            if (!VertexFaces.TryGetValue(v, out var list)) { list = new List<int>(); VertexFaces.Add(v, list); }
            return list;
        }

        void AddEdge(int u, int w, int face)
        {
            long key = EdgeKey(u, w);
            if (!EdgeOf.TryGetValue(key, out int e))
            {
                e = Edges.Count;
                EdgeOf.Add(key, e);
                Edges.Add(new Edge { V0 = Math.Min(u, w), V1 = Math.Max(u, w) });
            }
            Edges[e].Faces.Add(face);
            Edges[e].Directions.Add(u < w ? 1 : -1);
        }

        public int BoundaryEdges { get { int n = 0; foreach (var e in Edges) if (e.Faces.Count == 1) n++; return n; } }

        /// <summary>
        /// DESIGN 6.2 contract on this topology: every edge has exactly two faces that traverse it in opposite directions,
        /// every vertex's faces form one closed fan, no face repeats a vertex. Returns the violations (empty when valid).
        /// </summary>
        public List<string> Validate(int maxReports = 8)
        {
            var out_ = new List<string>(Problems);
            foreach (var e in Edges)
            {
                if (e.Faces.Count != 2) { Report(out_, "edge (" + e.V0 + "," + e.V1 + ") has " + e.Faces.Count + " face(s)", maxReports); continue; }
                if (e.Directions[0] == e.Directions[1]) Report(out_, "edge (" + e.V0 + "," + e.V1 + ") is traversed in the same direction by faces " + e.Faces[0] + " and " + e.Faces[1], maxReports);
            }
            foreach (var kv in VertexFaces)
            {
                // link of the vertex: the opposite edge of each incident face; a closed manifold fan is one cycle
                var nodeIndex = new Dictionary<int, int>();
                var degree = new List<int>();
                var parent = new List<int>();
                int Node(int lv)
                {
                    if (!nodeIndex.TryGetValue(lv, out int n)) { n = degree.Count; nodeIndex.Add(lv, n); degree.Add(0); parent.Add(n); }
                    return n;
                }
                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                foreach (int f in kv.Value)
                {
                    int[] tri = FaceTopology[f];
                    int p, q;
                    if (tri[0] == kv.Key) { p = tri[1]; q = tri[2]; }
                    else if (tri[1] == kv.Key) { p = tri[2]; q = tri[0]; }
                    else { p = tri[0]; q = tri[1]; }
                    int np = Node(p), nq = Node(q);
                    degree[np]++; degree[nq]++;
                    int rp = Find(np), rq = Find(nq);
                    if (rp != rq) parent[rp] = rq;
                }
                int deg1 = 0, bad = 0, components = 0;
                for (int n = 0; n < degree.Count; n++) { if (degree[n] == 1) deg1++; else if (degree[n] != 2) bad++; }
                for (int n = 0; n < parent.Count; n++) if (Find(n) == n) components++;
                if (!(components == 1 && bad == 0 && deg1 == 0))
                    Report(out_, "vertex " + kv.Key + " link: " + components + " component(s), " + deg1 + " endpoint(s), " + bad + " node(s) of degree != 2 over " + kv.Value.Count + " face(s)", maxReports);
            }
            return out_;
        }

        static void Report(List<string> into, string s, int max)
        {
            if (into.Count < max) into.Add(s);
            else if (into.Count == max) into.Add("...");
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("faces=").Append(FaceTopology.Count).Append(" edges=").Append(Edges.Count).Append(" vertices=").Append(VertexFaces.Count).Append(" boundary=").Append(BoundaryEdges);
            return sb.ToString();
        }
    }
}
