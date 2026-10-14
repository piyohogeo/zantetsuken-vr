namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// What of the environment's names is fit to be recorded (DESIGN 21.18): a name that says nothing of the device --
    /// the engine's own generic name for "the tracked head" -- is "not obtained" (null), not a device's name, and
    /// nothing is guessed in its place from the runtime's name (neither a headset's model nor how it is connected).
    /// </summary>
    public static class PlayEnvironment
    {
        /// <summary>The XR head device's name as it may be recorded: null where it is empty or one of the engine's generic names.</summary>
        public static string XrDeviceName(string reported)
        {
            if (string.IsNullOrWhiteSpace(reported))
            {
                return null;
            }

            string name = reported.Trim();
            // The OpenXR plug-in names its devices by what they track ("Head Tracking - OpenXR"), whatever the headset is.
            if (name.StartsWith("Head Tracking", System.StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("- OpenXR", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "none", System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }

        /// <summary>
        /// The display's refresh rate as a CONDITION of play: whole hertz, 0 where there is none. A runtime may report
        /// the rate as a measured number -- a Quest gave 45, 45.00002, 44.99998, 44.99996 and 45.00004 within a minute
        /// -- and conditions compared by that number changed every few frames: each change is a boundary, so hardly a
        /// frame was counted and no frame time was ever attributed. The rates a display runs at, and their halves under
        /// frame synthesis, are whole numbers; the jitter of a report is not a change of conditions.
        /// </summary>
        public static float RefreshRateHz(float reported)
        {
            if (float.IsNaN(reported) || float.IsInfinity(reported) || reported <= 0f)
            {
                return 0f;
            }

            return (float)System.Math.Round(reported, System.MidpointRounding.AwayFromZero);
        }

        /// <summary>A runtime's name or version as it may be recorded: null where empty.</summary>
        public static string OrNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>What stands for "no XR" in the runtime's place: a state, not a name that was not obtained.</summary>
        public const string NoXr = "none";

        /// <summary>
        /// The XR runtime's name as it may be recorded: <see cref="NoXr"/> without XR; with XR the runtime's own name as
        /// the runtime gave it, or null where it gave none -- never the engine's name for its loader or display in its
        /// place, so that "not obtained" and a name obtained are told apart.
        /// </summary>
        public static string XrRuntimeName(bool xrActive, string runtimeOwnName)
        {
            return xrActive ? OrNull(runtimeOwnName) : NoXr;
        }
    }
}
