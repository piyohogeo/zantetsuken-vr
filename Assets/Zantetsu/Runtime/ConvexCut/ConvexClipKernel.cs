using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut
{
    public enum CutStatus : int
    {
        Ok = 0,
        NotSplit = 1,
        CutPolygonTooSmall = 2,
        CapacityVertex = 3,
        CapacityFace = 4,
        CapacityIndex = 5,
        CapacityEdge = 6,
        CapacityCut = 7,
        CapacityHash = 8,
        WalkFailed = 9,
        NonManifold = 10,
        CapacityScratch = 11,
    }

    /// <summary>
    /// Scratch of one plane clip (both sides reuse it sequentially). Every array is fully initialised by the
    /// kernel before use, so a reused or garbage-filled block gives the same result as a fresh one.
    /// </summary>
    public unsafe struct CutScratch
    {
        public int* vertexRemap;   // V
        public int* edgeCutIdx;    // E  (-2: not crossing, -1: crossing not yet emitted, >=0 out index)
        public int* edgeAlias;     // E  (-1: none; >=0: the intersection point is bitwise equal to this endpoint)
        public sbyte* clsLocal;    // V  kernel-local copy of the sign classes (rounding-level on-plane vertices become class 0)
        public float3* cutPoint;   // E
        public int* faceScratch;   // maxFaceLoop + 2 (encoded: orig >= 0, crossing edge = -(e+1))
        public int* cutList;       // cutCap
        public long* hashKeys;     // hashCap (pow2)
        public int* hashVals;      // hashCap
        public int* boundaryNext;  // boundaryCap (>= output vertex capacity)
        public int V, E, faceScratchCap, cutCap, hashCap, boundaryCap;

        /// <summary>
        /// Lays the scratch out inside <paramref name="basePtr"/> (null only computes <paramref name="bytes"/>).
        /// The layout is the capacity formula: callers reserve the returned size and never recompute it.
        /// </summary>
        public static CutScratch Layout(byte* basePtr, int V, int E, int maxFaceLoop, int cutCap, int hashCap, int boundaryCap, out int bytes)
        {
            int off = 0;
            T* Take<T>(int count) where T : unmanaged { off = (off + 15) / 16 * 16; var p = basePtr == null ? null : (T*)(basePtr + off); off += math.max(1, count) * sizeof(T); return p; }
            var s = new CutScratch { V = V, E = E, faceScratchCap = maxFaceLoop + 2, cutCap = cutCap, hashCap = hashCap, boundaryCap = boundaryCap };
            s.vertexRemap = Take<int>(V); s.edgeCutIdx = Take<int>(E); s.edgeAlias = Take<int>(E); s.clsLocal = Take<sbyte>(V); s.cutPoint = Take<float3>(E); s.faceScratch = Take<int>(maxFaceLoop + 2);
            s.cutList = Take<int>(cutCap);
            s.hashKeys = Take<long>(hashCap); s.hashVals = Take<int>(hashCap); s.boundaryNext = Take<int>(boundaryCap);
            bytes = off;
            return s;
        }
    }

    public struct CutStats
    {
        public int crossing, onVertices, cutK, posV, posF, posI, posE, negV, negF, negI, negE, hashProbes;
    }

    /// <summary>
    /// Exact plane clip of one convex polygon B-rep into its positive and negative halves (probe candidate
    /// "A-Walk": polygon CSR + persistent undirected edges, cut face by boundary walk).
    ///
    /// DESIGN 7.6: the robust-support epsilon never reaches this kernel. The caller decides Split per convex and
    /// passes the raw signed distance <c>sd</c> and its exact sign <c>cls</c> (+1: d &gt; 0, -1: d &lt; 0, 0: d == 0);
    /// the clip classifies by that sign only. An intersection that rounds bitwise onto an endpoint means the plane
    /// passes through that vertex at float resolution: the vertex is then treated as exactly on the plane (class 0,
    /// kept on both sides). No epsilon, snap, offset or kerf is applied anywhere.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public static unsafe class ConvexClipKernel
    {
        /// <summary>
        /// Clips <paramref name="input"/> by <paramref name="plane"/> (s(x) = dot(plane.xyz, x) + plane.w) into
        /// <paramref name="pos"/> (s &gt; 0 side) and <paramref name="neg"/> (s &lt; 0 side). Returns a
        /// <see cref="CutStatus"/>; on any status other than Ok the outputs are invalid. Never writes beyond the
        /// output capacities or the scratch layout; never modifies the input.
        /// </summary>
        [BurstCompile]
        public static int Cut(in BrepBuffer input, float* sd, sbyte* cls, in float4 plane, ref BrepBuffer pos, ref BrepBuffer neg, ref CutScratch s, ref CutStats stats)
        {
            int V = input.V, E = input.E, F = input.F;
            if (V > s.V || E > s.E) return (int)CutStatus.CapacityScratch;
            bool anyPos = false, anyNeg = false;
            for (int i = 0; i < V; i++) { s.clsLocal[i] = cls[i]; if (cls[i] > 0) anyPos = true; else if (cls[i] < 0) anyNeg = true; }
            if (!(anyPos && anyNeg)) return (int)CutStatus.NotSplit;
            // rounding-level on-plane detection (no epsilon): if the plane meets a crossing edge exactly at an endpoint (the interpolated
            // point rounds to the endpoint), the plane passes through that vertex at float resolution -> exact class 0 on both sides.
            // Two passes: mark, then recompute crossings with the updated classes (marking only removes crossings, so no cascade).
            for (int e = 0; e < E; e++)
            {
                int a = input.edges[e].v0, b = input.edges[e].v1;
                if (cls[a] * cls[b] < 0)
                {
                    float sa = sd[a], sb = sd[b];
                    float t = sa / (sa - sb);
                    float3 x = input.v[a] + (input.v[b] - input.v[a]) * t;
                    if (math.all(x == input.v[a])) s.clsLocal[a] = 0; else if (math.all(x == input.v[b])) s.clsLocal[b] = 0;
                }
            }
            cls = s.clsLocal;
            int on = 0;
            anyPos = false; anyNeg = false;
            for (int i = 0; i < V; i++) { if (cls[i] > 0) anyPos = true; else if (cls[i] < 0) anyNeg = true; else on++; }
            stats.onVertices = on;
            if (!(anyPos && anyNeg)) return (int)CutStatus.NotSplit; // a robust Split has both strict sides (|d| > eps vertices never round onto the plane)

            // canonical intersection per undirected edge
            int crossing = 0;
            for (int e = 0; e < E; e++)
            {
                int a = input.edges[e].v0, b = input.edges[e].v1;
                if (cls[a] * cls[b] < 0)
                {
                    float sa = sd[a], sb = sd[b];
                    float t = sa / (sa - sb);
                    float3 x = input.v[a] + (input.v[b] - input.v[a]) * t;
                    s.cutPoint[e] = x;
                    s.edgeCutIdx[e] = -1;
                    // exact (bitwise) coincidence with an endpoint: the plane passes through that vertex at float resolution
                    s.edgeAlias[e] = math.all(x == input.v[a]) ? a : math.all(x == input.v[b]) ? b : -1;
                    crossing++;
                }
                else { s.edgeCutIdx[e] = -2; s.edgeAlias[e] = -1; }
            }
            stats.crossing = crossing;

            int r = BuildSide(in input, cls, +1, ref pos, ref s, ref stats);
            if (r != 0) return r;
            stats.posV = pos.V; stats.posF = pos.F; stats.posI = pos.I; stats.posE = pos.E;
            r = BuildSide(in input, cls, -1, ref neg, ref s, ref stats);
            if (r != 0) return r;
            stats.negV = neg.V; stats.negF = neg.F; stats.negI = neg.I; stats.negE = neg.E;
            return (int)CutStatus.Ok;
        }

        static int HashFind(long* keys, int cap, long key, ref int probes)
        {
            int mask = cap - 1;
            int h = (int)((key ^ (key >> 29)) * 0x9E3779B1L) & mask;
            while (true)
            {
                probes++;
                long k = keys[h];
                if (k == key || k < 0) return h;
                h = (h + 1) & mask;
            }
        }

        static int BuildSide(in BrepBuffer input, sbyte* cls, int side, ref BrepBuffer o, ref CutScratch s, ref CutStats stats)
        {
            int V = input.V, F = input.F, E = input.E;
            for (int i = 0; i < V; i++) s.vertexRemap[i] = -1;
            for (int e = 0; e < E; e++) if (s.edgeCutIdx[e] != -2) s.edgeCutIdx[e] = -1;
            o.V = 0; o.F = 0; o.I = 0; o.E = 0;
            int cutCount = 0;
            // boundaryNext doubles as the "already in the cut list" marker (-2); the walk overwrites it with successors (>= 0)
            for (int i = 0; i < s.boundaryCap; i++) s.boundaryNext[i] = -1;

            for (int f = 0; f < F; f++)
            {
                int start = input.faceOff[f], L = input.faceOff[f + 1] - start;
                if (L + 2 > s.faceScratchCap) return (int)CutStatus.CapacityScratch;
                int n = 0;
                bool allBoundary = true;
                for (int k = 0; k < L; k++)
                {
                    int a = input.faceIdx[start + k], b = input.faceIdx[start + (k + 1) % L];
                    int ca = cls[a] * side;
                    if (ca >= 0) { s.faceScratch[n++] = a; if (ca > 0) allBoundary = false; }
                    if (cls[a] * cls[b] < 0) s.faceScratch[n++] = -(input.faceEdge[start + k] + 1);
                }
                if (n < 3 || allBoundary) continue;
                if (o.F + 1 > o.fCap) return (int)CutStatus.CapacityFace;
                if (o.I + n > o.iCap) return (int)CutStatus.CapacityIndex;
                o.faceOff[o.F] = o.I;
                int faceBase = o.I;
                int prevOut = -1, prevBoundary = 0, firstOut = -1, firstBoundary = 0;
                for (int k = 0; k < n; k++)
                {
                    int code = s.faceScratch[k];
                    int outIdx;
                    bool boundary;
                    // an intersection bitwise equal to endpoint u IS u on both sides (u lies on the plane at float resolution):
                    // every crossing edge of u resolves to the same output vertex, so no coincident duplicates arise
                    bool aliased = code < 0 && s.edgeAlias[-code - 1] >= 0;
                    if (aliased) code = s.edgeAlias[-code - 1];
                    if (code >= 0)
                    {
                        boundary = cls[code] == 0 || aliased;
                        outIdx = s.vertexRemap[code];
                        if (outIdx < 0)
                        {
                            if (o.V + 1 > o.vCap) return (int)CutStatus.CapacityVertex;
                            outIdx = o.V++;
                            o.v[outIdx] = input.v[code];
                            s.vertexRemap[code] = outIdx;
                        }
                    }
                    else
                    {
                        int e = -code - 1;
                        boundary = true;
                        outIdx = s.edgeCutIdx[e];
                        if (outIdx < 0)
                        {
                            if (o.V + 1 > o.vCap) return (int)CutStatus.CapacityVertex;
                            outIdx = o.V++;
                            o.v[outIdx] = s.cutPoint[e];
                            s.edgeCutIdx[e] = outIdx;
                        }
                    }
                    if (outIdx >= s.boundaryCap) return (int)CutStatus.CapacityScratch;
                    if (boundary && s.boundaryNext[outIdx] == -1)
                    {
                        if (cutCount >= s.cutCap) return (int)CutStatus.CapacityCut;
                        s.cutList[cutCount++] = outIdx;
                        s.boundaryNext[outIdx] = -2;
                    }
                    // collapse consecutive duplicates (an aliased intersection next to its own endpoint)
                    if (o.I > faceBase && o.faceIdx[o.I - 1] == outIdx) { prevBoundary |= boundary ? 1 : 0; continue; }
                    o.faceIdx[o.I++] = outIdx;
                    if (k == 0) { firstOut = outIdx; firstBoundary = boundary ? 1 : 0; }
                    else if (prevBoundary != 0 && boundary && prevOut != outIdx)
                    {
                        if (s.boundaryNext[prevOut] >= 0 && s.boundaryNext[prevOut] != outIdx) return (int)CutStatus.WalkFailed;
                        s.boundaryNext[prevOut] = outIdx;
                    }
                    prevOut = outIdx; prevBoundary = boundary ? 1 : 0;
                }
                // closing duplicate (last == first) and degenerate loops
                if (o.I - faceBase >= 2 && o.faceIdx[o.I - 1] == o.faceIdx[faceBase]) o.I--;
                if (o.I - faceBase < 3) { o.I = faceBase; continue; }
                if (prevBoundary != 0 && firstBoundary != 0 && prevOut != firstOut)
                {
                    if (s.boundaryNext[prevOut] >= 0 && s.boundaryNext[prevOut] != firstOut) return (int)CutStatus.WalkFailed;
                    s.boundaryNext[prevOut] = firstOut;
                }
                o.F++;
            }
            if (cutCount < 3) return (int)CutStatus.CutPolygonTooSmall;
            stats.cutK = cutCount;

            // cut face: walk the boundary chain (kept-face direction), then reverse for the cut face
            if (o.F + 1 > o.fCap) return (int)CutStatus.CapacityFace;
            if (o.I + cutCount > o.iCap) return (int)CutStatus.CapacityIndex;
            o.faceOff[o.F] = o.I;
            {
                int startV = s.cutList[0];
                int cur = startV, count = 0;
                int baseI = o.I;
                do
                {
                    if (count >= cutCount) return (int)CutStatus.WalkFailed;
                    o.faceIdx[baseI + count] = cur;
                    count++;
                    cur = s.boundaryNext[cur];
                    if (cur < 0) return (int)CutStatus.WalkFailed;
                } while (cur != startV);
                if (count != cutCount) return (int)CutStatus.WalkFailed;
                for (int i = 0, j = count - 1; i < j; i++, j--) { int t = o.faceIdx[baseI + i]; o.faceIdx[baseI + i] = o.faceIdx[baseI + j]; o.faceIdx[baseI + j] = t; }
                o.I += count;
            }
            o.F++;
            o.faceOff[o.F] = o.I;

            // output edge table (+ faceEdge) by hashing the directed edges of every output face
            return BuildEdgeTable(ref o, s.hashKeys, s.hashVals, s.hashCap, ref stats.hashProbes);
        }

        /// <summary>
        /// Builds <c>edges[]</c> (v0 &lt; v1, f0 traverses v0->v1, f1 the reverse) and <c>faceEdge[]</c> for a CSR face
        /// set. Requires every edge in exactly two faces with opposite directions and Euler V - E + F == 2; otherwise
        /// NonManifold. Shared by the clip and reduction kernels.
        /// </summary>
        public static int BuildEdgeTable(ref BrepBuffer o, long* hashKeys, int* hashVals, int hashCap, ref int probes)
        {
            if (hashCap < 16 || (hashCap & (hashCap - 1)) != 0) return (int)CutStatus.CapacityHash;
            for (int i = 0; i < hashCap; i++) hashKeys[i] = -1;
            o.E = 0;
            for (int f = 0; f < o.F; f++)
            {
                int start = o.faceOff[f], L = o.faceOff[f + 1] - start;
                for (int k = 0; k < L; k++)
                {
                    int a = o.faceIdx[start + k], b = o.faceIdx[start + (k + 1) % L];
                    if (a == b) return (int)CutStatus.NonManifold;
                    int lo = math.min(a, b), hi = math.max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    int slot = HashFind(hashKeys, hashCap, key, ref probes);
                    int ei;
                    if (hashKeys[slot] < 0)
                    {
                        if (o.E + 1 > o.eCap) return (int)CutStatus.CapacityEdge;
                        if (o.E + 1 >= hashCap) return (int)CutStatus.CapacityHash;   // keep the open-addressing table non-full
                        ei = o.E++;
                        hashKeys[slot] = key; hashVals[slot] = ei;
                        o.edges[ei] = new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 };
                    }
                    else ei = hashVals[slot];
                    if (a == lo) { if (o.edges[ei].f0 >= 0) return (int)CutStatus.NonManifold; o.edges[ei].f0 = f; }
                    else { if (o.edges[ei].f1 >= 0) return (int)CutStatus.NonManifold; o.edges[ei].f1 = f; }
                    o.faceEdge[start + k] = ei;
                }
            }
            for (int e = 0; e < o.E; e++) if (o.edges[e].f0 < 0 || o.edges[e].f1 < 0) return (int)CutStatus.NonManifold;
            if (o.V - o.E + o.F != 2) return (int)CutStatus.NonManifold;
            return 0;
        }
    }
}
