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
        public const string ApplicationIdentifier = "com.zantetsuken.quest";

        public static void BuildApk() => Build(true);

        private static string PackageSuffixFromCommandLine()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "-zantetsuQuestPackageSuffix") continue;
                string v = args[i + 1].Trim().ToLowerInvariant();
                foreach (char c in v) if (!(char.IsLetterOrDigit(c))) throw new System.ArgumentException("-zantetsuQuestPackageSuffix takes letters and digits only, not '" + args[i + 1] + "'");
                return v.Length > 0 ? v : null;
            }
            return null;
        }

        /// <summary>
        /// The build itself. In batch mode a failure exits the Editor at once when <paramref name="exitOnFailure"/>
        /// (the plain entry); the distribution build passes false, puts its settings back, and exits itself.
        /// </summary>
        public static void Build(bool exitOnFailure)
        {
            // The empty XR diagnosis scene (unit 22) rides along: it has no game content and is started only by the scenario "empty-xr".
            var scenes = DemoBuildPolicy.SelectScenes(new[] { QuestCityWalkBuild.ScenePath, QuestStaticDemoBuild.ScenePath, QuestNpcSlashBuild.ScenePath, QuestEmptyXrBuild.ScenePath }, out bool demo);
            if (demo)
            {
                QuestStaticDemoBuild.ConfigureCommon(new System.Collections.Generic.List<string>());
                UnityEditor.AssetDatabase.SaveAssets();
            }
            // A check build beside an installed release (SKIN-GPU-FIRST-STARTUP-1): -zantetsuQuestPackageSuffix <suffix> names the package
            // com.zantetsuken.quest.<suffix>, so that the device's play (another version, another key) stays untouched. Not for a distribution.
            string suffix = PackageSuffixFromCommandLine();
            string identifier = demo ? ApplicationIdentifier + ".bunnydemo" : suffix != null ? ApplicationIdentifier + "." + suffix : ApplicationIdentifier;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, identifier);
            if (suffix != null) UnityEngine.Debug.Log("Quest scenarios build: package " + identifier + " (check build, -zantetsuQuestPackageSuffix)");
            using (DemoBuildPolicy.Scope(demo, scenes)) QuestStaticDemoBuild.BuildApkForScenes(scenes, exitOnFailure);
        }
    }
}
