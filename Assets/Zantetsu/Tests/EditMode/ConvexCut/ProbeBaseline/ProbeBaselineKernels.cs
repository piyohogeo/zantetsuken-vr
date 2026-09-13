// Test-only performance baseline: the probe kernels (zantetsuken-convex-cut-cook-probe, 2026-09-13) copied verbatim except for
// the namespace and the shared BrepBuffer / BrepEdge / MassProperties types. They exist so that the ported kernels can be
// compared against the adopted probe implementation inside one Burst run (same input, same process); they are not a product
// backend and are not maintained beyond that comparison.
using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests.ProbeBaseline
{
    public enum CutFaceMode : int { AngleSort = 0, Walk = 1 }

    public unsafe struct BaselineCutScratch
    {
        public int* vertexRemap; public int* edgeCutIdx; public int* edgeAlias; public sbyte* clsLocal; public float3* cutPoint; public int* faceScratch;
        public int* cutList; public float* cutAngle; public int* cutOrder; public long* hashKeys; public int* hashVals; public int* boundaryNext;
        public int V, E, faceScratchCap, cutCap, hashCap, boundaryCap;
        public long* tHashKeys; public int* tHashVals; public int tHashCap;

        /// OwnerCutPipeline.LayoutScratch of the probe.
        public static BaselineCutScratch Layout(byte* basePtr, int V, int E, int lmax, int cutCap, int hashCap, int boundaryCap, out int bytes)
        {
            int off = 0;
            T* Take<T>(int count) where T : unmanaged { off = (off + 15) / 16 * 16; var p = basePtr == null ? null : (T*)(basePtr + off); off += count * sizeof(T); return p; }
            var s = new BaselineCutScratch { V = V, E = E, faceScratchCap = lmax + 2, cutCap = cutCap, hashCap = hashCap, boundaryCap = boundaryCap };
            s.vertexRemap = Take<int>(V); s.edgeCutIdx = Take<int>(E); s.edgeAlias = Take<int>(E); s.clsLocal = Take<sbyte>(V); s.cutPoint = Take<float3>(E); s.faceScratch = Take<int>(lmax + 2);
            s.cutList = Take<int>(cutCap); s.cutAngle = Take<float>(cutCap); s.cutOrder = Take<int>(cutCap);
            s.hashKeys = Take<long>(hashCap); s.hashVals = Take<int>(hashCap); s.boundaryNext = Take<int>(boundaryCap);
            s.tHashCap = 0; s.tHashKeys = null; s.tHashVals = null;
            bytes = off;
            return s;
        }
    }

    [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public static unsafe class BaselinePolygonEdgeCutKernel
    {
        [BurstCompile]
        public static int Cut(in BrepBuffer input, float* sd, sbyte* cls, in float4 plane, int mode, ref BrepBuffer pos, ref BrepBuffer neg, ref BaselineCutScratch s, ref CutStats stats)
        {
            int V = input.V, E = input.E, F = input.F;
            bool hasEdges = input.faceEdge != null && input.edges != null;
            bool anyPos = false, anyNeg = false;
            for (int i = 0; i < V; i++) { s.clsLocal[i] = cls[i]; if (cls[i] > 0) anyPos = true; else if (cls[i] < 0) anyNeg = true; }
            if (!(anyPos && anyNeg)) return (int)CutStatus.NotSplit;
            if (hasEdges)
            {
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
            }
            else
            {
                for (int f = 0; f < F; f++)
                {
                    int start = input.faceOff[f], L = input.faceOff[f + 1] - start;
                    for (int k = 0; k < L; k++)
                    {
                        int a = input.faceIdx[start + k], b = input.faceIdx[start + (k + 1) % L];
                        if (cls[a] * cls[b] < 0)
                        {
                            float sa = sd[a], sb = sd[b];
                            float t = sa / (sa - sb);
                            float3 x = input.v[a] + (input.v[b] - input.v[a]) * t;
                            if (math.all(x == input.v[a])) s.clsLocal[a] = 0; else if (math.all(x == input.v[b])) s.clsLocal[b] = 0;
                        }
                    }
                }
            }
            cls = s.clsLocal;
            int on = 0;
            anyPos = false; anyNeg = false;
            for (int i = 0; i < V; i++) { if (cls[i] > 0) anyPos = true; else if (cls[i] < 0) anyNeg = true; else on++; }
            stats.onVertices = on;
            if (!(anyPos && anyNeg)) return (int)CutStatus.NotSplit;

            int crossing = 0;
            if (hasEdges)
            {
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
                        s.edgeAlias[e] = math.all(x == input.v[a]) ? a : math.all(x == input.v[b]) ? b : -1;
                        crossing++;
                    }
                    else { s.edgeCutIdx[e] = -2; s.edgeAlias[e] = -1; }
                }
            }
            else
            {
                if (s.tHashCap < 16) return (int)CutStatus.CapacityHash;
                for (int i = 0; i < s.tHashCap; i++) s.tHashKeys[i] = -1;
                int eCount = 0;
                for (int f = 0; f < F; f++)
                {
                    int start = input.faceOff[f], L = input.faceOff[f + 1] - start;
                    for (int k = 0; k < L; k++)
                    {
                        int a = input.faceIdx[start + k], b = input.faceIdx[start + (k + 1) % L];
                        int lo = math.min(a, b), hi = math.max(a, b);
                        long key = ((long)lo << 32) | (uint)hi;
                        int slot = HashFind(s.tHashKeys, s.tHashCap, key, ref stats.hashProbes);
                        if (s.tHashKeys[slot] < 0)
                        {
                            if (eCount >= s.E) return (int)CutStatus.CapacityEdge;
                            s.tHashKeys[slot] = key; s.tHashVals[slot] = eCount;
                            if (cls[lo] * cls[hi] < 0)
                            {
                                float sa = sd[lo], sb = sd[hi];
                                float t = sa / (sa - sb);
                                float3 x = input.v[lo] + (input.v[hi] - input.v[lo]) * t;
                                s.cutPoint[eCount] = x;
                                s.edgeCutIdx[eCount] = -1;
                                s.edgeAlias[eCount] = math.all(x == input.v[lo]) ? lo : math.all(x == input.v[hi]) ? hi : -1;
                                crossing++;
                            }
                            else { s.edgeCutIdx[eCount] = -2; s.edgeAlias[eCount] = -1; }
                            eCount++;
                        }
                    }
                }
                E = eCount;
            }
            stats.crossing = crossing;

            int r = BuildSide(in input, sd, cls, in plane, mode, +1, ref pos, ref s, E, ref stats);
            if (r != 0) return r;
            stats.posV = pos.V; stats.posF = pos.F; stats.posI = pos.I; stats.posE = pos.E;
            r = BuildSide(in input, sd, cls, in plane, mode, -1, ref neg, ref s, E, ref stats);
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

        static int EdgeIndexOfSlot(in BrepBuffer input, int f, int k, int L, ref BaselineCutScratch s, ref CutStats stats)
        {
            int start = input.faceOff[f];
            if (input.faceEdge != null && input.edges != null) return input.faceEdge[start + k];
            int a = input.faceIdx[start + k], b = input.faceIdx[start + (k + 1) % L];
            long key = ((long)math.min(a, b) << 32) | (uint)math.max(a, b);
            return s.tHashVals[HashFind(s.tHashKeys, s.tHashCap, key, ref stats.hashProbes)];
        }

        static int BuildSide(in BrepBuffer input, float* sd, sbyte* cls, in float4 plane, int mode, int side, ref BrepBuffer o, ref BaselineCutScratch s, int E, ref CutStats stats)
        {
            int V = input.V, F = input.F;
            for (int i = 0; i < V; i++) s.vertexRemap[i] = -1;
            for (int e = 0; e < E; e++) if (s.edgeCutIdx[e] != -2) s.edgeCutIdx[e] = -1;
            o.V = 0; o.F = 0; o.I = 0; o.E = 0;
            int cutCount = 0;
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
                    if (cls[a] * cls[b] < 0) s.faceScratch[n++] = -(EdgeIndexOfSlot(in input, f, k, L, ref s, ref stats) + 1);
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
                    if (boundary && s.boundaryNext[outIdx] == -1)
                    {
                        if (cutCount >= s.cutCap) return (int)CutStatus.CapacityCut;
                        s.cutList[cutCount++] = outIdx;
                        s.boundaryNext[outIdx] = -2;
                    }
                    if (o.I > faceBase && o.faceIdx[o.I - 1] == outIdx) { prevBoundary |= boundary ? 1 : 0; continue; }
                    o.faceIdx[o.I++] = outIdx;
                    if (mode == (int)CutFaceMode.Walk)
                    {
                        if (k == 0) { firstOut = outIdx; firstBoundary = boundary ? 1 : 0; }
                        else if (prevBoundary != 0 && boundary && prevOut != outIdx)
                        {
                            if (prevOut >= s.boundaryCap) return (int)CutStatus.CapacityScratch;
                            if (s.boundaryNext[prevOut] >= 0 && s.boundaryNext[prevOut] != outIdx) return (int)CutStatus.WalkFailed;
                            s.boundaryNext[prevOut] = outIdx;
                        }
                        prevOut = outIdx; prevBoundary = boundary ? 1 : 0;
                    }
                }
                if (o.I - faceBase >= 2 && o.faceIdx[o.I - 1] == o.faceIdx[faceBase]) o.I--;
                if (o.I - faceBase < 3) { o.I = faceBase; continue; }
                if (mode == (int)CutFaceMode.Walk && prevBoundary != 0 && firstBoundary != 0 && prevOut != firstOut)
                {
                    if (s.boundaryNext[prevOut] >= 0 && s.boundaryNext[prevOut] != firstOut) return (int)CutStatus.WalkFailed;
                    s.boundaryNext[prevOut] = firstOut;
                }
                o.F++;
            }
            if (cutCount < 3) return (int)CutStatus.CutPolygonTooSmall;
            stats.cutK = cutCount;

            if (o.F + 1 > o.fCap) return (int)CutStatus.CapacityFace;
            if (o.I + cutCount > o.iCap) return (int)CutStatus.CapacityIndex;
            o.faceOff[o.F] = o.I;
            if (mode == (int)CutFaceMode.AngleSort)
            {
                float3 nrm = plane.xyz;
                float3 u = math.abs(nrm.x) < 0.9f ? math.normalize(math.cross(nrm, new float3(1, 0, 0))) : math.normalize(math.cross(nrm, new float3(0, 1, 0)));
                float3 w = math.cross(nrm, u);
                float3 c = 0;
                for (int i = 0; i < cutCount; i++) c += o.v[s.cutList[i]];
                c /= cutCount;
                for (int i = 0; i < cutCount; i++)
                {
                    float3 d = o.v[s.cutList[i]] - c;
                    s.cutAngle[i] = math.atan2(math.dot(d, w), math.dot(d, u));
                    s.cutOrder[i] = i;
                }
                SortByKey(s.cutOrder, s.cutAngle, cutCount);
                if (side > 0) for (int i = cutCount - 1; i >= 0; i--) o.faceIdx[o.I++] = s.cutList[s.cutOrder[i]];
                else for (int i = 0; i < cutCount; i++) o.faceIdx[o.I++] = s.cutList[s.cutOrder[i]];
            }
            else
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

            if (o.faceEdge != null && o.edges != null)
            {
                int r = BuildEdgeTable(ref o, s.hashKeys, s.hashVals, s.hashCap, ref stats.hashProbes);
                if (r != 0) return r;
            }
            return 0;
        }

        public static int BuildEdgeTable(ref BrepBuffer o, long* hashKeys, int* hashVals, int hashCap, ref int probes)
        {
            if (hashCap < 16) return (int)CutStatus.CapacityHash;
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

        static void SortByKey(int* order, float* key, int n)
        {
            if (n <= 24)
            {
                for (int i = 1; i < n; i++)
                {
                    int o = order[i]; float k = key[o]; int j = i - 1;
                    while (j >= 0 && key[order[j]] > k) { order[j + 1] = order[j]; j--; }
                    order[j + 1] = o;
                }
                return;
            }
            for (int i = n / 2 - 1; i >= 0; i--) SiftDown(order, key, i, n);
            for (int end = n - 1; end > 0; end--)
            {
                int t = order[0]; order[0] = order[end]; order[end] = t;
                SiftDown(order, key, 0, end);
            }
        }

        static void SiftDown(int* order, float* key, int root, int n)
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= n) return;
                if (child + 1 < n && key[order[child + 1]] > key[order[child]]) child++;
                if (key[order[child]] <= key[order[root]]) return;
                int t = order[root]; order[root] = order[child]; order[child] = t;
                root = child;
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class BaselineMassPropertiesKernel
    {
        [BurstCompile]
        public static void ComputeDouble(in BrepBuffer b, ref MassProperties m)
        {
            double3 r = 0;
            for (int i = 0; i < b.V; i++) r += b.v[i];
            r /= b.V;
            double vol = 0; double3 fm = 0;
            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            for (int f = 0; f < b.F; f++)
            {
                int start = b.faceOff[f], L = b.faceOff[f + 1] - start;
                double3 a = b.v[b.faceIdx[start]];
                for (int k = 1; k + 1 < L; k++)
                {
                    double3 p = b.v[b.faceIdx[start + k]], q = b.v[b.faceIdx[start + k + 1]];
                    double det = math.dot(a - r, math.cross(p - r, q - r));
                    double tv = det / 6.0;
                    double3 S = r + a + p + q;
                    vol += tv;
                    fm += tv * (S * 0.25);
                    double k120 = det / 120.0;
                    xx += k120 * (r.x * r.x + a.x * a.x + p.x * p.x + q.x * q.x + S.x * S.x);
                    yy += k120 * (r.y * r.y + a.y * a.y + p.y * p.y + q.y * q.y + S.y * S.y);
                    zz += k120 * (r.z * r.z + a.z * a.z + p.z * p.z + q.z * q.z + S.z * S.z);
                    xy += k120 * (r.x * r.y + a.x * a.y + p.x * p.y + q.x * q.y + S.x * S.y);
                    xz += k120 * (r.x * r.z + a.x * a.z + p.x * p.z + q.x * q.z + S.x * S.z);
                    yz += k120 * (r.y * r.z + a.y * a.z + p.y * p.z + q.y * q.z + S.y * S.z);
                }
            }
            m.volume = vol; m.firstMoment = fm;
            m.secondMoment = new SymmetricMatrix3 { xx = xx, yy = yy, zz = zz, xy = xy, xz = xz, yz = yz };
        }
    }

    public unsafe struct BaselineReductionScratch
    {
        public int V, Fcap, Icap, Ncap, Hcap, HullFcap;
        public double3* pos; public byte* alive; public byte* dirty; public double* score; public int* head;
        public int* faceStart; public int* faceCount; public byte* faceAlive; public int* loop; public int faceTotal, loopCursor;
        public int* nodeFace; public int* nodeNext; public int nodeFree, nodeUsed;
        public int* link; public int* nextOf; public int* stamp; public int* starFaces; public int* starK; public int* patchA; public int* patchB; public int* patchC;
        public int faceFree, triFree;
        public double3* hp; public int* hfA; public int* hfB; public int* hfC; public double3* hfN; public double* hfD; public byte* hfDead; public byte* hfVisible;
        public long* hkeys; public int* hvals;
        public long* ekeys; public int* evals; public int ecap;

        public static BaselineReductionScratch Layout(byte* basePtr, int V, int E, int F, int I, out int bytes)
        {
            int off = 0;
            T* Take<T>(int count) where T : unmanaged { off = (off + 15) / 16 * 16; var p = basePtr == null ? null : (T*)(basePtr + off); off += math.max(1, count) * sizeof(T); return p; }
            var s = new BaselineReductionScratch { V = V };
            int newFacesMax = 2 * V + 8;
            s.Fcap = F + newFacesMax;
            s.Icap = I + 3 * newFacesMax + V + 4;
            s.Ncap = s.Icap;
            s.HullFcap = 2 * V + 8;
            s.Hcap = math.ceilpow2(math.max(64, 4 * 3 * s.HullFcap));
            s.ecap = math.ceilpow2(math.max(64, 4 * (V + F)));
            s.pos = Take<double3>(V); s.alive = Take<byte>(V); s.dirty = Take<byte>(V); s.score = Take<double>(V); s.head = Take<int>(V);
            s.faceStart = Take<int>(s.Fcap); s.faceCount = Take<int>(s.Fcap); s.faceAlive = Take<byte>(s.Fcap); s.loop = Take<int>(s.Icap);
            s.nodeFace = Take<int>(s.Ncap); s.nodeNext = Take<int>(s.Ncap);
            s.link = Take<int>(V); s.nextOf = Take<int>(V); s.stamp = Take<int>(V); s.starFaces = Take<int>(V + 4); s.starK = Take<int>(V + 4);
            s.patchA = Take<int>(s.HullFcap); s.patchB = Take<int>(s.HullFcap); s.patchC = Take<int>(s.HullFcap);
            s.hp = Take<double3>(V); s.hfA = Take<int>(s.HullFcap); s.hfB = Take<int>(s.HullFcap); s.hfC = Take<int>(s.HullFcap);
            s.hfN = Take<double3>(s.HullFcap); s.hfD = Take<double>(s.HullFcap); s.hfDead = Take<byte>(s.HullFcap); s.hfVisible = Take<byte>(s.HullFcap);
            s.hkeys = Take<long>(s.Hcap); s.hvals = Take<int>(s.Hcap);
            s.ekeys = Take<long>(s.ecap); s.evals = Take<int>(s.ecap);
            bytes = off;
            return s;
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class BaselineReductionKernel
    {
        const double k_relTol = 1e-6;
        const int k_r1SmallRing = 12;

        [BurstCompile]
        public static int Reduce(ref BrepBuffer io, int L, ref BaselineReductionScratch s, ref ReductionStats stats) => ReduceUpTo(ref io, L, ref s, ref stats, 2);

        [BurstCompile]
        public static int ReduceUpTo(ref BrepBuffer io, int L, ref BaselineReductionScratch s, ref ReductionStats stats, int maxMode)
        {
            stats.inputV = io.V;
            if (io.V <= L) { stats.outputV = io.V; return (int)ReductionStatus.NotNeeded; }
            if (io.V > s.V || io.F > s.Fcap || io.I > s.Icap) return (int)ReductionStatus.CapacityScratch;
            Init(ref io, ref s, out double tol);
            int alive = io.V;
            int mode = 0;
            for (int i = 0; i < io.V; i++) { s.dirty[i] = 1; s.score[i] = double.PositiveInfinity; }
            while (alive > L)
            {
                int best = SelectBest(ref s, mode, tol, ref stats);
                if (best < 0)
                {
                    if (mode == 0 && maxMode > 0)
                    {
                        mode = 1; stats.r1Invoked = 1;
                        for (int i = 0; i < s.V; i++) if (s.alive[i] != 0) { s.dirty[i] = 1; }
                        continue;
                    }
                    if (mode == 1 && maxMode > 1)
                    {
                        mode = 2;
                        for (int i = 0; i < s.V; i++) if (s.alive[i] != 0) { s.dirty[i] = 1; }
                        continue;
                    }
                    stats.outputV = alive;
                    return (int)ReductionStatus.ReductionFailed;
                }
                int r = Apply(ref s, best, tol, ref stats);
                if (r != 0) return r;
                alive--;
                if (mode == 0) stats.removedR0++; else stats.removedR1++;
            }
            stats.outputV = alive;
            return Compact(ref io, ref s, alive);
        }

        static void Init(ref BrepBuffer io, ref BaselineReductionScratch s, out double tol)
        {
            double3 mn = new double3(double.MaxValue), mx = new double3(double.MinValue);
            for (int i = 0; i < io.V; i++) { s.pos[i] = io.v[i]; s.alive[i] = 1; s.head[i] = -1; mn = math.min(mn, s.pos[i]); mx = math.max(mx, s.pos[i]); }
            for (int i = io.V; i < s.V; i++) { s.alive[i] = 0; s.dirty[i] = 0; s.score[i] = double.PositiveInfinity; s.head[i] = -1; }
            tol = k_relTol * math.cmax(mx - mn);
            for (int i = 0; i < s.Ncap; i++) s.nodeNext[i] = i + 1;
            s.nodeNext[s.Ncap - 1] = -1; s.nodeFree = 0; s.nodeUsed = 0;
            for (int f = 0; f < io.F; f++)
            {
                int start = io.faceOff[f], cnt = io.faceOff[f + 1] - start;
                s.faceStart[f] = start; s.faceCount[f] = cnt; s.faceAlive[f] = 1;
                for (int k = 0; k < cnt; k++) { s.loop[start + k] = io.faceIdx[start + k]; AddIncidence(ref s, io.faceIdx[start + k], f); }
            }
            s.faceTotal = io.F; s.loopCursor = io.I;
            s.faceFree = -1; s.triFree = -1;
        }

        static int AllocFace(ref BaselineReductionScratch s)
        {
            if (s.faceFree >= 0) { int f = s.faceFree; s.faceFree = s.faceStart[f]; return f; }
            if (s.faceTotal >= s.Fcap) return -1;
            return s.faceTotal++;
        }

        static void FreeFace(ref BaselineReductionScratch s, int f)
        {
            s.faceAlive[f] = 0; s.faceCount[f] = 0;
            s.faceStart[f] = s.faceFree; s.faceFree = f;
        }

        static int AllocTriangle(ref BaselineReductionScratch s)
        {
            if (s.triFree >= 0) { int t = s.triFree; s.triFree = s.loop[t]; return t; }
            if (s.loopCursor + 3 > s.Icap) return -1;
            int start = s.loopCursor; s.loopCursor += 3; return start;
        }

        static void FreeLoopBlock(ref BaselineReductionScratch s, int start, int cnt)
        {
            if (cnt != 3) return;
            s.loop[start] = s.triFree; s.triFree = start;
        }

        static bool AddIncidence(ref BaselineReductionScratch s, int v, int f)
        {
            int n = s.nodeFree;
            if (n < 0) return false;
            s.nodeFree = s.nodeNext[n];
            s.nodeFace[n] = f; s.nodeNext[n] = s.head[v]; s.head[v] = n; s.nodeUsed++;
            return true;
        }

        static void RemoveIncidence(ref BaselineReductionScratch s, int v, int f)
        {
            int prev = -1, n = s.head[v];
            while (n >= 0)
            {
                if (s.nodeFace[n] == f)
                {
                    if (prev < 0) s.head[v] = s.nodeNext[n]; else s.nodeNext[prev] = s.nodeNext[n];
                    s.nodeNext[n] = s.nodeFree; s.nodeFree = n; s.nodeUsed--;
                    return;
                }
                prev = n; n = s.nodeNext[n];
            }
        }

        static int Degree(ref BaselineReductionScratch s, int v) { int d = 0; for (int n = s.head[v]; n >= 0; n = s.nodeNext[n]) d++; return d; }

        static int IndexInLoop(ref BaselineReductionScratch s, int f, int v)
        {
            int start = s.faceStart[f], cnt = s.faceCount[f];
            for (int k = 0; k < cnt; k++) if (s.loop[start + k] == v) return k;
            return -1;
        }

        static int RingCycle(ref BaselineReductionScratch s, int v, out int starCount)
        {
            starCount = 0;
            int d = 0;
            int first = -1;
            for (int n = s.head[v]; n >= 0; n = s.nodeNext[n])
            {
                int f = s.nodeFace[n];
                if (starCount >= s.V + 4) { ClearRing(ref s, starCount); return -1; }
                int start = s.faceStart[f], cnt = s.faceCount[f];
                int k = IndexInLoop(ref s, f, v);
                s.starFaces[starCount] = f; s.starK[starCount] = k; starCount++;
                if (k < 0 || cnt < 3) { ClearRing(ref s, starCount); return -1; }
                int a = s.loop[start + (k + cnt - 1) % cnt], b = s.loop[start + (k + 1) % cnt];
                if (s.nextOf[a] >= 0) { ClearRing(ref s, starCount); return -1; }
                s.nextOf[a] = b; d++;
                if (first < 0) first = a;
            }
            if (d < 3) { ClearRing(ref s, starCount); return -1; }
            int cur = first, len = 0;
            do
            {
                if (len >= d) { ClearRing(ref s, starCount); return -1; }
                s.link[len++] = cur;
                cur = s.nextOf[cur];
                if (cur < 0) { ClearRing(ref s, starCount); return -1; }
            } while (cur != first);
            ClearRing(ref s, starCount);
            return len == d ? d : -1;
        }

        static void ClearRing(ref BaselineReductionScratch s, int starCount)
        {
            for (int i = 0; i < starCount; i++)
            {
                int f = s.starFaces[i]; int start = s.faceStart[f], cnt = s.faceCount[f];
                for (int k = 0; k < cnt; k++) s.nextOf[s.loop[start + k]] = -1;
            }
        }

        static int SelectBest(ref BaselineReductionScratch s, int mode, double tol, ref ReductionStats stats)
        {
            int best = -1; double bestScore = double.PositiveInfinity;
            for (int v = 0; v < s.V; v++)
            {
                if (s.alive[v] == 0) continue;
                if (s.dirty[v] != 0)
                {
                    s.dirty[v] = 0;
                    s.score[v] = Evaluate(ref s, v, mode, tol, ref stats, out _);
                }
                else if (mode == 0 && !double.IsInfinity(s.score[v]) && Degree(ref s, v) != 3) continue;
                if (s.score[v] < bestScore) { bestScore = s.score[v]; best = v; }
            }
            return best;
        }

        static double Evaluate(ref BaselineReductionScratch s, int v, int mode, double tol, ref ReductionStats stats, out int patchCount)
        {
            patchCount = 0;
            stats.evaluations++;
            int d = RingCycle(ref s, v, out int starCount);
            if (d < 3) { stats.rejNotDisk++; stats.rejected++; return double.PositiveInfinity; }
            if (mode == 0 && d != 3) return double.PositiveInfinity;
            if (mode == 1 && d > k_r1SmallRing) return double.PositiveInfinity;
            double3 vp = s.pos[v];
            if (d == 3)
            {
                s.patchA[0] = s.link[0]; s.patchB[0] = s.link[1]; s.patchC[0] = s.link[2];
                patchCount = 1;
            }
            else
            {
                int hc = RingHullPatch(ref s, v, d, tol, out bool coplanar, ref stats);
                if (coplanar)
                {
                    patchCount = -1;
                    double3 n = PolyNormal(ref s, d);
                    double ln = math.length(n);
                    if (ln <= tol * tol) { stats.rejDegenerate++; stats.rejected++; return double.PositiveInfinity; }
                    n /= ln;
                    if (math.dot(n, vp - s.pos[s.link[0]]) < 0) n = -n;
                    if (math.dot(n, vp - s.pos[s.link[0]]) <= tol) { stats.rejNoCap++; stats.rejected++; return double.PositiveInfinity; }
                    bool anyThick = false;
                    for (int i = 0; i < d; i++)
                    {
                        int far = FarAcrossRingEdge(ref s, v, s.link[i], s.link[(i + 1) % d], starCount);
                        if (far < 0) { stats.rejFarMissing++; stats.rejected++; return double.PositiveInfinity; }
                        double h = math.dot(n, s.pos[far] - s.pos[s.link[0]]);
                        if (h > tol) { stats.rejOutside++; stats.rejected++; return double.PositiveInfinity; }
                        if (h < -tol) anyThick = true;
                    }
                    if (!anyThick) { stats.rejFlat++; stats.rejected++; return double.PositiveInfinity; }
                    double vol = 0;
                    for (int i = 1; i + 1 < d; i++) vol += math.abs(math.dot(s.pos[s.link[0]] - vp, math.cross(s.pos[s.link[i]] - vp, s.pos[s.link[i + 1]] - vp))) / 6.0;
                    return vol;
                }
                if (hc <= 0) { stats.rejected++; return double.PositiveInfinity; }
                patchCount = hc;
            }
            double capVolume = 0;
            for (int t = 0; t < patchCount; t++)
            {
                int a = s.patchA[t], b = s.patchB[t], c = s.patchC[t];
                double3 n = math.cross(s.pos[b] - s.pos[a], s.pos[c] - s.pos[a]);
                double ln = math.length(n);
                if (ln <= tol * tol) { stats.rejDegenerate++; stats.rejected++; return double.PositiveInfinity; }
                n /= ln;
                double dv = math.dot(n, vp - s.pos[a]);
                bool flip = false;
                if (dv < -tol) flip = true;
                else if (dv <= tol)
                {
                    int orientBy = -1;
                    for (int e = 0; e < 3 && orientBy < 0; e++)
                    {
                        int p = e == 0 ? a : e == 1 ? b : c, q = e == 0 ? b : e == 1 ? c : a;
                        int far = FarAcrossRingEdge(ref s, v, p, q, starCount);
                        if (far >= 0 && math.abs(math.dot(n, s.pos[far] - s.pos[a])) > tol) { orientBy = far; flip = math.dot(n, s.pos[far] - s.pos[a]) > 0; }
                    }
                    if (orientBy < 0) { stats.rejFlat++; stats.rejected++; return double.PositiveInfinity; }
                }
                if (flip) { n = -n; dv = -dv; int tmp = s.patchB[t]; s.patchB[t] = s.patchC[t]; s.patchC[t] = tmp; b = s.patchB[t]; c = s.patchC[t]; }
                if (dv < -tol) { stats.rejNoCap++; stats.rejected++; return double.PositiveInfinity; }
                capVolume += math.abs(math.dot(s.pos[a] - vp, math.cross(s.pos[b] - vp, s.pos[c] - vp))) / 6.0;
                for (int e = 0; e < 3; e++)
                {
                    int p = e == 0 ? a : e == 1 ? b : c, q = e == 0 ? b : e == 1 ? c : a;
                    int far = -1;
                    for (int u = 0; u < patchCount && far < 0; u++)
                    {
                        if (u == t) continue;
                        int ua = s.patchA[u], ub = s.patchB[u], uc = s.patchC[u];
                        int shared = (ua == p || ua == q ? 1 : 0) + (ub == p || ub == q ? 1 : 0) + (uc == p || uc == q ? 1 : 0);
                        if (shared == 2) far = ua != p && ua != q ? ua : ub != p && ub != q ? ub : uc;
                    }
                    if (far < 0) far = FarAcrossRingEdge(ref s, v, p, q, starCount);
                    if (far < 0) { stats.rejFarMissing++; stats.rejected++; return double.PositiveInfinity; }
                    if (math.dot(n, s.pos[far] - s.pos[a]) > tol) { stats.rejOutside++; stats.rejected++; return double.PositiveInfinity; }
                }
            }
            return capVolume;
        }

        static int FarAcrossRingEdge(ref BaselineReductionScratch s, int v, int a, int b, int starCount)
        {
            for (int i = 0; i < starCount; i++)
            {
                int f = s.starFaces[i]; int start = s.faceStart[f], cnt = s.faceCount[f];
                int k = s.starK[i];
                int prev = s.loop[start + (k + cnt - 1) % cnt], next = s.loop[start + (k + 1) % cnt];
                if (!((prev == a && next == b) || (prev == b && next == a))) continue;
                if (cnt >= 4)
                {
                    for (int j = 0; j < cnt; j++) { int z = s.loop[start + j]; if (z != a && z != b && z != v) return z; }
                    return -1;
                }
                for (int n = s.head[a]; n >= 0; n = s.nodeNext[n])
                {
                    int g = s.nodeFace[n];
                    if (g == f) continue;
                    int gs = s.faceStart[g], gc = s.faceCount[g];
                    for (int m = 0; m < gc; m++)
                    {
                        int p = s.loop[gs + m], q = s.loop[gs + (m + 1) % gc];
                        if ((p == a && q == b) || (p == b && q == a))
                        {
                            for (int j = 0; j < gc; j++) { int z = s.loop[gs + j]; if (z != a && z != b) return z; }
                            return -1;
                        }
                    }
                }
                return -1;
            }
            return -1;
        }

        static double3 PolyNormal(ref BaselineReductionScratch s, int d)
        {
            double3 n = 0;
            for (int k = 0; k < d; k++)
            {
                double3 a = s.pos[s.link[k]], b = s.pos[s.link[(k + 1) % d]];
                n.x += (a.y - b.y) * (a.z + b.z); n.y += (a.z - b.z) * (a.x + b.x); n.z += (a.x - b.x) * (a.y + b.y);
            }
            return n;
        }

        static long EdgeKey(int a, int b) => ((long)math.min(a, b) << 32) | (uint)math.max(a, b);

        static int HashFind(long* keys, int cap, long key)
        {
            int mask = cap - 1;
            int h = (int)((key ^ (key >> 29)) * 0x9E3779B1L) & mask;
            while (true) { long k = keys[h]; if (k == key || k < 0) return h; h = (h + 1) & mask; }
        }

        static int RingHullPatch(ref BaselineReductionScratch s, int v, int d, double tol, out bool coplanar, ref ReductionStats stats)
        {
            coplanar = false;
            int hcap = math.min(s.Hcap, math.ceilpow2(math.max(64, 24 * d)));
            for (int i = 0; i < d; i++) s.hp[i] = s.pos[s.link[i]];
            int i0 = 0, i1 = 0;
            for (int i = 1; i < d; i++) { if (s.hp[i].x < s.hp[i0].x) i0 = i; if (s.hp[i].x > s.hp[i1].x) i1 = i; }
            if (i0 == i1) { for (int i = 1; i < d; i++) { if (s.hp[i].y < s.hp[i0].y) i0 = i; if (s.hp[i].y > s.hp[i1].y) i1 = i; } }
            if (i0 == i1) { stats.rejHull++; return 0; }
            int i2 = -1; double best = 0; double3 d01 = s.hp[i1] - s.hp[i0];
            for (int i = 0; i < d; i++) { double l = math.length(math.cross(d01, s.hp[i] - s.hp[i0])); if (l > best) { best = l; i2 = i; } }
            if (i2 < 0 || best <= tol * tol) { stats.rejHull++; return 0; }
            int i3 = -1; best = 0; double3 nn = math.normalize(math.cross(d01, s.hp[i2] - s.hp[i0]));
            for (int i = 0; i < d; i++) { double h = math.abs(math.dot(nn, s.hp[i] - s.hp[i0])); if (h > best) { best = h; i3 = i; } }
            if (i3 < 0 || best <= tol) { coplanar = true; return 0; }
            int fc = 0;
            double3 centroid = (s.hp[i0] + s.hp[i1] + s.hp[i2] + s.hp[i3]) * 0.25;
            AddHullFace(ref s, ref fc, i0, i1, i2, centroid); AddHullFace(ref s, ref fc, i0, i1, i3, centroid); AddHullFace(ref s, ref fc, i0, i2, i3, centroid); AddHullFace(ref s, ref fc, i1, i2, i3, centroid);
            for (int p = 0; p < d; p++)
            {
                if (p == i0 || p == i1 || p == i2 || p == i3) continue;
                bool any = false;
                for (int f = 0; f < fc; f++) { s.hfVisible[f] = (byte)((s.hfDead[f] == 0 && math.dot(s.hfN[f], s.hp[p]) - s.hfD[f] > tol) ? 1 : 0); any |= s.hfVisible[f] != 0; }
                if (!any) continue;
                for (int i = 0; i < hcap; i++) s.hkeys[i] = -1;
                for (int f = 0; f < fc; f++)
                {
                    if (s.hfVisible[f] == 0) continue;
                    MarkDirected(ref s, hcap, s.hfA[f], s.hfB[f]); MarkDirected(ref s, hcap, s.hfB[f], s.hfC[f]); MarkDirected(ref s, hcap, s.hfC[f], s.hfA[f]);
                }
                int fcOld = fc;
                for (int f = 0; f < fcOld; f++)
                {
                    if (s.hfVisible[f] == 0) continue;
                    int a = s.hfA[f], b = s.hfB[f], c = s.hfC[f];
                    if (!HasDirected(ref s, hcap, b, a)) { if (!AddHullFaceRaw(ref s, ref fc, a, b, p)) return 0; }
                    if (!HasDirected(ref s, hcap, c, b)) { if (!AddHullFaceRaw(ref s, ref fc, b, c, p)) return 0; }
                    if (!HasDirected(ref s, hcap, a, c)) { if (!AddHullFaceRaw(ref s, ref fc, c, a, p)) return 0; }
                    s.hfDead[f] = 1;
                }
            }
            for (int i = 0; i < d; i++) s.stamp[i] = 0;
            for (int f = 0; f < fc; f++) if (s.hfDead[f] == 0) { s.stamp[s.hfA[f]] = 1; s.stamp[s.hfB[f]] = 1; s.stamp[s.hfC[f]] = 1; }
            for (int i = 0; i < d; i++) if (s.stamp[i] == 0) { stats.rejInterior++; return 0; }
            double3 vp = s.pos[v];
            int patch = 0;
            for (int i = 0; i < hcap; i++) s.hkeys[i] = -1;
            for (int f = 0; f < fc; f++)
            {
                if (s.hfDead[f] != 0) continue;
                bool vis = math.dot(s.hfN[f], vp) - s.hfD[f] > -tol;
                s.hfVisible[f] = (byte)(vis ? 1 : 0);
                if (!vis) continue;
                if (patch >= s.HullFcap) return 0;
                s.patchA[patch] = s.link[s.hfA[f]]; s.patchB[patch] = s.link[s.hfB[f]]; s.patchC[patch] = s.link[s.hfC[f]]; patch++;
                CountUndirected(ref s, hcap, s.hfA[f], s.hfB[f]); CountUndirected(ref s, hcap, s.hfB[f], s.hfC[f]); CountUndirected(ref s, hcap, s.hfC[f], s.hfA[f]);
            }
            if (patch == 0) { stats.rejHull++; return 0; }
            int horizon = 0;
            for (int i = 0; i < hcap; i++) if (s.hkeys[i] >= 0 && s.hvals[i] == 1) horizon++;
            if (horizon != d) { stats.rejHorizon++; return 0; }
            for (int i = 0; i < d; i++)
            {
                int slot = HashFind(s.hkeys, hcap, EdgeKey(i, (i + 1) % d));
                if (s.hkeys[slot] < 0 || s.hvals[slot] != 1) { stats.rejHorizon++; return 0; }
            }
            return patch;
        }

        static void MarkDirected(ref BaselineReductionScratch s, int hcap, int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            int slot = HashFind(s.hkeys, hcap, key);
            s.hkeys[slot] = key; s.hvals[slot] = 1;
        }

        static bool HasDirected(ref BaselineReductionScratch s, int hcap, int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            int slot = HashFind(s.hkeys, hcap, key);
            return s.hkeys[slot] == key;
        }

        static void CountUndirected(ref BaselineReductionScratch s, int hcap, int a, int b)
        {
            long key = EdgeKey(a, b);
            int slot = HashFind(s.hkeys, hcap, key);
            if (s.hkeys[slot] < 0) { s.hkeys[slot] = key; s.hvals[slot] = 1; } else s.hvals[slot]++;
        }

        static void AddHullFace(ref BaselineReductionScratch s, ref int fc, int a, int b, int c, double3 centroid)
        {
            double3 n = math.cross(s.hp[b] - s.hp[a], s.hp[c] - s.hp[a]);
            if (math.dot(n, (s.hp[a] + s.hp[b] + s.hp[c]) / 3.0 - centroid) < 0) { int t = b; b = c; c = t; }
            AddHullFaceRaw(ref s, ref fc, a, b, c);
        }

        static bool AddHullFaceRaw(ref BaselineReductionScratch s, ref int fc, int a, int b, int c)
        {
            if (fc >= s.HullFcap) return false;
            s.hfA[fc] = a; s.hfB[fc] = b; s.hfC[fc] = c;
            double3 n = math.normalize(math.cross(s.hp[b] - s.hp[a], s.hp[c] - s.hp[a]));
            s.hfN[fc] = n; s.hfD[fc] = math.dot(n, s.hp[a]); s.hfDead[fc] = 0; s.hfVisible[fc] = 0;
            fc++;
            return true;
        }

        static int Apply(ref BaselineReductionScratch s, int v, double tol, ref ReductionStats stats)
        {
            int mode = 2;
            double sc = Evaluate(ref s, v, mode, tol, ref stats, out int patchCount);
            if (double.IsInfinity(sc) || patchCount == 0) return (int)ReductionStatus.InternalFailure;
            int d = RingCycle(ref s, v, out int starCount);
            if (d < 3) return (int)ReductionStatus.InternalFailure;
            stats.removedVolume += sc;
            stats.maxDegreeRemoved = math.max(stats.maxDegreeRemoved, d);
            for (int i = 0; i < starCount; i++)
            {
                int f = s.starFaces[i]; int start = s.faceStart[f], cnt = s.faceCount[f];
                int k = s.starK[i];
                for (int j = k; j + 1 < cnt; j++) s.loop[start + j] = s.loop[start + j + 1];
                s.faceCount[f] = --cnt;
                if (cnt < 3)
                {
                    for (int j = 0; j < cnt; j++) RemoveIncidence(ref s, s.loop[start + j], f);
                    FreeLoopBlock(ref s, start, cnt + 1);
                    FreeFace(ref s, f);
                }
            }
            while (s.head[v] >= 0) { int n = s.head[v]; s.head[v] = s.nodeNext[n]; s.nodeNext[n] = s.nodeFree; s.nodeFree = n; s.nodeUsed--; }
            s.alive[v] = 0;
            if (patchCount < 0)
            {
                int f = AllocFace(ref s);
                if (f < 0 || s.loopCursor + d > s.Icap) return (int)ReductionStatus.CapacityScratch;
                s.faceStart[f] = s.loopCursor; s.faceCount[f] = d; s.faceAlive[f] = 1;
                double3 n = PolyNormal(ref s, d);
                bool reverse = math.dot(n, s.pos[v] - s.pos[s.link[0]]) < 0;
                for (int i = 0; i < d; i++) { int lv = reverse ? s.link[d - 1 - i] : s.link[i]; s.loop[s.loopCursor + i] = lv; if (!AddIncidence(ref s, lv, f)) return (int)ReductionStatus.CapacityScratch; }
                s.loopCursor += d;
                stats.maxPatchFaces = math.max(stats.maxPatchFaces, 1);
            }
            else
            {
                for (int t = 0; t < patchCount; t++)
                {
                    int f = AllocFace(ref s);
                    int blk = AllocTriangle(ref s);
                    if (f < 0 || blk < 0) return (int)ReductionStatus.CapacityScratch;
                    s.faceStart[f] = blk; s.faceCount[f] = 3; s.faceAlive[f] = 1;
                    s.loop[blk] = s.patchA[t]; s.loop[blk + 1] = s.patchB[t]; s.loop[blk + 2] = s.patchC[t];
                    if (!AddIncidence(ref s, s.patchA[t], f) || !AddIncidence(ref s, s.patchB[t], f) || !AddIncidence(ref s, s.patchC[t], f)) return (int)ReductionStatus.CapacityScratch;
                }
                stats.maxPatchFaces = math.max(stats.maxPatchFaces, patchCount);
            }
            for (int i = 0; i < d; i++)
            {
                int a = s.link[i], b = s.link[(i + 1) % d];
                s.dirty[a] = 1;
                for (int n = s.head[a]; n >= 0; n = s.nodeNext[n])
                {
                    int f = s.nodeFace[n]; int start = s.faceStart[f], cnt = s.faceCount[f];
                    if (cnt > 4) continue;
                    bool hasB = false;
                    for (int k = 0; k < cnt; k++) if (s.loop[start + k] == b) { hasB = true; break; }
                    if (!hasB) continue;
                    for (int k = 0; k < cnt; k++) s.dirty[s.loop[start + k]] = 1;
                }
            }
            return 0;
        }

        static int Compact(ref BrepBuffer io, ref BaselineReductionScratch s, int alive)
        {
            int nv = 0;
            for (int i = 0; i < s.V; i++) s.nextOf[i] = s.alive[i] != 0 ? nv++ : -1;
            if (nv != alive || nv > io.vCap) return (int)ReductionStatus.InternalFailure;
            for (int i = 0; i < s.V; i++) if (s.alive[i] != 0) io.v[s.nextOf[i]] = (float3)s.pos[i];
            int nf = 0, ni = 0;
            for (int f = 0; f < s.faceTotal; f++)
            {
                if (s.faceAlive[f] == 0) continue;
                int cnt = s.faceCount[f];
                if (nf + 1 > io.fCap || ni + cnt > io.iCap) return (int)ReductionStatus.InternalFailure;
                io.faceOff[nf] = ni;
                for (int k = 0; k < cnt; k++) io.faceIdx[ni + k] = s.nextOf[s.loop[s.faceStart[f] + k]];
                ni += cnt; nf++;
            }
            io.faceOff[nf] = ni;
            io.V = nv; io.F = nf; io.I = ni; io.E = 0;
            for (int i = 0; i < s.V; i++) s.nextOf[i] = -1;
            int probes = 0;
            int r = BaselinePolygonEdgeCutKernel.BuildEdgeTable(ref io, s.ekeys, s.evals, s.ecap, ref probes);
            return r == 0 ? (int)ReductionStatus.Ok : (int)ReductionStatus.InternalFailure;
        }
    }
}
