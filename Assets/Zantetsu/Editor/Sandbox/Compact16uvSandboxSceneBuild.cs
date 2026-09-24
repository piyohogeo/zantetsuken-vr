using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>Explicit private variant of the existing product scene, not an all-asset replacement.</summary>
    public static class Compact16uvSandboxSceneBuild
    {
        private const string Root="Assets/Licensed/Compact16uvIntake/SceneIntegration";
        public const string ScenePath=Root+"/Compact16uvSandbox.unity";
        public static void BuildScene()
        {
            var normal=AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas/Normal.png");
            var debug=AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas/Debug.png");
            if(normal==null||debug==null)throw new InvalidOperationException("Prepare the verified private atlas pair first.");
            Directory.CreateDirectory(Root);
            var scene=EditorSceneManager.OpenScene(CutWorldSandboxSceneBuilder.ScenePathInProject,OpenSceneMode.Single);
            var world=UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
            if(world==null)throw new InvalidOperationException("Product sandbox root missing");
            var serialized=new SerializedObject(world);
            serialized.FindProperty("normalPaletteAtlas").objectReferenceValue=normal;
            serialized.FindProperty("debugPaletteAtlas").objectReferenceValue=debug;
            if (Environment.GetEnvironmentVariable("VP_SCENE_AB_BUILD") == "1")
            {
                string profilePath = Root + "/SceneAbProfile.asset";
                var originalProfile = serialized.FindProperty("profile").objectReferenceValue;
                var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(profilePath);
                if (profile == null) { profile = ScriptableObject.CreateInstance<CutWorldProfile>(); AssetDatabase.CreateAsset(profile, profilePath); }
                EditorUtility.CopySerialized(originalProfile, profile);
                var settings = new SerializedObject(profile);
                settings.FindProperty("vertexCapacity").intValue = 524288;
                settings.FindProperty("indexCapacity").intValue = 2097152;
                settings.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(profile);
                serialized.FindProperty("profile").objectReferenceValue = profile;
            }
            var bindings=serialized.FindProperty("materials");
            for(int i=0;i<bindings.arraySize;i++)
            {
                var slot=bindings.GetArrayElementAtIndex(i).FindPropertyRelative("material");
                var original=(Material)slot.objectReferenceValue;
                if(original==null||!original.HasProperty("_VpUsePaletteAtlas"))throw new InvalidOperationException("Unverified forward material");
                string path=Root+$"/SharedForward{i}.mat";
                var material=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(material==null){material=new Material(original);AssetDatabase.CreateAsset(material,path);}
                else EditorUtility.CopySerialized(original,material);
                material.SetFloat("_VpUsePaletteAtlas",1);EditorUtility.SetDirty(material);
                slot.objectReferenceValue=material;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            if(!EditorSceneManager.SaveScene(scene,ScenePath))throw new InvalidOperationException("Private scene save failed");
            Debug.Log("Compact16uv scene: existing synthetic body, product root/driver/cameras/profile unchanged; private atlas/material opt-in; original scene not overwritten.");
        }
        public static void BuildPlayer()
        {
            string output=Environment.GetEnvironmentVariable("VP_COMPACT16UV_PLAYER_OUT");
            if(string.IsNullOrEmpty(output))throw new InvalidOperationException("Set VP_COMPACT16UV_PLAYER_OUT to an external build directory");
            BuildScene();
            var xr=UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if(xr==null)throw new InvalidOperationException("Standalone XR settings missing; cannot establish mono diagnostic build");
            bool initializeXr=xr.InitManagerOnStart;
            bool success;
            try
            {
                xr.InitManagerOnStart=false;EditorUtility.SetDirty(xr);AssetDatabase.SaveAssets();
                Debug.Log("Compact16uv mono diagnostic build: XR initialize-on-start disabled for this build only.");
                bool ab = Environment.GetEnvironmentVariable("VP_SCENE_AB_BUILD") == "1";
                bool legacy = Environment.GetEnvironmentVariable("VP_SCENE_AB_LEGACY32") == "1";
                if (legacy && !ab) throw new InvalidOperationException("Legacy32 requires the explicit scene AB diagnostic build");
                var defines = !ab ? Array.Empty<string>() : legacy ? new[] { "VP_DIAGNOSTIC_SCENE_AB", "VP_DIAGNOSTIC_LEGACY32" } : new[] { "VP_DIAGNOSTIC_SCENE_AB" };
                Debug.Log("SCENE AB BUILD: enabled=" + ab + " legacy32=" + legacy);
                success=CutWorldSandboxPlayerBuild.Build(output,ScenePath,defines);
            }
            finally
            {
                xr.InitManagerOnStart=initializeXr;EditorUtility.SetDirty(xr);AssetDatabase.SaveAssets();
            }
            EditorApplication.Exit(success?0:1);
        }
    }
}
