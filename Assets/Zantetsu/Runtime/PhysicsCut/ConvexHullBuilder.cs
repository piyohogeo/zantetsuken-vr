using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What a hull build did and how far its result stands from the exact hull of the input (metres).</summary>
    public struct HullBuildReport
    {
        public int inputPoints, triangles, faces, vertices, merges;
        /// <summary>How far an input point stands outside the output's faces at most (an outer envelope: numeric noise only).</summary>
        public double shrink;
        /// <summary>How far an output vertex stands outside the exact triangle hull's supporting planes at most: what the simplification filled.</summary>
        public double expansion;
        public double extent;
        public string reason;
        /// <summary>Faces removed to reach the vertex limit, each within the merge tolerance.</summary>
        public int decimated;
        /// <summary>When the limit could not be reached within the merge tolerance: the expansion (metres) reaching it would have needed; else 0.</summary>
        public double expansionToReachLimit;
    }

    /// <summary>
    /// The convex hull of a point set as a B-rep with polygon faces (2026-09-30, the building hull trial), as an **outer
    /// envelope**: an incremental hull of triangles, the coplanar triangles merged into one face each (by adjacency, on a
    /// reference plane), every face then placed as the supporting plane of its normal over every input point -- so that
    /// no input point stands outside the result by more than numeric noise, and what a merge simplifies is filled, never
    /// trimmed -- and every vertex recomputed from the planes it stands on. A result that is not closed, not planar, not
    /// convex or not containing every point is refused with its reason. Managed, for a worker or Main; nothing of Unity
    /// is touched.
    /// </summary>
    public static class ConvexHullBuilder
    {
        /// <summary>The relative tolerance (of the points' extent) within which adjacent faces are one face. Two hulls that rest flush but a millimetre apart give sliver faces the clip kernel cannot walk; at this tolerance they are one face each, and the envelope grows by about this at most.</summary>
        public const double PlaneTolerance = 2e-3;

        /// <summary>
        /// The relative numeric tolerance (of the extent) of the checks -- containment, planarity, convexity -- and of the
        /// vertex collapse: 1e-5 (36 um at 3.6 m, 0.25 mm at 25 m), two hundred times under the merge tolerance. It is the
        /// scale at which a vertex where four planes nearly meet is defined at all: the clipping computes it from two
        /// nearly parallel edge-plane pairs about 1e-5 of the extent apart, which no arithmetic makes one point.
        /// </summary>
        public const double NumericTolerance = 1e-5;

        /// <summary>Tests only (diagnosis): when set on the building thread, every stage of a build writes what it made -- counts, the envelope's expansion, the worst reflex corner of the face polygons and that face's vertices, plane and corner values -- so that a refusal can be placed at its stage.</summary>
        [ThreadStatic] internal static List<string> traceForTest;

        /// <summary>
        /// The worst reflex corner of the face polygons (metres: how far a corner stands inside the chord of its neighbours,
        /// against the face's plane normal; positive is reflex), with its face and corner. The builder's own check and the
        /// diagnosis read the same measure.
        /// </summary>
        internal static double MaxReflex(IReadOnlyList<double3> verts, int[] faceOffsets, int[] faceIndices, IReadOnlyList<(double3 n, double d)> facePlanes, out int face, out int corner)
        {
            double worst = double.NegativeInfinity;
            face = -1; corner = -1;
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f], count = faceOffsets[f + 1] - from;
                double3 nrm = facePlanes[f].n;
                for (int k = 0; k < count; k++)
                {
                    double3 p0 = verts[faceIndices[from + k]], p1 = verts[faceIndices[from + (k + 1) % count]], p2 = verts[faceIndices[from + (k + 2) % count]];
                    double chord = math.length(p2 - p0);
                    double reflex = chord > 0.0 ? -math.dot(math.cross(p1 - p0, p2 - p1), nrm) / chord : 0.0;
                    if (reflex > worst) { worst = reflex; face = f; corner = k; }
                }
            }

            return worst;
        }

        /// <summary>The same measure over loops of vertex indices (the intersection's substages), with the worst loop described.</summary>
        internal static double MaxReflexOfLoops(IReadOnlyList<double3> verts, List<List<int>> loops, IReadOnlyList<(double3 n, double d)> planes, out string worstFace)
        {
            var offsets = new int[loops.Count + 1];
            var indices = new List<int>();
            for (int f = 0; f < loops.Count; f++) { indices.AddRange(loops[f]); offsets[f + 1] = indices.Count; }
            int[] idx = indices.ToArray();
            double r = MaxReflex(verts, offsets, idx, planes, out int face, out _);
            worstFace = DescribeFace(verts, offsets, idx, planes, face);
            return r;
        }

        /// <summary>A face for the diagnosis: its plane, its vertices in order with their distance to the plane, and every corner's reflex value.</summary>
        internal static string DescribeFace(IReadOnlyList<double3> verts, int[] faceOffsets, int[] faceIndices, IReadOnlyList<(double3 n, double d)> facePlanes, int f)
        {
            if (f < 0) return "(none)";
            int from = faceOffsets[f], count = faceOffsets[f + 1] - from;
            (double3 nrm, double d) = facePlanes[f];
            var t = new System.Text.StringBuilder("face " + f + " plane n " + nrm.x.ToString("R") + "," + nrm.y.ToString("R") + "," + nrm.z.ToString("R") + " d " + d.ToString("R") + ", " + count + " vertices:");
            for (int k = 0; k < count; k++)
            {
                int vi = faceIndices[from + k];
                double3 v = verts[vi], p0 = verts[faceIndices[from + (k + count - 1) % count]], p2 = verts[faceIndices[from + (k + 1) % count]];
                double chord = math.length(p2 - p0);
                double reflex = chord > 0.0 ? -math.dot(math.cross(v - p0, p2 - v), nrm) / chord : 0.0;
                t.Append(" [v" + vi + " " + v.x.ToString("R") + "," + v.y.ToString("R") + "," + v.z.ToString("R") + " off-plane " + (math.dot(nrm, v) - d).ToString("E2") + " reflex " + reflex.ToString("E2") + "]");
            }

            return t.ToString();
        }

        private sealed class Triangle
        {
            public int a, b, c;
            public double3 n;
            public double d;
            public bool dead;
        }

        public static bool TryBuild(IReadOnlyList<float3> points, out float3[] vertices, out int[] faceOffsets, out int[] faceIndices, out string reason)
        {
            bool built = TryBuild(points, out vertices, out faceOffsets, out faceIndices, out HullBuildReport report);
            reason = report.reason;
            return built;
        }

        public static bool TryBuild(IReadOnlyList<float3> points, out float3[] vertices, out int[] faceOffsets, out int[] faceIndices, out HullBuildReport report)
        {
            return TryBuild(points, 0, out vertices, out faceOffsets, out faceIndices, out report);
        }

        /// <summary>
        /// As above, within a vertex limit (0: none): an envelope past the limit is simplified by removing faces, the
        /// smallest first, each removal kept only while every input point stays inside and the expansion stays within
        /// the merge tolerance, until the limit is met -- or refused, with the expansion reaching it would have needed.
        /// </summary>
        public static bool TryBuild(IReadOnlyList<float3> points, int vertexLimit, out float3[] vertices, out int[] faceOffsets, out int[] faceIndices, out HullBuildReport report)
        {
            vertices = null; faceOffsets = null; faceIndices = null;
            report = new HullBuildReport { inputPoints = points.Count };
            int n = points.Count;
            if (n < 4) { report.reason = "fewer than four points"; return false; }
            var pts = new double3[n];
            double3 lo = double.MaxValue, hi = double.MinValue, centre = 0;
            for (int i = 0; i < n; i++) { pts[i] = points[i]; lo = math.min(lo, pts[i]); hi = math.max(hi, pts[i]); centre += pts[i]; }
            centre /= n;
            double extent = math.cmax(hi - lo);
            report.extent = extent;
            if (!(extent > 0.0) || !math.isfinite(extent)) { report.reason = "no extent"; return false; }
            double eps = 1e-7 * extent, numeric = NumericTolerance * extent;
            double3[] all = pts;   // every input point: the supports and the containment read them all
            int allCount = n;

            // Points that coincide within the tolerance (a cut's section vertices come from both children): one of each.
            var kept = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                bool dup = false;
                foreach (int k in kept) if (math.lengthsq(pts[i] - pts[k]) <= 1e-12 * extent * extent) { dup = true; break; }
                if (!dup) kept.Add(i);
            }

            if (kept.Count != n) { var unique = new double3[kept.Count]; for (int i = 0; i < kept.Count; i++) unique[i] = pts[kept[i]]; pts = unique; n = pts.Length; }
            if (n < 4) { report.reason = "fewer than four distinct points"; return false; }

            var triangles = new List<Triangle>(64);
            if (!TriangleHull(pts, eps, triangles, out string reason)) { report.reason = reason; return false; }
            report.triangles = triangles.Count;
            List<string> trace = traceForTest;
            if (trace != null)
            {
                double pointOut = 0.0, vertexOut = 0.0;
                Triangle worstT = null; int worstP = -1, degenerate = 0, over = 0;
                foreach (Triangle t in triangles)
                {
                    if (!math.all(math.isfinite(t.n))) degenerate++;
                    bool thisOver = false;
                    for (int i = 0; i < allCount; i++) { double h = math.dot(t.n, all[i]) - t.d; if (h > numeric) thisOver = true; if (h > pointOut) { pointOut = h; worstT = t; worstP = i; } }
                    if (thisOver) over++;
                    foreach (Triangle u in triangles) { vertexOut = math.max(vertexOut, math.dot(t.n, pts[u.a]) - t.d); vertexOut = math.max(vertexOut, math.dot(t.n, pts[u.b]) - t.d); vertexOut = math.max(vertexOut, math.dot(t.n, pts[u.c]) - t.d); }
                }

                var twinless = 0;
                var directed = new HashSet<long>();
                foreach (Triangle t in triangles) { directed.Add(Key(t.a, t.b)); directed.Add(Key(t.b, t.c)); directed.Add(Key(t.c, t.a)); }
                foreach (long k in directed) if (!directed.Contains(((k & 0xFFFFFFFFL) << 32) | (uint)(k >> 32))) twinless++;
                trace.Add("1 triangle hull: " + n + " distinct points of " + allCount + ", " + triangles.Count + " triangles (Euler " + (n - directed.Count / 2 + triangles.Count) + " over all distinct points, directed edges without a twin " + twinless + ", degenerate normals " + degenerate + ", triangles with a point outside past numeric " + over + "); an input point outside a triangle by " + pointOut.ToString("E2") + " m, a hull vertex outside a triangle by " + vertexOut.ToString("E2") + " m"
                    + (worstT != null ? " (the worst: triangle " + worstT.a + "," + worstT.b + "," + worstT.c + " n " + worstT.n + " area " + math.length(math.cross(pts[worstT.b] - pts[worstT.a], pts[worstT.c] - pts[worstT.a])).ToString("E2") + ", point " + worstP + " " + all[worstP] + ")" : "") + "; extent " + extent.ToString("R") + ", numeric " + numeric.ToString("E2") + ", merge tolerance " + (PlaneTolerance * extent).ToString("E2"));
            }
            // The exact hull's planes as supports over every input point: what the expansion is measured against.
            var exact = new List<(double3 n, double d)>(triangles.Count);
            foreach (Triangle t in triangles) { double d = double.NegativeInfinity; for (int i = 0; i < allCount; i++) d = math.max(d, math.dot(t.n, all[i])); exact.Add((t.n, d)); }

            // Coplanar triangles into one face each, by adjacency on a reference plane (each face's largest triangle): two
            // adjacent faces are one when both references and both triangles lie in the larger reference's plane. A chain
            // of slivers cannot carry a face onto another plane.
            double planeEps = PlaneTolerance * extent;
            double Area(Triangle t) => math.length(math.cross(pts[t.b] - pts[t.a], pts[t.c] - pts[t.a]));
            var byEdge = new Dictionary<long, Triangle>(triangles.Count * 3);
            foreach (Triangle t in triangles) { byEdge[Key(t.a, t.b)] = t; byEdge[Key(t.b, t.c)] = t; byEdge[Key(t.c, t.a)] = t; }
            var parent = new Dictionary<Triangle, Triangle>(triangles.Count);
            var reference = new Dictionary<Triangle, Triangle>(triangles.Count);
            foreach (Triangle t in triangles) { parent[t] = t; reference[t] = t; }
            Triangle Find(Triangle t) { while (!ReferenceEquals(parent[t], t)) { parent[t] = parent[parent[t]]; t = parent[t]; } return t; }
            bool InPlane(Triangle plane, Triangle t) => math.abs(math.dot(plane.n, pts[t.a]) - plane.d) <= planeEps && math.abs(math.dot(plane.n, pts[t.b]) - plane.d) <= planeEps && math.abs(math.dot(plane.n, pts[t.c]) - plane.d) <= planeEps;
            int merges = 0;
            foreach (Triangle t in triangles)
            {
                Twin(t.a, t.b); Twin(t.b, t.c); Twin(t.c, t.a);
                void Twin(int a, int b)
                {
                    if (!byEdge.TryGetValue(Key(b, a), out Triangle u)) return;
                    Triangle ra = Find(t), rb = Find(u);
                    if (ReferenceEquals(ra, rb)) return;
                    Triangle pa = reference[ra], pb = reference[rb];
                    Triangle big = Area(pa) >= Area(pb) ? pa : pb, small = ReferenceEquals(big, pa) ? pb : pa;
                    if (!InPlane(big, small) || !InPlane(big, t) || !InPlane(big, u)) return;
                    Triangle keep = ReferenceEquals(big, pa) ? ra : rb, drop = ReferenceEquals(keep, ra) ? rb : ra;
                    parent[drop] = keep;
                    reference[keep] = big;
                    merges++;
                }
            }

            report.merges = merges;
            // Each merged face as the supporting plane of its reference normal over every input point: the outer envelope's
            // half-spaces. What they enclose is computed exactly below; nothing is trimmed past numeric noise.
            var planes = new List<(double3 n, double d)>();
            var roots = new List<Triangle>(triangles.Count);
            foreach (Triangle t in triangles) roots.Add(Find(t));
            var planeOfRoot = new HashSet<Triangle>();
            for (int t = 0; t < triangles.Count; t++)
            {
                Triangle r = roots[t];
                if (!planeOfRoot.Add(r)) continue;
                double3 nrm = reference[r].n;
                double d = double.NegativeInfinity;
                for (int i = 0; i < allCount; i++) d = math.max(d, math.dot(nrm, all[i]));
                Fold(planes, nrm, d, planeEps);
            }

            trace?.Add("2 merges: " + merges + " triangle pairs merged, " + planes.Count + " supporting planes after the fold");

            // The intersection of the half-spaces, by clipping: the input's box, padded by twice the merge tolerance, cut by
            // every supporting plane in turn (each face's polygon against the half-space, the cut points shared along the
            // edges so that the cap closes exactly, the cap one new face). Two planes nearly the same leave a sliver, not a
            // failure: the vertices that coincide within numeric noise are one, and a face left without three corners
            // goes. A box face that survives (a direction no merged plane bounds) stands outside the exact hull and is
            // trimmed by the expansion loop below. Every vertex is the meeting of the planes it stands on, so every face is
            // planar to numeric noise.
            bool Intersect(List<(double3 n, double d)> set, out List<double3> outVerts, out int[] outOffsets, out int[] outIndices, out List<(double3 n, double d)> outPlanes, out string why)
            {
                outVerts = null; outOffsets = null; outIndices = null; outPlanes = null; why = null;
                double pad = 2.0 * planeEps + numeric;
                // A vertex within double-precision noise of a plane is on it (kept). Nothing wider: a vertex kept while it
                // stands outside by the numeric tolerance would leave the polytope non-convex by that much, and a plane
                // nearly parallel to that one then cuts such a corner as an island of its own.
                double onPlane = 1e-9 * extent;
                double3 blo = lo - pad, bhi = hi + pad;
                var verts = new List<double3>(64);
                for (int i = 0; i < 8; i++) verts.Add(new double3((i & 1) == 0 ? blo.x : bhi.x, (i & 2) == 0 ? blo.y : bhi.y, (i & 4) == 0 ? blo.z : bhi.z));
                var faces = new List<List<int>>(32);
                var facePlanesHere = new List<(double3 n, double d)>(32);
                void BoxFace(int a, int b, int c, int d, double3 nrm, double dist) { faces.Add(new List<int> { a, b, c, d }); facePlanesHere.Add((nrm, dist)); }
                BoxFace(1, 3, 7, 5, new double3(1, 0, 0), bhi.x); BoxFace(0, 4, 6, 2, new double3(-1, 0, 0), -blo.x);
                BoxFace(2, 6, 7, 3, new double3(0, 1, 0), bhi.y); BoxFace(0, 1, 5, 4, new double3(0, -1, 0), -blo.y);
                BoxFace(4, 5, 7, 6, new double3(0, 0, 1), bhi.z); BoxFace(0, 2, 3, 1, new double3(0, 0, -1), -blo.z);
                // Each box face wound so that its normal (right-hand rule) points outward.
                for (int f = 0; f < faces.Count; f++)
                {
                    List<int> loop = faces[f];
                    double3 nrm = math.cross(verts[loop[1]] - verts[loop[0]], verts[loop[2]] - verts[loop[0]]);
                    if (math.dot(nrm, facePlanesHere[f].n) < 0) loop.Reverse();
                }

                var cutOnEdge = new Dictionary<long, int>();
                var capNext = new Dictionary<int, int>();
                // The diagnosis (tests only): the first plane after which a face polygon is not convex, with what it cut.
                List<string> planeTrace = traceForTest;
                bool planeBreakReported = false;
                int planeIndex = -1;
                var capLinkFace = planeTrace != null ? new Dictionary<int, int>() : null;
                List<List<int>> facesBefore = null;
                List<(double3 n, double d)> planesBefore = null;
                double worstBefore = double.NegativeInfinity;
                var kept = new List<List<int>>(32);
                var keptPlanes = new List<(double3 n, double d)>(32);
                var side = new List<double>(64);
                foreach ((double3 n, double d) in set)
                {
                    planeIndex++;
                    if (planeTrace != null && !planeBreakReported)
                    {
                        facesBefore = new List<List<int>>();
                        foreach (List<int> f0 in faces) facesBefore.Add(new List<int>(f0));
                        planesBefore = new List<(double3 n, double d)>(facePlanesHere);
                        capLinkFace.Clear();
                    }

                    side.Clear();
                    for (int i = 0; i < verts.Count; i++) side.Add(math.dot(n, verts[i]) - d);
                    cutOnEdge.Clear();
                    capNext.Clear();
                    kept.Clear();
                    keptPlanes.Clear();
                    bool touched = false;
                    for (int f = 0; f < faces.Count; f++)
                    {
                        List<int> loop = faces[f];
                        int inside = 0;
                        foreach (int v in loop) if (side[v] <= onPlane) inside++;
                        if (inside == loop.Count) { kept.Add(loop); keptPlanes.Add(facePlanesHere[f]); continue; }
                        touched = true;
                        if (inside == 0) continue;
                        var clipped = new List<int>(loop.Count + 2);
                        // The face's cut edges run from an exit to the entry that follows it; the cap runs each the other
                        // way (entry -> exit). A face crossed more than once (a corner within noise of the plane) gives
                        // more than one such edge, each chained.
                        int firstEntry = -1, lastExit = -1;
                        for (int k = 0; k < loop.Count; k++)
                        {
                            int a = loop[k], b = loop[(k + 1) % loop.Count];
                            bool aIn = side[a] <= onPlane, bIn = side[b] <= onPlane;
                            if (aIn) clipped.Add(a);
                            if (aIn == bIn) continue;
                            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                            if (!cutOnEdge.TryGetValue(key, out int x))
                            {
                                // A vertex kept on the inner side within the on-plane noise counts as on the plane here: the cut point
                                // lies on the edge, never beyond its kept end (2026-09-30: a kept end 1.3e-10 m outside and the other
                                // 9.7e-8 m outside gave t = 1.0014 on a 16 m edge, a cut point 2.3 cm past the kept end -- a notch).
                                double sa = aIn ? math.min(side[a], 0.0) : side[a], sb = bIn ? math.min(side[b], 0.0) : side[b];
                                double t = sa / (sa - sb);
                                x = verts.Count;
                                verts.Add(verts[a] + t * (verts[b] - verts[a]));
                                side.Add(0.0);
                                cutOnEdge[key] = x;
                            }

                            clipped.Add(x);
                            if (aIn) lastExit = x;
                            else if (lastExit >= 0) { if (x != lastExit) { capNext[x] = lastExit; if (capLinkFace != null) capLinkFace[x] = f; } lastExit = -1; }
                            else firstEntry = x;
                        }

                        if (lastExit >= 0 && firstEntry >= 0 && firstEntry != lastExit) { capNext[firstEntry] = lastExit; if (capLinkFace != null) capLinkFace[firstEntry] = f; }
                        if (clipped.Count >= 3) { kept.Add(clipped); keptPlanes.Add(facePlanesHere[f]); }
                    }

                    if (!touched) continue;   // a redundant plane: nothing cut
                    faces = new List<List<int>>(kept);
                    facePlanesHere = new List<(double3 n, double d)>(keptPlanes);
                    // The cap: the cut edges chained into loops. One loop for a convex polytope; a corner an earlier cut
                    // point left standing out by its conditioning gives a tiny loop of its own, a sliver cap the collapse
                    // below removes. Each loop of three or more is a face on the plane.
                    var capVisited = new HashSet<int>();
                    foreach (int first in capNext.Keys)
                    {
                        if (capVisited.Contains(first)) continue;
                        var cap = new List<int>(capNext.Count);
                        int at = first;
                        for (int guard = 0; guard <= capNext.Count; guard++)
                        {
                            cap.Add(at);
                            capVisited.Add(at);
                            if (!capNext.TryGetValue(at, out at)) { why = "the cap of a clipping plane does not close (plane " + set.IndexOf((n, d)) + " of " + set.Count + ")"; return false; }
                            if (at == first) break;
                        }

                        if (at != first) { why = "the cap of a clipping plane does not close (plane " + set.IndexOf((n, d)) + " of " + set.Count + ")"; return false; }
                        if (cap.Count < 3) continue;
                        faces.Add(cap);
                        facePlanesHere.Add((n, d));
                    }

                    if (planeTrace != null && !planeBreakReported)
                    {
                        // Every face wound as the collapse winds it, measured; the first plane that leaves one not convex is written out.
                        var wound = new List<List<int>>(faces.Count);
                        for (int f = 0; f < faces.Count; f++)
                        {
                            var loop = new List<int>(faces[f]);
                            double3 newell = 0;
                            for (int k = 0; k < loop.Count; k++) newell += math.cross(verts[loop[k]], verts[loop[(k + 1) % loop.Count]]);
                            if (math.dot(newell, facePlanesHere[f].n) < 0) loop.Reverse();
                            wound.Add(loop);
                        }

                        var traceOffsets = new int[wound.Count + 1];
                        var traceIndices = new List<int>();
                        for (int f = 0; f < wound.Count; f++) { traceIndices.AddRange(wound[f]); traceOffsets[f + 1] = traceIndices.Count; }
                        double r = MaxReflex(verts, traceOffsets, traceIndices.ToArray(), facePlanesHere, out int rf, out int rc);
                        if (r > numeric)
                        {
                            planeBreakReported = true;
                            (double3 fn, double fd) = facePlanesHere[rf];
                            bool isCap = fn.Equals(n) && fd == d;
                            planeTrace.Add("3c the first plane that leaves a face not convex: plane " + planeIndex + " of " + set.Count + " (n " + n.x.ToString("R") + "," + n.y.ToString("R") + "," + n.z.ToString("R") + " d " + d.ToString("R") + "), worst reflex before it " + worstBefore.ToString("E2") + " m, after it " + r.ToString("E2") + " m at face " + rf + " corner " + rc + (isCap ? " (the plane's own cap)" : " (a face it clipped)"));
                            planeTrace.Add("3c   after: " + DescribeFace(verts, traceOffsets, traceIndices.ToArray(), facePlanesHere, rf));
                            for (int f = 0; f < planesBefore.Count; f++)
                            {
                                if (!planesBefore[f].n.Equals(fn) || planesBefore[f].d != fd) continue;
                                var t = new System.Text.StringBuilder("3c   the same face before: " + facesBefore[f].Count + " vertices:");
                                foreach (int v in facesBefore[f]) t.Append(" [v" + v + " " + verts[v].x.ToString("R") + "," + verts[v].y.ToString("R") + "," + verts[v].z.ToString("R") + " side " + (math.dot(n, verts[v]) - d).ToString("E3") + "]");
                                planeTrace.Add(t.ToString());
                            }

                            var links = new System.Text.StringBuilder("3c   the cap links (entry -> exit, the face that gave it):");
                            foreach (KeyValuePair<int, int> link in capNext)
                            {
                                capLinkFace.TryGetValue(link.Key, out int from);
                                links.Append(" [v" + link.Key + " -> v" + link.Value + " from face " + from + "]");
                            }

                            planeTrace.Add(links.ToString());
                            var cuts = new System.Text.StringBuilder("3c   the cut points of this plane:");
                            foreach (KeyValuePair<long, int> c in cutOnEdge)
                            {
                                int a = (int)(c.Key >> 32), b = (int)(c.Key & 0xFFFFFFFF);
                                cuts.Append(" [v" + c.Value + " on v" + a + "(" + (math.dot(n, verts[a]) - d).ToString("E3") + ")-v" + b + "(" + (math.dot(n, verts[b]) - d).ToString("E3") + ") at " + verts[c.Value].x.ToString("R") + "," + verts[c.Value].y.ToString("R") + "," + verts[c.Value].z.ToString("R") + "]");
                            }

                            planeTrace.Add(cuts.ToString());
                            // The faces this plane touched, before it: their vertices with the side of each.
                            for (int f = 0; f < facesBefore.Count; f++)
                            {
                                bool crossed = false;
                                foreach (int v in facesBefore[f]) if (math.dot(n, verts[v]) - d > onPlane) crossed = true;
                                if (!crossed) continue;
                                var t = new System.Text.StringBuilder("3c   touched face " + f + " (plane n " + planesBefore[f].n + " d " + planesBefore[f].d.ToString("F6") + "):");
                                foreach (int v in facesBefore[f]) t.Append(" v" + v + "(" + (math.dot(n, verts[v]) - d).ToString("E3") + ")");
                                planeTrace.Add(t.ToString());
                            }
                        }
                        else worstBefore = r;
                    }
                }

                // Vertices that coincide within numeric noise are one; a face keeps one of consecutive ones and needs three corners.
                var found = new List<double3>();
                var vertexOf = new Dictionary<int, int>();
                int Canonical(int v)
                {
                    if (vertexOf.TryGetValue(v, out int m)) return m;
                    for (int k = 0; k < found.Count; k++) if (math.lengthsq(found[k] - verts[v]) <= numeric * numeric) { vertexOf[v] = k; return k; }
                    vertexOf[v] = found.Count;
                    found.Add(verts[v]);
                    return found.Count - 1;
                }

                // A loop after the collapse: consecutive vertices that are one leave one, and a spike (a corner whose
                // neighbours are one vertex: a sliver's tip) goes with its tip, until nothing of either is left.
                void Clean(List<int> loop)
                {
                    bool changed = true;
                    while (changed && loop.Count > 2)
                    {
                        changed = false;
                        for (int k = loop.Count - 1; k >= 0; k--) if (loop[k] == loop[(k + 1) % loop.Count]) { loop.RemoveAt(k); changed = true; break; }
                        if (changed) continue;
                        for (int k = loop.Count - 1; k >= 0; k--) if (loop[(k + loop.Count - 1) % loop.Count] == loop[(k + 1) % loop.Count]) { loop.RemoveAt(k); changed = true; break; }
                    }
                }

                List<string> substage = traceForTest;
                double rawReflex = double.NaN, collapsedReflex = double.NaN;
                string rawFace = null, collapsedFace = null;
                if (substage != null)
                {
                    // The raw loops wound as the collapse winds them, so that the measure reads the same way.
                    var raw = new List<List<int>>(faces.Count);
                    for (int f = 0; f < faces.Count; f++)
                    {
                        var loop = new List<int>(faces[f]);
                        double3 newell = 0;
                        for (int k = 0; k < loop.Count; k++) newell += math.cross(verts[loop[k]], verts[loop[(k + 1) % loop.Count]]);
                        if (math.dot(newell, facePlanesHere[f].n) < 0) loop.Reverse();
                        raw.Add(loop);
                    }

                    rawReflex = MaxReflexOfLoops(verts, raw, facePlanesHere, out rawFace);
                }

                var loops = new List<List<int>>(faces.Count);
                var loopPlanes = new List<(double3 n, double d)>(faces.Count);
                for (int f = 0; f < faces.Count; f++)
                {
                    var loop = new List<int>(faces[f].Count);
                    foreach (int v in faces[f]) loop.Add(Canonical(v));
                    Clean(loop);
                    if (loop.Count < 3) continue;
                    // Wound outward: reversed when the polygon's normal runs against its plane's.
                    double3 newell = 0;
                    for (int k = 0; k < loop.Count; k++) newell += math.cross(found[loop[k]], found[loop[(k + 1) % loop.Count]]);
                    if (math.dot(newell, facePlanesHere[f].n) < 0) loop.Reverse();
                    loops.Add(loop);
                    loopPlanes.Add(facePlanesHere[f]);
                }

                if (substage != null) collapsedReflex = MaxReflexOfLoops(found, loops, loopPlanes, out collapsedFace);

                // Two faces on the same corners: a sliver wedge between two nearly parallel planes collapsed onto one
                // triangle from both sides. The same way round, one is kept; the other way round, both go (a flap of no
                // volume). Then a vertex on fewer than three faces lies on an edge or a face of the others: dropped from
                // every loop, and the loops cleaned again -- until nothing changes.
                string KeyOf(List<int> loop, bool reversed)
                {
                    int n = loop.Count, first = 0;
                    for (int k = 1; k < n; k++) if (loop[k] < loop[first]) first = k;
                    var t = new System.Text.StringBuilder();
                    for (int k = 0; k < n; k++) t.Append(loop[((first + (reversed ? -k : k)) % n + n) % n]).Append(',');
                    return t.ToString();
                }

                var removalEvents = substage != null ? new List<string>() : null;
                string LoopText(List<int> loop, (double3 n, double d) plane) { var t = new System.Text.StringBuilder("[plane n " + plane.n + " d " + plane.d.ToString("F6") + ":"); foreach (int v in loop) t.Append(" v" + v + " " + found[v]); return t.Append(']').ToString(); }
                bool changedFaces = true;
                while (changedFaces)
                {
                    changedFaces = false;
                    var byKey = new Dictionary<string, int>();
                    var drop = new HashSet<int>();
                    for (int f = 0; f < loops.Count; f++)
                    {
                        string same = KeyOf(loops[f], false), reversed = KeyOf(loops[f], true);
                        if (byKey.TryGetValue(same, out int twin)) { drop.Add(f); removalEvents?.Add("twin dropped: " + LoopText(loops[f], loopPlanes[f]) + " (same as " + LoopText(loops[twin], loopPlanes[twin]) + ")"); continue; }
                        if (byKey.TryGetValue(reversed, out int flap)) { drop.Add(f); drop.Add(flap); removalEvents?.Add("flap pair dropped: " + LoopText(loops[f], loopPlanes[f]) + " and " + LoopText(loops[flap], loopPlanes[flap])); continue; }
                        byKey[same] = f;
                    }

                    if (drop.Count > 0) { changedFaces = true; for (int f = loops.Count - 1; f >= 0; f--) if (drop.Contains(f)) { loops.RemoveAt(f); loopPlanes.RemoveAt(f); } }
                    var facesOf = new Dictionary<int, int>();
                    foreach (List<int> loop in loops) foreach (int v in loop) { facesOf.TryGetValue(v, out int k); facesOf[v] = k + 1; }
                    for (int f = 0; f < loops.Count; f++)
                    {
                        List<int> loop = loops[f];
                        int before = loop.Count;
                        if (removalEvents != null) foreach (int v in loop) if (facesOf[v] < 3) removalEvents.Add("vertex v" + v + " " + found[v] + " on " + facesOf[v] + " face(s) dropped from " + LoopText(loop, loopPlanes[f]));
                        loop.RemoveAll(v => facesOf[v] < 3);
                        string beforeClean = removalEvents != null ? LoopText(loop, loopPlanes[f]) : null;
                        Clean(loop);
                        if (removalEvents != null && loop.Count != before) removalEvents.Add("  -> after the clean " + LoopText(loop, loopPlanes[f]) + (beforeClean != LoopText(loop, loopPlanes[f]) ? " (the clean removed spikes or repeats from " + beforeClean + ")" : ""));
                        if (loop.Count != before) changedFaces = true;
                    }

                    for (int f = loops.Count - 1; f >= 0; f--) if (loops[f].Count < 3) { loops.RemoveAt(f); loopPlanes.RemoveAt(f); changedFaces = true; }
                }

                if (loops.Count < 4) { why = "fewer than four faces"; return false; }
                if (substage != null)
                {
                    double cleanedReflex = MaxReflexOfLoops(found, loops, loopPlanes, out string cleanedFace);
                    if (rawReflex > numeric || collapsedReflex > numeric || cleanedReflex > numeric)
                    {
                        substage.Add("3a intersection substages (" + set.Count + " planes): worst reflex after the clipping " + rawReflex.ToString("E2") + " m (" + verts.Count + " clip vertices), after the collapse and clean " + collapsedReflex.ToString("E2") + " m (" + found.Count + " vertices), after the twin/flap/valence removal " + cleanedReflex.ToString("E2") + " m");
                        substage.Add("3a   after the clipping: " + rawFace);
                        substage.Add("3a   after the collapse: " + collapsedFace);
                        substage.Add("3a   after the removal: " + cleanedFace);
                        foreach (string e in removalEvents) substage.Add("3b   " + e);
                    }
                }

                var remap = new Dictionary<int, int>();
                var finalVerts = new List<double3>();
                var offsets = new List<int>(loops.Count + 1) { 0 };
                var indices = new List<int>();
                foreach (List<int> loop in loops)
                {
                    foreach (int v in loop)
                    {
                        if (!remap.TryGetValue(v, out int m)) { m = finalVerts.Count; remap[v] = m; finalVerts.Add(found[v]); }
                        indices.Add(m);
                    }

                    offsets.Add(indices.Count);
                }

                outVerts = finalVerts;
                outOffsets = offsets.ToArray();
                outIndices = indices.ToArray();
                outPlanes = loopPlanes;
                return true;
            }

            // The envelope stands outside the exact hull by the merge tolerance at most. A merged face's plane can leave
            // unbounded what its slivers alone bounded (the polytope then runs off far away): the merge whose exact plane
            // the farthest vertex violates most is undone -- every exact plane of that cluster put back, folded against
            // the reference plane they came from, never a lone triangle's plane beside its cluster's shifted one (that
            // pair breeds sliver strips) -- and the polytope made again, until every vertex is within the tolerance.
            List<double3> verts;
            List<(double3 n, double d)> facePlanes;
            double expansion;
            int restored = 0;
            var undone = new HashSet<Triangle>();
            for (int round = 0; ; round++)
            {
                if (!Intersect(planes, out verts, out faceOffsets, out faceIndices, out facePlanes, out reason)) { faceOffsets = null; faceIndices = null; report.reason = reason; return false; }
                expansion = 0.0;
                int worst = -1;
                foreach (double3 v in verts) for (int t = 0; t < exact.Count; t++) { double e = math.dot(exact[t].n, v) - exact[t].d; if (e > expansion) { expansion = e; worst = t; } }
                if (trace != null)
                {
                    double r = MaxReflex(verts, faceOffsets, faceIndices, facePlanes, out int rf, out int rc);
                    trace.Add("3 intersection round " + round + ": " + planes.Count + " planes, " + verts.Count + " vertices, " + (faceOffsets.Length - 1) + " faces, expansion " + expansion.ToString("E2") + " m; worst reflex " + r.ToString("E2") + " m at face " + rf + " corner " + rc + (r > numeric ? ": " + DescribeFace(verts, faceOffsets, faceIndices, facePlanes, rf) : ""));
                }
                if (expansion <= planeEps) break;
                if (round >= exact.Count) { faceOffsets = null; faceIndices = null; report.reason = "the envelope does not close in on the exact hull (" + expansion.ToString("E2") + " m out)"; return false; }
                Triangle cluster = roots[worst];
                if (undone.Add(cluster))
                {
                    for (int u = 0; u < exact.Count; u++) if (ReferenceEquals(roots[u], cluster)) Fold(planes, exact[u].n, exact[u].d, planeEps);
                    restored++;
                }
                else
                {
                    planes.Add(exact[worst]);   // folded away before its cluster was undone: back as it is
                }
            }

            report.merges = merges - restored;

            // The vertex limit: faces removed, the smallest first, while the envelope keeps every point and stays within
            // the merge tolerance of the exact hull. A limit not met that way is refused with the expansion it would need.
            double Expansion(List<double3> vs) { double e = 0.0; foreach (double3 v in vs) for (int t = 0; t < exact.Count; t++) e = math.max(e, math.dot(exact[t].n, v) - exact[t].d); return e; }
            double AreaOf(int plane, List<(double3 n, double d)> fp, int[] fo, int[] fi, List<double3> vs)
            {
                for (int f = 0; f < fp.Count; f++)
                {
                    if (!fp[f].n.Equals(planes[plane].n) || fp[f].d != planes[plane].d) continue;
                    double3 nrm = 0; int from = fo[f], count = fo[f + 1] - from;
                    for (int k = 0; k < count; k++) nrm += math.cross(vs[fi[from + k]], vs[fi[from + (k + 1) % count]]);
                    return 0.5 * math.length(nrm);
                }

                return 0.0;
            }

            if (vertexLimit > 0 && verts.Count > vertexLimit)
            {
                bool bounded = true;
                while (verts.Count > vertexLimit)
                {
                    var order = new List<int>(planes.Count);
                    for (int i = 0; i < planes.Count; i++) order.Add(i);
                    var areas = new double[planes.Count];
                    for (int i = 0; i < planes.Count; i++) areas[i] = AreaOf(i, facePlanes, faceOffsets, faceIndices, verts);
                    order.Sort((x, y) => areas[x].CompareTo(areas[y]));
                    bool removed = false;
                    foreach (int i in order)
                    {
                        var trial = new List<(double3 n, double d)>(planes);
                        trial.RemoveAt(i);
                        if (trial.Count < 4 || !Intersect(trial, out List<double3> tv, out int[] to, out int[] ti, out List<(double3 n, double d)> tp, out _)) continue;
                        double e = Expansion(tv);
                        if (bounded && e > planeEps) continue;
                        if (tv.Count >= verts.Count) continue;
                        planes = trial; verts = tv; faceOffsets = to; faceIndices = ti; facePlanes = tp; expansion = e;
                        if (bounded) report.decimated++;
                        if (trace != null)
                        {
                            double r = MaxReflex(verts, faceOffsets, faceIndices, facePlanes, out int rf, out int rc);
                            trace.Add("4 decimation: plane " + i + " removed (area " + areas[i].ToString("E2") + "), " + planes.Count + " planes, " + verts.Count + " vertices, " + (faceOffsets.Length - 1) + " faces, expansion " + e.ToString("E2") + " m; worst reflex " + r.ToString("E2") + " m at face " + rf + (r > numeric ? ": " + DescribeFace(verts, faceOffsets, faceIndices, facePlanes, rf) : ""));
                        }
                        removed = true;
                        break;
                    }

                    if (!removed)
                    {
                        if (bounded) { bounded = false; continue; }   // nothing more within the tolerance: past it, for the report of what it would need
                        break;
                    }
                }

                if (!bounded)
                {
                    report.expansionToReachLimit = verts.Count <= vertexLimit ? expansion : double.PositiveInfinity;
                    faceOffsets = null; faceIndices = null;
                    report.reason = "past the vertex limit " + vertexLimit + " within the merge tolerance " + planeEps.ToString("E2") + " m (" + report.decimated + " faces removed); reaching it would need an expansion of "
                        + (double.IsPositiveInfinity(report.expansionToReachLimit) ? "more than any face removal gives" : report.expansionToReachLimit.ToString("E2") + " m (" + (report.expansionToReachLimit / planeEps).ToString("F1") + " x the tolerance)");
                    return false;
                }
            }

            report.faces = faceOffsets.Length - 1;
            report.vertices = verts.Count;
            if (trace != null)
            {
                double r = MaxReflex(verts, faceOffsets, faceIndices, facePlanes, out int rf, out int rc);
                var asFloat = new List<double3>(verts.Count);
                foreach (double3 v in verts) asFloat.Add((double3)(float3)v);
                double rFloat = MaxReflex(asFloat, faceOffsets, faceIndices, facePlanes, out int ff, out int fc);
                trace.Add("5 before the checks: " + verts.Count + " vertices (limit " + vertexLimit + "), " + report.faces + " faces, decimated " + report.decimated + ", expansion " + expansion.ToString("E2") + " m; worst reflex in double " + r.ToString("E2") + " m at face " + rf + " corner " + rc + ", in the float output " + rFloat.ToString("E2") + " m at face " + ff + " corner " + fc
                    + (r > numeric ? "; " + DescribeFace(verts, faceOffsets, faceIndices, facePlanes, rf) : ""));
            }

            // The checks, apart from each other: closed edges; planar faces; convex (every vertex inside every face, every
            // face polygon convex); containment of every input point. The shrink and the expansion are measured.
            var seen = new Dictionary<long, int>();
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f], count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    long key = Key(faceIndices[from + k], faceIndices[from + (k + 1) % count]);
                    if (seen.TryGetValue(key, out int other))
                    {
                        int[] offsetsCopy = faceOffsets, indicesCopy = faceIndices;
                        string Loop(int face) { var t = new System.Text.StringBuilder("["); for (int i = offsetsCopy[face]; i < offsetsCopy[face + 1]; i++) t.Append(indicesCopy[i]).Append(' '); return t.Append(']').ToString(); }
                        report.reason = "an edge runs the same way twice (" + (key >> 32) + "->" + (int)(key & 0xFFFFFFFF) + " in faces " + other + " " + Loop(other) + " and " + f + " " + Loop(f) + " of " + report.faces + "; vertices " + verts.Count + ")";
                        return false;
                    }

                    seen[key] = f;
                }
            }

            foreach (long key in seen.Keys) if (!seen.ContainsKey(((key & 0xFFFFFFFFL) << 32) | (uint)(key >> 32))) { report.reason = "an edge has one face"; return false; }
            int edgeCount = seen.Count / 2;
            if (verts.Count - edgeCount + report.faces != 2) { report.reason = "not a closed convex (Euler " + (verts.Count - edgeCount + report.faces) + ")"; return false; }
            double shrink = 0.0, worstPlanar = 0.0, worstConvex = 0.0;
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f], count = faceOffsets[f + 1] - from;
                (double3 nrm, double d) = facePlanes[f];
                for (int k = 0; k < count; k++) worstPlanar = math.max(worstPlanar, math.abs(math.dot(nrm, verts[faceIndices[from + k]]) - d));
                foreach (double3 v in verts) worstConvex = math.max(worstConvex, math.dot(nrm, v) - d);
                for (int i = 0; i < allCount; i++) shrink = math.max(shrink, math.dot(nrm, all[i]) - d);
                for (int k = 0; k < count; k++)
                {
                    // A reflex corner, measured as how far the corner stands inside the chord of its neighbours (metres).
                    double3 p0 = verts[faceIndices[from + k]], p1 = verts[faceIndices[from + (k + 1) % count]], p2 = verts[faceIndices[from + (k + 2) % count]];
                    double chord = math.length(p2 - p0);
                    double reflex = chord > 0.0 ? -math.dot(math.cross(p1 - p0, p2 - p1), nrm) / chord : 0.0;
                    if (reflex > numeric) { report.reason = "a face polygon is not convex (" + reflex.ToString("E2") + " m at a corner of face " + f + " with " + count + " vertices: " + p0 + " " + p1 + " " + p2 + ")"; return false; }
                }
            }

            report.shrink = shrink;
            if (worstPlanar > numeric) { report.reason = "a face is not planar (" + worstPlanar.ToString("E2") + " m)"; return false; }
            if (worstConvex > numeric) { report.reason = "a vertex stands outside another face (" + worstConvex.ToString("E2") + " m)"; return false; }
            if (shrink > numeric) { report.reason = "an input point stands outside a face (" + shrink.ToString("E2") + " m)"; return false; }
            report.expansion = expansion;
            vertices = new float3[verts.Count];
            for (int i = 0; i < verts.Count; i++) vertices[i] = (float3)verts[i];
            return true;
        }

        /// <summary>
        /// A plane into the half-space set: one nearly the same as a plane already there (two faces not adjacent but
        /// parallel -- a cut's two halves' tops -- or turned against each other by a hair) folds into it, the outer of the
        /// two kept; else it is added. Nearly the same: normals within about 1.4e-4 rad and supports within half the merge
        /// tolerance, so that what the fold trims away stays under the merge tolerance over the extent. Two such planes
        /// left apart would clip only slivers between them.
        /// </summary>
        private static int Fold(List<(double3 n, double d)> planes, double3 nrm, double d, double planeEps)
        {
            for (int i = 0; i < planes.Count; i++)
            {
                (double3 pn, double pd) = planes[i];
                if (math.dot(pn, nrm) < 1.0 - 1e-8 || math.abs(pd - d) > 0.5 * planeEps) continue;
                if (d > pd) planes[i] = (nrm, d);
                return i;
            }

            planes.Add((nrm, d));
            return planes.Count - 1;
        }

        /// <summary>Tests only: the triangle hull of the points alone (deduplicated as a build does), and how far any input point stands outside any of its triangles (metres); NaN when there is none.</summary>
        internal static double TriangleHullWorstOutsideForTest(IReadOnlyList<float3> points, out int triangleCount)
        {
            triangleCount = 0;
            int n = points.Count;
            var all = new double3[n];
            double3 lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < n; i++) { all[i] = points[i]; lo = math.min(lo, all[i]); hi = math.max(hi, all[i]); }
            double extent = math.cmax(hi - lo);
            var kept = new List<double3>(n);
            foreach (double3 p in all) { bool dup = false; foreach (double3 k in kept) if (math.lengthsq(p - k) <= 1e-12 * extent * extent) { dup = true; break; } if (!dup) kept.Add(p); }
            var triangles = new List<Triangle>();
            if (!TriangleHull(kept.ToArray(), 1e-7 * extent, triangles, out _)) return double.NaN;
            triangleCount = triangles.Count;
            double worst = 0.0;
            foreach (Triangle t in triangles) foreach (double3 p in all) worst = math.max(worst, math.dot(t.n, p) - t.d);
            return worst;
        }

        /// <summary>The grid of the triangle hull's orientation tests (metres a unit: 2^-20, about 0.95 um): the points are snapped to it for the exact predicate only; every plane is supported over the original points after.</summary>
        internal const double OrientationGrid = 1.0 / 1048576.0;

        /// <summary>
        /// The sign of the orientation of d against the plane of a, b, c (positive: d stands on the side the right-hand
        /// normal of a, b, c points to), on grid points (integers held exactly in doubles), exact: a floating filter first
        /// (the determinant against its error bound over the permanent), the exact integer determinant when the filter
        /// cannot decide.
        /// </summary>
        internal static int Orient(double3[] g, int a, int b, int c, int d)
        {
            double3 ab = g[b] - g[a], ac = g[c] - g[a], ad = g[d] - g[a];   // exact: integers well under 2^53
            double c0 = ab.y * ac.z - ab.z * ac.y, c1 = ab.z * ac.x - ab.x * ac.z, c2 = ab.x * ac.y - ab.y * ac.x;
            double det = c0 * ad.x + c1 * ad.y + c2 * ad.z;
            double permanent = (math.abs(ab.y * ac.z) + math.abs(ab.z * ac.y)) * math.abs(ad.x) + (math.abs(ab.z * ac.x) + math.abs(ab.x * ac.z)) * math.abs(ad.y) + (math.abs(ab.x * ac.y) + math.abs(ab.y * ac.x)) * math.abs(ad.z);
            double bound = 1e-15 * permanent;   // above the orient3d filter's (7 + 56e)e ~ 7.8e-16
            if (det > bound) return 1;
            if (det < -bound) return -1;
            System.Numerics.BigInteger X(double v) => new System.Numerics.BigInteger((long)v);
            System.Numerics.BigInteger bx = X(ab.x), by = X(ab.y), bz = X(ab.z), cx = X(ac.x), cy = X(ac.y), cz = X(ac.z), dx = X(ad.x), dy = X(ad.y), dz = X(ad.z);
            System.Numerics.BigInteger exact = (by * cz - bz * cy) * dx + (bz * cx - bx * cz) * dy + (bx * cy - by * cx) * dz;
            return exact.Sign;
        }

        /// <summary>
        /// The incremental hull of triangles (outward, counter-clockwise) over distinct points, or false with why there is
        /// none. Its combinatorics are decided by the exact orientation predicate on the points snapped to the orientation
        /// grid (2026-09-30: a floating visibility threshold let a point barely above a sliver's neighbour leave an edge
        /// reflex by that threshold; next to a sliver such an edge tilts the surface by millimetres over the extent, and
        /// later insertions folded it by metres -- 11.7 m on the refused 102-point candidate), so that the surface is convex
        /// exactly over the snapped points. The normals are the snapped triangles'; the supports are taken over the original
        /// points by the caller.
        /// </summary>
        private static bool TriangleHull(double3[] pts, double eps, List<Triangle> triangles, out string reason)
        {
            reason = null;
            int n = pts.Length;
            var g = new double3[n];
            for (int i = 0; i < n; i++) g[i] = math.round(pts[i] / OrientationGrid);
            // Points that snap onto an earlier point's grid point are that point for the combinatorics (inside or on the hull).
            var snappedTwin = new bool[n];
            var seenGrid = new HashSet<(double, double, double)>();
            for (int i = 0; i < n; i++) if (!seenGrid.Add((g[i].x, g[i].y, g[i].z))) snappedTwin[i] = true;

            // The first tetrahedron: the extremes along x, the farthest from that line, the farthest from that plane.
            int i0 = -1, i1 = -1;
            for (int i = 0; i < n; i++) { if (snappedTwin[i]) continue; if (i0 < 0 || pts[i].x < pts[i0].x) i0 = i; if (i1 < 0 || pts[i].x > pts[i1].x) i1 = i; }
            if (i0 < 0 || math.lengthsq(pts[i1] - pts[i0]) <= eps * eps) { reason = "all points coincide"; return false; }
            int i2 = -1; double best = 0;
            double3 d01 = pts[i1] - pts[i0];
            for (int i = 0; i < n; i++) { if (snappedTwin[i]) continue; double l = math.length(math.cross(d01, pts[i] - pts[i0])); if (l > best) { best = l; i2 = i; } }
            if (i2 < 0 || best <= eps * math.length(d01)) { reason = "collinear"; return false; }
            int i3 = -1; best = 0;
            double3 nn = math.normalize(math.cross(d01, pts[i2] - pts[i0]));
            for (int i = 0; i < n; i++) { if (snappedTwin[i]) continue; double h = math.abs(math.dot(nn, pts[i] - pts[i0])); if (h > best) { best = h; i3 = i; } }
            if (i3 < 0 || best <= eps || Orient(g, i0, i1, i2, i3) == 0) { reason = "coplanar"; return false; }

            var live = new Dictionary<long, Triangle>(64);   // each directed edge to the live triangle that runs it
            void Add(int a, int b, int c)
            {
                var t = new Triangle { a = a, b = b, c = c };
                t.n = math.normalize(math.cross(g[b] - g[a], g[c] - g[a]));   // the snapped triangle's normal (never degenerate: its vertices are not collinear on the grid)
                t.d = math.dot(t.n, pts[a]);
                triangles.Add(t);
                live[Key(a, b)] = t; live[Key(b, c)] = t; live[Key(c, a)] = t;
            }

            // Outward: the tetrahedron's fourth vertex below.
            void AddOriented(int a, int b, int c, int inside) { if (Orient(g, a, b, c, inside) > 0) Add(a, c, b); else Add(a, b, c); }

            AddOriented(i0, i1, i2, i3); AddOriented(i0, i1, i3, i2); AddOriented(i0, i2, i3, i1); AddOriented(i1, i2, i3, i0);
            var visible = new List<Triangle>();
            var marked = new HashSet<Triangle>();
            var stack = new Stack<Triangle>();
            var horizon = new List<(int a, int b)>();
            for (int p = 0; p < n; p++)
            {
                if (p == i0 || p == i1 || p == i2 || p == i3 || snappedTwin[p]) continue;
                // A face the point stands strictly outside of (exactly), the farthest by the floating height; none: the point is inside or on the hull.
                Triangle seed = null;
                double farthest = double.NegativeInfinity;
                foreach (Triangle t in triangles)
                {
                    if (t.dead || Orient(g, t.a, t.b, t.c, p) <= 0) continue;
                    double h = math.dot(t.n, pts[p]) - t.d;
                    if (seed == null || h > farthest) { farthest = h; seed = t; }
                }

                if (seed == null) continue;
                // The visible region, grown from that face across the edges to faces the point is strictly outside of (exactly):
                // on a convex surface that set is one connected patch, its rim one loop.
                visible.Clear(); marked.Clear(); stack.Clear();
                stack.Push(seed); marked.Add(seed);
                while (stack.Count > 0)
                {
                    Triangle t = stack.Pop();
                    visible.Add(t);
                    Across(t.b, t.a); Across(t.c, t.b); Across(t.a, t.c);
                    void Across(int a, int b)
                    {
                        if (!live.TryGetValue(Key(a, b), out Triangle twin) || twin.dead || marked.Contains(twin)) return;
                        if (Orient(g, twin.a, twin.b, twin.c, p) > 0) { marked.Add(twin); stack.Push(twin); }
                    }
                }

                horizon.Clear();
                foreach (Triangle t in visible)
                {
                    Rim(t.a, t.b); Rim(t.b, t.c); Rim(t.c, t.a);
                    void Rim(int a, int b) { if (!live.TryGetValue(Key(b, a), out Triangle twin) || !marked.Contains(twin)) horizon.Add((a, b)); }
                }

                foreach (Triangle t in visible)
                {
                    t.dead = true;
                    if (live.TryGetValue(Key(t.a, t.b), out Triangle e) && ReferenceEquals(e, t)) live.Remove(Key(t.a, t.b));
                    if (live.TryGetValue(Key(t.b, t.c), out e) && ReferenceEquals(e, t)) live.Remove(Key(t.b, t.c));
                    if (live.TryGetValue(Key(t.c, t.a), out e) && ReferenceEquals(e, t)) live.Remove(Key(t.c, t.a));
                }

                foreach ((int a, int b) in horizon) Add(a, b, p);
                if (triangles.Count > 4096) triangles.RemoveAll(t => t.dead);
            }

            triangles.RemoveAll(t => t.dead);
            return true;
        }

        private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        /// <summary>One edge per unordered vertex pair: f0 the face that runs it from its lower vertex to its higher one, f1 the other way (the kernel's convention).</summary>
        public static void BuildEdges(int[] faceOffsets, int[] faceIndices, out int[] faceEdges, out BrepEdge[] edges)
        {
            var found = new Dictionary<long, int>();
            var table = new List<BrepEdge>();
            faceEdges = new int[faceIndices.Length];
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f];
                int count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = faceIndices[from + k];
                    int b = faceIndices[from + ((k + 1) % count)];
                    int lo = math.min(a, b), hi = math.max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!found.TryGetValue(key, out int at))
                    {
                        at = table.Count;
                        found.Add(key, at);
                        table.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                    }

                    BrepEdge edge = table[at];
                    if (a == lo) { if (edge.f0 < 0) edge.f0 = f; }
                    else if (edge.f1 < 0) edge.f1 = f;
                    table[at] = edge;
                    faceEdges[from + k] = at;
                }
            }

            edges = table.ToArray();
        }

        /// <summary>The collider mesh's arrays (any thread): the faces fanned into triangles, wound for Unity.</summary>
        public static void ColliderArrays(float3[] vertices, int[] faceOffsets, int[] faceIndices, out Vector3[] meshVertices, out int[] triangles)
        {
            meshVertices = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) meshVertices[i] = vertices[i];
            var tris = new List<int>(faceIndices.Length * 3);
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f], to = faceOffsets[f + 1];
                for (int k = from + 1; k + 1 < to; k++) { tris.Add(faceIndices[from]); tris.Add(faceIndices[k + 1]); tris.Add(faceIndices[k]); }
            }

            triangles = tris.ToArray();
        }
    }

    /// <summary>
    /// A convex B-rep in native memory with its bank and range (one convex), made from arrays: what an authored hull
    /// shape is filled from, and what a hull's mass properties are read from. Disposed by its owner.
    /// </summary>
    public sealed unsafe class HullBrep : IDisposable
    {
        private NativeArray<float3> _vertices;
        private NativeArray<int> _offsets, _indices, _faceEdges;
        private NativeArray<BrepEdge> _edges;
        private bool _disposed;

        public ConvexBrepBank Bank { get; private set; }
        public ConvexBrepRange Range { get; private set; }
        public int VertexCount => Range.vertexCount;
        public int FaceCount => Range.faceCount;
        public float3 Lo { get; private set; }
        public float3 Hi { get; private set; }

        public static HullBrep FromArrays(float3[] vertices, int[] faceOffsets, int[] faceIndices)
        {
            ConvexHullBuilder.BuildEdges(faceOffsets, faceIndices, out int[] faceEdges, out BrepEdge[] edges);
            var brep = new HullBrep
            {
                _vertices = new NativeArray<float3>(vertices, Allocator.Persistent),
                _offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent),
                _indices = new NativeArray<int>(faceIndices, Allocator.Persistent),
                _faceEdges = new NativeArray<int>(faceEdges, Allocator.Persistent),
                _edges = new NativeArray<BrepEdge>(edges, Allocator.Persistent),
            };
            brep.Bank = new ConvexBrepBank
            {
                vertices = (float3*)brep._vertices.GetUnsafePtr(),
                faceOffsets = (int*)brep._offsets.GetUnsafePtr(),
                faceIndices = (int*)brep._indices.GetUnsafePtr(),
                faceEdges = (int*)brep._faceEdges.GetUnsafePtr(),
                edges = (BrepEdge*)brep._edges.GetUnsafePtr(),
            };
            int maxLoop = 0;
            for (int f = 0; f + 1 < faceOffsets.Length; f++) maxLoop = math.max(maxLoop, faceOffsets[f + 1] - faceOffsets[f]);
            brep.Range = new ConvexBrepRange
            {
                vertexBase = 0, vertexCount = vertices.Length, faceBase = 0, faceCount = faceOffsets.Length - 1,
                faceIndexBase = 0, faceIndexCount = faceIndices.Length, edgeBase = 0, edgeCount = edges.Length, maxFaceLoop = maxLoop,
            };
            float3 lo = float.MaxValue, hi = float.MinValue;
            foreach (float3 v in vertices) { lo = math.min(lo, v); hi = math.max(hi, v); }
            brep.Lo = lo; brep.Hi = hi;
            return brep;
        }

        /// <summary>The convex hull of points, or null with the reason.</summary>
        public static HullBrep OfPoints(IReadOnlyList<float3> points, out string reason)
        {
            return ConvexHullBuilder.TryBuild(points, out float3[] v, out int[] o, out int[] i, out reason) ? FromArrays(v, o, i) : null;
        }

        /// <summary>The convex hull of points within a vertex limit (faces removed within the merge tolerance), or null with the reason.</summary>
        public static HullBrep OfPoints(IReadOnlyList<float3> points, int vertexLimit, out HullBuildReport report)
        {
            return ConvexHullBuilder.TryBuild(points, vertexLimit, out float3[] v, out int[] o, out int[] i, out report) ? FromArrays(v, o, i) : null;
        }

        /// <summary>The vertices of a convex, read from any bank and range (a shape's, a product's).</summary>
        public static void CopyVertices(in ConvexBrepBank bank, in ConvexBrepRange range, List<float3> into, float4x4 transform)
        {
            for (int i = 0; i < range.vertexCount; i++) into.Add(math.transform(transform, bank.vertices[range.vertexBase + i]));
        }

        /// <summary>The unit-density mass properties of this hull (its volume, first and second moments).</summary>
        public MassProperties Mass()
        {
            var m = new MassProperties();
            BrepBuffer view = Bank.View(Range);
            MassPropertiesKernel.ComputeDouble(in view, ref m);
            return m;
        }

        /// <summary>A collider mesh of this hull (Main): the faces fanned into triangles, wound for Unity, baked convex here.</summary>
        public Mesh MakeColliderMesh(string name, MeshColliderCookingOptions cooking)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            var vertices = new Vector3[Range.vertexCount];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = _vertices[i];
            mesh.vertices = vertices;
            var triangles = new List<int>(Range.faceIndexCount * 3);
            for (int f = 0; f < Range.faceCount; f++)
            {
                int from = _offsets[f], to = _offsets[f + 1];
                for (int k = from + 1; k + 1 < to; k++) { triangles.Add(_indices[from]); triangles.Add(_indices[k + 1]); triangles.Add(_indices[k]); }
            }

            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            Physics.BakeMesh(mesh.GetEntityId(), true, cooking);
            return mesh;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_edges.IsCreated) _edges.Dispose();
            if (_faceEdges.IsCreated) _faceEdges.Dispose();
            if (_indices.IsCreated) _indices.Dispose();
            if (_offsets.IsCreated) _offsets.Dispose();
            if (_vertices.IsCreated) _vertices.Dispose();
        }
    }
}
