using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
using Zantetsu.EditorTools.Sandbox;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;
using Zantetsu.Quest;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>Repeatable Android settings and a private copy of the licensed seed14 scene.</summary>
    public static class QuestStaticDemoBuild
    {
        public const string ApplicationIdentifier = "com.junic.zantetsu.queststatic";
        public const string SourceScene = "Assets/Licensed/WalkCity/WalkCity.unity";
        public const string Root = "Assets/Zantetsu/QuestSettings";
        public const string RendererPath = Root + "/Quest_Renderer.asset";
        public const string PipelinePath = Root + "/Quest_RPAsset.asset";
        public const string PrivateRoot = "Assets/Licensed/Quest3s";
        public const string ScenePath = PrivateRoot + "/QuestStaticSeed14.unity";
        public const string ProfilePath = PrivateRoot + "/QuestStaticProfile.asset";
        public const string XrGeneralSettingsPath = "Assets/XR/XRGeneralSettings.asset";

        public static void Configure()
        {
            try
            {
                var report = new List<string>();
                ConfigureCommon(report);
                ConfigureScene(report);
                AssetDatabase.SaveAssets();
                Debug.Log("QUEST CONFIGURE: " + string.Join("; ", report));
            }
            catch (Exception e)
            {
                Debug.LogError("QUEST CONFIGURE failed: " + e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        // Shared Android / XR settings used by the static and NPC comparison scenes.
        public static void ConfigureCommon(List<string> report)
        {
            EnsureFolder(Root); EnsureFolder(PrivateRoot); EnsureFolder("Assets/XR");
            ConfigurePlayer(); ConfigureUrp(); ConfigureXr(report); ConfigureShaders();
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
            PlayerSettings.SetMobileMTRendering(NamedBuildTarget.Android, true);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
            PlayerSettings.Android.optimizedFramePacing = false;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.enableFrameTimingStats = true;
        }

        private static void ConfigureUrp()
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Mobile_Renderer.asset"));
                renderer.name = "Quest_Renderer";
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }
            var rso = new SerializedObject(renderer);
            rso.FindProperty("m_RenderingMode").intValue = (int)RenderingMode.Forward;
            // Vulkan plugin events use Unity's legacy current render pass through unsafe RenderGraph passes.
            // IUnityGraphicsVulkan explicitly excludes SRP RenderPass API preconditions.
            rso.FindProperty("m_UseNativeRenderPass").boolValue = false;
            rso.FindProperty("m_DepthPrimingMode").intValue = (int)DepthPrimingMode.Disabled;
            rso.FindProperty("m_CopyDepthMode").intValue = (int)CopyDepthMode.AfterOpaques;
            rso.ApplyModifiedPropertiesWithoutUndo();
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = Object.Instantiate(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset"));
                pipeline.name = "Quest_RPAsset";
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }
            var pso = new SerializedObject(pipeline);
            var renderers = pso.FindProperty("m_RendererDataList");
            renderers.arraySize = 1;
            renderers.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            pso.FindProperty("m_DefaultRendererIndex").intValue = 0;
            pso.FindProperty("m_MSAA").intValue = 4;
            pso.FindProperty("m_RenderScale").floatValue = 1f;
            pso.FindProperty("m_SupportsHDR").boolValue = false;
            pso.FindProperty("m_RequireDepthTexture").boolValue = false;
            pso.FindProperty("m_RequireOpaqueTexture").boolValue = false;
            pso.ApplyModifiedPropertiesWithoutUndo();
            int previous = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false); // Android's existing default quality; PC quality keeps its own asset.
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.antiAliasing = 4;
            QualitySettings.SetQualityLevel(previous, false);
            if (!renderer.rendererFeatures.OfType<QuestXrTargetInfoFeature>().Any())
            {
                var info = ScriptableObject.CreateInstance<QuestXrTargetInfoFeature>();
                info.name = "Quest XR target info";
                AssetDatabase.AddObjectToAsset(info, renderer);
                renderer.rendererFeatures.Add(info);
            }
            EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(pipeline);
        }

        private static void ConfigureShaders()
        {
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
            var list = settings.FindProperty("m_AlwaysIncludedShaders");
            foreach (string name in new[] { "Zantetsu/VP Indexed Indirect Unlit", "Zantetsu/VP Indexed Indirect Shadow Caster", "Zantetsu/VP Stencil Init", "Zantetsu/VP Stencil Volume", "Zantetsu/VP Stencil Cap" })
            {
                var shader = Shader.Find(name);
                if (shader == null) throw new InvalidOperationException("Shader missing: " + name);
                bool exists = false;
                for (int i = 0; i < list.arraySize; i++) exists |= list.GetArrayElementAtIndex(i).objectReferenceValue == shader;
                if (exists) continue;
                int index = list.arraySize++; list.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureScene(List<string> report)
        {
            if (!File.Exists(SourceScene)) throw new FileNotFoundException("Copy ignored Assets/Licensed from the main worktree first", SourceScene);
            if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
            if (!AssetDatabase.CopyAsset(SourceScene, ScenePath)) throw new IOException("Scene copy failed");
            EditorSceneManager.OpenScene(ScenePath);
            var world = Object.FindAnyObjectByType<CutWorldRoot>();
            if (world == null) throw new InvalidOperationException("Static scene has no CutWorldRoot");
            var bank = new SceneDrawMeshBank();
            var inventory = SceneDrawInventory.OfPlacedCuttables(Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None), world.MaterialBindings, bank);
            if (inventory.Targets.Count == 0 || inventory.Refusals.Count > 0) throw new InvalidOperationException("Invalid comparison inventory: " + inventory.Describe() + "; " + string.Join("; ", inventory.Refusals));
            string folder = "Assets/StreamingAssets/Quest3s";
            Directory.CreateDirectory(folder);
            using (var stream = File.Create(Path.Combine(folder, SceneDrawCompareBake.GeometryFile))) bank.Write(stream);
            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(ProfilePath);
            if (profile == null)
            {
                profile = Object.Instantiate(world.Profile);
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            var pso = new SerializedObject(profile);
            // Reserve remains virtual; initial GPU capacity is sized to this static scene instead of PC's 512 MiB.
            pso.FindProperty("gpuVertexInitialCapacity").intValue = Mathf.NextPowerOfTwo(checked((int)inventory.Vertices + 1024));
            pso.FindProperty("gpuIndexInitialCapacity").intValue = Mathf.NextPowerOfTwo(checked((int)inventory.Indices + 1024));
            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            var wso = new SerializedObject(world); wso.FindProperty("profile").objectReferenceValue = profile; wso.ApplyModifiedPropertiesWithoutUndo();
            foreach (var crowd in Object.FindObjectsByType<MobPlanCrowd>(FindObjectsInactive.Include, FindObjectsSortMode.None)) crowd.enabled = false;
            foreach (var scenario in Object.FindObjectsByType<ScenarioRenderPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                scenario.pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
                EditorUtility.SetDirty(scenario);
            }
            var camera = Camera.main;
            report.Add("source " + SourceScene + "; camera " + camera.transform.position.ToString("F2") + " yaw " + camera.transform.eulerAngles.y);
            camera.allowHDR = false;
            camera.allowMSAA = true;
            var data = camera.GetUniversalAdditionalCameraData();
            data.SetRenderer(0); data.renderPostProcessing = false;
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            AssetDatabase.Refresh();
            report.Add("scene " + ScenePath + ": targets " + inventory.Targets.Count + ", vertices " + inventory.Vertices + ", indices " + inventory.Indices + ", bank " + bank.Count + ", refused " + inventory.Refusals.Count + " (Unity in both modes), AppSW via XRMotionVectors re-rasterization, MSAA4/scale1");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        public static void BuildApk()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
            BuildApkForScene(ScenePath);
        }

        public static void BuildApkForScene(string scenePath)
        {
            try
            {
                const string nativePath = "Assets/Zantetsu/Runtime/Rendering/Plugins/Android/arm64-v8a/libZantetsuVpVulkan.so";
                AssetDatabase.ImportAsset(nativePath, ImportAssetOptions.ForceUpdate);
                var native = AssetImporter.GetAtPath(nativePath) as PluginImporter;
                if (native == null) throw new InvalidOperationException("Vulkan native plugin has no PluginImporter: " + nativePath);
                native.SetCompatibleWithAnyPlatform(false);
                native.SetCompatibleWithEditor(false);
                native.SetCompatibleWithPlatform(BuildTarget.Android, true);
                native.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
                native.isPreloaded = true;
                native.SaveAndReimport();
                Debug.Log("QUEST NATIVE BUILD: Android=" + native.GetCompatibleWithPlatform(BuildTarget.Android) + " CPU=" + native.GetPlatformData(BuildTarget.Android, "CPU") + " preload=" + native.isPreloaded);
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-zantetsuQuestOut");
                string output = index >= 0 ? args[index + 1] : Path.Combine(Directory.GetCurrentDirectory(), "Build", "quest-static.apk");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                EditorUserBuildSettings.buildAppBundle = false;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, target = BuildTarget.Android,
                    locationPathName = output, options = Array.IndexOf(args, "-zantetsuQuestDev") >= 0 ? BuildOptions.Development : BuildOptions.None });
                Debug.Log("QUEST BUILD: " + report.summary.result + " size=" + report.summary.totalSize + " errors=" + report.summary.totalErrors + " output=" + output);
                if (report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0) throw new InvalidOperationException("Android build failed or reported errors; inspect quest-build.log");
            }
            catch (Exception e) { Debug.LogError(e); if (Application.isBatchMode) EditorApplication.Exit(1); throw; }
        }
        private static void ConfigureXr(List<string> report)
        {
            const BuildTargetGroup group = BuildTargetGroup.Android;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(XrGeneralSettingsPath);
                if (perTarget == null)
                {
                    perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                    AssetDatabase.CreateAsset(perTarget, XrGeneralSettingsPath);
                    AssetDatabase.SaveAssets();
                }

                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
                report.Add("XR: created " + XrGeneralSettingsPath);
            }

            if (!perTarget.HasSettingsForBuildTarget(group))
            {
                perTarget.CreateDefaultSettingsForBuildTarget(group);
            }

            if (!perTarget.HasManagerSettingsForBuildTarget(group))
            {
                perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            }

            XRGeneralSettings general = perTarget.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;
            XRManagerSettings manager = general.Manager;
            bool assigned = XRPackageMetadataStore.AssignLoader(manager, "UnityEngine.XR.OpenXR.OpenXRLoader", group);
            EditorUtility.SetDirty(general);
            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(perTarget);
            report.Add("XR: Android OpenXR loader assigned=" + assigned + ", init on start, active loaders " + manager.activeLoaders.Count);

            FeatureHelpers.RefreshFeatures(group);
            OpenXRSettings openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (openXr == null) throw new InvalidOperationException("OpenXR settings for Android not found after RefreshFeatures");
            openXr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            openXr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;
            EditorUtility.SetDirty(openXr);

            OpenXRFeature quest = EnableFeature(group, MetaQuestFeature.featureId, report);
            if (quest != null)
            {
                var so = new SerializedObject(quest);
                SetBool(so, "lateLatchingMode", false);
                SetBool(so, "lateLatchingDebug", false);
                SetBool(so, "m_symmetricProjection", false);
                SetBool(so, "m_optimizeBufferDiscards", false);
                SetBool(so, "forceRemoveInternetPermission", false);
                SerializedProperty format = so.FindProperty("spacewarpMotionVectorTextureFormat");
                if (format != null) format.intValue = (int)OpenXRSettings.SpaceWarpMotionVectorTextureFormat.RGBA16f;
                SerializedProperty mvr = so.FindProperty("m_multiviewRenderRegionsOptimizationMode");
                if (mvr != null) mvr.intValue = 0;
                SerializedProperty devices = so.FindProperty("targetDevices");
                var enabledDevices = new List<string>();
                if (devices != null)
                {
                    for (int i = 0; i < devices.arraySize; i++)
                    {
                        SerializedProperty d = devices.GetArrayElementAtIndex(i);
                        d.FindPropertyRelative("enabled").boolValue = true;
                        enabledDevices.Add(d.FindPropertyRelative("manifestName").stringValue);
                    }
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(quest);
                report.Add("XR: MetaQuestFeature lateLatching off, symmetricProjection off, optimizeBufferDiscards off, MV format RGBA16f, devices " + string.Join(",", enabledDevices));
            }

            OpenXRFeature spaceWarp = EnableFeature(group, SpaceWarpFeature.k_SpaceWarpFeatureId, report);
            if (spaceWarp != null)
            {
                var so = new SerializedObject(spaceWarp);
                SerializedProperty rh = so.FindProperty("m_UseRightHandedNDC");
                string ndc = "n/a";
                if (rh != null)
                {
                    rh.boolValue = false;
                    ndc = "false (left-handed, package default for Quest)";
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(spaceWarp);
                report.Add("XR: SpaceWarpFeature useRightHandedNDC=" + ndc);
            }

            EnableFeature(group, OculusTouchControllerProfile.featureId, report);
            OpenXRFeature refresh = EnableFeature(group, QuestDisplayRefreshRateFeature.featureId, report);
            if (refresh is QuestDisplayRefreshRateFeature r)
            {
                r.requestedRate = 90f;
                EditorUtility.SetDirty(r);
            }

            report.Add("XR: OpenXR Android renderMode SinglePassInstanced, depthSubmission None");
        }

        private static OpenXRFeature EnableFeature(BuildTargetGroup group, string featureId, List<string> report)
        {
            OpenXRFeature feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(group, featureId);
            if (feature == null)
            {
                throw new InvalidOperationException("XR feature NOT FOUND " + featureId);
            }

            feature.enabled = true;
            EditorUtility.SetDirty(feature);
            report.Add("XR: enabled " + featureId + " (" + feature.GetType().Name + ")");
            return feature;
        }

        private static void SetBool(SerializedObject so, string name, bool value)
        {
            SerializedProperty p = so.FindProperty(name);
            if (p != null) p.boolValue = value;
        }

    }
}
