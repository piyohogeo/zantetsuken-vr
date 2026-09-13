using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Test-only correctness reference (probe Reference/ReferenceDoubleClip): managed, binary64, face-by-face
    /// Sutherland-Hodgman clip with canonical per-undirected-edge intersection points and a boundary-walk cut face.
    /// Plane: s(x) = dot(n, x) + w. DESIGN 7.6: eps is used only for the robust-support decision (Split iff some
    /// s &gt; +eps and some s &lt; -eps). The clip itself classifies by the exact sign of s.
    /// </summary>
    public static class ReferenceDoubleClip
    {
        public enum Status : byte { Ok, NotSplitPositive, NotSplitNegative, Degenerate }

        /// Robust support (7.6): returns +1 (inherit positive: positive-only or no support), -1 (inherit negative), 0 (Split).
        public static int RobustDisposition(ConvexPoly p, double3 n, double w, double eps)
        {
            bool pos = false, neg = false;
            foreach (var v in p.V)
            {
                double s = math.dot(n, v) + w;
                if (s > eps) pos = true; else if (s < -eps) neg = true;
            }
            if (pos && neg) return 0;
            return neg && !pos ? -1 : +1;
        }

        public sealed class Result
        {
            public Status status;
            public ConvexPoly positive, negative;
            public int onVertexCount, crossingEdgeCount, cutPolygonVertexCount;
            public string note = "";
        }

        struct Key : IEquatable<Key>
        {
            public int kind; // 0 original vertex, 1 intersection (edge lo,hi)
            public int a, b;
            public bool Equals(Key o) => kind == o.kind && a == o.a && b == o.b;
            public override int GetHashCode() => (kind * 397 ^ a) * 31 + b;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
        }

        public static Result Cut(ConvexPoly p, double3 n, double w, double eps)
        {
            var r = new Result();
            int V = p.V.Length;
            var s = new double[V];
            var cls = new int[V];
            bool supPos = false, supNeg = false;
            for (int i = 0; i < V; i++)
            {
                s[i] = math.dot(n, p.V[i]) + w;
                if (!double.IsFinite(s[i])) { r.status = Status.Degenerate; r.note = "non-finite signed distance"; return r; }
                cls[i] = s[i] > 0 ? 1 : s[i] < 0 ? -1 : 0;
                if (cls[i] == 0) r.onVertexCount++;
                if (s[i] > eps) supPos = true; else if (s[i] < -eps) supNeg = true;
            }
            if (!(supPos && supNeg))
            {
                r.status = supNeg ? Status.NotSplitNegative : Status.NotSplitPositive;
                return r;
            }

            // rounding-level on-plane detection (no epsilon): an intersection that rounds onto an endpoint means the plane passes
            // through that vertex at double resolution -> exact class 0 on both sides; then recompute crossings
            foreach (var e in p.Edges)
            {
                if (cls[e.v0] * cls[e.v1] < 0)
                {
                    double sa = s[e.v0], sb = s[e.v1];
                    double t = sa / (sa - sb);
                    double3 x = p.V[e.v0] + (p.V[e.v1] - p.V[e.v0]) * t;
                    if (x.Equals(p.V[e.v0])) cls[e.v0] = 0; else if (x.Equals(p.V[e.v1])) cls[e.v1] = 0;
                }
            }
            r.onVertexCount = 0;
            for (int i = 0; i < V; i++) if (cls[i] == 0) r.onVertexCount++;
            var cut = new Dictionary<Key, double3>();
            foreach (var e in p.Edges)
            {
                if (cls[e.v0] * cls[e.v1] < 0)
                {
                    double sa = s[e.v0], sb = s[e.v1];
                    double t = sa / (sa - sb);
                    double3 x = p.V[e.v0] + (p.V[e.v1] - p.V[e.v0]) * t;
                    cut[new Key { kind = 1, a = e.v0, b = e.v1 }] = x;
                    r.crossingEdgeCount++;
                }
            }

            r.positive = BuildSide(p, s, cls, cut, +1, out var notePos);
            r.negative = BuildSide(p, s, cls, cut, -1, out var noteNeg);
            r.note = notePos + noteNeg;
            r.cutPolygonVertexCount = r.positive != null ? r.positive.F[r.positive.F.Length - 1].Length : 0;
            r.status = (r.positive == null || r.negative == null) ? Status.Degenerate : Status.Ok;
            return r;
        }

        static ConvexPoly BuildSide(ConvexPoly p, double[] s, int[] cls, Dictionary<Key, double3> cut, int side, out string note)
        {
            note = "";
            var outV = new List<double3>();
            var map = new Dictionary<Key, int>();
            int Emit(Key k)
            {
                if (!map.TryGetValue(k, out int idx))
                {
                    idx = outV.Count;
                    outV.Add(k.kind == 0 ? p.V[k.a] : cut[k]);
                    map[k] = idx;
                }
                return idx;
            }

            var faces = new List<int[]>();
            var loopKeys = new List<Key>();
            var loopBoundary = new List<bool>();
            var next = new Dictionary<int, int>();   // boundary chain in kept-face direction (output indices)
            for (int f = 0; f < p.F.Length; f++)
            {
                var loop = p.F[f];
                loopKeys.Clear();
                bool allBoundary = true;
                for (int k = 0; k < loop.Length; k++)
                {
                    int a = loop[k], b = loop[(k + 1) % loop.Length];
                    int ca = cls[a] * side;
                    if (ca >= 0)
                    {
                        var ak = new Key { kind = 0, a = a };
                        if (loopKeys.Count == 0 || !loopKeys[loopKeys.Count - 1].Equals(ak)) loopKeys.Add(ak);
                        if (ca > 0) allBoundary = false;
                    }
                    if (cls[a] * cls[b] < 0) loopKeys.Add(new Key { kind = 1, a = math.min(a, b), b = math.max(a, b) });
                }
                if (loopKeys.Count >= 2 && loopKeys[loopKeys.Count - 1].Equals(loopKeys[0])) loopKeys.RemoveAt(loopKeys.Count - 1);
                if (loopKeys.Count < 3 || allBoundary) continue;
                var outLoop = new int[loopKeys.Count];
                loopBoundary.Clear();
                for (int k = 0; k < loopKeys.Count; k++)
                {
                    var key = loopKeys[k];
                    outLoop[k] = Emit(key);
                    loopBoundary.Add(key.kind == 1 || cls[key.a] == 0);
                }
                for (int k = 0; k < outLoop.Length; k++)
                {
                    int k2 = (k + 1) % outLoop.Length;
                    if (!loopBoundary[k] || !loopBoundary[k2] || outLoop[k] == outLoop[k2]) continue;
                    if (next.TryGetValue(outLoop[k], out int prev) && prev != outLoop[k2]) { note += side > 0 ? "[pos: boundary chain conflict]" : "[neg: boundary chain conflict]"; return null; }
                    next[outLoop[k]] = outLoop[k2];
                }
                faces.Add(outLoop);
            }

            if (next.Count < 3) { note += side > 0 ? "[pos: cut polygon < 3]" : "[neg: cut polygon < 3]"; return null; }

            var cutFace = new List<int>();
            int startV = -1;
            foreach (var kv in next) { startV = kv.Key; break; }
            int cur = startV;
            do
            {
                cutFace.Add(cur);
                if (!next.TryGetValue(cur, out cur) || cutFace.Count > next.Count) { note += side > 0 ? "[pos: boundary walk failed]" : "[neg: boundary walk failed]"; return null; }
            } while (cur != startV);
            if (cutFace.Count != next.Count) { note += side > 0 ? "[pos: boundary chain has several cycles]" : "[neg: boundary chain has several cycles]"; return null; }
            cutFace.Reverse();
            faces.Add(cutFace.ToArray());

            return new ConvexPoly(outV.ToArray(), faces.ToArray());
        }
    }
}
