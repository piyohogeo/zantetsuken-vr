#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The graph stage's limits (GRAPH-CONNECT-1 §4.1, §4.3, §3.3; GRAPH-CONNECT-4 §3-1): the capacity caps and the work
    /// budget the engine stops at (Unfinished(capacity / work), never a verdict), and the wait before an unfinished
    /// attempt is tried again. Every value is a positive number; the engine's caps of 0 ("none") are not offered here,
    /// so that the product never runs without a bound. Provisional values (2026-10-23), from the tuning set's observed
    /// maxima (span 465,932, nodes 3,615, candidates 39,077, work 19.64 M units); a change is another search
    /// (<see cref="CutSpatialSplitSettings.SearchesAs"/>).
    /// </summary>
    [Serializable]
    public struct SpatialSplitGraphLimits
    {
        /// <summary>Triangles of the geometry, convexes of the shape: over either, the stage is not entered (Unfinished(capacity)).</summary>
        public int maxTriangles, maxConvexes;

        /// <summary>The span of the referenced vertex ids (greatest - least + 1): the compaction's marks are this many bytes.</summary>
        public int maxVertexSpan;

        /// <summary>Nodes (convexes + mesh components) and candidate pairs; the engine's native bytes held at once; the work units.</summary>
        public int maxNodes, maxCandidates;
        public long maxGraphBytes, workBudget;

        /// <summary>The wait, in frames, before an unfinished attempt is tried again: the first, then doubled up to the greatest (saturating).</summary>
        public int deferFirstFrames, deferMaxFrames;

        /// <summary>The greatest wait allowed: the doubling and the deadline's addition stay far from overflowing.</summary>
        public const int MaxDeferFrames = 1 << 24;

        public const int DefaultTriangles = 131072, DefaultConvexes = 64, DefaultVertexSpan = 1048576, DefaultNodes = 4096, DefaultCandidates = 131072;
        public const long DefaultGraphBytes = 16L << 20, DefaultWorkBudget = 32000000L;
        public const int DefaultDeferFirstFrames = 64, DefaultDeferMaxFrames = 4096;

        public SpatialSplitGraphLimits(
            int maxTriangles, int maxConvexes, int maxVertexSpan, int maxNodes, int maxCandidates, long maxGraphBytes, long workBudget,
            int deferFirstFrames, int deferMaxFrames)
        {
            this.maxTriangles = maxTriangles;
            this.maxConvexes = maxConvexes;
            this.maxVertexSpan = maxVertexSpan;
            this.maxNodes = maxNodes;
            this.maxCandidates = maxCandidates;
            this.maxGraphBytes = maxGraphBytes;
            this.workBudget = workBudget;
            this.deferFirstFrames = deferFirstFrames;
            this.deferMaxFrames = deferMaxFrames;
        }

        public static SpatialSplitGraphLimits Default => new SpatialSplitGraphLimits(
            DefaultTriangles, DefaultConvexes, DefaultVertexSpan, DefaultNodes, DefaultCandidates, DefaultGraphBytes, DefaultWorkBudget,
            DefaultDeferFirstFrames, DefaultDeferMaxFrames);

        public bool IsValid => maxTriangles >= 1 && maxConvexes >= 2 && maxVertexSpan >= 1 && maxNodes >= 2 && maxCandidates >= 1
            && maxGraphBytes >= 1 && workBudget >= 1
            && deferFirstFrames >= 1 && deferFirstFrames <= deferMaxFrames && deferMaxFrames <= MaxDeferFrames;

        public bool Equals(in SpatialSplitGraphLimits other)
        {
            return maxTriangles == other.maxTriangles && maxConvexes == other.maxConvexes && maxVertexSpan == other.maxVertexSpan
                && maxNodes == other.maxNodes && maxCandidates == other.maxCandidates && maxGraphBytes == other.maxGraphBytes
                && workBudget == other.workBudget && deferFirstFrames == other.deferFirstFrames && deferMaxFrames == other.deferMaxFrames;
        }

        /// <summary>
        /// The wait before the count-th retry (count from 1): the first wait doubled count - 1 times, never past the greatest
        /// (saturating: once the greatest is reached it is not doubled again, whatever the count). Computed in 64 bits.
        /// </summary>
        public long WaitFrames(int count)
        {
            long wait = deferFirstFrames;
            for (int k = 1; k < count && wait < deferMaxFrames; k++)
            {
                wait = Math.Min(wait * 2L, (long)deferMaxFrames);
            }

            return Math.Min(wait, (long)deferMaxFrames);
        }
    }

    /// <summary>What the graph stage concluded of an attempt's input: nothing (not entered), a placement, or why none.</summary>
    public enum SpatialSplitGraphVerdict
    {
        None = 0,
        Separable,
        NotSeparable,
        NotFound,
        Unfinished,
        Invalid,
    }

    /// <summary>
    /// Why the graph stage ended short of a conclusion (GRAPH-CONNECT-4 §3-2, §3-3). CutAttempts and GjkIterations are the
    /// engine's fixed search limits (a NotFound reached by them is not a conclusion); the others are the caps, the budget,
    /// the allocations, the preparation contract and the clock.
    /// </summary>
    public enum SpatialSplitGraphStop
    {
        None = 0,
        CutAttempts,
        GjkIterations,
        Work,
        Capacity,
        Allocation,
        NotPrepared,
        Time,
        Invalid,
        Count,
    }

    /// <summary>
    /// The graph stage's record of one attempt, kept apart from the plane search's result (which the mesh-led search
    /// resets): the verdict, the stop, the counts, the work, the room and the time.
    /// </summary>
    public struct SpatialSplitGraphRecord
    {
        public SpatialSplitGraphVerdict verdict;
        public SpatialSplitGraphStop stop;
        public bool unfinished;
        public int nodes, meshComponents, cutAttempts, gjkLimitHit, listGrowths, vertexSpan, referencedVertices;
        public long candidates, work, workPrepare, workSearch, nativeBytes;
        public int positiveConvexes, negativeConvexes, positiveTriangles, negativeTriangles;
        public double seconds;
        public string reason;
        /// <summary>Whether the engine's numeric bodies ran at all (false: the decision ended before them -- a cap, an allocation, the preparation contract, an invalid input).</summary>
        public bool bodiesRan;
        /// <summary>Whether the bodies ran through Burst (the direct call's own mark; false with bodiesRan: the managed fallback). Meaningful only with bodiesRan.</summary>
        public bool burstRan;

        public bool Entered => verdict != SpatialSplitGraphVerdict.None;
    }

    public sealed partial class CutSpatialSplit
    {
        // ---- the graph stage (GRAPH-CONNECT-4): its effective state, its counts and its record ---------------------------

        /// <summary>Whether the engine's main-thread preparation succeeded for this world (asked for once, when the graph was first wanted).</summary>
        public bool GraphPrepared { get; private set; }

        /// <summary>
        /// Whether the graph stage runs for attempts issued now: wanted by the settings, prepared, and not refused. A change
        /// of this is another search: the marks and the waits made under the other state are void.
        /// </summary>
        public bool GraphEffective { get; private set; }

        /// <summary>Why the graph stage is off though the settings ask for it ("" when it is not asked for, or runs).</summary>
        public string GraphRefusal { get; private set; } = "";

        private bool _graphPrepareTried, _graphReevaluatePending;

        public int GraphSearches { get; private set; }
        public int GraphSeparable { get; private set; }
        public int GraphNotSeparable { get; private set; }
        public int GraphNotFound { get; private set; }
        public int GraphUnfinished { get; private set; }
        public int GraphInvalid { get; private set; }
        public int PublishedGraph { get; private set; }
        public long GraphGjkLimitHits { get; private set; }

        /// <summary>Of the attempts that entered the graph stage: how many ran the engine's bodies through Burst, as managed code, or not at all (ended before them).</summary>
        public int GraphBurstRan { get; private set; }
        public int GraphManagedRan { get; private set; }
        public int GraphBodiesNotRun { get; private set; }
        public long GraphWorkMax { get; private set; }
        public long GraphNativeBytesMax { get; private set; }
        /// <summary>The graph stage's time, summed over the engine runs, and the longest; meaningful only when <see cref="GraphTimeMeasured"/> (an observation of the Editor and the Development Player: a Release build does not clock it, and says so).</summary>
        public double GraphSeconds { get; private set; }
        public double MaxGraphSeconds { get; private set; }

        /// <summary>Whether this build clocks the graph stage (the observation define, per file): false in a Release build, where the time stays 0 without having been measured.</summary>
        public static bool GraphTimeMeasured
        {
            get
            {
#if ZANTETSU_OBSERVE
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>The time's words in <see cref="DescribeGraph"/>: a measured sum and longest, or "not measured" when the build does not observe (never a 0.000 that was not clocked).</summary>
        internal static string DescribeGraphTime(bool measured, double seconds, double maxSeconds)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return measured
                ? "worker ms " + (seconds * 1000.0).ToString("F3", inv) + " (longest " + (maxSeconds * 1000.0).ToString("F3", inv) + ")"
                : "worker ms not measured (observation off in this build)";
        }
        private readonly int[] _graphStops = new int[(int)SpatialSplitGraphStop.Count];

        /// <summary>How many attempts the graph stage ended short of a conclusion for this reason.</summary>
        public int GraphUnfinishedAs(SpatialSplitGraphStop stop) => _graphStops[(int)stop];

        /// <summary>The last attempt's graph record (default until one entered the stage).</summary>
        public SpatialSplitGraphRecord LastGraph { get; private set; }

        /// <summary>
        /// The effective state of the graph stage, recomputed where what it depends on changes: the settings, the capture
        /// (GRAPH-CONNECT-4 §3-11) and the end of an attempt. With an attempt out, the change waits for its collection (the
        /// attempt runs with the settings it was issued with); a change of the effective state advances the mark epoch,
        /// forgets the waits and ends an idle wait, as a change of the search does.
        /// </summary>
        private void ReevaluateGraph()
        {
            if (_attempt != null)
            {
                _graphReevaluatePending = true;
                return;
            }

            _graphReevaluatePending = false;
            bool wanted = _settings.graphSearch;
            bool effective = wanted;
            string refusal = "";
            if (wanted && !GraphPrepared)
            {
                // The main thread's one-time preparation (GRAPH-CONNECT-1 §1.4): once for the process; this world asks once.
                if (!_graphPrepareTried)
                {
                    _graphPrepareTried = true;
                    GraphPrepared = SpatialSplitGraph.IsPrepared || (TestGraphPrepareFailAllocation >= 0 ? SpatialSplitGraph.PrepareForTests(TestGraphPrepareFailAllocation) : SpatialSplitGraph.Prepare());
                }

                if (!GraphPrepared)
                {
                    effective = false;
                    refusal = "the main thread's preparation of the graph engine failed in this world";
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (effective && Capture != null)
            {
                // The capture records the plane search's result and distribution in its current form (SpatialSplitCapture);
                // a graph-led attempt has no such result. Until the capture has a graph section (unit 4), the two do not run together.
                effective = false;
                refusal = "the capture records the search in its current form only (GRAPH-CONNECT-4 §3-11; the graph section is unit 4)";
            }
#endif

            if (effective != GraphEffective)
            {
                // Another search: what was marked or deferred under the other state says nothing of this one.
                _markEpoch++;
                ForgetDeferrals();
                _limitReached.Clear();
                _idle = false;
            }

            if (wanted && !effective && refusal != GraphRefusal)
            {
                UnityEngine.Debug.Log("SPATIAL SPLIT GRAPH: off -- asked for by the settings, but " + refusal);
            }

            GraphEffective = effective;
            GraphRefusal = wanted ? refusal : "";
        }

        // ---- the deferred retry (GRAPH-CONNECT-4 §3-5) ------------------------------------------------------------------------
        // An attempt that ended Unfinished recorded nothing about its input: its fragment waits, then is tried again. The wait
        // table is apart from the marks and never feeds them. The look (Examine) reads it only; the walk (Choose), the issue
        // and the end write it. The idle wait ends at the nearest deadline still to come, and never holds while a retry whose
        // deadline passed is unissued.

        private struct Deferral
        {
            internal Input input;
            internal long until;      // the frame the wait ends
            internal int count;       // how many waits this fragment's input has had, the doubling's step
            internal bool dueSeen;    // the walk looked at it after the deadline and did not issue it: no longer a reason to wake
        }

        private readonly Dictionary<LogicalFragmentId, Deferral> _deferred = new Dictionary<LogicalFragmentId, Deferral>();
        private readonly List<LogicalFragmentId> _deferredScratch = new List<LogicalFragmentId>();
        private long _deferredWake = long.MaxValue;
        private bool _deferredDue;

        /// <summary>Fragments waiting for a retry after an unfinished attempt.</summary>
        public int DeferredNow => _deferred.Count;

        /// <summary>The nearest frame a wait ends at (long.MaxValue: none to come), and whether a wait ended without its retry issued yet.</summary>
        public long DeferredWakeFrame => _deferredWake;
        public bool DeferredDue => _deferredDue;

        public int DeferralsMade { get; private set; }

        /// <summary>Tests: the engine's allocation of this ordinal fails in every attempt issued while set (-1: none).</summary>
        internal static int TestGraphFailAllocation = -1;

        /// <summary>Tests: the GJK iteration limit of the attempts issued while set (0: the engine's default).</summary>
        internal static int TestGraphGjkMaxIterations;

        /// <summary>Tests: the sides' allocation on the main thread is refused while set.</summary>
        internal static bool TestFailGraphSidesAllocation;

        /// <summary>Tests: the issue's enqueue is refused while set (as a full queue refuses it): the wait of a deferred fragment stays.</summary>
        internal static bool TestRefuseIssue;

        /// <summary>Tests: a world preparing the engine has the allocation of this ordinal fail (-1: the ordinary preparation).</summary>
        internal static int TestGraphPrepareFailAllocation = -1;

        /// <summary>Tests: the sides buffer handed to the engine is declared too short while set: an invalid input, as a malformed one.</summary>
        internal static bool TestGraphShortSides;

        /// <summary>Tests: the sharing out by the engine's sides is refused while set (as a malformed sides buffer would be): a Separable left incomplete.</summary>
        internal static bool TestGraphRefuseDistribution;

        /// <summary>Tests: every attempt issued while set runs the configuration C order (GRAPH-CONNECT-10: the graph stage before the bent partition, the bent partition on an unfinished graph). The product's settings never set it.</summary>
        internal static bool TestGraphFirst;

        // ---- a fixed limit reached (GRAPH-CONNECT-5 reviews 1 and 2) ---------------------------------------------------------
        // The graph stage ended by a limit the same input meets at the same place under the same search -- the cut-attempt or
        // GJK-iteration limit, the work budget, a capacity cap (span, nodes, candidates, bytes): not a conclusion (never a mark),
        // and not worth a retry until the input or the search changes (the engine is deterministic; waiting changes nothing).
        // Recorded here, keyed to the input (with the search's epoch), until either changes; swept with the marks; cleared when
        // the stage's effective state changes. A passing cause -- an allocation refused, the preparation contract, the clock --
        // is deferred instead (the wait, GRAPH-CONNECT-4 §3-5).

        private readonly Dictionary<LogicalFragmentId, Input> _limitReached = new Dictionary<LogicalFragmentId, Input>();
        private readonly List<LogicalFragmentId> _limitScratch = new List<LogicalFragmentId>();

        /// <summary>Fragments whose graph stage reached a fixed search limit on their current input: not tried again until it changes.</summary>
        public int LimitReachedNow => _limitReached.Count;

        public int LimitsReached { get; private set; }

        private static bool IsFixedLimit(SpatialSplitGraphStop stop) =>
            stop == SpatialSplitGraphStop.CutAttempts || stop == SpatialSplitGraphStop.GjkIterations || stop == SpatialSplitGraphStop.Work || stop == SpatialSplitGraphStop.Capacity;

        private void NoteLimitReached(Attempt attempt)
        {
            _limitReached[attempt.fragment] = attempt.input;
            LimitsReached++;
        }

        private void ForgetGoneLimits()
        {
            if (_limitReached.Count == 0) return;
            LogicalCutLedger ledger = _world.Ledger;
            _limitScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, Input> pair in _limitReached)
            {
                if (ledger == null || !ledger.IsCurrentTarget(pair.Key)) _limitScratch.Add(pair.Key);
            }

            for (int i = 0; i < _limitScratch.Count; i++) _limitReached.Remove(_limitScratch[i]);
        }

        /// <summary>Tests: the frame the given fragment's wait ends at (long.MinValue when it is not waiting).</summary>
        internal long DeferredUntil(LogicalFragmentId fragment) => _deferred.TryGetValue(fragment, out Deferral d) ? d.until : long.MinValue;

        internal int DeferredCount(LogicalFragmentId fragment) => _deferred.TryGetValue(fragment, out Deferral d) ? d.count : 0;

        /// <summary>The waits forgotten (a change of the search): nothing is deferred under the new one.</summary>
        private void ForgetDeferrals()
        {
            if (_deferred.Count == 0) return;
            _deferred.Clear();
            RebuildDeferredWake();
        }

        /// <summary>
        /// The wake rebuilt from the table (never accumulated): the least deadline still to come, and whether a deadline passed
        /// with its retry unissued and not yet looked at by the walk.
        /// </summary>
        private void RebuildDeferredWake()
        {
            long frame = Time.frameCount;
            long wake = long.MaxValue;
            bool due = false;
            foreach (KeyValuePair<LogicalFragmentId, Deferral> pair in _deferred)
            {
                if (pair.Value.until > frame) wake = Math.Min(wake, pair.Value.until);
                else if (!pair.Value.dueSeen) due = true;
            }

            _deferredWake = wake;
            _deferredDue = due;
        }

        /// <summary>
        /// The walk looked at a fragment (Choose, after its Examine): a wait whose input is no longer the fragment's is cancelled;
        /// a wait whose deadline passed and whose fragment was not a candidate now is no longer a reason to wake (it will be
        /// looked at again with the others). A candidate goes on to the issue, which takes the wait over on success.
        /// </summary>
        private void NoteChosen(LogicalFragmentId fragment, in Input input, SpatialSplitPass pass)
        {
            if (!_deferred.TryGetValue(fragment, out Deferral d)) return;
            if (!d.input.Equals(input))
            {
                _deferred.Remove(fragment);
                RebuildDeferredWake();
                return;
            }

            if (pass != SpatialSplitPass.Deferred && pass != SpatialSplitPass.Candidate && Time.frameCount >= d.until && !d.dueSeen)
            {
                d.dueSeen = true;
                _deferred[fragment] = d;
                RebuildDeferredWake();
            }
        }

        /// <summary>The issue succeeded: the wait ends, its count goes on with the attempt (an unfinished end doubles from it).</summary>
        private void TakeDeferral(LogicalFragmentId fragment, Attempt attempt)
        {
            if (!_deferred.TryGetValue(fragment, out Deferral d)) return;
            attempt.deferralCount = d.count;
            _deferred.Remove(fragment);
            RebuildDeferredWake();
        }

        /// <summary>An attempt ended Unfinished: its fragment waits, for longer each time in a row (saturating), and the wake follows.</summary>
        private void Defer(Attempt attempt)
        {
            int count = attempt.deferralCount < int.MaxValue ? attempt.deferralCount + 1 : int.MaxValue;
            long wait = _settings.graphLimits.WaitFrames(count);
            long frame = Time.frameCount;
            long until = frame > long.MaxValue - wait ? long.MaxValue : frame + wait;
            _deferred[attempt.fragment] = new Deferral { input = attempt.input, until = until, count = count, dueSeen = false };
            DeferralsMade++;
            RebuildDeferredWake();
        }

        /// <summary>Fragments that are no longer the ledger's current targets wait for nothing: swept where the marks are, at a round's start.</summary>
        private void ForgetGoneDeferrals()
        {
            if (_deferred.Count == 0) return;
            LogicalCutLedger ledger = _world.Ledger;
            _deferredScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, Deferral> pair in _deferred)
            {
                if (ledger == null || !ledger.IsCurrentTarget(pair.Key)) _deferredScratch.Add(pair.Key);
            }

            for (int i = 0; i < _deferredScratch.Count; i++) _deferred.Remove(_deferredScratch[i]);
            RebuildDeferredWake();
        }

        private void CountGraph(Attempt attempt)
        {
            SpatialSplitGraphRecord g = attempt.graph;
            LastGraph = g;
            if (!g.Entered)
            {
                return;
            }

            GraphSearches++;
            switch (g.verdict)
            {
                case SpatialSplitGraphVerdict.Separable: GraphSeparable++; break;
                case SpatialSplitGraphVerdict.NotSeparable: GraphNotSeparable++; break;
                case SpatialSplitGraphVerdict.NotFound: GraphNotFound++; break;
                case SpatialSplitGraphVerdict.Unfinished: GraphUnfinished++; _graphStops[(int)g.stop]++; break;
                case SpatialSplitGraphVerdict.Invalid: GraphInvalid++; break;
            }

            GraphGjkLimitHits += g.gjkLimitHit;
            // the bodies' execution, as the engine says it (not inferred from the work: a compaction scan spends work before the bodies)
            if (!g.bodiesRan) GraphBodiesNotRun++; else if (g.burstRan) GraphBurstRan++; else GraphManagedRan++;
            GraphWorkMax = Math.Max(GraphWorkMax, g.work);
            GraphNativeBytesMax = Math.Max(GraphNativeBytesMax, g.nativeBytes);
#if ZANTETSU_OBSERVE
            GraphSeconds += g.seconds;
            MaxGraphSeconds = Math.Max(MaxGraphSeconds, g.seconds);
#endif
        }

        public string DescribeGraph()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            SpatialSplitGraphLimits l = Settings.graphLimits;
            var text = new StringBuilder(384);
            text.Append("graph partition (").Append(Settings.graphSearch
                    ? (GraphEffective ? "on" : "asked for, off: " + GraphRefusal) + "; triangles " + l.maxTriangles.ToString(inv) + ", convexes " + l.maxConvexes.ToString(inv)
                      + ", span " + l.maxVertexSpan.ToString(inv) + ", nodes " + l.maxNodes.ToString(inv) + ", candidates " + l.maxCandidates.ToString(inv)
                      + ", bytes " + l.maxGraphBytes.ToString(inv) + ", work " + l.workBudget.ToString(inv)
                      + ", wait " + l.deferFirstFrames.ToString(inv) + ".." + l.deferMaxFrames.ToString(inv) + " frames"
                    : "off")
                .Append("): searches ").Append(GraphSearches).Append(", separable ").Append(GraphSeparable).Append(", published ").Append(PublishedGraph)
                .Append(", not separable ").Append(GraphNotSeparable).Append(", not found ").Append(GraphNotFound)
                .Append(", unfinished ").Append(GraphUnfinished);
            if (GraphUnfinished != 0)
            {
                text.Append(" (");
                bool first = true;
                for (int s = 1; s < (int)SpatialSplitGraphStop.Count; s++)
                {
                    if (_graphStops[s] == 0) continue;
                    if (!first) text.Append(", ");
                    first = false;
                    text.Append((SpatialSplitGraphStop)s).Append(' ').Append(_graphStops[s]);
                }

                text.Append(')');
            }

            text.Append(", invalid ").Append(GraphInvalid)
                .Append("; bodies through Burst ").Append(GraphBurstRan).Append(", managed ").Append(GraphManagedRan).Append(", not run ").Append(GraphBodiesNotRun)
                .Append("; GJK limit hits ").Append(GraphGjkLimitHits)
                .Append(", most work ").Append(GraphWorkMax).Append(" units, most native bytes ").Append(GraphNativeBytesMax)
                .Append("; limit reached now ").Append(LimitReachedNow).Append(" (").Append(LimitsReached).Append(" in all)")
                .Append("; deferred now ").Append(DeferredNow).Append(" (waits made ").Append(DeferralsMade).Append(", wake frame ").Append(_deferredWake == long.MaxValue ? "-" : _deferredWake.ToString(inv)).Append(_deferredDue ? ", a retry due" : "").Append(')')
                .Append("; ").Append(DescribeGraphTime(GraphTimeMeasured, GraphSeconds, MaxGraphSeconds));
            return text.ToString();
        }
    }
}
