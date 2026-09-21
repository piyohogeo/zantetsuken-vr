using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// Makes the sandbox scene of the cut world and the assets it needs (DESIGN 4.5.6, 7.2): the profile, the two
    /// materials, and one scene with a camera that draws, a light, a floor, the world's root and one body to cut.
    /// <para>
    /// **It is an authoring tool, not part of the game.** It exists so that the scene can be made again from the
    /// values written here rather than being an opaque asset: run it, and the scene on disk is the scene these lines
    /// describe. Opening the scene and pressing Play is all that is needed afterwards — nothing here runs at play
    /// time, and the scene does not need it.
    /// </para>
    /// </summary>
    public static class CutWorldSandboxSceneBuilder
    {
        private const string SceneFolder = "Assets/Scenes";
        private const string ScenePath = SceneFolder + "/CutWorldSandbox.unity";
        private const string AssetFolder = "Assets/Zantetsu/Settings";
        private const string ProfilePath = AssetFolder + "/CutWorldSandboxProfile.asset";
        private const string SideMaterialPath = AssetFolder + "/CutWorldSandboxSide.mat";
        private const string EndMaterialPath = AssetFolder + "/CutWorldSandboxEnd.mat";
        private const string FloorMaterialPath = AssetFolder + "/CutWorldSandboxFloor.mat";
        private const string DisplayShader = "Zantetsu/VP Indexed Indirect Unlit";

        /// <summary>The scene this builds, for whoever needs to open or load it.</summary>
        public static string ScenePathInProject => ScenePath;

        [MenuItem("Zantetsu/Build the cut world sandbox scene")]
        public static void Build()
        {
            Directory.CreateDirectory(SceneFolder);
            Directory.CreateDirectory(AssetFolder);

            LoadOrCreateProfile();
            LoadOrCreateMaterial(SideMaterialPath, DisplayShader, new Color(0.15f, 0.35f, 0.90f));
            LoadOrCreateMaterial(EndMaterialPath, DisplayShader, new Color(0.95f, 0.55f, 0.10f));
            LoadOrCreateMaterial(FloorMaterialPath, null, new Color(0.18f, 0.20f, 0.24f));

            // Written and imported before anything refers to them: an asset made in this same run is not a reference
            // a scene can keep until the database has it.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // The scene is made first, and the assets are loaded **after** it: making a new single scene unloads what
            // nothing refers to, which would leave a reference taken before it pointing at a destroyed object.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(ProfilePath);
            var side = AssetDatabase.LoadAssetAtPath<Material>(SideMaterialPath);
            var end = AssetDatabase.LoadAssetAtPath<Material>(EndMaterialPath);
            var floor = AssetDatabase.LoadAssetAtPath<Material>(FloorMaterialPath);
            if (profile == null || side == null || end == null)
            {
                Debug.LogError("The sandbox scene was not built: its profile or materials could not be loaded.");
                return;
            }

            // The camera that really draws what the display settles.
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            camera.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, -3.2f), Quaternion.Euler(8f, 0f, 0f));
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 60f;

            var lightObject = new GameObject("Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(45f, 35f, 0f);

            // A floor, so that what is drawn has something behind it and the body's place is legible.
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Floor";
            ground.transform.position = new Vector3(0f, -0.25f, 0f);
            ground.transform.localScale = new Vector3(8f, 0.5f, 8f);
            if (floor != null)
            {
                ground.GetComponent<MeshRenderer>().sharedMaterial = floor;
            }

            // The world: the root with its profile and the materials its display draws with, the drawing of that
            // display for this camera, and the one body with the keys that ask for a cut of it.
            var worldObject = new GameObject("Cut World");
            CutWorldRoot root = worldObject.AddComponent<CutWorldRoot>();
            var serialized = new SerializedObject(root);
            serialized.FindProperty("profile").objectReferenceValue = profile;
            SerializedProperty materials = serialized.FindProperty("materials");
            materials.arraySize = 2;
            SetBinding(materials.GetArrayElementAtIndex(0), 0, side);
            SetBinding(materials.GetArrayElementAtIndex(1), 1, end);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            CutWorldCameraDrawing drawing = worldObject.AddComponent<CutWorldCameraDrawing>();
            var drawingSerialized = new SerializedObject(drawing);
            drawingSerialized.FindProperty("world").objectReferenceValue = root;
            SerializedProperty cameras = drawingSerialized.FindProperty("cameras");
            cameras.arraySize = 1;
            cameras.GetArrayElementAtIndex(0).objectReferenceValue = camera;
            drawingSerialized.FindProperty("layer").intValue = 0;
            drawingSerialized.ApplyModifiedPropertiesWithoutUndo();

            SandboxCutWorldProbe probe = worldObject.AddComponent<SandboxCutWorldProbe>();
            var probeSerialized = new SerializedObject(probe);
            probeSerialized.FindProperty("world").objectReferenceValue = root;
            probeSerialized.FindProperty("bodyPosition").vector3Value = new Vector3(0f, 1f, 0f);
            probeSerialized.FindProperty("bodyExtents").vector3Value = new Vector3(0.5f, 0.5f, 0.5f);
            probeSerialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("The cut world sandbox scene was built at " + ScenePath + ".");
        }

        private static void SetBinding(SerializedProperty element, int sourceIndex, Material material)
        {
            element.FindPropertyRelative("sourceIndex").intValue = sourceIndex;
            element.FindPropertyRelative("material").objectReferenceValue = material;
        }

        private static CutWorldProfile LoadOrCreateProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(ProfilePath);
            if (profile != null)
            {
                return profile;
            }

            profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            return profile;
        }

        private static Material LoadOrCreateMaterial(string path, string shaderName, Color colour)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                return material;
            }

            Shader shader = shaderName != null ? Shader.Find(shaderName) : null;
            if (shader == null && shaderName != null)
            {
                Debug.LogError("The shader " + shaderName + " was not found; " + path + " was not made.");
                return null;
            }

            material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"));
            material.color = colour;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        /// <summary>Puts the scene into the build settings, so that it can be loaded by name at play time.</summary>
        private static void AddToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path == path)
                {
                    scenes[i] = new EditorBuildSettingsScene(path, true);
                    EditorBuildSettings.scenes = scenes.ToArray();
                    return;
                }
            }

            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
