using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut
{
    public enum ReductionStatus : int { Ok = 0, NotNeeded = 1, ReductionFailed = 2, CapacityScratch = 3, InternalFailure = 4 }

    public struct ReductionStats
    {
        public int inputV, outputV, removedR0, removedR1, r1Invoked, evaluations, maxPatchFaces, maxDegreeRemoved, rejected;
        public int rejNotDisk, rejHull, rejInterior, rejHorizon, rejDegenerate, rejNoCap, rejFarMissing, rejOutside, rejFlat;
        public double removedVolume;
    }

    /// <summary>
    /// Editable polytope in worst-case pre-reserved scratch (no allocation). Faces keep their loops in a shared index
    /// arena: original loops in place (they only shrink), patch triangles appended. Vertex -> incident face lists are a
    /// node pool. <see cref="ConvexReductionKernel"/> initialises every slot it reads, so a reused block never leaks
    /// state between calls (probe finding: an earlier version assumed zeroed scratch and produced phantom vertices).
    /// </summary>
    public unsafe struct ReductionScratch
    {
        public int V, Fcap, Icap, Ncap, Hcap, HullFcap;
        // vertices
        public double3* pos; public byte* alive; public byte* dirty; public double* score; public int* head;
        // faces
        public int* faceStart; public int* faceCount; public byte* faceAlive; public int* loop; public int faceTotal, loopCursor;
        // incidence node pool
        public int* nodeFace; public int* nodeNext; public int nodeFree, nodeUsed;
        // link / patch temporaries
        public int* link; public int* nextOf; public int* stamp; public int* starFaces; public int* starK; public int* patchA; public int* patchB; public int* patchC;
        public int faceFree, triFree;      // free lists: dead face slots, dead 3-slot loop blocks (patch triangles)
        // hull temporaries (link points)
        public double3* hp; public int* hfA; public int* hfB; public int* hfC; public double3* hfN; public double* hfD; public byte* hfDead; public byte* hfVisible;
        public long* hkeys; public int* hvals;
        // edge-table hash for the final compaction
        public long* ekeys; public int* evals; public int ecap;

        /// <summary>
        /// Lays out the scratch for a polytope with up to V vertices, E edges, F faces and I loop slots (the output
        /// buffer capacities of the clip). basePtr == null only computes the size. The layout is the capacity formula.
        /// </summary>
        public static ReductionScratch Layout(byte* basePtr, int V, int E, int F, int I, out int bytes)
        {
            int off = 0;
            T* Take<T>(int count) where T : unmanaged { off = (off + 15) / 16 * 16; var p = basePtr == null ? null : (T*)(basePtr + off); off += math.max(1, count) * sizeof(T); return p; }
            var s = new ReductionScratch { V = V };
            // alive faces never exceed 2V - 4 (Euler) and dead slots are reused, so F + 2V + 8 face slots and a 2V + 8 triangle block pool suffice;
            // polygon patches (coplanar ring, rare) take at most V slots from the arena tail
            int newFacesMax = 2 * V + 8;
            s.Fcap = F + newFacesMax;
            s.Icap = I + 3 * newFacesMax + V + 4;
            s.Ncap = s.Icap;                          // one incidence node per loop slot
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

    /// <summary>
    /// Inscribed reduction of a convex B-rep to at most L vertices (DESIGN 7.2, probe "Burst R0 -> R1"):
    /// R0 degree-3 cutoff, then (only on R0 exhaustion) R1 local cavity hull, both incremental. Every step replaces
    /// one vertex by a locally convex patch over its link, so Q ⊆ C by construction (each patch plane is a supporting
    /// plane verified by local dihedral convexity). Candidate scores are cached per vertex and re-evaluated only in
    /// the neighbourhood of a removed vertex. No global hull rebuild, no outward growth, no allocation.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class ConvexReductionKernel
    {
        const double k_relTol = 1e-6;
        const int k_r1SmallRing = 12;

        /// <summary>Reduces <paramref name="io"/> in place to at most <paramref name="L"/> vertices (edge table rebuilt).</summary>
        [BurstCompile]
        public static int Reduce(ref BrepBuffer io, int L, ref ReductionScratch s, ref ReductionStats stats) => ReduceUpTo(ref io, L, ref s, ref stats, 2);

        /// <summary>maxMode: 0 = R0 only (ReductionFailed when R0 is exhausted, io untouched), 1 = R0 + small-ring R1, 2 = full (product).</summary>
        [BurstCompile]
        public static int ReduceUpTo(ref BrepBuffer io, int L, ref ReductionScratch s, ref ReductionStats stats, int maxMode)
        {
            stats.inputV = io.V;
            if (io.V <= L) { stats.outputV = io.V; return (int)ReductionStatus.NotNeeded; }
            if (io.V > s.V || io.F > s.Fcap || io.I > s.Icap) return (int)ReductionStatus.CapacityScratch;
            Init(ref io, ref s, out double tol);
            int alive = io.V;

            // R0: degree-3 only
            int mode = 0;
            for (int i = 0; i < io.V; i++) { s.dirty[i] = 1; s.score[i] = double.PositiveInfinity; }
            while (alive > L)
            {
                int best = SelectBest(ref s, mode, tol, ref stats);
                if (best < 0)
                {
                    if (mode == 0 && maxMode > 0)
                    {
                        // R0 exhausted: switch to R1 (small rings) from the current (partial) state
                        mode = 1; stats.r1Invoked = 1;
                        for (int i = 0; i < s.V; i++) if (s.alive[i] != 0) { s.dirty[i] = 1; }
                        continue;
                    }
                    if (mode == 1 && maxMode > 1)
                    {
                        // no small-ring candidate: allow any ring degree
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

        // ---------------- structure ----------------

        static void Init(ref BrepBuffer io, ref ReductionScratch s, out double tol)
        {
            double3 mn = new double3(double.MaxValue), mx = new double3(double.MinValue);
            for (int i = 0; i < io.V; i++) { s.pos[i] = io.v[i]; s.alive[i] = 1; s.head[i] = -1; mn = math.min(mn, s.pos[i]); mx = math.max(mx, s.pos[i]); }
            // the scratch may be laid out from the buffer capacity (s.V >= io.V) and reused: slots beyond io.V must read as dead
            for (int i = io.V; i < s.V; i++) { s.alive[i] = 0; s.dirty[i] = 0; s.score[i] = double.PositiveInfinity; s.head[i] = -1; }
            for (int i = 0; i < s.V; i++) s.nextOf[i] = -1;
            tol = k_relTol * math.cmax(mx - mn);
            for (int i = 0; i < s.Ncap; i++) s.nodeNext[i] = i + 1;
            s.nodeNext[s.Ncap - 1] = -1; s.nodeFree = 0; s.nodeUsed = 0;
            for (int f = 0; f < s.Fcap; f++) s.faceAlive[f] = 0;
            for (int f = 0; f < io.F; f++)
            {
                int start = io.faceOff[f], cnt = io.faceOff[f + 1] - start;
                s.faceStart[f] = start; s.faceCount[f] = cnt; s.faceAlive[f] = 1;
                for (int k = 0; k < cnt; k++) { s.loop[start + k] = io.faceIdx[start + k]; AddIncidence(ref s, io.faceIdx[start + k], f); }
            }
            s.faceTotal = io.F; s.loopCursor = io.I;
            s.faceFree = -1; s.triFree = -1;
        }

        static int AllocFace(ref ReductionScratch s)
        {
            if (s.faceFree >= 0) { int f = s.faceFree; s.faceFree = s.faceStart[f]; return f; }
            if (s.faceTotal >= s.Fcap) return -1;
            return s.faceTotal++;
        }

        static void FreeFace(ref ReductionScratch s, int f)
        {
            s.faceAlive[f] = 0; s.faceCount[f] = 0;
            s.faceStart[f] = s.faceFree; s.faceFree = f;   // faceStart doubles as the free-list link while dead
        }

        static int AllocTriangle(ref ReductionScratch s)
        {
            if (s.triFree >= 0) { int t = s.triFree; s.triFree = s.loop[t]; return t; }
            if (s.loopCursor + 3 > s.Icap) return -1;
            int start = s.loopCursor; s.loopCursor += 3; return start;
        }

        static void FreeLoopBlock(ref ReductionScratch s, int start, int cnt)
        {
            if (cnt != 3) return;   // only triangle blocks are pooled (trimmed originals shrink in place)
            s.loop[start] = s.triFree; s.triFree = start;
        }

        static bool AddIncidence(ref ReductionScratch s, int v, int f)
        {
            int n = s.nodeFree;
            if (n < 0) return false;
            s.nodeFree = s.nodeNext[n];
            s.nodeFace[n] = f; s.nodeNext[n] = s.head[v]; s.head[v] = n; s.nodeUsed++;
            return true;
        }

        static void RemoveIncidence(ref ReductionScratch s, int v, int f)
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

        static int Degree(ref ReductionScratch s, int v) { int d = 0; for (int n = s.head[v]; n >= 0; n = s.nodeNext[n]) d++; return d; }

        static int IndexInLoop(ref ReductionScratch s, int f, int v)
        {
            int start = s.faceStart[f], cnt = s.faceCount[f];
            for (int k = 0; k < cnt; k++) if (s.loop[start + k] == v) return k;
            return -1;
        }

        /// Ordered ring of v's immediate loop neighbours (the chord endpoints of every star face, in star order) into s.link.
        /// Trimming each star face (…, a, v, b, …) leaves the chord (a,b); the ring of those chords bounds the hole the patch must fill.
        /// Returns the ring length (== degree), or -1 when the star is not a disk. Fills s.starFaces (starCount).
        static int RingCycle(ref ReductionScratch s, int v, out int starCount)
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

        static void ClearRing(ref ReductionScratch s, int starCount)
        {
            for (int i = 0; i < starCount; i++)
            {
                int f = s.starFaces[i]; int start = s.faceStart[f], cnt = s.faceCount[f];
                for (int k = 0; k < cnt; k++) s.nextOf[s.loop[start + k]] = -1;
            }
        }

        // ---------------- candidate evaluation ----------------

        /// Re-evaluates dirty candidates (mode 0: degree 3 only) and returns the alive vertex with the smallest cap volume, or -1.
        static int SelectBest(ref ReductionScratch s, int mode, double tol, ref ReductionStats stats)
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
                else if (mode == 0 && !double.IsInfinity(s.score[v]) && Degree(ref s, v) != 3) continue; // stale non-degree-3 under R0 (guard)
                if (s.score[v] < bestScore) { bestScore = s.score[v]; best = v; }
            }
            return best;
        }

        /// Cap volume of removing v with a valid local patch over its neighbour ring, or +inf. patchCount > 0 leaves triangles in
        /// s.patchA/B/C (vertex ids); patchCount == -1 means the ring itself is the (planar) patch polygon.
        static double Evaluate(ref ReductionScratch s, int v, int mode, double tol, ref ReductionStats stats, out int patchCount)
        {
            patchCount = 0;
            stats.evaluations++;
            int d = RingCycle(ref s, v, out int starCount);
            if (d < 3) { stats.rejNotDisk++; stats.rejected++; return double.PositiveInfinity; }
            if (mode == 0 && d != 3) return double.PositiveInfinity;
            if (mode == 1 && d > k_r1SmallRing) return double.PositiveInfinity;   // R1 tier 1: small rings only
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
            // validity: v on or outside each patch plane; the far vertex across every patch edge inside (local convexity)
            double capVolume = 0;
            for (int t = 0; t < patchCount; t++)
            {
                int a = s.patchA[t], b = s.patchB[t], c = s.patchC[t];
                double3 n = math.cross(s.pos[b] - s.pos[a], s.pos[c] - s.pos[a]);
                double ln = math.length(n);
                if (ln <= tol * tol) { stats.rejDegenerate++; stats.rejected++; return double.PositiveInfinity; }
                n /= ln;
                double dv = math.dot(n, vp - s.pos[a]);
                // orientation: away from v when v is clearly off the plane; otherwise (flat cap piece, v coplanar within rounding)
                // by the far vertices, which must end up inside. A flat piece is a legal zero-volume removal (vertex on a face plane).
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

        /// The vertex across ring edge (a,b) after removing v: (a,b) is the chord of the star face whose loop neighbours of v are a and b.
        /// If that face keeps >= 3 vertices, any of its other vertices; if it collapses (triangle a,v,b), a vertex of the outer face sharing (a,b).
        static int FarAcrossRingEdge(ref ReductionScratch s, int v, int a, int b, int starCount)
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
                // collapsing triangle: the outer face across (a,b)
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

        static double3 PolyNormal(ref ReductionScratch s, int d)
        {
            double3 n = 0;
            for (int k = 0; k < d; k++)
            {
                double3 a = s.pos[s.link[k]], b = s.pos[s.link[(k + 1) % d]];
                n.x += (a.y - b.y) * (a.z + b.z); n.y += (a.z - b.z) * (a.x + b.x); n.z += (a.x - b.x) * (a.y + b.y);
            }
            return n;
        }

        // ---------------- local hull of the link ----------------

        static long EdgeKey(int a, int b) => ((long)math.min(a, b) << 32) | (uint)math.max(a, b);

        static int HashFind(long* keys, int cap, long key)
        {
            int mask = cap - 1;
            int h = (int)((key ^ (key >> 29)) * 0x9E3779B1L) & mask;
            while (true) { long k = keys[h]; if (k == key || k < 0) return h; h = (h + 1) & mask; }
        }

        /// Incremental 3D hull of the link points; the patch is the set of hull faces visible from v (including v-coplanar ones).
        /// Returns the number of patch triangles (link indices), 0 when not locally closable, and coplanar = true when the link is planar.
        static int RingHullPatch(ref ReductionScratch s, int v, int d, double tol, out bool coplanar, ref ReductionStats stats)
        {
            coplanar = false;
            // hash sized by the ring, not by the worst-case pool: the clears dominate otherwise
            int hcap = math.min(s.Hcap, math.ceilpow2(math.max(64, 24 * d)));
            for (int i = 0; i < d; i++) s.hp[i] = s.pos[s.link[i]];
            // initial simplex
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
                // horizon: directed edges of visible faces whose reverse is not in a visible face
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
            // every link vertex must be a hull vertex (otherwise it became interior: not locally closable)
            for (int i = 0; i < d; i++) s.stamp[i] = 0;
            for (int f = 0; f < fc; f++) if (s.hfDead[f] == 0) { s.stamp[s.hfA[f]] = 1; s.stamp[s.hfB[f]] = 1; s.stamp[s.hfC[f]] = 1; }
            for (int i = 0; i < d; i++) if (s.stamp[i] == 0) { stats.rejInterior++; return 0; }
            // visible from v (including coplanar faces: duplicates of trimmed star faces or rejected by the outside check)
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
                // count undirected edges of visible faces: horizon edges appear once
                CountUndirected(ref s, hcap, s.hfA[f], s.hfB[f]); CountUndirected(ref s, hcap, s.hfB[f], s.hfC[f]); CountUndirected(ref s, hcap, s.hfC[f], s.hfA[f]);
            }
            if (patch == 0) { stats.rejHull++; return 0; }
            // horizon must be exactly the link cycle
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

        static void MarkDirected(ref ReductionScratch s, int hcap, int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            int slot = HashFind(s.hkeys, hcap, key);
            s.hkeys[slot] = key; s.hvals[slot] = 1;
        }

        static bool HasDirected(ref ReductionScratch s, int hcap, int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            int slot = HashFind(s.hkeys, hcap, key);
            return s.hkeys[slot] == key;
        }

        static void CountUndirected(ref ReductionScratch s, int hcap, int a, int b)
        {
            long key = EdgeKey(a, b);
            int slot = HashFind(s.hkeys, hcap, key);
            if (s.hkeys[slot] < 0) { s.hkeys[slot] = key; s.hvals[slot] = 1; } else s.hvals[slot]++;
        }

        static void AddHullFace(ref ReductionScratch s, ref int fc, int a, int b, int c, double3 centroid)
        {
            double3 n = math.cross(s.hp[b] - s.hp[a], s.hp[c] - s.hp[a]);
            if (math.dot(n, (s.hp[a] + s.hp[b] + s.hp[c]) / 3.0 - centroid) < 0) { int t = b; b = c; c = t; }
            AddHullFaceRaw(ref s, ref fc, a, b, c);
        }

        static bool AddHullFaceRaw(ref ReductionScratch s, ref int fc, int a, int b, int c)
        {
            if (fc >= s.HullFcap) return false;
            s.hfA[fc] = a; s.hfB[fc] = b; s.hfC[fc] = c;
            double3 n = math.normalize(math.cross(s.hp[b] - s.hp[a], s.hp[c] - s.hp[a]));
            s.hfN[fc] = n; s.hfD[fc] = math.dot(n, s.hp[a]); s.hfDead[fc] = 0; s.hfVisible[fc] = 0;
            fc++;
            return true;
        }

        // ---------------- apply ----------------

        static int Apply(ref ReductionScratch s, int v, double tol, ref ReductionStats stats)
        {
            // recompute the patch for the chosen vertex (evaluation caches only the score)
            int mode = 2;
            double sc = Evaluate(ref s, v, mode, tol, ref stats, out int patchCount);
            if (double.IsInfinity(sc) || patchCount == 0) return (int)ReductionStatus.InternalFailure;
            int d = RingCycle(ref s, v, out int starCount);
            if (d < 3) return (int)ReductionStatus.InternalFailure;
            stats.removedVolume += sc;
            stats.maxDegreeRemoved = math.max(stats.maxDegreeRemoved, d);
            // trim star faces (dead faces / triangle blocks return to the pools)
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
            // v's incidence list is now stale: free it
            while (s.head[v] >= 0) { int n = s.head[v]; s.head[v] = s.nodeNext[n]; s.nodeNext[n] = s.nodeFree; s.nodeFree = n; s.nodeUsed--; }
            s.alive[v] = 0;
            // add patch faces
            if (patchCount < 0)
            {
                int f = AllocFace(ref s);
                if (f < 0 || s.loopCursor + d > s.Icap) return (int)ReductionStatus.CapacityScratch;
                s.faceStart[f] = s.loopCursor; s.faceCount[f] = d; s.faceAlive[f] = 1;
                // orientation: outward = away from v
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
            // dirty set: the ring vertices (their rings changed) and the vertices of faces adjacent to the new patch across ring edges
            // (their far-vertex checks now see a patch plane). Vertices of large trimmed faces that are not ring vertices keep their ring
            // and their adjacent planes, so they are not re-evaluated.
            for (int i = 0; i < d; i++)
            {
                int a = s.link[i], b = s.link[(i + 1) % d];
                s.dirty[a] = 1;
                for (int n = s.head[a]; n >= 0; n = s.nodeNext[n])
                {
                    int f = s.nodeFace[n]; int start = s.faceStart[f], cnt = s.faceCount[f];
                    if (cnt > 4) continue;                       // large faces: only ring vertices matter (their planes are unchanged)
                    bool hasB = false;
                    for (int k = 0; k < cnt; k++) if (s.loop[start + k] == b) { hasB = true; break; }
                    if (!hasB) continue;                          // only faces across the ring edge (a,b)
                    for (int k = 0; k < cnt; k++) s.dirty[s.loop[start + k]] = 1;
                }
            }
            return 0;
        }

        // ---------------- compaction ----------------

        static int Compact(ref BrepBuffer io, ref ReductionScratch s, int alive)
        {
            // remap vertices (reuse nextOf as remap)
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
            int r = ConvexClipKernel.BuildEdgeTable(ref io, s.ekeys, s.evals, s.ecap, ref probes);
            return r == 0 ? (int)ReductionStatus.Ok : (int)ReductionStatus.InternalFailure;
        }
    }
}
