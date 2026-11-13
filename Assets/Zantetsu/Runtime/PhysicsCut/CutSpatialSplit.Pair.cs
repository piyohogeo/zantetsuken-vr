using System;
using System.Diagnostics;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// NPC-PAIR-EVAL-1: one decision of the pairwise combination of the bones and the geometry (the evaluation's; nothing in the
    /// product asks for it) -- what it came to, what it counted by pair kind, and its times on the deciding thread.
    /// </summary>
    internal sealed class SpatialSplitPairRecord
    {
        /// <summary>
        /// 1: a pair is taken off where the bones or the geometry say it is not connected. 2: the same, with the main supporting mesh's
        /// connection to its bone's convex kept. 3 (A, NPC-PAIR-EVAL-3): 2 and each mesh component's connection to the convex of the
        /// bone it supports by the most vertices kept. 4 (B): 2 and, after a placement, a mesh node left with no edge moved to the
        /// side of its own main convexes.
        /// </summary>
        public int mode;
        /// <summary>A: the pairs the component-side protection added to the protected ones, and of the own main pairs those whose cluster the ratio had left out (kept as pairs; no cluster is added).</summary>
        public int ownProtectedAdded, ownUnderRatio, ownMainPairs;
        public bool separable, invalid, unfinished, bonesKnown, usedTopology, bodiesRan, burstRan;
        public SpatialSplitPairBones bones;
        public string verdict = "", stop = "", reason = "";
        public NpcBoneSplitVerdict boneAlone;
        public string boneAloneReason = "";
        public int nodes, meshComponents, clusters, cutAttempts, seamSlots, protectedPairsOfBones;
        public long candidates, gjkIterations, gjkLimitHit, triedB, refutedB, ambiguousB, work, graphBytes, attributeBytes, boneBytes;
        public int positiveConvexes, negativeConvexes, positiveTriangles, negativeTriangles;
        /// <summary>The counts by pair kind (0 convex-convex, 1 convex-mesh, 2 mesh-mesh) and the protected pairs' ends; null when the engine did not reach its pairs.</summary>
        public SpatialSplitGraph.PairInput counts;
        /// <summary>
        /// Seconds on the deciding thread, from the driver's own clock readings (the same the graph stage of the method as before has;
        /// no clock is read inside the bodies: measure 0): the bones' attributes (the bone-aware decision with its rows); the engine's
        /// compaction and conversion; its preparation body (owner coordinates, components, boxes, hulls and the pairs' exclusions by
        /// the bones and the boxes -- pairsSeconds is 0, not taken apart); the search's areas; the search (the cuts, the hulls); the
        /// read-back; and all of it.
        /// </summary>
        public double boneSeconds, convertSeconds, nodesSeconds, pairsSeconds, allocateSeconds, searchSeconds, finishSeconds, totalSeconds;
    }

    /// <summary>What the bone-aware decision gives a pairwise combination (NPC-PAIR-EVAL-1 review 1).</summary>
    internal enum SpatialSplitPairBones
    {
        /// <summary>The connections were worked out (Separable or NotSeparable): the rows are read.</summary>
        Known = 0,

        /// <summary>
        /// Not known of a well-formed input (the rig or the pose not known, a convex of a bone the rig does not have, a weight on such
        /// a bone, triangles weighted to no bone, two convexes of one bone) or over the bones' limits: no pair is left out by the
        /// bones, the geometry alone decides.
        /// </summary>
        NotKnown,

        /// <summary>
        /// The input is malformed (<see cref="NpcBoneSplitRecord.malformed"/>: an array missing or short, the topology missing or
        /// without a vertex, a bone number outside the rig's range, a vertex or an origin outside the lineage): the combination ends
        /// Invalid with the reason; nothing is placed and nothing falls back.
        /// </summary>
        Invalid,
    }

    public sealed partial class CutSpatialSplit
    {
        internal sealed unsafe partial class Attempt
        {
            // NPC-PAIR-EVAL-1 (the evaluation's switch; 0 in the product): the pairwise combination decides first -- 1 the plain
            // combination, 2 with the protection -- and, where it places nothing and pairFallback is set, the method as before goes on.
            internal int pairMode;
            internal bool pairFallback, pairLed, pairFellBack;
            internal SpatialSplitPairRecord pair;

            /// <summary>
            /// Which of the three the input is for the combination: its topology (<paramref name="topologyKnown"/>), then the bones'
            /// record (the rows filled or not). The topology is required whatever the bones say (review 2, 2026-11-13): the common
            /// nodes are made by it, and the bone-aware decision may end before it looks at the topology (the rig not known, over its
            /// limits) -- an input without it is Invalid, never decided by render vertex.
            /// </summary>
            internal static SpatialSplitPairBones BonesOf(in NpcBoneSplitRecord record, bool rowsFilled, bool topologyKnown = true)
            {
                if (!topologyKnown || record.malformed) return SpatialSplitPairBones.Invalid;
                return rowsFilled && (record.verdict == NpcBoneSplitVerdict.Separable || record.verdict == NpcBoneSplitVerdict.NotSeparable) ? SpatialSplitPairBones.Known : SpatialSplitPairBones.NotKnown;
            }

            /// <summary>
            /// The pairwise combination over the attempt's own pointers (the worker): the bone-aware decision once, for the nodes'
            /// attributes (its own verdict is kept in <see cref="bone"/>); then the graph engine with those attributes and the
            /// geometry's topology -- a pair the bones do not connect is left out before its boxes are read, the rest go through the
            /// boxes and, where a cut needs them, the hulls. The sides of a placement into the attempt's sides' bytes. The bones not
            /// known (insufficient, over their limits): no pair is left out by them, the geometry alone decides.
            /// </summary>
            private void RunPair()
            {
                long begin = Stopwatch.GetTimestamp();
                var p = new SpatialSplitPairRecord { mode = pairMode };
                var attributes = new NpcBonePairAttributes();
                try
                {
                    NpcBoneSplitSearch.Input given = boneInput;
                    given.indices = indices;
                    given.indexCount = indexCount;
                    given.pairs = attributes;
                    var record = default(NpcBoneSplitRecord);
                    NpcBoneSplitSearch.Decide(in given, (byte*)graphSides.GetUnsafePtr(), graphSides.IsCreated ? graphSides.Length : 0, ref record);
                    p.boneSeconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    record.seconds = p.boneSeconds;
                    bone = record;
                    p.boneAlone = record.verdict;
                    p.boneAloneReason = record.reason ?? "";
                    p.boneBytes = record.nativeBytes;
                    // The topology is the combination's own requirement, checked here whatever the bones' decision came to (it may have
                    // ended before its own topology check: the rig not known, the triangles over its limit).
                    bool topologyKnown = given.topology.ranges != null && given.topology.rangeCount > 0 && given.topology.topologyVertexCount > 0;
                    SpatialSplitPairBones bones = BonesOf(in record, attributes.filled, topologyKnown);
                    p.bones = bones;
                    p.bonesKnown = bones == SpatialSplitPairBones.Known;
                    if (bones == SpatialSplitPairBones.Invalid)
                    {
                        // refused here, with the reason (the engine is not run over it)
                        p.verdict = "Invalid";
                        p.invalid = true;
                        p.reason = !topologyKnown ? "the geometry's topology is not known (the bones: " + record.verdict + ", " + (record.reason ?? "") + ")"
                            : "the bones' input is malformed: " + (record.reason ?? "");
                        return;
                    }

                    p.attributeBytes = attributes.NativeBytes;
                    p.clusters = attributes.clusters;
                    p.protectedPairsOfBones = attributes.protectedPairs;

                    var settings = new SpatialSplitGraph.GraphSettings
                    {
                        secondsLimit = 0.0, measure = 0, nearestSelection = true, flowDinic = true, pairsSweep = false, hashPrepare = false, requirePrepared = true,
                        maxTriangles = graphLimits.maxTriangles, maxConvexes = graphLimits.maxConvexes, maxVertexSpan = graphLimits.maxVertexSpan, maxNodes = graphLimits.maxNodes,
                        maxCandidates = graphLimits.maxCandidates, maxGraphBytes = graphLimits.maxGraphBytes, workBudget = graphLimits.workBudget,
                    };
                    // the common nodes are always by the topology (a vertex without a topology vertex is the engine's Invalid)
                    var counts = new SpatialSplitGraph.PairInput { protect = pairMode >= 2, relocate = pairMode == 4, useTopology = true, topology = given.topology };
                    p.ownMainPairs = attributes.ownMainPairs;
                    if (p.bonesKnown && pairMode == 3) attributes.ApplyOwnMain(out p.ownProtectedAdded, out p.ownUnderRatio);
                    p.usedTopology = true;
                    if (p.bonesKnown)
                    {
                        counts.rows = attributes.Rows;
                        counts.nodes = attributes.Nodes;
                        counts.clusterWords = attributes.clusterWords;
                        counts.rowWords = attributes.rowWords;
                        counts.convexWords = attributes.convexWords;
                        counts.triComponent = attributes.TriComponent;
                    }

                    settings.pair = counts;
                    var span = new SpatialSplitGraph.SpanInput
                    {
                        vertices = vertices, vertexViewLength = vertexViewLength, indices = indices, indexCount = indexCount,
                        convexVertices = null, banks = banks, ranges = ranges, convexCount = convexCount,
                        geometryToOwner = geometryToOwner, shapeToOwner = shapeToOwner, margin = margin,
                        sidesOut = (byte*)graphSides.GetUnsafePtr(), sidesOutLength = graphSides.Length,
                    };
                    SpatialSplitGraph.GraphOutcome o = SpatialSplitGraph.DecideSpan(span, settings, true, out SpatialSplitGraph.NativeRun run);
                    p.counts = counts.ran ? counts : null;
                    p.seamSlots = counts.seamSlots;
                    p.nodes = o.convexCount + o.meshComponents;
                    p.meshComponents = o.meshComponents;
                    p.candidates = o.candidatesA;
                    p.cutAttempts = o.cutAttempts;
                    p.gjkIterations = o.gjkIterations;
                    p.gjkLimitHit = o.gjkLimitHit;
                    p.triedB = o.triedB;
                    p.refutedB = o.refutedB;
                    p.ambiguousB = o.ambiguousB;
                    p.work = o.work;
                    p.graphBytes = run.nativeBytes;
                    p.bodiesRan = run.bodiesRan;
                    p.burstRan = run.burstRan;
                    p.reason = o.reason ?? "";
                    p.positiveConvexes = o.positiveConvexes; p.negativeConvexes = o.negativeConvexes;
                    p.positiveTriangles = o.positiveTriangles; p.negativeTriangles = o.negativeTriangles;
                    p.convertSeconds = run.secondsConvert;
                    p.pairsSeconds = 0.0;
                    p.nodesSeconds = run.secondsPrepare;
                    p.allocateSeconds = run.secondsAllocate;
                    p.searchSeconds = run.secondsSearch;
                    p.finishSeconds = run.secondsFinish;
                    switch (o.verdict)
                    {
                        case SpatialSplitGraph.GraphVerdict.Separable:
                            p.verdict = "Separable";
                            p.separable = true;
                            break;
                        case SpatialSplitGraph.GraphVerdict.NotSeparable:
                            p.verdict = "NotSeparable";
                            break;
                        case SpatialSplitGraph.GraphVerdict.NotFound:
                            // as the graph stage reads it: a NotFound reached by a fixed search limit is not a conclusion
                            if (o.cutLimitHit) { p.verdict = "Unfinished"; p.unfinished = true; p.stop = "CutAttempts"; }
                            else if (o.gjkLimitHit > 0) { p.verdict = "Unfinished"; p.unfinished = true; p.stop = "GjkIterations"; }
                            else p.verdict = "NotFound";
                            break;
                        case SpatialSplitGraph.GraphVerdict.InvalidInput:
                            p.verdict = "Invalid";
                            p.invalid = true;
                            break;
                        default:
                            p.verdict = "Unfinished";
                            p.unfinished = true;
                            p.stop = o.stop ?? "";
                            break;
                    }
                }
                finally
                {
                    attributes.Dispose();
                    p.totalSeconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    pair = p;
                }
            }
        }
    }
}
