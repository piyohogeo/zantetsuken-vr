using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    public static class BunnyDemoBuild
    {
        public const string Root = "Assets/NonCommercialDemo/StanfordBunny";
        public const string ScenePath = Root + "/" + BunnyDemo.SceneName + ".unity";

        [MenuItem("Zantetsu/Build Stanford Bunny noncommercial demo scene")]
        public static string EnsureScene()
        {
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/StanfordBunny.json") == null)
                throw new FileNotFoundException("Run Tools/StanfordBunny/Prepare-Bunny.ps1 first.");
            Directory.CreateDirectory(Root);
            var scene = EditorSceneManager.OpenScene(SandboxLocomotionSetup.ScenePath, OpenSceneMode.Single);
            // Reuse only the public XR/katana/physics setup. Keep no probe or sample city objects.
            string[] keep = { "XR Origin", "Katana", "Sandbox Katana Rig", "Slash Wave VFX", "World Physics Bootstrap", "Directional Light", "Locomotion Walls" };
            foreach (var go in scene.GetRootGameObjects())
                if (!keep.Contains(go.name)) UnityEngine.Object.DestroyImmediate(go);
            var camera = Camera.main;
            if (camera == null) throw new InvalidOperationException("Public sandbox XR camera missing.");
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.035f,.06f,.1f);
            var katana = UnityEngine.Object.FindFirstObjectByType<SandboxRightHandKatana>();
            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(Root + "/BunnyProfile.asset");
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<CutWorldProfile>();
                AssetDatabase.CreateAsset(profile, Root + "/BunnyProfile.asset");
            }
            var ps = new SerializedObject(profile);
            ps.FindProperty("gpuVertexInitialCapacity").intValue = 1048576;
            ps.FindProperty("gpuIndexInitialCapacity").intValue = 4194304;
            ps.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            var goWorld = new GameObject("Bunny cut world");
            var world = goWorld.AddComponent<CutWorldRoot>();
            var ws = new SerializedObject(world);
            ws.FindProperty("profile").objectReferenceValue = profile;
            var bindings = ws.FindProperty("materials");
            bindings.arraySize = 2;
            for (int i=0; i<2; i++)
            {
                var b = bindings.GetArrayElementAtIndex(i);
                b.FindPropertyRelative("sourceIndex").intValue = i;
                b.FindPropertyRelative("material").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>("Assets/Zantetsu/Settings/CutWorldSandbox"+(i==0?"Side":"End")+".mat");
            }
            CutWorldShadowCasters.Assign(ws);
            ws.ApplyModifiedPropertiesWithoutUndo();
            // The root-only prefab stores authoring data, never live workers, camera refs or shapes.
            var template = PrefabUtility.SaveAsPrefabAsset(goWorld, Root+"/BunnyWorld.prefab");
            var ds = new SerializedObject(goWorld.AddComponent<CutWorldCameraDrawing>());
            ds.FindProperty("world").objectReferenceValue = world;
            ds.FindProperty("cameras").arraySize = 1;
            ds.FindProperty("cameras").GetArrayElementAtIndex(0).objectReferenceValue = camera;
            ds.ApplyModifiedPropertiesWithoutUndo();
            var hs = new SerializedObject(goWorld.AddComponent<SandboxSlashPropHit>());
            hs.FindProperty("world").objectReferenceValue = world;
            hs.FindProperty("katana").objectReferenceValue = katana;
            hs.FindProperty("separationImpulse").floatValue = 0.1f;
            hs.ApplyModifiedPropertiesWithoutUndo();
            var demo = new GameObject("Bunny demo controller").AddComponent<BunnyDemo>();
            demo.world = world;
            demo.worldTemplate = template;
            demo.katana = katana;
            demo.input = AssetDatabase.LoadAssetAtPath<TextAsset>(Root+"/StanfordBunny.json");
            demo.licenseNotice = AssetDatabase.LoadAssetAtPath<TextAsset>(Root+"/STANFORD-NOTICE.txt");
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Demo floor";
            floor.transform.position = new Vector3(0,-.1f,3);
            floor.transform.localScale = new Vector3(12,.2f,12);
            // A low platform supports falling pieces at the initial bunny height.
            var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = "Bunny table";
            platform.transform.position = new Vector3(0,.375f,3.0f);
            platform.transform.localScale = new Vector3(5.2f,.75f,4.4f);
            var label = new GameObject("Noncommercial attribution").AddComponent<TextMesh>();
            label.text = "NONCOMMERCIAL DEMO\nStanford Bunny\nStanford University Computer Graphics Laboratory\ngraphics.stanford.edu/data/3Dscanrep/\nLeft menu: Bunny count / Reset scene";
            label.fontSize = 40; label.characterSize = .035f;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
            label.anchor = TextAnchor.MiddleCenter;
            label.transform.position = new Vector3(0,2.0f,4.7f);
            AssetDatabase.SaveAssets();
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Bunny scene save failed.");
            AssetDatabase.ImportAsset(ScenePath);
            // Permit a direct Editor Play session to reset this scene by its path.
            if (!EditorBuildSettings.scenes.Any(s=>s.path == ScenePath))
                EditorBuildSettings.scenes = EditorBuildSettings.scenes.Concat(new[] {new EditorBuildSettingsScene(ScenePath,true)}).ToArray();
            return ScenePath;
        }

        public static void PrepareFromCommandLine()
        {
            try { EnsureScene(); AssetDatabase.SaveAssets(); EditorApplication.Exit(0); }
            catch(Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
    }
}
