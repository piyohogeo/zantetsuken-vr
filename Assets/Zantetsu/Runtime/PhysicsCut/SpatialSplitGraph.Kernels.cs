using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling.LowLevel.Unsafe;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The numeric body of the graph candidate's AB (GRAPH-CONNECT-2, unit 1): the preparation (owner coordinates, the error
    /// bounds, the mesh components, the nodes' boxes and hulls, stage A's candidate pairs) and the search (the deal, the
    /// minimum cuts, the hulls' refutations, the placement), ported from the Editor's graph-candidate-native 8 (commit
    /// 7928569e) without a numeric change. Everything here is unmanaged: the bodies hold raw pointers and
    /// <see cref="UnsafeList{T}"/> pointers, so that the two entries (<see cref="RunPrepare"/>, <see cref="RunSearch"/>) are
    /// Burst direct calls -- the route a Background worker may take (the pool's contract: no Unity job is scheduled or
    /// completed off the main thread; a native container cannot be an argument of a direct call) -- and the same bodies run as
    /// managed code when called without Burst (N0). FloatMode.Strict and FloatPrecision.Standard as before.
    /// <para>
    /// **The work meter** (GRAPH-CONNECT-1 §4.3): one counter over both bodies, one unit an iteration of every loop whose length
    /// depends on the input (a vertex read, a parent reference of the union-find, a BoxGap, a sort compare or move, an arc
    /// check, an edge of a path update, a hull vertex of a support query, a GJK iteration, ...), compared with the budget every
    /// <see cref="WorkMeter.CheckEvery"/> units; past the budget the counted loops stop and the body returns Unfinished. What the
    /// meter does not cover: the allocations, the lists' growth copies and the releases (the capacity caps bound those).
    /// </para>
    /// <para>
    /// **The capacity caps** (§4.1): the node count after the components (maxNodes) and the candidate lists' growth before it
    /// happens (candidateCap: a growth that would take the capacity past it is refused, and the body returns Unfinished).
    /// </para>
    /// <para>
    /// The main thread must call <see cref="Prepare"/> once before a worker calls an entry (§1.4); the managed driver enforces it
    /// where asked (requirePrepared).
    /// </para>
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    internal static unsafe partial class SpatialSplitGraph
    {
        // ---- the stats the bodies write (one long each) ---------------------------------------------------------------------

        internal const int StatNodePairs = 0, StatApartByBoxes = 1, StatCandidatesA = 2, StatTriedB = 3, StatRefutedB = 4, StatAmbiguousB = 5, StatGjkIterations = 6,
            StatSupportQueries = 7, StatVertexScans = 8, StatCutAttempts = 9, StatCutLimitHit = 10, StatMeshComponents = 11, StatDefiniteComponents = 12, StatVerdict = 13,
            StatStop = 14, StatStage = 15, StatError = 16, StatManagedMarker = 17, StatNodes = 18,
            StatWorkPrepare = 19, StatWorkSearch = 20, StatUnfinished = 21, StatNeededCapacity = 22, StatGjkLimitHit = 23, StatCount = 24;

        internal const int VerdictUndecided = 0, VerdictSeparable = 1, VerdictNotSeparable = 2, VerdictNotFound = 3;

        // The measurement: exclusive sections of the search (ticks of ProfilerUnsafeUtility.Timestamp read only at the section
        // boundaries, measure 1) and diagnostic counts (measure 2); nothing is read with measure 0.
        internal const int SecRegroup = 0, SecSeeds = 1, SecSortNearest = 2, SecCutBuild = 3, SecCutFlow = 4, SecCutExtract = 5, SecCollect = 6, SecSortToRefute = 7,
            SecGjk = 8, SecProjection = 9, SecConclude = 10, SectionTimes = 11;
        internal const int CntProjectionCalls = 11, CntGjkCalls = 12, CntMinCutCalls = 13, CntArcsBuilt = 14, CntBfsDequeues = 15, CntAugmentations = 16,
            CntNearestCompares = 17, CntNearestMoves = 18, CntRefuteSorted = 19, CntRefuteMoves = 20, CntNearestGaps = 21, CntLevelBuilds = 22, CntArcChecks = 23, CntPushes = 24, SearchSectionCount = 25;

        // The preparation's sections (measure 1) and counts (measure 2), after the search's.
        internal const int PrepOwnerBounds = 25, PrepSlots = 26, PrepComponents = 27, PrepConvexes = 28, PrepMeshBoxesCounts = 29, PrepMeshFill = 30, PrepPairs = 31, PrepSectionTimes = 7;
        internal const int PcNamed = 32, PcTriangles = 33, PcComponents = 34, PcNodes = 35, PcHullStored = 36, PcSlotCompares = 37, PcParentRefs = 38, PcPairsTotal = 39, PcGapEvals = 40, PcApart = 41,
            PcCandidates = 42, PcCandGrowths = 43, PcCandGrowthOld = 44, PcGapGrowths = 45, PcGapGrowthOld = 46,
            PcSweepSkipped = 47, PcGapExcluded = 48, PcNodeSortCompares = 49, PcNodeSortMoves = 50, PcKeySortCompares = 51, PcKeySortMoves = 52, PcKeysSorted = 53, SectionCount = 54;

        /// <summary>Why a body stopped: the search's own reasons (a NotFound), and the unit-1 stops (Undecided, Unfinished).</summary>
        internal const int StopNone = 0, StopCutLimit = 1, StopHulls = 2, StopExhausted = 3, StopWork = 4, StopCapacity = 5, StopGrow = 6;
        internal const int ReasonAllConvexesOne = 1, ReasonAllTrianglesOne = 2, ReasonFewerThanTwo = 3;

        /// <summary>The counting definition the work meter implements (recorded with every run that carries work counts).</summary>
        public const string WorkDefinition = "work 3 (2026-10-23): work 2 and, for a span input, the compaction's scans and copies on the deciding thread (an index scanned, marked and copied: three units; a slot of the span read: one) -- one unit an iteration of every input-length loop of both bodies (a heap-sort sift step, with its compares and move, is one; a union-find walk spends one a parent reference and one for the final read, none once stopped), checked against the budget every 4096 units; past a stop at most one unit more is spent";

        /// <summary>A node's sweep key: its box's least x, ties by the node's index (a fixed, deterministic order).</summary>
        internal struct NodeKey
        {
            public double x;
            public int index;
            public bool After(NodeKey o) => x > o.x || (x == o.x && index > o.index);
        }

        /// <summary>A candidate pair in the original order (key = i * n + j, i &lt; j) with its gap computed on (i, j).</summary>
        internal struct KeyGap
        {
            public long key;
            public double gap;
        }

        internal struct EnumerateCounts
        {
            // (the lists never grow inside the kernels since the TL review of 2026-10-23: the growths are the driver's, counted there)
            public long gapEvals, sweepSkipped, excluded, nodeSortCompares, nodeSortMoves, keySortCompares, keySortMoves, keysSorted;
        }

        /// <summary>The counts of one maximum-flow computation (flow-internal: they differ between the methods).</summary>
        internal struct FlowCounts
        {
            public long dequeues, augmentations, levelBuilds, arcChecks, pushes;
        }

        /// <summary>
        /// The work meter: the units spent, the budget (0: none) and the stop. <see cref="Spend"/> compares the count with the
        /// budget whenever it crosses a multiple of <see cref="CheckEvery"/>; the counted loops read <see cref="Stopped"/> at
        /// their heads and leave, so that the work past the budget is at most CheckEvery units and one more unit: every counted loop
        /// spends exactly one unit an iteration, and nothing spends once the meter stopped. A decision whose whole work stays under
        /// the first check point is never stopped.
        /// </summary>
        internal struct WorkMeter
        {
            public const long CheckEvery = 4096;
            public long budget, work, nextCheck;
            public int stopped, reason;

            public static WorkMeter Of(long budget) => new WorkMeter { budget = budget, nextCheck = CheckEvery };

            /// <summary>A meter that continues a count already at <paramref name="before"/> units (the search after the preparation): the same budget, the next check point past that count.</summary>
            public static WorkMeter Continue(long budget, long before) => new WorkMeter { budget = budget, work = before, nextCheck = ((before / CheckEvery) + 1) * CheckEvery };

            public bool Stopped => stopped != 0;

            public void Spend(long units)
            {
                work += units;
                if (work >= nextCheck)
                {
                    nextCheck = ((work / CheckEvery) + 1) * CheckEvery;
                    if (budget > 0 && work > budget && stopped == 0) { stopped = 1; reason = StopWork; }
                }
            }

            public void StopForCapacity()
            {
                if (stopped == 0) { stopped = 1; reason = StopCapacity; }
            }

            /// <summary>A list is full: the body ends and the driver, which owns every allocation, grows the list and runs the body again.</summary>
            public void StopForGrow()
            {
                if (stopped == 0) { stopped = 1; reason = StopGrow; }
            }
        }

        /// <summary>The capacity a list of <paramref name="length"/> elements would grow to on its next Add (UnsafeList: the next power of two).</summary>
        internal static long NextCapacity(int length) => math.ceilpow2((long)length + 1);

        /// <summary>
        /// The union-find root of <paramref name="i"/> in the parent area at <paramref name="at"/>, with path halving; the meter
        /// takes one unit a parent reference (one an iteration of the loop, one for the final read), so that a deep chain costs
        /// its depth and not a constant, and the loop leaves when the meter stops (the value returned is then some node of the
        /// chain, within the area, not the root: a caller that has stopped must not act on it). Both bodies use it.
        /// </summary>
        internal static int FindRoot(int* a, int at, int i, ref WorkMeter meter)
        {
            while (a[at + i] != i && !meter.Stopped)
            {
                a[at + i] = a[at + a[at + i]];
                i = a[at + i];
                meter.Spend(1);
            }

            if (!meter.Stopped) meter.Spend(1);
            return i;
        }

        // ---- the Burst entries -----------------------------------------------------------------------------------------------

        /// <summary>The preparation, through Burst when compiled (the direct call), else as managed code.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        internal static void RunPrepare(ref PrepareBody body) => body.Execute();

        /// <summary>The search, through Burst when compiled (the direct call), else as managed code.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        internal static void RunSearch(ref SearchBody body) => body.Execute();

        // In Burst the call is discarded, so the marker stays 0: it proves the body ran through Burst.
        [BurstDiscard]
        private static void MarkManaged(long* stats) => stats[StatManagedMarker] = 1;

        // ---- the candidate enumeration of stage A -----------------------------------------------------------------------------

        /// <summary>
        /// Every pair of nodes whose boxes are not apart by more than far (far = 2m + 4E1), in the original (i, j) order
        /// (i &lt; j), with its BoxGap on (i, j). AllPairs is the baseline (every pair evaluated); Sweep sorts the nodes by their
        /// box's least x (ties by index), and for each node in that order scans the following ones until lo.x(b) - hi.x(a) &gt;
        /// far: that rounded difference is the x term BoxGap(i, j) itself computes, and BoxGap = sqrt(fl(dx^2 + dy^2 + dz^2)) &gt;=
        /// dx for correctly rounded IEEE operations without underflow or overflow (dx &gt; far &gt;= 2m here, |coordinate| bounded),
        /// so every pair skipped has BoxGap &gt; far and would have been excluded. The pairs found are restored to the (i, j)
        /// order by a heap sort of their keys; the gaps are those computed on (i, j), bit for bit.
        /// <para>The meter counts a BoxGap, a sort compare, a sort move and an append as one unit each; candidateCap (-1: none; 0 or
        /// more: the most pairs the lists may hold) refuses a growth of the candidate lists past it (the body returns Unfinished); the
        /// meter's stop ends the enumeration.</para>
        /// </summary>
        internal static class PairKernel
        {
            public static void AllPairs(double3* lo, double3* hi, int n, double far, UnsafeList<int2>* candidates, UnsafeList<double>* gaps, bool count, ref EnumerateCounts c, ref WorkMeter meter, int candidateCap)
            {
                for (int i = 0; i < n && !meter.Stopped; i++)
                {
                    for (int j = i + 1; j < n && !meter.Stopped; j++)
                    {
                        double gap = BoxGap(lo[i], hi[i], lo[j], hi[j]);
                        meter.Spend(1);   // one unit a pair: the gap, with its append when kept
                        if (count) c.gapEvals++;
                        if (gap > far)
                        {
                            if (count) c.excluded++;
                            continue;
                        }

                        Append(candidates, gaps, new int2(i, j), gap, count, ref c, ref meter, candidateCap);
                    }
                }
            }

            public static void Sweep(double3* lo, double3* hi, int n, double far, NodeKey* order, UnsafeList<KeyGap>* keyGaps, UnsafeList<int2>* candidates, UnsafeList<double>* gaps, bool count, ref EnumerateCounts c, ref WorkMeter meter, int candidateCap)
            {
                for (int k = 0; k < n && !meter.Stopped; k++) { order[k] = new NodeKey { x = lo[k].x, index = k }; meter.Spend(1); }
                SortNodes(order, n, count, ref c, ref meter);
                keyGaps->Clear();
                for (int p = 0; p < n && !meter.Stopped; p++)
                {
                    int a = order[p].index;
                    double hiAx = hi[a].x;
                    for (int q = p + 1; q < n && !meter.Stopped; q++)
                    {
                        int b = order[q].index;
                        if (lo[b].x - hiAx > far)
                        {
                            if (count) c.sweepSkipped += n - q;
                            break;   // every later node has a larger or equal lo.x: the same difference is larger or equal
                        }

                        int i = math.min(a, b), j = math.max(a, b);
                        double gap = BoxGap(lo[i], hi[i], lo[j], hi[j]);
                        meter.Spend(1);
                        if (count) c.gapEvals++;
                        if (gap > far)
                        {
                            if (count) c.excluded++;
                            continue;
                        }

                        // The key list never reallocates here: full, the body stops and the driver grows it (or refuses by the cap).
                        if (keyGaps->Length == keyGaps->Capacity)
                        {
                            if (candidateCap >= 0 && NextCapacity(keyGaps->Length) > candidateCap) meter.StopForCapacity(); else meter.StopForGrow();
                            return;
                        }

                        keyGaps->AddNoResize(new KeyGap { key = ((long)i * n) + j, gap = gap });
                    }
                }

                if (meter.Stopped) return;
                SortKeys(keyGaps, count, ref c, ref meter);
                if (count) c.keysSorted = keyGaps->Length;
                for (int k = 0; k < keyGaps->Length && !meter.Stopped; k++)
                {
                    KeyGap kg = keyGaps->Ptr[k];
                    meter.Spend(1);   // one unit a key restored
                    Append(candidates, gaps, new int2((int)(kg.key / n), (int)(kg.key % n)), kg.gap, count, ref c, ref meter, candidateCap);
                }
            }

            private static void Append(UnsafeList<int2>* candidates, UnsafeList<double>* gaps, int2 pair, double gap, bool count, ref EnumerateCounts c, ref WorkMeter meter, int candidateCap)
            {
                // No reallocation inside the body (GRAPH-CONNECT-1 §4.1; TL review): a full list stops the body -- by the cap when the
                // next capacity would exceed it, else for a growth the driver performs (through its one allocation boundary) before
                // running the body again. The lists' capacities are equal, so one check covers both.
                if (candidates->Length == candidates->Capacity || gaps->Length == gaps->Capacity)
                {
                    if (candidateCap >= 0 && NextCapacity(candidates->Length) > candidateCap) meter.StopForCapacity(); else meter.StopForGrow();
                    return;
                }

                candidates->AddNoResize(pair);
                gaps->AddNoResize(gap);
            }

            // Heap sorts (deterministic, in place, no recursion); the compares and moves are counted on request and metered.
            private static void SortNodes(NodeKey* a, int n, bool count, ref EnumerateCounts c, ref WorkMeter meter)
            {
                for (int start = (n / 2) - 1; start >= 0 && !meter.Stopped; start--) SiftNodes(a, start, n, count, ref c, ref meter);
                for (int end = n - 1; end > 0 && !meter.Stopped; end--)
                {
                    NodeKey tmp = a[0]; a[0] = a[end]; a[end] = tmp;
                    meter.Spend(1);
                    if (count) c.nodeSortMoves++;
                    SiftNodes(a, 0, end, count, ref c, ref meter);
                }
            }

            private static void SiftNodes(NodeKey* a, int root, int n, bool count, ref EnumerateCounts c, ref WorkMeter meter)
            {
                while (!meter.Stopped)
                {
                    int child = (2 * root) + 1;
                    if (child >= n) return;
                    meter.Spend(1);   // one unit a sift step: its one or two compares and its move
                    if (child + 1 < n)
                    {
                        if (count) c.nodeSortCompares++;
                        if (a[child + 1].After(a[child])) child++;
                    }

                    if (count) c.nodeSortCompares++;
                    if (!a[child].After(a[root])) return;
                    NodeKey tmp = a[root]; a[root] = a[child]; a[child] = tmp;
                    if (count) c.nodeSortMoves++;
                    root = child;
                }
            }

            private static void SortKeys(UnsafeList<KeyGap>* list, bool count, ref EnumerateCounts c, ref WorkMeter meter)
            {
                KeyGap* a = list->Ptr;
                int n = list->Length;
                for (int start = (n / 2) - 1; start >= 0 && !meter.Stopped; start--) SiftKeys(a, start, n, count, ref c, ref meter);
                for (int end = n - 1; end > 0 && !meter.Stopped; end--)
                {
                    KeyGap tmp = a[0]; a[0] = a[end]; a[end] = tmp;
                    meter.Spend(1);
                    if (count) c.keySortMoves++;
                    SiftKeys(a, 0, end, count, ref c, ref meter);
                }
            }

            private static void SiftKeys(KeyGap* a, int root, int n, bool count, ref EnumerateCounts c, ref WorkMeter meter)
            {
                while (!meter.Stopped)
                {
                    int child = (2 * root) + 1;
                    if (child >= n) return;
                    meter.Spend(1);   // one unit a sift step: its one or two compares and its move
                    if (child + 1 < n)
                    {
                        if (count) c.keySortCompares++;
                        if (a[child + 1].key > a[child].key) child++;
                    }

                    if (count) c.keySortCompares++;
                    if (a[child].key <= a[root].key) return;
                    KeyGap tmp = a[root]; a[root] = a[child]; a[child] = tmp;
                    if (count) c.keySortMoves++;
                    root = child;
                }
            }
        }

        // ---- the maximum flow -----------------------------------------------------------------------------------------------

        /// <summary>
        /// The maximum flow between two terminals over arcs in a CSR (adjStart[nodes + 1], arcTo, arcRev, arcCap, all in one
        /// int area at the offsets given), and the source side of the final residual graph. Edmonds-Karp (the baseline) and
        /// Dinic (level graph + blocking flow with an explicit stack and current-arc pointers, no recursion) give the same
        /// value and, since the residual reachability of any maximum flow is the same set, the same source side and cut.
        /// Capacities: a group's arcs 1 or Inf both ways, a terminal's Inf one way and 0 back; a flow of Inf or more means no
        /// finite cut (-1). The counts are kept only when asked (the diagnostic run). The meter counts a node of an
        /// initialisation, a dequeue, an arc check and an edge of a path's minimum-capacity pass and of its residual update as
        /// one unit each; when it stops, the value returned is not to be used.
        /// </summary>
        internal static class FlowKernel
        {
            public const int Inf = 1 << 28;

            // The tail of an arc: the node whose adjacency range holds it (a binary search of the starts).
            private static int Tail(int* w, int adjStart, int nodes, int e, ref WorkMeter meter)
            {
                int lo = 0, hi = nodes - 1;
                while (lo < hi && !meter.Stopped)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (w[adjStart + mid] <= e) lo = mid; else hi = mid - 1;
                    meter.Spend(1);
                }

                return lo;
            }

            public static int EdmondsKarp(int* w, int adjStart, int arcTo, int arcRev, int arcCap, int queue, int parent, int nodes, int src, int dst, bool count, ref FlowCounts c, ref WorkMeter meter)
            {
                int flow = 0;
                while (!meter.Stopped)
                {
                    for (int v = 0; v < nodes && !meter.Stopped; v++) { w[parent + v] = -1; meter.Spend(1); }
                    w[parent + src] = -2;
                    int head = 0, tail = 0;
                    w[queue + tail++] = src;
                    while (head < tail && w[parent + dst] == -1 && !meter.Stopped)
                    {
                        int u = w[queue + head++];
                        meter.Spend(1);
                        if (count) c.dequeues++;
                        for (int e = w[adjStart + u]; e < w[adjStart + u + 1] && !meter.Stopped; e++)
                        {
                            meter.Spend(1);
                            if (count) c.arcChecks++;
                            int v = w[arcTo + e];
                            if (w[parent + v] == -1 && w[arcCap + e] > 0)
                            {
                                w[parent + v] = e;
                                w[queue + tail++] = v;
                            }
                        }
                    }

                    if (meter.Stopped) return -1;
                    if (w[parent + dst] == -1) return flow;
                    int push = Inf;
                    for (int v = dst; v != src && !meter.Stopped; v = Tail(w, adjStart, nodes, w[parent + v], ref meter)) { push = math.min(push, w[arcCap + w[parent + v]]); meter.Spend(1); }
                    if (push >= Inf) return -1;
                    for (int v = dst; v != src && !meter.Stopped; v = Tail(w, adjStart, nodes, w[parent + v], ref meter))
                    {
                        int e = w[parent + v];
                        w[arcCap + e] -= push;
                        w[arcCap + w[arcRev + e]] += push;
                        meter.Spend(1);
                    }

                    flow += push;
                    if (count) { c.augmentations++; c.pushes++; }
                    if (flow >= Inf) return -1;
                }

                return -1;
            }

            public static int Dinic(int* w, int adjStart, int arcTo, int arcRev, int arcCap, int queue, int level, int cur, int pathNode, int pathArc, int nodes, int src, int dst, bool count, ref FlowCounts c, ref WorkMeter meter)
            {
                int flow = 0;
                while (!meter.Stopped)
                {
                    // The level graph: breadth-first over the residual arcs.
                    for (int v = 0; v < nodes && !meter.Stopped; v++) { w[level + v] = -1; meter.Spend(1); }
                    w[level + src] = 0;
                    int head = 0, tail = 0;
                    w[queue + tail++] = src;
                    while (head < tail && !meter.Stopped)
                    {
                        int u = w[queue + head++];
                        meter.Spend(1);
                        if (count) c.dequeues++;
                        for (int e = w[adjStart + u]; e < w[adjStart + u + 1] && !meter.Stopped; e++)
                        {
                            meter.Spend(1);
                            if (count) c.arcChecks++;
                            int v = w[arcTo + e];
                            if (w[arcCap + e] > 0 && w[level + v] < 0)
                            {
                                w[level + v] = w[level + u] + 1;
                                w[queue + tail++] = v;
                            }
                        }
                    }

                    if (meter.Stopped) return -1;
                    if (count) c.levelBuilds++;
                    if (w[level + dst] < 0) return flow;

                    // A blocking flow: depth-first along the levels with an explicit path and current-arc pointers.
                    for (int v = 0; v < nodes && !meter.Stopped; v++) { w[cur + v] = w[adjStart + v]; meter.Spend(1); }
                    int depth = 0;
                    w[pathNode] = src;
                    while (!meter.Stopped)
                    {
                        int u = w[pathNode + depth];
                        if (u == dst)
                        {
                            // The path's least capacity, then its residual update: an edge a unit in each pass.
                            int push = Inf;
                            for (int i = 0; i < depth && !meter.Stopped; i++) { push = math.min(push, w[arcCap + w[pathArc + i]]); meter.Spend(1); }
                            if (push >= Inf) return -1;
                            int retreat = depth;
                            for (int i = 0; i < depth && !meter.Stopped; i++)
                            {
                                int e = w[pathArc + i];
                                w[arcCap + e] -= push;
                                w[arcCap + w[arcRev + e]] += push;
                                if (w[arcCap + e] == 0 && retreat == depth) retreat = i;
                                meter.Spend(1);
                            }

                            flow += push;
                            if (count) { c.augmentations++; c.pushes++; }
                            if (flow >= Inf) return -1;
                            depth = retreat;   // back to the tail of the first saturated arc
                            continue;
                        }

                        bool advanced = false;
                        while (w[cur + u] < w[adjStart + u + 1] && !meter.Stopped)
                        {
                            int e = w[cur + u];
                            meter.Spend(1);
                            if (count) c.arcChecks++;
                            int v = w[arcTo + e];
                            if (w[arcCap + e] > 0 && w[level + v] == w[level + u] + 1)
                            {
                                w[pathArc + depth] = e;
                                w[pathNode + depth + 1] = v;
                                depth++;
                                advanced = true;
                                break;
                            }

                            w[cur + u]++;
                        }

                        if (!advanced)
                        {
                            if (depth == 0) break;   // the blocking flow is complete
                            w[level + u] = -1;       // a dead end in this phase
                            depth--;
                            w[cur + w[pathNode + depth]]++;
                        }
                    }
                }

                return -1;
            }

            /// <summary>The source side: what the final residual graph reaches from the source (a stack in the queue's room).</summary>
            public static void ReachFromSource(int* w, int adjStart, int arcTo, int arcCap, int queue, int nodes, int src, byte* reach, int reachAt, ref WorkMeter meter)
            {
                for (int v = 0; v < nodes && !meter.Stopped; v++) { reach[reachAt + v] = 0; meter.Spend(1); }
                int top = 0;
                w[queue + top++] = src;
                reach[reachAt + src] = 1;
                while (top > 0 && !meter.Stopped)
                {
                    int u = w[queue + --top];
                    for (int e = w[adjStart + u]; e < w[adjStart + u + 1] && !meter.Stopped; e++)
                    {
                        meter.Spend(1);
                        int v = w[arcTo + e];
                        if (reach[reachAt + v] == 0 && w[arcCap + e] > 0)
                        {
                            reach[reachAt + v] = 1;
                            w[queue + top++] = v;
                        }
                    }
                }
            }
        }

        // ---- the nearest three ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The first three of a sequence by (gap ascending, representative ascending), kept in order while the sequence is
        /// scanned once: the order the full insertion sort (<see cref="SortAllReference"/>) gives its first three. Shared by
        /// the search body and the tests' static helper, so that both use one rule.
        /// </summary>
        internal struct Top3
        {
            public int kept;
            public int x0, x1, x2;        // the items
            public int r0, r1, r2;        // their representatives (the tie rule)
            public double g0, g1, g2;     // their gaps

            private static bool Before(double g, int r, double gy, int ry) => g < gy || (g == gy && r < ry);

            /// <summary>Offers an item; compares and moves are the diagnostic counts (at most three compares, two moves).</summary>
            public void Offer(int x, int r, double g, ref long compares, ref long moves)
            {
                int at = kept;
                if (kept > 0) { compares++; if (Before(g, r, g0, r0)) at = 0; }
                if (at == kept && kept > 1) { compares++; if (Before(g, r, g1, r1)) at = 1; }
                if (at == kept && kept > 2) { compares++; if (Before(g, r, g2, r2)) at = 2; }
                if (at >= 3) return;   // kept is 3 and x goes after all three
                if (at <= 1 && kept > 1) { x2 = x1; r2 = r1; g2 = g1; moves++; }
                if (at == 0 && kept > 0) { x1 = x0; r1 = r0; g1 = g0; moves++; }
                if (at == 0) { x0 = x; r0 = r; g0 = g; }
                else if (at == 1) { x1 = x; r1 = r; g1 = g; }
                else { x2 = x; r2 = r; g2 = g; }
                if (kept < 3) kept++;
            }
        }

        /// <summary>Tests: the first min(3, n) items by the selection.</summary>
        internal static int[] SelectTop3Reference(double[] gaps, int[] reps, out long compares, out long moves)
        {
            var top = new Top3();
            compares = 0;
            moves = 0;
            for (int i = 0; i < gaps.Length; i++) top.Offer(i, reps[i], gaps[i], ref compares, ref moves);
            var result = new int[top.kept];
            if (top.kept > 0) result[0] = top.x0;
            if (top.kept > 1) result[1] = top.x1;
            if (top.kept > 2) result[2] = top.x2;
            return result;
        }

        /// <summary>Tests: the whole sequence by the full insertion sort of the baseline (SortNearest's rule), as item indices.</summary>
        internal static int[] SortAllReference(double[] gaps, int[] reps)
        {
            var order = new int[gaps.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = 1; i < order.Length; i++)
            {
                int x = order[i];
                int j = i - 1;
                while (j >= 0)
                {
                    int y = order[j];
                    bool after = gaps[y] > gaps[x] || (gaps[y] == gaps[x] && reps[y] > reps[x]);
                    if (!after) break;
                    order[j + 1] = y;
                    j--;
                }

                order[j + 1] = x;
            }

            return order;
        }

        // ---- the preparation body -------------------------------------------------------------------------------------------------

        /// <summary>
        /// The preparation: the inputs (the named vertices' local positions and ids, the indices, the convex vertices), the
        /// outputs (owner coordinates, corner slots, triangle components, the nodes' boxes and hulls, the bounds, the
        /// candidates and gaps) and the stats, all as pointers into areas the driver holds; the lists grow through their
        /// UnsafeList (the driver's NativeLists). The work meter and the caps (maxNodes, candidateCap; 0: none) end it early with
        /// stats[StatUnfinished] = 1 and the stop reason in stats[StatStop]; its units go to stats[StatWorkPrepare].
        /// </summary>
        internal struct PrepareBody
        {
            public float3* local;
            public uint* ids;
            public uint* indices;
            public float3* convexLocal;
            public int* convexStart;
            public float4x4 geometryToOwner, shapeToOwner;
            public float margin;
            public int vertexViewLength, named, triangleCount, convexCount;

            public double3* owner;
            public int* cornerSlot, triComponent, nodeHullStart, nodeTriangleCount, scratch;
            public double3* nodeLo, nodeHi, hull;
            public double* bounds;   // m2, e1, far, near
            public long* stats;
            public UnsafeList<int2>* candidates;
            public UnsafeList<double>* gaps;
            public int measure;                  // 0 off (no clock read, no count), 1 the section times, 2 the diagnostic counts
            public long* sections;
            public int pairs;                    // 0: every pair of nodes evaluated (the baseline); 1: the sweep along x
            public NodeKey* order;               // the sweep's node order (n), created only for the sweep
            public UnsafeList<KeyGap>* keyGaps;  // the sweep's pairs before the order is restored, created only for the sweep
            public long workBudget;              // 0: none
            public long workBefore;              // the units spent by the earlier runs of this body (the driver's growth re-runs): the meter continues from them
            public int maxNodes;                 // 0: none
            public int candidateCapEnabled;      // 0: no cap on the candidate lists; 1: candidateCap applies (a cap of 0 then means no room at all)
            public int candidateCap;
            private long last;
            private WorkMeter meter;

            private void Tick(int section)
            {
                if (measure != 1) return;
                long now = ProfilerUnsafeUtility.Timestamp;
                sections[section] += now - last;
                last = now;
            }

            private void Count(int counter, long by)
            {
                if (measure == 2) sections[counter] += by;
            }

            // Union-find over the triangles with the parent reads counted (measure 2: two a loop iteration, one for the final read,
            // as before) and metered (a unit a parent reference, through the shared FindRoot).
            private int FindCounted(int at, int i)
            {
                long before = meter.work;
                int root = FindRoot(scratch, at, i, ref meter);
                long iterations = meter.work - before - 1;
                Count(PcParentRefs, (2 * iterations) + 1);
                return root;
            }

            private void UnionCounted(int at, int x, int y)
            {
                x = FindCounted(at, x);
                y = FindCounted(at, y);
                if (x == y) return;
                scratch[at + math.max(x, y)] = math.min(x, y);
            }

            private void Stop()
            {
                stats[StatUnfinished] = 1;
                stats[StatStop] = meter.reason;
                stats[StatWorkPrepare] = meter.work;   // cumulative over the driver's runs of this body
                stats[StatNeededCapacity] = meter.reason == StopGrow ? NextCapacity(math.max(candidates->Length, keyGaps->Length)) : 0;
            }

            public void Execute()
            {
                MarkManaged(stats);
                meter = WorkMeter.Continue(workBudget, workBefore);
                if (measure == 1) last = ProfilerUnsafeUtility.Timestamp;
                for (int i = 0; i < StatCount; i++) if (i != StatManagedMarker) stats[i] = 0;

                // Owner coordinates, as the managed OwnerDouble: ((double)E(i,0) * p.x) + ((double)E(i,1) * p.y) + ((double)E(i,2) * p.z) + E(i,3).
                double bound = 1.0;
                for (int i = 0; i < named && !meter.Stopped; i++)
                {
                    double3 o = Owner(local[i], in geometryToOwner);
                    owner[i] = o;
                    bound = math.max(bound, math.cmax(math.abs(o)));
                    meter.Spend(1);
                }

                bound = math.max(bound, TermBound(local, 0, named, in geometryToOwner, ref meter));
                double convexBound = 0.0;
                for (int c = 0; c < convexCount && !meter.Stopped; c++) convexBound = math.max(convexBound, TermBound(convexLocal, convexStart[c], convexStart[c + 1] - convexStart[c], in shapeToOwner, ref meter));
                bound = math.max(bound, convexBound);
                double m2 = 2.0 * margin;
                double e1 = bound * (1.0 / 17592186044416.0);   // 2^-44, exact
                bounds[0] = m2;
                bounds[1] = e1;
                bounds[2] = m2 + (4.0 * e1);
                bounds[3] = m2 - (4.0 * e1);
                Tick(PrepOwnerBounds);
                Count(PcNamed, named);
                Count(PcTriangles, triangleCount);
                if (meter.Stopped) { Stop(); return; }

                // The corners' slots: binary search of the sorted ids, an id under the view's length only.
                for (int i = 0; i < 3 * triangleCount && !meter.Stopped; i++)
                {
                    uint id = indices[i];
                    int at = id < (uint)math.max(0, vertexViewLength) ? Find(id) : -1;
                    meter.Spend(1);
                    if (at < 0)
                    {
                        if (meter.Stopped) { Stop(); return; }   // a search cut short is the budget's doing, not a bad index
                        stats[StatError] = 1;
                        return;
                    }

                    cornerSlot[i] = at;
                }

                Tick(PrepSlots);
                if (meter.Stopped) { Stop(); return; }

                // Mesh components: triangles sharing a vertex index, united; ids in the order of the first triangle.
                // scratch: [0, T) union-find parents, [T, T + named) the first triangle of a slot, [T + named, ...) component of a root.
                int parents = 0, firstOf = triangleCount, compOfRoot = triangleCount + named;
                for (int k = 0; k < triangleCount && !meter.Stopped; k++) { scratch[parents + k] = k; meter.Spend(1); }
                for (int i = 0; i < named && !meter.Stopped; i++) { scratch[firstOf + i] = -1; meter.Spend(1); }
                for (int k = 0; k < triangleCount && !meter.Stopped; k++)
                {
                    for (int c = 0; c < 3 && !meter.Stopped; c++)
                    {
                        int slot = cornerSlot[(3 * k) + c];
                        int other = scratch[firstOf + slot];
                        if (other >= 0) UnionCounted(parents, k, other);
                        else scratch[firstOf + slot] = k;
                        meter.Spend(1);
                    }
                }

                if (meter.Stopped) { Stop(); return; }
                for (int k = 0; k < triangleCount && !meter.Stopped; k++) { scratch[compOfRoot + k] = -1; meter.Spend(1); }
                int components = 0;
                for (int k = 0; k < triangleCount && !meter.Stopped; k++)
                {
                    int root = FindCounted(parents, k);
                    if (scratch[compOfRoot + root] < 0) scratch[compOfRoot + root] = components++;
                    triComponent[k] = scratch[compOfRoot + root];
                    meter.Spend(1);
                }

                if (meter.Stopped) { Stop(); return; }
                int n = convexCount + components;
                stats[StatMeshComponents] = components;
                stats[StatNodes] = n;
                Tick(PrepComponents);
                Count(PcComponents, components);
                Count(PcNodes, n);

                // The node cap (GRAPH-CONNECT-1 §4.1): known only now, checked before the node areas are read or written.
                if (maxNodes > 0 && n > maxNodes)
                {
                    meter.StopForCapacity();
                    Stop();
                    return;
                }

                // The nodes: convexes (their vertices the hull), then the components (their distinct corners, in the order of
                // first appearance, the hull). Hull slots: convexes first in order, then the components.
                for (int i = 0; i < n && !meter.Stopped; i++)
                {
                    nodeLo[i] = new double3(double.PositiveInfinity);
                    nodeHi[i] = new double3(double.NegativeInfinity);
                    nodeTriangleCount[i] = 0;
                    meter.Spend(1);
                }

                int fill = 0;
                for (int c = 0; c < convexCount && !meter.Stopped; c++)
                {
                    nodeHullStart[c] = fill;
                    for (int i = convexStart[c]; i < convexStart[c + 1] && !meter.Stopped; i++)
                    {
                        double3 o = Owner(convexLocal[i], in shapeToOwner);
                        hull[fill++] = o;
                        nodeLo[c] = math.min(nodeLo[c], o);
                        nodeHi[c] = math.max(nodeHi[c], o);
                        meter.Spend(1);
                    }
                }

                Tick(PrepConvexes);   // includes the node arrays' initialisation above
                if (meter.Stopped) { Stop(); return; }

                // Component boxes and distinct-corner counts (the first-triangle array is reused as "seen by component", the
                // component-of-root array as the count by node).
                for (int i = 0; i < named && !meter.Stopped; i++) { scratch[firstOf + i] = -1; meter.Spend(1); }
                for (int node = convexCount; node < n && !meter.Stopped; node++) { scratch[compOfRoot + node] = 0; meter.Spend(1); }
                for (int k = 0; k < triangleCount && !meter.Stopped; k++)
                {
                    int node = convexCount + triComponent[k];
                    nodeTriangleCount[node]++;
                    double3 a = owner[cornerSlot[3 * k]], b = owner[cornerSlot[(3 * k) + 1]], c3 = owner[cornerSlot[(3 * k) + 2]];
                    double3 lo = math.min(a, math.min(b, c3)), hi = math.max(a, math.max(b, c3));
                    nodeLo[node] = math.min(nodeLo[node], lo);
                    nodeHi[node] = math.max(nodeHi[node], hi);
                    for (int c = 0; c < 3 && !meter.Stopped; c++)
                    {
                        int slot = cornerSlot[(3 * k) + c];
                        if (scratch[firstOf + slot] != node)
                        {
                            scratch[firstOf + slot] = node;
                            scratch[compOfRoot + node]++;
                        }

                        meter.Spend(1);
                    }
                }

                if (meter.Stopped) { Stop(); return; }

                // The distinct counts by node: prefix sums into the hull starts.
                for (int node = convexCount; node < n && !meter.Stopped; node++)
                {
                    nodeHullStart[node] = fill;
                    fill += scratch[compOfRoot + node];
                    meter.Spend(1);
                }

                nodeHullStart[n] = fill;
                Tick(PrepMeshBoxesCounts);
                Count(PcHullStored, fill);
                for (int i = 0; i < named && !meter.Stopped; i++) { scratch[firstOf + i] = -1; meter.Spend(1); }
                for (int node = convexCount; node < n && !meter.Stopped; node++) { scratch[compOfRoot + node] = nodeHullStart[node]; meter.Spend(1); }   // the fill cursor
                for (int k = 0; k < triangleCount && !meter.Stopped; k++)
                {
                    int node = convexCount + triComponent[k];
                    for (int c = 0; c < 3 && !meter.Stopped; c++)
                    {
                        int slot = cornerSlot[(3 * k) + c];
                        if (scratch[firstOf + slot] != node)
                        {
                            scratch[firstOf + slot] = node;
                            hull[scratch[compOfRoot + node]++] = owner[slot];
                        }

                        meter.Spend(1);
                    }
                }

                Tick(PrepMeshFill);
                if (meter.Stopped) { Stop(); return; }

                // Stage A: the boxes; a pair apart by more than 2m + 4E1 is apart, the rest candidates in (i, j) order (the lists
                // grow as needed: the growths are counted in the diagnostic run; a growth past the cap stops the body).
                double far = bounds[2];
                long total = (long)n * (n - 1) / 2;
                stats[StatNodePairs] = total;
                Count(PcPairsTotal, total);
                var ec = new EnumerateCounts();
                int cap = candidateCapEnabled != 0 ? math.max(0, candidateCap) : -1;
                if (pairs == 0) PairKernel.AllPairs(nodeLo, nodeHi, n, far, candidates, gaps, measure == 2, ref ec, ref meter, cap);
                else PairKernel.Sweep(nodeLo, nodeHi, n, far, order, keyGaps, candidates, gaps, measure == 2, ref ec, ref meter, cap);
                stats[StatCandidatesA] = candidates->Length;
                stats[StatApartByBoxes] = total - candidates->Length;   // the pairs skipped by the sweep and those excluded by BoxGap, together (as before: every non-candidate)
                Tick(PrepPairs);
                Count(PcApart, stats[StatApartByBoxes]);
                Count(PcCandidates, candidates->Length);
                Count(PcGapEvals, ec.gapEvals);
                // PcCandGrowths .. PcGapGrowthOld: the driver's growths of the lists, written by it after the last run
                Count(PcSweepSkipped, ec.sweepSkipped);
                Count(PcGapExcluded, ec.excluded);
                Count(PcNodeSortCompares, ec.nodeSortCompares);
                Count(PcNodeSortMoves, ec.nodeSortMoves);
                Count(PcKeySortCompares, ec.keySortCompares);
                Count(PcKeySortMoves, ec.keySortMoves);
                Count(PcKeysSorted, ec.keysSorted);
                stats[StatWorkPrepare] = meter.work;
                if (meter.Stopped) Stop();
            }

            private int Find(uint id)
            {
                int lo = 0, hi = named - 1;
                while (lo <= hi && !meter.Stopped)
                {
                    Count(PcSlotCompares, 1);
                    meter.Spend(1);
                    int mid = lo + ((hi - lo) >> 1);
                    uint v = ids[mid];
                    if (v == id) return mid;
                    if (v < id) lo = mid + 1; else hi = mid - 1;
                }

                return -1;
            }
        }

        // ---- the search body (stage A's conclusion, stage B, the placement) ------------------------------------------------------------

        /// <summary>
        /// The search over the preparation's outputs: the deal, the minimum cuts, the hulls, the placement into nodeSide. The
        /// work meter (workBudget, 0: none) ends it early with verdict Undecided, stats[StatUnfinished] = 1 and StopWork; its
        /// units (beyond the preparation's, which the meter continues from) go to stats[StatWorkSearch].
        /// </summary>
        internal struct SearchBody
        {
            public int n, convexCount, candidateCount, maxCutAttempts, gjkMaxIterations;
            public double3* nodeLo, nodeHi, hull;
            public int* nodeHullStart, nodeTriangleCount;
            public int2* candidates;
            public double* gaps, bounds;
            public long* stats;
            public byte* nodeSide;
            public int* work;
            public double3* work3;
            public double* workD;
            public byte* workB;
            public int measure;                  // 0 off (no clock read), 1 the section times, 2 the diagnostic counts
            public int nearest;                  // 0: the full insertion sort of the triangle-only soft components (the baseline); 1: the selection of the first three
            public int flow;                     // 0: Edmonds-Karp (the baseline); 1: Dinic
            public long* sections;               // SectionCount: ticks by section (measure 1), counts (measure 2); projection calls in both
            public long workBudget;              // 0: none; the whole decision's budget (the preparation's units are counted in)
            public long workBefore;              // the preparation's units: the meter continues from them (one count over both bodies)
            private long last;
            private WorkMeter meter;

            // The time since the last boundary goes to the section given (measure 1 only; nothing is read otherwise).
            private void Tick(int section)
            {
                if (measure != 1) return;
                long now = ProfilerUnsafeUtility.Timestamp;
                sections[section] += now - last;
                last = now;
            }

            private void Mark()
            {
                if (measure == 1) last = ProfilerUnsafeUtility.Timestamp;
            }

            private void Count(int counter, long by)
            {
                if (measure == 2) sections[counter] += by;
            }

            // The work areas (offsets into work / work3 / workD / workB).
            private int Definite => 0;                         // n: the union-find of definite contacts (nodes)
            private int Graph => n;                            // n: the conservative graph's union-find
            private int CompOf => 2 * n;                       // n
            private int SoftParent => 3 * n;                   // n (over components)
            private int SoftRep => 4 * n;                      // n: root -> representative component, -1 none
            private int SoftOrder => 5 * n;                    // n: the roots in order of first appearance
            private int Cs => 6 * n;                           // n
            private int Ts => 7 * n;                           // n
            private int Nearest => 8 * n;                      // n
            private int Seeds => 9 * n;                        // 2 * (n + 9): (first, second or -1) pairs; at most n full soft components plus 3 x 3 pairs
            private int Queue => (11 * n) + 18;                // n + 2
            private int ParentArc => (12 * n) + 20;            // n + 2
            private int CandOrder => (13 * n) + 22;            // cand: candidate indices sorted by (component pair key, index)
            private int GroupStart => CandOrder + Cand;        // cand + 1
            private int GroupA => GroupStart + Cand + 1;       // cand
            private int GroupB => GroupA + Cand;               // cand
            private int GroupOf => GroupB + Cand;              // cand: candidate -> group
            private int Cut => GroupOf + Cand;                 // cand: groups in the cut
            private int ToRefute => Cut + Cand;                // cand
            private int ArcTo => ToRefute + Cand;              // arcs
            private int ArcRev => ArcTo + Arcs;                // arcs
            private int AdjStart => ArcRev + Arcs;             // n + 3
            private int ArcCap => AdjStart + n + 3;            // arcs (int capacities)
            private int Level => ArcCap + Arcs;                // n + 3: Dinic's levels
            private int Cur => Level + n + 3;                  // n + 3: Dinic's current arc a node
            private int PathNode => Cur + n + 3;               // n + 3: the explicit path's nodes
            private int PathArc => PathNode + n + 3;           // n + 3: the explicit path's arcs
            private int Cand => math.max(1, candidateCount);
            private int Arcs => (2 * Cand) + (4 * math.max(1, n));

            private int CandState => 0;                        // workB: cand (0 removed, 1 candidate, 2 ambiguous, 3 limit)
            private int Tried => Cand;                         // workB: cand
            private int HasC => 2 * Cand;                      // workB: n
            private int HasT => (2 * Cand) + n;                // workB: n
            private int SoftHasC => (2 * Cand) + (2 * n);      // workB: n
            private int SoftHasT => (2 * Cand) + (3 * n);      // workB: n
            private int GroupBlocked => (2 * Cand) + (4 * n);  // workB: cand
            private int Reach => (3 * Cand) + (4 * n);         // workB: n + 2

            private int CompLo => 0;                           // work3: n
            private int CompHi => n;                           // work3: n
            private int SoftLo => 2 * n;                       // work3: n
            private int SoftHi => 3 * n;                       // work3: n
            private int KeyD => 0;                             // workD: cand (sort keys)

            private const int Inf = 1 << 28;

            public void Execute()
            {
                MarkManaged(stats);
                meter = WorkMeter.Continue(workBudget, workBefore);
                Mark();
                for (int i = 0; i < n && !meter.Stopped; i++) { work[Definite + i] = i; meter.Spend(1); }
                for (int i = 0; i < candidateCount && !meter.Stopped; i++) { workB[CandState + i] = 1; workB[Tried + i] = 0; meter.Spend(1); }
                if (meter.Stopped) { Unfinished(); return; }
                stats[StatStage] = 0;
                bool concluded = Conclude();
                Tick(SecConclude);
                if (meter.Stopped) { Unfinished(); return; }
                if (concluded) { stats[StatWorkSearch] = meter.work - workBefore; return; }
                stats[StatStage] = 1;
                bool found = Search();
                if (meter.Stopped) { Tick(SecConclude); Unfinished(); return; }
                if (found)
                {
                    Tick(SecConclude);
                    stats[StatWorkSearch] = meter.work - workBefore;
                    return;
                }

                stats[StatVerdict] = VerdictNotFound;
                stats[StatStop] = stats[StatCutLimitHit] != 0 ? StopCutLimit : StopHulls;
                stats[StatWorkSearch] = meter.work - workBefore;
                Tick(SecConclude);
            }

            private void Unfinished()
            {
                stats[StatVerdict] = VerdictUndecided;
                stats[StatUnfinished] = 1;
                stats[StatStop] = meter.reason;
                stats[StatWorkSearch] = meter.work - workBefore;
            }

            // ---- the graph: components, the deal ------------------------------------------------------------------------------

            private bool Conclude()
            {
                // Definite components: counts of convexes and triangles by root; NotSeparable from the definite contacts alone.
                int convexes = convexCount, triangles = 0;
                for (int i = convexCount; i < n && !meter.Stopped; i++) { triangles += nodeTriangleCount[i]; meter.Spend(1); }
                for (int i = 0; i < n && !meter.Stopped; i++) { work[Nearest + i] = 0; work[Cs + i] = 0; meter.Spend(1); }   // Nearest: convexes by root; Cs: triangles by root (temporary use)
                int definite = 0;
                for (int i = 0; i < n && !meter.Stopped; i++)
                {
                    int root = Find(Definite, i);
                    if (work[Nearest + root] == 0 && work[Cs + root] == 0) definite++;
                    if (i < convexCount) work[Nearest + root]++; else work[Cs + root] += nodeTriangleCount[i];
                    meter.Spend(1);
                }

                if (meter.Stopped) return false;
                stats[StatDefiniteComponents] = definite;
                int allC = 0, allT = 0;
                for (int i = 0; i < n && !meter.Stopped; i++)
                {
                    meter.Spend(1);
                    if (Find(Definite, i) != i) continue;
                    if (work[Nearest + i] == convexes && convexes > 0) allC++;
                    if (work[Cs + i] == triangles && triangles > 0) allT++;
                }

                if (meter.Stopped) return false;
                if (allC > 0 || allT > 0 || convexes < 2 || triangles < 2)
                {
                    stats[StatVerdict] = VerdictNotSeparable;
                    stats[StatStop] = allC > 0 ? ReasonAllConvexesOne : allT > 0 ? ReasonAllTrianglesOne : ReasonFewerThanTwo;
                    return true;
                }

                // The conservative graph: the definite contacts and every candidate left.
                for (int i = 0; i < n && !meter.Stopped; i++) { work[Graph + i] = i; meter.Spend(1); }
                for (int i = 0; i < n && !meter.Stopped; i++) { Union(Graph, i, Find(Definite, i)); meter.Spend(1); }
                for (int c = 0; c < candidateCount && !meter.Stopped; c++)
                {
                    meter.Spend(1);
                    if (workB[CandState + c] == 0) continue;
                    Union(Graph, candidates[c].x, candidates[c].y);
                }

                if (meter.Stopped) return false;
                if (TryDeal())
                {
                    stats[StatVerdict] = VerdictSeparable;
                    return true;
                }

                return false;
            }

            // The deal of the conservative graph's components (by Find(Graph, i)): the first full component alone where
            // there are two, or one with a convex-only and a triangle-only beside it; else the first convex-only with the
            // first triangle-only. Writes nodeSide.
            private bool TryDeal()
            {
                for (int i = 0; i < n && !meter.Stopped; i++) { workB[HasC + i] = 0; workB[HasT + i] = 0; work[SoftOrder + i] = -1; meter.Spend(1); }
                int order = 0;
                for (int i = 0; i < n && !meter.Stopped; i++)
                {
                    int k = Find(Graph, i);
                    if (work[SoftOrder + k] < 0) { work[SoftOrder + k] = order; work[Ts + order] = k; order++; }
                    if (i < convexCount) workB[HasC + k] = 1; else workB[HasT + k] = 1;
                    meter.Spend(1);
                }

                if (meter.Stopped) return false;
                int firstFull = -1, fulls = 0, firstC = -1, cs = 0, firstT = -1, ts = 0;
                for (int o = 0; o < order && !meter.Stopped; o++)
                {
                    int k = work[Ts + o];
                    bool c = workB[HasC + k] != 0, t = workB[HasT + k] != 0;
                    if (c && t) { fulls++; if (firstFull < 0) firstFull = k; }
                    else if (c) { cs++; if (firstC < 0) firstC = k; }
                    else { ts++; if (firstT < 0) firstT = k; }
                    meter.Spend(1);
                }

                int chosenA, chosenB = -1;
                if (fulls >= 2 || (fulls == 1 && cs >= 1 && ts >= 1)) chosenA = firstFull;
                else if (fulls == 0 && cs >= 2 && ts >= 2) { chosenA = firstC; chosenB = firstT; }
                else return false;
                for (int i = 0; i < n && !meter.Stopped; i++)
                {
                    int k = Find(Graph, i);
                    nodeSide[i] = (byte)(k == chosenA || k == chosenB ? 1 : 0);
                    meter.Spend(1);
                }

                return !meter.Stopped;
            }

            // ---- the search along minimum cuts ------------------------------------------------------------------------------------

            private bool Search()
            {
                while (!meter.Stopped)
                {
                    if (stats[StatCutAttempts] >= maxCutAttempts)
                    {
                        stats[StatCutLimitHit] = 1;
                        return false;
                    }

                    // The definite components as the graph's nodes (compact ids in node order).
                    int m = 0;
                    for (int i = 0; i < n && !meter.Stopped; i++) { work[SoftRep + i] = -1; meter.Spend(1); }   // root -> compact id (temporary)
                    for (int i = 0; i < n && !meter.Stopped; i++)
                    {
                        int root = Find(Definite, i);
                        if (work[SoftRep + root] < 0) work[SoftRep + root] = m++;
                        work[CompOf + i] = work[SoftRep + root];
                        meter.Spend(1);
                    }

                    if (meter.Stopped) return false;
                    for (int k = 0; k < m && !meter.Stopped; k++)
                    {
                        workB[HasC + k] = 0;
                        workB[HasT + k] = 0;
                        work3[CompLo + k] = new double3(double.PositiveInfinity);
                        work3[CompHi + k] = new double3(double.NegativeInfinity);
                        meter.Spend(1);
                    }

                    for (int i = 0; i < n && !meter.Stopped; i++)
                    {
                        int k = work[CompOf + i];
                        if (i < convexCount) workB[HasC + k] = 1; else workB[HasT + k] = 1;
                        work3[CompLo + k] = math.min(work3[CompLo + k], nodeLo[i]);
                        work3[CompHi + k] = math.max(work3[CompHi + k], nodeHi[i]);
                        meter.Spend(1);
                    }

                    // The edges between components: the live candidates grouped by their component pair, groups in the order of
                    // first appearance (the managed Dictionary's insertion order), a group blocked when any of its candidates is
                    // ambiguous, at a limit, or already tried at this stage.
                    int live = 0;
                    for (int c = 0; c < candidateCount && !meter.Stopped; c++)
                    {
                        meter.Spend(1);
                        if (workB[CandState + c] == 0) continue;
                        int a = work[CompOf + candidates[c].x], b = work[CompOf + candidates[c].y];
                        if (a == b) continue;
                        work[CandOrder + live] = c;
                        workD[KeyD + live] = ((double)math.min(a, b) * m) + math.max(a, b);
                        live++;
                    }

                    if (meter.Stopped) return false;
                    SortByKey(live);
                    if (meter.Stopped) return false;
                    int groups = 0;
                    for (int t = 0; t < live && !meter.Stopped; t++)
                    {
                        int c = work[CandOrder + t];
                        int a = work[CompOf + candidates[c].x], b = work[CompOf + candidates[c].y];
                        int ga = math.min(a, b), gb = math.max(a, b);
                        if (t == 0 || work[GroupA + groups - 1] != ga || work[GroupB + groups - 1] != gb)
                        {
                            work[GroupStart + groups] = t;
                            work[GroupA + groups] = ga;
                            work[GroupB + groups] = gb;
                            workB[GroupBlocked + groups] = 0;
                            groups++;
                        }

                        work[GroupOf + c] = groups - 1;
                        if (workB[CandState + c] >= 2 || workB[Tried + c] != 0) workB[GroupBlocked + groups - 1] = 1;
                        meter.Spend(1);
                    }

                    work[GroupStart + groups] = live;
                    Tick(SecRegroup);
                    if (meter.Stopped) return false;

                    // The seeds, from the soft components (definite + blocked edges).
                    for (int k = 0; k < m && !meter.Stopped; k++) { work[SoftParent + k] = k; meter.Spend(1); }
                    for (int g = 0; g < groups && !meter.Stopped; g++) { meter.Spend(1); if (workB[GroupBlocked + g] != 0) Union(SoftParent, work[GroupA + g], work[GroupB + g]); }
                    int softCount = 0;
                    for (int k = 0; k < m && !meter.Stopped; k++) { work[SoftRep + k] = -1; meter.Spend(1); }
                    for (int k = 0; k < m && !meter.Stopped; k++)
                    {
                        int root = Find(SoftParent, k);
                        if (work[SoftRep + root] < 0)
                        {
                            work[SoftRep + root] = k;
                            work[SoftOrder + softCount++] = root;
                            workB[SoftHasC + root] = 0;
                            workB[SoftHasT + root] = 0;
                            work3[SoftLo + root] = work3[CompLo + k];
                            work3[SoftHi + root] = work3[CompHi + k];
                        }

                        workB[SoftHasC + root] |= workB[HasC + k];
                        workB[SoftHasT + root] |= workB[HasT + k];
                        work3[SoftLo + root] = math.min(work3[SoftLo + root], work3[CompLo + k]);
                        work3[SoftHi + root] = math.max(work3[SoftHi + root], work3[CompHi + k]);
                        meter.Spend(1);
                    }

                    if (meter.Stopped) return false;
                    int seeds = 0, cs = 0, ts = 0;
                    for (int o = 0; o < softCount && !meter.Stopped; o++)
                    {
                        int root = work[SoftOrder + o];
                        bool c = workB[SoftHasC + root] != 0, t = workB[SoftHasT + root] != 0;
                        if (c && t) { work[Seeds + (2 * seeds)] = work[SoftRep + root]; work[Seeds + (2 * seeds) + 1] = -1; seeds++; }
                        else if (c) work[Cs + cs++] = root;
                        else work[Ts + ts++] = root;
                        meter.Spend(1);
                    }

                    // cs is already in the order of the representatives (roots appear in the order of their smallest member).
                    for (int i = 0; i < math.min(cs, 3) && !meter.Stopped; i++)
                    {
                        int c = work[Cs + i];
                        Tick(SecSeeds);
                        if (nearest == 0)
                        {
                            for (int j = 0; j < ts && !meter.Stopped; j++) { work[Nearest + j] = work[Ts + j]; meter.Spend(1); }
                            SortNearest(ts, c);
                        }
                        else
                        {
                            SelectNearest(ts, c);
                        }

                        Tick(SecSortNearest);
                        if (meter.Stopped) return false;
                        for (int j = 0; j < math.min(ts, 3); j++)
                        {
                            work[Seeds + (2 * seeds)] = work[SoftRep + c];
                            work[Seeds + (2 * seeds) + 1] = work[SoftRep + work[Nearest + j]];
                            seeds++;
                        }
                    }

                    Tick(SecSeeds);
                    if (meter.Stopped) return false;
                    bool anyCut = false;
                    for (int i = 0; i < seeds && !anyCut && !meter.Stopped; i++)
                    {
                        for (int j = i + 1; j < seeds && !anyCut && !meter.Stopped; j++)
                        {
                            meter.Spend(1);
                            if (Overlap(i, j)) continue;
                            if (stats[StatCutAttempts] >= maxCutAttempts)
                            {
                                stats[StatCutLimitHit] = 1;
                                return false;
                            }

                            stats[StatCutAttempts]++;
                            int cutSize = MinCut(m, groups, i, j);
                            if (meter.Stopped) return false;
                            if (cutSize < 0) continue;
                            anyCut = true;

                            // The cut's candidates, the largest box gap first, ties in the order of the groups and the candidates.
                            int count = 0;
                            for (int q = 0; q < cutSize && !meter.Stopped; q++)
                            {
                                int g = work[Cut + q];
                                for (int t = work[GroupStart + g]; t < work[GroupStart + g + 1] && !meter.Stopped; t++) { work[ToRefute + count++] = work[CandOrder + t]; meter.Spend(1); }
                            }

                            Tick(SecCollect);
                            SortToRefute(count);
                            Tick(SecSortToRefute);
                            if (meter.Stopped) return false;
                            bool allRefuted = true;
                            for (int q = 0; q < count && !meter.Stopped; q++)
                            {
                                int c = work[ToRefute + q];
                                meter.Spend(1);
                                if (workB[CandState + c] == 0) continue;
                                int outcome = RefuteByHulls(candidates[c].x, candidates[c].y);
                                if (meter.Stopped) return false;
                                workB[Tried + c] = 1;
                                if (outcome == 1) workB[CandState + c] = 0;
                                else
                                {
                                    workB[CandState + c] = 2;
                                    allRefuted = false;
                                }
                            }

                            if (meter.Stopped) return false;
                            if (allRefuted)
                            {
                                bool dealt = Conclude();
                                Tick(SecConclude);
                                if (meter.Stopped) return false;
                                if (dealt) return true;
                                for (int k = 0; k < n && !meter.Stopped; k++) { nodeSide[k] = workB[Reach + work[CompOf + k]]; meter.Spend(1); }
                                stats[StatVerdict] = VerdictSeparable;
                                return true;
                            }
                        }
                    }

                    if (meter.Stopped) return false;
                    if (!anyCut) return false;   // the hulls cannot confirm a contact: nothing left to try here
                    bool done = Conclude();
                    Tick(SecConclude);
                    if (meter.Stopped) return false;
                    if (done) return true;
                }

                return false;
            }

            private bool Overlap(int i, int j)
            {
                int a0 = work[Seeds + (2 * i)], a1 = work[Seeds + (2 * i) + 1], b0 = work[Seeds + (2 * j)], b1 = work[Seeds + (2 * j) + 1];
                return a0 == b0 || a0 == b1 || (a1 >= 0 && (a1 == b0 || a1 == b1));
            }

            // The minimum cut (unit capacities, blocked groups infinite) between two seeds; the cut's groups into work[Cut..],
            // their count, or -1 where no finite cut exists. workB[Reach..] holds the source side (by component).
            private int MinCut(int m, int groups, int si, int sj)
            {
                int src = m, dst = m + 1, nodes = m + 2;
                // Degrees: each group adds one arc at each end; a seed member adds an arc to src or dst (and the reverse).
                for (int v = 0; v <= nodes && !meter.Stopped; v++) { work[AdjStart + v] = 0; meter.Spend(1); }
                if (meter.Stopped) return -1;
                for (int g = 0; g < groups && !meter.Stopped; g++) { work[AdjStart + work[GroupA + g] + 1]++; work[AdjStart + work[GroupB + g] + 1]++; meter.Spend(1); }
                if (meter.Stopped) return -1;
                int s0 = work[Seeds + (2 * si)], s1 = work[Seeds + (2 * si) + 1], t0 = work[Seeds + (2 * sj)], t1 = work[Seeds + (2 * sj) + 1];
                work[AdjStart + s0 + 1]++; work[AdjStart + src + 1]++;
                if (s1 >= 0) { work[AdjStart + s1 + 1]++; work[AdjStart + src + 1]++; }
                work[AdjStart + t0 + 1]++; work[AdjStart + dst + 1]++;
                if (t1 >= 0) { work[AdjStart + t1 + 1]++; work[AdjStart + dst + 1]++; }
                for (int v = 0; v < nodes && !meter.Stopped; v++) { work[AdjStart + v + 1] += work[AdjStart + v]; meter.Spend(1); }
                if (meter.Stopped) return -1;
                int arcs = work[AdjStart + nodes];
                // Fill cursors in work[Queue..] (reused as cursors here). A stop in any loop below returns before the data it built is
                // read by the next one: nothing half-initialised is used as an index.
                for (int v = 0; v < nodes && !meter.Stopped; v++) { work[Queue + v] = work[AdjStart + v]; meter.Spend(1); }
                if (meter.Stopped) return -1;
                for (int g = 0; g < groups && !meter.Stopped; g++)
                {
                    int a = work[GroupA + g], b = work[GroupB + g], cap = workB[GroupBlocked + g] != 0 ? Inf : 1;
                    int ia = work[Queue + a]++, ib = work[Queue + b]++;
                    work[ArcTo + ia] = b; work[ArcCap + ia] = cap; work[ArcRev + ia] = ib;
                    work[ArcTo + ib] = a; work[ArcCap + ib] = cap; work[ArcRev + ib] = ia;
                    meter.Spend(1);   // one unit a group: its two arcs
                }

                if (meter.Stopped) return -1;
                AddSeedArcs(src, s0, true);
                if (s1 >= 0) AddSeedArcs(src, s1, true);
                AddSeedArcs(dst, t0, false);
                if (t1 >= 0) AddSeedArcs(dst, t1, false);
                Tick(SecCutBuild);
                Count(CntMinCutCalls, 1);
                Count(CntArcsBuilt, arcs);
                if (meter.Stopped) return -1;

                var counts = new FlowCounts();
                int flowValue = flow == 0
                    ? FlowKernel.EdmondsKarp(work, AdjStart, ArcTo, ArcRev, ArcCap, Queue, ParentArc, nodes, src, dst, measure == 2, ref counts, ref meter)
                    : FlowKernel.Dinic(work, AdjStart, ArcTo, ArcRev, ArcCap, Queue, Level, Cur, PathNode, PathArc, nodes, src, dst, measure == 2, ref counts, ref meter);
                Count(CntBfsDequeues, counts.dequeues);
                Count(CntAugmentations, counts.augmentations);
                Count(CntLevelBuilds, counts.levelBuilds);
                Count(CntArcChecks, counts.arcChecks);
                Count(CntPushes, counts.pushes);
                Tick(SecCutFlow);
                if (meter.Stopped) return -1;
                if (flowValue < 0) return -1;   // no finite cut between these seeds

                // The source side: reachable in the final residual graph (the same set for any maximum flow).
                FlowKernel.ReachFromSource(work, AdjStart, ArcTo, ArcCap, Queue, nodes, src, workB, Reach, ref meter);
                if (meter.Stopped) return -1;
                int cut = 0;
                for (int g = 0; g < groups && !meter.Stopped; g++)
                {
                    if (workB[Reach + work[GroupA + g]] != workB[Reach + work[GroupB + g]]) work[Cut + cut++] = g;
                    meter.Spend(1);
                }

                Tick(SecCutExtract);
                return cut;
            }

            private void AddSeedArcs(int terminal, int k, bool fromTerminal)
            {
                int it = work[Queue + terminal]++, ik = work[Queue + k]++;
                // fromTerminal: terminal -> k with Inf, k -> terminal with 0; else k -> terminal with Inf, terminal -> k with 0.
                work[ArcTo + it] = k; work[ArcCap + it] = fromTerminal ? Inf : 0; work[ArcRev + it] = ik;
                work[ArcTo + ik] = terminal; work[ArcCap + ik] = fromTerminal ? 0 : Inf; work[ArcRev + ik] = it;
            }

            // ---- the sorts (stable: ties keep the order given) ----------------------------------------------------------------

            private void SortByKey(int count)
            {
                // Insertion sort by key, stable; the lists are small per iteration relative to the search (candidates in a sample).
                for (int i = 1; i < count && !meter.Stopped; i++)
                {
                    int c = work[CandOrder + i];
                    double key = workD[KeyD + i];
                    int j = i - 1;
                    while (j >= 0 && workD[KeyD + j] > key && !meter.Stopped)
                    {
                        work[CandOrder + j + 1] = work[CandOrder + j];
                        workD[KeyD + j + 1] = workD[KeyD + j];
                        j--;
                        meter.Spend(1);
                    }

                    work[CandOrder + j + 1] = c;
                    workD[KeyD + j + 1] = key;
                    meter.Spend(1);
                }
            }

            // The three nearest triangle-only soft components of c by a single scan (Top3): each gap computed once with the same
            // BoxGap as the sort's, the same tie rule, the same order of the three; written to work[Nearest + 0..min(count, 3)).
            private void SelectNearest(int count, int c)
            {
                var top = new Top3();
                long compares = 0, moves = 0;
                double3 lo = work3[SoftLo + c], hi = work3[SoftHi + c];
                for (int j = 0; j < count && !meter.Stopped; j++)
                {
                    int x = work[Ts + j];
                    double gx = BoxGap(lo, hi, work3[SoftLo + x], work3[SoftHi + x]);
                    top.Offer(x, work[SoftRep + x], gx, ref compares, ref moves);
                    meter.Spend(1);
                }

                Count(CntNearestGaps, count);
                Count(CntNearestCompares, compares);
                Count(CntNearestMoves, moves);
                if (top.kept > 0) work[Nearest + 0] = top.x0;
                if (top.kept > 1) work[Nearest + 1] = top.x1;
                if (top.kept > 2) work[Nearest + 2] = top.x2;
            }

            private void SortNearest(int count, int c)
            {
                for (int i = 1; i < count && !meter.Stopped; i++)
                {
                    int x = work[Nearest + i];
                    double gx = BoxGap(work3[SoftLo + c], work3[SoftHi + c], work3[SoftLo + x], work3[SoftHi + x]);
                    Count(CntNearestGaps, 1);
                    meter.Spend(1);
                    int j = i - 1;
                    while (j >= 0 && !meter.Stopped)
                    {
                        int y = work[Nearest + j];
                        double gy = BoxGap(work3[SoftLo + c], work3[SoftHi + c], work3[SoftLo + y], work3[SoftHi + y]);
                        Count(CntNearestGaps, 1);
                        bool after = gy > gx || (gy == gx && work[SoftRep + y] > work[SoftRep + x]);
                        Count(CntNearestCompares, 1);
                        meter.Spend(1);   // one unit an iteration: the compare with its move
                        if (!after) break;
                        Count(CntNearestMoves, 1);
                        work[Nearest + j + 1] = y;
                        j--;
                    }

                    work[Nearest + j + 1] = x;
                }
            }

            private void SortToRefute(int count)
            {
                // By gap, largest first; ties keep the order given.
                Count(CntRefuteSorted, count);
                for (int i = 1; i < count && !meter.Stopped; i++)
                {
                    int c = work[ToRefute + i];
                    double g = gaps[c];
                    int j = i - 1;
                    while (j >= 0 && gaps[work[ToRefute + j]] < g && !meter.Stopped)
                    {
                        Count(CntRefuteMoves, 1);
                        work[ToRefute + j + 1] = work[ToRefute + j];
                        j--;
                        meter.Spend(1);
                    }

                    work[ToRefute + j + 1] = c;
                    meter.Spend(1);
                }
            }

            // ---- B: the hulls ---------------------------------------------------------------------------------------------------

            private int RefuteByHulls(int a, int b)
            {
                stats[StatTriedB]++;
                bool settled = Gjk(a, b, out double3 direction, out bool meet);
                Tick(SecGjk);   // GJK's time stops at its return; the projection below is its own section, called only when GJK settled apart
                Count(CntGjkCalls, 1);
                if (meter.Stopped) return 0;
                if (settled && !meet)
                {
                    double gap = ProjectionGap(direction, a, b);
                    Tick(SecProjection);
                    if (measure != 0) sections[CntProjectionCalls]++;
                    if (meter.Stopped) return 0;
                    if (gap > bounds[2])
                    {
                        stats[StatRefutedB]++;
                        return 1;
                    }
                }

                stats[StatAmbiguousB]++;
                return 0;
            }

            private double ProjectionGap(double3 axis, int a, int b)
            {
                double aLo = double.PositiveInfinity, aHi = double.NegativeInfinity, bLo = double.PositiveInfinity, bHi = double.NegativeInfinity;
                for (int i = nodeHullStart[a]; i < nodeHullStart[a + 1] && !meter.Stopped; i++) { double x = math.dot(axis, hull[i]); aLo = math.min(aLo, x); aHi = math.max(aHi, x); meter.Spend(1); }
                for (int i = nodeHullStart[b]; i < nodeHullStart[b + 1] && !meter.Stopped; i++) { double x = math.dot(axis, hull[i]); bLo = math.min(bLo, x); bHi = math.max(bHi, x); meter.Spend(1); }
                stats[StatVertexScans] += (nodeHullStart[a + 1] - nodeHullStart[a]) + (nodeHullStart[b + 1] - nodeHullStart[b]);
                return math.max(bLo - aHi, aLo - bHi);
            }

            private double3 Support(int node, double3 d)
            {
                double best = double.NegativeInfinity;
                double3 at = hull[nodeHullStart[node]];
                for (int i = nodeHullStart[node]; i < nodeHullStart[node + 1] && !meter.Stopped; i++)
                {
                    double x = math.dot(hull[i], d);
                    if (x > best) { best = x; at = hull[i]; }
                    meter.Spend(1);
                }

                stats[StatSupportQueries]++;
                stats[StatVertexScans] += nodeHullStart[node + 1] - nodeHullStart[node];
                return at;
            }

            private double3 Centroid(int node)
            {
                var c = new double3(0.0);
                for (int i = nodeHullStart[node]; i < nodeHullStart[node + 1] && !meter.Stopped; i++) { c += hull[i]; meter.Spend(1); }
                return c / (nodeHullStart[node + 1] - nodeHullStart[node]);
            }

            private bool Gjk(int a, int b, out double3 direction, out bool meet)
            {
                var simplex = new Simplex();
                direction = default;
                meet = false;
                double3 d = Centroid(a) - Centroid(b);
                if (math.lengthsq(d) <= 0.0) d = new double3(1.0, 0.0, 0.0);
                double3 v = d;
                for (int it = 0; it < gjkMaxIterations && !meter.Stopped; it++)
                {
                    stats[StatGjkIterations]++;
                    meter.Spend(1);
                    double3 w = Support(a, -v) - Support(b, v);
                    if (meter.Stopped) return false;
                    double vv = math.lengthsq(v);
                    if (vv <= 0.0)
                    {
                        meet = true;
                        return true;
                    }

                    if (vv - math.dot(v, w) <= 1e-12 * vv || (simplex.count > 0 && simplex.Contains(w)))
                    {
                        direction = v * (1.0 / math.sqrt(math.dot(v, v)));   // math.normalize: rsqrt(dot) * v
                        return true;
                    }

                    simplex.Add(w);
                    if (!ClosestToOrigin(ref simplex, out v))
                    {
                        meet = true;
                        return true;
                    }
                }

                // The iteration limit, not a conclusion: counted apart (GRAPH-CONNECT-4 §3-2), so that a NotFound reached with such
                // pairs among the ambiguous ones is an unfinished search, not a verdict. (A stop by the meter is the meter's.)
                if (!meter.Stopped) stats[StatGjkLimitHit]++;
                return false;
            }

            private struct Simplex
            {
                public double3 p0, p1, p2, p3;
                public int count;

                public double3 this[int i]
                {
                    get => i == 0 ? p0 : i == 1 ? p1 : i == 2 ? p2 : p3;
                    set { if (i == 0) p0 = value; else if (i == 1) p1 = value; else if (i == 2) p2 = value; else p3 = value; }
                }

                public void Add(double3 w) { this[count] = w; count++; }

                public bool Contains(double3 w)
                {
                    for (int i = 0; i < count; i++) if (math.all(this[i] == w)) return true;
                    return false;
                }

                public void Reduce(int keep)
                {
                    int kept = 0;
                    for (int i = 0; i < count; i++) if ((keep & (1 << i)) != 0) this[kept++] = this[i];
                    count = kept;
                }

                public void RemoveAt(int at)
                {
                    for (int i = at; i + 1 < count; i++) this[i] = this[i + 1];
                    count--;
                }
            }

            private static bool ClosestToOrigin(ref Simplex simplex, out double3 v)
            {
                var o = new double3(0.0);
                switch (simplex.count)
                {
                    case 1:
                        v = simplex[0];
                        return true;
                    case 2:
                    {
                        v = ClosestOnSegment(o, simplex[0], simplex[1], out double t);
                        if (t <= 0.0) simplex.RemoveAt(1); else if (t >= 1.0) simplex.RemoveAt(0);
                        return true;
                    }
                    case 3:
                    {
                        v = ClosestOnTriangle(o, simplex[0], simplex[1], simplex[2], out int keep);
                        simplex.Reduce(keep);
                        return true;
                    }
                    default:
                    {
                        bool inside = true;
                        double best = double.PositiveInfinity;
                        double3 bestV = default;
                        int bestKeep = 0;
                        for (int f = 0; f < 4; f++)
                        {
                            int f0 = f == 3 ? 1 : 0, f1 = f <= 1 ? 1 : 2, f2 = f == 0 ? 2 : 3, opposite = 3 - f;   // faces {0,1,2},{0,1,3},{0,2,3},{1,2,3}
                            double3 p = simplex[f0], q = simplex[f1], rr = simplex[f2], other = simplex[opposite];
                            double3 nrm = math.cross(q - p, rr - p);
                            double sideO = math.dot(nrm, o - p), sideOther = math.dot(nrm, other - p);
                            if (sideO * sideOther < 0.0 || sideOther == 0.0)
                            {
                                inside = false;
                                double3 cand = ClosestOnTriangle(o, p, q, rr, out int keep);
                                double l = math.lengthsq(cand);
                                if (l < best)
                                {
                                    best = l;
                                    bestV = cand;
                                    bestKeep = ((keep & 1) != 0 ? 1 << f0 : 0) | ((keep & 2) != 0 ? 1 << f1 : 0) | ((keep & 4) != 0 ? 1 << f2 : 0);
                                }
                            }
                        }

                        if (inside)
                        {
                            v = o;
                            return false;
                        }

                        v = bestV;
                        simplex.Reduce(bestKeep);
                        return true;
                    }
                }
            }

            private static double3 ClosestOnSegment(double3 p, double3 a, double3 b, out double t)
            {
                double3 e = b - a;
                double l = math.lengthsq(e);
                t = l > 0.0 ? math.clamp(math.dot(p - a, e) / l, 0.0, 1.0) : 0.0;
                return a + (e * t);
            }

            private static double3 ClosestOnTriangle(double3 p, double3 a, double3 b, double3 c, out int keep)
            {
                double3 ab = b - a, ac = c - a, ap = p - a;
                double d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
                if (d1 <= 0.0 && d2 <= 0.0) { keep = 1; return a; }
                double3 bp = p - b;
                double d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
                if (d3 >= 0.0 && d4 <= d3) { keep = 2; return b; }
                double vc = (d1 * d4) - (d3 * d2);
                if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0) { keep = 3; return a + (ab * (d1 / (d1 - d3))); }
                double3 cp = p - c;
                double d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
                if (d6 >= 0.0 && d5 <= d6) { keep = 4; return c; }
                double vb = (d5 * d2) - (d1 * d6);
                if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0) { keep = 5; return a + (ac * (d2 / (d2 - d6))); }
                double va = (d3 * d6) - (d5 * d4);
                if (va <= 0.0 && d4 - d3 >= 0.0 && d5 - d6 >= 0.0) { keep = 6; return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))); }
                double denom = 1.0 / (va + vb + vc);
                keep = 7;
                return a + (ab * (vb * denom)) + (ac * (vc * denom));
            }

            // ---- union-find in the work area (a unit a parent reference) --------------------------------------------------------

            private int Find(int at, int i) => FindRoot(work, at, i, ref meter);

            private void Union(int at, int a, int b)
            {
                a = Find(at, a);
                b = Find(at, b);
                if (a == b) return;
                work[at + math.max(a, b)] = math.min(a, b);
            }
        }

        // ---- shared pieces -------------------------------------------------------------------------------------------------------------

        private static double3 Owner(float3 p, in float4x4 m)
        {
            double x = ((double)m[0][0] * p.x) + ((double)m[1][0] * p.y) + ((double)m[2][0] * p.z) + m[3][0];
            double y = ((double)m[0][1] * p.x) + ((double)m[1][1] * p.y) + ((double)m[2][1] * p.z) + m[3][1];
            double z = ((double)m[0][2] * p.x) + ((double)m[1][2] * p.y) + ((double)m[2][2] * p.z) + m[3][2];
            return new double3(x, y, z);
        }

        // As the managed TermBound: the largest |entry| times the largest |coordinate| (float), or the largest |translation|; a unit a point.
        private static double TermBound(float3* points, int start, int count, in float4x4 m, ref WorkMeter meter)
        {
            double entry = 0.0, translation = 0.0, coordinate = 0.0;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) entry = math.max(entry, math.abs((double)m[c][r]));
                translation = math.max(translation, math.abs((double)m[3][r]));
            }

            for (int i = start; i < start + count && !meter.Stopped; i++) { coordinate = math.max(coordinate, (double)math.cmax(math.abs(points[i]))); meter.Spend(1); }
            return math.max(entry * coordinate, translation);
        }

        private static double BoxGap(double3 lo1, double3 hi1, double3 lo2, double3 hi2) => math.length(math.max(math.max(lo2 - hi1, lo1 - hi2), 0.0));
    }
}
