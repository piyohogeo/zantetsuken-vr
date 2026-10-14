using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The Windows Player of every scenario (DESIGN 3.4): the boot scene first, which holds nothing, then the city
    /// (play, static-wait, city-walk) and the ten-NPC scene (npc-slash). The launch's scenario chooses which of them
    /// is loaded; nothing of a world is made before that choice.
    /// <para>
    /// The city scenes are the licensed ones of this working copy and are not in the repository: this entry names
    /// them in commercial mode. With no private data, auto mode builds the noncommercial Bunny scene. The Players of one scene (each scene's own build entry) go on
    /// working as they did: such a Player runs the launch's scenario in its one scene.
    /// </para>
    /// </summary>
    public static class ScenarioPlayerBuild
    {
        public const string BootScenePath = "Assets/Scenes/" + SandboxScenario.BootSceneName + ".unity";
        public const string CityScenePath = "Assets/Licensed/WalkCity/WalkCity.unity";
        public const string NpcScenePath = "Assets/Licensed/MultiNpc/MultiNpcCity.unity";

        private const string OutArgument = "-zantetsuPlayerOut";

        /// <summary>With this argument the Player is a Release (non-Development) build: the distribution kind, for the check of DESIGN 21.18.</summary>
        private const string ReleaseArgument = "-zantetsuPlayerRelease";

        /// <summary>
        /// With this argument the Player is the VERIFICATION build of the play statistics (DESIGN 21.18): a Release build
        /// with the symbol below, which no product build defines. It measures the statistics' own cost, takes the consent
        /// from the launch's argument and can send nothing; it is never a build to distribute.
        /// </summary>
        private const string StatsVerifyArgument = "-zantetsuPlayerStatsVerify";

        public const string StatsVerifySymbol = "ZANTETSU_STATS_VERIFY";

        /// <summary>The boot scene, made if it is not there: an empty scene, saved under its fixed name.</summary>
        [MenuItem("Zantetsu/Make the scenario boot scene")]
        public static string EnsureBootScene()
        {
            if (File.Exists(BootScenePath))
            {
                return BootScenePath;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, BootScenePath))
            {
                throw new IOException("the boot scene could not be saved at " + BootScenePath);
            }

            AssetDatabase.ImportAsset(BootScenePath);
            Debug.Log("SCENARIO PLAYER BUILD: made " + BootScenePath);
            return BootScenePath;
        }

        /// <summary>For <c>-executeMethod</c>: makes the boot scene and ends the Editor.</summary>
        public static void EnsureBootSceneFromCommandLine()
        {
            bool made = false;
            try
            {
                EnsureBootScene();
                made = File.Exists(BootScenePath);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            EditorApplication.Exit(made ? 0 : 1);
        }

        /// <summary>
        /// For <c>-executeMethod</c>, with <c>-zantetsuPlayerOut &lt;directory&gt;</c>. The frame timing statistics are
        /// switched on for this build and put back afterwards, as the city's own measurement build does.
        /// </summary>
        public static void BuildPlayerFromCommandLine()
        {
            string directory = null;
            bool development = true;
            bool statsVerify = false;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i < arguments.Length - 1 && string.Equals(arguments[i], OutArgument, StringComparison.OrdinalIgnoreCase))
                {
                    directory = arguments[i + 1];
                }

                if (string.Equals(arguments[i], ReleaseArgument, StringComparison.OrdinalIgnoreCase))
                {
                    development = false;
                }

                if (string.Equals(arguments[i], StatsVerifyArgument, StringComparison.OrdinalIgnoreCase))
                {
                    statsVerify = true;
                    development = false;   // what it measures is a Release Player's cost
                }
            }

            bool before = PlayerSettings.enableFrameTimingStats;
            bool built = false;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                Debug.Log("SCENARIO PLAYER BUILD: enableFrameTimingStats " + before + " -> true for this build; kind "
                    + (development ? "Development" : statsVerify ? "Release with " + StatsVerifySymbol + " (" + StatsVerifyArgument + "): the statistics' VERIFICATION build, not for distribution" : "Release (" + ReleaseArgument + ")"));
                var scenes = DemoBuildPolicy.SelectScenes(new[] { CityScenePath, NpcScenePath }, out bool demo);
                using (DemoBuildPolicy.Scope(demo, scenes))
                    built = !string.IsNullOrEmpty(directory) && CutWorldSandboxPlayerBuild.Build(directory, scenes, statsVerify ? new[] { StatsVerifySymbol } : null, development);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = before;
                AssetDatabase.SaveAssets();
                Debug.Log("SCENARIO PLAYER BUILD: enableFrameTimingStats back to " + PlayerSettings.enableFrameTimingStats);
            }

            EditorApplication.Exit(built ? 0 : 1);
        }
    }
}
