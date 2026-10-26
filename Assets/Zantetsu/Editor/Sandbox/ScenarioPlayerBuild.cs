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

        /// <summary>
        /// The verification build as a DEVELOPMENT build: for the allocation check alone, since the engine's record of
        /// managed allocations exists in a Development Player and not in a Release one. Its times are not a Release's.
        /// </summary>
        private const string StatsVerifyDevelopmentArgument = "-zantetsuPlayerStatsVerifyDevelopment";

        public const string StatsVerifySymbol = "ZANTETSU_STATS_VERIFY";

        /// <summary>
        /// With this argument the Player's graphics APIs are Direct3D 12 first, then Direct3D 11 (2026-10-27): it starts on
        /// Direct3D 12, where a Windows Player draws the cut bodies through the native plugin by default (VP3C-NATIVE, D-215),
        /// and falls back to Direct3D 11 (Unity's calls) where Direct3D 12 cannot start. For this build only: the project's
        /// own setting (Direct3D 11 alone, 0ea76d04) is put back afterwards.
        /// </summary>
        public const string Direct3D12Argument = "-zantetsuPlayerD3D12";

        /// <summary>The graphics APIs of <see cref="Direct3D12Argument"/>, in order.</summary>
        public static readonly UnityEngine.Rendering.GraphicsDeviceType[] Direct3D12Apis =
            { UnityEngine.Rendering.GraphicsDeviceType.Direct3D12, UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 };

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
            bool statsVerify = false, statsVerifyDevelopment = false;
            bool direct3D12 = false;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], Direct3D12Argument, StringComparison.OrdinalIgnoreCase))
                {
                    direct3D12 = true;
                }

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

                if (string.Equals(arguments[i], StatsVerifyDevelopmentArgument, StringComparison.OrdinalIgnoreCase))
                {
                    statsVerify = true;
                    statsVerifyDevelopment = true;
                }
            }

            bool before = PlayerSettings.enableFrameTimingStats;
            const BuildTarget windows = BuildTarget.StandaloneWindows64;
            bool apisAutomaticBefore = PlayerSettings.GetUseDefaultGraphicsAPIs(windows);
            UnityEngine.Rendering.GraphicsDeviceType[] apisBefore = PlayerSettings.GetGraphicsAPIs(windows);
            bool built = false;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                if (direct3D12)
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(windows, false);
                    PlayerSettings.SetGraphicsAPIs(windows, Direct3D12Apis);
                    Debug.Log("SCENARIO PLAYER BUILD: graphics APIs " + string.Join(",", apisBefore) + " (automatic " + apisAutomaticBefore + ") -> "
                        + string.Join(",", PlayerSettings.GetGraphicsAPIs(windows)) + " for this build (" + Direct3D12Argument + ")");
                }

                if (statsVerifyDevelopment) development = true;   // whatever the order of the arguments
                Debug.Log("SCENARIO PLAYER BUILD: enableFrameTimingStats " + before + " -> true for this build; kind "
                    + (statsVerifyDevelopment ? "Development with " + StatsVerifySymbol + " (" + StatsVerifyDevelopmentArgument + "): the statistics' VERIFICATION build for the allocation check, not for distribution"
                        : development ? "Development" : statsVerify ? "Release with " + StatsVerifySymbol + " (" + StatsVerifyArgument + "): the statistics' VERIFICATION build, not for distribution" : "Release (" + ReleaseArgument + ")"));
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
                if (direct3D12)
                {
                    PlayerSettings.SetGraphicsAPIs(windows, apisBefore);
                    PlayerSettings.SetUseDefaultGraphicsAPIs(windows, apisAutomaticBefore);
                }
                AssetDatabase.SaveAssets();
                Debug.Log("SCENARIO PLAYER BUILD: enableFrameTimingStats back to " + PlayerSettings.enableFrameTimingStats
                    + (direct3D12 ? "; graphics APIs back to " + string.Join(",", PlayerSettings.GetGraphicsAPIs(windows)) + " (automatic " + PlayerSettings.GetUseDefaultGraphicsAPIs(windows) + ")" : ""));
            }

            EditorApplication.Exit(built ? 0 : 1);
        }
    }
}
