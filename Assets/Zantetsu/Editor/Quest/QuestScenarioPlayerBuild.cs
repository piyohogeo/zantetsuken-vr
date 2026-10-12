using UnityEditor;
using UnityEditor.Build;
using Zantetsu.EditorTools.Sandbox;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The Quest APK of every scenario (DESIGN 3.4): the boot scene first, then the city walk's scene (which play also
    /// runs in), the static scene and the ten-NPC scene, each as its own Configure step made it. The launch's
    /// <c>scenario</c> in <c>quest3s-config.json</c> chooses which is loaded.
    /// <para>
    /// For commercial scenes it configures nothing; the Bunny demo configures the shared Android/XR settings: the three scenes, their profiles, their materials and the files under StreamingAssets
    /// are the ones the three packages' own Configure steps left, so each scenario runs in the scene it has always
    /// run in. The three packages of one scene each go on being built by their own entries.
    /// </para>
    /// </summary>
    public static class QuestScenarioPlayerBuild
    {
        public const string ApplicationIdentifier = "com.junic.zantetsu.quest";

        public static void BuildApk()
        {
            var scenes = DemoBuildPolicy.SelectScenes(new[] { QuestCityWalkBuild.ScenePath, QuestStaticDemoBuild.ScenePath, QuestNpcSlashBuild.ScenePath }, out bool demo);
            if (demo)
            {
                QuestStaticDemoBuild.ConfigureCommon(new System.Collections.Generic.List<string>());
                UnityEditor.AssetDatabase.SaveAssets();
            }
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, demo ? ApplicationIdentifier + ".bunnydemo" : ApplicationIdentifier);
            using (DemoBuildPolicy.Scope(demo, scenes)) QuestStaticDemoBuild.BuildApkForScenes(scenes);
        }
    }
}
