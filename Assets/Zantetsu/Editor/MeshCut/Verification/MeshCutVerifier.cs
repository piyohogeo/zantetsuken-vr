using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>What the harness reads back from one kernel run: the input, the two side geometries and the kernel's correspondence outputs.</summary>
    public sealed class CutRun
    {
        public CutGeometry Input;
        public float4 Plane;
        public CutGeometry Positive, Negative;
        public MeshCutResult Result;
        public MeshCutIndexRange[] OutputRanges;
        public uint NewVertexBase;
        public int NewVertexCount;
        public int[] NewVertexTopology;
        public long[] NodeKeys;
        public float[] NodeParams;
        public int TopologyBase;
        public bool IsNew(uint v) => v >= NewVertexBase && v < NewVertexBase + (uint)NewVertexCount;
    }

    public sealed class VerificationReport
    {
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public bool Passed => Failures.Count == 0;
        public int SimpleLoops, NonSimpleLoops;
        public void Fail(string s) { if (Failures.Count < 40) Failures.Add(s); }
        public override string ToString() => Passed ? "passed (" + string.Join("; ", Notes) + ")" : string.Join("\n", Failures);
    }

    public sealed class VerifyOptions
    {
        public bool CheckInput = true;
        public double PlaneRelative = 1e-5;
        public double SideRelative = 1e-5;
        public double IntersectionRelative = 1e-5;
        public double AreaRelative = 1e-4;
        public double AttributeAbsolute = 1e-3;
    }

    /// <summary>
    /// Offline verifier of the DESIGN 6 output contract (T-006 / T-083 numerical part), test-side only: every non-empty
    /// output inherits closure, edge / vertex manifoldness and local winding consistency on its logical topology;
    /// existing vertices stay on their side; nodes agree with an independent double reference; attributes are the
    /// one-sided interpolation of the face they came from (no seam mixing); caps are hard-edged, carry the fixed UV
    /// marker and the plane normal signed by their winding; the positive-then-negative placement, the triangle count
    /// identity and (for simple loops) the area identity hold.
    /// </summary>
    public static class MeshCutVerifier
    {
        public static VerificationReport Verify(CutRun run, VerifyOptions o = null)
        {
            o = o ?? new VerifyOptions();
            var r = new VerificationReport();
            CutGeometry input = run.Input;
            float4 plane = run.Plane;
            MeshCutResult res = run.Result;
            if (res.status != MeshCutStatus.Ok) { r.Fail("status " + res.status); return r; }

            LogicalTopology inputTopology = LogicalTopology.Build(input);
            bool closedInput = inputTopology.BoundaryEdges == 0;
            if (o.CheckInput)
                foreach (string s in inputTopology.Validate()) r.Fail("input: " + s);

            ReferenceCut reference = ReferenceCut.Compute(input, plane);
            foreach (string s in reference.Problems) r.Fail("input: " + s);
            double extent = Math.Max(reference.MaxExtent, 1e-30);
            double planeTol = o.PlaneRelative * extent, sideTol = o.SideRelative * extent, interTol = o.IntersectionRelative * extent;

            if (res.crossingTriangles != reference.CrossingTriangles)
                r.Fail("K mismatch: kernel " + res.crossingTriangles + ", reference " + reference.CrossingTriangles);
            if (res.triangleCount != input.TriangleCount) r.Fail("triangle count " + res.triangleCount + " != input " + input.TriangleCount);

            // ---- whole mesh on one side
            if (res.positive.reusesInput != 0 || res.negative.reusesInput != 0)
            {
                bool positive = res.positive.reusesInput != 0;
                if (reference.CrossingTriangles != 0) r.Fail("input reuse reported although the reference finds " + reference.CrossingTriangles + " crossing triangle(s)");
                if (positive && reference.NegativeSurface != 0) r.Fail("positive reuse although the reference has negative triangles");
                if (!positive && reference.PositiveSurface != 0) r.Fail("negative reuse although the reference has positive triangles");
                if (res.newIndexCount != 0 || res.newVertexCount != 0) r.Fail("input reuse wrote new indices/vertices");
                for (int i = 0; i < input.Ranges.Count; i++)
                {
                    var rr = run.OutputRanges[positive ? i : input.Ranges.Count + i];
                    if (rr.indexStart != input.Ranges[i].indexStart || rr.indexCount != input.Ranges[i].indexCount) r.Fail("reused range " + i + " differs from the input range");
                }
                r.Notes.Add("whole mesh " + (positive ? "positive" : "negative"));
                return r;
            }

            // ---- nodes against the reference
            int nodeCount = res.nodeCount;
            if (nodeCount == 0) { run.NodeKeys = run.NodeKeys ?? Array.Empty<long>(); run.NodeParams = run.NodeParams ?? Array.Empty<float>(); }
            if (run.NodeKeys == null || run.NodeKeys.Length < nodeCount) { r.Fail("node correspondence not available"); return r; }
            var nodeIndexOfKey = new Dictionary<long, int>();
            for (int n = 0; n < nodeCount; n++)
            {
                long key = run.NodeKeys[n];
                if (nodeIndexOfKey.ContainsKey(key)) { r.Fail("node " + n + " repeats edge key " + key); continue; }
                nodeIndexOfKey.Add(key, n);
                if (!reference.EdgeParams.TryGetValue(key, out double refParam))
                {
                    r.Fail("node " + n + " lies on topology edge (" + (key >> 32) + "," + (int)(key & 0xFFFFFFFF) + ") that the reference does not consider crossed");
                    continue;
                }
                double3 refPoint = reference.EdgePoint(key, refParam);
                double3 implPoint = reference.EdgePoint(key, run.NodeParams[n]);
                if (math.length(refPoint - implPoint) > interTol) r.Fail("node " + n + " parameter differs from the reference by " + Num(math.length(refPoint - implPoint)));
            }
            foreach (long key in reference.EdgeParams.Keys)
                if (!nodeIndexOfKey.ContainsKey(key)) r.Fail("reference expects topology edge (" + (key >> 32) + "," + (int)(key & 0xFFFFFFFF) + ") to be crossed but no node lies on it");

            // ---- new vertices: kind by structure, topology ids, plane membership, finiteness
            int topoBase = run.TopologyBase;
            int newCount = run.NewVertexCount;
            var usedBySurface = new bool[newCount];
            var usedByCap = new bool[newCount];
            var newTopo = run.NewVertexTopology;
            var nodeSlotVertex = new int[nodeCount];   // one interpolated vertex per node (for its position)
            for (int n = 0; n < nodeCount; n++) nodeSlotVertex[n] = -1;
            int auxSeen = 0;
            for (int i = 0; i < newCount; i++)
            {
                RenderVertex v = input.Vertices[run.NewVertexBase + (uint)i];
                if (!IsFinite(v)) r.Fail("new vertex " + i + " has a non-finite attribute");
                double d = math.dot((double3)plane.xyz, (double3)v.position) + plane.w;
                if (Math.Abs(d) > planeTol) r.Fail("new vertex " + i + " is off the plane by " + Num(Math.Abs(d)));
                int t = newTopo[i] - topoBase;
                if (t < 0) r.Fail("new vertex " + i + " has topology id " + newTopo[i] + " below the new id space");
                else if (t >= nodeCount) auxSeen++;
            }
            if (res.newTopologyVertexCount != topoBase + nodeCount + res.capAuxVertices)
                r.Fail("newTopologyVertexCount " + res.newTopologyVertexCount + " != base + nodes + aux = " + (topoBase + nodeCount + res.capAuxVertices));
            if (auxSeen != res.capAuxVertices) r.Fail("aux vertex count " + auxSeen + " (by topology id) != reported " + res.capAuxVertices);

            // ---- per side
            var sideSegments = new HashSet<long>[2];
            var sideLoops = new List<List<int>>[2];
            int surfaceTotal = 0, capTotal = 0;
            double capAreaSimple = 0.0;
            bool allLoopsSimple = true;
            for (int side = 0; side < 2; side++)
            {
                bool positive = side == 0;
                CutGeometry g = positive ? run.Positive : run.Negative;
                MeshCutSideResult sr = positive ? res.positive : res.negative;
                string label = positive ? "positive" : "negative";
                if (g == null) { r.Fail(label + ": geometry missing"); continue; }
                if (g.TriangleCount * 3 != sr.indexCount) r.Fail(label + ": index count " + sr.indexCount + " != 3 * triangles " + g.TriangleCount);
                if (g.TriangleCount == 0) { r.Fail(label + ": empty side although both signs are present"); continue; }

                // ranges: contiguous within the side's block, caps after every surface triangle
                {
                    uint expectStart = sr.indexStart;
                    int rangeSum = 0;
                    for (int i = 0; i < input.Ranges.Count; i++)
                    {
                        var rr = run.OutputRanges[side * input.Ranges.Count + i];
                        if (rr.indexCount == 0) continue;
                        if (rr.indexStart != expectStart) r.Fail(label + ": range " + i + " starts at " + rr.indexStart + ", expected " + expectStart);
                        expectStart = rr.indexStart + (uint)rr.indexCount;
                        rangeSum += rr.indexCount;
                    }
                    if (rangeSum != sr.indexCount) r.Fail(label + ": ranges sum to " + rangeSum + " != side count " + sr.indexCount);
                }

                LogicalTopology topo = LogicalTopology.Build(g);
                var topoProblems = topo.Validate();
                foreach (string s in topoProblems) r.Fail(label + ": " + s);
                if (topoProblems.Count > 0) r.Fail(label + ": " + DescribeBadEdges(run, g, topo));
                if (closedInput && topo.BoundaryEdges != 0) r.Fail(label + ": input is closed but the output has " + topo.BoundaryEdges + " boundary edge(s)");

                ReferenceCut sideRef = ReferenceCut.Compute(g, plane);
                foreach (string s in sideRef.Problems) r.Fail(label + ": output " + s);

                // triangle kinds and side membership
                int surface = 0, caps = 0;
                var segments = new HashSet<long>();
                var outDeg = new Dictionary<int, int>(); var inDeg = new Dictionary<int, int>();
                var nextOf = new Dictionary<int, int>();
                var capTriangles = new List<(uint, uint, uint)>();
                int lastSurfacePos = -1, firstCapPos = int.MaxValue, pos = 0;
                foreach (var (a, b, c, _) in g.Triangles())
                {
                    bool na = run.IsNew(a), nb = run.IsNew(b), nc = run.IsNew(c);
                    bool cap = na && nb && nc;
                    if (cap)
                    {
                        caps++;
                        capTriangles.Add((a, b, c));
                        usedByCap[a - run.NewVertexBase] = true; usedByCap[b - run.NewVertexBase] = true; usedByCap[c - run.NewVertexBase] = true;
                        firstCapPos = Math.Min(firstCapPos, pos);
                    }
                    else
                    {
                        surface++;
                        lastSurfacePos = Math.Max(lastSurfacePos, pos);
                        foreach (uint v in new[] { a, b, c })
                        {
                            if (run.IsNew(v)) { usedBySurface[v - run.NewVertexBase] = true; continue; }
                            int t = g.TopologyOf(v);
                            if (t < 0) { r.Fail(label + ": existing vertex " + v + " is not in the topology map"); continue; }
                            bool negative = reference.IsNegative(t);
                            if (negative == positive)
                            {
                                double dd = Math.Abs(reference.Distance[t]);
                                if (dd > sideTol) r.Fail(label + ": existing vertex " + v + " (topology " + t + ") is on the wrong side by " + Num(dd));
                                else r.Notes.Add(label + ": vertex " + v + " within " + Num(dd) + " of the plane sits on the other side");
                            }
                        }
                        // cut segments: surface half-edges between two interpolated node vertices
                        Segment(a, b); Segment(b, c); Segment(c, a);
                    }
                    pos++;
                }
                if (caps > 0 && firstCapPos < lastSurfacePos) r.Fail(label + ": a cap triangle precedes a surface triangle in the side's index block");
                if (surface != (positive ? reference.PositiveSurface : reference.NegativeSurface))
                    r.Fail(label + ": surface triangle count " + surface + " != reference " + (positive ? reference.PositiveSurface : reference.NegativeSurface));
                surfaceTotal += surface; capTotal += caps;

                void Segment(uint u, uint w)
                {
                    if (!run.IsNew(u) || !run.IsNew(w)) return;
                    int tu = newTopo[u - run.NewVertexBase] - topoBase, tw = newTopo[w - run.NewVertexBase] - topoBase;
                    if (tu < 0 || tu >= nodeCount || tw < 0 || tw >= nodeCount) { r.Fail(label + ": surface edge between non-node new vertices " + u + "," + w); return; }
                    if (nodeSlotVertex[tu] < 0) nodeSlotVertex[tu] = (int)(u - run.NewVertexBase);
                    if (nodeSlotVertex[tw] < 0) nodeSlotVertex[tw] = (int)(w - run.NewVertexBase);
                    long key = ((long)tu << 32) | (uint)tw;
                    if (!segments.Add(key)) { r.Fail(label + ": cut segment " + tu + "->" + tw + " occurs twice"); return; }
                    outDeg[tu] = outDeg.TryGetValue(tu, out int od) ? od + 1 : 1;
                    inDeg[tw] = inDeg.TryGetValue(tw, out int id) ? id + 1 : 1;
                    nextOf[tu] = tw;
                }
                foreach (var kv in outDeg) if (kv.Value != 1) r.Fail(label + ": node " + kv.Key + " has " + kv.Value + " outgoing cut segments");
                foreach (var kv in inDeg) if (kv.Value != 1) r.Fail(label + ": node " + kv.Key + " has " + kv.Value + " incoming cut segments");
                for (int n = 0; n < nodeCount; n++) if (!outDeg.ContainsKey(n) && !inDeg.ContainsKey(n)) r.Fail(label + ": node " + n + " appears in no cut segment");
                sideSegments[side] = segments;

                // loops from the segments (closed input: every chain closes)
                var loops = new List<List<int>>();
                {
                    var visited = new HashSet<int>();
                    foreach (int start in outDeg.Keys)
                    {
                        if (visited.Contains(start)) continue;
                        var loop = new List<int>();
                        int cur = start;
                        while (!visited.Contains(cur)) { visited.Add(cur); loop.Add(cur); if (!nextOf.TryGetValue(cur, out cur)) break; }
                        bool closed = nextOf.TryGetValue(loop[loop.Count - 1], out int back) && back == start;
                        if (closed) loops.Add(loop); else if (closedInput) r.Fail(label + ": open cut chain of " + loop.Count + " nodes on a closed input");
                    }
                }
                sideLoops[side] = loops;

                // attribute provenance of every surface triangle
                CheckSurfaceAttributes(r, run, g, label, reference, topo, o.AttributeAbsolute);

                // caps: marker, normal, hard edge, winding on simple loops
                CheckCaps(r, run, g, label, plane, capTriangles, loops, nodeSlotVertex, o.AttributeAbsolute, ref capAreaSimple, ref allLoopsSimple);
            }

            for (int i = 0; i < newCount; i++)
            {
                if (usedBySurface[i] && usedByCap[i]) r.Fail("new vertex " + i + " is shared by a surface and a cap triangle (the cap boundary must be a hard edge)");
                if (!usedBySurface[i] && !usedByCap[i]) r.Fail("new vertex " + i + " is referenced by no triangle");
            }

            // ---- the two sides traverse every cut segment in opposite directions
            if (sideSegments[0] != null && sideSegments[1] != null)
            {
                foreach (long s in sideSegments[0])
                {
                    long rev = (s << 32) | (long)((ulong)s >> 32);
                    if (!sideSegments[1].Contains(rev)) r.Fail("positive cut segment " + (s >> 32) + "->" + (int)(s & 0xFFFFFFFF) + " has no reversed counterpart on the negative side");
                }
                foreach (long s in sideSegments[1])
                {
                    long rev = (s << 32) | (long)((ulong)s >> 32);
                    if (!sideSegments[0].Contains(rev)) r.Fail("negative cut segment " + (s >> 32) + "->" + (int)(s & 0xFFFFFFFF) + " has no reversed counterpart on the positive side");
                }
            }

            // ---- identities
            int expectedTriangles = input.TriangleCount + 2 * reference.CrossingTriangles + res.capTriangles;
            if (surfaceTotal + capTotal != expectedTriangles)
                r.Fail("output triangles " + (surfaceTotal + capTotal) + " != T + 2K + caps = " + input.TriangleCount + " + 2*" + reference.CrossingTriangles + " + " + res.capTriangles);
            if (capTotal != res.capTriangles) r.Fail("cap triangles counted " + capTotal + " != reported " + res.capTriangles);
            int loopNodes = 0, cappedLoops = 0, twoCycles = 0;
            for (int side = 0; side < 2; side++)
                if (sideLoops[side] != null)
                    foreach (var loop in sideLoops[side]) { if (loop.Count == 2) twoCycles++; else { cappedLoops++; loopNodes += loop.Count; } }
            if (twoCycles != res.capCyclesClosedBySurface) r.Fail("two-node cycles " + twoCycles + " != reported closed-by-surface " + res.capCyclesClosedBySurface);
            int expectedCaps = loopNodes + 2 * res.capAuxVertices - 2 * cappedLoops;
            if (res.capTriangles != expectedCaps) r.Fail("cap triangles " + res.capTriangles + " do not satisfy the disk relation nodes + 2*aux - 2*loops = " + expectedCaps);
            if (res.newIndexCount != res.positive.indexCount + res.negative.indexCount) r.Fail("newIndexCount != n0 + n1");
            if (res.negative.indexStart != res.positive.indexStart + (uint)res.positive.indexCount) r.Fail("negative block does not start right after the positive block");

            if (allLoopsSimple)
            {
                double inputArea = input.TotalArea();
                double outputArea = run.Positive.TotalArea() + run.Negative.TotalArea();
                double expected = inputArea + capAreaSimple;
                double tol = o.AreaRelative * Math.Max(expected, 1e-30);
                if (Math.Abs(expected - outputArea) > tol)
                    r.Fail("surface area not accounted for: expected " + Num(expected) + " (input " + Num(inputArea) + " + caps " + Num(capAreaSimple) + "), got " + Num(outputArea));
            }
            else r.Notes.Add("non-simple loop(s): area identity skipped");
            r.Notes.Add("K=" + reference.CrossingTriangles + " nodes=" + nodeCount + " loops=" + (sideLoops[0]?.Count ?? 0) + " caps=" + res.capTriangles + " aux=" + res.capAuxVertices + " fan=" + res.capFanFallbacks + " newV=" + newCount);
            return r;
        }

        /// <summary>Diagnostic detail for the first few edges with a face count other than two: every incident face with its render corners, kinds and topology ids.</summary>
        static string DescribeBadEdges(CutRun run, CutGeometry g, LogicalTopology topo)
        {
            var sb = new System.Text.StringBuilder("bad edges: ");
            int reported = 0;
            foreach (var e in topo.Edges)
            {
                if (e.Faces.Count == 2 && e.Directions[0] != e.Directions[1]) continue;
                if (reported++ >= 3) { sb.Append(" ..."); break; }
                sb.Append("[edge (").Append(Vertex(run, e.V0)).Append(",").Append(Vertex(run, e.V1)).Append(") faces:");
                for (int i = 0; i < e.Faces.Count; i++)
                {
                    int f = e.Faces[i];
                    uint[] rc = topo.FaceRender[f];
                    int[] tc = topo.FaceTopology[f];
                    bool cap = run.IsNew(rc[0]) && run.IsNew(rc[1]) && run.IsNew(rc[2]);
                    sb.Append(' ').Append(cap ? "cap" : "surface").Append('(');
                    for (int k = 0; k < 3; k++) sb.Append(k > 0 ? "," : "").Append(rc[k]).Append('/').Append(Vertex(run, tc[k]));
                    sb.Append(')').Append(e.Directions[i] > 0 ? "+" : "-");
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        /// <summary>A topology id with its provenance relative to this run: existing, node n (interpolated), or aux a.</summary>
        static string Vertex(CutRun run, int topology)
        {
            int t = topology - run.TopologyBase;
            if (t < 0) return topology + "e";
            if (t < run.Result.nodeCount) return topology + "n" + t;
            return topology + "a" + (t - run.Result.nodeCount);
        }

        // ---------------------------------------------------------------- attributes

        static void CheckSurfaceAttributes(VerificationReport r, CutRun run, CutGeometry g, string label, ReferenceCut reference, LogicalTopology topo, double tol)
        {
            CutGeometry input = run.Input;
            // input faces by render vertex, for provenance lookup
            var facesByRender = new Dictionary<uint, List<int>>();
            var inputFaces = new List<(uint a, uint b, uint c)>();
            foreach (var (a, b, c, _) in input.Triangles())
            {
                int f = inputFaces.Count;
                inputFaces.Add((a, b, c));
                foreach (uint v in new[] { a, b, c })
                {
                    if (!facesByRender.TryGetValue(v, out var list)) { list = new List<int>(); facesByRender.Add(v, list); }
                    list.Add(f);
                }
            }
            int failures = 0;
            foreach (var (a, b, c, _) in g.Triangles())
            {
                uint[] corners = { a, b, c };
                int newCorners = 0;
                foreach (uint v in corners) if (run.IsNew(v)) newCorners++;
                if (newCorners == 3) continue;   // cap
                if (newCorners == 0)
                {
                    // a copied triangle: must be an input triangle verbatim (rotation allowed)
                    bool found = false;
                    if (facesByRender.TryGetValue(a, out var candidates))
                        foreach (int f in candidates)
                        {
                            var t = inputFaces[f];
                            if (SameCycle(t.a, t.b, t.c, a, b, c)) { found = true; break; }
                        }
                    if (!found && failures++ < 8) r.Fail(label + ": copied triangle (" + a + "," + b + "," + c + ") is not an input triangle");
                    continue;
                }
                // clipped: candidates share every existing render corner and contain both endpoints of every node edge
                uint anchor = 0; bool haveAnchor = false;
                foreach (uint v in corners) if (!run.IsNew(v)) { anchor = v; haveAnchor = true; break; }
                if (!haveAnchor) continue;
                bool matched = false;
                string why = null;
                if (facesByRender.TryGetValue(anchor, out var cands))
                    foreach (int f in cands)
                    {
                        var t = inputFaces[f];
                        uint[] fr = { t.a, t.b, t.c };
                        int[] ft = { input.TopologyOf(t.a), input.TopologyOf(t.b), input.TopologyOf(t.c) };
                        bool ok = true;
                        foreach (uint v in corners)
                        {
                            if (!run.IsNew(v)) { if (Array.IndexOf(fr, v) < 0) { ok = false; break; } continue; }
                            int node = run.NewVertexTopology[v - run.NewVertexBase] - run.TopologyBase;
                            if (node < 0 || node >= run.NodeKeys.Length) { ok = false; break; }
                            long key = run.NodeKeys[node];
                            int lo = (int)(key >> 32), hi = (int)(key & 0xFFFFFFFF);
                            int jLo = Array.IndexOf(ft, lo), jHi = Array.IndexOf(ft, hi);
                            if (jLo < 0 || jHi < 0) { ok = false; break; }
                            double param = reference.EdgeParams.TryGetValue(key, out double p) ? p : run.NodeParams[node];
                            RenderVertex outV = g.Vertices[v];
                            RenderVertex vLo = input.Vertices[fr[jLo]], vHi = input.Vertices[fr[jHi]];
                            if (!InterpolationMatches(vLo, vHi, param, outV, tol, out why)) { ok = false; break; }
                        }
                        if (ok) { matched = true; break; }
                    }
                if (!matched && failures++ < 8)
                    r.Fail(label + ": clipped triangle (" + a + "," + b + "," + c + ") is not the one-sided interpolation of any incident input face" + (why != null ? " (" + why + ")" : ""));
            }
        }

        static bool SameCycle(uint a, uint b, uint c, uint x, uint y, uint z) =>
            (a == x && b == y && c == z) || (a == y && b == z && c == x) || (a == z && b == x && c == y);

        static bool InterpolationMatches(RenderVertex lo, RenderVertex hi, double t, RenderVertex outV, double tol, out string why)
        {
            why = null;
            double u = lo.uv0.x + (hi.uv0.x - lo.uv0.x) * t, v = lo.uv0.y + (hi.uv0.y - lo.uv0.y) * t;
            if (Math.Abs(u - outV.uv0.x) > tol || Math.Abs(v - outV.uv0.y) > tol) { why = "uv"; return false; }
            if (!DirectionMatches(lo.normal, hi.normal, t, outV.normal, tol)) { why = "normal"; return false; }
            if (!DirectionMatches(lo.tangent.xyz, hi.tangent.xyz, t, outV.tangent.xyz, tol)) { why = "tangent"; return false; }
            if (Math.Abs(lo.tangent.w - outV.tangent.w) > tol) { why = "tangent.w"; return false; }
            return true;
        }

        static bool DirectionMatches(float3 a, float3 b, double t, float3 actual, double tol)
        {
            double3 d = (double3)a + ((double3)b - (double3)a) * t;
            double len = math.length(d);
            if (len < 1e-3) return true;   // near-cancelling endpoints: the normalized direction is float noise
            d /= len;
            return math.cmax(math.abs(d - (double3)actual)) <= tol;
        }

        // ---------------------------------------------------------------- caps

        static void CheckCaps(VerificationReport r, CutRun run, CutGeometry g, string label, float4 plane, List<(uint, uint, uint)> capTriangles,
                              List<List<int>> loops, int[] nodeSlotVertex, double tol, ref double capAreaSimple, ref bool allLoopsSimple)
        {
            float3 n = plane.xyz;
            float3 seed = math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            float3 axisU = math.cross(seed, n);
            axisU *= 1f / math.length(axisU);   // the kernel's normalization, bit for bit
            float3 axisV = math.cross(n, axisU);

            double extent = run.Input.MaxExtent();
            // loop simplicity (projected), and the loop of each node
            var loopOfNode = new Dictionary<int, int>();
            var loopSimple = new List<bool>();
            for (int li = 0; li < loops.Count; li++)
            {
                var loop = loops[li];
                foreach (int node in loop) loopOfNode[node] = li;
                var pts = new List<double2>();
                foreach (int node in loop)
                {
                    int sv = nodeSlotVertex[node];
                    float3 p = sv >= 0 ? g.Vertices[run.NewVertexBase + (uint)sv].position : float3.zero;
                    pts.Add(new double2(math.dot(p, axisU), math.dot(p, axisV)));
                }
                // "simple" as the kernel decides it: no proper crossing, no touch and no near contact within its epsilon
                // (a few float ulps of the input extent); only such loops are ear clipped exactly and owe geometric winding
                double eps = MeshCutKernel.NearContactEpsilonRelative * extent;
                bool simple = loop.Count >= 3 && IsSimplePolygon(pts, eps);
                loopSimple.Add(simple);
                if (simple) { r.SimpleLoops++; capAreaSimple += Math.Abs(SignedArea(pts)); }
                else { r.NonSimpleLoops++; if (loop.Count >= 3) allLoopsSimple = false; }
            }

            int failures = 0;
            foreach (var (a, b, c) in capTriangles)
            {
                int expectedSign = 0;
                int loopIndex = -1;
                foreach (uint v in new[] { a, b, c })
                {
                    RenderVertex rv = g.Vertices[v];
                    if (rv.uv0.x != RenderCutMarker.CapUvX || rv.uv0.y != RenderCutMarker.CapUvY)
                    { if (failures++ < 8) r.Fail(label + ": cap vertex " + v + " does not carry the fixed cap UV marker (" + rv.uv0.x + "," + rv.uv0.y + ")"); }
                    double dot = math.dot((double3)rv.normal, (double3)n);
                    if (Math.Abs(Math.Abs(dot) - 1.0) > tol) { if (failures++ < 8) r.Fail(label + ": cap vertex " + v + " normal is not the unit plane normal (|dot| = " + Num(Math.Abs(dot)) + ")"); }
                    int sign = dot > 0 ? 1 : -1;
                    if (expectedSign == 0) expectedSign = sign;
                    else if (sign != expectedSign && failures++ < 8) r.Fail(label + ": cap triangle mixes normal signs");
                    double tl = math.length(rv.tangent.xyz);
                    if (Math.Abs(tl - 1.0) > tol || Math.Abs(math.dot(rv.tangent.xyz, rv.normal)) > 1e-3) { if (failures++ < 8) r.Fail(label + ": cap vertex " + v + " tangent is not a unit vector in the plane"); }
                    int t = run.NewVertexTopology[v - run.NewVertexBase] - run.TopologyBase;
                    if (t >= 0 && t < nodeSlotVertex.Length)
                    {
                        if (loopIndex < 0 && loopOfNode.TryGetValue(t, out int li)) loopIndex = li;
                        // a cap render vertex of a node shares the node's canonical position
                        int sv = nodeSlotVertex[t];
                        if (sv >= 0 && !g.Vertices[run.NewVertexBase + (uint)sv].position.Equals(rv.position) && failures++ < 8)
                            r.Fail(label + ": cap vertex " + v + " of node " + t + " does not share the node's canonical position");
                    }
                }
                if (loopIndex >= 0 && loopSimple[loopIndex])
                {
                    // A simple loop is ear clipped exactly: every cap triangle's orientation in the plane basis (the same
                    // float projection the kernel triangulates in, evaluated in double) must agree with the cap's normal
                    // sign or be exactly zero. The 3D cross product is not used: for slivers it is float noise.
                    double2 pa = Project(g.Vertices[a].position, axisU, axisV), pb = Project(g.Vertices[b].position, axisU, axisV), pc = Project(g.Vertices[c].position, axisU, axisV);
                    double area2 = (pb.x - pa.x) * (pc.y - pa.y) - (pb.y - pa.y) * (pc.x - pa.x);
                    // a sliver (sine of its corner angle at or below 1e-5, the probe's degenerate-ear scale) has no winding to follow
                    double e0 = math.length(pb - pa), e1 = math.length(pc - pa);
                    double sine = e0 > 0 && e1 > 0 ? Math.Abs(area2) / (e0 * e1) : 0.0;
                    // float projection noise on an edge of length e is about 8e-7 * extent * e in area2 (three tiny nodes 2e-4 apart on a torus sit there)
                    double noise = 8e-7 * extent * Math.Max(e0, Math.Max(e1, math.length(pc - pb)));
                    if (sine > 1e-5 && Math.Abs(area2) > noise && area2 * expectedSign < 0 && failures++ < 8)
                        r.Fail(label + ": cap triangle (" + a + "," + b + "," + c + ") render normal sign does not follow its winding (projected area2 " + Num(area2) + ", sin " + Num(sine) + ")");
                }
            }
        }

        /// <summary>The kernel's projection: float dot products with the float plane basis, then double arithmetic on them.</summary>
        static double2 Project(float3 p, float3 axisU, float3 axisV) => new double2(math.dot(p, axisU), math.dot(p, axisV));

        static double SignedArea(List<double2> p)
        {
            double s = 0; int n = p.Count;
            for (int i = 0; i < n; i++) { double2 a = p[i], b = p[(i + 1) % n]; s += a.x * b.y - b.x * a.y; }
            return 0.5 * s;
        }

        static bool IsSimplePolygon(List<double2> p, double eps)
        {
            int n = p.Count;
            double eps2 = eps * eps;
            for (int e = 0; e < n; e++)
                for (int f = e + 1; f < n; f++)
                {
                    int d = f - e;
                    if (d == 1 || d == n - 1) continue;
                    double2 a0 = p[e], a1 = p[(e + 1) % n], b0 = p[f], b1 = p[(f + 1) % n];
                    double d1 = Cross(b0, b1, a0), d2 = Cross(b0, b1, a1), d3 = Cross(a0, a1, b0), d4 = Cross(a0, a1, b1);
                    if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return false;
                    if ((d1 == 0 && OnSeg(b0, b1, a0)) || (d2 == 0 && OnSeg(b0, b1, a1)) || (d3 == 0 && OnSeg(a0, a1, b0)) || (d4 == 0 && OnSeg(a0, a1, b1))) return false;
                    if (eps > 0)
                    {
                        double s2 = Math.Min(Math.Min(PointSegment2(a0, b0, b1), PointSegment2(a1, b0, b1)), Math.Min(PointSegment2(b0, a0, a1), PointSegment2(b1, a0, a1)));
                        if (s2 < eps2) return false;
                    }
                }
            return true;
        }

        static double PointSegment2(double2 p, double2 s0, double2 s1)
        {
            double2 v = s1 - s0, w = p - s0;
            double len2 = math.dot(v, v);
            double t = len2 > 0 ? Math.Max(0.0, Math.Min(1.0, math.dot(w, v) / len2)) : 0.0;
            double2 d = s0 + t * v - p;
            return math.dot(d, d);
        }

        static double Cross(double2 o, double2 a, double2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        static bool OnSeg(double2 s0, double2 s1, double2 p) =>
            p.x >= Math.Min(s0.x, s1.x) && p.x <= Math.Max(s0.x, s1.x) && p.y >= Math.Min(s0.y, s1.y) && p.y <= Math.Max(s0.y, s1.y);

        static bool IsFinite(RenderVertex v) =>
            math.all(math.isfinite(v.position)) && math.all(math.isfinite(v.normal)) && math.all(math.isfinite(v.uv0)) && math.all(math.isfinite(v.tangent));

        static string Num(double d) => d.ToString("G6", CultureInfo.InvariantCulture);
    }
}
