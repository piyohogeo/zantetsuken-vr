using System;
using System.Globalization;
using Unity.Profiling;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The convex trim of a large fragment whose convexes reach beyond its drawn mesh (<see cref="CutConvexTrim"/>,
    /// 2026-11-20): made with the world, given one turn a frame after the additional split's, and let go with the world.
    /// **Off by default** -- an evaluation's switch; <see cref="ConvexTrimArgument"/> turns it on for a run.
    /// </summary>
    public partial class CutWorldRoot
    {
        private static readonly ProfilerMarker s_convexTrimMarker = new ProfilerMarker("Zantetsu.ConvexTrim.Step");

        /// <summary>The convex trim of this world; off unless asked for.</summary>
        public CutConvexTrim ConvexTrim { get; private set; }

        /// <summary>The Player argument that switches the convex trim on for a run (off by default).</summary>
        public const string ConvexTrimArgument = "-zantetsuConvexTrim";

        /// <summary>The Player argument that switches the convex trim off for a run: the comparison's control once the default changes. Given with <see cref="ConvexTrimArgument"/>, off wins.</summary>
        public const string ConvexTrimOffArgument = "-zantetsuConvexTrimOff";

        /// <summary>The Player arguments that give the two provisional lengths for a run, each followed by metres: the size above which a body is looked at (0.2), and the reach beyond the mesh a convex is trimmed for (0.1).</summary>
        public const string ConvexTrimSizeArgument = "-zantetsuConvexTrimSize";
        public const string ConvexTrimOverhangArgument = "-zantetsuConvexTrimOverhang";

        /// <summary>
        /// The settings a launch asks for: the defaults, with the switch and the two lengths the arguments give. A length that
        /// cannot be read (not a number above 0 and at most 100) leaves the default and is said in <paramref name="refusal"/>.
        /// </summary>
        internal static CutConvexTrimSettings ReadConvexTrim(string[] arguments, out string asked, out string refusal)
        {
            CutConvexTrimSettings settings = CutConvexTrimSettings.Default;
            refusal = null;
            bool on = arguments != null && Array.IndexOf(arguments, ConvexTrimArgument) >= 0;
            bool off = arguments != null && Array.IndexOf(arguments, ConvexTrimOffArgument) >= 0;
            settings.enabled = on && !off;
            asked = off ? "off, asked for by " + ConvexTrimOffArgument + (on ? " (given with " + ConvexTrimArgument + ": off wins)" : "")
                : on ? "on, asked for by " + ConvexTrimArgument
                : "off -- the default; " + ConvexTrimArgument + " turns it on";
            if (TryReadLength(arguments, ConvexTrimSizeArgument, out float size, ref refusal)) settings.minSizeMeters = size;
            if (TryReadLength(arguments, ConvexTrimOverhangArgument, out float overhang, ref refusal)) settings.minOverhangMeters = overhang;
            return settings;
        }

        private static bool TryReadLength(string[] arguments, string name, out float metres, ref string refusal)
        {
            metres = 0f;
            int at = arguments != null ? Array.IndexOf(arguments, name) : -1;
            if (at < 0) return false;
            if (at + 1 < arguments.Length
                && float.TryParse(arguments[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out metres) && metres > 0f && metres <= 100f)
            {
                return true;
            }

            refusal = (refusal != null ? refusal + "; " : "") + name + " is followed by "
                + (at + 1 < arguments.Length ? "'" + arguments[at + 1] + "'" : "nothing") + ", not by metres above 0 and at most 100";
            metres = 0f;
            return false;
        }

        internal static string DescribeConvexTrim(string asked, in CutConvexTrimSettings settings)
        {
            return "CONVEX TRIM: " + asked + "; a fragment's body with two or more extents above "
                + settings.minSizeMeters.ToString("R", CultureInfo.InvariantCulture) + " m has each convex that reaches "
                + settings.minOverhangMeters.ToString("R", CultureInfo.InvariantCulture)
                + " m or more beyond its drawn mesh (the mesh's support planes in 26 directions) cut off there, after the additional split has nothing to do; not for buildings or NPC pieces";
        }

        private void MakeConvexTrim()
        {
            CutConvexTrimSettings settings = ReadConvexTrim(Environment.GetCommandLineArgs(), out string asked, out string refusal);
            ConvexTrim = new CutConvexTrim(this, settings);
            if (refusal != null) UnityEngine.Debug.LogError("CONVEX TRIM: " + refusal + ": the default stands", this);
            // Off by the default: no line (a run's log says nothing of a feature that is not there); on, or an argument given: one line.
            if (settings.enabled || !asked.StartsWith("off -- the default", StringComparison.Ordinal)) UnityEngine.Debug.Log(DescribeConvexTrim(asked, in settings), this);
        }

        /// <summary>
        /// Switches the convex trim on for this world, every other value as it is: what <see cref="ConvexTrimArgument"/>
        /// does, for a host that has no command line and for a test. False when the world has none.
        /// </summary>
        public bool EnableConvexTrim(string askedBy)
        {
            if (ConvexTrim == null) return false;
            CutConvexTrimSettings settings = ConvexTrim.Settings;
            settings.enabled = true;
            ConvexTrim.Settings = settings;
            UnityEngine.Debug.Log(DescribeConvexTrim("on, asked for by " + askedBy, in settings), this);
            return true;
        }

        // In LateUpdate, right after the additional split's turn: the trim reads what that turn ended with. Off, and with
        // no attempt outstanding: nothing at all.
        private void StepConvexTrim()
        {
            if (ConvexTrim != null && (ConvexTrim.Settings.enabled || ConvexTrim.HasAttempt))
            {
                using (s_convexTrimMarker.Auto())
                {
                    ConvexTrim.Step();
                }
            }
        }

        private void ReleaseConvexTrim()
        {
            ConvexTrim?.Release();
            ConvexTrim = null;
        }
    }
}
