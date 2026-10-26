using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>Builds only a separate, synthetic loopback check; it cannot overwrite the distributed game.</summary>
    public static class AndroidFlatStatisticsRecoveryBuild
    {
        public static void Build()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-statisticsRecoveryOut");
            if (at < 0 || at + 1 >= args.Length) throw new BuildFailedException("Specify -statisticsRecoveryOut");
            string output = Path.GetFullPath(args[at + 1]);
            if (File.Exists(output)) throw new BuildFailedException("The probe APK already exists");
            string scenePath = AssetDatabase.GenerateUniqueAssetPath(AndroidFlatBuild.PrivateRoot + "/StatisticsRecoveryProbe.unity");
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Camera", typeof(Camera));
                EditorSceneManager.SaveScene(scene, scenePath);
                using (new AndroidFlatBuild.BuildSettingsScope())
                {
                    PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.zantetsuken.statisticsrecoveryprobe");
                    PlayerSettings.productName = "Statistics recovery check";
                    PlayerSettings.bundleVersion = "1";
                    PlayerSettings.Android.bundleVersionCode = 1;
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // Only synthetic loopback in this separate package.
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                        scenes = new[] { scenePath }, target = BuildTarget.Android, locationPathName = output,
                        extraScriptingDefines = new[] { "ZANTETSU_STATS_RECOVERY_PROBE" }, options = BuildOptions.Development
                    });
                    if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Statistics recovery probe build failed");
                }
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(scenePath);
            }
        }
    }
}
