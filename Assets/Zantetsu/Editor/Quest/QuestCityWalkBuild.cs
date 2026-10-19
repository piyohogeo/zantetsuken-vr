using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Zantetsu.PhysicsCut;
using Zantetsu.Quest;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>Android copy of the existing city-walk scene and its route; private licensed inputs remain ignored.</summary>
    public static class QuestCityWalkBuild
    {
        public const string ScenePath = "Assets/Licensed/Quest3s/QuestCityWalk.unity";
        private const string ProfilePath = "Assets/Licensed/Quest3s/QuestCityWalkProfile.asset";
        private const string MaterialRoot = "Assets/Licensed/Quest3s/CityWalkMaterials";
        private const string InputDefault = @"C:\log\zantetsuken-vr\SlashSpan\20260918-153438-normal\input.csv";
        private const string ScriptDefault = @"C:\log\zantetsuken-vr\walk-city-2026-10-03\route\v7\script.txt";

        public static void Configure()
        {
            try
            {
                var report = new List<string>();
                QuestStaticDemoBuild.ConfigureCommon(report);
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, QuestNpcSlashDemo.CityWalkPackageName);
                if (!File.Exists(QuestStaticDemoBuild.SourceScene)) throw new FileNotFoundException("Licensed WalkCity scene is missing");
                if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
                if (!AssetDatabase.CopyAsset(QuestStaticDemoBuild.SourceScene, ScenePath)) throw new IOException("City walk scene copy failed");
                EditorSceneManager.OpenScene(ScenePath);
                var worlds = Object.FindObjectsByType<CutWorldRoot>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Where(w => w.isActiveAndEnabled).ToArray();
                if (worlds.Length != 1) throw new InvalidOperationException("City walk requires one active CutWorldRoot");
                CutWorldRoot world = worlds[0];
                var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(ProfilePath);
                if (profile == null) { profile = Object.Instantiate(world.Profile); AssetDatabase.CreateAsset(profile, ProfilePath); }
                else EditorUtility.CopySerialized(world.Profile, profile);
                profile.name = "QuestCityWalkProfile";
                var settings = new SerializedObject(profile);
                settings.FindProperty("geometryWorkerCount").intValue = 1;
                settings.FindProperty("backgroundWorkerCount").intValue = 1;
                settings.FindProperty("gpuVertexInitialCapacity").intValue = 1048576;
                settings.FindProperty("gpuIndexInitialCapacity").intValue = 4194304;
                settings.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(profile);
                var binding = new SerializedObject(world);
                binding.FindProperty("profile").objectReferenceValue = profile;
                binding.ApplyModifiedPropertiesWithoutUndo();
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(QuestStaticDemoBuild.PipelinePath);
                foreach (var scenario in Object.FindObjectsByType<ScenarioRenderPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                { scenario.pipeline = pipeline; EditorUtility.SetDirty(scenario); }
                Camera camera = Camera.main;
                if (camera == null) throw new InvalidOperationException("City walk scene has no MainCamera");
                camera.nearClipPlane = 0.01f; camera.farClipPlane = 100f;
                camera.allowHDR = false; camera.allowMSAA = true;
                var cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.SetRenderer(0); cameraData.renderPostProcessing = false;
                if (!AssetDatabase.IsValidFolder(MaterialRoot)) AssetDatabase.CreateFolder("Assets/Licensed/Quest3s", "CityWalkMaterials");
                // One copy per source material AND motion (2026-10-19): a skin's VP Mesh Surface slots take URP's object
                // motion (this and the previous frame's object matrix and skinned vertices), every other renderer's the
                // camera's alone, as before -- a source shared by both gets two copies.
                var materials = new QuestSurfaceMotionMaterials(MaterialRoot, "Surface-", "Surface-Skin-");
                var skinsBefore = new Dictionary<string, int>();
                var notSurface = new Dictionary<string, int>();
                foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    QuestSurfaceMotion motion = QuestSurfaceMotionMaterials.MotionOf(renderer);
                    if (renderer is SkinnedMeshRenderer skin)
                    {
                        skin.quality = SkinQuality.Bone4;
                        QuestSurfaceMotionMaterials.EnableSkinHistory(skin, skinsBefore);
                    }

                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        Material source = slots[i];
                        if (source == null || source.shader == null || source.shader.name != QuestSurfaceMotionMaterials.SurfaceShaderName)
                        {
                            // Not this change's: a skin's other material is listed, not taken for done.
                            if (renderer is SkinnedMeshRenderer)
                            {
                                string key = (source == null ? "(none)" : source.name) + " [" + (source == null || source.shader == null ? "no shader" : source.shader.name) + "]";
                                notSurface[key] = notSurface.TryGetValue(key, out int n) ? n + 1 : 1;
                            }
                            continue;
                        }

                        slots[i] = materials.CopyOf(source, motion);
                    }
                    renderer.sharedMaterials = slots; EditorUtility.SetDirty(renderer);
                }

                report.Add("skins before: " + QuestSurfaceMotionMaterials.Describe(skinsBefore) + " (now Object, skinned motion vectors on)");
                report.Add("skin slots not VP Mesh Surface (left as they are): " + QuestSurfaceMotionMaterials.Describe(notSurface));
                report.Add("characters settling their skin's motion history: " + QuestSurfaceMotionMaterials.EnableCharacterSkinHistory());
                QuestNpcSlashBuild.ConfigureReplayVisualMaterials(report, MaterialRoot);
                foreach (var recorder in Object.FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var options = new SerializedObject(recorder);
                    options.FindProperty("showControls").boolValue = false;
                    options.FindProperty("autoCaptureWhenIdle").boolValue = false;
                    options.FindProperty("autoDumpOnLatch").boolValue = false;
                    options.ApplyModifiedPropertiesWithoutUndo();
                }
                foreach (var overlay in Object.FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                { overlay.Visible = false; EditorUtility.SetDirty(overlay); }
                if (Object.FindObjectsByType<MobPlanCrowd>(FindObjectsSortMode.None).Length != 1)
                    throw new InvalidOperationException("City walk requires one active crowd");
                int cuttables = Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                if (cuttables == 0) throw new InvalidOperationException("City walk has no placed cuttables");
                string[] args = Environment.GetCommandLineArgs();
                int inputIndex = Array.IndexOf(args, "-zantetsuQuestNpcInput"), scriptIndex = Array.IndexOf(args, "-zantetsuQuestCityScript");
                string input = inputIndex >= 0 ? args[inputIndex + 1] : InputDefault;
                string script = scriptIndex >= 0 ? args[scriptIndex + 1] : ScriptDefault;
                if (!File.Exists(input) || !File.Exists(script)) throw new FileNotFoundException("Recorded input or city route is missing");
                if (!CityWalkSteps.IsStepScript(File.ReadAllLines(script))) throw new InvalidDataException("The route must be a city step script");
                Directory.CreateDirectory("Assets/StreamingAssets/Quest3s");
                File.Copy(input, "Assets/StreamingAssets/Quest3s/NpcSlashInput.csv", true);
                File.Copy(script, "Assets/StreamingAssets/Quest3s/CityWalkScript.txt", true);
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
                AssetDatabase.Refresh(); AssetDatabase.SaveAssets();
                Debug.Log("QUEST CITY CONFIGURE: " + string.Join("; ", report) + "; cuttables=" + cuttables
                    + "; G=1 B=1; GPU initial=32MiB; colour far=100m; camera-only surface materials=" + materials.CameraOnlyCount
                    + "; object-motion skin materials=" + materials.ObjectCount + "; route=" + script);
            }
            catch (Exception e) { Debug.LogError(e); if (Application.isBatchMode) EditorApplication.Exit(1); throw; }
        }

        public static void BuildApk()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, QuestNpcSlashDemo.CityWalkPackageName);
            QuestStaticDemoBuild.BuildApkForScene(ScenePath);
        }
    }
}
