#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The offline replay of a split capture session in a Development Player (DESIGN 7.9.8), for the Player's own
    /// arithmetic (IL2CPP, Burst as the Player has it) beside the Editor's: <c>-zantetsuSplitReplay &lt;session folder&gt;
    /// -zantetsuSplitReplayOut &lt;a new folder&gt;</c>. It runs before the first scene, starts no scenario, replays every
    /// sample with the same runner the Editor's batch uses (<see cref="SpatialSplitReplay.RunSession"/>), and quits with
    /// the same exit codes: 0 every sample compared and the same, 3 not, 4 no sample, 1 could not run -- a request whose
    /// folders are missing or not absolute among them: once either argument is named, the launch is a replay and never
    /// goes on to the game. Without either argument nothing happens. The Player's XR initialisation is not this one's:
    /// it is run with no XR session by its launch script (perf\split-capture\Run-PlayerReplay.ps1 points the OpenXR
    /// runtime at a file that does not exist). The Editor never runs it (its replay is SpatialSplitReplayBatch).
    /// </summary>
    internal static class SpatialSplitReplayPlayer
    {
        public const string Argument = "-zantetsuSplitReplay";
        public const string OutArgument = "-zantetsuSplitReplayOut";

        /// <summary>The replay's options (GRAPH-CONNECT-6 §2): the input's shape (compact, the default, or span), the graph stage (span only), the tuning list (an absolute file).</summary>
        public const string InputArgument = "-zantetsuSplitReplayInput";
        public const string GraphArgument = "-zantetsuSplitReplayGraph";
        public const string ListArgument = "-zantetsuSplitReplayList";
        /// <summary>GRAPH-CONNECT-8: saved (the default) or off -- the bent partition left out, only with the graph on the span input.</summary>
        public const string BentArgument = "-zantetsuSplitReplayBent";
        /// <summary>GRAPH-CONNECT-10: current (the default) or graph-first -- the graph stage before the bent partition, the bent partition on an unfinished graph; only with the graph on the span input, not with bent off.</summary>
        public const string OrderArgument = "-zantetsuSplitReplayOrder";

        /// <summary>Reads the options (<see cref="SpatialSplitReplayOptions.TryRead"/> with this entry's argument names).</summary>
        internal static bool TryReadOptions(string[] arguments, out SpatialSplitReplayOptions options, out string refusal) =>
            SpatialSplitReplayOptions.TryRead(arguments, InputArgument, GraphArgument, ListArgument, BentArgument, OrderArgument, out options, out refusal);

        /// <summary>
        /// Whether the launch asks for a replay at all (either argument named), and -- when it does -- its two folders,
        /// or why the request cannot be run (a value missing, another switch in its place, a folder not absolute).
        /// </summary>
        internal static bool TryReadRequest(string[] arguments, out string session, out string output, out string refusal)
        {
            session = output = refusal = null;
            arguments ??= Array.Empty<string>();
            int sessionAt = Array.FindIndex(arguments, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
            int outAt = Array.FindIndex(arguments, a => string.Equals(a, OutArgument, StringComparison.OrdinalIgnoreCase));
            if (sessionAt < 0 && outAt < 0)
            {
                return false;
            }

            session = ValueAt(arguments, sessionAt);
            output = ValueAt(arguments, outAt);
            if (sessionAt < 0) refusal = Argument + " is not given (" + OutArgument + " is)";
            else if (session == null) refusal = Argument + " has no value";
            else if (outAt < 0) refusal = OutArgument + " is not given";
            else if (output == null) refusal = OutArgument + " has no value";
            else if (!Path.IsPathRooted(session) || !Path.IsPathRooted(output)) refusal = Argument + " and " + OutArgument + " take absolute folders";
            return true;
        }

        private static string ValueAt(string[] arguments, int at)
        {
            return at >= 0 && at + 1 < arguments.Length && !arguments[at + 1].StartsWith("-", StringComparison.Ordinal) ? arguments[at + 1] : null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RunFromLaunch()
        {
            if (Application.isEditor || !TryReadRequest(Environment.GetCommandLineArgs(), out string session, out string output, out string refusal))
            {
                return;
            }

            // A replay was asked for: whatever follows, nothing of a scenario is started.
            SandboxScenario.HoldForDevelopmentReplay();
            int code = 1;
            if (refusal != null)
            {
                Debug.LogError("SPLIT REPLAY (Player) refused: " + refusal + "; the Player ends without starting the game");
            }
            else if (!TryReadOptions(Environment.GetCommandLineArgs(), out SpatialSplitReplayOptions options, out string optionsRefusal))
            {
                Debug.LogError("SPLIT REPLAY (Player) refused: " + optionsRefusal + "; the Player ends without starting the game");
            }
            else
            {
                try
                {
                    string summary = SpatialSplitReplay.RunSession(session, output, options, out bool allSame, out bool noSample);
                    Debug.Log("SPLIT REPLAY (Player):\n" + summary);
                    code = allSame ? 0 : noSample ? 4 : 3;
                }
                catch (Exception e)
                {
                    Debug.LogError("SPLIT REPLAY (Player) could not run: " + e);
                }
            }

            Debug.Log("SPLIT REPLAY (Player): quitting with " + code);
            Application.Quit(code);
        }
    }
}
#endif
