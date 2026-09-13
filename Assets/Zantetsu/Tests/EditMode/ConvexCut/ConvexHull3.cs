using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>Test-only incremental 3D convex hull (double) generating triangle-faced hulls for the corpus.</summary>
    public static class ConvexHull3
    {
        sealed class Face
        {
            public int a, b, c;
            public double3 n; // unit outward
            public double d;
            public bool dead;
        }

        public static ConvexPoly Build(double3[] pts, double eps = 1e-9)
        {
            int n = pts.Length;
            if (n < 4) throw new ArgumentException("need >= 4 points");
            int i0 = 0, i1 = 0;
            for (int i = 1; i < n; i++) { if (pts[i].x < pts[i0].x) i0 = i; if (pts[i].x > pts[i1].x) i1 = i; }
            if (i0 == i1) throw new ArgumentException("degenerate points (x)");
            int i2 = -1; double best = 0;
            double3 d01 = pts[i1] - pts[i0];
            for (int i = 0; i < n; i++)
            {
                double l = math.length(math.cross(d01, pts[i] - pts[i0]));
                if (l > best) { best = l; i2 = i; }
            }
            if (i2 < 0 || best <= eps) throw new ArgumentException("degenerate points (collinear)");
            int i3 = -1; best = 0;
            double3 nn = math.normalize(math.cross(d01, pts[i2] - pts[i0]));
            for (int i = 0; i < n; i++)
            {
                double h = math.abs(math.dot(nn, pts[i] - pts[i0]));
                if (h > best) { best = h; i3 = i; }
            }
            if (i3 < 0 || best <= eps) throw new ArgumentException("degenerate points (coplanar)");

            var faces = new List<Face>();
            double3 centroid = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25;
            void AddFace(int a, int b, int c)
            {
                var f = new Face { a = a, b = b, c = c };
                f.n = math.normalize(math.cross(pts[b] - pts[a], pts[c] - pts[a]));
                f.d = math.dot(f.n, pts[a]);
                faces.Add(f);
            }
            void AddFaceOriented(int a, int b, int c)
            {
                double3 nrm = math.cross(pts[b] - pts[a], pts[c] - pts[a]);
                if (math.dot(nrm, (pts[a] + pts[b] + pts[c]) / 3.0 - centroid) < 0) AddFace(a, c, b); else AddFace(a, b, c);
            }
            AddFaceOriented(i0, i1, i2); AddFaceOriented(i0, i1, i3); AddFaceOriented(i0, i2, i3); AddFaceOriented(i1, i2, i3);

            var visible = new List<Face>();
            var horizon = new Dictionary<long, (int a, int b)>();
            for (int p = 0; p < n; p++)
            {
                if (p == i0 || p == i1 || p == i2 || p == i3) continue;
                visible.Clear();
                foreach (var f in faces) if (!f.dead && math.dot(f.n, pts[p]) - f.d > eps) visible.Add(f);
                if (visible.Count == 0) continue;
                horizon.Clear();
                foreach (var f in visible)
                {
                    AddDirected(horizon, f.a, f.b); AddDirected(horizon, f.b, f.c); AddDirected(horizon, f.c, f.a);
                }
                foreach (var f in visible)
                {
                    horizon.Remove(Key(f.b, f.a)); horizon.Remove(Key(f.c, f.b)); horizon.Remove(Key(f.a, f.c));
                    f.dead = true;
                }
                foreach (var kv in horizon) AddFace(kv.Value.a, kv.Value.b, p);
                if (faces.Count > 4096) faces.RemoveAll(f => f.dead);
            }
            faces.RemoveAll(f => f.dead);

            var remap = new int[n];
            for (int i = 0; i < n; i++) remap[i] = -1;
            var verts = new List<double3>();
            int Map(int i) { if (remap[i] < 0) { remap[i] = verts.Count; verts.Add(pts[i]); } return remap[i]; }
            var F = new int[faces.Count][];
            for (int i = 0; i < faces.Count; i++) F[i] = new[] { Map(faces[i].a), Map(faces[i].b), Map(faces[i].c) };
            return new ConvexPoly(verts.ToArray(), F);
        }

        static long Key(int a, int b) => ((long)a << 32) | (uint)b;
        static void AddDirected(Dictionary<long, (int, int)> h, int a, int b) { h[Key(a, b)] = (a, b); }
    }
}
