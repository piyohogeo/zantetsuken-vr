#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// NPC-BONE-EVAL-1: the comparison of the NPC fragments' two additional splits (<see cref="SpatialSplitMethodEval"/>) in a
    /// Development Player: <c>-zantetsuSplitMethodEval &lt;capture session folder&gt; -zantetsuSplitMethodEvalList &lt;the fixed list&gt;
    /// -zantetsuSplitMethodEvalOut &lt;a new folder&gt; [-zantetsuSplitMethodEvalRepeats N]</c>. Like the split replay's entry it runs
    /// before the first scene, starts no scenario and quits with the comparison's code (0 / 3 / 1); without the argument nothing
    /// happens. <c>-zantetsuSplitTimelineOff</c> and <c>-zantetsuDispatchObserveOff</c> keep the timeline's and the work log's
    /// readings out of the measured runs, as for the replay.
    /// </summary>
    internal static class SpatialSplitMethodEvalPlayer
    {
        public const string Argument = "-zantetsuSplitMethodEval";
        public const string ListArgument = "-zantetsuSplitMethodEvalList";
        public const string OutArgument = "-zantetsuSplitMethodEvalOut";
        public const string RepeatsArgument = "-zantetsuSplitMethodEvalRepeats";

        /// <summary>NPC-BONE-FALLBACK-1: a third method, the bones then the method as before where they say NotSeparable.</summary>
        public const string BoneFallbackArgument = "-zantetsuSplitMethodEvalBoneFallback";

        private static string ValueOf(string[] arguments, string name)
        {
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length && !arguments[at + 1].StartsWith("-", StringComparison.Ordinal) ? arguments[at + 1] : null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RunFromLaunch()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Application.isEditor || Array.FindIndex(arguments, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                return;
            }

            SandboxScenario.HoldForDevelopmentReplay();
            if (Array.IndexOf(arguments, CutWorldRoot.SplitTimelineOffArgument) >= 0)
            {
                CutSpatialSplit.TimelineObserve = false;
                Zantetsu.MeshCut.DispatchObservation.Enabled = false;
            }

            DispatchRecord.ReadArguments(arguments);
            string session = ValueOf(arguments, Argument), list = ValueOf(arguments, ListArgument), output = ValueOf(arguments, OutArgument), repeatsText = ValueOf(arguments, RepeatsArgument);
            int repeats = SpatialSplitMethodEval.DefaultRepeats;
            int code = 1;
            if (session == null || list == null || output == null || !Path.IsPathRooted(session) || !Path.IsPathRooted(list) || !Path.IsPathRooted(output))
            {
                Debug.LogError("SPLIT METHOD EVAL refused: " + Argument + ", " + ListArgument + " and " + OutArgument + " take absolute paths");
            }
            else if (repeatsText != null && (!int.TryParse(repeatsText, NumberStyles.None, CultureInfo.InvariantCulture, out repeats) || repeats < 1 || repeats > 20))
            {
                Debug.LogError("SPLIT METHOD EVAL refused: " + RepeatsArgument + " takes 1..20, not '" + repeatsText + "'");
            }
            else
            {
                try
                {
                    bool boneFallback = Array.FindIndex(arguments, a => string.Equals(a, BoneFallbackArgument, StringComparison.OrdinalIgnoreCase)) >= 0;
                    string summary = SpatialSplitMethodEval.RunList(session, list, output, repeats, out code, boneFallback);
                    Debug.Log("SPLIT METHOD EVAL:\n" + summary);
                }
                catch (Exception e)
                {
                    code = 1;
                    Debug.LogError("SPLIT METHOD EVAL could not run: " + e);
                }
            }

            Debug.Log("SPLIT METHOD EVAL: quitting with " + code);
            Application.Quit(code);
        }
    }
}
#endif
