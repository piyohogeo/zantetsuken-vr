using System;
using Unity.Burst;
using Unity.Mathematics;

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
    /// surface) with the plane normal signed by the cap's own winding, the fixed cap UV marker of 5.3 and the plane
    /// U axis as tangent.
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
            public int capVertexPos, capVertexNeg;
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

        static int Align16(int bytes) => (bytes + 15) / 16 * 16;

        /// <summary>Bytes of the classification part (needed by every run, including a whole-mesh reuse): triangle classes, per-range counts, the per-topology-vertex distance memo.</summary>
        static int PhaseABytes(int T, int R, int N) => Align16(T) + Align16(4 * R) + Align16(4 * R) + Align16(4 * math.max(1, N)) + Align16(math.max(1, N));

        /// <summary>Bytes of the crossing-dependent fixed part (edge map, nodes, segments, contours, projection).</summary>
        static int PhaseBBytes(int K, out int nodeCap, out int mapCap)
        {
            nodeCap = math.max(1, 2 * K);
            mapCap = 16;
            while (mapCap < nodeCap * 4) mapCap <<= 1;
            int bytes = 0;
            bytes += Align16(mapCap * 8) + Align16(mapCap * 4);
            bytes += Align16(nodeCap * sizeof(Node));
            bytes += Align16(math.max(1, K) * sizeof(Crossing));
            bytes += 4 * Align16(math.max(1, K) * 4);
            bytes += 2 * Align16(nodeCap * 4) + Align16(nodeCap);
            bytes += Align16(nodeCap * 4) + Align16((nodeCap + 1) * 4) + Align16(nodeCap);
            bytes += 2 * Align16(nodeCap * 4) + Align16(nodeCap * 12);
            bytes += Align16(4 * nodeCap * sizeof(NewVertex));   // interpolated slots and cap render vertices (2 per node each)
            return bytes;
        }

        static int ArenaEstimate(int K) => 256 * math.max(1, 2 * K) + 32 * 1024;
        static int AuxEstimate(int K) => math.max(32, K / 4);

        static void LayoutPhaseA(byte* scratch, int T, int R, int N, ref Layout l)
        {
            int off = 0;
            l.cls = (sbyte*)(scratch + off); off += Align16(T);
            l.posCount = (int*)(scratch + off); off += Align16(4 * R);
            l.negCount = (int*)(scratch + off); off += Align16(4 * R);
            l.dist = (float*)(scratch + off); off += Align16(4 * math.max(1, N));
            l.sideMemo = (sbyte*)(scratch + off); off += Align16(math.max(1, N));
            l.fixedBytes = off;
        }

        static void LayoutPhaseB(byte* scratch, int scratchBytes, int K, ref Layout l)
        {
            int off = l.fixedBytes;
            PhaseBBytes(K, out int nodeCap, out int mapCap);
            l.nodeCap = nodeCap; l.mapMask = mapCap - 1;
            l.mapKeys = (long*)(scratch + off); off += Align16(mapCap * 8);
            l.mapValues = (int*)(scratch + off); off += Align16(mapCap * 4);
            l.nodes = (Node*)(scratch + off); off += Align16(nodeCap * sizeof(Node));
            l.crossings = (Crossing*)(scratch + off); off += Align16(math.max(1, K) * sizeof(Crossing));
            l.segFromPos = (int*)(scratch + off); off += Align16(math.max(1, K) * 4);
            l.segToPos = (int*)(scratch + off); off += Align16(math.max(1, K) * 4);
            l.segFromNeg = (int*)(scratch + off); off += Align16(math.max(1, K) * 4);
            l.segToNeg = (int*)(scratch + off); off += Align16(math.max(1, K) * 4);
            l.next = (int*)(scratch + off); off += Align16(nodeCap * 4);
            l.prev = (int*)(scratch + off); off += Align16(nodeCap * 4);
            l.used = scratch + off; off += Align16(nodeCap);
            l.contourNodes = (int*)(scratch + off); off += Align16(nodeCap * 4);
            l.contourStart = (int*)(scratch + off); off += Align16((nodeCap + 1) * 4);
            l.contourClosed = scratch + off; off += Align16(nodeCap);
            l.U = (float*)(scratch + off); off += Align16(nodeCap * 4);
            l.V = (float*)(scratch + off); off += Align16(nodeCap * 4);
            l.nodePos = (float3*)(scratch + off); off += Align16(nodeCap * 12);
            l.records = (NewVertex*)(scratch + off);
            int fixedRecords = 4 * nodeCap;
            off += Align16(fixedRecords * sizeof(NewVertex));
            l.fixedBytes = off;
            // Whatever is left: a quarter for auxiliary vertex records, the rest for the per-cycle cap arena. Both scale
            // with the scratch size, so a larger reservation on the next attempt admits more contacts.
            int leftover = math.max(0, scratchBytes - off);
            int auxRecords = leftover / 4 / sizeof(NewVertex);
            l.recordCap = fixedRecords + auxRecords;
            off += Align16(auxRecords * sizeof(NewVertex));
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
        /// Reservation estimate for <paramref name="input"/> (DESIGN 6.1 "容量照会"). Runs the classification pass
        /// without scratch: the triangle and crossing counts are exact; nodes, slots and cap render vertices are bounded
        /// by them; cap auxiliary vertices are estimated and reported exactly by a run that exceeds the estimate.
        /// </summary>
        [BurstCompile]
        public static void QueryCapacity(in MeshCutInput input, ref MeshCutCapacity cap)
        {
            cap = default;
            var pb = new Bounds(); var nb = new Bounds();
            if (input.rangeCount <= 0 || input.ranges == null) { cap.invalidInput = 1; return; }
            int N = input.topology.topologyVertexCount;
            int T = 0;
            bool rangesValid = input.indices != null;
            for (int r = 0; r < input.rangeCount; r++)
            {
                MeshCutIndexRange range = input.ranges[r];
                if (range.indexCount < 0 || range.indexCount % 3 != 0 || (long)range.indexStart + range.indexCount > input.indexViewLength) rangesValid = false;
                T += math.max(0, range.indexCount) / 3;
            }
            cap.triangleCount = T;
            // even an invalid input gets the classification scratch, so a run reports InvalidInput rather than CapacityScratch
            cap.scratchBytes = PhaseABytes(T, input.rangeCount, N);
            if (!rangesValid || T <= 0 || input.vertices == null || !Classify(in input, null, null, null, null, null, out _, out int K, out bool anyPos, out bool anyNeg, ref pb, ref nb)) { cap.invalidInput = 1; return; }
            cap.crossingTriangles = K;
            int phaseA = PhaseABytes(T, input.rangeCount, N);
            if (!anyNeg || !anyPos)
            {
                cap.wholeMeshSide = (sbyte)(anyNeg ? -1 : 1);
                cap.scratchBytes = phaseA;
                return;
            }
            if (K == 0)
            {
                cap.newIndices = 3 * T;
                cap.scratchBytes = phaseA;
                return;
            }
            int aux = AuxEstimate(K);
            cap.newVertices = 8 * K + aux;
            cap.newIndices = 3 * (T + 6 * K + 4 * aux);
            int phaseB = PhaseBBytes(K, out _, out _);
            int leftover = 4 * aux * sizeof(NewVertex) + ArenaEstimate(K) * 4 / 3;
            cap.scratchBytes = phaseA + phaseB + Align16(leftover);
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

            int T = 0;
            for (int r = 0; r < input.rangeCount; r++)
            {
                MeshCutIndexRange range = input.ranges[r];
                if (range.indexCount < 0 || range.indexCount % 3 != 0 || input.indices == null || (long)range.indexStart + range.indexCount > input.indexViewLength)
                { result.status = MeshCutStatus.InvalidInput; return; }
                T += range.indexCount / 3;
            }
            if (T <= 0 || input.vertices == null) { result.status = MeshCutStatus.InvalidInput; return; }

            int N = input.topology.topologyVertexCount;
            int phaseA = PhaseABytes(T, input.rangeCount, N);
            if (output.scratch == null || output.scratchBytes < phaseA)
            {
                result.status = MeshCutStatus.CapacityScratch;
                result.requiredScratchBytes = phaseA + ArenaEstimate(0);
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
                int phaseB = PhaseBBytes(K, out _, out _);
                int minLeftover = 4096;
                if (output.scratchBytes < phaseA + phaseB + minLeftover)
                {
                    result.status = MeshCutStatus.CapacityScratch;
                    result.requiredScratchBytes = phaseA + phaseB + Align16(4 * AuxEstimate(K) * sizeof(NewVertex) + ArenaEstimate(K) * 4 / 3);
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
            int posCapTris = 0, negCapTris = 0, auxTotal = 0;
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
                        int splitMark = l.arena.used;
                        MeshCutCap.SplitCap(in context, cycle, k, ref cap, ref l.arena);
                        if (l.arena.overflow != 0 || cap.failure == 3) { scratchOverflow = 1; break; }
                        if (cap.failure != 0)
                        {
                            // The split could not close this cycle as a disk (a doubled region whose near-coincident nodes defeat
                            // the contact arithmetic): the fan is the combinatorially exact cap of such a cycle, so the output
                            // stays closed and manifold. Counted, never silent.
                            int crossings = cap.crossings, contacts = cap.contacts;
                            l.arena.used = splitMark;
                            MeshCutCap.FanCap(in context, cycle, k, ref cap, ref l.arena);
                            if (l.arena.overflow != 0) { scratchOverflow = 1; break; }
                            cap.crossings = crossings; cap.contacts = contacts;
                            result.capFanFallbacks++;
                        }

                        // The cap normal is the plane normal signed by the cap's own winding (area-weighted over its
                        // triangles in the plane basis), never by the plane side: a reversed input gets a reversed cap.
                        double signedArea = 0.0;
                        for (int i = 0; i < cap.triangleCount; i += 3)
                        {
                            CapUv(in context, cycle, k, in cap, cap.triangles[i], out double u0, out double v0);
                            CapUv(in context, cycle, k, in cap, cap.triangles[i + 1], out double u1, out double v1);
                            CapUv(in context, cycle, k, in cap, cap.triangles[i + 2], out double u2, out double v2);
                            signedArea += (u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0);
                        }
                        sbyte sign = (sbyte)(signedArea >= 0 ? 1 : -1);

                        int* auxRecord = l.arena.Take<int>(math.max(1, cap.auxCount));
                        if (l.arena.overflow != 0) { scratchOverflow = 1; break; }
                        for (int i = 0; i < cap.auxCount; i++)
                        {
                            auxRecord[i] = AddRecord(ref l, new NewVertex { kind = 2, sign = sign, node = auxTotal + i, position = cap.auxPos[i] }, ref recordCount, ref scratchOverflow);
                        }
                        auxTotal += cap.auxCount;

                        for (int i = 0; i < cap.triangleCount; i += 3)
                        {
                            uint g0 = CapVertex(ref l, cycle, k, in cap, cap.triangles[i], auxRecord, positive, sign, output.newVertexBase, ref recordCount, ref scratchOverflow);
                            uint g1 = CapVertex(ref l, cycle, k, in cap, cap.triangles[i + 1], auxRecord, positive, sign, output.newVertexBase, ref recordCount, ref scratchOverflow);
                            uint g2 = CapVertex(ref l, cycle, k, in cap, cap.triangles[i + 2], auxRecord, positive, sign, output.newVertexBase, ref recordCount, ref scratchOverflow);
                            int at = capBase + capCursor;
                            if (at + 3 <= output.newIndexCapacity && output.newIndices != null)
                            {
                                output.newIndices[at] = g0; output.newIndices[at + 1] = g1; output.newIndices[at + 2] = g2;
                            }
                            else indexOverflow = 1;
                            capCursor += 3;
                        }
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
                result.capRenderVertices = recordCount - result.interpolatedVertices - result.capAuxVertices;
            }

            // ---- capacity verdicts, before any vertex is materialized (nothing outside a reservation was written)
            int newVertexCount = recordCount;
            int n0 = 3 * (posSurf + posCapTris), n1 = 3 * (negSurf + negCapTris);
            result.newVertexCount = newVertexCount;
            result.newIndexCount = n0 + n1;
            if (scratchOverflow != 0)
            {
                result.status = MeshCutStatus.CapacityScratch;
                result.requiredScratchBytes = l.fixedBytes + 2 * math.max(output.scratchBytes - l.fixedBytes, ArenaEstimate(K));
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
                float3 seed = math.abs(nrm.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
                float3 axisU = math.cross(seed, nrm);
                axisU *= 1f / math.length(axisU);
                for (int i = 0; i < newVertexCount; i++)
                {
                    NewVertex rec = l.records[i];
                    RenderVertex v;
                    if (rec.kind == 0)
                    {
                        RenderVertex a = input.vertices[rec.rLo], b = input.vertices[rec.rHi];
                        float f = rec.f, w = 1f - f;
                        v.position = l.nodes[rec.node].position;
                        float3 nn = w * a.normal + f * b.normal;
                        v.normal = Renormalize(nn, new float3(0, 1, 0));
                        v.uv0 = w * a.uv0 + f * b.uv0;
                        float3 tt = w * a.tangent.xyz + f * b.tangent.xyz;
                        v.tangent = new float4(Renormalize(tt, new float3(1, 0, 0)), a.tangent.w);
                        output.newVertexTopology[i] = topoBase + rec.node;
                    }
                    else
                    {
                        v.position = rec.kind == 1 ? l.nodes[rec.node].position : rec.position;
                        v.normal = rec.sign >= 0 ? nrm : -nrm;
                        v.uv0 = RenderCutMarker.CapUv;
                        v.tangent = new float4(axisU, 1f);
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
                slot0 = -1, slot1 = -1, capVertexPos = -1, capVertexNeg = -1,
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

        /// <summary>Global vertex number of a cap triangle corner: the node's cap render vertex for this side, or an auxiliary vertex.</summary>
        static uint CapVertex(ref Layout l, int* cycle, int k, in CapOut cap, int local, int* auxRecord, bool positive, sbyte sign, uint vbase,
                              ref int recordCount, ref byte overflow)
        {
            if (local >= k) return vbase + (uint)auxRecord[local - k];
            int node = cycle[local];
            ref Node n = ref l.nodes[node];
            int id = positive ? n.capVertexPos : n.capVertexNeg;
            if (id < 0)
            {
                id = AddRecord(ref l, new NewVertex { kind = 1, node = node, sign = sign }, ref recordCount, ref overflow);
                if (positive) n.capVertexPos = id; else n.capVertexNeg = id;
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
