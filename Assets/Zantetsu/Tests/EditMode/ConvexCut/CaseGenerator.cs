using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    public sealed class CutCase
    {
        public string id;
        public string family;
        public string planeClass;
        public int seed;
        public ConvexPoly poly;
        public double3 n;
        public double w;
        public double eps;
        public int V => poly.V.Length;
        public override string ToString() => id;
    }

    /// <summary>
    /// Test-only deterministic corpus generator (probe Harness/CaseGenerator + Fixtures). All shapes are closed convex
    /// polygon B-reps in double, centered near the origin (the common local frame of DESIGN 7.2).
    /// </summary>
    public static class CaseGenerator
    {
        public static readonly int[] VertexSeries = { 4, 8, 16, 32, 64, 96, 128 };
        public const double DefaultEpsRel = 1e-5;

        // ---------------- shape families ----------------

        public static ConvexPoly Tetra()
        {
            var v = new[] { new double3(1, 1, 1), new double3(1, -1, -1), new double3(-1, 1, -1), new double3(-1, -1, 1) };
            var f = new[] { new[] { 0, 1, 2 }, new[] { 0, 1, 3 }, new[] { 0, 2, 3 }, new[] { 1, 2, 3 } };
            return Orient(new ConvexPoly(v, f));
        }

        public static ConvexPoly Box() => Prism(4, 1, 1);

        public static ConvexPoly SkewBox()
        {
            var b = Box();
            for (int i = 0; i < b.V.Length; i++) { var v = b.V[i]; b.V[i] = new double3(v.x + 0.4 * v.y + 0.2 * v.z, v.y + 0.1 * v.z, v.z * 0.7); }
            return b;
        }

        /// n-gon prism: 2 polygon caps + n quads. V = 2n.
        public static ConvexPoly Prism(int n, double radius, double halfHeight)
        {
            var v = new double3[2 * n];
            for (int k = 0; k < n; k++)
            {
                double a = 2 * math.PI * k / n + (n == 4 ? math.PI / 4 : 0);
                double cx = radius * math.cos(a), cz = radius * math.sin(a);
                if (n == 4) { double c = radius * math.SQRT2 / 2; cx = k == 1 || k == 2 ? -c : c; cz = k >= 2 ? -c : c; }   // exact +-c
                v[k] = new double3(cx, halfHeight, cz);
                v[n + k] = new double3(cx, -halfHeight, cz);
            }
            var f = new List<int[]>();
            var top = new int[n]; var bot = new int[n];
            for (int k = 0; k < n; k++) { top[k] = n - 1 - k; bot[k] = n + k; }
            f.Add(top); f.Add(bot);
            for (int k = 0; k < n; k++) { int k1 = (k + 1) % n; f.Add(new[] { k, k1, n + k1, n + k }); }
            return Orient(new ConvexPoly(v, f.ToArray()));
        }

        /// Two apexes at y = +-1 and a ring of V-2 vertices at y = 0 (float-rounded ring positions, as the probe fixture).
        public static ConvexPoly Bipyramid(int vertexCount)
        {
            int n = vertexCount - 2;
            var v = new double3[vertexCount];
            v[0] = new double3(0, 1, 0);
            v[1] = new double3(0, -1, 0);
            for (int k = 0; k < n; k++)
            {
                float a = 2f * math.PI * k / n;
                v[2 + k] = new float3(math.cos(a), 0, math.sin(a));
            }
            var f = new List<int[]>();
            for (int k = 0; k < n; k++)
            {
                int r0 = 2 + k, r1 = 2 + (k + 1) % n;
                f.Add(new[] { 0, r0, r1 });
                f.Add(new[] { 1, r1, r0 });
            }
            return Orient(new ConvexPoly(v, f.ToArray()));
        }

        public static ConvexPoly RandomSphereHull(int vertexCount, int seed, double3 scale)
        {
            var rng = new Unity.Mathematics.Random((uint)(seed * 7919 + 17));
            var pts = new double3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                double z = rng.NextDouble() * 2 - 1;
                double t = rng.NextDouble() * 2 * math.PI;
                double r = math.sqrt(1 - z * z);
                pts[i] = new double3(r * math.cos(t), z, r * math.sin(t)) * scale;
            }
            return ConvexHull3.Build(pts, 1e-12);
        }

        /// apex + ring: high valence apex (V-1 incident edges); a clip below the apex creates V-1 new vertices.
        public static ConvexPoly Cone(int vertexCount, double apexHeight = 1.0)
        {
            int n = vertexCount - 1;
            var v = new double3[vertexCount];
            v[0] = new double3(0, apexHeight, 0);
            for (int k = 0; k < n; k++) { double a = 2 * math.PI * k / n; v[1 + k] = new double3(math.cos(a), 0, math.sin(a)); }
            var f = new List<int[]>();
            var baseLoop = new int[n];
            for (int k = 0; k < n; k++) baseLoop[k] = 1 + k;
            f.Add(baseLoop);
            for (int k = 0; k < n; k++) f.Add(new[] { 0, 1 + (k + 1) % n, 1 + k });
            return Orient(new ConvexPoly(v, f.ToArray()));
        }

        /// Ring with tiny jitter (near-coplanar faces) plus two apexes.
        public static ConvexPoly NearCoplanarHull(int vertexCount, int seed, double jitter = 1e-6)
        {
            var rng = new Unity.Mathematics.Random((uint)(seed * 104729 + 3));
            int n = vertexCount - 2;
            var pts = new double3[vertexCount];
            pts[0] = new double3(0, 0.5, 0);
            pts[1] = new double3(0, -0.5, 0);
            for (int k = 0; k < n; k++)
            {
                double a = 2 * math.PI * k / n;
                pts[2 + k] = new double3(math.cos(a), (rng.NextDouble() * 2 - 1) * jitter, math.sin(a));
            }
            return ConvexHull3.Build(pts, 1e-14);
        }

        static ConvexPoly Orient(ConvexPoly p)
        {
            var c = p.VertexCentroid();
            for (int f = 0; f < p.F.Length; f++)
                if (math.dot(p.FaceNormalUnnormalized(f), p.FaceCentroid(f) - c) < 0) Array.Reverse(p.F[f]);
            p.InvalidateEdges();
            return p;
        }

        // ---------------- plane classes ----------------

        public static readonly string[] PlaneClasses =
        {
            "missPositive", "missNegative", "center", "offCenter", "grazing", "throughVertex", "throughEdge", "nearFace", "manyEdge", "random",
            // DESIGN 7.6 exact-clip classes:
            "nearPlaneNoSplit",   // extreme vertex at d = +0.5 eps: crosses d = 0 but has no positive robust support -> must inherit uncut (negative)
            "nearPlaneSplit",     // one interior vertex at d = +0.5 eps while both robust supports exist -> Split, and that vertex clips by sign (positive)
        };

        static double3 RandomDir(ref Unity.Mathematics.Random rng)
        {
            double z = rng.NextDouble() * 2 - 1;
            double t = rng.NextDouble() * 2 * math.PI;
            double r = math.sqrt(1 - z * z);
            return new double3(r * math.cos(t), z, r * math.sin(t));
        }

        static void SupportRange(ConvexPoly p, double3 n, out double smin, out double smax)
        {
            smin = double.MaxValue; smax = double.MinValue;
            foreach (var v in p.V) { double s = math.dot(n, v); smin = math.min(smin, s); smax = math.max(smax, s); }
        }

        /// Returns (n, w) for the requested class. s(x) = dot(n,x) + w.
        public static void MakePlane(ConvexPoly p, string cls, int seed, double eps, out double3 n, out double w)
        {
            var rng = new Unity.Mathematics.Random((uint)(seed * 2654435761u + 12345));
            double3 c = p.VertexCentroid();
            double ext = p.MaxExtent();
            switch (cls)
            {
                case "missPositive": { n = RandomDir(ref rng); SupportRange(p, n, out var lo, out _); w = -lo + 0.05 * ext; return; }
                case "missNegative": { n = RandomDir(ref rng); SupportRange(p, n, out _, out var hi); w = -hi - 0.05 * ext; return; }
                case "center": { n = RandomDir(ref rng); w = -math.dot(n, c); return; }
                case "offCenter": { n = RandomDir(ref rng); w = -math.dot(n, c) - 0.3 * ext * (rng.NextDouble() * 2 - 1); return; }
                case "grazing": { n = RandomDir(ref rng); SupportRange(p, n, out _, out var hi); w = -hi + 5 * eps; return; }
                case "nearPlaneNoSplit": { n = RandomDir(ref rng); SupportRange(p, n, out _, out var hi); w = -hi + 0.5 * eps; return; }
                case "nearPlaneSplit":
                    {
                        n = RandomDir(ref rng);
                        double wc = -math.dot(n, c);
                        int best = 0; double bd = double.MaxValue;
                        for (int i = 0; i < p.V.Length; i++) { double d = math.abs(math.dot(n, p.V[i]) + wc); if (d < bd) { bd = d; best = i; } }
                        w = -math.dot(n, p.V[best]) + 0.5 * eps;
                        return;
                    }
                case "throughVertex": { n = RandomDir(ref rng); int k = rng.NextInt(p.V.Length); w = -math.dot(n, p.V[k]); return; }
                case "throughEdge":
                    {
                        var e = p.Edges[rng.NextInt(p.EdgeCount)];
                        double3 d = math.normalize(p.V[e.v1] - p.V[e.v0]);
                        double3 r = RandomDir(ref rng);
                        n = math.normalize(r - d * math.dot(r, d));
                        w = -math.dot(n, p.V[e.v0]);
                        return;
                    }
                case "nearFace":
                    {
                        int f = rng.NextInt(p.F.Length);
                        p.FacePlane(f, out n, out var d);
                        w = -d + 10 * eps; // face vertices strictly positive by ~10 eps, rest negative
                        return;
                    }
                case "manyEdge":
                    {
                        p.Bounds(out var mn, out var mx);
                        double3 e3 = mx - mn;
                        n = e3.y >= e3.x && e3.y >= e3.z ? new double3(0, 1, 0) : e3.x >= e3.z ? new double3(1, 0, 0) : new double3(0, 0, 1);
                        SupportRange(p, n, out var lo, out var hi);
                        int best = -1; w = -(lo + hi) * 0.5;
                        for (int k = 1; k < 40; k++)
                        {
                            double cand = -(lo + (hi - lo) * (k / 40.0)) + 3.7 * eps; // avoid exact vertex hits
                            int cross = 0;
                            foreach (var e in p.Edges)
                            {
                                double sa = math.dot(n, p.V[e.v0]) + cand, sb = math.dot(n, p.V[e.v1]) + cand;
                                if ((sa > eps && sb < -eps) || (sa < -eps && sb > eps)) cross++;
                            }
                            if (cross > best) { best = cross; w = cand; }
                        }
                        return;
                    }
                default:
                    {
                        n = RandomDir(ref rng); SupportRange(p, n, out var lo, out var hi);
                        w = -(lo + (hi - lo) * rng.NextDouble());
                        return;
                    }
            }
        }

        // ---------------- corpus ----------------

        /// The probe corpus (19 families x plane classes x seeds + exact d == 0 / reduction planes). seedsPerCombo = 2 gives the probe's 880 cases.
        public static List<CutCase> Corpus(int seedsPerCombo = 2)
        {
            var list = new List<CutCase>();
            var shapes = new List<(string family, int V, ConvexPoly poly)>();
            shapes.Add(("tetra", 4, Tetra()));
            shapes.Add(("box", 8, Box()));
            shapes.Add(("skewBox", 8, SkewBox()));
            foreach (var ng in new[] { 3, 4, 8, 16, 32 }) shapes.Add(("prism" + ng, 2 * ng, Prism(ng, 1, 0.6)));
            foreach (var v in new[] { 8, 16, 32, 64, 96, 128 }) shapes.Add(("bipyramid", v, Bipyramid(v)));
            foreach (var v in VertexSeries) if (v >= 8) shapes.Add(("sphereHull", v, RandomSphereHull(v, v, 1)));
            foreach (var v in new[] { 16, 64, 128 }) shapes.Add(("elongated", v, RandomSphereHull(v, v + 1, new double3(4, 1, 0.5))));
            foreach (var v in new[] { 16, 64 }) shapes.Add(("thin", v, RandomSphereHull(v, v + 2, new double3(1, 1e-3, 1))));
            foreach (var v in new[] { 16, 64, 128 }) shapes.Add(("cone", v, Cone(v)));
            foreach (var v in new[] { 16, 64, 128 }) shapes.Add(("nearCoplanar", v, NearCoplanarHull(v, v)));
            shapes.Add(("offsetScaled", 32, RandomSphereHull(32, 5, 1).Transformed(new double3(100, 100, 100), new double3(1e4, -2e3, 5e3))));
            shapes.Add(("tiny", 32, RandomSphereHull(32, 6, 1).Transformed(new double3(1e-3, 1e-3, 1e-3), 0)));
            shapes.Add(("reductionStress", 128, Cone(128)));
            shapes.Add(("reductionStressHull", 128, RandomSphereHull(128, 99, 1)));
            shapes.Add(("skewBoxRot", 8, SkewBox().Rotated(quaternion.EulerXYZ(0.3f, 1.1f, -0.7f))));

            foreach (var (family, V, poly) in shapes)
            {
                double eps = DefaultEpsRel * poly.MaxExtent();
                foreach (var cls in PlaneClasses)
                {
                    for (int s = 0; s < seedsPerCombo; s++)
                    {
                        int seed = (StableHash(family) & 0xffff) * 131 + V * 17 + StableHash(cls) % 977 + s;
                        MakePlane(poly, cls, seed, eps, out var n, out var w);
                        list.Add(new CutCase { id = family + "-v" + V + "-" + cls + "-" + s, family = family, planeClass = cls, seed = seed, poly = poly, n = n, w = w, eps = eps });
                    }
                }
                // exact d == 0 vertices (axis-aligned planes through symmetric coordinates: the dot products are exact)
                if (family == "bipyramid")
                    list.Add(new CutCase { id = family + "-v" + V + "-exactRing-0", family = family, planeClass = "exactZero", seed = 0, poly = poly, n = new double3(0, 1, 0), w = 0, eps = eps });
                if (family == "box")
                {
                    list.Add(new CutCase { id = family + "-v" + V + "-exactEdge-0", family = family, planeClass = "exactZero", seed = 0, poly = poly, n = math.normalize(new double3(1, 0, 1)), w = 0, eps = eps });
                    list.Add(new CutCase { id = family + "-v" + V + "-exactFace-0", family = family, planeClass = "exactZeroNoSplit", seed = 0, poly = poly, n = new double3(0, 1, 0), w = -1, eps = eps });
                }
                if (family == "cone")
                    list.Add(new CutCase { id = family + "-v" + V + "-exactBase-0", family = family, planeClass = "exactZeroNoSplit", seed = 0, poly = poly, n = new double3(0, 1, 0), w = 0, eps = eps });
                if (family.StartsWith("reductionStress") || family == "cone")
                {
                    double3 n = new double3(0, 1, 0);
                    SupportRange(poly, n, out var lo, out var hi);
                    double w = -(hi - 0.1 * (hi - lo));
                    list.Add(new CutCase { id = family + "-v" + V + "-reduction-0", family = family, planeClass = "reduction", seed = 0, poly = poly, n = n, w = w, eps = eps });
                }
            }
            return list;
        }

        /// Reduction corpus additions (probe Phase 3): grazing / throughVertex planes on V = 128 shapes.
        public static List<CutCase> ReductionExtraCases()
        {
            var cases = new List<CutCase>();
            foreach (var (fam, poly) in new[] { ("sphereHull128g", RandomSphereHull(128, 7, 1)), ("elongated128g", RandomSphereHull(128, 8, new double3(4, 1, 0.5))), ("bipyramid128g", Bipyramid(128)), ("cone128g", Cone(128)) })
            {
                double eps = DefaultEpsRel * poly.MaxExtent();
                for (int seed = 0; seed < 6; seed++)
                {
                    MakePlane(poly, "grazing", 500 + seed, eps, out var n, out var w);
                    cases.Add(new CutCase { id = fam + "-grazing-" + seed, family = fam, planeClass = "grazing", seed = seed, poly = poly, n = n, w = w, eps = eps });
                    MakePlane(poly, "throughVertex", 700 + seed, eps, out n, out w);
                    cases.Add(new CutCase { id = fam + "-throughVertex-" + seed, family = fam, planeClass = "throughVertex", seed = seed, poly = poly, n = n, w = w, eps = eps });
                }
            }
            return cases;
        }

        /// string.GetHashCode is randomised per process in some runtimes; the corpus needs stable seeds.
        static int StableHash(string s)
        {
            unchecked
            {
                int h = 5381;
                foreach (var ch in s) h = h * 33 + ch;
                return h & 0x7fffffff;
            }
        }
    }
}
