using System;
using UnityEditor;
using UnityEngine;
using Zantetsu.EditorTools.Sandbox;

namespace Zantetsu.Sandbox.Editor
{
    /// <summary>Windows IL2CPP diagnostic player, with normal rendering and XR initialization disabled only for this build.</summary>
    public static class MobPlanProfileBuild
    {
        public static void Run()
        {
            string directory = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-zantetsuPlayerOut") directory = args[i + 1];
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("-zantetsuPlayerOut is required");
            MobPlanDataVerification.Verify();
            var xr = UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if (xr == null) throw new InvalidOperationException("Standalone XR settings missing");
            bool previous = xr.InitManagerOnStart;
            bool built;
            try
            {
                xr.InitManagerOnStart = false;
                EditorUtility.SetDirty(xr);
                AssetDatabase.SaveAssets();
                Debug.Log("MOBPLAN PROFILE: mono rendering, XR init disabled for diagnostic build only");
                built = CutWorldSandboxPlayerBuild.Build(directory, MobPlanSceneBuild.ScenePath, new[] { "ZANTETSU_MOBPLAN_PROFILE" });
            }
            finally
            {
                xr.InitManagerOnStart = previous;
                EditorUtility.SetDirty(xr);
                AssetDatabase.SaveAssets();
            }
            EditorApplication.Exit(built ? 0 : 1);
        }
    }
}
