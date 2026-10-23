using System;
using System.Diagnostics;
using System.Globalization;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling.LowLevel.Unsafe;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The managed driver of the graph candidate's AB (GRAPH-CONNECT-2, unit 1): the input reduced to compact arrays (the
    /// referenced vertices only, in ascending id order), the areas sized, capped and tracked, the two Burst entries called
    /// (N1) or the bodies run as managed code (N0), the result read back and every area released. This file is the Release
    /// part: it depends on nothing of the capture (the sample input, its record and its JSON live in
    /// SpatialSplitGraph.Development.cs under UNITY_EDITOR || DEVELOPMENT_BUILD). Nothing here touches a Unity scene object.
    /// </summary>
    internal static unsafe partial class SpatialSplitGraph
    {
        public const string Version = "graph-candidate-native 10 (2026-10-23, the numeric body in the Runtime PhysicsCut assembly as Burst direct calls over pointer bodies; the same numbers as graph-candidate-native 8 of 7928569e and 9 of d326b460; a work meter, capacity caps and a span input added, all off by default; the product's convexes by pointer, the sides written to the caller's bytes, the span compaction in native areas and metered, the GJK iteration-limit count): AB of graph-candidate 3; N0 = the bodies as managed code, N1 = the Burst direct call (FloatMode.Strict); every area released on any failure, out of memory a failure result; optional section times (measure 1) and diagnostic counts (measure 2); the nearest triangle-only seeds by a top-3 selection or the full sort; the minimum cut by Edmonds-Karp (flow ek) or Dinic (flow dinic); the candidate enumeration by every pair or a sweep along x";

        internal static readonly string[] SectionNames = { "regroup", "seeds", "sortNearest", "cutBuild", "cutFlow", "cutExtract", "collect", "sortToRefute", "gjk", "projection", "conclude" };
        internal static readonly string[] CounterNames = { "projectionCalls", "gjkCalls", "minCutCalls", "arcsBuilt", "bfsDequeues", "augmentations", "nearestCompares", "nearestMoves", "refuteSorted", "refuteMoves", "nearestGaps", "levelBuilds", "arcChecks", "pushes" };
        internal static readonly string[] PrepSectionNames = { "ownerBounds", "slots", "components", "convexes", "meshBoxesCounts", "meshFill", "pairs" };
        internal static readonly string[] PrepCounterNames = { "named", "triangles", "components", "nodes", "hullStored", "slotCompares", "parentRefs", "pairsTotal", "gapEvals", "apart", "candidates", "candGrowths", "candGrowthOld", "gapGrowths", "gapGrowthOld",
            "sweepSkipped", "gapExcluded", "nodeSortCompares", "nodeSortMoves", "keySortCompares", "keySortMoves", "keysSorted" };

        /// <summary>What the graph's AB came to.</summary>
        internal enum GraphVerdict
        {
            /// <summary>A limit (the work budget, a capacity cap, time, allocation) stopped the decision, or nothing was decided yet; nothing is said.</summary>
            Undecided = 0,

            /// <summary>A placement was found: every pair across its sides was certified apart by more than 2m (double, with error bounds).</summary>
            Separable,

            /// <summary>The definite contacts alone put every convex, or every triangle, in one component: no placement exists.</summary>
            NotSeparable,

            /// <summary>This approximation found no placement; nothing is said of whether one exists.</summary>
            NotFound,

            InvalidInput,
        }

        /// <summary>The settings of one decision. Every cap and the budget are 0 = none by default (unit 1: the product's values come with the connection).</summary>
        internal sealed class GraphSettings
        {
            /// <summary>The soft time limit, applied once at the return (0: none).</summary>
            public double secondsLimit = 120.0;
            public int gjkMaxIterations = 64;
            public int maxCutAttempts = 8;

            /// <summary>0 none (no clock read in the bodies), 1 the search's section times, 2 the diagnostic counts (not a timing run).</summary>
            public int measure;

            /// <summary>The three nearest triangle-only soft components by a linear selection (true) or by the full insertion sort (false).</summary>
            public bool nearestSelection = true;

            /// <summary>The minimum cut's maximum flow by Dinic (true) or Edmonds-Karp (false, the baseline and the default).</summary>
            public bool flowDinic;

            /// <summary>The candidate enumeration of stage A by a sweep along x (true) or every pair (false, the baseline and the default).</summary>
            public bool pairsSweep;

            /// <summary>Hash the preparation's candidate list and gaps after the prepare body (a verification run, never a timing run).</summary>
            public bool hashPrepare;

            // ---- the caps and the budget (GRAPH-CONNECT-1 §4.1, §4.3; 0 = none). Exceeding one is Unfinished(capacity / work), never Invalid.
            /// <summary>The most triangles (checked from the count alone, before anything is scanned or allocated).</summary>
            public int maxTriangles;
            /// <summary>The most convexes (checked from the count alone, before anything is scanned or allocated).</summary>
            public int maxConvexes;
            /// <summary>The span input only: the most slots between the least and the greatest referenced vertex id (checked before the marks are allocated).</summary>
            public int maxVertexSpan;
            /// <summary>The most nodes (convexes + mesh components; checked in the prepare body right after the components).</summary>
            public int maxNodes;
            /// <summary>The most candidate pairs the lists may hold (their first capacity and every growth are checked against it).</summary>
            public int maxCandidates;
            /// <summary>The most bytes of native areas one decision may hold (checked before each allocation and, through the candidate cap, before each growth).</summary>
            public long maxGraphBytes;
            /// <summary>The work budget in the meter's units (<see cref="WorkDefinition"/>).</summary>
            public long workBudget;

            /// <summary>The worker path's contract: refuse to run the bodies unless <see cref="Prepare"/> ran on the main thread (Unfinished(notPrepared)).</summary>
            public bool requirePrepared;

            /// <summary>Tests only: the allocation with this ordinal (1-based) fails. -1 in use.</summary>
            public int testFailNativeAllocation = -1;

            /// <summary>Tests only: the failing allocation throws OutOfMemoryException instead of the allocator's refusal.</summary>
            public bool testFailWithOutOfMemory;

            /// <summary>Tests only: this many sentinel bytes after every search area; a sentinel changed by a body is an error (a write past an area). 0 in use.</summary>
            public int testGuardBytes;
        }

        /// <summary>The decision's result: the verdict, the placement (by convex and by triangle), the counters, the stop, the work and the time.</summary>
        internal sealed class GraphOutcome
        {
            public GraphVerdict verdict;
            public string reason = "";
            public string stage = "";
            /// <summary>Why it ended short of a verdict: "" (none), cut-limit, hulls, exhausted (a NotFound's), work, capacity, allocation, time, notPrepared.</summary>
            public string stop = "";
            /// <summary>True when a cap, the budget, the time, an allocation or the preparation contract ended it: Undecided, nothing recorded about the input.</summary>
            public bool unfinished;
            public int convexCount, triangleCount, meshComponents, definiteComponents;
            public bool[] convexPositive, trianglePositive;
            public int positiveConvexes, positiveTriangles, negativeConvexes, negativeTriangles;
            public long nodePairs, pairsApartByBoxes, candidatesA;
            public long triedB, refutedB, ambiguousB, gjkIterations, supportQueries, vertexScans;
            public int cutAttempts;
            public bool cutLimitHit, timedOut;
            /// <summary>Stage B pairs whose GJK reached the iteration limit without settling (GRAPH-CONNECT-4 §3-2): a NotFound with any is not a conclusion.</summary>
            public long gjkLimitHit;
            /// <summary>True when the placement's sides went to the span input's sidesOut (the bool arrays are then null).</summary>
            public bool sidesWritten;
            /// <summary>The work meter's units (<see cref="WorkDefinition"/>): both bodies, the preparation's and the search's.</summary>
            public long work, workPrepare, workSearch;
            /// <summary>The span input: the slots between the least and the greatest referenced id, and the referenced vertices (0 for a compact input).</summary>
            public int vertexSpan, referencedVertices;
            public double seconds;
        }

        /// <summary>The timings and sizes of one native decision, beside the <see cref="GraphOutcome"/>.</summary>
        internal sealed class NativeRun
        {
            public bool burstRequested, burstRan, burstEnabled;
            public double secondsConvert, secondsPrepare, secondsAllocate, secondsSearch, secondsFinish, secondsTotal;
            public long nativeBytes, candidateCapacity;

            /// <summary>0: no clock read in the bodies; 1: the search's section times (ms, exclusive); 2: the diagnostic counts (its time is not a measurement).</summary>
            public int measure;
            public bool nearestSelection, flowDinic, pairsSweep;
            public long sweepBytes, keyGapsCapacity;   // the sweep's added areas (capacities), 0 for the baseline

            /// <summary>On request (a verification run): FNV-1a 64 over the candidate pairs and the gaps' bits, in order; "" otherwise.</summary>
            public string prepareHash = "";
            public double[] sectionMs = new double[SectionTimes];
            public long[] counters = new long[SearchSectionCount - SectionTimes];
            public double residualMs;

            /// <summary>The preparation's exclusive sections (measure 1), its residual against the Stopwatch, and its counts (measure 2).</summary>
            public double[] prepareSectionMs = new double[PrepSectionTimes];
            public double prepareResidualMs;
            public long[] prepareCounters = new long[SectionCount - PcNamed];
            public long candidateCapacityInitial, gapsCapacityInitial, gapsCapacity;

            /// <summary>The work and the limits applied, echoed for the record (0 = none; candidateCap -1 = none).</summary>
            public long work, workPrepare, workSearch, workBudget, maxGraphBytes, candidateCap = -1;
            public int maxTriangles, maxConvexes, maxVertexSpan, maxNodes, maxCandidates, vertexSpan, referencedVertices;
            public bool unfinished;
            public string stop = "";

            /// <summary>How many times the candidate lists were grown (the preparation re-run each time), and how many allocations the decision made.</summary>
            public int listGrowths, allocations;
        }

        /// <summary>Tests: the preparation's outputs, copied after the body ran (never in a measured run).</summary>
        internal sealed class PrepareSnapshot
        {
            public int nodes;
            public double3[] owner, nodeLo, nodeHi, hull;
            public int[] cornerSlot, triComponent, nodeHullStart, nodeTriangleCount;
            public double[] bounds, gaps;
            public int2[] candidates;
        }

        /// <summary>
        /// The product's input shape: a view of render vertices with indices into it (ids below <see cref="vertexViewLength"/>), and
        /// the convexes' vertices in the shape's local frame. Only the vertices the indices name are read; the rest of the view
        /// may hold anything (GRAPH-CONNECT-1 §4.1).
        /// </summary>
        internal sealed class SpanInput
        {
            public VpRenderVertex* vertices;
            public int vertexViewLength;
            public uint* indices;
            public int indexCount;
            /// <summary>The convexes as vertex arrays (the Editor's), or null: then the product's banks and ranges below, <see cref="convexCount"/> of them.</summary>
            public float3[][] convexVertices;
            public ConvexBrepBank* banks;
            public ConvexBrepRange* ranges;
            public int convexCount;
            public float4x4 geometryToOwner, shapeToOwner;
            public float margin;
            /// <summary>
            /// Where a Separable placement's sides are written (the convexes' C bytes, then the triangles' T bytes; 1 positive, 0 negative),
            /// instead of the bool arrays, when not null: the product's buffer. At least C + T bytes long, or the input is invalid.
            /// </summary>
            public byte* sidesOut;
            public int sidesOutLength;
        }

        /// <summary>
        /// The compact input both entries reduce to: the referenced vertices (local positions and ids in ascending id order), the
        /// indices (ids), the convexes' vertices, the frames and the margin. The capture's sample is already in this form; the
        /// span input is compacted on the calling thread (<see cref="FromSpan"/>).
        /// </summary>
        internal sealed class CompactInput
        {
            public float3[] local;
            public uint[] ids;
            public uint[] indices;
            public float3[][] convexes;
            public float4x4 geometryToOwner, shapeToOwner;
            public float margin;
            public int vertexViewLength;
        }

        /// <summary>An allocation that failed, in the engine's own areas (the allocator's refusal, memory, or the test hook): the one failure the decision turns into Unfinished(allocation).</summary>
        internal sealed class GraphAllocationException : Exception
        {
            public GraphAllocationException(string message, Exception inner = null) : base(message, inner) { }
        }

        // ---- the main-thread preparation (GRAPH-CONNECT-1 §1.4) ---------------------------------------------------------------

        /// <summary>
        /// Whether <see cref="Prepare"/> ran: the Burst entries were called once on the main thread, so that their synchronous
        /// compilation (Editor) happened there and a Background worker never compiles. A decision with requirePrepared refuses to
        /// run the bodies while this is false.
        /// </summary>
        internal static bool IsPrepared { get; private set; }

        /// <summary>
        /// The main thread's one-time preparation: both bodies once on a tiny input, through Burst and as managed code. The engine is
        /// prepared only when both runs reached the input's verdict (an allocation that failed, or any other ending short of it,
        /// leaves it unprepared and returns false; a worker then refuses with Unfinished(notPrepared)).
        /// </summary>
        internal static bool Prepare() => PrepareWith(-1);

        /// <summary>Tests only: the preparation with the allocation of the given ordinal failing (-1: none).</summary>
        internal static bool PrepareForTests(int failAllocation) => PrepareWith(failAllocation);

        private static bool PrepareWith(int failAllocation)
        {
            bool ok = Warmup(0, true, true, false, failAllocation);
            if (ok) IsPrepared = true;   // a later failure (another world's) does not revoke a preparation that succeeded (GRAPH-CONNECT-4 §3-10)
            return ok;
        }

        /// <summary>Tests only: forgets the preparation, so that the refusal path can be exercised.</summary>
        internal static void ResetPreparedForTests() => IsPrepared = false;

        /// <summary>
        /// Runs both bodies once on a tiny input (through Burst and as managed code) so that Burst has compiled them before anything is
        /// timed. True when both runs reached the input's verdict (Separable after stage B).
        /// </summary>
        internal static bool Warmup(int measure = 0, bool nearestSelection = true, bool flowDinic = false, bool pairsSweep = false, int failAllocation = -1)
        {
            CompactInput input = WarmupInput();
            var settings = new GraphSettings { secondsLimit = 0.0, measure = measure, nearestSelection = nearestSelection, flowDinic = flowDinic, pairsSweep = pairsSweep, testFailNativeAllocation = failAllocation };
            GraphOutcome burst = DecideCompact(input, settings, true, out NativeRun rb);
            GraphOutcome managed = DecideCompact(input, settings, false, out NativeRun rm);
            return burst.verdict == GraphVerdict.Separable && !burst.unfinished && managed.verdict == GraphVerdict.Separable && !managed.unfinished && rb.burstRan == BurstCompiler.IsEnabled && !rm.burstRan;
        }

        /// <summary>
        /// The warm-up's tiny input: two unit boxes at x 0..1 and 2..3; a triangle in the plane z = x through the first and over the
        /// second (apart by 1/sqrt 2), a triangle inside the second: the boxes overlap, the hulls refute the candidates: a cut, GJK
        /// and projections run; both bodies, the flow and the clock of the measure mode are exercised.
        /// </summary>
        internal static CompactInput WarmupInput()
        {
            return new CompactInput
            {
                local = new[]
                {
                    new float3(-1f, -1f, -1f), new float3(5f, -1f, 5f), new float3(5f, 5f, 5f),
                    new float3(2.2f, 0.2f, 0.5f), new float3(2.8f, 0.2f, 0.5f), new float3(2.5f, 0.8f, 0.5f),
                },
                ids = new uint[] { 0, 1, 2, 3, 4, 5 }, indices = new uint[] { 0, 1, 2, 3, 4, 5 }, vertexViewLength = 6,
                convexes = new[] { WarmupBox(new float3(0f), new float3(1f)), WarmupBox(new float3(2f, 0f, 0f), new float3(3f, 1f, 1f)) },
                geometryToOwner = float4x4.identity, shapeToOwner = float4x4.identity, margin = 1f / 4096f,
            };
        }

        internal static float3[] WarmupBox(float3 lo, float3 hi)
        {
            var v = new float3[8];
            for (int i = 0; i < 8; i++) v[i] = new float3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            return v;
        }

        // ---- the entries -----------------------------------------------------------------------------------------------------------

        /// <summary>Decides AB on a compact input; <paramref name="burst"/> calls the Burst entries (N1), else runs the bodies as managed code (N0).</summary>
        internal static GraphOutcome DecideCompact(CompactInput compact, GraphSettings settings, bool burst, out NativeRun run, PrepareSnapshot snapshot = null)
        {
            return Decide(compact, null, settings, burst, out run, snapshot);
        }

        /// <summary>Decides AB on the product's input shape: the referenced vertices are compacted on this thread first (§4.1).</summary>
        internal static GraphOutcome DecideSpan(SpanInput span, GraphSettings settings, bool burst, out NativeRun run, PrepareSnapshot snapshot = null)
        {
            return Decide(null, span, settings, burst, out run, snapshot);
        }

        private static GraphOutcome Decide(CompactInput compact, SpanInput span, GraphSettings settings, bool burst, out NativeRun run, PrepareSnapshot snapshot)
        {
            long begin = Stopwatch.GetTimestamp();
            run = new NativeRun { burstRequested = burst, burstEnabled = BurstCompiler.IsEnabled };
            var r = new GraphOutcome { stage = "A" };
            try
            {
                if (settings == null) throw new ArgumentException("no settings");
                if (settings.maxTriangles < 0 || settings.maxConvexes < 0 || settings.maxVertexSpan < 0 || settings.maxNodes < 0 || settings.maxCandidates < 0 || settings.maxGraphBytes < 0 || settings.workBudget < 0 || settings.testGuardBytes < 0)
                {
                    throw new ArgumentException("a cap, the work budget or the guard is negative");
                }

                run.maxTriangles = settings.maxTriangles; run.maxConvexes = settings.maxConvexes; run.maxVertexSpan = settings.maxVertexSpan; run.maxNodes = settings.maxNodes;
                run.maxCandidates = settings.maxCandidates; run.maxGraphBytes = settings.maxGraphBytes; run.workBudget = settings.workBudget;
                if (settings.requirePrepared && !IsPrepared)
                {
                    // The worker path's contract (GRAPH-CONNECT-1 §1.4): the bodies do not run before the main thread prepared them.
                    Unfinish(r, "notPrepared", "the main thread has not prepared the engine (SpatialSplitGraph.Prepare); the bodies did not run");
                }
                else
                {
                    DecideInto(compact, span, settings, burst, r, run, begin, snapshot);
                }
            }
            catch (ArgumentException e)
            {
                r.verdict = GraphVerdict.InvalidInput;
                r.reason = e.Message;
            }
            catch (GraphAllocationException e)
            {
                // An allocation of the engine's own areas failed (the allocator's refusal, memory, or the test hook): the areas created were released.
                Unfinish(r, "allocation", "a native allocation failed: " + e.Message);
            }
            catch (OutOfMemoryException e)
            {
                // A managed array of the result or the compaction could not be made: the same ending.
                Unfinish(r, "allocation", "out of memory: " + e.Message);
            }

            // The deadline, at the return only (the bodies carry no clock): a soft limit, as in the managed version's return check.
            if (r.verdict != GraphVerdict.InvalidInput && settings != null && settings.secondsLimit > 0.0 && Since(begin) > settings.secondsLimit)
            {
                Unfinish(r, "time", "the time limit passed before the return (stage " + r.stage + "; the native port checks the time at the return only)");
                r.timedOut = true;
            }

            run.unfinished = r.unfinished;
            run.stop = r.stop;
            run.work = r.work; run.workPrepare = r.workPrepare; run.workSearch = r.workSearch;
            r.seconds = Since(begin);
            run.secondsTotal = r.seconds;
            return r;
        }

        private static void Unfinish(GraphOutcome r, string stop, string reason)
        {
            r.verdict = GraphVerdict.Undecided;
            r.unfinished = true;
            r.stop = stop;
            r.reason = reason;
            r.convexPositive = null;
            r.trianglePositive = null;
        }

        private static double Since(long begin) => (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;

        // ---- the areas: allocation, tracking, release ----------------------------------------------------------------------------

        /// <summary>The native areas created by this class and not yet released (tests: it returns to its value after any decision).</summary>
        internal static long LiveNativeAllocations;

        private struct Areas
        {
            public int allocations, failAt;
            public bool failAsOutOfMemory;
            public long bytes;
        }

        // Every allocation of the engine goes through these: the failure of the allocator, or memory, becomes the engine's own
        // GraphAllocationException here, at the allocation site, so that nothing else is mistaken for one; the test hook fails the
        // allocation with the given ordinal the same way (or as a real OutOfMemoryException when asked).
        private static NativeArray<T> NewArray<T>(int length, NativeArrayOptions options, ref Areas a, long bytesOfOne) where T : struct
        {
            a.allocations++;
            if (a.failAt >= 0 && a.allocations > a.failAt)
            {
                if (a.failAsOutOfMemory) throw new OutOfMemoryException("test: the native allocation " + a.allocations + " ran out of memory");
                throw new GraphAllocationException("test: the native allocation " + a.allocations + " failed");
            }

            NativeArray<T> made;
            try
            {
                made = new NativeArray<T>(length, Allocator.Persistent, options);
            }
            catch (Exception e)
            {
                throw new GraphAllocationException("the allocation " + a.allocations + " of " + length + " elements failed: " + e.GetType().Name + ": " + e.Message, e);
            }

            LiveNativeAllocations++;
            a.bytes += (long)made.Length * bytesOfOne;
            return made;
        }

        private static NativeList<T> NewList<T>(int capacity, ref Areas a) where T : unmanaged
        {
            a.allocations++;
            if (a.failAt >= 0 && a.allocations > a.failAt)
            {
                if (a.failAsOutOfMemory) throw new OutOfMemoryException("test: the native allocation " + a.allocations + " ran out of memory");
                throw new GraphAllocationException("test: the native allocation " + a.allocations + " failed");
            }

            NativeList<T> made;
            try
            {
                made = new NativeList<T>(capacity, Allocator.Persistent);
            }
            catch (Exception e)
            {
                throw new GraphAllocationException("the allocation " + a.allocations + " of a list of " + capacity + " (a candidate list, first or grown) failed: " + e.GetType().Name + ": " + e.Message, e);
            }

            LiveNativeAllocations++;
            // the bytes of a list are counted by the caller from its actual capacity
            return made;
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

        /// <summary>The capacity NativeList gives a list asked for <paramref name="wanted"/> elements of <paramref name="bytesOfOne"/> bytes: at least a cache line, rounded up to a power of two.</summary>
        internal static long ListCapacity(long wanted, int bytesOfOne) => math.ceilpow2(math.max(wanted, 64 / bytesOfOne));

        private static bool Finite(in float4x4 m)
        {
            for (int c = 0; c < 4; c++) if (!math.all(math.isfinite(m[c]))) return false;
            return true;
        }

        private static void DecideInto(CompactInput compact, SpanInput span, GraphSettings settings, bool burst, GraphOutcome r, NativeRun run, long begin, PrepareSnapshot snapshot)
        {
            var areas = new Areas { failAt = settings.testFailNativeAllocation, failAsOutOfMemory = settings.testFailWithOutOfMemory };
            var nc = new NativeCompact();
            try
            {
                // ---- 0. The caps known from the counts alone, before anything is scanned or allocated (Unfinished(capacity), never Invalid).
                int indexCount, convexCount;
                ConvexSource convexes;
                byte* sidesOut = null;
                if (compact != null)
                {
                    if (compact.indices == null || compact.convexes == null) throw new ArgumentException("the compact input is missing its indices or convexes");
                    indexCount = compact.indices.Length; convexCount = compact.convexes.Length;
                    convexes = new ConvexSource(compact.convexes, null, null, convexCount);
                }
                else if (span != null)
                {
                    if (span.indices == null) throw new ArgumentException("the span input is missing its indices");
                    if (span.convexVertices == null && (span.banks == null || span.ranges == null || span.convexCount < 0)) throw new ArgumentException("the span input is missing its convexes");
                    indexCount = span.indexCount; convexCount = span.convexVertices != null ? span.convexVertices.Length : span.convexCount;
                    convexes = new ConvexSource(span.convexVertices, span.banks, span.ranges, convexCount);
                    if (span.sidesOut != null)
                    {
                        if (indexCount < 0 || span.sidesOutLength < convexCount + (indexCount / 3)) throw new ArgumentException("the sides buffer of " + span.sidesOutLength + " bytes is shorter than the convexes and triangles (" + convexCount + " + " + (indexCount / 3) + ")");
                        sidesOut = span.sidesOut;
                    }
                }
                else throw new ArgumentException("no compact and no span input");
                if (indexCount < 0 || indexCount % 3 != 0) throw new ArgumentException("the index count is not a multiple of three");
                int triangleCount = indexCount / 3;
                if (settings.maxTriangles > 0 && triangleCount > settings.maxTriangles) { Unfinish(r, "capacity", "the triangle count " + triangleCount + " exceeds maxTriangles " + settings.maxTriangles); return; }
                if (settings.maxConvexes > 0 && convexCount > settings.maxConvexes) { Unfinish(r, "capacity", "the convex count " + convexCount + " exceeds maxConvexes " + settings.maxConvexes); return; }
                for (int c = 0; c < convexCount; c++)
                {
                    if (convexes.VertexCount(c) < 4) throw new ArgumentException("convex " + c + " has fewer than four vertices");
                }

                // ---- 1. The input checked and reduced to the compact native arrays: the referenced vertices only, in ascending id order.
                // The Editor's compact input is copied (unmetered: not the product's path); the span input is scanned and compacted
                // here, on the deciding thread, with the scans and copies on the work meter (work 3), into areas counted like the engine's.
                if (compact != null) { if (!FromCompact(compact, settings, r, ref areas, ref nc)) return; }
                else if (!FromSpan(span, settings, r, run, ref areas, ref nc)) return;

                DecideCore(in nc, in convexes, settings, burst, r, run, begin, snapshot, ref areas, sidesOut);
            }
            finally
            {
                Free(ref nc.local); Free(ref nc.ids); Free(ref nc.indices);
                run.nativeBytes = areas.bytes;
                run.allocations = areas.allocations;
            }
        }

        // A compact input checked: finite, consistent counts, convexes of four vertices or more.
        private static CompactInput CheckCompact(CompactInput c)
        {
            if (c.local == null || c.ids == null || c.local.Length != c.ids.Length) throw new ArgumentException("the compact input's positions and ids differ in count");
            if (!math.isfinite(c.margin) || c.margin <= 0f) throw new ArgumentException("the margin is " + c.margin.ToString("R", CultureInfo.InvariantCulture));
            if (!Finite(in c.geometryToOwner) || !Finite(in c.shapeToOwner)) throw new ArgumentException("a transform is not finite");
            for (int i = 0; i < c.local.Length; i++)
            {
                if (!math.all(math.isfinite(c.local[i]))) throw new ArgumentException("vertex " + c.ids[i] + " is not finite");
                if (i > 0 && c.ids[i] <= c.ids[i - 1]) throw new ArgumentException("the ids are not strictly ascending at " + i);
            }

            return c;
        }

        /// <summary>The compact arrays the bodies read, in native areas counted like the engine's, and the compaction's work (a span input's).</summary>
        private struct NativeCompact
        {
            public NativeArray<float3> local;
            public NativeArray<uint> ids, indices;
            public int named, indexCount, vertexViewLength;
            public float4x4 geometryToOwner, shapeToOwner;
            public float margin;
            public long work;
        }

        /// <summary>The convexes' vertices, from the Editor's arrays or the product's banks and ranges (read the way the plane search reads them).</summary>
        private readonly struct ConvexSource
        {
            private readonly float3[][] _arrays;
            private readonly ConvexBrepBank* _banks;
            private readonly ConvexBrepRange* _ranges;
            public readonly int count;

            public ConvexSource(float3[][] arrays, ConvexBrepBank* banks, ConvexBrepRange* ranges, int count)
            {
                _arrays = arrays; _banks = banks; _ranges = ranges; this.count = count;
            }

            public int VertexCount(int c) => _arrays != null ? (_arrays[c] != null ? _arrays[c].Length : 0) : (_banks[c].vertices != null ? _ranges[c].vertexCount : 0);

            public float3 Vertex(int c, int v) => _arrays != null ? _arrays[c][v] : _banks[c].vertices[_ranges[c].vertexBase + v];
        }

        // The Editor's compact input into the native arrays: a copy (not the product's path: unmetered), within the byte cap.
        private static bool FromCompact(CompactInput compact, GraphSettings settings, GraphOutcome r, ref Areas areas, ref NativeCompact nc)
        {
            CompactInput c = CheckCompact(compact);
            int named = c.local.Length;
            long bytes = ((long)Math.Max(1, named) * 16) + ((long)Math.Max(1, c.indices.Length) * 4);
            if (settings.maxGraphBytes > 0 && areas.bytes + bytes > settings.maxGraphBytes)
            {
                Unfinish(r, "capacity", "the compact arrays of " + bytes + " B would exceed maxGraphBytes " + settings.maxGraphBytes);
                return false;
            }

            nc.local = NewArray<float3>(Math.Max(1, named), NativeArrayOptions.UninitializedMemory, ref areas, 12);
            nc.ids = NewArray<uint>(Math.Max(1, named), NativeArrayOptions.UninitializedMemory, ref areas, 4);
            nc.indices = NewArray<uint>(Math.Max(1, c.indices.Length), NativeArrayOptions.UninitializedMemory, ref areas, 4);
            for (int i = 0; i < named; i++) { nc.local[i] = c.local[i]; nc.ids[i] = c.ids[i]; }
            for (int i = 0; i < c.indices.Length; i++) nc.indices[i] = c.indices[i];
            nc.named = named;
            nc.indexCount = c.indices.Length;
            nc.vertexViewLength = c.vertexViewLength; nc.geometryToOwner = c.geometryToOwner; nc.shapeToOwner = c.shapeToOwner; nc.margin = c.margin;
            nc.work = 0;
            return true;
        }

        // The product's shape (GRAPH-CONNECT-1 §4.1, in four steps; GRAPH-CONNECT-4 §3-8): (1) the indices scanned for the range check
        // and the span; (2) the span cap (the span is known only now), then the marks allocated; (3) the indices scanned again to
        // mark the referenced slots; (4) the marked slots in ascending order give the compact ids and positions, into native areas
        // counted like the engine's, and the indices are copied. Only referenced vertices are read: an unreferenced slot's contents
        // never reach a number. Every scan and copy is on the work meter (work 3: an index three units over its scans and copy, a
        // slot of the span one), checked against the budget: Unfinished(work). Invalid: a malformed input. Unfinished(capacity): a
        // span or an area over a cap. False: the decision ended here (r says how). The marks are released here whatever happens;
        // the compact arrays are the caller's to release.
        private static bool FromSpan(SpanInput span, GraphSettings settings, GraphOutcome r, NativeRun run, ref Areas areas, ref NativeCompact nc)
        {
            if (span.vertices == null) throw new ArgumentException("the span input is missing its vertices");
            if (!math.isfinite(span.margin) || span.margin <= 0f) throw new ArgumentException("the margin is " + span.margin.ToString("R", CultureInfo.InvariantCulture));
            if (!Finite(in span.geometryToOwner) || !Finite(in span.shapeToOwner)) throw new ArgumentException("a transform is not finite");
            if (span.vertexViewLength <= 0) throw new ArgumentException("the vertex view is empty");
            var meter = WorkMeter.Of(settings.workBudget);

            // (1) the range and the span
            uint lo = uint.MaxValue, hi = 0;
            for (int i = 0; i < span.indexCount && !meter.Stopped; i++)
            {
                uint id = span.indices[i];
                if (id >= (uint)span.vertexViewLength) throw new ArgumentException("index " + i + " names vertex " + id + " outside the view of " + span.vertexViewLength);
                if (id < lo) lo = id;
                if (id > hi) hi = id;
                meter.Spend(1);
            }

            r.workPrepare = r.work = nc.work = meter.work;   // the work so far, kept whatever ends the decision next (a cap, an allocation)
            if (meter.Stopped) return StoppedInCompaction(r, settings, meter, nc.work = meter.work, "the index scan");
            long spanLength = span.indexCount == 0 ? 0 : (long)hi - lo + 1;
            r.vertexSpan = (int)math.min(spanLength, int.MaxValue);
            run.vertexSpan = r.vertexSpan;
            // (2) the span cap, before the marks exist
            if (settings.maxVertexSpan > 0 && spanLength > settings.maxVertexSpan)
            {
                Unfinish(r, "capacity", "the referenced-vertex span " + spanLength + " exceeds maxVertexSpan " + settings.maxVertexSpan);
                return false;
            }

            long markBytes = math.max(1, spanLength);
            if (settings.maxGraphBytes > 0 && areas.bytes + markBytes > settings.maxGraphBytes)
            {
                Unfinish(r, "capacity", "the marks of the span " + spanLength + " exceed maxGraphBytes " + settings.maxGraphBytes);
                return false;
            }

            NativeArray<byte> marks = default;
            try
            {
                marks = NewArray<byte>((int)markBytes, NativeArrayOptions.ClearMemory, ref areas, 1);
                // (3) the referenced slots
                int referenced = 0;
                for (int i = 0; i < span.indexCount && !meter.Stopped; i++)
                {
                    int slot = (int)(span.indices[i] - lo);
                    if (marks[slot] == 0) { marks[slot] = 1; referenced++; }
                    meter.Spend(1);
                }

                r.workPrepare = r.work = nc.work = meter.work;   // the work so far, kept whatever ends the decision next (a cap, an allocation)
                if (meter.Stopped) return StoppedInCompaction(r, settings, meter, nc.work = meter.work, "the marking scan");
                r.referencedVertices = referenced;
                run.referencedVertices = referenced;

                // (4) the compact arrays, within the byte cap, in ascending id order: the engine's slot lookup (a binary search of
                // the sorted ids) is unchanged, and a saved sample's referenced vertices come out in the same order
                long compactBytes = ((long)Math.Max(1, referenced) * 16) + ((long)Math.Max(1, span.indexCount) * 4);
                if (settings.maxGraphBytes > 0 && areas.bytes + compactBytes > settings.maxGraphBytes)
                {
                    Unfinish(r, "capacity", "the compact arrays of " + compactBytes + " B (" + referenced + " vertices, " + span.indexCount + " indices) would exceed maxGraphBytes " + settings.maxGraphBytes);
                    return false;
                }

                nc.local = NewArray<float3>(Math.Max(1, referenced), NativeArrayOptions.UninitializedMemory, ref areas, 12);
                nc.ids = NewArray<uint>(Math.Max(1, referenced), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                nc.indices = NewArray<uint>(Math.Max(1, span.indexCount), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
                int v = 0;
                for (long s = 0; s < spanLength && !meter.Stopped; s++)
                {
                    meter.Spend(1);
                    if (marks[(int)s] == 0) continue;
                    uint id = lo + (uint)s;
                    float3 p = *(float3*)((byte*)(span.vertices + id) + at0);
                    if (!math.all(math.isfinite(p))) throw new ArgumentException("vertex " + id + " is not finite");
                    nc.local[v] = p;
                    nc.ids[v] = id;
                    v++;
                }

                r.workPrepare = r.work = nc.work = meter.work;   // the work so far, kept whatever ends the decision next (a cap, an allocation)
                if (meter.Stopped) return StoppedInCompaction(r, settings, meter, nc.work = meter.work, "the span scan");
                for (int i = 0; i < span.indexCount && !meter.Stopped; i++) { nc.indices[i] = span.indices[i]; meter.Spend(1); }
                r.workPrepare = r.work = nc.work = meter.work;   // the work so far, kept whatever ends the decision next (a cap, an allocation)
                if (meter.Stopped) return StoppedInCompaction(r, settings, meter, nc.work = meter.work, "the index copy");
                nc.named = referenced;
                nc.indexCount = span.indexCount;
                nc.vertexViewLength = span.vertexViewLength; nc.geometryToOwner = span.geometryToOwner; nc.shapeToOwner = span.shapeToOwner; nc.margin = span.margin;
                nc.work = meter.work;
                return true;
            }
            finally
            {
                // the marks go here whatever happened; their bytes stay counted (they were held during the compaction)
                Free(ref marks);
            }
        }

        private static bool StoppedInCompaction(GraphOutcome r, GraphSettings settings, in WorkMeter meter, long work, string where)
        {
            r.workPrepare = work;
            r.work = work;
            Unfinish(r, "work", "the work budget " + settings.workBudget + " passed in the span compaction at " + where + " (" + work + " units)");
            return false;
        }

        private const byte Guard = 0x7E;

        private static void FillGuard<T>(NativeArray<T> a, int used, int bytesOfOne, int guardBytes) where T : struct
        {
            if (guardBytes <= 0) return;
            byte* p = (byte*)a.GetUnsafePtr() + ((long)used * bytesOfOne);
            for (int i = 0; i < guardBytes; i++) p[i] = Guard;
        }

        private static void CheckGuard<T>(NativeArray<T> a, int used, int bytesOfOne, int guardBytes, string name) where T : struct
        {
            if (guardBytes <= 0 || !a.IsCreated) return;
            byte* p = (byte*)a.GetUnsafePtr() + ((long)used * bytesOfOne);
            for (int i = 0; i < guardBytes; i++)
            {
                if (p[i] != Guard) throw new InvalidOperationException("a body wrote past the area " + name + " (guard byte " + i + ")");
            }
        }

        private static void DecideCore(in NativeCompact nc, in ConvexSource convexes, GraphSettings settings, bool burst, GraphOutcome r, NativeRun run, long begin, PrepareSnapshot snapshot, ref Areas areas, byte* sidesOut)
        {
            int named = nc.named;
            int triangleCount = nc.indexCount / 3;
            int convexCount = convexes.count;
            int convexVertexTotal = 0;
            for (int c = 0; c < convexCount; c++) convexVertexTotal += convexes.VertexCount(c);
            r.convexCount = convexCount;
            r.triangleCount = triangleCount;
            r.workPrepare = nc.work;   // the compaction's work, returned whatever ends the decision before the bodies run (a cap, an allocation)
            r.work = nc.work;
            NativeArray<float3> local = nc.local, convexLocal = default;
            NativeArray<uint> ids = nc.ids, indices = nc.indices;
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
            long maxBytes = settings.maxGraphBytes;
            int guard = settings.testGuardBytes;
            int mLen = 0, workLen = 0, work3Len = 0, workDLen = 0, workBLen = 0;
            try
            {
                // The fixed areas, their sizes known from the input, and the sweep's node order: the byte cap is checked over their sum
                // before the first one.
                int orderLength = settings.pairsSweep ? Math.Max(1, nodeCapacity) : 1;
                long fixedBytes = ((long)Math.Max(1, named) * 24) + ((long)Math.Max(1, nc.indexCount) * 4) + ((long)Math.Max(1, convexVertexTotal) * 12) + ((long)(convexCount + 1) * 4)
                    + ((long)Math.Max(1, triangleCount) * 4) + ((long)Math.Max(1, nodeCapacity) * (24 + 24 + 4)) + ((long)Math.Max(1, nodeCapacity + 1) * 4) + ((long)Math.Max(1, hullCapacity) * 24)
                    + ((long)Math.Max(1, Math.Max(named, triangleCount) * 2 + nodeCapacity) * 4) + (4 * 8) + (StatCount * 8) + (SectionCount * 8) + ((long)orderLength * 16);
                if (maxBytes > 0 && areas.bytes + fixedBytes > maxBytes)
                {
                    Unfinish(r, "capacity", "the fixed areas of " + fixedBytes + " B would exceed maxGraphBytes " + maxBytes);
                    return;
                }

                convexLocal = NewArray<float3>(Math.Max(1, convexVertexTotal), NativeArrayOptions.UninitializedMemory, ref areas, 12);
                convexStart = NewArray<int>(convexCount + 1, NativeArrayOptions.ClearMemory, ref areas, 4);
                owner = NewArray<double3>(Math.Max(1, named), NativeArrayOptions.UninitializedMemory, ref areas, 24);
                cornerSlot = NewArray<int>(Math.Max(1, nc.indexCount), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                triComponent = NewArray<int>(Math.Max(1, triangleCount), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                nodeLo = NewArray<double3>(Math.Max(1, nodeCapacity), NativeArrayOptions.UninitializedMemory, ref areas, 24);
                nodeHi = NewArray<double3>(Math.Max(1, nodeCapacity), NativeArrayOptions.UninitializedMemory, ref areas, 24);
                nodeHullStart = NewArray<int>(Math.Max(1, nodeCapacity + 1), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                nodeTriangleCount = NewArray<int>(Math.Max(1, nodeCapacity), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                hull = NewArray<double3>(Math.Max(1, hullCapacity), NativeArrayOptions.UninitializedMemory, ref areas, 24);
                scratchInt = NewArray<int>(Math.Max(1, Math.Max(named, triangleCount) * 2 + nodeCapacity), NativeArrayOptions.UninitializedMemory, ref areas, 4);
                bounds = NewArray<double>(4, NativeArrayOptions.ClearMemory, ref areas, 8);
                stats = NewArray<long>(StatCount, NativeArrayOptions.ClearMemory, ref areas, 8);
                sections = NewArray<long>(SectionCount, NativeArrayOptions.ClearMemory, ref areas, 8);
                order = NewArray<NodeKey>(orderLength, NativeArrayOptions.UninitializedMemory, ref areas, 16);
                run.measure = settings.measure;

                // The candidate lists (GRAPH-CONNECT-1 §4.1). Their real first capacity is NativeList's rounding of what is asked (a power of
                // two, at least a cache line); the cap on the pairs is the lesser of maxCandidates and what the byte cap leaves for a
                // growth (which holds the old area and the new one at once: 16 + 8 B a pair; the sweep's key list the same again). A
                // first capacity over the cap is lowered to the largest power of two within it; none within it is Unfinished(capacity);
                // a settings cap of 0 means none, a cap computed as 0 means no room. The body checks every growth against the cap.
                int wanted = Math.Max(16, Math.Min(nodeCapacity * 4, 1 << 16));
                long perPair = settings.pairsSweep ? 48 : 24;
                bool capped = settings.maxCandidates > 0 || maxBytes > 0;
                long cap = long.MaxValue;
                if (settings.maxCandidates > 0) cap = settings.maxCandidates;
                if (maxBytes > 0) cap = Math.Min(cap, Math.Max(0, (maxBytes - areas.bytes) / perPair));
                long actual = ListCapacity(wanted, 8);
                if (capped && actual > cap)
                {
                    long within = cap >= 8 ? 1L << (63 - math.lzcnt((ulong)cap)) : 0;   // the largest power of two not above the cap
                    if (within < 8)
                    {
                        Unfinish(r, "capacity", "the candidate lists cannot be created within the caps (" + cap + " pairs; maxCandidates " + settings.maxCandidates + ", maxGraphBytes " + maxBytes + ")");
                        return;
                    }

                    actual = within;
                }

                long listBytes = actual * (settings.pairsSweep ? 32 : 16) + (settings.pairsSweep ? 0 : 64);   // candidates 8 + gaps 8 (+ the key list 16) a pair; the key placeholder 64 B
                if (maxBytes > 0 && areas.bytes + listBytes > maxBytes)
                {
                    Unfinish(r, "capacity", "the candidate lists of " + listBytes + " B would exceed maxGraphBytes " + maxBytes + " (" + areas.bytes + " B held)");
                    return;
                }

                run.candidateCap = capped ? (int)Math.Min(cap, int.MaxValue) : -1;
                candidates = NewList<int2>((int)actual, ref areas);
                gaps = NewList<double>((int)actual, ref areas);
                keyGaps = NewList<KeyGap>(settings.pairsSweep ? (int)actual : 1, ref areas);
                long listBytesHeld = ((long)candidates.Capacity * 8) + ((long)gaps.Capacity * 8) + ((long)keyGaps.Capacity * 16);
                areas.bytes += listBytesHeld;

                int fill = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    convexStart[c] = fill;
                    int vertexCount = convexes.VertexCount(c);
                    for (int k = 0; k < vertexCount; k++)
                    {
                        float3 p = convexes.Vertex(c, k);
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
                var prepare = new PrepareBody
                {
                    local = (float3*)local.GetUnsafePtr(), ids = (uint*)ids.GetUnsafePtr(), indices = (uint*)indices.GetUnsafePtr(), convexLocal = (float3*)convexLocal.GetUnsafePtr(), convexStart = (int*)convexStart.GetUnsafePtr(),
                    geometryToOwner = nc.geometryToOwner, shapeToOwner = nc.shapeToOwner,
                    margin = nc.margin, vertexViewLength = nc.vertexViewLength, named = named, triangleCount = triangleCount, convexCount = convexCount,
                    owner = (double3*)owner.GetUnsafePtr(), cornerSlot = (int*)cornerSlot.GetUnsafePtr(), triComponent = (int*)triComponent.GetUnsafePtr(),
                    nodeLo = (double3*)nodeLo.GetUnsafePtr(), nodeHi = (double3*)nodeHi.GetUnsafePtr(), nodeHullStart = (int*)nodeHullStart.GetUnsafePtr(), nodeTriangleCount = (int*)nodeTriangleCount.GetUnsafePtr(),
                    hull = (double3*)hull.GetUnsafePtr(), scratch = (int*)scratchInt.GetUnsafePtr(), bounds = (double*)bounds.GetUnsafePtr(), stats = (long*)stats.GetUnsafePtr(),
                    candidates = candidates.GetUnsafeList(), gaps = gaps.GetUnsafeList(), measure = settings.measure, sections = (long*)sections.GetUnsafePtr(),
                    pairs = settings.pairsSweep ? 1 : 0, order = (NodeKey*)order.GetUnsafePtr(), keyGaps = keyGaps.GetUnsafeList(),
                    workBudget = settings.workBudget, workBefore = nc.work, maxNodes = settings.maxNodes, candidateCapEnabled = capped ? 1 : 0, candidateCap = capped ? (int)Math.Min(cap, int.MaxValue) : 0,
                };
                run.pairsSweep = settings.pairsSweep;
                // The body never reallocates a list: a full list ends it with StopGrow and the next capacity. The driver -- the one
                // place that allocates, through NewList and its failure boundary -- frees the lists, allocates them at that capacity
                // (within the caps) and runs the deterministic body again; the meter continues across the runs. What is cumulative
                // over the runs: the work (the meter) and the time (secondsPrepare, the measure-1 sections). What describes the final
                // run only: the measure-2 counts (cleared before a re-run) and every result count in stats (the body writes them anew).
                long releasedCapacity = 0;
                while (true)
                {
                    if (burst) RunPrepare(ref prepare); else prepare.Execute();
                    // the work spent and the time so far, recorded before any growth allocation: an allocation that fails returns them
                    r.workPrepare = stats[StatWorkPrepare];
                    r.work = r.workPrepare;
                    run.secondsPrepare = Since(t1);
                    if (stats[StatUnfinished] == 0 || stats[StatStop] != StopGrow) break;
                    long needed = stats[StatNeededCapacity];
                    long room = maxBytes > 0 ? Math.Max(0, maxBytes - (areas.bytes - listBytesHeld)) / perPair : long.MaxValue;
                    if ((settings.maxCandidates > 0 && needed > settings.maxCandidates) || needed > room)
                    {
                        // the growth would exceed a cap: the body's stop becomes Unfinished(capacity) below
                        stats[StatStop] = StopCapacity;
                        break;
                    }

                    areas.bytes -= listBytesHeld;
                    releasedCapacity += candidates.Capacity;
                    Free(ref candidates); Free(ref gaps); Free(ref keyGaps);
                    run.listGrowths++;
                    if (settings.measure == 2) for (int i = PcNamed; i < SectionCount; i++) sections[i] = 0;   // the counts restart with the run
                    candidates = NewList<int2>((int)needed, ref areas);
                    gaps = NewList<double>((int)needed, ref areas);
                    keyGaps = NewList<KeyGap>(settings.pairsSweep ? (int)needed : 1, ref areas);
                    listBytesHeld = ((long)candidates.Capacity * 8) + ((long)gaps.Capacity * 8) + ((long)keyGaps.Capacity * 16);
                    areas.bytes += listBytesHeld;
                    prepare.candidates = candidates.GetUnsafeList(); prepare.gaps = gaps.GetUnsafeList(); prepare.keyGaps = keyGaps.GetUnsafeList();
                    prepare.workBefore = stats[StatWorkPrepare];
                }

                run.secondsPrepare = Since(t1);
                if (settings.measure == 2)
                {
                    // the growths are the driver's: how many, and the capacities released (the same for the candidates and the gaps)
                    sections[PcCandGrowths] = run.listGrowths; sections[PcGapGrowths] = run.listGrowths;
                    sections[PcCandGrowthOld] = releasedCapacity; sections[PcGapGrowthOld] = releasedCapacity;
                }

                run.gapsCapacity = gaps.Capacity;
                run.candidateCapacity = candidates.Capacity;
                if (settings.pairsSweep) { run.sweepBytes = ((long)order.Length * 16) + ((long)keyGaps.Capacity * 16); run.keyGapsCapacity = keyGaps.Capacity; }
                r.workPrepare = stats[StatWorkPrepare];
                r.work = r.workPrepare;
                run.burstRan = burst && stats[StatManagedMarker] == 0;
                if (stats[StatError] != 0)
                {
                    throw new ArgumentException(stats[StatError] == 1 ? "an index names a vertex which the sample does not hold" : "the preparation failed (" + stats[StatError] + ")");
                }

                r.meshComponents = (int)stats[StatMeshComponents];
                r.nodePairs = stats[StatNodePairs];
                if (stats[StatUnfinished] != 0)
                {
                    // The body stopped: the budget, the node cap or a refused growth of the candidate lists.
                    int n0 = (int)stats[StatNodes];
                    string why = stats[StatStop] == StopWork ? "the work budget " + settings.workBudget + " passed in the preparation (" + stats[StatWorkPrepare] + " units)"
                        : settings.maxNodes > 0 && n0 > settings.maxNodes ? "the node count " + n0 + " exceeds maxNodes " + settings.maxNodes
                        : "a growth of the candidate lists to " + stats[StatNeededCapacity] + " pairs was refused (cap " + run.candidateCap + "; maxCandidates " + settings.maxCandidates + ", maxGraphBytes " + settings.maxGraphBytes + ")";
                    Unfinish(r, stats[StatStop] == StopWork ? "work" : "capacity", why);
                    return;
                }

                if (settings.hashPrepare)
                {
                    // Outside every section (after the prepare body's time was taken): the whole candidate list and the gaps, hashed
                    // for the full-set agreement check. Never used in a timing run.
                    ulong h = 14695981039346656037UL;
                    void Mix(ulong v) { for (int b = 0; b < 8; b++) { h ^= (v >> (8 * b)) & 0xFF; h *= 1099511628211UL; } }
                    Mix((ulong)candidates.Length);
                    for (int k = 0; k < candidates.Length; k++) { Mix((ulong)(uint)candidates[k].x); Mix((ulong)(uint)candidates[k].y); Mix((ulong)BitConverter.DoubleToInt64Bits(gaps[k])); }
                    run.prepareHash = h.ToString("x16");
                }

                if (snapshot != null)
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

                int n = (int)stats[StatNodes];
                int candidateCount = candidates.Length;
                r.pairsApartByBoxes = stats[StatApartByBoxes];
                r.candidatesA = candidateCount;

                // ---- 3. The work areas of the search, sized by what the preparation found (the byte cap checked over their sum first), then the search.
                long t2 = Stopwatch.GetTimestamp();
                int m = Math.Max(1, n);
                int cand = Math.Max(1, candidateCount);
                int arcs = (2 * cand) + (4 * m);
                mLen = m; workLen = (19 * m) + (8 * cand) + (3 * arcs) + 76; work3Len = 4 * m; workDLen = 2 * cand; workBLen = (6 * m) + (3 * cand) + 8;
                long searchBytes = mLen + ((long)workLen * 4) + ((long)work3Len * 24) + ((long)workDLen * 8) + workBLen + (5L * guard);
                if (maxBytes > 0 && areas.bytes + searchBytes > maxBytes)
                {
                    Unfinish(r, "capacity", "the search areas of " + searchBytes + " B would exceed maxGraphBytes " + maxBytes + " (" + areas.bytes + " B held)");
                    return;
                }

                // (the guard bytes, tests only, sit after each area's used length and are checked after the body)
                nodeSide = NewArray<byte>(mLen + guard, NativeArrayOptions.ClearMemory, ref areas, 1); FillGuard(nodeSide, mLen, 1, guard);
                work = NewArray<int>(workLen + ((guard + 3) / 4), NativeArrayOptions.UninitializedMemory, ref areas, 4); FillGuard(work, workLen, 4, guard);
                work3 = NewArray<double3>(work3Len + ((guard + 23) / 24), NativeArrayOptions.UninitializedMemory, ref areas, 24); FillGuard(work3, work3Len, 24, guard);
                workD = NewArray<double>(workDLen + ((guard + 7) / 8), NativeArrayOptions.UninitializedMemory, ref areas, 8); FillGuard(workD, workDLen, 8, guard);
                workB = NewArray<byte>(workBLen + guard, NativeArrayOptions.UninitializedMemory, ref areas, 1); FillGuard(workB, workBLen, 1, guard);
                run.secondsAllocate = Since(t2);
                long t3 = Stopwatch.GetTimestamp();
                // One meter over both bodies: the search continues the preparation's count and its check points (the whole budget applies).
                var search = new SearchBody
                {
                    n = n, convexCount = convexCount, candidateCount = candidateCount, maxCutAttempts = settings.maxCutAttempts, gjkMaxIterations = settings.gjkMaxIterations,
                    nodeLo = (double3*)nodeLo.GetUnsafePtr(), nodeHi = (double3*)nodeHi.GetUnsafePtr(), nodeHullStart = (int*)nodeHullStart.GetUnsafePtr(), nodeTriangleCount = (int*)nodeTriangleCount.GetUnsafePtr(),
                    hull = (double3*)hull.GetUnsafePtr(), candidates = candidates.GetUnsafeList()->Ptr, gaps = gaps.GetUnsafeList()->Ptr,
                    bounds = (double*)bounds.GetUnsafePtr(), stats = (long*)stats.GetUnsafePtr(), nodeSide = (byte*)nodeSide.GetUnsafePtr(),
                    work = (int*)work.GetUnsafePtr(), work3 = (double3*)work3.GetUnsafePtr(), workD = (double*)workD.GetUnsafePtr(), workB = (byte*)workB.GetUnsafePtr(),
                    measure = settings.measure, sections = (long*)sections.GetUnsafePtr(), nearest = settings.nearestSelection ? 1 : 0, flow = settings.flowDinic ? 1 : 0,
                    workBudget = settings.workBudget, workBefore = stats[StatWorkPrepare],
                };
                run.nearestSelection = settings.nearestSelection;
                run.flowDinic = settings.flowDinic;
                if (burst) RunSearch(ref search); else search.Execute();
                run.secondsSearch = Since(t3);
                CheckGuard(nodeSide, mLen, 1, guard, "nodeSide"); CheckGuard(work, workLen, 4, guard, "work"); CheckGuard(work3, work3Len, 24, guard, "work3"); CheckGuard(workD, workDLen, 8, guard, "workD"); CheckGuard(workB, workBLen, 1, guard, "workB");
                r.workSearch = stats[StatWorkSearch];
                r.work = r.workPrepare + r.workSearch;
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
                r.gjkLimitHit = stats[StatGjkLimitHit];
                r.stage = stats[StatStage] == 0 ? "A" : "B";
                if (stats[StatUnfinished] != 0)
                {
                    Unfinish(r, "work", "the work budget " + settings.workBudget + " passed in the search at stage " + r.stage + " (" + r.work + " units in all)");
                    run.secondsFinish = Since(t4);
                    return;
                }

                switch ((int)stats[StatVerdict])
                {
                    case VerdictSeparable:
                        r.verdict = GraphVerdict.Separable;
                        if (sidesOut != null)
                        {
                            // The product's buffer: the convexes' sides, then the triangles' (a triangle's side is its component's node's).
                            for (int c = 0; c < convexCount; c++)
                            {
                                bool positive = nodeSide[c] != 0;
                                sidesOut[c] = positive ? (byte)1 : (byte)0;
                                if (positive) r.positiveConvexes++; else r.negativeConvexes++;
                            }

                            for (int k = 0; k < triangleCount; k++)
                            {
                                bool positive = nodeSide[convexCount + triComponent[k]] != 0;
                                sidesOut[convexCount + k] = positive ? (byte)1 : (byte)0;
                                if (positive) r.positiveTriangles++; else r.negativeTriangles++;
                            }

                            r.sidesWritten = true;
                            break;
                        }

                        r.convexPositive = new bool[convexCount];
                        r.trianglePositive = new bool[triangleCount];
                        for (int c = 0; c < convexCount; c++)
                        {
                            r.convexPositive[c] = nodeSide[c] != 0;
                            if (r.convexPositive[c]) r.positiveConvexes++; else r.negativeConvexes++;
                        }

                        // The nodes are the convexes then the mesh components: a triangle's side is its component's node's.
                        for (int k = 0; k < triangleCount; k++)
                        {
                            r.trianglePositive[k] = nodeSide[convexCount + triComponent[k]] != 0;
                            if (r.trianglePositive[k]) r.positiveTriangles++; else r.negativeTriangles++;
                        }

                        break;
                    case VerdictNotSeparable:
                        r.verdict = GraphVerdict.NotSeparable;
                        r.reason = stats[StatStop] == ReasonAllConvexesOne ? "every convex is in one component of definite contacts"
                            : stats[StatStop] == ReasonAllTrianglesOne ? "every triangle is in one component of definite contacts" : "fewer than two convexes or two triangles";
                        break;
                    default:
                        r.verdict = GraphVerdict.NotFound;
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
                // Whatever happened -- an input refused after the first allocation, an allocation failed, a body threw -- every
                // native area that was created is released here; the live count proves it in the tests.
                Free(ref convexLocal); Free(ref convexStart);   // (local, ids and indices are the caller's: freed in DecideInto)
                Free(ref owner); Free(ref cornerSlot); Free(ref triComponent); Free(ref nodeLo); Free(ref nodeHi); Free(ref nodeHullStart); Free(ref nodeTriangleCount);
                Free(ref hull); Free(ref scratchInt); Free(ref bounds); Free(ref stats); Free(ref sections); Free(ref candidates); Free(ref gaps);
                Free(ref nodeSide); Free(ref work); Free(ref work3); Free(ref workD); Free(ref workB);
                Free(ref order); Free(ref keyGaps);
            }
        }

        /// <summary>The clock the sections use: ProfilerUnsafeUtility.Timestamp, with its nanosecond conversion ratio (read for the record).</summary>
        internal static string ClockDescription()
        {
            var ratio = ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
            return "ProfilerUnsafeUtility.Timestamp (ticks; nanoseconds = ticks * " + ratio.Numerator + " / " + ratio.Denominator + "), read only at the section boundaries of the search body";
        }
    }
}
