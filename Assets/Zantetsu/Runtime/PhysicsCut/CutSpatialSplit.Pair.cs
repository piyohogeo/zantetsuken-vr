using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.MeshCut;

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
        // ---- NPC-PAIR-LIVE-1: the protected combination in ordinary play ------------------------------------------------------------
        // On by default in every build since 2026-11-14 (NPC-PAIR-DEFAULT-1; -zantetsuNpcPairProtectOff turns it off, read by the
        // world: CutWorldRoot.ReadNpcPairProtect). With it an NPC's attempt is decided by the
        // pairwise combination of the bones and the geometry with the protection (mode 2), exactly as the evaluation ran it (RunPair:
        // the protection, the engine's own short cuts, the topology required, the smaller group first, an invalid input ended, anything
        // else that places nothing going on to the method as before where the NPC fallback is on). Without it nothing here runs and an
        // NPC's attempt is decided as before. A placement the combination makes is shared out by its sides as the bones' and the
        // graph's are, and published by the same path; each child's part of the rig is worked out from that placement (NeedBones),
        // not from the bones' own decision's sides. A fallback's publication keeps the fallback's own following (its plane, or the
        // convexes' bones). Every attempt is counted once, by the way it ended.

        /// <summary>The mode the argument turns on: the combination with the protection (NPC-PAIR-EVAL-1's pair-or-protect).</summary>
        public const int PairProtectMode = 2;

        // ---- NPC-PAIR-REUSE-1: what the combination's decision leaves for the graph stage of its own fallback ----------------------
        // The combination and the graph stage of the method as before run the same engine over the same input on the same worker, one
        // after the other. Within one attempt the stage reads what the combination's decision computed and does not depend on the
        // bones, the protection or the way the mesh components are made: the compaction, the owner coordinates, the bounds, the
        // corners' slots, and the hull judgments carried out on pairs of convexes (SpatialSplitGraph.Kept). Before the stage, the
        // links' check (SpatialSplitGraph.ConvexesHeld) says whether any graph of the input could place a convex on each side; where
        // none can, the stage is not run. The work budget and the byte cap count everything reused as if it had been computed, so a
        // stage that runs ends as it ended before. The kept set lives on the worker, inside the attempt's run, and is gone when the
        // run returns -- whatever the run came to.

        /// <summary>
        /// The Player argument (every build, NPC-PAIR-DEFAULT-1) that keeps the combination and turns the reuse off: the fallback's graph
        /// stage then runs from the start -- no links' check, no stage left out, nothing reused -- as before NPC-PAIR-REUSE-1.
        /// </summary>
        public const string PairReuseOffArgument = "-zantetsuNpcPairReuseOff";

        /// <summary>Whether a fallback's graph stage reuses the combination's decision (on unless the launch says otherwise, read once; tests set it).</summary>
        internal static bool PairReuse = ReadPairReuseOfThisLaunch();

        /// <summary>The reuse a launch asks for: on unless <see cref="PairReuseOffArgument"/> is given.</summary>
        internal static bool ReadPairReuse(string[] arguments) => arguments == null || Array.IndexOf(arguments, PairReuseOffArgument) < 0;

        private static bool ReadPairReuseOfThisLaunch()
        {
            try
            {
                return ReadPairReuse(Environment.GetCommandLineArgs());
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Fallbacks after the combination whose graph stage was entered; of them not run (the links' check); of the others, run on the combination's compaction and first preparation.</summary>
        public int PairGraphStages { get; private set; }
        public int PairGraphSkipped { get; private set; }
        public int PairGraphReusedInput { get; private set; }

        /// <summary>Those stages' own searches: hull judgments asked, of them of two convexes, of them answered from the kept ones.</summary>
        public long PairGraphHullPairs { get; private set; }
        public long PairGraphConvexHullPairs { get; private set; }
        public long PairGraphReusedPairs { get; private set; }

        /// <summary>The links' checks that ran to their end, the hull judgments they carried out and those they read.</summary>
        public int PairLinkChecks { get; private set; }
        public long PairLinkJudged { get; private set; }
        public long PairLinkReused { get; private set; }

        /// <summary>Seconds on the workers (development builds; the stage's own clock): the stages entered, of them those not run, and the links' checks within both.</summary>
        public double PairGraphSeconds { get; private set; }
        public double PairGraphSkippedSeconds { get; private set; }
        public double PairLinkSeconds { get; private set; }

        /// <summary>
        /// What an NPC attempt of the combination will hold beyond the blocks every attempt takes, known before anything is taken: the
        /// marks' room (a byte a vertex of the lineage) and, where its fallback's graph stage may reuse, the most the kept set's own
        /// tables can be. 0 for another owner, or with the combination off. A fragment with anchors takes neither (it is decided as
        /// before): the count is then over by these bytes, never under.
        /// </summary>
        private long PairBytesBeforeIssue(LogicalFragmentId fragment, int convexCount, int maxConvexVertices)
        {
            if (!PairEffective) return 0;
            NpcBoneSplitRegistry bones = _world.NpcBones;
            if (bones == null || !bones.TryGet(fragment, out NpcBoneFragment record)) return 0;
            return Math.Max(1, record.Lineage.LocalCount) + PairKeptBytesAtMost(convexCount, maxConvexVertices);
        }

        private long PairKeptBytesAtMost(int convexCount, int maxConvexVertices) =>
            PairReuse && BoneFallback && GraphEffective && convexCount >= 2 ? SpatialSplitGraph.Kept.OwnBytesAtMost(convexCount, maxConvexVertices) : 0;

        /// <summary>Asked for (the argument, or a test).</summary>
        public bool PairProtect { get; private set; }

        /// <summary>Asked for and the graph engine prepared on the main thread: NPC attempts issued now are the combination's.</summary>
        public bool PairEffective { get; private set; }

        /// <summary>Why it is asked for and not effective ("" otherwise).</summary>
        public string PairRefusal { get; private set; } = "";

        /// <summary>NPC attempts issued as the combination's; of those not made one because the fragment has anchors (decided as before).</summary>
        public int PairIssued { get; private set; }
        public int PairPassedForAnchors { get; private set; }

        /// <summary>The combination's decisions that ran, by what they came to.</summary>
        public int PairDecided { get; private set; }
        public int PairSeparable { get; private set; }
        public int PairNotSeparable { get; private set; }
        public int PairNotFound { get; private set; }
        public int PairUnfinished { get; private set; }
        public int PairInvalid { get; private set; }

        /// <summary>Attempts that went on to the method as before after the combination placed nothing.</summary>
        public int PairFellBack { get; private set; }

        /// <summary>Publications: of the combination's own placement, and of a fallback's after it.</summary>
        public int PublishedPair { get; private set; }
        public int PublishedPairFallback { get; private set; }

        /// <summary>Attempts of the combination that did not run, or threw, on the worker; and those whose result was of an input no longer the fragment's.</summary>
        public int PairNotRun { get; private set; }
        public int PairExceptions { get; private set; }
        public int PairStale { get; private set; }

        /// <summary>Placements whose children's bones could not be worked out (each child then keeps the bones with a convex on its side), and the last reason.</summary>
        public int PairBonesNotWorkedOut { get; private set; }
        public string LastPairBonesFailure { get; private set; } = "";

        /// <summary>Of the published placements: bones both children keep, and bones kept with no convex of their own (auxiliary), summed.</summary>
        public long PairBonesOnBothSides { get; private set; }
        public long PairAuxiliaryBones { get; private set; }

        private readonly int[] _pairEnds = new int[(int)SpatialSplitEnd.Count];
        private readonly int[] _pairFallbackEnds = new int[(int)SpatialSplitEnd.Count];

        /// <summary>How the combination's attempts ended, the fallbacks apart.</summary>
        public int PairEndedAs(SpatialSplitEnd end) => _pairEnds[(int)end];
        public int PairFallbackEndedAs(SpatialSplitEnd end) => _pairFallbackEnds[(int)end];

        /// <summary>The last combination's record (null until one ran).</summary>
        internal SpatialSplitPairRecord LastPair { get; private set; }

        /// <summary>
        /// Turns the protected combination on or off for the NPC attempts issued from now on (the main thread; no attempt out). On: the
        /// graph engine's main-thread preparation is made sure of here -- the combination's bodies are the engine's -- and where it
        /// fails the combination stays off with the reason, NPC attempts decided as before. A change is another search: the marks and
        /// the waits made under the other state are void.
        /// </summary>
        internal void SetPairProtect(bool on)
        {
            if (_attempt != null || _attempts.Count != 0) throw new InvalidOperationException("the combination is switched with no attempt out");
            PairProtect = on;
            bool effective = on;
            string refusal = "";
            if (on)
            {
                bool prepared;
                try
                {
                    prepared = SpatialSplitGraph.IsPrepared || (TestGraphPrepareFailAllocation >= 0 ? SpatialSplitGraph.PrepareForTests(TestGraphPrepareFailAllocation) : SpatialSplitGraph.Prepare());
                }
                catch (Exception e)
                {
                    prepared = false;
                    refusal = "the graph engine's preparation threw: " + e.GetType().Name + ": " + e.Message;
                }

                if (!prepared)
                {
                    effective = false;
                    if (refusal.Length == 0) refusal = "the main thread's preparation of the graph engine failed in this world";
                }
            }

            if (effective != PairEffective)
            {
                _markEpoch++;
                ForgetDeferrals();
                _limitReached.Clear();
                AllMarksChanged();
                ForgetFound();
                _idle = false;
            }

            PairEffective = effective;
            PairRefusal = refusal;
        }

        // The issue's part (the main thread, after TakeBone made the attempt an NPC's): the combination's switch, its limits (the graph
        // stage's where that runs, the defaults otherwise -- as the evaluation issued it), the fallback as the NPC fallback has it, and
        // the room the children's bones are worked out in (freed with the attempt).
        private void TakePair(Attempt attempt, in CutSpatialSplitSettings settings)
        {
            if (!PairEffective) return;
            if (attempt.anchors != null)
            {
                PairPassedForAnchors++;   // the combination takes owners without anchors (the evaluation's inputs); this one is decided as before
                return;
            }

            int n = attempt.boneInput.lineage.nodeCount;
            attempt.pairPositiveBones = new bool[Math.Max(0, n)];
            attempt.pairNegativeBones = new bool[Math.Max(0, n)];
            attempt.pairMarks = PhysicsCutBlocks.Take<byte>(Math.Max(1, attempt.boneInput.lineage.localCount));
            attempt.pairMode = PairProtectMode;
            attempt.pairFallback = attempt.boneFallback;
            attempt.graphLimits = GraphEffective ? settings.graphLimits : SpatialSplitGraphLimits.Default;
            // NPC-PAIR-REUSE-1: the room the worker's kept set may take for its own tables, held against the attempts' bytes while
            // this one is out (the count the issue made before taking anything)
            attempt.pairKeptBytes = PairKeptBytesAtMost(attempt.convexCount, attempt.maxConvexVertices);
            // (counted as issued by the issue itself, once the queue took it)
        }

        /// <summary>The issue's part once the queue took the attempt: a combination's attempt is counted as issued.</summary>
        private void NotePairIssued(Attempt attempt)
        {
            if (attempt.pairMode != 0) PairIssued++;
        }

        // The end's part: one count an attempt, by the way it ended (in place of the bones' own counts: the bones did not decide it).
        private void CountPair(Attempt attempt)
        {
            SpatialSplitPairRecord p = attempt.pair;
            if (attempt.failure != null) PairExceptions++;
            if (LastEnd == SpatialSplitEnd.NotRun) PairNotRun++;
            if (LastEnd == SpatialSplitEnd.Stale) PairStale++;
            if (attempt.pairFellBack) { PairFellBack++; _pairFallbackEnds[(int)LastEnd]++; }
            else _pairEnds[(int)LastEnd]++;
            SpatialSplitGraphRecord g = attempt.graph;
            if (attempt.pairFellBack && g.Entered)
            {
                // NPC-PAIR-REUSE-1: the fallback's graph stage -- not run, or run on what the combination left
                PairGraphStages++;
                PairGraphSeconds += g.seconds;
                if (g.skipped) { PairGraphSkipped++; PairGraphSkippedSeconds += g.seconds; }
                else
                {
                    if (g.reusedInput) PairGraphReusedInput++;
                    PairGraphHullPairs += g.hullPairs;
                    PairGraphConvexHullPairs += g.convexHullPairs;
                    PairGraphReusedPairs += g.reusedPairs;
                }

                if (g.linkRan)
                {
                    PairLinkChecks++;
                    PairLinkJudged += g.linkJudged;
                    PairLinkReused += g.linkReused;
                }

                PairLinkSeconds += g.linkSeconds;
            }

            if (p == null) return;
            LastPair = p;
            PairDecided++;
            if (p.separable) PairSeparable++;
            else if (p.invalid) PairInvalid++;
            else if (p.unfinished) PairUnfinished++;
            else if (p.verdict == "NotSeparable") PairNotSeparable++;
            else PairNotFound++;
        }

        /// <summary>The combination's tallies in ordinary play, as a text of its own (the world's end).</summary>
        public string DescribePair()
        {
            var text = new StringBuilder(512);
            text.Append("NPC pair protect (").Append(PairEffective ? "on" : PairProtect ? "asked for, off: " + PairRefusal : "off").Append("; mode ").Append(PairProtectMode)
                .Append(", fallback ").Append(BoneFallback ? "on" : "off").Append("): issued ").Append(PairIssued).Append(" (NPC attempts with anchors, decided as before ").Append(PairPassedForAnchors)
                .Append("), decided ").Append(PairDecided).Append(" (apart ").Append(PairSeparable).Append(", connected ").Append(PairNotSeparable).Append(", not found ").Append(PairNotFound)
                .Append(", unfinished ").Append(PairUnfinished).Append(", invalid ").Append(PairInvalid).Append("); published by the combination ").Append(PublishedPair)
                .Append("; fell back ").Append(PairFellBack).Append(", published by the fallback ").Append(PublishedPairFallback).Append("; ended, the combination's own:");
            for (int e = 0; e < (int)SpatialSplitEnd.Count; e++) if (_pairEnds[e] > 0) text.Append(' ').Append((SpatialSplitEnd)e).Append(' ').Append(_pairEnds[e]);
            text.Append("; the fallbacks:");
            for (int e = 0; e < (int)SpatialSplitEnd.Count; e++) if (_pairFallbackEnds[e] > 0) text.Append(' ').Append((SpatialSplitEnd)e).Append(' ').Append(_pairFallbackEnds[e]);
            text.Append("; not run ").Append(PairNotRun).Append(", stale ").Append(PairStale).Append(", exceptions on the worker ").Append(PairExceptions)
                .Append("; children's bones: kept by both children ").Append(PairBonesOnBothSides.ToString(CultureInfo.InvariantCulture)).Append(", kept with no convex of their own ")
                .Append(PairAuxiliaryBones.ToString(CultureInfo.InvariantCulture)).Append(", not worked out ").Append(PairBonesNotWorkedOut)
                .Append(LastPairBonesFailure.Length > 0 ? " (the last: " + LastPairBonesFailure + ")" : "");
            text.Append("; the fallbacks' graph stage (reuse ").Append(PairReuse ? "on" : "off").Append("): entered ").Append(PairGraphStages).Append(", not run (the convexes held together) ").Append(PairGraphSkipped)
                .Append(", run on the combination's compaction and first preparation ").Append(PairGraphReusedInput).Append("; its hull judgments ").Append(PairGraphHullPairs.ToString(CultureInfo.InvariantCulture))
                .Append(" (of two convexes ").Append(PairGraphConvexHullPairs.ToString(CultureInfo.InvariantCulture)).Append(", of them not carried out again ")
                .Append(PairGraphReusedPairs.ToString(CultureInfo.InvariantCulture)).Append("); links' checks ").Append(PairLinkChecks).Append(" (judgments carried out ")
                .Append(PairLinkJudged.ToString(CultureInfo.InvariantCulture)).Append(", read ").Append(PairLinkReused.ToString(CultureInfo.InvariantCulture)).Append("); worker ms: the stages ")
                .Append((PairGraphSeconds * 1000.0).ToString("F3", CultureInfo.InvariantCulture)).Append(" (those not run ").Append((PairGraphSkippedSeconds * 1000.0).ToString("F3", CultureInfo.InvariantCulture))
                .Append(", the links' checks ").Append((PairLinkSeconds * 1000.0).ToString("F3", CultureInfo.InvariantCulture)).Append(')');
            if (LastPair != null)
            {
                text.Append("; the last decision: ").Append(LastPair.verdict).Append(LastPair.stop.Length > 0 ? " (" + LastPair.stop + ")" : "").Append(LastPair.reason.Length > 0 ? " (" + LastPair.reason + ")" : "")
                    .Append(", bones ").Append(LastPair.bones).Append(", nodes ").Append(LastPair.nodes).Append(", protected pairs ").Append(LastPair.protectedPairsOfBones)
                    .Append(", through Burst ").Append(LastPair.burstRan ? "yes" : "no");
            }

            if (_world.NpcBones != null) text.Append("; ").Append(_world.NpcBones.Describe());
            return text.ToString();
        }

        // The publication's part: what the records follow the split by. The combination's own placement: each child's needed bones
        // (NeedBones); where those could not be worked out, the bones with a convex on each side, as a fallback with no plane.
        private void NotePairSplit(LogicalFragmentId source, Attempt attempt, PhysicsOwnerShape positiveShape, PhysicsOwnerShape negativeShape)
        {
            NpcBoneSplitRegistry bones = _world.NpcBones;
            if (bones == null) return;
            if (attempt.pairBonesFailure != null || attempt.pairPositiveBones == null || attempt.pairNegativeBones == null)
            {
                PairBonesNotWorkedOut++;
                LastPairBonesFailure = attempt.pairBonesFailure ?? "no room was taken for them";
                int n = attempt.boneInput.lineage.nodeCount;
                bones.NoteSplitConvexBones(source, ConvexBonesOf(positiveShape, n), ConvexBonesOf(negativeShape, n));
                return;
            }

            PairBonesOnBothSides += attempt.pairBothBones;
            PairAuxiliaryBones += attempt.pairAuxiliaryBones;
            bones.NoteSplitNeededBones(source, attempt.pairPositiveBones, attempt.pairNegativeBones);
        }

        internal sealed unsafe partial class Attempt
        {
            // NPC-PAIR-EVAL-1 (the evaluation's switch; 0 in the product; NPC-PAIR-LIVE-1: 2 where the Development Player's argument
            // asks for it): the pairwise combination decides first -- 1 the plain combination, 2 with the protection -- and, where it
            // places nothing and pairFallback is set, the method as before goes on.
            internal int pairMode;
            internal bool pairFallback, pairLed, pairFellBack;
            internal SpatialSplitPairRecord pair;

            /// <summary>The combination decides this attempt's end and placement: asked of it, and it did not fall back (read after the worker).</summary>
            internal bool PairDecides => pairMode != 0 && !pairFellBack;

            /// <summary>The method as before ran after the bones, or the combination, placed nothing.</summary>
            internal bool FellBack => boneFellBack || pairFellBack;

            // NPC-PAIR-LIVE-1: the bones each child needs by the combination's placement (one entry a bone of the rig; the main thread
            // makes them at the issue, the worker fills them, the publication reads them), the marks' room the worker walks the
            // vertices' origins in (a byte a vertex of the lineage; taken at the issue, freed with the attempt), why they could not
            // be worked out (null: they were), and two counts of them.
            internal bool[] pairPositiveBones, pairNegativeBones;
            internal NativeArray<byte> pairMarks;
            internal string pairBonesFailure;
            internal int pairBothBones, pairAuxiliaryBones;

            // NPC-PAIR-REUSE-1: what the combination's decision leaves for the graph stage of its fallback -- made by the worker in
            // RunPair, read by RunGraph, disposed where the worker's run ends (Begin's finally), on every way out of it; never
            // touched by the main thread while the worker runs. And the bytes its own tables may take, counted with the attempt's
            // from the issue to its end.
            internal SpatialSplitGraph.Kept pairKept;
            internal long pairKeptBytes;

            /// <summary>The worker's kept set, gone (the worker's run ended, or the attempt is freed).</summary>
            private void DropPairKept()
            {
                SpatialSplitGraph.Kept kept = pairKept;
                pairKept = null;
                kept?.Dispose();
            }

            private void FreePair()
            {
                DropPairKept();
                pairKeptBytes = 0;
                if (pairMarks.IsCreated) pairMarks.Dispose();
                pairMarks = default;
                pairPositiveBones = null;
                pairNegativeBones = null;
            }

            /// <summary>
            /// The bones each child needs by the placement in the sides' bytes (the worker, right after the combination placed it and
            /// before the sharing out recodes the convexes' bytes) -- the evaluation's own rule for what a child would have to keep
            /// (SpatialSplitPairEval, "the bones each child would keep"): a bone of the rig the piece holds, or one with a convex in
            /// the piece, is needed by a child when a convex of it is on that child's side or a vertex of that child's triangles is
            /// weighted to it (walked back to the base vertices by the cuts' origins; any weight the model holds, so the bones with no
            /// convex of their own are among them). A bone both children need is both's. The joints and the segments each child keeps
            /// are then those of its bones that the piece still holds (NpcBonePiece.KeepBones: nothing taken off comes back).
            /// </summary>
            private void NeedBones()
            {
                pairBonesFailure = null;
                pairBothBones = pairAuxiliaryBones = 0;
                NpcBoneSplitSearch.Lineage lineage = boneInput.lineage;
                int n = lineage.nodeCount, locals = lineage.localCount;
                bool[] up = pairPositiveBones, down = pairNegativeBones;
                if (up == null || down == null || up.Length < n || down.Length < n || !pairMarks.IsCreated || pairMarks.Length < locals)
                {
                    pairBonesFailure = "no room was taken for them at the issue";
                    return;
                }

                if (lineage.baseBones == null || lineage.chunks == null || lineage.parent == null || lineage.present == null || boneInput.convexBones == null
                    || boneInput.jointKept == null || boneInput.segmentLo == null || boneInput.segmentHi == null)
                {
                    pairBonesFailure = "the lineage's base vertices, its cuts' origins, the convexes' bones or the piece's part of the rig are not known";
                    return;
                }

                Array.Clear(up, 0, up.Length);
                Array.Clear(down, 0, down.Length);
                byte* marks = (byte*)pairMarks.GetUnsafePtr();
                UnsafeUtility.MemClear(marks, locals);
                byte* sides = (byte*)graphSides.GetUnsafePtr();
                int triangles = indexCount / 3;

                // Each named vertex by the side of its triangles (bit 0 positive, bit 1 negative).
                for (int k = 0; k < triangles; k++)
                {
                    byte bit = sides[convexCount + k] != 0 ? (byte)1 : (byte)2;
                    for (int q = 0; q < 3; q++)
                    {
                        int local = NpcBoneSplitSearch.Local(in lineage, indices[(3 * k) + q]);
                        if ((uint)local >= (uint)locals)
                        {
                            pairBonesFailure = "a vertex of the piece is outside the lineage";
                            return;
                        }

                        marks[local] |= bit;
                    }
                }

                // Down the origins to the base: a cut's vertex gives its sides to the two it came from. The newest first; a pass that
                // marked a vertex above the one it came from is followed by another.
                for (int pass = 0; ; pass++)
                {
                    bool again = false;
                    for (int local = locals - 1; local >= lineage.baseCount; local--)
                    {
                        byte m = marks[local];
                        if (m == 0) continue;
                        if (!NpcBoneSplitSearch.TryOrigin(in lineage, local, out uint2 origin))
                        {
                            pairBonesFailure = "a cut's vertex has no origin in the lineage";
                            return;
                        }

                        if (origin.x == MeshCutOutput.NoVertexOrigin) continue;   // a cap's auxiliary vertex: weighted to nothing of its own
                        for (int q = 0; q < 2; q++)
                        {
                            if (q == 1 && origin.y == origin.x) break;
                            int from = NpcBoneSplitSearch.Local(in lineage, q == 0 ? origin.x : origin.y);
                            if ((uint)from >= (uint)locals)
                            {
                                pairBonesFailure = "a cut's vertex came from a vertex outside the lineage";
                                return;
                            }

                            if ((marks[from] & m) == m) continue;
                            marks[from] |= m;
                            if (from > local) again = true;
                        }
                    }

                    if (!again) break;
                    if (pass >= 64)
                    {
                        pairBonesFailure = "the origins did not settle in 64 passes";
                        return;
                    }
                }

                // The base vertices' bones, by the sides that reached them; then the convexes' bones by the convexes' sides.
                for (int x = 0; x < lineage.baseCount; x++)
                {
                    byte m = marks[x];
                    if (m == 0) continue;
                    ulong entry = lineage.baseBones[x];
                    for (int q = 0; q < 4; q++)
                    {
                        int bone = (int)((entry >> (16 * q)) & 0xFFFF);
                        if (bone == NpcVertexBones.None || (uint)bone >= (uint)n) continue;
                        if ((m & 1) != 0) up[bone] = true;
                        if ((m & 2) != 0) down[bone] = true;
                    }
                }

                // What the piece holds of the rig (NpcBonePiece.Present, from the same joints and segment parts the decision read): a
                // bone whose joint is kept, or a part of one of whose segments is.
                Span<bool> present = n <= 1024 ? stackalloc bool[n] : new bool[n];
                present.Clear();
                for (int k = 0; k < n; k++)
                {
                    if (!lineage.present[k]) continue;
                    if ((boneInput.jointKept[k >> 6] & (1UL << (k & 63))) != 0) present[k] = true;
                }

                for (int c = 0; c < n; c++)
                {
                    int p = lineage.parent[c];
                    if (p >= 0 && p < n && lineage.present[p] && boneInput.segmentLo[c] <= boneInput.segmentHi[c]) present[p] = true;
                }

                Span<bool> hasConvex = n <= 1024 ? stackalloc bool[n] : new bool[n];
                hasConvex.Clear();
                for (int c = 0; c < convexCount && c < boneInput.convexBones.Length; c++)
                {
                    int bone = boneInput.convexBones[c];
                    if ((uint)bone >= (uint)n) continue;
                    hasConvex[bone] = true;
                    if (sides[c] != 0) up[bone] = true; else down[bone] = true;
                }

                for (int k = 0; k < n; k++)
                {
                    bool held = present[k];
                    if (!held && !hasConvex[k])
                    {
                        up[k] = down[k] = false;   // neither held by the piece nor a convex's: nothing of it to keep
                        continue;
                    }

                    if (up[k] && down[k]) pairBothBones++;
                    if (!hasConvex[k]) pairAuxiliaryBones += (up[k] ? 1 : 0) + (down[k] ? 1 : 0);
                }
            }

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
                    // NPC-PAIR-REUSE-1: where the method as before may go on to its graph stage on this input, this decision leaves it
                    // what does not depend on the bones, the protection or the mesh components (none of the stage's test hooks set)
                    if (PairReuse && pairFallback && graphSearch && anchors == null && convexCount >= 2
                        && testGraphFailAllocation < 0 && testGraphGjkMaxIterations <= 0 && !testGraphShortSides)
                    {
                        pairKept = new SpatialSplitGraph.Kept();
                        settings.keep = pairKept;
                    }

                    var span = new SpatialSplitGraph.SpanInput
                    {
                        vertices = vertices, vertexViewLength = vertexViewLength, indices = indices, indexCount = indexCount,
                        convexVertices = null, banks = banks, ranges = ranges, convexCount = convexCount,
                        geometryToOwner = geometryToOwner, shapeToOwner = shapeToOwner, margin = margin,
                        sidesOut = (byte*)graphSides.GetUnsafePtr(), sidesOutLength = graphSides.Length,
                    };
                    SpatialSplitGraph.GraphOutcome o = SpatialSplitGraph.DecideSpan(span, settings, true, out SpatialSplitGraph.NativeRun run);
                    if (pairKept != null && !pairKept.Usable) DropPairKept();   // the decision ended before its preparation completed: nothing to reuse
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
                            // NPC-PAIR-LIVE-1: where the attempt was issued with the room for it, the bones each child needs by this placement
                            if (pairPositiveBones != null)
                            {
                                try
                                {
                                    NeedBones();
                                }
                                catch (Exception e)
                                {
                                    pairBonesFailure = "working them out threw: " + e.GetType().Name + ": " + e.Message;
                                }
                            }
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
