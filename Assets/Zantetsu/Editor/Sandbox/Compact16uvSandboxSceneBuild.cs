using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>Explicit private variant of the existing product scene, not an all-asset replacement.</summary>
    public static class Compact16uvSandboxSceneBuild
    {
        private const string Root="Assets/Licensed/Compact16uvIntake/SceneIntegration";
        public const string ScenePath=Root+"/Compact16uvSandbox.unity";
        public static void BuildScene()
        {
            bool authored = Environment.GetEnvironmentVariable("VP_AUTHORED_MEGACITY_SCENE") == "1";
            if (authored && Environment.GetEnvironmentVariable("VP_SCENE_AB_BUILD") == "1")
                throw new InvalidOperationException("Keep authored integration separate from dense synthetic AB");
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
            if (authored) bindings.arraySize = 3;
            for(int i=0;i<bindings.arraySize;i++)
            {
                var slot=bindings.GetArrayElementAtIndex(i).FindPropertyRelative("material");
                var original=(Material)slot.objectReferenceValue;
                if(original==null||!original.HasProperty("_VpUsePaletteAtlas"))throw new InvalidOperationException("Unverified forward material");
                string path=Root+(authored ? $"/MegacityForward{i}.mat" : $"/SharedForward{i}.mat");
                var material=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(material==null){material=new Material(original);AssetDatabase.CreateAsset(material,path);}
                else EditorUtility.CopySerialized(original,material);
                material.SetFloat("_VpUsePaletteAtlas",1);EditorUtility.SetDirty(material);
                if (authored)
                {
                    material.SetColor("_BaseColor", Color.white);
                    material.SetTexture("_BaseMap", normal);
                    bindings.GetArrayElementAtIndex(i).FindPropertyRelative("sourceIndex").intValue = i;
                }
                slot.objectReferenceValue=material;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            if (authored)
            {
                var probe = new SerializedObject(UnityEngine.Object.FindFirstObjectByType<SandboxCutWorldProbe>());
                var physics = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Licensed/Compact16uvIntake/Resources/AuthoredPhysicsMigration/Megacity.json");
                var geometry = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Licensed/Compact16uvIntake/Resources/Static16Migration/Megacity.bytes");
                if (physics == null || geometry == null) throw new InvalidOperationException("Prepare pinned authored physics/Static16 first");
                probe.FindProperty("authoredPhysics").objectReferenceValue = physics;
                probe.FindProperty("static16Geometry").objectReferenceValue = geometry;
                probe.FindProperty("bodyPosition").vector3Value = new Vector3(0, .15f, 0);
                // Deliberate diagnostic placement: Mesh-local Z up -> world Y up.
                // Both physics and rendering share the same actor frame, unit scale.
                probe.FindProperty("bodyEuler").vector3Value = new Vector3(-90, 0, 0);
                probe.FindProperty("lookImpulse").floatValue = 0;
                probe.FindProperty("plane").vector4Value = new Vector4(0, 0, 1, -2.5f);
                probe.FindProperty("childPlane").vector4Value = new Vector4(1, 0, 0, .01360642f);
                probe.ApplyModifiedPropertiesWithoutUndo();
                var camera = Camera.main;
                camera.transform.position = new Vector3(22, 20, -28);
                camera.transform.LookAt(new Vector3(0, 6, 0));
                camera.nearClipPlane = .1f; camera.farClipPlane = 150;
                GameObject.Find("Floor").transform.localScale = new Vector3(40, .5f, 40);
            }
            AssetDatabase.SaveAssets();
            string target = authored ? Root+"/Compact16uvMegacity.unity" : ScenePath;
            if(!EditorSceneManager.SaveScene(scene,target))throw new InvalidOperationException("Private scene save failed");
            Debug.Log("Compact16uv scene: authoredMegacity="+authored+" private variant; original scene not overwritten.");
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
                string scene = Environment.GetEnvironmentVariable("VP_AUTHORED_MEGACITY_SCENE") == "1" ? Root+"/Compact16uvMegacity.unity" : ScenePath;
                success=CutWorldSandboxPlayerBuild.Build(output,scene,defines);
            }
            finally
            {
                xr.InitManagerOnStart=initializeXr;EditorUtility.SetDirty(xr);AssetDatabase.SaveAssets();
            }
            EditorApplication.Exit(success?0:1);
        }
    }
}
