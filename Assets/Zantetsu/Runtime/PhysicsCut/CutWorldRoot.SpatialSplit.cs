using System;
using Unity.Profiling;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The additional split of one owner into two by a plane that separates its parts (DESIGN 7.9.2, Phase 5.6):
    /// made with the world, given one turn a frame before the frame's collection, and let go with the world. On by
    /// the profile's default (2026-10-11); a profile or <see cref="SpatialSplitOffArgument"/> switches it off.
    /// </summary>
    public partial class CutWorldRoot
    {
        private static readonly ProfilerMarker s_spatialSplitMarker = new ProfilerMarker("Zantetsu.SpatialSplit.Step");

        /// <summary>The optional split of a settled owner whose parts one plane separates (DESIGN 7.9.2).</summary>
        public CutSpatialSplit SpatialSplit { get; private set; }

        /// <summary>
        /// The Player argument that switches the spatial split on for a run (DESIGN 7.9.2) where a profile leaves it
        /// off. Every other value is the profile's. With the profile's default (on) it changes nothing.
        /// </summary>
        public const string SpatialSplitArgument = "-zantetsuSpatialSplit";

        /// <summary>
        /// The Player argument that switches the spatial split off for a run: the way back from the default, and for
        /// comparing with and without it in one build. It wins over <see cref="SpatialSplitArgument"/>.
        /// </summary>
        public const string SpatialSplitOffArgument = "-zantetsuSpatialSplitOff";

        /// <summary>
        /// The Player argument that switches the bent partition on for a run (DESIGN 7.9.2; off in the profile's
        /// default of 2026-10-15), with the profile's own limits where they are valid and the provisional ones
        /// otherwise. Nothing without the split itself on.
        /// </summary>
        public const string SpatialSplitBentArgument = "-zantetsuSpatialSplitBent";

        /// <summary>The Player argument that switches the bent partition off for a run (on in the profile's default since 2026-10-16, D-237): the comparison's control.</summary>
        public const string SpatialSplitBentOffArgument = "-zantetsuSpatialSplitBentOff";

        /// <summary>
        /// The Player argument that switches the graph stage on for a run (GRAPH-CONNECT-4; on in the profile's default since
        /// 2026-10-26, D-256; off on 2026-10-23), with the profile's own limits where they are valid and the provisional ones
        /// otherwise. Nothing without the split itself on; nothing with the capture on (the split says why).
        /// </summary>
        public const string SpatialSplitGraphArgument = "-zantetsuSpatialSplitGraph";

        /// <summary>
        /// The Player argument that switches the graph stage off for a run: with <see cref="SpatialSplitGraphFirstOffArgument"/>, the way
        /// back to the configuration D (D-256). Given with <see cref="SpatialSplitGraphArgument"/>, on wins.
        /// </summary>
        public const string SpatialSplitGraphOffArgument = "-zantetsuSpatialSplitGraphOff";

        /// <summary>
        /// The Player argument that asks for the graph-first order for a run (GRAPH-CONNECT-12: the graph stage before the bent
        /// partition, the bent partition only after an unfinished graph; on in the profile's default since 2026-10-26, D-256): applied
        /// only where the graph stage is on and effective -- without it the request is logged and the order stays as before.
        /// </summary>
        public const string SpatialSplitGraphFirstArgument = "-zantetsuSpatialSplitGraphFirst";

        /// <summary>The Player argument that keeps the order as before for a run (the configuration D's order; D-256). Given with <see cref="SpatialSplitGraphFirstArgument"/>, on wins.</summary>
        public const string SpatialSplitGraphFirstOffArgument = "-zantetsuSpatialSplitGraphFirstOff";

        /// <summary>
        /// GRAPH-CONNECT-14: the Player argument that turns the timeline's new clock readings and tallies off for a run (development builds;
        /// the existing clocks stand), and the one that names the file the per-attempt rows go to at the world's end.
        /// </summary>
        public const string SplitTimelineOffArgument = "-zantetsuSplitTimelineOff";

        public const string SplitTimelineArgument = "-zantetsuSplitTimeline";

        /// <summary>Reads <see cref="SpatialSplitGraphFirstArgument"/> and <see cref="SpatialSplitGraphFirstOffArgument"/> as the other switches are read; both given = on.</summary>
        internal static bool TryReadGraphFirstSwitch(string[] arguments, out bool on)
        {
            on = arguments != null && Array.IndexOf(arguments, SpatialSplitGraphFirstArgument) >= 0;
            return on || (arguments != null && Array.IndexOf(arguments, SpatialSplitGraphFirstOffArgument) >= 0);
        }

        /// <summary>
        /// NPC-BONE-FALLBACK-1: the Player argument that asked for the NPC fallback when it was an evaluation's switch; on by default since
        /// 2026-11-04 (NPC-BONE-FALLBACK-DEFAULT-1), it is kept for the launches that still give it (on, as the default).
        /// </summary>
        public const string NpcBoneFallbackArgument = "-zantetsuNpcBoneFallback";

        /// <summary>
        /// NPC-BONE-FALLBACK-DEFAULT-1: the Player argument that turns the NPC fallback off for a run -- the method as before does not go
        /// on behind an NPC's own decision. That decision is the protected combination's (the default since 2026-11-14,
        /// NPC-PAIR-DEFAULT-1), which then ends where it places nothing; with <see cref="NpcPairProtectOffArgument"/> as well it is the
        /// bones' alone, as before 2026-11-04. Given with <see cref="NpcBoneFallbackArgument"/>, off wins.
        /// </summary>
        public const string NpcBoneFallbackOffArgument = "-zantetsuNpcBoneFallbackOff";

        /// <summary>The NPC fallback a launch asks for (<see cref="NpcBoneFallbackArgument"/>, <see cref="NpcBoneFallbackOffArgument"/>): on unless the off argument is given; the launch line's words.</summary>
        internal static bool ReadNpcBoneFallback(string[] arguments, out string asked)
        {
            bool on = arguments != null && Array.IndexOf(arguments, NpcBoneFallbackArgument) >= 0;
            bool off = arguments != null && Array.IndexOf(arguments, NpcBoneFallbackOffArgument) >= 0;
            asked = off ? "off, asked for by " + NpcBoneFallbackOffArgument + (on ? " (given with " + NpcBoneFallbackArgument + ": off wins)" : "") + ": the method as before does not go on behind an NPC's own decision -- the protected combination's (the default since 2026-11-14), which ends where it places nothing; the bones' alone, as before 2026-11-04, with " + NpcPairProtectOffArgument + " as well"
                : on ? "on, asked for by " + NpcBoneFallbackArgument + " (the default since 2026-11-04 in any case): an NPC's piece its bones connect goes on to the method as before"
                : "on -- the default since 2026-11-04 (NPC-BONE-FALLBACK-DEFAULT-1): an NPC's piece its bones connect goes on to the method as before; " + NpcBoneFallbackOffArgument + " turns it off";
            return !off;
        }

        /// <summary>
        /// NPC-PAIR-LIVE-1: the Player argument that asked for an NPC's additional split to be decided by the pairwise combination of the
        /// bones and the geometry with the protection (pair-or-protect, mode 2) when it was a Development Player's switch; on by default
        /// in every build since 2026-11-14 (NPC-PAIR-DEFAULT-1), it is kept for the launches that still give it (on, as the default).
        /// </summary>
        public const string NpcPairProtectArgument = "-zantetsuNpcPairProtect";

        /// <summary>
        /// NPC-PAIR-DEFAULT-1: the Player argument that turns the protected combination off for a run -- an NPC's piece is decided by its
        /// bones, with the method as before behind them as the NPC fallback's own setting has it (as before 2026-11-14). Read by every
        /// build. Given with <see cref="NpcPairProtectArgument"/>, off wins.
        /// </summary>
        public const string NpcPairProtectOffArgument = "-zantetsuNpcPairProtectOff";

        /// <summary>The protected combination a launch asks for (<see cref="NpcPairProtectArgument"/>, <see cref="NpcPairProtectOffArgument"/>): on unless the off argument is given; the launch line's words of what was asked.</summary>
        internal static bool ReadNpcPairProtect(string[] arguments, out string asked)
        {
            bool on = arguments != null && Array.IndexOf(arguments, NpcPairProtectArgument) >= 0;
            bool off = arguments != null && Array.IndexOf(arguments, NpcPairProtectOffArgument) >= 0;
            asked = off ? "off, asked for by " + NpcPairProtectOffArgument + (on ? " (given with " + NpcPairProtectArgument + ": off wins)" : "")
                : on ? "on, asked for by " + NpcPairProtectArgument + " (the default since 2026-11-14 in any case)"
                : "on -- the default since 2026-11-14 (NPC-PAIR-DEFAULT-1); " + NpcPairProtectOffArgument + " turns it off";
            return !off;
        }

        /// <summary>The launch line of the protected combination: what was asked, what is in effect, the engine's preparation, the fallback and its graph stage.</summary>
        internal static string DescribeNpcPairProtect(string asked, bool askedOn, bool effective, string refusal, bool enginePrepared, bool boneFallback, bool reuse)
        {
            string stage = reuse
                ? "the fallback's graph stage reuses the combination's decision and is not run where the links' check holds (no bent partition after a stage not run)"
                : "the fallback's graph stage runs from the start (" + CutSpatialSplit.PairReuseOffArgument + "): no links' check, nothing reused";
            return "NPC PAIR PROTECT: asked: " + asked + "; in effect: " + (effective
                    ? "on -- an NPC's additional split is decided by the pairwise combination of the bones and the geometry with the protection (pair-or-protect, mode "
                      + CutSpatialSplit.PairProtectMode + "); where it places nothing the method as before goes on (the NPC fallback " + (boneFallback ? "on" : "OFF: it ends there") + "); " + stage
                    : askedOn ? "OFF though asked for: " + refusal + "; an NPC's piece is decided by its bones (the NPC fallback " + (boneFallback ? "on" : "off") + ")"
                    : "off -- an NPC's piece is decided by its bones, the method as before behind them where the NPC fallback is on (" + (boneFallback ? "on" : "off") + ")")
                + "; the graph engine: " + (enginePrepared ? "prepared" : askedOn ? "NOT prepared" : "not asked of the combination (prepared: no)");
        }

        private void MakeSpatialSplit(CutWorldProfile profile)
        {
            SpatialSplit = new CutSpatialSplit(this, SplitSettings(profile));
            SpatialSplit.BoneFallback = ReadNpcBoneFallback(Environment.GetCommandLineArgs(), out string fallback);
            UnityEngine.Debug.Log("NPC BONE FALLBACK: " + fallback, this);
            // NPC-PAIR-DEFAULT-1 (every build): the protected combination is settled here, before this world's additional split issues
            // anything -- on unless the launch turns it off; the graph engine's main-thread preparation is made sure of by the switch,
            // and where it fails the combination stays off for this world with the reason. One line: what was asked, what is in
            // effect, the engine's preparation, and the fallback's graph stage (CutSpatialSplit.PairReuse, read from the launch once).
            bool pairAsked = ReadNpcPairProtect(Environment.GetCommandLineArgs(), out string pairWords);
            SpatialSplit.SetPairProtect(pairAsked);
            UnityEngine.Debug.Log(DescribeNpcPairProtect(pairWords, pairAsked, SpatialSplit.PairEffective, SpatialSplit.PairRefusal, SpatialSplitGraph.IsPrepared, SpatialSplit.BoneFallback, CutSpatialSplit.PairReuse), this);
#if ZANTETSU_OBSERVE
            if (SpatialSplit.PairEffective && SpatialSplitCaptureSession.Sink != null && SpatialSplitCaptureSession.CaptureBones)
            {
                // the capture's bone-results line is the bone-aware decision's own (an attempt the combination decides has none)
                UnityEngine.Debug.LogError("SPLIT CAPTURE: the NPC attempts' capture is of the bone-aware decision (bone-results.jsonl); with the protected combination on an NPC's attempt "
                    + "has no result line there. Launch a capture with " + NpcPairProtectOffArgument + ".", this);
            }
#endif
            if (TryReadRetryAfter(Environment.GetCommandLineArgs(), out int holds, out double seconds, out string refusal))
            {
                SpatialSplit.RetryAfterHolds = holds;
                SpatialSplit.RetryAfterHeldSeconds = seconds;
                UnityEngine.Debug.Log("SPATIAL SPLIT RETRY AFTER: " + holds + " budget-short comparisons and " + seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + " s of play, asked for by " + SpatialSplitRetryAfterArgument + " (the product's are " + CutSpatialSplit.DefaultRetryAfterHolds + " and "
                    + CutSpatialSplit.DefaultRetryAfterHeldSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")", this);
            }
            else if (refusal != null)
            {
                UnityEngine.Debug.LogError("SPATIAL SPLIT RETRY AFTER: " + refusal + ": the product's stand", this);
            }
        }

        /// <summary>
        /// The Player argument that gives the retry of an unmeasured cut publication its two conditions for a run (unit
        /// 10-B, a device check of the retry itself): followed by a whole number of budget-short comparisons (1 or more)
        /// and a play time in seconds (0 or more). Without it the product's stand (<see cref="CutSpatialSplit.DefaultRetryAfterHolds"/>,
        /// <see cref="CutSpatialSplit.DefaultRetryAfterHeldSeconds"/>).
        /// </summary>
        public const string SpatialSplitRetryAfterArgument = "-zantetsuSpatialSplitRetryAfter";

        internal static bool TryReadRetryAfter(string[] arguments, out int holds, out double seconds, out string refusal)
        {
            holds = 0;
            seconds = 0.0;
            refusal = null;
            int at = arguments != null ? Array.IndexOf(arguments, SpatialSplitRetryAfterArgument) : -1;
            if (at < 0)
            {
                return false;
            }

            if (at + 2 < arguments.Length
                && int.TryParse(arguments[at + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out holds) && holds >= 1
                && double.TryParse(arguments[at + 2], System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out seconds)
                && seconds >= 0.0 && !double.IsInfinity(seconds))
            {
                return true;
            }

            refusal = SpatialSplitRetryAfterArgument + " is not followed by a whole number of 1 or more and a number of seconds of 0 or more";
            holds = 0;
            seconds = 0.0;
            return false;
        }

        /// <summary>
        /// The Player argument that gives the convex cut's triangle minimum for a run (DESIGN 7.9.2), followed by the
        /// number: for looking at other minimums in one build. Without it the profile's stands (50 by default).
        /// </summary>
        public const string SpatialSplitMinTrianglesArgument = "-zantetsuSpatialSplitMinTriangles";

        /// <summary>
        /// Reads <see cref="SpatialSplitMinTrianglesArgument"/>: false with no refusal when it is not given, false
        /// with the refusal when what follows it is not a whole number of one or more.
        /// </summary>
        internal static bool TryReadMinTriangles(string[] arguments, out int minimum, out string refusal)
        {
            minimum = 0;
            refusal = null;
            int at = arguments != null ? Array.IndexOf(arguments, SpatialSplitMinTrianglesArgument) : -1;
            if (at < 0)
            {
                return false;
            }

            if (at + 1 < arguments.Length
                && int.TryParse(arguments[at + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minimum)
                && minimum >= 1)
            {
                return true;
            }

            refusal = SpatialSplitMinTrianglesArgument + " is followed by " + (at + 1 < arguments.Length ? "'" + arguments[at + 1] + "'" : "nothing") + ", not by a whole number of 1 or more";
            minimum = 0;
            return false;
        }

        /// <summary>
        /// Reads <see cref="SpatialSplitBentArgument"/> and <see cref="SpatialSplitBentOffArgument"/>: false when neither
        /// is given (the profile's setting stands); true with <paramref name="on"/> otherwise, the on argument winning
        /// where both are given.
        /// </summary>
        internal static bool TryReadBentSwitch(string[] arguments, out bool on)
        {
            on = arguments != null && Array.IndexOf(arguments, SpatialSplitBentArgument) >= 0;
            return on || (arguments != null && Array.IndexOf(arguments, SpatialSplitBentOffArgument) >= 0);
        }

        /// <summary>Reads <see cref="SpatialSplitGraphArgument"/> and <see cref="SpatialSplitGraphOffArgument"/> as <see cref="TryReadBentSwitch"/> reads the bent partition's.</summary>
        internal static bool TryReadGraphSwitch(string[] arguments, out bool on)
        {
            on = arguments != null && Array.IndexOf(arguments, SpatialSplitGraphArgument) >= 0;
            return on || (arguments != null && Array.IndexOf(arguments, SpatialSplitGraphOffArgument) >= 0);
        }

        // The profile's settings, or -- asked for by the arguments -- the same with the split on and with the
        // convex cut's triangle minimum given for the run.
        private CutSpatialSplitSettings SplitSettings(CutWorldProfile profile)
        {
            CutSpatialSplitSettings settings = profile.SpatialSplit;
            string[] arguments = Environment.GetCommandLineArgs();
            if (TryReadMinTriangles(arguments, out int minimum, out string refusal))
            {
                UnityEngine.Debug.Log(
                    "SPATIAL SPLIT MINIMUM: " + minimum + " triangles on the side with fewer, asked for by " + SpatialSplitMinTrianglesArgument
                    + " (the profile's own is " + settings.minCutSideTriangles + ")", this);
                settings.minCutSideTriangles = minimum;
            }
            else if (refusal != null)
            {
                UnityEngine.Debug.LogError("SPATIAL SPLIT MINIMUM: " + refusal + ": the profile's " + settings.minCutSideTriangles + " stands", this);
            }

            if (TryReadBentSwitch(arguments, out bool bentOn))
            {
                if (bentOn)
                {
                    bool valid = settings.bentLimits.IsValid;
                    UnityEngine.Debug.Log(
                        "SPATIAL SPLIT BENT: on -- asked for by " + SpatialSplitBentArgument + " (the profile's own setting is " + (settings.bentSearch ? "on" : "off")
                        + "), the " + (valid ? "profile's" : "provisional") + " limits", this);
                    settings.bentSearch = true;
                    if (!valid) settings.bentLimits = SpatialSplitBentLimits.Default;
                }
                else
                {
                    UnityEngine.Debug.Log(
                        "SPATIAL SPLIT BENT: off -- asked for by " + SpatialSplitBentOffArgument + " (the profile's own setting is " + (settings.bentSearch ? "on" : "off") + ")", this);
                    settings.bentSearch = false;
                }
            }

            if (TryReadGraphSwitch(arguments, out bool graphOn))
            {
                if (graphOn)
                {
                    bool valid = settings.graphLimits.IsValid;
                    UnityEngine.Debug.Log(
                        "SPATIAL SPLIT GRAPH: on -- asked for by " + SpatialSplitGraphArgument + " (the profile's own setting is " + (settings.graphSearch ? "on" : "off")
                        + "), the " + (valid ? "profile's" : "provisional") + " limits", this);
                    settings.graphSearch = true;
                    if (!valid) settings.graphLimits = SpatialSplitGraphLimits.Default;
                }
                else
                {
                    UnityEngine.Debug.Log(
                        "SPATIAL SPLIT GRAPH: off -- asked for by " + SpatialSplitGraphOffArgument + " (the profile's own setting is " + (settings.graphSearch ? "on" : "off") + ")", this);
                    settings.graphSearch = false;
                }
            }

            if (TryReadGraphFirstSwitch(arguments, out bool graphFirstOn))
            {
                // the order's request is logged with whether it can apply: the graph stage must be on (the setting or its switch)
                UnityEngine.Debug.Log(
                    "SPATIAL SPLIT ORDER: " + (graphFirstOn ? "graph-first" : "as before") + " -- asked for by " + (graphFirstOn ? SpatialSplitGraphFirstArgument : SpatialSplitGraphFirstOffArgument)
                    + " (the profile's own setting is " + (settings.graphFirst ? "graph-first" : "as before") + "); the graph stage is " + (settings.graphSearch ? "on" : "OFF: the order cannot apply") , this);
                settings.graphFirst = graphFirstOn;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Array.IndexOf(arguments, SplitTimelineOffArgument) >= 0)
            {
                CutSpatialSplit.TimelineObserve = false;
                DispatchObservation.Enabled = false;
                UnityEngine.Debug.Log("SPATIAL SPLIT TIMELINE: off -- asked for by " + SplitTimelineOffArgument + " (the new stamps and tallies are not taken; the existing clocks stand)", this);
            }
            int timelineAt = Array.IndexOf(arguments, SplitTimelineArgument);
            if (timelineAt >= 0 && timelineAt + 1 < arguments.Length && !arguments[timelineAt + 1].StartsWith("-", StringComparison.Ordinal))
            {
                CutSpatialSplit.TimelineCsvPath = arguments[timelineAt + 1];
                UnityEngine.Debug.Log("SPATIAL SPLIT TIMELINE: the attempts' rows go to " + CutSpatialSplit.TimelineCsvPath + " at the world's end (asked for by " + SplitTimelineArgument + ")", this);
            }
            else if (timelineAt >= 0)
            {
                UnityEngine.Debug.LogError("SPATIAL SPLIT TIMELINE: " + SplitTimelineArgument + " has no file: no rows are kept", this);
            }
#endif

            if (Array.IndexOf(arguments, SpatialSplitOffArgument) >= 0)
            {
                UnityEngine.Debug.Log(
                    "SPATIAL SPLIT: off -- asked for by " + SpatialSplitOffArgument + " (the profile's own setting is " + (settings.enabled ? "on" : "off") + ")", this);
                settings.enabled = false;
                return settings;
            }

            if (Array.IndexOf(arguments, SpatialSplitArgument) < 0)
            {
                if (settings.enabled)
                {
                    // On by the profile: said once, as the argument's line says it, so that a run's log tells which.
                    UnityEngine.Debug.Log("SPATIAL SPLIT: on -- the profile's setting: " + DescribeSplit(in settings), this);
                }

                return settings;
            }

            UnityEngine.Debug.Log(
                "SPATIAL SPLIT: on -- asked for by " + SpatialSplitArgument + " (the profile's own setting is " + (settings.enabled ? "on" : "off")
                + "): " + DescribeSplit(in settings), this);
            settings.enabled = true;
            return settings;
        }

        /// <summary>What a world with these settings splits, for the line that says the split was switched on: the settings as they are.</summary>
        internal static string DescribeSplit(in CutSpatialSplitSettings settings)
        {
            string what = settings.cutConvexes
                ? "a settled owner whose parts one plane separates becomes two objects -- a plane that leaves its convexes whole where there is one, "
                  + "and otherwise a plane that leaves its triangles whole, the convexes that cross it cut by it (convex cut on: only when the side with fewer triangles has "
                  + settings.minCutSideTriangles + " or more; an owner of one convex is looked at too); not for buildings, no triangle is cut"
                : "a settled owner of two or more convexes that one plane separates, triangles and convexes whole, becomes two objects; not for buildings, no convex is cut (convex cut off)";
            return what
                + (settings.bentSearch
                    ? "; bent partition on: where no plane leaves the convexes whole, a surface of a few plane pieces that leaves convexes and triangles whole, before any convex is cut"
                    : "; bent partition off")
                + "; " + settings.examinePerFrame + " fragments looked at a frame at most, one attempt at a time";
        }

        /// <summary>
        /// Switches the spatial split on for this world, every other value the profile's: what
        /// <see cref="SpatialSplitArgument"/> does, for a host that has no command line (a device run's configuration
        /// file). To be called before the run's cuts; false when the world has no split.
        /// </summary>
        public bool EnableSpatialSplit(string askedBy)
        {
            if (SpatialSplit == null)
            {
                return false;
            }

            CutSpatialSplitSettings settings = SpatialSplit.Settings;
            UnityEngine.Debug.Log(
                "SPATIAL SPLIT: on -- asked for by " + askedBy + " (the profile's own setting is " + (settings.enabled ? "on" : "off")
                + "): " + DescribeSplit(in settings), this);
            settings.enabled = true;
            SpatialSplit.Settings = settings;
            return true;
        }

        /// <summary>
        /// Switches the bent partition on for this world's split (DESIGN 7.9.2), the profile's limits where valid and
        /// the provisional ones otherwise: what <see cref="SpatialSplitBentArgument"/> does, for a host that has no
        /// command line (a device run's configuration file). Nothing of the split itself is switched. False when the
        /// world has no split.
        /// </summary>
        public bool EnableSpatialSplitBent(string askedBy)
        {
            if (SpatialSplit == null)
            {
                return false;
            }

            CutSpatialSplitSettings settings = SpatialSplit.Settings;
            bool valid = settings.bentLimits.IsValid;
            UnityEngine.Debug.Log(
                "SPATIAL SPLIT BENT: on -- asked for by " + askedBy + " (the profile's own setting is " + (settings.bentSearch ? "on" : "off")
                + "), the " + (valid ? "profile's" : "provisional") + " limits", this);
            settings.bentSearch = true;
            if (!valid) settings.bentLimits = SpatialSplitBentLimits.Default;
            SpatialSplit.Settings = settings;
            return true;
        }

        // In LateUpdate, after the groups' turn and before the frame's collection: a publication here is reflected by
        // this very frame's collection, and a retirement the groups made in this frame holds this turn (the ledger
        // changed). Off, and with no attempt outstanding: nothing at all.
        private void StepSpatialSplit()
        {
            if (SpatialSplit != null && (SpatialSplit.Settings.enabled || SpatialSplit.HasAttempt))
            {
                using (s_spatialSplitMarker.Auto())
                {
                    SpatialSplit.Step();
                }
            }
        }

        private void ReleaseSpatialSplit()
        {
            SpatialSplit?.Release();
            SpatialSplit = null;
        }
    }
}
