using System;

namespace Zantetsu.Core
{
    /// <summary>
    /// DIAGNOSIS ONLY (unit 8-C / 22, 2026-10-17): a launch condition the whole Player reads before anything is made.
    /// <see cref="EmptyXrScene"/>: the Quest Player starts in the empty XR scene (scenario "empty-xr") -- the project's
    /// XR and URP configuration with nothing of the game: no world, no runner, no menu, no statistics, no physics loop.
    /// The game's session installers (CutPhysicsStep's player-loop systems, SandboxScenario's menu) read it and stand
    /// down; what still runs is said in the log by the scene's own entry. Set once by the Quest entry at
    /// SubsystemRegistration from the launch's configuration; never a product option.
    /// </summary>
    public static class LaunchDiagnosis
    {
        /// <summary>The Player runs the empty XR scene diagnosis.</summary>
        public static bool EmptyXrScene { get; private set; }

        /// <summary>Why it was marked, for the log.</summary>
        public static string Reason { get; private set; } = "none";

        /// <summary>What was stood down for it, one entry each, for the log.</summary>
        public static string StoodDown { get; private set; } = "";

        public static void MarkEmptyXrScene(string reason)
        {
            EmptyXrScene = true;
            Reason = reason ?? "the launch's configuration";
        }

        /// <summary>A session installer says it stood down for the diagnosis.</summary>
        public static void NoteStoodDown(string what)
        {
            if (string.IsNullOrEmpty(what)) return;
            StoodDown = StoodDown.Length == 0 ? what : StoodDown + "; " + what;
        }

        /// <summary>For tests and a new session.</summary>
        public static void Reset()
        {
            EmptyXrScene = false;
            Reason = "none";
            StoodDown = "";
        }

        public static string Describe()
        {
            return "emptyXrScene=" + EmptyXrScene + " (" + Reason + "); stood down: " + (StoodDown.Length == 0 ? "nothing" : StoodDown);
        }
    }
}
