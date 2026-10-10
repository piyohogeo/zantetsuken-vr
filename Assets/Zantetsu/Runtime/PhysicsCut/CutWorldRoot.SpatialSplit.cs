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

        // The profile's settings, or -- asked for by the argument -- the same with the split on.
        private CutSpatialSplitSettings SplitSettings(CutWorldProfile profile)
        {
            CutSpatialSplitSettings settings = profile.SpatialSplit;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), SpatialSplitArgument) < 0)
            {
                return settings;
            }

            UnityEngine.Debug.Log(
                "SPATIAL SPLIT: on -- asked for by " + SpatialSplitArgument + " (the profile's own setting is " + (settings.enabled ? "on" : "off")
                + "): a settled owner of two or more convexes that one plane separates, triangles and convexes whole, becomes two objects; not for buildings, no convex is cut; "
                + settings.examinePerFrame + " fragments looked at a frame at most, one attempt at a time", this);
            settings.enabled = true;
            return settings;
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
                + "): a settled owner of two or more convexes that one plane separates, triangles and convexes whole, becomes two objects; not for buildings, no convex is cut; "
                + settings.examinePerFrame + " fragments looked at a frame at most, one attempt at a time", this);
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
