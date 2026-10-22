using System;
using System.Diagnostics;
using System.Globalization;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling.LowLevel.Unsafe;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>Which implementation of the graph candidate's AB decides: M0 the managed one, N0 the native-container
    /// port run as managed code, N1 the same port run through Burst.</summary>
    internal enum CandidateEngine { M0 = 0, N0 = 1, N1 = 2 }

    /// <summary>
    /// The stages A and B of <see cref="SpatialSplitGraphCandidate"/> (graph-candidate 3, commit 9c91466f) ported to
    /// structs, NativeArrays and explicit work areas, so that the same code runs as managed code (N0) or through Burst
    /// (N1) and the cost of the managed objects can be told from the cost of the search. The numbers, the margin, the
    /// certificates, the limits, the orders of the candidates, seeds and support points, and the tie rules are those of the
    /// managed version; what the managed version left to a Dictionary's or an unstable sort's order is fixed here as the
    /// order of first appearance (candidates in (i, j) order; the cut's candidates by gap, ties in that order).
    /// <para>
    /// Not ported, on purpose: stage C (the faces, edges and BVHs are not built at all -- the managed version builds the
    /// faces of every triangle and convex even for AB, and that is part of its preparation cost), the clock inside the
    /// search (the deadline is applied once, at the return: a decision made after it is discarded as Undecided), the
    /// JSON, the files and the exact verification, which stay in managed code around the jobs.
    /// </para>
    /// </summary>
    internal static class SpatialSplitGraphCandidateNative
    {
        public const string Version = "graph-candidate-native 8 (2026-10-23, the candidate enumeration by a sweep along x on request): AB of graph-candidate 3 in native containers; N0 = the job run as managed code, N1 = the job through Burst (FloatMode.Strict); every area released on any failure, out of memory a failure result; optional section times (measure 1) and diagnostic counts (measure 2); the nearest triangle-only seeds by a top-3 selection or the full sort; the minimum cut by Edmonds-Karp (flow ek) or Dinic (flow dinic)";

        // ---- the stats the jobs write (one long each) ------------------------------------------------------------------------

        private const int StatNodePairs = 0, StatApartByBoxes = 1, StatCandidatesA = 2, StatTriedB = 3, StatRefutedB = 4, StatAmbiguousB = 5, StatGjkIterations = 6,
            StatSupportQueries = 7, StatVertexScans = 8, StatCutAttempts = 9, StatCutLimitHit = 10, StatMeshComponents = 11, StatDefiniteComponents = 12, StatVerdict = 13,
            StatStop = 14, StatStage = 15, StatError = 16, StatManagedMarker = 17, StatNodes = 18, StatCount = 19;

        private const int VerdictUndecided = 0, VerdictSeparable = 1, VerdictNotSeparable = 2, VerdictNotFound = 3;

        // ---- the measurement (TL unit 2026-10-22 night): exclusive sections of the search job, ticks of ProfilerUnsafeUtility.Timestamp
        // read only at the section boundaries (measure 1), and diagnostic counts (measure 2); nothing is read with measure 0.
        internal const int SecRegroup = 0, SecSeeds = 1, SecSortNearest = 2, SecCutBuild = 3, SecCutFlow = 4, SecCutExtract = 5, SecCollect = 6, SecSortToRefute = 7,
            SecGjk = 8, SecProjection = 9, SecConclude = 10, SectionTimes = 11;
        internal const int CntProjectionCalls = 11, CntGjkCalls = 12, CntMinCutCalls = 13, CntArcsBuilt = 14, CntBfsDequeues = 15, CntAugmentations = 16,
            CntNearestCompares = 17, CntNearestMoves = 18, CntRefuteSorted = 19, CntRefuteMoves = 20, CntNearestGaps = 21, CntLevelBuilds = 22, CntArcChecks = 23, CntPushes = 24, SearchSectionCount = 25;

        // The preparation job's sections (measure 1) and counts (measure 2), after the search's.
        internal const int PrepOwnerBounds = 25, PrepSlots = 26, PrepComponents = 27, PrepConvexes = 28, PrepMeshBoxesCounts = 29, PrepMeshFill = 30, PrepPairs = 31, PrepSectionTimes = 7;
        internal const int PcNamed = 32, PcTriangles = 33, PcComponents = 34, PcNodes = 35, PcHullStored = 36, PcSlotCompares = 37, PcParentRefs = 38, PcPairsTotal = 39, PcGapEvals = 40, PcApart = 41,
            PcCandidates = 42, PcCandGrowths = 43, PcCandGrowthOld = 44, PcGapGrowths = 45, PcGapGrowthOld = 46,
            PcSweepSkipped = 47, PcGapExcluded = 48, PcNodeSortCompares = 49, PcNodeSortMoves = 50, PcKeySortCompares = 51, PcKeySortMoves = 52, PcKeysSorted = 53, SectionCount = 54;
        internal static readonly string[] PrepSectionNames = { "ownerBounds", "slots", "components", "convexes", "meshBoxesCounts", "meshFill", "pairs" };
        internal static readonly string[] PrepCounterNames = { "named", "triangles", "components", "nodes", "hullStored", "slotCompares", "parentRefs", "pairsTotal", "gapEvals", "apart", "candidates", "candGrowths", "candGrowthOld", "gapGrowths", "gapGrowthOld",
            "sweepSkipped", "gapExcluded", "nodeSortCompares", "nodeSortMoves", "keySortCompares", "keySortMoves", "keysSorted" };

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
            public long gapEvals, sweepSkipped, excluded, candGrowths, candGrowthOld, gapGrowths, gapGrowthOld, nodeSortCompares, nodeSortMoves, keySortCompares, keySortMoves, keysSorted;
        }

        /// <summary>
        /// The candidate enumeration of stage A: every pair of nodes whose boxes are not apart by more than far
        /// (far = 2m + 4E1), in the original (i, j) order (i &lt; j), with its BoxGap on (i, j). AllPairs is the baseline (every
        /// pair evaluated); Sweep sorts the nodes by their box's least x (ties by index), and for each node in that order
        /// scans the following ones until lo.x(b) - hi.x(a) &gt; far: that rounded difference is the x term BoxGap(i, j) itself
        /// computes (max(lo2 - hi1, lo1 - hi2) with the same operands), and BoxGap = sqrt(fl(dx^2 + dy^2 + dz^2)) &gt;= dx for
        /// correctly rounded IEEE operations (sqrt(fl(dx*dx)) = dx, and the sum and the sqrt are monotone), so every pair
        /// skipped has BoxGap &gt; far and would have been excluded; a pair within the threshold, at it, or whose boundary is
        /// doubtful is kept and judged by the same BoxGap. The pairs found are restored to the (i, j) order by a heap sort of
        /// their keys; the gaps are those computed on (i, j), bit for bit.
        /// </summary>
        internal static class PairKernel
        {
            public static void AllPairs(NativeArray<double3> lo, NativeArray<double3> hi, int n, double far, NativeList<int2> candidates, NativeList<double> gaps, bool count, ref EnumerateCounts c)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        double gap = BoxGap(lo[i], hi[i], lo[j], hi[j]);
                        if (count) c.gapEvals++;
                        if (gap > far)
                        {
                            if (count) c.excluded++;
                            continue;
                        }

                        Append(candidates, gaps, new int2(i, j), gap, count, ref c);
                    }
                }
            }

            public static void Sweep(NativeArray<double3> lo, NativeArray<double3> hi, int n, double far, NativeArray<NodeKey> order, NativeList<KeyGap> keyGaps, NativeList<int2> candidates, NativeList<double> gaps, bool count, ref EnumerateCounts c)
            {
                for (int k = 0; k < n; k++) order[k] = new NodeKey { x = lo[k].x, index = k };
                SortNodes(order, n, count, ref c);
                keyGaps.Clear();
                for (int p = 0; p < n; p++)
                {
                    int a = order[p].index;
                    double hiAx = hi[a].x;
                    for (int q = p + 1; q < n; q++)
                    {
                        int b = order[q].index;
                        if (lo[b].x - hiAx > far)
                        {
                            if (count) c.sweepSkipped += n - q;
                            break;   // every later node has a larger or equal lo.x: the same difference is larger or equal
                        }

                        int i = math.min(a, b), j = math.max(a, b);
                        double gap = BoxGap(lo[i], hi[i], lo[j], hi[j]);
                        if (count) c.gapEvals++;
                        if (gap > far)
                        {
                            if (count) c.excluded++;
                            continue;
                        }

                        keyGaps.Add(new KeyGap { key = ((long)i * n) + j, gap = gap });
                    }
                }

                SortKeys(keyGaps, count, ref c);
                if (count) c.keysSorted = keyGaps.Length;
                for (int k = 0; k < keyGaps.Length; k++)
                {
                    KeyGap kg = keyGaps[k];
                    Append(candidates, gaps, new int2((int)(kg.key / n), (int)(kg.key % n)), kg.gap, count, ref c);
                }
            }

            private static void Append(NativeList<int2> candidates, NativeList<double> gaps, int2 pair, double gap, bool count, ref EnumerateCounts c)
            {
                if (count)
                {
                    if (candidates.Length == candidates.Capacity) { c.candGrowths++; c.candGrowthOld += candidates.Length; }
                    if (gaps.Length == gaps.Capacity) { c.gapGrowths++; c.gapGrowthOld += gaps.Length; }
                }

                candidates.Add(pair);
                gaps.Add(gap);
            }

            // Heap sorts (deterministic, in place, no recursion); the compares and moves are counted on request.
            private static void SortNodes(NativeArray<NodeKey> a, int n, bool count, ref EnumerateCounts c)
            {
                for (int start = (n / 2) - 1; start >= 0; start--) SiftNodes(a, start, n, count, ref c);
                for (int end = n - 1; end > 0; end--)
                {
                    NodeKey tmp = a[0]; a[0] = a[end]; a[end] = tmp;
                    if (count) c.nodeSortMoves++;
                    SiftNodes(a, 0, end, count, ref c);
                }
            }

            private static void SiftNodes(NativeArray<NodeKey> a, int root, int n, bool count, ref EnumerateCounts c)
            {
                while (true)
                {
                    int child = (2 * root) + 1;
                    if (child >= n) return;
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

            private static void SortKeys(NativeList<KeyGap> a, bool count, ref EnumerateCounts c)
            {
                int n = a.Length;
                for (int start = (n / 2) - 1; start >= 0; start--) SiftKeys(a, start, n, count, ref c);
                for (int end = n - 1; end > 0; end--)
                {
                    KeyGap tmp = a[0]; a[0] = a[end]; a[end] = tmp;
                    if (count) c.keySortMoves++;
                    SiftKeys(a, 0, end, count, ref c);
                }
            }

            private static void SiftKeys(NativeList<KeyGap> a, int root, int n, bool count, ref EnumerateCounts c)
            {
                while (true)
                {
                    int child = (2 * root) + 1;
                    if (child >= n) return;
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
        internal static readonly string[] SectionNames = { "regroup", "seeds", "sortNearest", "cutBuild", "cutFlow", "cutExtract", "collect", "sortToRefute", "gjk", "projection", "conclude" };
        internal static readonly string[] CounterNames = { "projectionCalls", "gjkCalls", "minCutCalls", "arcsBuilt", "bfsDequeues", "augmentations", "nearestCompares", "nearestMoves", "refuteSorted", "refuteMoves", "nearestGaps", "levelBuilds", "arcChecks", "pushes" };

        /// <summary>The counts of one maximum-flow computation (flow-internal: they differ between the methods).</summary>
        internal struct FlowCounts
        {
            public long dequeues, augmentations, levelBuilds, arcChecks, pushes;
        }

        /// <summary>
        /// The maximum flow between two terminals over arcs in a CSR (adjStart[nodes + 1], arcTo, arcRev, arcCap, all in one
        /// int array at the offsets given), and the source side of the final residual graph. Edmonds-Karp (the baseline) and
        /// Dinic (level graph + blocking flow with an explicit stack and current-arc pointers, no recursion) give the same
        /// value and, since the residual reachability of any maximum flow is the same set, the same source side and cut.
        /// Capacities: a group's arcs 1 or Inf both ways, a terminal's Inf one way and 0 back; a flow of Inf or more means no
        /// finite cut (-1). The counts are kept only when asked (the diagnostic run).
        /// </summary>
        internal static class FlowKernel
        {
            public const int Inf = 1 << 28;

            // The tail of an arc: the node whose adjacency range holds it (a binary search of the starts).
            private static int Tail(NativeArray<int> w, int adjStart, int nodes, int e)
            {
                int lo = 0, hi = nodes - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (w[adjStart + mid] <= e) lo = mid; else hi = mid - 1;
                }

                return lo;
            }

            public static int EdmondsKarp(NativeArray<int> w, int adjStart, int arcTo, int arcRev, int arcCap, int queue, int parent, int nodes, int src, int dst, bool count, ref FlowCounts c)
            {
                int flow = 0;
                while (true)
                {
                    for (int v = 0; v < nodes; v++) w[parent + v] = -1;
                    w[parent + src] = -2;
                    int head = 0, tail = 0;
                    w[queue + tail++] = src;
                    while (head < tail && w[parent + dst] == -1)
                    {
                        int u = w[queue + head++];
                        if (count) c.dequeues++;
                        for (int e = w[adjStart + u]; e < w[adjStart + u + 1]; e++)
                        {
                            if (count) c.arcChecks++;
                            int v = w[arcTo + e];
                            if (w[parent + v] == -1 && w[arcCap + e] > 0)
                            {
                                w[parent + v] = e;
                                w[queue + tail++] = v;
                            }
                        }
                    }

                    if (w[parent + dst] == -1) return flow;
                    int push = Inf;
                    for (int v = dst; v != src; v = Tail(w, adjStart, nodes, w[parent + v])) push = math.min(push, w[arcCap + w[parent + v]]);
                    if (push >= Inf) return -1;
                    for (int v = dst; v != src; v = Tail(w, adjStart, nodes, w[parent + v]))
                    {
                        int e = w[parent + v];
                        w[arcCap + e] -= push;
                        w[arcCap + w[arcRev + e]] += push;
                    }

                    flow += push;
                    if (count) { c.augmentations++; c.pushes++; }
                    if (flow >= Inf) return -1;
                }
            }

            public static int Dinic(NativeArray<int> w, int adjStart, int arcTo, int arcRev, int arcCap, int queue, int level, int cur, int pathNode, int pathArc, int nodes, int src, int dst, bool count, ref FlowCounts c)
            {
                int flow = 0;
                while (true)
                {
                    // The level graph: breadth-first over the residual arcs.
                    for (int v = 0; v < nodes; v++) w[level + v] = -1;
                    w[level + src] = 0;
                    int head = 0, tail = 0;
                    w[queue + tail++] = src;
                    while (head < tail)
                    {
                        int u = w[queue + head++];
                        if (count) c.dequeues++;
                        for (int e = w[adjStart + u]; e < w[adjStart + u + 1]; e++)
                        {
                            if (count) c.arcChecks++;
                            int v = w[arcTo + e];
                            if (w[arcCap + e] > 0 && w[level + v] < 0)
                            {
                                w[level + v] = w[level + u] + 1;
                                w[queue + tail++] = v;
                            }
                        }
                    }

                    if (count) c.levelBuilds++;
                    if (w[level + dst] < 0) return flow;

                    // A blocking flow: depth-first along the levels with an explicit path and current-arc pointers.
                    for (int v = 0; v < nodes; v++) w[cur + v] = w[adjStart + v];
                    int depth = 0;
                    w[pathNode] = src;
                    while (true)
                    {
                        int u = w[pathNode + depth];
                        if (u == dst)
                        {
                            int push = Inf;
                            for (int i = 0; i < depth; i++) push = math.min(push, w[arcCap + w[pathArc + i]]);
                            if (push >= Inf) return -1;
                            int retreat = depth;
                            for (int i = 0; i < depth; i++)
                            {
                                int e = w[pathArc + i];
                                w[arcCap + e] -= push;
                                w[arcCap + w[arcRev + e]] += push;
                                if (w[arcCap + e] == 0 && retreat == depth) retreat = i;
                            }

                            flow += push;
                            if (count) { c.augmentations++; c.pushes++; }
                            if (flow >= Inf) return -1;
                            depth = retreat;   // back to the tail of the first saturated arc
                            continue;
                        }

                        bool advanced = false;
                        while (w[cur + u] < w[adjStart + u + 1])
                        {
                            int e = w[cur + u];
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
            }

            /// <summary>The source side: what the final residual graph reaches from the source (a stack in the queue's room).</summary>
            public static void ReachFromSource(NativeArray<int> w, int adjStart, int arcTo, int arcCap, int queue, int nodes, int src, NativeArray<byte> reach, int reachAt)
            {
                for (int v = 0; v < nodes; v++) reach[reachAt + v] = 0;
                int top = 0;
                w[queue + top++] = src;
                reach[reachAt + src] = 1;
                while (top > 0)
                {
                    int u = w[queue + --top];
                    for (int e = w[adjStart + u]; e < w[adjStart + u + 1]; e++)
                    {
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

        /// <summary>
        /// The first three of a sequence by (gap ascending, representative ascending), kept in order while the sequence is
        /// scanned once: the order the full insertion sort (<see cref="SortAllReference"/>) gives its first three. Shared by
        /// the search job and the tests' static helper, so that both use one rule.
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
        private const int StopNone = 0, StopCutLimit = 1, StopHulls = 2, StopExhausted = 3;
        private const int ReasonAllConvexesOne = 1, ReasonAllTrianglesOne = 2, ReasonFewerThanTwo = 3;

        /// <summary>The timings and sizes of one native decision, beside the <see cref="CandidateResult"/>.</summary>
        internal sealed class NativeRun
        {
            public bool burstRequested, burstRan, burstEnabled;
            public double secondsConvert, secondsPrepare, secondsAllocate, secondsSearch, secondsFinish, secondsTotal;
            public long nativeBytes, candidateCapacity;

            /// <summary>0: no clock read in the jobs; 1: the search's section times (ms, exclusive); 2: the diagnostic counts (its time is not a measurement).</summary>
            public int measure;
            public bool nearestSelection, flowDinic, pairsSweep;
            public long sweepBytes, keyGapsCapacity;   // the sweep's added areas (capacities), 0 for the baseline

            /// <summary>On request (a verification run): FNV-1a 64 over the candidate pairs and the gaps' bits, in order; "" otherwise.</summary>
            public string prepareHash = "";
            public double[] sectionMs = new double[SectionTimes];
            public long[] counters = new long[SearchSectionCount - SectionTimes];
            public double residualMs;

            /// <summary>The preparation job's exclusive sections (measure 1), its residual against the Stopwatch, and its counts (measure 2).</summary>
            public double[] prepareSectionMs = new double[PrepSectionTimes];
            public double prepareResidualMs;
            public long[] prepareCounters = new long[SectionCount - PcNamed];
            public long candidateCapacityInitial, gapsCapacityInitial, gapsCapacity;   // the search's Stopwatch time less the sections' sum (the job's scheduling, the ticks, what no section covers)
        }

        // ---- the entry ------------------------------------------------------------------------------------------------------------

        /// <summary>Decides AB with the native port; <paramref name="burst"/> runs the jobs through Burst (N1), else as managed code (N0).</summary>
        /// <summary>Tests: the preparation job's outputs, copied after the job (never in a measured run).</summary>
        internal sealed class PrepareSnapshot
        {
            public int nodes;
            public double3[] owner, nodeLo, nodeHi, hull;
            public int[] cornerSlot, triComponent, nodeHullStart, nodeTriangleCount;
            public double[] bounds, gaps;
            public int2[] candidates;
        }

        internal static CandidateResult Decide(SpatialSplitSample sample, CandidateSettings settings, bool burst, out NativeRun run, PrepareSnapshot snapshot = null)
        {
            long begin = Stopwatch.GetTimestamp();
            run = new NativeRun { burstRequested = burst, burstEnabled = BurstCompiler.IsEnabled };
            var r = new CandidateResult { stage = "A" };
            if (settings.stages != CandidateStages.AB)
            {
                r.verdict = CandidateVerdict.InvalidInput;
                r.reason = "the native port decides AB only";
                r.seconds = Since(begin);
                return r;
            }

            try
            {
                DecideInto(sample, settings, burst, r, run, begin, snapshot);
            }
            catch (ArgumentException e)
            {
                r.verdict = CandidateVerdict.InvalidInput;
                r.reason = e.Message;
            }
            catch (InvalidOperationException e)
            {
                // An allocation refused by the allocator (or the test hook): nothing is said; the areas created were released.
                r.verdict = CandidateVerdict.Undecided;
                r.stop = "allocation";
                r.reason = "a native allocation failed: " + e.Message;
            }
            catch (OutOfMemoryException e)
            {
                // Memory ran out -- in a native area or in a managed one (the result arrays): the same failure result as above
                // (TL review 3: the real OOM must not escape to the caller, as the managed version's does not).
                r.verdict = CandidateVerdict.Undecided;
                r.stop = "allocation";
                r.reason = "out of memory: " + e.Message;
            }

            // The deadline, at the return only (the jobs carry no clock): a soft limit, as in the managed version's return check.
            if (r.verdict != CandidateVerdict.InvalidInput && settings.secondsLimit > 0.0 && Since(begin) > settings.secondsLimit)
            {
                r.timedOut = true;
                r.verdict = CandidateVerdict.Undecided;
                r.stop = "time";
                r.reason = "the time limit passed before the return (stage " + r.stage + "; the native port checks the time at the return only)";
                r.convexPositive = null;
                r.trianglePositive = null;
            }

            r.seconds = Since(begin);
            run.secondsTotal = r.seconds;
            return r;
        }

        private static double Since(long begin) => (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;

        /// <summary>The native areas created by this class and not yet released (tests: it returns to its value after any decision).</summary>
        internal static long LiveNativeAllocations;

        // Counts an allocation just made (and its bytes); the test hook throws after the given number of allocations.
        private static bool failAsOutOfMemory;

        private static void Track(ref int allocations, int failAt, ref long bytes, long size)
        {
            LiveNativeAllocations++;
            allocations++;
            bytes += size;
            if (failAt >= 0 && allocations > failAt)
            {
                if (failAsOutOfMemory) throw new OutOfMemoryException("test: the native allocation " + allocations + " ran out of memory");
                throw new InvalidOperationException("test: the native allocation " + allocations + " failed");
            }
        }

        private static void Free<T>(ref NativeArray<T> a) where T : struct
        {
            if (!a.IsCreated) return;
            a.Dispose();
            a = default;
            LiveNativeAllocations--;
        }

        private static void Free<T>(ref NativeList<T> a) where T : unmanaged
        {
            if (!a.IsCreated) return;
            a.Dispose();
            a = default;
            LiveNativeAllocations--;
        }

        private static void DecideInto(SpatialSplitSample sample, CandidateSettings settings, bool burst, CandidateResult r, NativeRun run, long begin, PrepareSnapshot snapshot)
        {
            // ---- 1. The input checked and copied into native arrays (the same checks as the managed version's BuildScene).
            if (sample == null) throw new ArgumentException("no sample");
            if (!math.isfinite(sample.margin) || sample.margin <= 0f) throw new ArgumentException("the margin is " + sample.margin.ToString("R", CultureInfo.InvariantCulture));
            if (!SpatialSplitReferenceDecider.Finite(in sample.geometryToOwner) || !SpatialSplitReferenceDecider.Finite(in sample.shapeToOwner)) throw new ArgumentException("a transform is not finite");
            if (sample.indices.Length % 3 != 0) throw new ArgumentException("the index count is not a multiple of three");
            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != vertexBytes) throw new ArgumentException("the vertex is " + sample.vertexStructBytes + " bytes, this build's " + vertexBytes);
            int named = sample.vertexIds.Length;
            if (sample.vertexBytes.Length != named * vertexBytes) throw new ArgumentException("the vertex bytes are not " + named + " vertices");
            int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            int triangleCount = sample.indices.Length / 3;
            int convexCount = sample.convexes.Length;
            int convexVertexTotal = 0;
            for (int c = 0; c < convexCount; c++)
            {
                SpatialSplitSample.Convex convex = sample.convexes[c];
                int faceCount = convex.faceOffsets.Length - 1;
                if (convex.vertices.Length < 4 || faceCount < 4) throw new ArgumentException("convex " + c + " of " + convex.vertices.Length + " vertices and " + faceCount + " faces");
                for (int f = 0; f < faceCount; f++)
                {
                    int from = convex.faceOffsets[f], to = convex.faceOffsets[f + 1];
                    if (to - from < 3 || from < 0 || to > convex.faceIndices.Length) throw new ArgumentException("convex " + c + ": face " + f + " of " + (to - from) + " vertices");
                    for (int i = from; i < to; i++)
                    {
                        if (convex.faceIndices[i] < 0 || convex.faceIndices[i] >= convex.vertices.Length) throw new ArgumentException("convex " + c + ": face " + f + " names vertex " + convex.faceIndices[i]);
                    }
                }

                convexVertexTotal += convex.vertices.Length;
            }

            r.convexCount = convexCount;
            r.triangleCount = triangleCount;
            long bytes = 0;
            int allocations = 0;
            int failAt = settings.testFailNativeAllocation;
            failAsOutOfMemory = settings.testFailWithOutOfMemory;
            NativeArray<float3> local = default, convexLocal = default;
            NativeArray<uint> ids = default, indices = default;
            NativeArray<int> convexStart = default, cornerSlot = default, triComponent = default, nodeHullStart = default, nodeTriangleCount = default, scratchInt = default, work = default;
            NativeArray<double3> owner = default, nodeLo = default, nodeHi = default, hull = default, work3 = default;
            NativeArray<double> bounds = default, workD = default;
            NativeArray<long> stats = default, sections = default;
            NativeList<int2> candidates = default;
            NativeList<double> gaps = default;
            NativeArray<byte> nodeSide = default, workB = default;
            NativeArray<NodeKey> order = default;
            NativeList<KeyGap> keyGaps = default;
            int nodeCapacity = convexCount + triangleCount;
            int hullCapacity = convexVertexTotal + (3 * triangleCount);
            try
            {
                local = new NativeArray<float3>(Math.Max(1, named), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)local.Length * 12);
                ids = new NativeArray<uint>(Math.Max(1, named), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)ids.Length * 4);
                indices = new NativeArray<uint>(Math.Max(1, sample.indices.Length), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)indices.Length * 4);
                convexLocal = new NativeArray<float3>(Math.Max(1, convexVertexTotal), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)convexLocal.Length * 12);
                convexStart = new NativeArray<int>(convexCount + 1, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, (long)convexStart.Length * 4);
                owner = new NativeArray<double3>(Math.Max(1, named), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)owner.Length * 24);
                cornerSlot = new NativeArray<int>(Math.Max(1, sample.indices.Length), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)cornerSlot.Length * 4);
                triComponent = new NativeArray<int>(Math.Max(1, triangleCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)triComponent.Length * 4);
                nodeLo = new NativeArray<double3>(Math.Max(1, nodeCapacity), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)nodeLo.Length * 24);
                nodeHi = new NativeArray<double3>(Math.Max(1, nodeCapacity), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)nodeHi.Length * 24);
                nodeHullStart = new NativeArray<int>(Math.Max(1, nodeCapacity + 1), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)nodeHullStart.Length * 4);
                nodeTriangleCount = new NativeArray<int>(Math.Max(1, nodeCapacity), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)nodeTriangleCount.Length * 4);
                hull = new NativeArray<double3>(Math.Max(1, hullCapacity), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)hull.Length * 24);
                scratchInt = new NativeArray<int>(Math.Max(1, Math.Max(named, triangleCount) * 2 + nodeCapacity), Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)scratchInt.Length * 4);
                bounds = new NativeArray<double>(4, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, 4 * 8);
                stats = new NativeArray<long>(StatCount, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, StatCount * 8);
                sections = new NativeArray<long>(SectionCount, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, SectionCount * 8);
                run.measure = settings.measure;
                candidates = new NativeList<int2>(Math.Max(16, Math.Min(nodeCapacity * 4, 1 << 16)), Allocator.Persistent); Track(ref allocations, failAt, ref bytes, 0);
                gaps = new NativeList<double>(candidates.Capacity, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, 0);
                // The sweep's areas: the node order (16 B a node) and the pairs before their order is restored (16 B a pair, grows with
                // the candidates: never a square-sized area). A job's container fields must all be assigned, so every-pair mode
                // carries one-element placeholders (32 B, counted).
                order = new NativeArray<NodeKey>(settings.pairsSweep ? Math.Max(1, nodeCapacity) : 1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)order.Length * 16);
                keyGaps = new NativeList<KeyGap>(settings.pairsSweep ? candidates.Capacity : 1, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, 0);

                for (int i = 0; i < named; i++)
                {
                    int at = (i * vertexBytes) + at0;
                    var p = new float3(BitConverter.ToSingle(sample.vertexBytes, at), BitConverter.ToSingle(sample.vertexBytes, at + 4), BitConverter.ToSingle(sample.vertexBytes, at + 8));
                    if (!math.all(math.isfinite(p))) throw new ArgumentException("vertex " + sample.vertexIds[i] + " is not finite");
                    local[i] = p;
                    ids[i] = sample.vertexIds[i];
                }

                for (int i = 0; i < sample.indices.Length; i++) indices[i] = sample.indices[i];
                int fill = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    convexStart[c] = fill;
                    foreach (float3 p in sample.convexes[c].vertices)
                    {
                        if (!math.all(math.isfinite(p))) throw new ArgumentException("a convex vertex is not finite");
                        convexLocal[fill++] = p;
                    }
                }

                convexStart[convexCount] = fill;
                run.secondsConvert = Since(begin);

                // ---- 2. The preparation: owner coordinates, the bounds, the mesh components, the nodes' boxes and hulls, stage A.
                long t1 = Stopwatch.GetTimestamp();
                run.candidateCapacityInitial = candidates.Capacity;
                run.gapsCapacityInitial = gaps.Capacity;
                var prepare = new PrepareJob
                {
                    local = local, ids = ids, indices = indices, convexLocal = convexLocal, convexStart = convexStart, geometryToOwner = sample.geometryToOwner, shapeToOwner = sample.shapeToOwner,
                    margin = sample.margin, vertexViewLength = sample.vertexViewLength, named = named, triangleCount = triangleCount, convexCount = convexCount,
                    owner = owner, cornerSlot = cornerSlot, triComponent = triComponent, nodeLo = nodeLo, nodeHi = nodeHi, nodeHullStart = nodeHullStart, nodeTriangleCount = nodeTriangleCount,
                    hull = hull, scratch = scratchInt, bounds = bounds, stats = stats, candidates = candidates, gaps = gaps, measure = settings.measure, sections = sections,
                    pairs = settings.pairsSweep ? 1 : 0, order = order, keyGaps = keyGaps,
                };
                run.pairsSweep = settings.pairsSweep;
                if (burst) prepare.Run(); else prepare.Execute();
                run.secondsPrepare = Since(t1);
                run.gapsCapacity = gaps.Capacity;
                if (settings.hashPrepare && stats[StatError] == 0)
                {
                    // Outside every section (after the prepare job's time was taken): the whole candidate list and the gaps, hashed
                    // for the full-set agreement check. Never used in a timing run.
                    ulong h = 14695981039346656037UL;
                    void Mix(ulong v) { for (int b = 0; b < 8; b++) { h ^= (v >> (8 * b)) & 0xFF; h *= 1099511628211UL; } }
                    Mix((ulong)candidates.Length);
                    for (int k = 0; k < candidates.Length; k++) { Mix((ulong)(uint)candidates[k].x); Mix((ulong)(uint)candidates[k].y); Mix((ulong)BitConverter.DoubleToInt64Bits(gaps[k])); }
                    run.prepareHash = h.ToString("x16");
                }

                if (snapshot != null && stats[StatError] == 0)
                {
                    // Tests only: a copy of the preparation's outputs (never in a measured run).
                    int nodesOut = (int)stats[StatNodes];
                    snapshot.nodes = nodesOut;
                    snapshot.owner = owner.ToArray();
                    snapshot.cornerSlot = cornerSlot.ToArray();
                    snapshot.triComponent = triComponent.ToArray();
                    snapshot.nodeLo = new double3[nodesOut]; snapshot.nodeHi = new double3[nodesOut]; snapshot.nodeHullStart = new int[nodesOut + 1]; snapshot.nodeTriangleCount = new int[nodesOut];
                    for (int i = 0; i < nodesOut; i++) { snapshot.nodeLo[i] = nodeLo[i]; snapshot.nodeHi[i] = nodeHi[i]; snapshot.nodeHullStart[i] = nodeHullStart[i]; snapshot.nodeTriangleCount[i] = nodeTriangleCount[i]; }
                    snapshot.nodeHullStart[nodesOut] = nodeHullStart[nodesOut];
                    snapshot.hull = new double3[nodeHullStart[nodesOut]];
                    for (int i = 0; i < snapshot.hull.Length; i++) snapshot.hull[i] = hull[i];
                    snapshot.bounds = bounds.ToArray();
                    snapshot.candidates = candidates.AsArray().ToArray();
                    snapshot.gaps = gaps.AsArray().ToArray();
                }

                if (stats[StatError] != 0)
                {
                    throw new ArgumentException(stats[StatError] == 1 ? "an index names a vertex which the sample does not hold" : "the preparation failed (" + stats[StatError] + ")");
                }

                int n = (int)stats[StatNodes];
                int candidateCount = candidates.Length;
                run.candidateCapacity = candidates.Capacity;
                bytes += ((long)candidates.Capacity * 8) + ((long)gaps.Capacity * 8);   // the lists' capacity after the growth (not a peak of old and new areas during a growth)
                bytes += (long)keyGaps.Capacity * 16;
                if (settings.pairsSweep) { run.sweepBytes = ((long)order.Length * 16) + ((long)keyGaps.Capacity * 16); run.keyGapsCapacity = keyGaps.Capacity; }
                r.meshComponents = (int)stats[StatMeshComponents];
                r.nodePairs = stats[StatNodePairs];
                r.pairsApartByBoxes = stats[StatApartByBoxes];
                r.candidatesA = candidateCount;

                // ---- 3. The work areas of the search, sized by what the preparation found, then the search.
                long t2 = Stopwatch.GetTimestamp();
                int m = Math.Max(1, n);
                int cand = Math.Max(1, candidateCount);
                int arcs = (2 * cand) + (4 * m);
                nodeSide = new NativeArray<byte>(m, Allocator.Persistent); Track(ref allocations, failAt, ref bytes, m);
                work = new NativeArray<int>((19 * m) + (8 * cand) + (3 * arcs) + 76, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)work.Length * 4);   // + 4 (m + 3) for Dinic's areas
                work3 = new NativeArray<double3>(4 * m, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)work3.Length * 24);
                workD = new NativeArray<double>(2 * cand, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)workD.Length * 8);
                workB = new NativeArray<byte>((6 * m) + (3 * cand) + 8, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); Track(ref allocations, failAt, ref bytes, (long)workB.Length);
                run.secondsAllocate = Since(t2);
                long t3 = Stopwatch.GetTimestamp();
                var search = new SearchJob
                {
                    n = n, convexCount = convexCount, candidateCount = candidateCount, maxCutAttempts = settings.maxCutAttempts, gjkMaxIterations = settings.gjkMaxIterations,
                    nodeLo = nodeLo, nodeHi = nodeHi, nodeHullStart = nodeHullStart, nodeTriangleCount = nodeTriangleCount, hull = hull, candidates = candidates.AsArray(), gaps = gaps.AsArray(),
                    bounds = bounds, stats = stats, nodeSide = nodeSide, work = work, work3 = work3, workD = workD, workB = workB,
                    measure = settings.measure, sections = sections, nearest = settings.nearestSelection ? 1 : 0, flow = settings.flowDinic ? 1 : 0,
                };
                run.nearestSelection = settings.nearestSelection;
                run.flowDinic = settings.flowDinic;
                if (burst) search.Run(); else search.Execute();
                run.secondsSearch = Since(t3);
                if (settings.measure == 1)
                {
                    var ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
                    double sum = 0.0;
                    for (int i = 0; i < SectionTimes; i++)
                    {
                        run.sectionMs[i] = (double)sections[i] * ratio.Numerator / ratio.Denominator / 1e6;
                        sum += run.sectionMs[i];
                    }

                    run.residualMs = (run.secondsSearch * 1000.0) - sum;
                }

                if (settings.measure != 0) for (int i = SectionTimes; i < SearchSectionCount; i++) run.counters[i - SectionTimes] = sections[i];
                if (settings.measure == 1)
                {
                    var ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
                    double sum = 0.0;
                    for (int i = 0; i < PrepSectionTimes; i++)
                    {
                        run.prepareSectionMs[i] = (double)sections[PrepOwnerBounds + i] * ratio.Numerator / ratio.Denominator / 1e6;
                        sum += run.prepareSectionMs[i];
                    }

                    run.prepareResidualMs = (run.secondsPrepare * 1000.0) - sum;
                }

                if (settings.measure == 2) for (int i = 0; i < SectionCount - PcNamed; i++) run.prepareCounters[i] = sections[PcNamed + i];

                // ---- 4. The result read back.
                long t4 = Stopwatch.GetTimestamp();
                run.burstRan = burst && stats[StatManagedMarker] == 0;
                r.definiteComponents = (int)stats[StatDefiniteComponents];
                r.triedB = stats[StatTriedB];
                r.refutedB = stats[StatRefutedB];
                r.ambiguousB = stats[StatAmbiguousB];
                r.gjkIterations = stats[StatGjkIterations];
                r.supportQueries = stats[StatSupportQueries];
                r.vertexScans = stats[StatVertexScans];
                r.cutAttempts = (int)stats[StatCutAttempts];
                r.cutLimitHit = stats[StatCutLimitHit] != 0;
                r.stage = stats[StatStage] == 0 ? "A" : "B";
                switch ((int)stats[StatVerdict])
                {
                    case VerdictSeparable:
                        r.verdict = CandidateVerdict.Separable;
                        r.convexPositive = new bool[convexCount];
                        r.trianglePositive = new bool[triangleCount];
                        for (int c = 0; c < convexCount; c++)
                        {
                            r.convexPositive[c] = nodeSide[c] != 0;
                            if (r.convexPositive[c]) r.positiveConvexes++; else r.negativeConvexes++;
                        }

                        for (int k = 0; k < triangleCount; k++)
                        {
                            r.trianglePositive[k] = nodeSide[convexCount + triComponent[k]] != 0;
                            if (r.trianglePositive[k]) r.positiveTriangles++; else r.negativeTriangles++;
                        }

                        break;
                    case VerdictNotSeparable:
                        r.verdict = CandidateVerdict.NotSeparable;
                        r.reason = stats[StatStop] == ReasonAllConvexesOne ? "every convex is in one component of definite contacts"
                            : stats[StatStop] == ReasonAllTrianglesOne ? "every triangle is in one component of definite contacts" : "fewer than two convexes or two triangles";
                        break;
                    default:
                        r.verdict = CandidateVerdict.NotFound;
                        r.stop = stats[StatStop] == StopCutLimit ? "cut-limit" : stats[StatStop] == StopHulls ? "hulls" : "exhausted";
                        int left = 0, ambiguous = 0;
                        for (int i = 0; i < candidateCount; i++) { if (workB[i] == 1) left++; else if (workB[i] == 2) ambiguous++; }
                        r.reason = "no cut of the candidates could be certified at stage " + r.stage + " (" + left + " candidates, " + ambiguous + " ambiguous, 0 at a limit; stop: " + r.stop + ")";
                        break;
                }

                run.secondsFinish = Since(t4);
            }
            finally
            {
                // Whatever happened -- an input refused after the first allocation, an allocation failed, a job threw -- every
                // native area that was created is released here (TL review, P2); the live count proves it in the tests.
                Free(ref local); Free(ref ids); Free(ref indices); Free(ref convexLocal); Free(ref convexStart);
                Free(ref owner); Free(ref cornerSlot); Free(ref triComponent); Free(ref nodeLo); Free(ref nodeHi); Free(ref nodeHullStart); Free(ref nodeTriangleCount);
                Free(ref hull); Free(ref scratchInt); Free(ref bounds); Free(ref stats); Free(ref sections); Free(ref candidates); Free(ref gaps);
                Free(ref nodeSide); Free(ref work); Free(ref work3); Free(ref workD); Free(ref workB);
                Free(ref order); Free(ref keyGaps);
                run.nativeBytes = bytes;
            }
        }

        /// <summary>Runs both jobs once on a tiny input so that Burst has compiled them before anything is timed.</summary>
        internal static void Warmup(int measure = 0, bool nearestSelection = true, bool flowDinic = false, bool pairsSweep = false)
        {
            // Two unit boxes at x 0..1 and 2..3; a triangle in the plane z = x through the first and over the second (apart by
            // 1/sqrt 2), a triangle inside the second: the boxes overlap, the hulls refute the candidates: a cut, GJK and
            // projections run; both jobs, the flow and the clock of the measure mode are exercised before anything is timed.
            float3[] positions =
            {
                new float3(-1f, -1f, -1f), new float3(5f, -1f, 5f), new float3(5f, 5f, 5f),
                new float3(2.2f, 0.2f, 0.5f), new float3(2.8f, 0.2f, 0.5f), new float3(2.5f, 0.8f, 0.5f),
            };
            var sample = new SpatialSplitSample
            {
                formatVersion = SpatialSplitCaptureSink.FormatVersion, vertexStructBytes = UnsafeUtility.SizeOf<VpRenderVertex>(), margin = 1f / 4096f, vertexViewLength = 6,
                geometryToOwner = float4x4.identity, shapeToOwner = float4x4.identity, indices = new uint[] { 0, 1, 2, 3, 4, 5 }, vertexIds = new uint[] { 0, 1, 2, 3, 4, 5 },
                convexes = new[] { WarmupBox(new float3(0f), new float3(1f)), WarmupBox(new float3(2f, 0f, 0f), new float3(3f, 1f, 1f)) },
            };
            int size = sample.vertexStructBytes;
            int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            sample.vertexBytes = new byte[6 * size];
            for (int i = 0; i < 6; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(positions[i].x), 0, sample.vertexBytes, (i * size) + at0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(positions[i].y), 0, sample.vertexBytes, (i * size) + at0 + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(positions[i].z), 0, sample.vertexBytes, (i * size) + at0 + 8, 4);
            }

            var settings = new CandidateSettings { stages = CandidateStages.AB, secondsLimit = 0.0, measure = measure, nearestSelection = nearestSelection, flowDinic = flowDinic, pairsSweep = pairsSweep };
            Decide(sample, settings, true, out _);
            Decide(sample, settings, false, out _);
        }

        private static SpatialSplitSample.Convex WarmupBox(float3 lo, float3 hi)
        {
            var v = new float3[8];
            for (int i = 0; i < 8; i++) v[i] = new float3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            var offsets = new int[7];
            var indices = new int[24];
            for (int f = 0; f < 6; f++)
            {
                offsets[f] = 4 * f;
                for (int i = 0; i < 4; i++) indices[(4 * f) + i] = faces[f][i];
            }

            offsets[6] = 24;
            return new SpatialSplitSample.Convex { vertices = v, faceOffsets = offsets, faceIndices = indices };
        }

        // ---- the preparation job ---------------------------------------------------------------------------------------------------

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        private struct PrepareJob : IJob
        {
            [ReadOnly] public NativeArray<float3> local;
            [ReadOnly] public NativeArray<uint> ids;
            [ReadOnly] public NativeArray<uint> indices;
            [ReadOnly] public NativeArray<float3> convexLocal;
            [ReadOnly] public NativeArray<int> convexStart;
            public float4x4 geometryToOwner, shapeToOwner;
            public float margin;
            public int vertexViewLength, named, triangleCount, convexCount;

            public NativeArray<double3> owner;
            public NativeArray<int> cornerSlot, triComponent, nodeHullStart, nodeTriangleCount, scratch;
            public NativeArray<double3> nodeLo, nodeHi, hull;
            public NativeArray<double> bounds;   // m2, e1, far, near
            public NativeArray<long> stats;
            public NativeList<int2> candidates;
            public NativeList<double> gaps;
            public int measure;                  // 0 off (no clock read, no count), 1 the section times, 2 the diagnostic counts
            public NativeArray<long> sections;
            public int pairs;                    // 0: every pair of nodes evaluated (the baseline); 1: the sweep along x
            public NativeArray<NodeKey> order;   // the sweep's node order (n), created only for the sweep
            public NativeList<KeyGap> keyGaps;   // the sweep's pairs before the order is restored, created only for the sweep
            private long last;

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

            // Union-find over the triangles with the parent reads counted (measure 2).
            private int FindCounted(int at, int i)
            {
                while (scratch[at + i] != i)
                {
                    Count(PcParentRefs, 2);
                    scratch[at + i] = scratch[at + scratch[at + i]];
                    i = scratch[at + i];
                }

                Count(PcParentRefs, 1);
                return i;
            }

            private void UnionCounted(int at, int x, int y)
            {
                x = FindCounted(at, x);
                y = FindCounted(at, y);
                if (x == y) return;
                scratch[at + math.max(x, y)] = math.min(x, y);
            }

            public void Execute()
            {
                MarkManaged(stats);
                if (measure == 1) last = ProfilerUnsafeUtility.Timestamp;
                for (int i = 0; i < StatCount; i++) if (i != StatManagedMarker) stats[i] = 0;

                // Owner coordinates, as the managed OwnerDouble: ((double)E(i,0) * p.x) + ((double)E(i,1) * p.y) + ((double)E(i,2) * p.z) + E(i,3).
                double bound = 1.0;
                for (int i = 0; i < named; i++)
                {
                    double3 o = Owner(local[i], in geometryToOwner);
                    owner[i] = o;
                    bound = math.max(bound, math.cmax(math.abs(o)));
                }

                bound = math.max(bound, TermBound(local, 0, named, in geometryToOwner));
                double convexBound = 0.0;
                for (int c = 0; c < convexCount; c++) convexBound = math.max(convexBound, TermBound(convexLocal, convexStart[c], convexStart[c + 1] - convexStart[c], in shapeToOwner));
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

                // The corners' slots: binary search of the sorted ids, an id under the view's length only.
                for (int i = 0; i < 3 * triangleCount; i++)
                {
                    uint id = indices[i];
                    int at = id < (uint)math.max(0, vertexViewLength) ? Find(id) : -1;
                    if (at < 0)
                    {
                        stats[StatError] = 1;
                        return;
                    }

                    cornerSlot[i] = at;
                }

                Tick(PrepSlots);

                // Mesh components: triangles sharing a vertex index, united; ids in the order of the first triangle.
                // scratch: [0, T) union-find parents, [T, T + named) the first triangle of a slot, [T + named, ...) component of a root.
                int parents = 0, firstOf = triangleCount, compOfRoot = triangleCount + named;
                for (int k = 0; k < triangleCount; k++) scratch[parents + k] = k;
                for (int i = 0; i < named; i++) scratch[firstOf + i] = -1;
                for (int k = 0; k < triangleCount; k++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int slot = cornerSlot[(3 * k) + c];
                        int other = scratch[firstOf + slot];
                        if (other >= 0) UnionCounted(parents, k, other);
                        else scratch[firstOf + slot] = k;
                    }
                }

                for (int k = 0; k < triangleCount; k++) scratch[compOfRoot + k] = -1;
                int components = 0;
                for (int k = 0; k < triangleCount; k++)
                {
                    int root = FindCounted(parents, k);
                    if (scratch[compOfRoot + root] < 0) scratch[compOfRoot + root] = components++;
                    triComponent[k] = scratch[compOfRoot + root];
                }

                int n = convexCount + components;
                stats[StatMeshComponents] = components;
                stats[StatNodes] = n;
                Tick(PrepComponents);
                Count(PcComponents, components);
                Count(PcNodes, n);

                // The nodes: convexes (their vertices the hull), then the components (their distinct corners, in the order of
                // first appearance, the hull). Hull slots: convexes first in order, then the components.
                for (int i = 0; i < n; i++)
                {
                    nodeLo[i] = new double3(double.PositiveInfinity);
                    nodeHi[i] = new double3(double.NegativeInfinity);
                    nodeTriangleCount[i] = 0;
                }

                int fill = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    nodeHullStart[c] = fill;
                    for (int i = convexStart[c]; i < convexStart[c + 1]; i++)
                    {
                        double3 o = Owner(convexLocal[i], in shapeToOwner);
                        hull[fill++] = o;
                        nodeLo[c] = math.min(nodeLo[c], o);
                        nodeHi[c] = math.max(nodeHi[c], o);
                    }
                }

                Tick(PrepConvexes);   // includes the node arrays' initialisation above

                // Component boxes and distinct-corner counts (the first-triangle array is reused as "seen by component", the
                // component-of-root array as the count by node).
                for (int i = 0; i < named; i++) scratch[firstOf + i] = -1;
                for (int node = convexCount; node < n; node++) scratch[compOfRoot + node] = 0;
                for (int k = 0; k < triangleCount; k++)
                {
                    int node = convexCount + triComponent[k];
                    nodeTriangleCount[node]++;
                    double3 a = owner[cornerSlot[3 * k]], b = owner[cornerSlot[(3 * k) + 1]], c3 = owner[cornerSlot[(3 * k) + 2]];
                    double3 lo = math.min(a, math.min(b, c3)), hi = math.max(a, math.max(b, c3));
                    nodeLo[node] = math.min(nodeLo[node], lo);
                    nodeHi[node] = math.max(nodeHi[node], hi);
                    for (int c = 0; c < 3; c++)
                    {
                        int slot = cornerSlot[(3 * k) + c];
                        if (scratch[firstOf + slot] != node)
                        {
                            scratch[firstOf + slot] = node;
                            scratch[compOfRoot + node]++;
                        }
                    }
                }

                // The distinct counts by node: prefix sums into the hull starts.
                for (int node = convexCount; node < n; node++)
                {
                    nodeHullStart[node] = fill;
                    fill += scratch[compOfRoot + node];
                }

                nodeHullStart[n] = fill;
                Tick(PrepMeshBoxesCounts);
                Count(PcHullStored, fill);
                for (int i = 0; i < named; i++) scratch[firstOf + i] = -1;
                for (int node = convexCount; node < n; node++) scratch[compOfRoot + node] = nodeHullStart[node];   // the fill cursor
                for (int k = 0; k < triangleCount; k++)
                {
                    int node = convexCount + triComponent[k];
                    for (int c = 0; c < 3; c++)
                    {
                        int slot = cornerSlot[(3 * k) + c];
                        if (scratch[firstOf + slot] != node)
                        {
                            scratch[firstOf + slot] = node;
                            hull[scratch[compOfRoot + node]++] = owner[slot];
                        }
                    }
                }

                Tick(PrepMeshFill);

                // Stage A: the boxes; a pair apart by more than 2m + 4E1 is apart, the rest candidates in (i, j) order (the lists
                // grow as needed: the growths are counted in the diagnostic run).
                double far = bounds[2];
                long total = (long)n * (n - 1) / 2;
                stats[StatNodePairs] = total;
                Count(PcPairsTotal, total);
                var ec = new EnumerateCounts();
                if (pairs == 0) PairKernel.AllPairs(nodeLo, nodeHi, n, far, candidates, gaps, measure == 2, ref ec);
                else PairKernel.Sweep(nodeLo, nodeHi, n, far, order, keyGaps, candidates, gaps, measure == 2, ref ec);
                stats[StatCandidatesA] = candidates.Length;
                stats[StatApartByBoxes] = total - candidates.Length;   // the pairs skipped by the sweep and those excluded by BoxGap, together (as before: every non-candidate)
                Tick(PrepPairs);
                Count(PcApart, stats[StatApartByBoxes]);
                Count(PcCandidates, candidates.Length);
                Count(PcGapEvals, ec.gapEvals);
                Count(PcCandGrowths, ec.candGrowths);
                Count(PcCandGrowthOld, ec.candGrowthOld);
                Count(PcGapGrowths, ec.gapGrowths);
                Count(PcGapGrowthOld, ec.gapGrowthOld);
                Count(PcSweepSkipped, ec.sweepSkipped);
                Count(PcGapExcluded, ec.excluded);
                Count(PcNodeSortCompares, ec.nodeSortCompares);
                Count(PcNodeSortMoves, ec.nodeSortMoves);
                Count(PcKeySortCompares, ec.keySortCompares);
                Count(PcKeySortMoves, ec.keySortMoves);
                Count(PcKeysSorted, ec.keysSorted);
            }

            private int Find(uint id)
            {
                int lo = 0, hi = named - 1;
                while (lo <= hi)
                {
                    Count(PcSlotCompares, 1);
                    int mid = lo + ((hi - lo) >> 1);
                    uint v = ids[mid];
                    if (v == id) return mid;
                    if (v < id) lo = mid + 1; else hi = mid - 1;
                }

                return -1;
            }
        }

        // ---- the search job (stage A's conclusion, stage B, the placement) ---------------------------------------------------------

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
        private struct SearchJob : IJob
        {
            public int n, convexCount, candidateCount, maxCutAttempts, gjkMaxIterations;
            [ReadOnly] public NativeArray<double3> nodeLo, nodeHi, hull;
            [ReadOnly] public NativeArray<int> nodeHullStart, nodeTriangleCount;
            [ReadOnly] public NativeArray<int2> candidates;
            [ReadOnly] public NativeArray<double> gaps, bounds;
            public NativeArray<long> stats;
            public NativeArray<byte> nodeSide;
            public NativeArray<int> work;
            public NativeArray<double3> work3;
            public NativeArray<double> workD;
            public NativeArray<byte> workB;
            public int measure;                  // 0 off (no clock read), 1 the section times, 2 the diagnostic counts
            public int nearest;                  // 0: the full insertion sort of the triangle-only soft components (the baseline); 1: the selection of the first three
            public int flow;                     // 0: Edmonds-Karp (the baseline); 1: Dinic
            public NativeArray<long> sections;   // SectionCount: ticks by section (measure 1), counts (measure 2); projection calls in both
            private long last;

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
                Mark();
                for (int i = 0; i < n; i++) work[Definite + i] = i;
                for (int i = 0; i < candidateCount; i++) { workB[CandState + i] = 1; workB[Tried + i] = 0; }
                stats[StatStage] = 0;
                bool concluded = Conclude();
                Tick(SecConclude);
                if (concluded) return;
                stats[StatStage] = 1;
                if (Search())
                {
                    Tick(SecConclude);
                    return;
                }

                stats[StatVerdict] = VerdictNotFound;
                stats[StatStop] = stats[StatCutLimitHit] != 0 ? StopCutLimit : StopHulls;
                Tick(SecConclude);
            }

            // ---- the graph: components, the deal ------------------------------------------------------------------------------

            private bool Conclude()
            {
                // Definite components: counts of convexes and triangles by root; NotSeparable from the definite contacts alone.
                int convexes = convexCount, triangles = 0;
                for (int i = convexCount; i < n; i++) triangles += nodeTriangleCount[i];
                for (int i = 0; i < n; i++) { work[Nearest + i] = 0; work[Cs + i] = 0; }   // Nearest: convexes by root; Cs: triangles by root (temporary use)
                int definite = 0;
                for (int i = 0; i < n; i++)
                {
                    int root = Find(Definite, i);
                    if (work[Nearest + root] == 0 && work[Cs + root] == 0) definite++;
                    if (i < convexCount) work[Nearest + root]++; else work[Cs + root] += nodeTriangleCount[i];
                }

                stats[StatDefiniteComponents] = definite;
                int allC = 0, allT = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Find(Definite, i) != i) continue;
                    if (work[Nearest + i] == convexes && convexes > 0) allC++;
                    if (work[Cs + i] == triangles && triangles > 0) allT++;
                }

                if (allC > 0 || allT > 0 || convexes < 2 || triangles < 2)
                {
                    stats[StatVerdict] = VerdictNotSeparable;
                    stats[StatStop] = allC > 0 ? ReasonAllConvexesOne : allT > 0 ? ReasonAllTrianglesOne : ReasonFewerThanTwo;
                    return true;
                }

                // The conservative graph: the definite contacts and every candidate left.
                for (int i = 0; i < n; i++) work[Graph + i] = i;
                for (int i = 0; i < n; i++) Union(Graph, i, Find(Definite, i));
                for (int c = 0; c < candidateCount; c++)
                {
                    if (workB[CandState + c] == 0) continue;
                    Union(Graph, candidates[c].x, candidates[c].y);
                }

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
                for (int i = 0; i < n; i++) { workB[HasC + i] = 0; workB[HasT + i] = 0; work[SoftOrder + i] = -1; }
                int order = 0;
                for (int i = 0; i < n; i++)
                {
                    int k = Find(Graph, i);
                    if (work[SoftOrder + k] < 0) { work[SoftOrder + k] = order; work[Ts + order] = k; order++; }
                    if (i < convexCount) workB[HasC + k] = 1; else workB[HasT + k] = 1;
                }

                int firstFull = -1, fulls = 0, firstC = -1, cs = 0, firstT = -1, ts = 0;
                for (int o = 0; o < order; o++)
                {
                    int k = work[Ts + o];
                    bool c = workB[HasC + k] != 0, t = workB[HasT + k] != 0;
                    if (c && t) { fulls++; if (firstFull < 0) firstFull = k; }
                    else if (c) { cs++; if (firstC < 0) firstC = k; }
                    else { ts++; if (firstT < 0) firstT = k; }
                }

                int chosenA, chosenB = -1;
                if (fulls >= 2 || (fulls == 1 && cs >= 1 && ts >= 1)) chosenA = firstFull;
                else if (fulls == 0 && cs >= 2 && ts >= 2) { chosenA = firstC; chosenB = firstT; }
                else return false;
                for (int i = 0; i < n; i++)
                {
                    int k = Find(Graph, i);
                    nodeSide[i] = (byte)(k == chosenA || k == chosenB ? 1 : 0);
                }

                return true;
            }

            // ---- the search along minimum cuts ------------------------------------------------------------------------------------

            private bool Search()
            {
                while (true)
                {
                    if (stats[StatCutAttempts] >= maxCutAttempts)
                    {
                        stats[StatCutLimitHit] = 1;
                        return false;
                    }

                    // The definite components as the graph's nodes (compact ids in node order).
                    int m = 0;
                    for (int i = 0; i < n; i++) work[SoftRep + i] = -1;   // root -> compact id (temporary)
                    for (int i = 0; i < n; i++)
                    {
                        int root = Find(Definite, i);
                        if (work[SoftRep + root] < 0) work[SoftRep + root] = m++;
                        work[CompOf + i] = work[SoftRep + root];
                    }

                    for (int k = 0; k < m; k++)
                    {
                        workB[HasC + k] = 0;
                        workB[HasT + k] = 0;
                        work3[CompLo + k] = new double3(double.PositiveInfinity);
                        work3[CompHi + k] = new double3(double.NegativeInfinity);
                    }

                    for (int i = 0; i < n; i++)
                    {
                        int k = work[CompOf + i];
                        if (i < convexCount) workB[HasC + k] = 1; else workB[HasT + k] = 1;
                        work3[CompLo + k] = math.min(work3[CompLo + k], nodeLo[i]);
                        work3[CompHi + k] = math.max(work3[CompHi + k], nodeHi[i]);
                    }

                    // The edges between components: the live candidates grouped by their component pair, groups in the order of
                    // first appearance (the managed Dictionary's insertion order), a group blocked when any of its candidates is
                    // ambiguous, at a limit, or already tried at this stage.
                    int live = 0;
                    for (int c = 0; c < candidateCount; c++)
                    {
                        if (workB[CandState + c] == 0) continue;
                        int a = work[CompOf + candidates[c].x], b = work[CompOf + candidates[c].y];
                        if (a == b) continue;
                        work[CandOrder + live] = c;
                        workD[KeyD + live] = ((double)math.min(a, b) * m) + math.max(a, b);
                        live++;
                    }

                    SortByKey(live);
                    int groups = 0;
                    for (int t = 0; t < live; t++)
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
                    }

                    work[GroupStart + groups] = live;
                    Tick(SecRegroup);

                    // The seeds, from the soft components (definite + blocked edges).
                    for (int k = 0; k < m; k++) { work[SoftParent + k] = k; }
                    for (int g = 0; g < groups; g++) if (workB[GroupBlocked + g] != 0) Union(SoftParent, work[GroupA + g], work[GroupB + g]);
                    int softCount = 0;
                    for (int k = 0; k < m; k++) work[SoftRep + k] = -1;
                    for (int k = 0; k < m; k++)
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
                    }

                    int seeds = 0, cs = 0, ts = 0;
                    for (int o = 0; o < softCount; o++)
                    {
                        int root = work[SoftOrder + o];
                        bool c = workB[SoftHasC + root] != 0, t = workB[SoftHasT + root] != 0;
                        if (c && t) { work[Seeds + (2 * seeds)] = work[SoftRep + root]; work[Seeds + (2 * seeds) + 1] = -1; seeds++; }
                        else if (c) work[Cs + cs++] = root;
                        else work[Ts + ts++] = root;
                    }

                    // cs is already in the order of the representatives (roots appear in the order of their smallest member).
                    for (int i = 0; i < math.min(cs, 3); i++)
                    {
                        int c = work[Cs + i];
                        Tick(SecSeeds);
                        if (nearest == 0)
                        {
                            for (int j = 0; j < ts; j++) work[Nearest + j] = work[Ts + j];
                            SortNearest(ts, c);
                        }
                        else
                        {
                            SelectNearest(ts, c);
                        }

                        Tick(SecSortNearest);
                        for (int j = 0; j < math.min(ts, 3); j++)
                        {
                            work[Seeds + (2 * seeds)] = work[SoftRep + c];
                            work[Seeds + (2 * seeds) + 1] = work[SoftRep + work[Nearest + j]];
                            seeds++;
                        }
                    }

                    Tick(SecSeeds);
                    bool anyCut = false;
                    for (int i = 0; i < seeds && !anyCut; i++)
                    {
                        for (int j = i + 1; j < seeds && !anyCut; j++)
                        {
                            if (Overlap(i, j)) continue;
                            if (stats[StatCutAttempts] >= maxCutAttempts)
                            {
                                stats[StatCutLimitHit] = 1;
                                return false;
                            }

                            stats[StatCutAttempts]++;
                            int cutSize = MinCut(m, groups, i, j);
                            if (cutSize < 0) continue;
                            anyCut = true;

                            // The cut's candidates, the largest box gap first, ties in the order of the groups and the candidates.
                            int count = 0;
                            for (int q = 0; q < cutSize; q++)
                            {
                                int g = work[Cut + q];
                                for (int t = work[GroupStart + g]; t < work[GroupStart + g + 1]; t++) work[ToRefute + count++] = work[CandOrder + t];
                            }

                            Tick(SecCollect);
                            SortToRefute(count);
                            Tick(SecSortToRefute);
                            bool allRefuted = true;
                            for (int q = 0; q < count; q++)
                            {
                                int c = work[ToRefute + q];
                                if (workB[CandState + c] == 0) continue;
                                int outcome = RefuteByHulls(candidates[c].x, candidates[c].y);
                                workB[Tried + c] = 1;
                                if (outcome == 1) workB[CandState + c] = 0;
                                else
                                {
                                    workB[CandState + c] = 2;
                                    allRefuted = false;
                                }
                            }

                            if (allRefuted)
                            {
                                bool dealt = Conclude();
                                Tick(SecConclude);
                                if (dealt) return true;
                                for (int k = 0; k < n; k++) nodeSide[k] = workB[Reach + work[CompOf + k]];
                                stats[StatVerdict] = VerdictSeparable;
                                return true;
                            }
                        }
                    }

                    if (!anyCut) return false;   // the hulls cannot confirm a contact: nothing left to try here
                    bool done = Conclude();
                    Tick(SecConclude);
                    if (done) return true;
                }
            }

            private bool Overlap(int i, int j)
            {
                int a0 = work[Seeds + (2 * i)], a1 = work[Seeds + (2 * i) + 1], b0 = work[Seeds + (2 * j)], b1 = work[Seeds + (2 * j) + 1];
                return a0 == b0 || a0 == b1 || (a1 >= 0 && (a1 == b0 || a1 == b1));
            }

            // The minimum cut (Edmonds-Karp, unit capacities, blocked groups infinite) between two seeds; the cut's groups
            // into work[Cut..], their count, or -1 where no finite cut exists. workB[Reach..] holds the source side (by component).
            private int MinCut(int m, int groups, int si, int sj)
            {
                int src = m, dst = m + 1, nodes = m + 2;
                // Degrees: each group adds one arc at each end; a seed member adds an arc to src or dst (and the reverse).
                for (int v = 0; v <= nodes; v++) work[AdjStart + v] = 0;
                for (int g = 0; g < groups; g++) { work[AdjStart + work[GroupA + g] + 1]++; work[AdjStart + work[GroupB + g] + 1]++; }
                int s0 = work[Seeds + (2 * si)], s1 = work[Seeds + (2 * si) + 1], t0 = work[Seeds + (2 * sj)], t1 = work[Seeds + (2 * sj) + 1];
                work[AdjStart + s0 + 1]++; work[AdjStart + src + 1]++;
                if (s1 >= 0) { work[AdjStart + s1 + 1]++; work[AdjStart + src + 1]++; }
                work[AdjStart + t0 + 1]++; work[AdjStart + dst + 1]++;
                if (t1 >= 0) { work[AdjStart + t1 + 1]++; work[AdjStart + dst + 1]++; }
                for (int v = 0; v < nodes; v++) work[AdjStart + v + 1] += work[AdjStart + v];
                int arcs = work[AdjStart + nodes];
                // Fill cursors in work[Queue..] (reused as cursors here).
                for (int v = 0; v < nodes; v++) work[Queue + v] = work[AdjStart + v];
                for (int g = 0; g < groups; g++)
                {
                    int a = work[GroupA + g], b = work[GroupB + g], cap = workB[GroupBlocked + g] != 0 ? Inf : 1;
                    int ia = work[Queue + a]++, ib = work[Queue + b]++;
                    work[ArcTo + ia] = b; work[ArcCap + ia] = cap; work[ArcRev + ia] = ib;
                    work[ArcTo + ib] = a; work[ArcCap + ib] = cap; work[ArcRev + ib] = ia;
                }

                AddSeedArcs(src, s0, true);
                if (s1 >= 0) AddSeedArcs(src, s1, true);
                AddSeedArcs(dst, t0, false);
                if (t1 >= 0) AddSeedArcs(dst, t1, false);
                Tick(SecCutBuild);
                Count(CntMinCutCalls, 1);
                Count(CntArcsBuilt, arcs);

                var counts = new FlowCounts();
                int flowValue = flow == 0
                    ? FlowKernel.EdmondsKarp(work, AdjStart, ArcTo, ArcRev, ArcCap, Queue, ParentArc, nodes, src, dst, measure == 2, ref counts)
                    : FlowKernel.Dinic(work, AdjStart, ArcTo, ArcRev, ArcCap, Queue, Level, Cur, PathNode, PathArc, nodes, src, dst, measure == 2, ref counts);
                Count(CntBfsDequeues, counts.dequeues);
                Count(CntAugmentations, counts.augmentations);
                Count(CntLevelBuilds, counts.levelBuilds);
                Count(CntArcChecks, counts.arcChecks);
                Count(CntPushes, counts.pushes);
                Tick(SecCutFlow);
                if (flowValue < 0) return -1;   // no finite cut between these seeds

                // The source side: reachable in the final residual graph (the same set for any maximum flow).
                FlowKernel.ReachFromSource(work, AdjStart, ArcTo, ArcCap, Queue, nodes, src, workB, Reach);
                int cut = 0;
                for (int g = 0; g < groups; g++)
                {
                    if (workB[Reach + work[GroupA + g]] != workB[Reach + work[GroupB + g]]) work[Cut + cut++] = g;
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
                for (int i = 1; i < count; i++)
                {
                    int c = work[CandOrder + i];
                    double key = workD[KeyD + i];
                    int j = i - 1;
                    while (j >= 0 && workD[KeyD + j] > key)
                    {
                        work[CandOrder + j + 1] = work[CandOrder + j];
                        workD[KeyD + j + 1] = workD[KeyD + j];
                        j--;
                    }

                    work[CandOrder + j + 1] = c;
                    workD[KeyD + j + 1] = key;
                }
            }

            // The three nearest triangle-only soft components of c by a single scan (Top3): each gap computed once with the same
            // BoxGap as the sort's, the same tie rule, the same order of the three; written to work[Nearest + 0..min(count, 3)).
            private void SelectNearest(int count, int c)
            {
                var top = new Top3();
                long compares = 0, moves = 0;
                double3 lo = work3[SoftLo + c], hi = work3[SoftHi + c];
                for (int j = 0; j < count; j++)
                {
                    int x = work[Ts + j];
                    double gx = BoxGap(lo, hi, work3[SoftLo + x], work3[SoftHi + x]);
                    top.Offer(x, work[SoftRep + x], gx, ref compares, ref moves);
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
                for (int i = 1; i < count; i++)
                {
                    int x = work[Nearest + i];
                    double gx = BoxGap(work3[SoftLo + c], work3[SoftHi + c], work3[SoftLo + x], work3[SoftHi + x]);
                    Count(CntNearestGaps, 1);
                    int j = i - 1;
                    while (j >= 0)
                    {
                        int y = work[Nearest + j];
                        double gy = BoxGap(work3[SoftLo + c], work3[SoftHi + c], work3[SoftLo + y], work3[SoftHi + y]);
                        Count(CntNearestGaps, 1);
                        bool after = gy > gx || (gy == gx && work[SoftRep + y] > work[SoftRep + x]);
                        Count(CntNearestCompares, 1);
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
                for (int i = 1; i < count; i++)
                {
                    int c = work[ToRefute + i];
                    double g = gaps[c];
                    int j = i - 1;
                    while (j >= 0 && gaps[work[ToRefute + j]] < g)
                    {
                        Count(CntRefuteMoves, 1);
                        work[ToRefute + j + 1] = work[ToRefute + j];
                        j--;
                    }

                    work[ToRefute + j + 1] = c;
                }
            }

            // ---- B: the hulls ---------------------------------------------------------------------------------------------------

            private int RefuteByHulls(int a, int b)
            {
                stats[StatTriedB]++;
                bool settled = Gjk(a, b, out double3 direction, out bool meet);
                Tick(SecGjk);   // GJK's time stops at its return; the projection below is its own section, called only when GJK settled apart
                Count(CntGjkCalls, 1);
                if (settled && !meet)
                {
                    double gap = ProjectionGap(direction, a, b);
                    Tick(SecProjection);
                    if (measure != 0) sections[CntProjectionCalls]++;
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
                for (int i = nodeHullStart[a]; i < nodeHullStart[a + 1]; i++) { double x = math.dot(axis, hull[i]); aLo = math.min(aLo, x); aHi = math.max(aHi, x); }
                for (int i = nodeHullStart[b]; i < nodeHullStart[b + 1]; i++) { double x = math.dot(axis, hull[i]); bLo = math.min(bLo, x); bHi = math.max(bHi, x); }
                stats[StatVertexScans] += (nodeHullStart[a + 1] - nodeHullStart[a]) + (nodeHullStart[b + 1] - nodeHullStart[b]);
                return math.max(bLo - aHi, aLo - bHi);
            }

            private double3 Support(int node, double3 d)
            {
                double best = double.NegativeInfinity;
                double3 at = hull[nodeHullStart[node]];
                for (int i = nodeHullStart[node]; i < nodeHullStart[node + 1]; i++)
                {
                    double x = math.dot(hull[i], d);
                    if (x > best) { best = x; at = hull[i]; }
                }

                stats[StatSupportQueries]++;
                stats[StatVertexScans] += nodeHullStart[node + 1] - nodeHullStart[node];
                return at;
            }

            private double3 Centroid(int node)
            {
                var c = new double3(0.0);
                for (int i = nodeHullStart[node]; i < nodeHullStart[node + 1]; i++) c += hull[i];
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
                for (int it = 0; it < gjkMaxIterations; it++)
                {
                    stats[StatGjkIterations]++;
                    double3 w = Support(a, -v) - Support(b, v);
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

            // ---- union-find in the work array ------------------------------------------------------------------------------------

            private int Find(int at, int i)
            {
                while (work[at + i] != i)
                {
                    work[at + i] = work[at + work[at + i]];
                    i = work[at + i];
                }

                return i;
            }

            private void Union(int at, int a, int b)
            {
                a = Find(at, a);
                b = Find(at, b);
                if (a == b) return;
                work[at + math.max(a, b)] = math.min(a, b);
            }
        }

        // ---- shared pieces -------------------------------------------------------------------------------------------------------------

        // In Burst the call is discarded, so the marker stays 0: it proves the job ran through Burst.
        [BurstDiscard]
        private static void MarkManaged(NativeArray<long> stats) => stats[StatManagedMarker] = 1;

        private static double3 Owner(float3 p, in float4x4 m)
        {
            double x = ((double)m[0][0] * p.x) + ((double)m[1][0] * p.y) + ((double)m[2][0] * p.z) + m[3][0];
            double y = ((double)m[0][1] * p.x) + ((double)m[1][1] * p.y) + ((double)m[2][1] * p.z) + m[3][1];
            double z = ((double)m[0][2] * p.x) + ((double)m[1][2] * p.y) + ((double)m[2][2] * p.z) + m[3][2];
            return new double3(x, y, z);
        }

        // As the managed TermBound: the largest |entry| times the largest |coordinate| (float), or the largest |translation|.
        private static double TermBound(NativeArray<float3> points, int start, int count, in float4x4 m)
        {
            double entry = 0.0, translation = 0.0, coordinate = 0.0;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) entry = math.max(entry, math.abs((double)m[c][r]));
                translation = math.max(translation, math.abs((double)m[3][r]));
            }

            for (int i = start; i < start + count; i++) coordinate = math.max(coordinate, (double)math.cmax(math.abs(points[i])));
            return math.max(entry * coordinate, translation);
        }

        private static double BoxGap(double3 lo1, double3 hi1, double3 lo2, double3 hi2) => math.length(math.max(math.max(lo2 - hi1, lo1 - hi2), 0.0));

        private static int FindRoot(NativeArray<int> a, int at, int i)
        {
            while (a[at + i] != i)
            {
                a[at + i] = a[at + a[at + i]];
                i = a[at + i];
            }

            return i;
        }

        private static void UnionRoots(NativeArray<int> a, int at, int x, int y)
        {
            x = FindRoot(a, at, x);
            y = FindRoot(a, at, y);
            if (x == y) return;
            a[at + math.max(x, y)] = math.min(x, y);
        }

        /// <summary>The fields of a native run as JSON members (no braces), for the batch's line.</summary>
        internal static string Json(NativeRun run)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "\"engine\":" + SpatialSplitCaptureSink.Json(run.burstRequested ? "N1" : "N0") + ",\"burstEnabled\":" + (run.burstEnabled ? "true" : "false") + ",\"burstRan\":" + (run.burstRan ? "true" : "false")
                + ",\"secondsConvert\":" + run.secondsConvert.ToString("R", inv) + ",\"secondsPrepareNative\":" + run.secondsPrepare.ToString("R", inv)
                + ",\"secondsAllocate\":" + run.secondsAllocate.ToString("R", inv) + ",\"secondsSearch\":" + run.secondsSearch.ToString("R", inv)
                + ",\"secondsFinish\":" + run.secondsFinish.ToString("R", inv) + ",\"nativeBytes\":" + run.nativeBytes + ",\"candidateCapacity\":" + run.candidateCapacity
                + ",\"measure\":" + run.measure + ",\"nearest\":" + SpatialSplitCaptureSink.Json(run.nearestSelection ? "select" : "sort") + ",\"flow\":" + SpatialSplitCaptureSink.Json(run.flowDinic ? "dinic" : "ek")
                + ",\"pairs\":" + SpatialSplitCaptureSink.Json(run.pairsSweep ? "sweep" : "all") + ",\"sweepBytes\":" + run.sweepBytes + ",\"keyGapsCapacity\":" + run.keyGapsCapacity
                + (run.prepareHash.Length > 0 ? ",\"prepareHash\":" + SpatialSplitCaptureSink.Json(run.prepareHash) : "") + MeasureJson(run);
        }

        private static string MeasureJson(NativeRun run)
        {
            if (run.measure == 0) return "";
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder();
            if (run.measure == 1)
            {
                for (int i = 0; i < SectionTimes; i++) text.Append(",\"sec_").Append(SectionNames[i]).Append("Ms\":").Append(run.sectionMs[i].ToString("R", inv));
                text.Append(",\"residualMs\":").Append(run.residualMs.ToString("R", inv));
                text.Append(",\"projectionCalls\":").Append(run.counters[CntProjectionCalls - SectionTimes]);
                for (int i = 0; i < PrepSectionTimes; i++) text.Append(",\"prep_").Append(PrepSectionNames[i]).Append("Ms\":").Append(run.prepareSectionMs[i].ToString("R", inv));
                text.Append(",\"prepResidualMs\":").Append(run.prepareResidualMs.ToString("R", inv));
            }
            else
            {
                for (int i = 0; i < SearchSectionCount - SectionTimes; i++) text.Append(",\"cnt_").Append(CounterNames[i]).Append("\":").Append(run.counters[i]);
                for (int i = 0; i < SectionCount - PcNamed; i++) text.Append(",\"pc_").Append(PrepCounterNames[i]).Append("\":").Append(run.prepareCounters[i]);
            }

            text.Append(",\"candidateCapacityInitial\":").Append(run.candidateCapacityInitial).Append(",\"gapsCapacityInitial\":").Append(run.gapsCapacityInitial).Append(",\"gapsCapacity\":").Append(run.gapsCapacity);

            return text.ToString();
        }

        /// <summary>The clock the sections use: ProfilerUnsafeUtility.Timestamp, with its nanosecond conversion ratio (read for the record).</summary>
        internal static string ClockDescription()
        {
            var ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
            return "ProfilerUnsafeUtility.Timestamp (ticks; nanoseconds = ticks * " + ratio.Numerator + " / " + ratio.Denominator + "), read only at the section boundaries of the search job";
        }
    }
}
