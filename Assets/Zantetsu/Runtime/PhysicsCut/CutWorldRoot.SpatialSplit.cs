using System;
using Unity.Profiling;

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

        private void MakeSpatialSplit(CutWorldProfile profile)
        {
            SpatialSplit = new CutSpatialSplit(this, SplitSettings(profile));
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
