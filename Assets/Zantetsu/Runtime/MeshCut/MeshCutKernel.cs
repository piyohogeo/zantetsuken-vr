using System;
using Unity.Burst;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Display / stencil shared-geometry cut kernel (DESIGN 6, Phase 2.9): the probe's adopted scan + edge-hash +
    /// split-cap cut, ported to the global AoS vertex / index pool with attribute seams, the 6.4 classification
    /// contract and the 4.5.6 direct positive-then-negative index placement.
    ///
    /// Classification (DESIGN 6.4). The signed distance of a topology vertex is d = dot(plane.xyz, p) + plane.w
    /// evaluated on its render vertices, which share one canonical posed position by the 6.2 contract, so every
    /// incident triangle sees the same value. d &gt;= 0 is positive: OnPlane vertices are owned by the positive side,
    /// there is no epsilon band and no third state. A triangle whose three vertices all lie on the plane is therefore
    /// an ordinary positive triangle, kept once and contributing no cap segment; a triangle with an OnPlane vertex
    /// and a negative vertex is an ordinary crossing triangle whose intersection node lands exactly on that vertex's
    /// position as a distinct topology vertex (several cut ports at one point are ordinary output, never welded).
    ///
    /// Identity. Intersection nodes are keyed by the topology edge (lo, hi) they lie on, so both incident triangles
    /// share one node, one parameter and one canonical position. Attributes are interpolated per face side from the
    /// face's own render vertices, keyed by (node, render pair): a smooth edge yields one new render vertex, a seam
    /// edge one per side, and no seam side is ever mixed. Caps get their own render vertices (hard edge to the
    /// surface) with the plane normal signed by the winding of the cap triangle that uses them (one per node, side
    /// and sign; the logical topology stays one vertex per node) and the fixed cap UV marker of 5.3.
    ///
    /// Output (DESIGN 4.5.3 / 4.5.6). Existing vertices are referenced by their global numbers; only new render
    /// vertices are appended from the head of the one new-vertex reservation, shared by both sides. New indices are
    /// written into the one new-index reservation as [positive submesh 0 .. S-1 surface, positive caps][negative ...],
    /// caps joining the side's last non-empty submesh range. A geometry wholly on one side reuses its input ranges
    /// with no new index. The kernel writes only inside the reserved ranges, fails before writing past them with the
    /// exact requirement, never modifies the input and retains nothing.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Default, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public static unsafe class MeshCutKernel
    {
        public const float NearContactEpsilonRelative = 1e-6f;
        // Triangle class: 1 wholly positive, -1 wholly negative, 10 + lone corner when the lone corner is negative,
        // 20 + lone corner when the lone corner is positive (the side is decided once, in the classification pass).
        const sbyte k_crossingLoneNegative = 10;
        const sbyte k_crossingLonePositive = 20;

        struct Node
        {
            public long key;
            public float3 position;
            public float f;
            public uint rLo, rHi;
            public int slot0, slot1;
            public long pair0, pair1;
            public int capPosPlus, capPosMinus, capNegPlus, capNegMinus;   // cap render vertex per side and winding sign
        }

        struct Crossing
        {
            public int nodeP, nodeQ, slotP, slotQ;
        }

        /// <summary>One new render vertex, in output order. kind 0 interpolated node slot, 1 cap render vertex of a node, 2 cap auxiliary vertex.</summary>
        struct NewVertex
        {
            public byte kind;
            public sbyte sign;
            public int node;       // kind 0/1: node id; kind 2: auxiliary index (topology id offset)
            public uint rLo, rHi;
            public float f;
            public float3 position;
        }

        struct Bounds
        {
            public float3 min, max;
            public byte any;
            public void Add(float3 p)
            {
                if (any == 0) { min = p; max = p; any = 1; }
                else { min = math.min(min, p); max = math.max(max, p); }
            }
        }

        struct Layout
        {
            public sbyte* cls;
            public int* posCount, negCount;
            /// <summary>Per topology vertex: the signed distance, decided once, and its side (0 not yet touched, +1, -1).</summary>
            public float* dist;
            public sbyte* sideMemo;
            public long* mapKeys;
            public int* mapValues;
            public int mapMask;
            public Node* nodes;
            public int nodeCap;
            public Crossing* crossings;
            public int* segFromPos, segToPos, segFromNeg, segToNeg;
            public int* next, prev;
            public byte* used;
            public int* contourNodes, contourStart;
            public byte* contourClosed;
            public float* U, V;
            public float3* nodePos;
            public NewVertex* records;
            public int recordCap;
            public ScratchArena arena;
            public int fixedBytes;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>
        /// Sizes are worked out in 64 bits throughout, so that no product, sum or alignment wraps before
        /// <see cref="Fits"/> has judged it. A figure is only ever narrowed to an int there.
        /// </summary>
        static long Align16(long bytes) => (bytes + 15) / 16 * 16;

        /// <summary>
        /// Whether a size can be used as an int. False leaves <paramref name="bytes"/> at zero, which is why no
        /// caller may use it: a size that cannot be expressed ends the run, and is never rounded into a small
        /// reservation.
        /// </summary>
        /// <summary>
        /// The recommendation for a requirement that cannot be expressed as an int. Negative by contract:
        /// <c>VpStorageCut.TryAdvance</c> reads a negative requirement as too large to express and ends the run
        /// rather than retrying, which is the existing route for this.
        /// </summary>
        const int TooLarge = -1;

        static bool Fits(long value, out int bytes)
        {
            bool fits = value >= 0 && value <= int.MaxValue;
            bytes = fits ? (int)value : 0;
            return fits;
        }

        /// <summary>Bytes of the classification part (needed by every run, including a whole-mesh reuse): triangle classes, per-range counts, the per-topology-vertex distance memo.</summary>
        static long PhaseABytes(long T, long R, long N) => Align16(T) + Align16(4 * R) + Align16(4 * R) + Align16(4 * math.max(1L, N)) + Align16(math.max(1L, N));

        /// <summary>Bytes of the crossing-dependent fixed part (edge map, nodes, segments, contours, projection).</summary>
        static long PhaseBBytes(long K, out long nodeCap, out long mapCap)
        {
            nodeCap = math.max(1L, 2 * K);
            mapCap = 16;
            while (mapCap < nodeCap * 4) mapCap <<= 1;
            long bytes = 0;
            bytes += Align16(mapCap * 8) + Align16(mapCap * 4);
            bytes += Align16(nodeCap * sizeof(Node));
            bytes += Align16(math.max(1, K) * sizeof(Crossing));
            bytes += 4 * Align16(math.max(1, K) * 4);
            bytes += 2 * Align16(nodeCap * 4) + Align16(nodeCap);
            bytes += Align16(nodeCap * 4) + Align16((nodeCap + 1) * 4) + Align16(nodeCap);
            bytes += 2 * Align16(nodeCap * 4) + Align16(nodeCap * 12);
            bytes += Align16(6 * nodeCap * sizeof(NewVertex));   // 2 interpolated slots and up to 2 cap render vertices per side per node
            return bytes;
        }

        static long ArenaEstimate(long K) => 256 * math.max(1L, 2 * K) + 32 * 1024;
        static int AuxEstimate(int K) => math.max(32, K / 4);

        /// <summary>
        /// How many triangles of a surface of <paramref name="T"/> a plane is taken to cross, before anything about
        /// the geometry is read: <see cref="CrossingsPerRoot"/> times the square root of the triangle count, and never
        /// more than all of them.
        /// <para>
        /// **This is a rule of thumb for an opening reservation, not a law.** The reasoning behind its shape is that a
        /// plane meets a surface along a curve, so the triangles it crosses tend to form a band whose length grows
        /// with the square root of the area; the multiple was chosen from a handful of ordinary closed shapes the
        /// measurements cover, where the count came to a few times the root. Nothing here holds for an arbitrary
        /// input, and a shape the plane runs along can cross far more.
        /// </para>
        /// <para>
        /// Being wrong is ordinary. A run given too little stops before writing outside its reservation and says what
        /// it needed, and the caller's existing rule reserves again and runs again -- as many times as that rule
        /// allows, not once. Even where the estimate reaches T, the cap's auxiliary vertices and its arena are still
        /// estimated; and putting a scratch shortfall right can be followed by a vertex or index shortfall found on
        /// the attempt after it.
        /// </para>
        /// </summary>
        const int CrossingsPerRoot = 8;

        static int EstimatedCrossings(int T)
        {
            if (T <= 0)
            {
                return 0;
            }

            long band = (long)math.ceil(CrossingsPerRoot * math.sqrt((double)T));
            return (int)math.min(T, math.max(1L, band));
        }

        /// <summary>
        /// The three reservation figures for a run of <paramref name="T"/> triangles of which <paramref name="K"/>
        /// cross the plane. The one place they are written: <see cref="QueryCapacity"/> passes the K it counted and
        /// <see cref="EstimateCapacity"/> the K it guessed, and neither can mean anything different by them.
        /// <para>
        /// False when any of the three cannot be expressed as an int. The figures are then left as they were and no
        /// smaller ones are put in their place: a run this size cannot be reserved for at all, which the caller reports
        /// (<see cref="MeshCutCapacity.capacityOverflow"/>).
        /// </para>
        /// </summary>
        static bool ReservationFigures(int T, int K, int R, int N, ref MeshCutCapacity cap)
        {
            if (!Fits(PhaseABytes(T, R, N), out int phaseA))
            {
                return false;
            }

            if (K <= 0)
            {
                if (!Fits(3L * T, out int wholeIndices))
                {
                    return false;
                }

                cap.newVertices = 0;
                cap.newIndices = wholeIndices;
                cap.scratchBytes = phaseA;
                return true;
            }

            int aux = AuxEstimate(K);
            if (!Fits(12L * K + 2L * aux, out int vertices)
                || !Fits(3L * ((long)T + 6L * K + 4L * aux), out int indices)
                || !Fits(Align16(4L * aux * sizeof(NewVertex) + ArenaEstimate(K) * 4 / 3), out int leftover)
                || !Fits(PhaseABytes(T, R, N) + PhaseBBytes(K, out _, out _) + leftover, out int scratch))
            {
                return false;
            }

            cap.newVertices = vertices;
            cap.newIndices = indices;
            cap.scratchBytes = scratch;
            return true;
        }

        /// <summary>
        /// How many triangles the input's ranges describe, and whether those ranges are within the index view. Counts
        /// only: no vertex, index or triangle is read. A count too large for an int is not a count.
        /// </summary>
        static bool TryCountTriangles(in MeshCutInput input, out int T)
        {
            long total = 0;
            bool valid = input.indices != null;
            for (int r = 0; r < input.rangeCount; r++)
            {
                MeshCutIndexRange range = input.ranges[r];
                if (range.indexCount < 0 || range.indexCount % 3 != 0 || (long)range.indexStart + range.indexCount > input.indexViewLength)
                {
                    valid = false;
                }

                total += math.max(0, range.indexCount) / 3;
                if (total > int.MaxValue)
                {
                    T = int.MaxValue;
                    return false;
                }
            }

            T = (int)total;
            return valid;
        }

        // The offsets below add up to exactly PhaseABytes / PhaseBBytes, and a caller lays out only after that
        // total has been judged to fit an int, so each running offset is within it.
        static void LayoutPhaseA(byte* scratch, int T, int R, int N, ref Layout l)
        {
            int off = 0;
            l.cls = (sbyte*)(scratch + off); off += (int)Align16(T);
            l.posCount = (int*)(scratch + off); off += (int)Align16(4L * R);
            l.negCount = (int*)(scratch + off); off += (int)Align16(4L * R);
            l.dist = (float*)(scratch + off); off += (int)Align16(4L * math.max(1, N));
            l.sideMemo = (sbyte*)(scratch + off); off += (int)Align16(math.max(1, N));
            l.fixedBytes = off;
        }

        static void LayoutPhaseB(byte* scratch, int scratchBytes, int K, ref Layout l)
        {
            int off = l.fixedBytes;
            PhaseBBytes(K, out long nodeCapacity, out long mapCapacity);
            int nodeCap = (int)nodeCapacity;
            int mapCap = (int)mapCapacity;
            l.nodeCap = nodeCap; l.mapMask = mapCap - 1;
            l.mapKeys = (long*)(scratch + off); off += (int)Align16(mapCap * 8);
            l.mapValues = (int*)(scratch + off); off += (int)Align16(mapCap * 4);
            l.nodes = (Node*)(scratch + off); off += (int)Align16(nodeCap * sizeof(Node));
            l.crossings = (Crossing*)(scratch + off); off += (int)Align16(math.max(1, K) * sizeof(Crossing));
            l.segFromPos = (int*)(scratch + off); off += (int)Align16(math.max(1, K) * 4);
            l.segToPos = (int*)(scratch + off); off += (int)Align16(math.max(1, K) * 4);
            l.segFromNeg = (int*)(scratch + off); off += (int)Align16(math.max(1, K) * 4);
            l.segToNeg = (int*)(scratch + off); off += (int)Align16(math.max(1, K) * 4);
            l.next = (int*)(scratch + off); off += (int)Align16(nodeCap * 4);
            l.prev = (int*)(scratch + off); off += (int)Align16(nodeCap * 4);
            l.used = scratch + off; off += (int)Align16(nodeCap);
            l.contourNodes = (int*)(scratch + off); off += (int)Align16(nodeCap * 4);
            l.contourStart = (int*)(scratch + off); off += (int)Align16((nodeCap + 1) * 4);
            l.contourClosed = scratch + off; off += (int)Align16(nodeCap);
            l.U = (float*)(scratch + off); off += (int)Align16(nodeCap * 4);
            l.V = (float*)(scratch + off); off += (int)Align16(nodeCap * 4);
            l.nodePos = (float3*)(scratch + off); off += (int)Align16(nodeCap * 12);
            l.records = (NewVertex*)(scratch + off);
            int fixedRecords = 6 * nodeCap;
            off += (int)Align16(fixedRecords * sizeof(NewVertex));
            l.fixedBytes = off;
            // Whatever is left: a quarter for auxiliary vertex records, the rest for the per-cycle cap arena. Both scale
            // with the scratch size, so a larger reservation on the next attempt admits more contacts.
            int leftover = math.max(0, scratchBytes - off);
            int auxRecords = leftover / 4 / sizeof(NewVertex);
            l.recordCap = fixedRecords + auxRecords;
            off += (int)Align16(auxRecords * sizeof(NewVertex));
            // records are contiguous: fixed part and aux part share one array
            l.arena = new ScratchArena(scratch + off, math.max(0, scratchBytes - off));
        }

        // ------------------------------------------------------------------ helpers

        static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        static ulong Mix(ulong h, ulong v)
        {
            h ^= v + 0x9E3779B97F4A7C15ul + (h << 6) + (h >> 2);
            h ^= h >> 30; h *= 0xBF58476D1CE4E5B9ul;
            h ^= h >> 27; h *= 0x94D049BB133111EBul;
            h ^= h >> 31;
            return h;
        }

        /// <summary>Topology vertex of a global render vertex through the map's blocks (last hit cached), -1 when unmapped.</summary>
        static int TopologyOf(in RenderCutTopologyMap map, uint r, ref int lastRange)
        {
            if (lastRange >= 0 && lastRange < map.rangeCount)
            {
                ref RenderTopologyRange t = ref map.ranges[lastRange];
                if (r >= t.vertexBase && r < t.vertexBase + (uint)t.count) return t.topologyVertex[r - t.vertexBase];
            }
            for (int i = 0; i < map.rangeCount; i++)
            {
                ref RenderTopologyRange t = ref map.ranges[i];
                if (r >= t.vertexBase && r < t.vertexBase + (uint)t.count) { lastRange = i; return t.topologyVertex[r - t.vertexBase]; }
            }
            return -1;
        }


        [BurstDiscard]
        static void MarkManaged(ref MeshCutResult r) { r.executedManaged = 1; }

        // ------------------------------------------------------------------ pass 1: classification

        /// <summary>
        /// The side of one corner. With a memo (Execute) the signed distance of the corner's topology vertex is decided
        /// once, on first touch, from that render vertex's position (canonical for the topology vertex by 6.2) and then
        /// shared by every incident triangle; the side's bounds take the position at the same moment. Without a memo
        /// (capacity query, no scratch) the distance is evaluated per corner with the identical expression.
        /// Returns 0 on an invalid reference.
        /// </summary>
        static sbyte CornerSide(in MeshCutInput input, uint r, float* dist, sbyte* sideMemo, int N, ref int lastRange, ref Bounds posBounds, ref Bounds negBounds)
        {
            if (r >= (uint)input.vertexViewLength) return 0;
            if (dist == null)
            {
                float d = math.dot(input.plane.xyz, input.vertices[r].position) + input.plane.w;
                return (sbyte)(d < 0f ? -1 : 1);
            }
            int t = TopologyOf(in input.topology, r, ref lastRange);
            if (t < 0 || t >= N) return 0;
            sbyte s = sideMemo[t];
            if (s != 0) return s;
            float3 p = input.vertices[r].position;
            float dd = math.dot(input.plane.xyz, p) + input.plane.w;
            dist[t] = dd;
            s = (sbyte)(dd < 0f ? -1 : 1);
            sideMemo[t] = s;
            if (s < 0) negBounds.Add(p); else posBounds.Add(p);
            return s;
        }

        /// <summary>
        /// Classifies every triangle of every range. With <paramref name="cls"/> null nothing is stored and no memo is
        /// used (capacity query). Returns false on an invalid index, range or topology reference.
        /// </summary>
        static bool Classify(in MeshCutInput input, sbyte* cls, int* posCount, int* negCount, float* dist, sbyte* sideMemo, out int T, out int K,
                             out bool anyPos, out bool anyNeg, ref Bounds posBounds, ref Bounds negBounds)
        {
            T = 0; K = 0; anyPos = false; anyNeg = false;
            int N = input.topology.topologyVertexCount;
            if (sideMemo != null) for (int i = 0; i < N; i++) sideMemo[i] = 0;
            int g = 0, lastRange = -1;
            for (int r = 0; r < input.rangeCount; r++)
            {
                MeshCutIndexRange range = input.ranges[r];
                if (range.indexCount < 0 || range.indexCount % 3 != 0) return false;
                if ((long)range.indexStart + range.indexCount > input.indexViewLength) return false;
                int pc = 0, nc = 0;
                uint* idx = input.indices + range.indexStart;
                int tris = range.indexCount / 3;
                for (int t = 0; t < tris; t++)
                {
                    sbyte s0 = CornerSide(in input, idx[3 * t], dist, sideMemo, N, ref lastRange, ref posBounds, ref negBounds);
                    sbyte s1 = CornerSide(in input, idx[3 * t + 1], dist, sideMemo, N, ref lastRange, ref posBounds, ref negBounds);
                    sbyte s2 = CornerSide(in input, idx[3 * t + 2], dist, sideMemo, N, ref lastRange, ref posBounds, ref negBounds);
                    if (s0 == 0 || s1 == 0 || s2 == 0) return false;
                    bool n0 = s0 < 0, n1 = s1 < 0, n2 = s2 < 0;
                    int negatives = (n0 ? 1 : 0) + (n1 ? 1 : 0) + (n2 ? 1 : 0);
                    sbyte c;
                    if (negatives == 0) { c = 1; pc++; anyPos = true; }
                    else if (negatives == 3) { c = -1; nc++; anyNeg = true; }
                    else
                    {
                        anyPos = true; anyNeg = true;
                        K++;
                        int lone;
                        if (negatives == 1) lone = n0 ? 0 : n1 ? 1 : 2;   // the lone corner is the negative one
                        else lone = n0 ? (n1 ? 2 : 1) : 0;               // two negatives: the lone corner is the positive one
                        bool lonePositive = negatives == 2;
                        if (lonePositive) { pc += 1; nc += 2; } else { pc += 2; nc += 1; }
                        c = (sbyte)((lonePositive ? k_crossingLonePositive : k_crossingLoneNegative) + lone);
                    }
                    if (cls != null) cls[g] = c;
                    g++;
                }
                if (posCount != null) { posCount[r] = pc; negCount[r] = nc; }
                T += tris;
            }
            return true;
        }

        // ------------------------------------------------------------------ capacity

        /// <summary>
        /// The reservation figures for <paramref name="input"/> from its size alone: the triangle count its ranges
        /// describe, and figures for an estimated crossing count (<see cref="CrossingsPerRoot"/>). **Nothing of the
        /// geometry is read** -- no vertex, no index, no triangle -- so this costs the ranges and nothing more. It is
        /// what the product reserves from (DESIGN 6.1).
        /// <para>
        /// The figures have the same meaning as <see cref="QueryCapacity"/>'s and are computed by the same function;
        /// what differs is that K is guessed rather than counted, so they are neither bounds nor exact.
        /// <see cref="MeshCutCapacity.crossingTriangles"/> and <see cref="MeshCutCapacity.wholeMeshSide"/> are left
        /// unset for that reason: whether the plane misses the geometry is not knowable without looking at it, and the
        /// run itself says so. A run given too little fails before writing outside its reservation and reports what it
        /// needed, which the caller's retry rule uses to reserve again -- possibly more than once.
        /// </para>
        /// </summary>
        [BurstCompile]
        public static void EstimateCapacity(in MeshCutInput input, ref MeshCutCapacity cap)
        {
            cap = default;
            if (input.rangeCount <= 0 || input.ranges == null) { cap.invalidInput = 1; return; }
            int N = input.topology.topologyVertexCount;
            bool rangesValid = TryCountTriangles(in input, out int T);
            cap.triangleCount = T;
            // even an invalid input gets the classification scratch, so a run reports InvalidInput rather than CapacityScratch
            if (Fits(PhaseABytes(T, input.rangeCount, N), out int classification)) { cap.scratchBytes = classification; }
            if (!rangesValid || T <= 0 || input.vertices == null) { cap.invalidInput = 1; return; }
            if (!ReservationFigures(T, EstimatedCrossings(T), input.rangeCount, N, ref cap)) { cap.capacityOverflow = 1; }
        }

        /// <summary>
        /// Reservation figures for <paramref name="input"/> with the crossing count **counted** (DESIGN 6.1
        /// "容量照会"): the classification pass runs without scratch, every corner's distance evaluated and no
        /// memo. What is measured is the crossing count: triangleCount and crossingTriangles are exact. **The figures
        /// built from it are not.** newVertices and newIndices bound that counted crossing count and then add an
        /// *estimate* of the cap auxiliary vertices; scratchBytes is exact for the classification and crossing parts
        /// and adds an *estimate* of the per-cycle cap arena. A run from these figures can still fall short, for the
        /// same reasons an estimated crossing count can.
        /// <para>
        /// **This is not the product's capacity path** and no product code calls it: a pass over every triangle before
        /// the cut is what <see cref="EstimateCapacity"/> exists to avoid. It is kept for callers that want the exact
        /// figures for their own reasons -- the verification harness sizing a probe, and the measurement that compares
        /// the two.
        /// </para>
        /// </summary>
        [BurstCompile]
        public static void QueryCapacity(in MeshCutInput input, ref MeshCutCapacity cap)
        {
            cap = default;
            var pb = new Bounds(); var nb = new Bounds();
            if (input.rangeCount <= 0 || input.ranges == null) { cap.invalidInput = 1; return; }
            int N = input.topology.topologyVertexCount;
            bool rangesValid = TryCountTriangles(in input, out int T);
            cap.triangleCount = T;
            // even an invalid input gets the classification scratch, so a run reports InvalidInput rather than CapacityScratch
            bool classificationFits = Fits(PhaseABytes(T, input.rangeCount, N), out int classification);
            if (classificationFits) { cap.scratchBytes = classification; }
            if (!rangesValid || T <= 0 || input.vertices == null || !Classify(in input, null, null, null, null, null, out _, out int K, out bool anyPos, out bool anyNeg, ref pb, ref nb)) { cap.invalidInput = 1; return; }
            cap.crossingTriangles = K;
            if (!anyNeg || !anyPos)
            {
                cap.wholeMeshSide = (sbyte)(anyNeg ? -1 : 1);
                if (!classificationFits) { cap.capacityOverflow = 1; }
                return;
            }

            if (!ReservationFigures(T, K, input.rangeCount, N, ref cap)) { cap.capacityOverflow = 1; }
        }

        // ------------------------------------------------------------------ execute

        /// <summary>
        /// Executes one cut. On any status other than Ok the outputs are invalid (they may have been partially written
        /// inside the reserved ranges but never outside them) and the capacity statuses carry the requirement.
        /// </summary>
        [BurstCompile]
        public static void Execute(in MeshCutInput input, in MeshCutOutput output, ref MeshCutResult result)
        {
            result = default;
            MarkManaged(ref result);
            if (input.rangeCount <= 0 || input.ranges == null || output.outputRanges == null) { result.status = MeshCutStatus.InvalidInput; return; }

            // Counted in 64 bits and judged before anything is laid out: a total that does not fit an int is not a
            // count, whichever way it would have wrapped.
            if (!TryCountTriangles(in input, out int T) || T <= 0 || input.vertices == null)
            { result.status = MeshCutStatus.InvalidInput; return; }

            int N = input.topology.topologyVertexCount;
            if (!Fits(PhaseABytes(T, input.rangeCount, N), out int phaseA))
            {
                // No scratch of any size would do. TooLarge is what TryAdvance reads a negative requirement as.
                result.status = MeshCutStatus.CapacityScratch;
                result.recommendedScratchBytes = TooLarge;
                return;
            }

            if (output.scratch == null || output.scratchBytes < phaseA)
            {
                result.status = MeshCutStatus.CapacityScratch;
                result.recommendedScratchBytes = Fits(phaseA + ArenaEstimate(0), out int recommended) ? recommended : TooLarge;
                return;
            }
            var l = new Layout();
            LayoutPhaseA(output.scratch, T, input.rangeCount, N, ref l);

            var posBounds = new Bounds(); var negBounds = new Bounds();
            if (!Classify(in input, l.cls, l.posCount, l.negCount, l.dist, l.sideMemo, out _, out int K, out bool anyPos, out bool anyNeg, ref posBounds, ref negBounds))
            { result.status = MeshCutStatus.InvalidInput; return; }
            result.triangleCount = T;
            result.crossingTriangles = K;
            result.newTopologyVertexCount = input.topology.topologyVertexCount;
            result.usedScratchBytes = phaseA;
            if (K == 0 && output.nodeEdgeKeys != null && output.nodeParams != null) result.nodeCorrespondenceWritten = 1;

            // ---- whole mesh on one side: the input geometry is reused unchanged, nothing is written (DESIGN 4.5.6).
            if (!anyNeg || !anyPos)
            {
                bool positive = !anyNeg;
                uint first = input.ranges[0].indexStart;
                for (int r = 0; r < input.rangeCount; r++)
                {
                    output.outputRanges[positive ? r : input.rangeCount + r] = input.ranges[r];
                    output.outputRanges[positive ? input.rangeCount + r : r] = default;
                }
                ref MeshCutSideResult side = ref (positive ? ref result.positive : ref result.negative);
                side.reusesInput = 1;
                side.indexStart = first;
                side.indexCount = 3 * T;
                Bounds b = positive ? posBounds : negBounds;
                side.boundsMin = b.min; side.boundsMax = b.max;
                result.status = MeshCutStatus.Ok;
                return;
            }

            // ---- crossing-dependent scratch
            int nodeCount = 0, crossingCount = 0, segPos = 0, segNeg = 0, recordCount = 0;
            byte scratchOverflow = 0;
            if (K > 0)
            {
                long phaseB = PhaseBBytes(K, out _, out _);
                const int minLeftover = 4096;
                if (!Fits(phaseA + phaseB + minLeftover, out int leastUsable) || output.scratchBytes < leastUsable)
                {
                    result.status = MeshCutStatus.CapacityScratch;
                    result.recommendedScratchBytes =
                        Fits(phaseA + phaseB + Align16(4L * AuxEstimate(K) * sizeof(NewVertex) + ArenaEstimate(K) * 4 / 3), out int recommended)
                            ? recommended
                            : TooLarge;
                    return;
                }
                LayoutPhaseB(output.scratch, output.scratchBytes, K, ref l);
                result.usedScratchBytes = l.fixedBytes;
                for (int i = 0; i <= l.mapMask; i++) l.mapKeys[i] = long.MinValue;

                // ---- pass 2: intersection nodes, per-side render slots, cut segments (crossing triangles only)
                int lastRange = -1;
                int g = 0;
                for (int r = 0; r < input.rangeCount; r++)
                {
                    uint* idx = input.indices + input.ranges[r].indexStart;
                    int tris = input.ranges[r].indexCount / 3;
                    for (int t = 0; t < tris; t++, g++)
                    {
                        sbyte c = l.cls[g];
                        if (c < k_crossingLoneNegative) continue;
                        bool lonePositive = c >= k_crossingLonePositive;
                        int lone = c - (lonePositive ? k_crossingLonePositive : k_crossingLoneNegative);
                        uint ia, ib, ic;
                        RotateCorners(idx + 3 * t, lone, out ia, out ib, out ic);
                        int ta = TopologyOf(in input.topology, ia, ref lastRange);
                        int tb = TopologyOf(in input.topology, ib, ref lastRange);
                        int tc = TopologyOf(in input.topology, ic, ref lastRange);
                        if (ta < 0 || tb < 0 || tc < 0) { result.status = MeshCutStatus.InvalidInput; return; }
                        // the distances decided once per topology vertex in the classification pass
                        float da = l.dist[ta], db = l.dist[tb], dc = l.dist[tc];

                        int p = NodeOfEdge(in input, ref l, ta, tb, ia, ib, da, db, ref nodeCount);
                        int q = NodeOfEdge(in input, ref l, tc, ta, ic, ia, dc, da, ref nodeCount);
                        int slotP = Slot(ref l, p, ta, ia, tb, ib, ref recordCount, ref scratchOverflow);
                        int slotQ = Slot(ref l, q, tc, ic, ta, ia, ref recordCount, ref scratchOverflow);
                        l.crossings[crossingCount++] = new Crossing { nodeP = p, nodeQ = q, slotP = slotP, slotQ = slotQ };
                        // Lone side keeps (a, P, Q) and walks P -> Q; the other side's quad walks Q -> P.
                        if (lonePositive)
                        {
                            l.segFromPos[segPos] = p; l.segToPos[segPos] = q; segPos++;
                            l.segFromNeg[segNeg] = q; l.segToNeg[segNeg] = p; segNeg++;
                        }
                        else
                        {
                            l.segFromNeg[segNeg] = p; l.segToNeg[segNeg] = q; segNeg++;
                            l.segFromPos[segPos] = q; l.segToPos[segPos] = p; segPos++;
                        }
                    }
                }
                result.nodeCount = nodeCount;
                result.interpolatedVertices = recordCount;
                for (int n = 0; n < nodeCount; n++)
                {
                    l.nodePos[n] = l.nodes[n].position;
                    posBounds.Add(l.nodes[n].position);
                    negBounds.Add(l.nodes[n].position);
                }
                if (output.nodeEdgeKeys != null && output.nodeParams != null && nodeCount <= output.nodeCapacity)
                {
                    for (int n = 0; n < nodeCount; n++) { output.nodeEdgeKeys[n] = l.nodes[n].key; output.nodeParams[n] = l.nodes[n].f; }
                    result.nodeCorrespondenceWritten = 1;
                }
            }

            // ---- index placement (DESIGN 4.5.6): surfaces are counted, caps follow each side's surface.
            int posSurf = 0, negSurf = 0;
            for (int r = 0; r < input.rangeCount; r++) { posSurf += l.posCount[r]; negSurf += l.negCount[r]; }
            int posCapTris = 0, negCapTris = 0, auxTotal = 0, auxRenderRecords = 0;
            byte indexOverflow = 0;

            if (K > 0)
            {
                // Plane basis for the caps (the same seed axis and cross products as the probe's CapContext.Build).
                float3 nrm = input.plane.xyz;
                float3 seed = math.abs(nrm.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
                float3 axisU = math.cross(seed, nrm);
                axisU *= 1f / math.length(axisU);
                float3 axisV = math.cross(nrm, axisU);
                for (int n = 0; n < nodeCount; n++)
                {
                    l.U[n] = math.dot(l.nodePos[n], axisU);
                    l.V[n] = math.dot(l.nodePos[n], axisV);
                }
                float3 ext = math.max(posBounds.max, negBounds.max) - math.min(posBounds.min, negBounds.min);
                float eps = NearContactEpsilonRelative * math.cmax(ext);
                var context = new CapContext { U = l.U, V = l.V, Pos = l.nodePos, Eps = eps, HashThreshold = MeshCutCap.DefaultHashThreshold };

                for (int side = 0; side < 2; side++)
                {
                    bool positive = side == 0;
                    int segCount = positive ? segPos : segNeg;
                    int* segFrom = positive ? l.segFromPos : l.segFromNeg;
                    int* segTo = positive ? l.segToPos : l.segToNeg;
                    int contourCount = BuildContours(ref l, nodeCount, segFrom, segTo, segCount, out int loops, out int opens);
                    if (positive) { result.loopCount = loops; result.openContourCount = opens; }

                    int capBase = positive ? 3 * posSurf : 3 * (posSurf + posCapTris + negSurf);
                    int capCursor = 0;
                    for (int c = 0; c < contourCount; c++)
                    {
                        if (l.contourClosed[c] == 0) continue;
                        int start = l.contourStart[c], end = l.contourStart[c + 1];
                        int k = end - start;
                        if (k == 2) { result.capCyclesClosedBySurface++; continue; }
                        l.arena.Reset();
                        int* cycle = l.arena.Take<int>(k);
                        if (l.arena.overflow != 0) { scratchOverflow = 1; break; }
                        cycle[0] = l.contourNodes[start];
                        for (int i = 1; i < k; i++) cycle[i] = l.contourNodes[start + k - i];

                        var cap = new CapOut();
                        MeshCutCap.SplitCap(in context, cycle, k, ref cap, ref l.arena);
                        if (l.arena.overflow != 0 || cap.failure == 3) { scratchOverflow = 1; break; }
                        if (cap.failure != 0)
                        {
                            // The split's disk relation is the construction's own invariant, not a validator of the output:
                            // violated, the run stops here and nothing is regenerated.
                            result.status = MeshCutStatus.InternalError;
                            return;
                        }
                        if (cap.method == 1) result.capSplitCycles++;
                        else if (cap.method == 2) result.capCombinatorialCycles++;

                        // The cap faces the cycle's own winding in the plane basis (the surface's directed boundary, reversed),
                        // never the plane side: a reversed input gets a reversed cap. A triangle whose projected winding opposes
                        // the cycle's (a fold of a combinatorial cap over a retraced contour) takes render vertices of its own
                        // sign, so its normal follows its winding (DESIGN 6.4) while the logical topology keeps
                        // one vertex per node; a degenerate triangle takes the cycle's sign.
                        sbyte cycleSign = (sbyte)(MeshCutCap.SignedArea2(in context, cycle, k) >= 0 ? 1 : -1);
                        int* auxRecord = l.arena.Take<int>(math.max(1, 2 * cap.auxCount));
                        if (l.arena.overflow != 0) { scratchOverflow = 1; break; }
                        for (int i = 0; i < 2 * cap.auxCount; i++) auxRecord[i] = -1;

                        for (int i = 0; i < cap.triangleCount; i += 3)
                        {
                            int c0 = cap.triangles[i], c1 = cap.triangles[i + 1], c2 = cap.triangles[i + 2];
                            CapUv(in context, cycle, k, in cap, c0, out double u0, out double v0);
                            CapUv(in context, cycle, k, in cap, c1, out double u1, out double v1);
                            CapUv(in context, cycle, k, in cap, c2, out double u2, out double v2);
                            double area2 = (u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0);
                            sbyte sign = area2 > 0 ? (sbyte)1 : area2 < 0 ? (sbyte)-1 : cycleSign;
                            if (sign != cycleSign) result.capReversedTriangles++;
                            uint g0 = CapVertex(ref l, cycle, k, in cap, c0, auxRecord, auxTotal, positive, sign, output.newVertexBase, ref recordCount, ref auxRenderRecords, ref scratchOverflow);
                            uint g1 = CapVertex(ref l, cycle, k, in cap, c1, auxRecord, auxTotal, positive, sign, output.newVertexBase, ref recordCount, ref auxRenderRecords, ref scratchOverflow);
                            uint g2 = CapVertex(ref l, cycle, k, in cap, c2, auxRecord, auxTotal, positive, sign, output.newVertexBase, ref recordCount, ref auxRenderRecords, ref scratchOverflow);
                            int at = capBase + capCursor;
                            if (at + 3 <= output.newIndexCapacity && output.newIndices != null)
                            {
                                output.newIndices[at] = g0; output.newIndices[at + 1] = g1; output.newIndices[at + 2] = g2;
                            }
                            else indexOverflow = 1;
                            capCursor += 3;
                        }
                        auxTotal += cap.auxCount;
                        result.capTriangles += cap.triangleCount / 3;
                        result.capDegenerateTriangles += cap.degenerate;
                        result.capStalledEars += cap.stalled;
                        result.capCrossings += cap.crossings;
                        result.capContacts += cap.contacts;
                        result.capAuxVertices += cap.auxCount;
                        if (scratchOverflow != 0) break;
                    }
                    if (scratchOverflow != 0) break;
                    if (positive) posCapTris = capCursor / 3; else negCapTris = capCursor / 3;
                }
                result.usedScratchBytes = l.fixedBytes + l.arena.peak;
                result.capRenderVertices = recordCount - result.interpolatedVertices - auxRenderRecords;
            }

            // ---- capacity verdicts, before any vertex is materialized (nothing outside a reservation was written)
            int newVertexCount = recordCount;
            int n0 = 3 * (posSurf + posCapTris), n1 = 3 * (negSurf + negCapTris);
            result.newVertexCount = newVertexCount;
            result.newIndexCount = n0 + n1;
            if (scratchOverflow != 0)
            {
                result.status = MeshCutStatus.CapacityScratch;
                result.recommendedScratchBytes =
                    Fits(l.fixedBytes + 2L * math.max((long)output.scratchBytes - l.fixedBytes, ArenaEstimate(K)), out int grown)
                        ? grown
                        : TooLarge;
                return;
            }
            if (newVertexCount > output.newVertexCapacity || (newVertexCount > 0 && (output.newVertices == null || output.newVertexTopology == null)))
            {
                result.status = MeshCutStatus.CapacityVertex;
                result.requiredVertexCapacity = newVertexCount;
                result.requiredIndexCapacity = n0 + n1;
                return;
            }
            if (indexOverflow != 0 || n0 + n1 > output.newIndexCapacity || output.newIndices == null)
            {
                result.status = MeshCutStatus.CapacityIndex;
                result.requiredVertexCapacity = newVertexCount;
                result.requiredIndexCapacity = n0 + n1;
                return;
            }

            // ---- materialize the new render vertices and their topology ids
            int topoBase = input.topology.topologyVertexCount;
            if (K > 0)
            {
                float3 nrm = input.plane.xyz;
                for (int i = 0; i < newVertexCount; i++)
                {
                    NewVertex rec = l.records[i];
                    VpRenderVertex v;
                    if (rec.kind == 0)
                    {
                        VpRenderVertex a = input.vertices[rec.rLo], b = input.vertices[rec.rHi];
                        float f = rec.f, w = 1f - f;
                        float3 aNormal = a.normal, bNormal = b.normal;
                        float2 aUv = a.uv0, bUv = b.uv0;
                        v.position = l.nodes[rec.node].position;
                        v.normal = Renormalize(w * aNormal + f * bNormal, new float3(0, 1, 0));
                        v.uv0 = w * aUv + f * bUv;
                        output.newVertexTopology[i] = topoBase + rec.node;
                    }
                    else
                    {
                        // normal along the winding of the triangles that use this vertex, so each triangle presents the
                        // face of its own winding (DESIGN 6.4)
                        v.position = rec.kind == 1 ? l.nodes[rec.node].position : rec.position;
                        v.normal = rec.sign >= 0 ? nrm : -nrm;
                        v.uv0 = RenderCutMarker.CapUv;
                        output.newVertexTopology[i] = rec.kind == 1 ? topoBase + rec.node : topoBase + nodeCount + rec.node;
                    }
                    output.newVertices[i] = v;
                }
                result.newTopologyVertexCount = topoBase + nodeCount + auxTotal;
            }

            // ---- pass 3: surface indices per submesh, positive then negative, crossing triangles clipped in place
            {
                int posCur = 0, negCur = n0;
                int g = 0, cross = 0;
                uint vbase = output.newVertexBase;
                for (int r = 0; r < input.rangeCount; r++)
                {
                    int posStart = posCur, negStart = negCur;
                    uint* idx = input.indices + input.ranges[r].indexStart;
                    int tris = input.ranges[r].indexCount / 3;
                    for (int t = 0; t < tris; t++, g++)
                    {
                        sbyte c = l.cls[g];
                        uint* src = idx + 3 * t;
                        if (c == 1)
                        {
                            output.newIndices[posCur] = src[0]; output.newIndices[posCur + 1] = src[1]; output.newIndices[posCur + 2] = src[2];
                            posCur += 3;
                            continue;
                        }
                        if (c == -1)
                        {
                            output.newIndices[negCur] = src[0]; output.newIndices[negCur + 1] = src[1]; output.newIndices[negCur + 2] = src[2];
                            negCur += 3;
                            continue;
                        }
                        bool lonePositive = c >= k_crossingLonePositive;
                        RotateCorners(src, c - (lonePositive ? k_crossingLonePositive : k_crossingLoneNegative), out uint ia, out uint ib, out uint ic);
                        Crossing cr = l.crossings[cross++];
                        uint gp = vbase + (uint)cr.slotP, gq = vbase + (uint)cr.slotQ;
                        int lc = lonePositive ? posCur : negCur;
                        int oc = lonePositive ? negCur : posCur;
                        output.newIndices[lc] = ia; output.newIndices[lc + 1] = gp; output.newIndices[lc + 2] = gq;
                        output.newIndices[oc] = gp; output.newIndices[oc + 1] = ib; output.newIndices[oc + 2] = ic;
                        output.newIndices[oc + 3] = gp; output.newIndices[oc + 4] = ic; output.newIndices[oc + 5] = gq;
                        if (lonePositive) { posCur += 3; negCur += 6; } else { negCur += 3; posCur += 6; }
                    }
                    output.outputRanges[r] = new MeshCutIndexRange { indexStart = output.newIndexBase + (uint)posStart, indexCount = posCur - posStart };
                    output.outputRanges[input.rangeCount + r] = new MeshCutIndexRange { indexStart = output.newIndexBase + (uint)negStart, indexCount = negCur - negStart };
                }
                // Caps join the side's last non-empty submesh range (DESIGN 6.1: no cap-only draw range).
                if (posCapTris > 0) ExtendLastNonEmpty(output.outputRanges, 0, input.rangeCount, 3 * posCapTris);
                if (negCapTris > 0) ExtendLastNonEmpty(output.outputRanges, input.rangeCount, input.rangeCount, 3 * negCapTris);
            }

            result.positive.indexStart = output.newIndexBase;
            result.positive.indexCount = n0;
            result.positive.boundsMin = posBounds.min; result.positive.boundsMax = posBounds.max;
            result.negative.indexStart = output.newIndexBase + (uint)n0;
            result.negative.indexCount = n1;
            result.negative.boundsMin = negBounds.min; result.negative.boundsMax = negBounds.max;
            result.status = MeshCutStatus.Ok;
        }

        static void ExtendLastNonEmpty(MeshCutIndexRange* ranges, int first, int count, int extra)
        {
            for (int r = first + count - 1; r >= first; r--)
            {
                if (ranges[r].indexCount == 0) continue;
                ranges[r].indexCount += extra;
                return;
            }
        }

        static void RotateCorners(uint* src, int lone, out uint a, out uint b, out uint c)
        {
            uint v0 = src[0], v1 = src[1], v2 = src[2];
            if (lone == 0) { a = v0; b = v1; c = v2; }
            else if (lone == 1) { a = v1; b = v2; c = v0; }
            else { a = v2; b = v0; c = v1; }
        }

        static float3 Renormalize(float3 v, float3 fallback)
        {
            float l2 = math.dot(v, v);
            if (l2 <= 1e-20f) return fallback;
            return v * (1f / (float)Math.Sqrt(l2));
        }

        static int FindSlot(ref Layout l, long key, out bool found)
        {
            int slot = (int)(Mix(0x9E3779B9ul, (ulong)key) & (ulong)l.mapMask);
            while (l.mapKeys[slot] != long.MinValue)
            {
                if (l.mapKeys[slot] == key) { found = true; return slot; }
                slot = (slot + 1) & l.mapMask;
            }
            found = false;
            return slot;
        }

        /// <summary>The node of topology edge (tu, tv): one per edge, positioned once from the lower-id endpoint's canonical position.</summary>
        static int NodeOfEdge(in MeshCutInput input, ref Layout l, int tu, int tv, uint ru, uint rv, float du, float dv, ref int nodeCount)
        {
            long key = EdgeKey(tu, tv);
            int slot = FindSlot(ref l, key, out bool found);
            if (found) return l.mapValues[slot];
            uint rLo = ru, rHi = rv; float dLo = du, dHi = dv;
            if (tu > tv) { rLo = rv; rHi = ru; dLo = dv; dHi = du; }
            double param = dLo / (double)(dLo - dHi);
            float f = (float)param;
            int id = nodeCount++;
            l.mapKeys[slot] = key;
            l.mapValues[slot] = id;
            float3 pLo = input.vertices[rLo].position, pHi = input.vertices[rHi].position;
            l.nodes[id] = new Node
            {
                key = key, f = f, rLo = rLo, rHi = rHi,
                position = (1f - f) * pLo + f * pHi,
                slot0 = -1, slot1 = -1, capPosPlus = -1, capPosMinus = -1, capNegPlus = -1, capNegMinus = -1,
            };
            return id;
        }

        static int AddRecord(ref Layout l, NewVertex rec, ref int recordCount, ref byte overflow)
        {
            int id = recordCount++;
            if (id < l.recordCap) l.records[id] = rec; else overflow = 1;
            return id;
        }

        /// <summary>
        /// The new render vertex of node <paramref name="node"/> for one face side, keyed by the face's render pair on the
        /// crossed edge (ordered by topology id). A manifold edge has at most two incident faces, so two slots per node.
        /// </summary>
        static int Slot(ref Layout l, int node, int tu, uint ru, int tv, uint rv, ref int recordCount, ref byte overflow)
        {
            if (tu > tv) { uint tmp = ru; ru = rv; rv = tmp; }
            long pair = ((long)ru << 32) | rv;
            ref Node n = ref l.nodes[node];
            if (n.slot0 >= 0 && n.pair0 == pair) return n.slot0;
            if (n.slot1 >= 0 && n.pair1 == pair) return n.slot1;
            int slot = AddRecord(ref l, new NewVertex { kind = 0, node = node, rLo = ru, rHi = rv, f = n.f }, ref recordCount, ref overflow);
            if (n.slot0 < 0) { n.slot0 = slot; n.pair0 = pair; }
            else { n.slot1 = slot; n.pair1 = pair; }
            return slot;
        }

        /// <summary>
        /// Global vertex number of a cap triangle corner: the node's cap render vertex for this side and winding sign, or
        /// the auxiliary vertex's render vertex for the sign. Created on first use; both signs of one node or auxiliary
        /// vertex share its topology id.
        /// </summary>
        static uint CapVertex(ref Layout l, int* cycle, int k, in CapOut cap, int local, int* auxRecord, int auxTotal, bool positive, sbyte sign, uint vbase,
                              ref int recordCount, ref int auxRenderRecords, ref byte overflow)
        {
            if (local >= k)
            {
                int a = local - k;
                int slot = 2 * a + (sign > 0 ? 0 : 1);
                if (auxRecord[slot] < 0)
                {
                    auxRecord[slot] = AddRecord(ref l, new NewVertex { kind = 2, sign = sign, node = auxTotal + a, position = cap.auxPos[a] }, ref recordCount, ref overflow);
                    auxRenderRecords++;
                }
                return vbase + (uint)auxRecord[slot];
            }
            int node = cycle[local];
            ref Node n = ref l.nodes[node];
            int id = positive ? (sign > 0 ? n.capPosPlus : n.capPosMinus) : (sign > 0 ? n.capNegPlus : n.capNegMinus);
            if (id < 0)
            {
                id = AddRecord(ref l, new NewVertex { kind = 1, node = node, sign = sign }, ref recordCount, ref overflow);
                if (positive) { if (sign > 0) n.capPosPlus = id; else n.capPosMinus = id; }
                else { if (sign > 0) n.capNegPlus = id; else n.capNegMinus = id; }
            }
            return vbase + (uint)id;
        }

        static void CapUv(in CapContext c, int* cycle, int k, in CapOut cap, int local, out double u, out double v)
        {
            if (local < k) { u = c.U[cycle[local]]; v = c.V[cycle[local]]; return; }
            u = cap.auxU[local - k]; v = cap.auxV[local - k];
        }

        /// <summary>
        /// Chains one side's directed cut segments by node identity alone (positions are never consulted, so sheets that
        /// meet in space but not in topology stay separate). Open chains first, then cycles. Returns the contour count;
        /// contours are laid out in contourNodes / contourStart / contourClosed.
        /// </summary>
        static int BuildContours(ref Layout l, int nodeCount, int* segFrom, int* segTo, int segCount, out int loops, out int opens)
        {
            loops = 0; opens = 0;
            for (int i = 0; i < nodeCount; i++) { l.next[i] = -1; l.prev[i] = -1; l.used[i] = 0; }
            for (int s = 0; s < segCount; s++)
            {
                l.next[segFrom[s]] = segTo[s];
                l.prev[segTo[s]] = segFrom[s];
            }
            int contourCount = 0, listCount = 0;
            l.contourStart[0] = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int n = 0; n < nodeCount; n++)
                {
                    if (l.used[n] != 0 || l.next[n] < 0) continue;
                    if (pass == 0 && l.prev[n] >= 0) continue;
                    int cur = n;
                    while (cur >= 0 && l.used[cur] == 0)
                    {
                        l.used[cur] = 1;
                        l.contourNodes[listCount++] = cur;
                        cur = l.next[cur];
                    }
                    l.contourStart[++contourCount] = listCount;
                    l.contourClosed[contourCount - 1] = (byte)(pass == 1 ? 1 : 0);
                    if (pass == 0) opens++; else loops++;
                }
            }
            return contourCount;
        }
    }
}
