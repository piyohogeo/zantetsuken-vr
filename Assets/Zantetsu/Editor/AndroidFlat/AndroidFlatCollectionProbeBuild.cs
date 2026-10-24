using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>Separate non-Development package; does not replace the signed distribution or change its settings.</summary>
    public static class AndroidFlatCollectionProbeBuild
    {
        public static void Build()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-collectionProbeOut");
            if (at < 0 || at + 1 >= args.Length) throw new BuildFailedException("Specify -collectionProbeOut");
            string output = Path.GetFullPath(args[at + 1]);
            if (File.Exists(output)) throw new BuildFailedException("Probe APK already exists");
            if (!File.Exists(AndroidFlatBuild.ScenePath)) throw new BuildFailedException("Configure the phone city first");
            using (new AndroidFlatBuild.BuildSettingsScope())
            {
                PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.zantetsuken.collectionprobe");
                PlayerSettings.productName = "Collection measurement";
                PlayerSettings.bundleVersion = "1";
                PlayerSettings.Android.bundleVersionCode = 1;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { AndroidFlatBuild.ScenePath }, target = BuildTarget.Android, locationPathName = output,
                    extraScriptingDefines = new[] { AndroidFlatBuild.Define, "ZANTETSU_ANDROID_COLLECTION_PROBE" },
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Collection probe build failed");
            }
            Debug.Log("ANDROID COLLECTION PROBE built: separate package, Release IL2CPP, normal scene and collection settings");
        }
    }
}
