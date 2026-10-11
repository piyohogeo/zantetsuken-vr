using System;
using Unity.Profiling;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The additional split of one owner into two by a plane that crosses nothing of it (DESIGN 7.9.2, Phase 5.6,
    /// first version): made with the world, given one turn a frame before the frame's collection, and let go with the
    /// world. Off unless the profile switches it on.
    /// </summary>
    public partial class CutWorldRoot
    {
        private static readonly ProfilerMarker s_spatialSplitMarker = new ProfilerMarker("Zantetsu.SpatialSplit.Step");

        /// <summary>The optional split of a settled owner whose parts one plane separates (DESIGN 7.9.2).</summary>
        public CutSpatialSplit SpatialSplit { get; private set; }

        /// <summary>
        /// The Player argument that switches the spatial split on for a run (DESIGN 7.9.2): for looking at it, and for
        /// comparing with and without it in one build, while the profile leaves it off. Every other value is the
        /// profile's.
        /// </summary>
        public const string SpatialSplitArgument = "-zantetsuSpatialSplit";

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

            if (Array.IndexOf(arguments, SpatialSplitArgument) < 0)
            {
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
            return what + "; " + settings.examinePerFrame + " fragments looked at a frame at most, one attempt at a time";
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
