using Zantetsu.PhysicsCut;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>Which implementation of the graph candidate's AB decides: M0 the managed one, N0 the native-container
    /// port run as managed code, N1 the same port run through Burst.</summary>
    internal enum CandidateEngine { M0 = 0, N0 = 1, N1 = 2 }

    /// <summary>How a saved sample reaches the native engine: its compact arrays, the span view, or the product's path (span view, convexes by pointer, sides to bytes).</summary>
    internal enum CandidateInput { Sample = 0, Span = 1, Product = 2 }

    /// <summary>
    /// The Editor's thin layer over the Runtime engine <see cref="SpatialSplitGraph"/> (GRAPH-CONNECT-2, unit 1): the batch's
    /// and the tests' <see cref="CandidateSettings"/> / <see cref="CandidateResult"/> mapped onto the engine's settings and
    /// outcome. No numbers live here; the numeric body, its kernels, constants, measurement record and warm-up are the
    /// Runtime's (<c>Runtime/PhysicsCut/SpatialSplitGraph*.cs</c>). The previous Editor engine (graph-candidate-native 8) stays
    /// in commit 7928569e and the preserved baseline native-ab-baseline-6.
    /// </summary>
    internal static class SpatialSplitGraphCandidateNative
    {
        public const string Version = SpatialSplitGraph.Version;

        /// <summary>Decides AB with the native engine; <paramref name="burst"/> calls the Burst entries (N1), else runs the bodies as managed code (N0).</summary>
        internal static CandidateResult Decide(SpatialSplitSample sample, CandidateSettings settings, bool burst, out SpatialSplitGraph.NativeRun run, SpatialSplitGraph.PrepareSnapshot snapshot = null)
        {
            if (settings.stages != CandidateStages.AB)
            {
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                run = new SpatialSplitGraph.NativeRun { burstRequested = burst, burstEnabled = Unity.Burst.BurstCompiler.IsEnabled };
                var refused = new CandidateResult { stage = "A", verdict = CandidateVerdict.InvalidInput, reason = "the native port decides AB only" };
                refused.seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
                return refused;
            }

            var graph = new SpatialSplitGraph.GraphSettings
            {
                secondsLimit = settings.secondsLimit, gjkMaxIterations = settings.gjkMaxIterations, maxCutAttempts = settings.maxCutAttempts,
                measure = settings.measure, nearestSelection = settings.nearestSelection, flowDinic = settings.flowDinic, pairsSweep = settings.pairsSweep, hashPrepare = settings.hashPrepare,
                testFailNativeAllocation = settings.testFailNativeAllocation, testFailWithOutOfMemory = settings.testFailWithOutOfMemory,
                maxTriangles = settings.maxTriangles, maxConvexes = settings.maxConvexes, maxVertexSpan = settings.maxVertexSpan, maxNodes = settings.maxNodes, maxCandidates = settings.maxCandidates,
                maxGraphBytes = settings.maxGraphBytes, workBudget = settings.workBudget, requirePrepared = settings.requirePrepared,
            };
            SpatialSplitGraph.GraphOutcome o = settings.input == CandidateInput.Product ? SpatialSplitGraph.DecideAsProduct(sample, graph, burst, out run, snapshot)
                : settings.input == CandidateInput.Span ? SpatialSplitGraph.DecideAsSpan(sample, graph, burst, out run, snapshot) : SpatialSplitGraph.Decide(sample, graph, burst, out run, snapshot);
            return new CandidateResult
            {
                unfinished = o.unfinished, work = o.work, workPrepare = o.workPrepare, workSearch = o.workSearch, vertexSpan = o.vertexSpan, referencedVertices = o.referencedVertices,
                verdict = Map(o.verdict), reason = o.reason, stage = o.stage, stop = o.stop,
                convexCount = o.convexCount, triangleCount = o.triangleCount, meshComponents = o.meshComponents, definiteComponents = o.definiteComponents,
                convexPositive = o.convexPositive, trianglePositive = o.trianglePositive,
                positiveConvexes = o.positiveConvexes, positiveTriangles = o.positiveTriangles, negativeConvexes = o.negativeConvexes, negativeTriangles = o.negativeTriangles,
                nodePairs = o.nodePairs, pairsApartByBoxes = o.pairsApartByBoxes, candidatesA = o.candidatesA,
                triedB = o.triedB, refutedB = o.refutedB, ambiguousB = o.ambiguousB, gjkIterations = o.gjkIterations, supportQueries = o.supportQueries, vertexScans = o.vertexScans,
                cutAttempts = o.cutAttempts, cutLimitHit = o.cutLimitHit, gjkLimitHit = o.gjkLimitHit, timedOut = o.timedOut, seconds = o.seconds,
            };
        }

        private static CandidateVerdict Map(SpatialSplitGraph.GraphVerdict v)
        {
            switch (v)
            {
                case SpatialSplitGraph.GraphVerdict.Separable: return CandidateVerdict.Separable;
                case SpatialSplitGraph.GraphVerdict.NotSeparable: return CandidateVerdict.NotSeparable;
                case SpatialSplitGraph.GraphVerdict.NotFound: return CandidateVerdict.NotFound;
                case SpatialSplitGraph.GraphVerdict.InvalidInput: return CandidateVerdict.InvalidInput;
                default: return CandidateVerdict.Undecided;
            }
        }

        /// <summary>Runs both bodies once on a tiny input so that Burst has compiled them before anything is timed.</summary>
        internal static void Warmup(int measure = 0, bool nearestSelection = true, bool flowDinic = false, bool pairsSweep = false)
            => SpatialSplitGraph.Warmup(measure, nearestSelection, flowDinic, pairsSweep);   // the bool (both bodies reached the verdict) is the product's Prepare concern

        /// <summary>The fields of a native run as JSON members (no braces), for the batch's line.</summary>
        internal static string Json(SpatialSplitGraph.NativeRun run) => SpatialSplitGraph.Json(run);

        /// <summary>The clock the sections use, for the record.</summary>
        internal static string ClockDescription() => SpatialSplitGraph.ClockDescription();
    }
}
