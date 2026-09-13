using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Test-only strong verifier (probe Reference/BrepVerifier). Never inside a timing loop, never connected to the
    /// product runtime (DESIGN 7.2: no runtime validator).
    /// </summary>
    public static class BrepVerifier
    {
        public sealed class Report
        {
            public readonly List<string> failures = new List<string>();
            public bool Ok => failures.Count == 0;
            public int V, E, F, I, degenerateFaces, coincidentVertexPairs;
            public double volume;
            public void Fail(string what) { if (failures.Count < 32) failures.Add(what); else if (failures.Count == 32) failures.Add("..."); }
            public override string ToString() => Ok ? "ok" : string.Join("; ", failures);
        }

        public sealed class Options
        {
            public double relTol = 1e-9;      // planarity / convexity / containment tolerance relative to extent
            public int maxVertices = 128;
            public ConvexPoly source;         // containment check (Q subset of source)
            public bool checkHalfspace;
            public double3 planeN; public double planeW; public int side; // side = +1: all s >= -tol
            public double halfspaceTol = 0;   // absolute
            public bool requireDistinctVertices = true;
            public double distinctRelTol = 1e-12;
            public double extentOverride = 0;
            // resolution rule (0 = off): exact clip at floating-point resolution legitimately leaves rounding-level residue next to a vertex the
            // plane passes through (one intersection per crossing edge, each within a few ulp of that vertex). Topology is checked exactly
            // regardless; geometry that has no meaning below resolution is counted instead of failed: bitwise-coincident vertices
            // (Report.coincidentVertexPairs) and faces whose vertices are collinear within this tolerance (Report.degenerateFaces,
            // no plane -> outward / planarity / convexity checks skipped). This is not a plane slab: classification stays d > 0 / d < 0 / d == 0.
            public double resolutionRelTol = 0;
        }

        /// 32 ulp of the lane's precision: the resolution below which a face has no plane and two vertices are the same point
        public const double FloatResolution = 32 * 1.1920929e-7;
        public const double DoubleResolution = 32 * 2.220446049250313e-16;

        /// True when every vertex of face f lies within tol of one line (or one point): the face has no plane at that resolution.
        public static bool CollinearAtResolution(ConvexPoly p, int f, double tol)
        {
            var loop = p.F[f];
            int bi = 0, bj = 1; double best = -1;
            for (int i = 0; i < loop.Length; i++)
                for (int j = i + 1; j < loop.Length; j++)
                {
                    double d = math.distance(p.V[loop[i]], p.V[loop[j]]);
                    if (d > best) { best = d; bi = i; bj = j; }
                }
            if (best <= tol) return true;
            double3 a = p.V[loop[bi]], u = (p.V[loop[bj]] - a) / best;
            foreach (var k in loop) if (math.length(math.cross(p.V[k] - a, u)) > tol) return false;
            return true;
        }

        public static Report Verify(ConvexPoly p, Options o)
        {
            var r = new Report();
            if (p == null) { r.Fail("null"); return r; }
            int V = p.V.Length, F = p.F.Length;
            r.V = V; r.F = F;
            if (V < 4) r.Fail("V<4");
            if (F < 4) r.Fail("F<4");
            if (V > o.maxVertices) r.Fail("V>" + o.maxVertices + " (" + V + ")");
            foreach (var v in p.V) if (!math.all(math.isfinite(v))) { r.Fail("non-finite vertex"); break; }
            if (!r.Ok && (V < 4 || F < 4)) return r;
            double extent = math.max(o.extentOverride > 0 ? o.extentOverride : p.MaxExtent(), 1e-300);
            double tol = o.relTol * extent;
            double magnitude = extent;
            foreach (var v in p.V) magnitude = math.max(magnitude, math.cmax(math.abs(v)));
            double resTol = o.resolutionRelTol * magnitude;

            var used = new bool[V];
            int I = 0;
            for (int f = 0; f < F; f++)
            {
                var loop = p.F[f];
                I += loop.Length;
                if (loop.Length < 3) r.Fail("face " + f + " has " + loop.Length + " vertices");
                for (int k = 0; k < loop.Length; k++)
                {
                    int i = loop[k];
                    if (i < 0 || i >= V) { r.Fail("face " + f + " index out of range"); break; }
                    used[i] = true;
                    for (int j = k + 1; j < loop.Length; j++) if (loop[j] == i) { r.Fail("face " + f + " repeats vertex " + i); break; }
                }
            }
            r.I = I;
            for (int i = 0; i < V; i++) if (!used[i]) { r.Fail("unreferenced vertex " + i); break; }
            if (!r.Ok) return r;

            if (o.requireDistinctVertices)
            {
                for (int i = 0; i < V && r.failures.Count < 8; i++)
                    for (int j = i + 1; j < V; j++)
                        if (o.distinctRelTol <= 0 ? p.V[i].Equals(p.V[j]) : math.distance(p.V[i], p.V[j]) <= o.distinctRelTol * extent)
                        {
                            if (o.resolutionRelTol > 0 && p.V[i].Equals(p.V[j])) { r.coincidentVertexPairs++; continue; }
                            r.Fail("duplicate vertex positions " + i + "," + j); break;
                        }
            }

            // edges: exactly two faces, opposite directions
            p.InvalidateEdges();
            var edgeFaces = new Dictionary<long, (int fwd, int bwd)>();
            for (int f = 0; f < F; f++)
            {
                var loop = p.F[f];
                for (int k = 0; k < loop.Length; k++)
                {
                    int a = loop[k], b = loop[(k + 1) % loop.Length];
                    long key = ((long)math.min(a, b) << 32) | (uint)math.max(a, b);
                    edgeFaces.TryGetValue(key, out var ef);
                    if (a < b) ef.fwd++; else ef.bwd++;
                    edgeFaces[key] = ef;
                }
            }
            int E = edgeFaces.Count;
            r.E = E;
            foreach (var kv in edgeFaces)
                if (kv.Value.fwd != 1 || kv.Value.bwd != 1) { r.Fail("edge " + (kv.Key >> 32) + "-" + (int)kv.Key + " faces fwd=" + kv.Value.fwd + " bwd=" + kv.Value.bwd); if (r.failures.Count > 8) break; }
            if (V - E + F != 2) r.Fail("Euler V-E+F=" + (V - E + F));

            var faceSets = new HashSet<string>();
            for (int f = 0; f < F; f++)
            {
                var sorted = (int[])p.F[f].Clone(); Array.Sort(sorted);
                if (!faceSets.Add(string.Join(",", sorted))) r.Fail("duplicate face " + f);
            }

            // planarity, outward winding, convexity (all vertices behind every face plane)
            double3 centroid = p.VertexCentroid();
            for (int f = 0; f < F; f++)
            {
                if (o.resolutionRelTol > 0 && CollinearAtResolution(p, f, resTol)) { r.degenerateFaces++; continue; }
                double3 nn = p.FaceNormalUnnormalized(f);
                double len = math.length(nn);
                if (len <= 0) { r.Fail("face " + f + " zero area"); continue; }
                double3 n = nn / len;
                double3 fc = p.FaceCentroid(f);
                if (math.dot(n, fc - centroid) <= 0) r.Fail("face " + f + " not outward");
                foreach (var i in p.F[f])
                    if (math.abs(math.dot(n, p.V[i] - fc)) > tol) { r.Fail("face " + f + " non-planar"); break; }
                double worst = 0;
                for (int i = 0; i < V; i++) worst = math.max(worst, math.dot(n, p.V[i] - fc));
                if (worst > tol) r.Fail("face " + f + " has vertices outside by " + worst.ToString("G3"));
                if (r.failures.Count > 16) break;
            }

            r.volume = ReferenceMassProperties.Volume(p);
            if (!(r.volume > 0) || !math.isfinite(r.volume)) r.Fail("volume " + r.volume);

            if (o.checkHalfspace)
            {
                double worst = 0;
                for (int i = 0; i < V; i++) worst = math.max(worst, -(math.dot(o.planeN, p.V[i]) + o.planeW) * o.side);
                if (worst > o.halfspaceTol + tol) r.Fail("vertex outside half-space by " + worst.ToString("G3"));
            }

            if (o.source != null)
            {
                double worst = 0;
                double srcMagnitude = magnitude;
                foreach (var v in o.source.V) srcMagnitude = math.max(srcMagnitude, math.cmax(math.abs(v)));
                for (int f = 0; f < o.source.F.Length; f++)
                {
                    // a source face below resolution (rounding residue of an earlier cut / a flat reduction cap) has no usable plane
                    if (o.resolutionRelTol > 0 && CollinearAtResolution(o.source, f, o.resolutionRelTol * srcMagnitude)) continue;
                    o.source.FacePlane(f, out var n, out var d);
                    for (int i = 0; i < V; i++) worst = math.max(worst, math.dot(n, p.V[i]) - d);
                }
                if (worst > tol) r.Fail("vertex outside source by " + worst.ToString("G3"));
            }
            return r;
        }
    }
}
