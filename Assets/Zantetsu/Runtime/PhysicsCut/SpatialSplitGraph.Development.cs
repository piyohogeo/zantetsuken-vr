#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Globalization;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The development side of the graph engine (GRAPH-CONNECT-2, unit 1): the capture's sample as an input, the sample through
    /// the span adapter (the product's shape rebuilt from it), and the run's record as JSON for the batch. Only here does the
    /// engine touch <see cref="SpatialSplitSample"/> and <see cref="SpatialSplitCaptureSink"/>, which exist in the Editor and
    /// Development Players only; the Release engine (SpatialSplitGraph.cs) knows nothing of them.
    /// </summary>
    internal static unsafe partial class SpatialSplitGraph
    {
        /// <summary>Decides AB on a capture sample; <paramref name="burst"/> calls the Burst entries (N1), else runs the bodies as managed code (N0).</summary>
        internal static GraphOutcome Decide(SpatialSplitSample sample, GraphSettings settings, bool burst, out NativeRun run, PrepareSnapshot snapshot = null)
        {
            CompactInput compact;
            try
            {
                compact = FromSample(sample);
            }
            catch (ArgumentException e)
            {
                run = new NativeRun { burstRequested = burst, burstEnabled = Unity.Burst.BurstCompiler.IsEnabled };
                return new GraphOutcome { stage = "A", verdict = GraphVerdict.InvalidInput, reason = e.Message };
            }

            return DecideCompact(compact, settings, burst, out run, snapshot);
        }

        // The capture's sample: the same checks as the managed version's BuildScene; the named vertices are already compact.
        private static CompactInput FromSample(SpatialSplitSample sample)
        {
            if (sample == null) throw new ArgumentException("no sample");
            if (sample.indices == null || sample.vertexIds == null || sample.vertexBytes == null || sample.convexes == null) throw new ArgumentException("the sample is missing its arrays");
            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != vertexBytes) throw new ArgumentException("the vertex is " + sample.vertexStructBytes + " bytes, this build's " + vertexBytes);
            int named = sample.vertexIds.Length;
            if (sample.vertexBytes.Length != named * vertexBytes) throw new ArgumentException("the vertex bytes are not " + named + " vertices");
            int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            var local = new float3[named];
            for (int i = 0; i < named; i++)
            {
                int at = (i * vertexBytes) + at0;
                local[i] = new float3(BitConverter.ToSingle(sample.vertexBytes, at), BitConverter.ToSingle(sample.vertexBytes, at + 4), BitConverter.ToSingle(sample.vertexBytes, at + 8));
            }

            var convexes = new float3[sample.convexes.Length][];
            for (int c = 0; c < convexes.Length; c++)
            {
                SpatialSplitSample.Convex convex = sample.convexes[c];
                if (convex == null || convex.vertices == null || convex.faceOffsets == null || convex.faceIndices == null) throw new ArgumentException("convex " + c + " is missing its arrays");
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

                convexes[c] = convex.vertices;
            }

            return new CompactInput
            {
                local = local, ids = sample.vertexIds, indices = sample.indices, convexes = convexes,
                geometryToOwner = sample.geometryToOwner, shapeToOwner = sample.shapeToOwner, margin = sample.margin, vertexViewLength = sample.vertexViewLength,
            };
        }

        /// <summary>The warm-up's tiny input as a capture sample (the tests' smallest Separable-after-B fixture).</summary>
        internal static SpatialSplitSample WarmupSample()
        {
            CompactInput w = WarmupInput();
            var sample = new SpatialSplitSample
            {
                formatVersion = SpatialSplitCaptureSink.FormatVersion, vertexStructBytes = UnsafeUtility.SizeOf<VpRenderVertex>(), margin = w.margin, vertexViewLength = w.vertexViewLength,
                geometryToOwner = w.geometryToOwner, shapeToOwner = w.shapeToOwner, indices = w.indices, vertexIds = w.ids,
                convexes = new[] { WarmupSampleBox(w.convexes[0]), WarmupSampleBox(w.convexes[1]) },
            };
            int size = sample.vertexStructBytes;
            int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            sample.vertexBytes = new byte[w.local.Length * size];
            for (int i = 0; i < w.local.Length; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(w.local[i].x), 0, sample.vertexBytes, (i * size) + at0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(w.local[i].y), 0, sample.vertexBytes, (i * size) + at0 + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(w.local[i].z), 0, sample.vertexBytes, (i * size) + at0 + 8, 4);
            }

            return sample;
        }

        private static SpatialSplitSample.Convex WarmupSampleBox(float3[] v)
        {
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

        // The product's input shape rebuilt from a sample (GRAPH-CONNECT-2, unit 1): a view as long as the greatest id + 1 with the
        // sample's vertices at their ids and the fill everywhere else, the indices as they are, the convexes' vertices. The view
        // is reused between samples (grown when needed) so that the full set does not allocate one a sample.
        private static VpRenderVertex[] s_view;

        /// <summary>Tests and the batch: the sample through the span adapter; the unreferenced slots hold NaN positions unless a fill is given.</summary>
        internal static GraphOutcome DecideAsSpan(SpatialSplitSample sample, GraphSettings graph, bool burst, out NativeRun run, PrepareSnapshot snapshot = null, float3? unreferencedFill = null)
        {
            int size = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != size) throw new ArgumentException("the vertex is " + sample.vertexStructBytes + " bytes, this build's " + size);
            uint greatest = 0;
            for (int i = 0; i < sample.vertexIds.Length; i++) greatest = Math.Max(greatest, sample.vertexIds[i]);
            for (int i = 0; i < sample.indices.Length; i++) greatest = Math.Max(greatest, sample.indices[i]);
            long length = Math.Max(1L, (long)greatest + 1);
            if (length > int.MaxValue / size) throw new ArgumentException("the view would be too long");
            if (s_view == null || s_view.Length < length) s_view = new VpRenderVertex[Math.Max(length, s_view == null ? 0L : 2L * s_view.Length)];
            float3 fill = unreferencedFill ?? new float3(float.NaN);
            int at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            fixed (VpRenderVertex* view = s_view)
            fixed (byte* bytes = sample.vertexBytes)
            fixed (uint* indices = sample.indices)
            {
                var raw = (byte*)view;
                for (long k = 0; k < length; k++) *(float3*)(raw + (k * size) + at0) = fill;
                for (int i = 0; i < sample.vertexIds.Length; i++) UnsafeUtility.MemCpy(raw + ((long)sample.vertexIds[i] * size), bytes + ((long)i * size), size);
                var convexes = new float3[sample.convexes.Length][];
                for (int c = 0; c < convexes.Length; c++) convexes[c] = sample.convexes[c].vertices;
                var span = new SpanInput
                {
                    vertices = view, vertexViewLength = (int)length, indices = indices, indexCount = sample.indices.Length, convexVertices = convexes,
                    geometryToOwner = sample.geometryToOwner, shapeToOwner = sample.shapeToOwner, margin = sample.margin,
                };
                return DecideSpan(span, graph, burst, out run, snapshot);
            }
        }

        // ---- the record ----------------------------------------------------------------------------------------------------------------

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
                + (run.prepareHash.Length > 0 ? ",\"prepareHash\":" + SpatialSplitCaptureSink.Json(run.prepareHash) : "")
                + ",\"work\":" + run.work + ",\"workPrepare\":" + run.workPrepare + ",\"workSearch\":" + run.workSearch + ",\"workDefinition\":" + SpatialSplitCaptureSink.Json(WorkDefinition)
                + ",\"workBudget\":" + run.workBudget + ",\"maxTriangles\":" + run.maxTriangles + ",\"maxConvexes\":" + run.maxConvexes + ",\"maxVertexSpan\":" + run.maxVertexSpan
                + ",\"maxNodes\":" + run.maxNodes + ",\"maxCandidates\":" + run.maxCandidates + ",\"maxGraphBytes\":" + run.maxGraphBytes + ",\"candidateCap\":" + run.candidateCap
                + ",\"unfinished\":" + (run.unfinished ? "true" : "false") + ",\"stop\":" + SpatialSplitCaptureSink.Json(run.stop) + ",\"vertexSpan\":" + run.vertexSpan + ",\"referencedVertices\":" + run.referencedVertices
                + MeasureJson(run);
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
    }
}
#endif
