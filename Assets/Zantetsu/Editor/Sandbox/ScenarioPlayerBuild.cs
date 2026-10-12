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
    /// them and refuses to build where one is missing. The Players of one scene (each scene's own build entry) go on
    /// working as they did: such a Player runs the launch's scenario in its one scene.
    /// </para>
    /// </summary>
    public static class ScenarioPlayerBuild
    {
        public const string BootScenePath = "Assets/Scenes/" + SandboxScenario.BootSceneName + ".unity";
        public const string CityScenePath = "Assets/Licensed/WalkCity/WalkCity.unity";
        public const string NpcScenePath = "Assets/Licensed/MultiNpc/MultiNpcCity.unity";

        private const string OutArgument = "-zantetsuPlayerOut";

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
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], OutArgument, StringComparison.OrdinalIgnoreCase))
                {
                    directory = arguments[i + 1];
                }
            }

            bool before = PlayerSettings.enableFrameTimingStats;
            bool built = false;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                Debug.Log("SCENARIO PLAYER BUILD: enableFrameTimingStats " + before + " -> true for this build");
                built = !string.IsNullOrEmpty(directory)
                    && CutWorldSandboxPlayerBuild.Build(directory, new[] { EnsureBootScene(), CityScenePath, NpcScenePath });
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
