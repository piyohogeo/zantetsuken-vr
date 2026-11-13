using System;
using System.Globalization;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The additional split's settings of a launch (2026-11-13, the defaults of SPLIT-CANDIDATES-2 and -3), read once a play
    /// session before anything is made -- any scene, any model or geometry (the topology component counts are taken where those
    /// are made), any world's Background pool (its accept capacity follows the attempts) -- in every build, Release too.
    /// <para>
    /// **The defaults**, on the PC and on the Quest alike: up to <see cref="DefaultAttempts"/> attempts out at once (with the
    /// limits of the evaluation: <see cref="CutSpatialSplit.EvaluationMaxWaitingMain"/> waiting for the main thread,
    /// <see cref="CutSpatialSplit.EvaluationMaxHeldBytes"/> held), the topology component counts taken and kept, and a geometry
    /// known to be one component passed over before an attempt (a count that is unknown passes nothing over).
    /// </para>
    /// <para>
    /// **The arguments.** <c>-zantetsuSplitAttempts n</c> (1..64; 1 is the control as before: one attempt at a time).
    /// <c>-zantetsuSplitOneComponent off|record|skip</c>: off takes no count; record takes and keeps the counts and passes
    /// nothing over; skip (the default) also passes over a geometry known to be one component. The launch as it was before
    /// these defaults: <c>-zantetsuSplitAttempts 1 -zantetsuSplitOneComponent off</c>. A value that cannot be read is told as
    /// an error and the default is kept.
    /// </para>
    /// </summary>
    public static class CutSpatialSplitLaunch
    {
        public const int DefaultAttempts = 8;
        public const string AttemptsArgument = "-zantetsuSplitAttempts";
        public const string OneComponentArgument = "-zantetsuSplitOneComponent";

        private static bool s_read;

        /// <summary>What the last reading came to, in words (the log's line).</summary>
        public static string Effective { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ReadForSession()
        {
            Apply(Environment.GetCommandLineArgs());
            Debug.Log("SPLIT SETTINGS: " + Effective);
        }

        /// <summary>Reads the launch once where no play session did (an Editor that makes a world outside play): before a world's pools.</summary>
        public static void EnsureRead()
        {
            if (s_read) return;
            Apply(Environment.GetCommandLineArgs());
        }

        /// <summary>
        /// Sets the three settings from <paramref name="arguments"/> -- the defaults first, then what the arguments say -- and
        /// returns the errors (none: empty). Tests give their own arguments.
        /// </summary>
        public static string Apply(string[] arguments)
        {
            s_read = true;
            int attempts = DefaultAttempts;
            string mode = "skip";
            string errors = "";
            arguments = arguments ?? Array.Empty<string>();
            int at = Array.FindIndex(arguments, a => string.Equals(a, AttemptsArgument, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                if (at + 1 < arguments.Length && int.TryParse(arguments[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= 64) attempts = n;
                else errors += AttemptsArgument + " takes 1..64; the default " + DefaultAttempts + " is kept. ";
            }

            at = Array.FindIndex(arguments, a => string.Equals(a, OneComponentArgument, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                string given = at + 1 < arguments.Length ? arguments[at + 1] : "";
                if (given == "off" || given == "record" || given == "skip") mode = given;
                else errors += OneComponentArgument + " takes off, record or skip; the default skip is kept. ";
            }

            CutSpatialSplit.DefaultMaxAttempts = attempts;
            VpTopologyComponents.Enabled = mode != "off";
            CutSpatialSplit.SkipOneComponent = mode == "skip";
            Effective = "up to " + attempts + " attempts out at once" + (attempts == 1 ? " (the control as before)" : "")
                + ", waiting for Main at most " + CutSpatialSplit.EvaluationMaxWaitingMain + ", held at most " + CutSpatialSplit.EvaluationMaxHeldBytes + " B"
                + "; topology component counts " + (mode == "off" ? "off" : "taken and kept")
                + (mode == "skip" ? ", a geometry known to be one component passed over before an attempt" : ", nothing passed over")
                + " (" + AttemptsArgument + " " + attempts + " " + OneComponentArgument + " " + mode + ")";
            if (errors.Length != 0) Debug.LogError("SPLIT SETTINGS: " + errors.Trim());
            return errors.Trim();
        }
    }
}
