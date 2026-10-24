using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>
    /// The offline replay of a capture session of the additional split's inputs (DESIGN 7.9.8), in the Editor:
    /// <c>-executeMethod Zantetsu.Editor.Sandbox.SpatialSplitReplayBatch.Run -splitReplaySession &lt;session folder&gt;
    /// -splitReplayOut &lt;a new folder&gt;</c>. Exit code 0 where every sample was read, replayed the same as it ran and
    /// passed the checks (one sample at least); 3 where any was not (replay.jsonl says which and how); 4 where the folder
    /// holds no sample at all; 1 where the replay could not run at all.
    /// The session's folder is only read.
    /// </summary>
    public static class SpatialSplitReplayBatch
    {
        public static void Run()
        {
            int code = 1;
            try
            {
                string session = Argument("-splitReplaySession");
                string output = Argument("-splitReplayOut");
                if (session == null || output == null || !Path.IsPathRooted(session) || !Path.IsPathRooted(output))
                {
                    throw new ArgumentException("-splitReplaySession and -splitReplayOut take absolute folders");
                }

                // the options (GRAPH-CONNECT-6 §2), read as the Player reads its own: a named argument without its value, an unknown
                // value, a relative list or the graph without the span input is refused before anything runs (never "no list")
                if (!SpatialSplitReplayOptions.TryRead(Environment.GetCommandLineArgs(), "-splitReplayInput", "-splitReplayGraph", "-splitReplayList", "-splitReplayBent", "-splitReplayOrder", "-splitReplayPool", "-splitReplayCompetitorWork", out SpatialSplitReplayOptions options, out string refusal))
                {
                    throw new ArgumentException(refusal);
                }

                if (Array.FindIndex(Environment.GetCommandLineArgs(), a => string.Equals(a, "-splitTimelineOff", StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    // GRAPH-CONNECT-14 §7: the timeline's new clock readings off for this replay (the existing clocks stand)
                    CutSpatialSplit.TimelineObserve = false;
                    Zantetsu.MeshCut.DispatchObservation.Enabled = false;
                }
                string summary = SpatialSplitReplay.RunSession(session, output, options, out bool allSame, out bool noSample);
                Debug.Log("SPLIT REPLAY:\n" + summary);
                code = allSame ? 0 : noSample ? 4 : 3;
            }
            catch (Exception e)
            {
                Debug.LogError("SPLIT REPLAY could not run: " + e);
            }

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        private static string Argument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }
    }
}
