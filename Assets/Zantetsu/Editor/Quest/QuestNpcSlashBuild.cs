using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>Private Android copy of the ten-model MultiNpcSlash scene and its recorded input.</summary>
    public static class QuestNpcSlashBuild
    {
        public const string ApplicationIdentifier = "com.zantetsuken.quest.npcslash";
        public const string SourceScene = "Assets/Licensed/MultiNpc/MultiNpcCity.unity";
        public const string ScenePath = "Assets/Licensed/Quest3s/QuestNpcSlash10.unity";
        private const string ProfilePath = "Assets/Licensed/Quest3s/QuestNpcSlashProfile.asset";
        private const string MaterialRoot = "Assets/Licensed/Quest3s/NpcSlashMaterials";
        private const string InputDefault = @"C:\log\zantetsuken-vr\SlashSpan\20260918-153438-normal\input.csv";

        public static void Configure()
        {
            try
            {
                var report = new List<string>();
                QuestStaticDemoBuild.ConfigureCommon(report);
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
                if (!File.Exists(SourceScene)) throw new FileNotFoundException("MultiNpcSlash licensed scene is missing", SourceScene);
                if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
                if (!AssetDatabase.CopyAsset(SourceScene, ScenePath)) throw new IOException("NPC scene copy failed");
                EditorSceneManager.OpenScene(ScenePath);
                var worlds = Object.FindObjectsByType<CutWorldRoot>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Where(w => w.isActiveAndEnabled).ToArray();
                if (worlds.Length == 0) throw new InvalidOperationException("NPC scene has no CutWorldRoot");
                foreach (var world in worlds)
                {
                    string assetPath = worlds.Length == 1 ? ProfilePath : ProfilePath.Replace(".asset", "-" + world.GetInstanceID() + ".asset");
                    var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(assetPath);
                    if (profile == null)
                    {
                        profile = Object.Instantiate(world.Profile);
                        AssetDatabase.CreateAsset(profile, assetPath);
                    }
                    var settings = new SerializedObject(profile);
                    // 32 MiB initial GPU geometry instead of the PC profile's 512 MiB; normal growth remains available.
                    settings.FindProperty("gpuVertexInitialCapacity").intValue = 1048576;
                    settings.FindProperty("gpuIndexInitialCapacity").intValue = 4194304;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(profile);
                    var binding = new SerializedObject(world);
                    binding.FindProperty("profile").objectReferenceValue = profile;
                    binding.ApplyModifiedPropertiesWithoutUndo();
                }
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(QuestStaticDemoBuild.PipelinePath);
                var scenarios = Object.FindObjectsByType<ScenarioRenderPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (scenarios.Length == 0) scenarios = new[] { new GameObject("Quest NPC render pipeline").AddComponent<ScenarioRenderPipeline>() };
                foreach (var scenario in scenarios) { scenario.pipeline = pipeline; EditorUtility.SetDirty(scenario); }
                Camera camera = Camera.main;
                if (camera == null) throw new InvalidOperationException("NPC scene has no MainCamera");
                camera.allowHDR = false; camera.allowMSAA = true;
                var cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.SetRenderer(0); cameraData.renderPostProcessing = false;
                if (!AssetDatabase.IsValidFolder(MaterialRoot)) AssetDatabase.CreateFolder("Assets/Licensed/Quest3s", "NpcSlashMaterials");
                var materials = new Dictionary<Material, Material>();
                int materialIndex = 0;
                foreach (var renderer in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    // The cut input and Unity skin must both retain all four authored bone weights.
                    renderer.quality = SkinQuality.Bone4;
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        Material source = slots[i];
                        if (source == null || source.shader == null || source.shader.name != "Zantetsu/VP Mesh Surface") continue;
                        if (!materials.TryGetValue(source, out var questMaterial))
                        {
                            string path = MaterialRoot + "/Skin-" + materialIndex++ + ".mat";
                            questMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
                            if (questMaterial == null) { questMaterial = new Material(source); AssetDatabase.CreateAsset(questMaterial, path); }
                            else questMaterial.CopyPropertiesFromMaterial(source);
                            questMaterial.EnableKeyword("VP_CAMERA_ONLY_ASW");
                            EditorUtility.SetDirty(questMaterial);
                            materials.Add(source, questMaterial);
                        }
                        slots[i] = questMaterial;
                    }
                    renderer.sharedMaterials = slots;
                    EditorUtility.SetDirty(renderer);
                }
                ConfigureReplayVisualMaterials(report);
                foreach (var recorder in Object.FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var settings = new SerializedObject(recorder);
                    settings.FindProperty("showControls").boolValue = false;
                    settings.FindProperty("autoCaptureWhenIdle").boolValue = false;
                    settings.FindProperty("autoDumpOnLatch").boolValue = false;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
                foreach (var overlay in Object.FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                { overlay.Visible = false; EditorUtility.SetDirty(overlay); }
                int npcCount = Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                if (npcCount != 10) throw new InvalidOperationException("Expected ten NPCs, found " + npcCount);
                string[] args = Environment.GetCommandLineArgs();
                int inputIndex = Array.IndexOf(args, "-zantetsuQuestNpcInput");
                string input = inputIndex >= 0 ? args[inputIndex + 1] : InputDefault;
                if (!File.Exists(input)) throw new FileNotFoundException("Recorded Slash input is missing", input);
                Directory.CreateDirectory("Assets/StreamingAssets/Quest3s");
                File.Copy(input, "Assets/StreamingAssets/Quest3s/NpcSlashInput.csv", true);
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
                AssetDatabase.Refresh(); AssetDatabase.SaveAssets();
                Debug.Log("QUEST NPC CONFIGURE: " + string.Join("; ", report) + "; scene=" + ScenePath + "; NPCs=" + npcCount
                    + "; camera-only skin materials=" + materials.Count + "; G=1 B=1; GPU initial=32MiB; input=" + input);
            }
            catch (Exception e) { Debug.LogError(e); if (Application.isBatchMode) EditorApplication.Exit(1); throw; }
        }

        internal static void ConfigureReplayVisualMaterials(List<string> report, string materialRoot = MaterialRoot)
        {
            MeshRenderer[] renderers = SandboxPropSlashPlayerCheck.ReplayVisualRenderers()
                .OrderBy(r => HierarchyPath(r.transform), StringComparer.Ordinal).ToArray();
            if (renderers.Length != 9) throw new InvalidOperationException("Expected five katana and four wave MeshRenderers, found " + renderers.Length);
            Shader lit = LoadReplayShader("Lit");
            Shader unlit = LoadReplayShader("Unlit");
            var copies = new Dictionary<Material, Material>();
            int litRenderers = 0, unlitRenderers = 0;
            foreach (MeshRenderer renderer in renderers)
            {
                Material[] slots = renderer.sharedMaterials;
                if (slots.Length == 0) throw new InvalidOperationException("Replay visual has no materials: " + renderer.name);
                string rendererKind = null;
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    Material source = slots[slot];
                    string name = source != null && source.shader != null ? source.shader.name : "missing";
                    string kind = name == "Universal Render Pipeline/Lit" ? "Lit" : name == "Universal Render Pipeline/Unlit" ? "Unlit" : null;
                    if (kind == null) throw new InvalidOperationException("Unsupported replay visual material: " + renderer.name + " slot " + slot + " shader=" + name);
                    if (rendererKind != null && rendererKind != kind) throw new InvalidOperationException("Replay visual mixes Lit and Unlit slots: " + renderer.name);
                    rendererKind = kind;
                    if (source.renderQueue > 2500 || !source.HasProperty("_Surface") || source.GetFloat("_Surface") != 0f ||
                        !source.HasProperty("_ZWrite") || source.GetFloat("_ZWrite") != 1f)
                        throw new InvalidOperationException("Camera-only replay visuals require opaque, depth-writing source material: " + source.name);
                    Shader wrapper = kind == "Lit" ? lit : unlit;
                    for (int property = 0; property < source.shader.GetPropertyCount(); property++)
                    {
                        string propertyName = source.shader.GetPropertyName(property);
                        if (wrapper.FindPropertyIndex(propertyName) < 0) throw new InvalidOperationException("Replay wrapper is missing URP property " + propertyName + ": " + kind);
                    }
                    foreach (string keyword in source.shaderKeywords)
                        if (!wrapper.keywordSpace.FindKeyword(keyword).isValid)
                            throw new InvalidOperationException("Replay wrapper is missing active URP keyword " + keyword + ": " + source.name);
                    if (!copies.TryGetValue(source, out Material copy))
                    {
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
                        if (string.IsNullOrEmpty(guid)) throw new InvalidOperationException("Replay visual material must be an authored asset: " + source.name);
                        string path = materialRoot + "/Visual-" + guid + ".mat";
                        copy = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (copy == null) { copy = new Material(wrapper); AssetDatabase.CreateAsset(copy, path); }
                        else copy.shader = wrapper;
                        copy.CopyPropertiesFromMaterial(source);
                        copy.shaderKeywords = source.shaderKeywords;
                        copy.SetFloat("_XRMotionVectorsPass", 1f);
                        copy.SetShaderPassEnabled("XRMotionVectors", true);
                        ValidateReplayShaderPasses(source, copy, wrapper, kind);
                        copy.name = source.name + " (Quest camera-only)";
                        EditorUtility.SetDirty(copy);
                        copies.Add(source, copy);
                    }
                    slots[slot] = copy;
                }
                if (rendererKind == "Lit") litRenderers++; else unlitRenderers++;
                renderer.sharedMaterials = slots;
                // Include the current geometry in URP's low-resolution depth. The wrapper's XR pass ignores all object history.
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                EditorUtility.SetDirty(renderer);
            }
            if (litRenderers != 5 || unlitRenderers != 4)
                throw new InvalidOperationException("Expected five Lit katana and four Unlit waves; found " + litRenderers + "/" + unlitRenderers);
            report.Add("camera-only replay visuals=" + renderers.Length + " (katana=" + litRenderers + ", waves=" + unlitRenderers + "); materials=" + copies.Count);
        }

        private static Shader LoadReplayShader(string kind)
        {
            string path = "Assets/Zantetsu/Runtime/Quest/Shaders/QuestCameraOnly" + kind + ".shader";
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null || shader.name != "Zantetsu/Quest Camera Only " + kind || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                throw new InvalidOperationException("Camera-only replay wrapper did not import or compile: " + path);
            return shader;
        }

        private static void ValidateReplayShaderPasses(Material source, Material copy, Shader wrapper, string kind)
        {
            // Before the first camera render, batch-mode Material.FindPass can select the fallback even for stock URP.
            // Inspect the imported first SubShader instead, just as the native shader bake does.
            string[] sourcePasses = ReplayShaderPassNames(source.shader);
            string[] wrapperPasses = ReplayShaderPassNames(wrapper);
            var comparer = StringComparer.OrdinalIgnoreCase;
            string colourPass = kind == "Lit" ? "ForwardLit" : "Unlit";
            if (copy.shader != wrapper || !sourcePasses.SequenceEqual(wrapperPasses, comparer)
                || !wrapperPasses.Contains("XRMotionVectors", comparer) || !wrapperPasses.Contains(colourPass, comparer))
                throw new InvalidOperationException("Replay wrapper lost its XR or ordinary colour pass: " + kind
                    + "; sourceSubshaderPasses=[" + string.Join(",", sourcePasses) + "]; wrapperSubshaderPasses=[" + string.Join(",", wrapperPasses)
                    + "]; " + ReplayPassDiagnostics(source, copy, wrapper));
            Debug.Log("QUEST REPLAY SHADER STRUCTURE: source=" + source.shader.name + "; wrapper=" + wrapper.name
                + "; firstSubshaderPasses=" + wrapperPasses.Length + "; passes=[" + string.Join(",", wrapperPasses) + "]");
        }

        private static string[] ReplayShaderPassNames(Shader shader)
        {
            ShaderData data = ShaderUtil.GetShaderData(shader);
            if (data.SubshaderCount < 1) throw new InvalidOperationException("Replay shader has no imported SubShader: " + shader.name);
            var subshader = data.GetSubshader(0);
            return Enumerable.Range(0, subshader.PassCount).Select(index => subshader.GetPass(index).Name).ToArray();
        }

        private static string ReplayPassDiagnostics(Material source, Material copy, Shader wrapper)
        {
            var freshWrapper = new Material(wrapper);
            try
            {
                string pipeline = GraphicsSettings.currentRenderPipeline == null ? "null" : GraphicsSettings.currentRenderPipeline.name;
                string qualityPipeline = QualitySettings.renderPipeline == null ? "null" : QualitySettings.renderPipeline.name;
                string messages = string.Join(" | ", ShaderUtil.GetShaderMessages(wrapper).Select(message => message.severity + ": " + message.message));
                return "graphics=" + SystemInfo.graphicsDeviceType + "; pipeline=" + pipeline + "; qualityPipeline=" + qualityPipeline
                    + "; wrapper=" + wrapper.name + ", shaderPassCount=" + wrapper.passCount + ", messages=[" + messages + "]"
                    + "; source={" + ReplayMaterialPassDiagnostics(source) + "}"
                    + "; copy={" + ReplayMaterialPassDiagnostics(copy) + "}"
                    + "; freshWrapper={" + ReplayMaterialPassDiagnostics(freshWrapper) + "}";
            }
            finally { Object.DestroyImmediate(freshWrapper); }
        }

        private static string ReplayMaterialPassDiagnostics(Material material)
        {
            string passes = string.Join(",", Enumerable.Range(0, material.passCount).Select(index => index + ":" + material.GetPassName(index)));
            return "material=" + material.name + ", shader=" + material.shader.name + ", shaderPassCount=" + material.shader.passCount
                + ", materialPassCount=" + material.passCount + ", passes=[" + passes + "]"
                + ", XR=" + material.FindPass("XRMotionVectors") + ", XRUpper=" + material.FindPass("XRMOTIONVECTORS")
                + ", Forward=" + material.FindPass("ForwardLit") + ", ForwardUpper=" + material.FindPass("FORWARDLIT")
                + ", Unlit=" + material.FindPass("Unlit") + ", UnlitUpper=" + material.FindPass("UNLIT");
        }

        private static string HierarchyPath(Transform transform)
            => transform.parent == null ? transform.name : HierarchyPath(transform.parent) + "/" + transform.name;

        public static void BuildApk()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
            QuestStaticDemoBuild.BuildApkForScene(ScenePath);
        }
    }
}
