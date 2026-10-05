using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tools/XrSim harness Player build. Copied to Assets/_XrSimHarness/Editor/ by Build-XrSimHarnessPlayer.ps1 only for the
// build and removed afterwards; never part of the product. Creates Resources materials so the VP3 / VP3C shaders are in
// the build, then builds the Sandbox scene into an x64 IL2CPP Development Player at XRSIM_PLAYER_DIR.
public static class XrSimHarnessBuild
{
    private const string ResourcesDir = "Assets/_XrSimHarness/Resources";
    private static readonly string[] Categories = { "Character", "Vehicle", "Building", "Prop" };

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("XRSIM_PLAYER_DIR");
        if (string.IsNullOrEmpty(outDir))
        {
            Exit("XRSIM_PLAYER_DIR is not set");
            return;
        }

        ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
        if (backend != ScriptingImplementation.IL2CPP)
        {
            Exit("Standalone scripting backend is " + backend + ", not IL2CPP");
            return;
        }

        foreach (string category in Categories)
        {
            if (AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Licensed/DisplayMeshes/" + category + ".asset") == null)
            {
                Exit("licensed display mesh missing: " + category);
                return;
            }
        }

        (string material, string shader)[] materials =
        {
            ("XrSimHarnessIndexed", "Assets/Zantetsu/Runtime/Rendering/VpIndexedIndirectUnlit.shader"),
            ("XrSimHarnessIndexedShadow", "Assets/Zantetsu/Runtime/Rendering/VpIndexedIndirectShadowCaster.shader"),
            ("XrSimHarnessCulled", "Assets/Zantetsu/Runtime/Rendering/VpCulledIndexedIndirectUnlit.shader"),
            ("XrSimHarnessCulledShadow", "Assets/Zantetsu/Runtime/Rendering/VpCulledIndexedShadowCaster.shader"),
        };

        Directory.CreateDirectory(ResourcesDir);
        foreach ((string material, string shaderPath) in materials)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
            {
                Exit("shader missing or with errors: " + shaderPath);
                return;
            }

            AssetDatabase.CreateAsset(new Material(shader), ResourcesDir + "/" + material + ".mat");
        }

        AssetDatabase.SaveAssets();
        var pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        Debug.Log("XrSimHarnessBuild: graphics APIs " + string.Join(",", PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64))
            + ", stereo rendering " + PlayerSettings.stereoRenderingPath + ", backend " + backend
            + (pipeline != null ? ", shadow cascades " + pipeline.shadowCascadeCount + ", main light shadow map " + pipeline.mainLightShadowmapResolution : ", default pipeline not URP"));

        BuildReport report;
        try
        {
            Directory.CreateDirectory(outDir);
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/Sandbox.unity" },
                locationPathName = Path.Combine(outDir, "zantetsuken-vr.exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development,
            });
        }
        finally
        {
            AssetDatabase.SaveAssets();
        }

        Debug.Log("XrSimHarnessBuild: result " + report.summary.result + ", errors " + report.summary.totalErrors + ", output " + report.summary.outputPath);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    private static void Exit(string message)
    {
        Debug.LogError("XrSimHarnessBuild: " + message);
        EditorApplication.Exit(3);
    }
}
