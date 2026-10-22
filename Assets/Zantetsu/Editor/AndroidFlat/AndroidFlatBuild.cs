using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using Zantetsu.EditorTools.Quest;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>Reproducible phone validation APK. Licensed content and the Quest build's settings are preserved.</summary>
    public static class AndroidFlatBuild
    {
        public const string ApplicationIdentifier = "com.zantetsuken.androidflat";
        public const string Define = "ZANTETSU_ANDROID_FLAT";
        public const string PrivateRoot = "Assets/Licensed/AndroidFlat";
        public const string ScenePath = PrivateRoot + "/AndroidFlatCity.unity";
        public const string PipelinePath = "Assets/Zantetsu/AndroidFlatSettings/AndroidFlat_RPAsset.asset";
        public const string RendererPath = "Assets/Zantetsu/AndroidFlatSettings/AndroidFlat_Renderer.asset";
        private const string ProfilePath = PrivateRoot + "/AndroidFlatProfile.asset";
        private const string NativePath = "Assets/Zantetsu/Runtime/Rendering/Plugins/Android/arm64-v8a/libZantetsuVpVulkan.so";

        [MenuItem("Zantetsu/Android phone/Configure validation scene")]
        public static void Configure()
        {
            EnsureFolder(PrivateRoot);
            EnsureFolder("Assets/Zantetsu/AndroidFlatSettings");
            ConfigurePipeline();
            string source = Argument("-zantetsuAndroidFlatSourceScene") ?? QuestStaticDemoBuild.SourceScene;
            if (!File.Exists(source)) throw new FileNotFoundException("The licensed city scene must be copied into this worktree first", source);
            CopyAsset(source, ScenePath);
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var worlds = Object.FindObjectsByType<CutWorldRoot>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (worlds.Length != 1 || worlds[0].Profile == null) throw new BuildFailedException("The phone scene needs one active CutWorldRoot with a profile");
            var world = worlds[0];
            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(ProfilePath);
            if (profile == null) { profile = Object.Instantiate(world.Profile); AssetDatabase.CreateAsset(profile, ProfilePath); }
            else EditorUtility.CopySerialized(world.Profile, profile);
            profile.name = "AndroidFlatProfile";
            var profileSettings = new SerializedObject(profile);
            profileSettings.FindProperty("geometryWorkerCount").intValue = 1;
            profileSettings.FindProperty("backgroundWorkerCount").intValue = 1;
            profileSettings.FindProperty("gpuVertexInitialCapacity").intValue = 1048576;
            profileSettings.FindProperty("gpuIndexInitialCapacity").intValue = 4194304;
            profileSettings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            var binding = new SerializedObject(world);
            binding.FindProperty("profile").objectReferenceValue = profile;
            binding.ApplyModifiedPropertiesWithoutUndo();
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            foreach (var scenario in Object.FindObjectsByType<ScenarioRenderPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            { scenario.pipeline = pipeline; EditorUtility.SetDirty(scenario); }
            // Include this pipeline through the scene even when its source did not carry a scenario override.
            if (Object.FindObjectsByType<ScenarioRenderPipeline>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0)
                new GameObject("Android phone rendering").AddComponent<ScenarioRenderPipeline>().pipeline = pipeline;
            var camera = Camera.main;
            if (camera == null) throw new BuildFailedException("The phone scene has no MainCamera");
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.nearClipPlane = 0.01f; camera.farClipPlane = 100f;
            camera.allowHDR = false; camera.allowMSAA = true;
            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.allowXRRendering = false;
            cameraData.renderPostProcessing = false;
            cameraData.SetRenderer(0);
            // This assembly does not need an Input System dependency just to stop authored HMD pose updates.
            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (behaviour != null && behaviour.GetType().FullName == "UnityEngine.InputSystem.XR.TrackedPoseDriver")
                { behaviour.enabled = false; EditorUtility.SetDirty(behaviour); }
            foreach (var skin in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            { skin.quality = SkinQuality.Bone4; skin.skinnedMotionVectors = false; EditorUtility.SetDirty(skin); }
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
            int cuttables = Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            if (cuttables == 0) throw new BuildFailedException("The phone scene has no placed city cuttables");
            if (Object.FindObjectsByType<MobPlanCrowd>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length != 1)
                throw new BuildFailedException("The phone scene needs one active city crowd");
            EditorUtility.SetDirty(camera); EditorUtility.SetDirty(cameraData);
            if (!EditorSceneManager.SaveScene(scene)) throw new BuildFailedException("The phone scene could not be saved");
            AssetDatabase.SaveAssets();
            Debug.Log("ANDROID FLAT CONFIGURE: " + source + " -> " + ScenePath + "; cuttables=" + cuttables
                + "; mono Vulkan, Quest HDR off/MSAA4/shadows; no XR/AppSW; FSR1 available at runtime; no recorded route required");
        }

        private static void ConfigurePipeline()
        {
            CopyAsset(QuestStaticDemoBuild.RendererPath, RendererPath);
            CopyAsset(QuestStaticDemoBuild.PipelinePath, PipelinePath);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null) throw new BuildFailedException("The Quest renderer could not be copied");
            renderer.name = "AndroidFlat_Renderer";
            foreach (var feature in renderer.rendererFeatures.ToArray())
            {
                renderer.rendererFeatures.Remove(feature);
                // Quest currently has only its XR diagnostic feature. Cloned features are local subassets.
                if (feature != null && AssetDatabase.GetAssetPath(feature) == RendererPath) Object.DestroyImmediate(feature, true);
            }
            var rso = new SerializedObject(renderer);
            rso.FindProperty("m_RenderingMode").intValue = (int)RenderingMode.Forward;
            rso.FindProperty("m_UseNativeRenderPass").boolValue = false;
            rso.FindProperty("m_DepthPrimingMode").intValue = (int)DepthPrimingMode.Disabled;
            rso.FindProperty("m_CopyDepthMode").intValue = (int)CopyDepthMode.AfterOpaques;
            rso.ApplyModifiedPropertiesWithoutUndo();
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            pipeline.name = "AndroidFlat_RPAsset";
            var pso = new SerializedObject(pipeline);
            var renderers = pso.FindProperty("m_RendererDataList");
            renderers.arraySize = 1; renderers.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            pso.FindProperty("m_DefaultRendererIndex").intValue = 0;
            pso.ApplyModifiedPropertiesWithoutUndo();
            pipeline.renderScale = 1f;
            pipeline.upscalingFilter = UpscalingFilterSelection.Linear; // First-run baseline; touch UI exposes FSR1.
            pipeline.supportsHDR = false; pipeline.msaaSampleCount = 4;
            EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(pipeline);
        }

        [MenuItem("Zantetsu/Android phone/Build validation APK")]
        public static void BuildApk()
        {
            try
            {
                if (!File.Exists(ScenePath)) throw new FileNotFoundException("Configure the Android phone scene first", ScenePath);
                if (!File.Exists(NativePath)) throw new FileNotFoundException("Build or copy the Android Vulkan native plugin first", NativePath);
                var native = AssetImporter.GetAtPath(NativePath) as PluginImporter;
                if (native == null || !native.GetCompatibleWithPlatform(BuildTarget.Android) || !native.isPreloaded)
                    throw new BuildFailedException("The Android Vulkan plugin must be Android-compatible and preloaded before device creation");
                string output = Path.GetFullPath(Argument("-zantetsuAndroidFlatOut") ?? "Build/android-flat.apk");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using (var scope = new BuildSettingsScope())
                {
                    var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                        scenes = new[] { ScenePath }, target = BuildTarget.Android, locationPathName = output,
                        extraScriptingDefines = new[] { Define },
                        options = HasArgument("-zantetsuAndroidFlatDev") ? BuildOptions.Development : BuildOptions.None
                    });
                    if (result.summary.result != BuildResult.Succeeded)
                        throw new BuildFailedException("Android phone build " + result.summary.result + "; errors=" + result.summary.totalErrors);
                    Debug.Log("ANDROID FLAT APK: " + output + "; bytes=" + result.summary.totalSize + "; define=" + Define);
                }
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void ConfigureAndBuild() { Configure(); BuildApk(); }

        // Snapshot serialized project settings and XR assets before applying build-only changes. The define is supplied
        // only to BuildPlayer, never to the project's scripting-define list. Restoration runs before batch failure exits.
        private sealed class BuildSettingsScope : IDisposable
        {
            private readonly List<(Object asset, string json)> _saved = new List<(Object, string)>();
            private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>();
            private readonly List<string> _shaderSourceImports = new List<string>();
            private readonly bool _bundle = EditorUserBuildSettings.buildAppBundle;
            private readonly bool _manifestSuppressed = QuestDistributionManifest.NonXrBuildInProgress;
            private readonly int _quality = QualitySettings.GetQualityLevel();
            private readonly bool _shaderAudit = AndroidFlatShaderVariantAudit.Enabled;
            private readonly VulkanDeviceFilterLists _originalVulkanFilters = PlayerSettings.Android.androidVulkanDeviceFilterListAsset;
            private VulkanDeviceFilterLists _vulkanFilters;
            private string _vulkanFilterPath;
            private bool _disposed;

            public BuildSettingsScope()
            {
                Save(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0], "ProjectSettings/ProjectSettings.asset");
                Save(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0], "ProjectSettings/GraphicsSettings.asset");
                Save(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0], "ProjectSettings/QualitySettings.asset");
                Save(AssetDatabase.LoadMainAssetAtPath("Assets/Settings/UniversalRenderPipelineGlobalSettings.asset"));
                Save(_originalVulkanFilters);
                try
                {
                    DisableXr();
                    QuestDistributionManifest.NonXrBuildInProgress = true;
                    PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
#pragma warning disable CS0618 // Preserve existing legacy filters when building the temporary current-API asset.
                    _vulkanFilters = CreatePhoneVulkanFilters(_originalVulkanFilters,
                        PlayerSettings.Android.androidVulkanAllowFilterList, PlayerSettings.Android.androidVulkanDenyFilterList);
#pragma warning restore CS0618
                    _vulkanFilterPath = AssetDatabase.GenerateUniqueAssetPath("Assets/AndroidFlatBuild.vulkandevicefilter");
                    AssetDatabase.CreateAsset(_vulkanFilters, _vulkanFilterPath);
                    PlayerSettings.Android.androidVulkanDeviceFilterListAsset = _vulkanFilters;
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                    PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
                    PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
                    PlayerSettings.SetMobileMTRendering(NamedBuildTarget.Android, true);
                    PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                    PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
                    PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel35;
                    PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;
                    PlayerSettings.Android.optimizedFramePacing = true;
                    PlayerSettings.Android.useCustomKeystore = false; // Local validation APK uses the Android debug signer.
                    PlayerSettings.Android.splitApplicationBinary = false;
                    PlayerSettings.colorSpace = ColorSpace.Linear;
                    PlayerSettings.enableFrameTimingStats = true;
                    PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
                    PlayerSettings.allowedAutorotateToPortrait = true;
                    PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
                    PlayerSettings.allowedAutorotateToLandscapeLeft = true;
                    PlayerSettings.allowedAutorotateToLandscapeRight = true;
                    var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                    for (int i = 0; i < QualitySettings.names.Length; i++)
                    { QualitySettings.SetQualityLevel(i, false); QualitySettings.renderPipeline = pipeline; QualitySettings.antiAliasing = 4; }
                    QualitySettings.SetQualityLevel(_quality, false);
                    ApplyPhoneShaderCompiler();
                    EnsureShaders();
                    AndroidFlatShaderVariantAudit.Enabled = HasArgument("-zantetsuAndroidFlatShaderAudit");
                    if (AndroidFlatShaderVariantAudit.Enabled) PlayerSettings.strictShaderVariantMatching = true;
                    EditorUserBuildSettings.buildAppBundle = false;
                    AssetDatabase.SaveAssets();
                    Debug.Log("ANDROID FLAT BUILD: ARM64 IL2CPP Release Vulkan; XR loaders/features off; optimized frame pacing on; portrait + both landscapes; Quest settings restored afterward");
                }
                catch { Dispose(); throw; }
            }

            private void Save(Object asset, string path = null)
            {
                if (asset == null) return;
                path ??= AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && !_files.ContainsKey(path))
                    _files.Add(path, File.ReadAllBytes(path));
                _saved.Add((asset, EditorJsonUtility.ToJson(asset)));
            }

            private void ApplyPhoneShaderCompiler()
            {
                string[] paths = {
                    "Assets/Zantetsu/Runtime/Rendering/VpIndexedIndirectUnlit.shader",
                    "Assets/Zantetsu/Runtime/Rendering/VpIndexedIndirectShadowCaster.shader",
                    "Assets/Zantetsu/Runtime/Rendering/VpStencilVolume.shader"
                };
                // Preserve every source before changing any. A failed write/import takes the constructor's
                // normal Dispose rollback, including a synchronous reimport of the restored source.
                foreach (string path in paths)
                {
                    if (!_files.ContainsKey(path)) _files.Add(path, File.ReadAllBytes(path));
                    _shaderSourceImports.Add(path);
                }
                var encoding = new UTF8Encoding(false, true);
                var target = new Regex(@"^([ \t]*)#pragma[ \t]+target[ \t]+4\.5[ \t]*(\r?\n)", RegexOptions.Multiline);
                int passes = 0;
                foreach (string path in paths)
                {
                    string source = encoding.GetString(_files[path]);
                    int matches = target.Matches(source).Count;
                    if (matches == 0) throw new BuildFailedException("Phone DXC scope found no target 4.5 pass in " + path);
                    // DXC fixes the non-VR Mali path. Keep Quest multiview and native shader bakes on their
                    // existing compiler outside this build scope; their OVR_multiview conversion differs.
                    string patched = target.Replace(source, "$0$1#pragma use_dxc vulkan$2");
                    File.WriteAllBytes(path, encoding.GetBytes(patched));
                    passes += matches;
                }
                ImportShaderSources(_shaderSourceImports);
                Debug.Log("ANDROID FLAT SHADERS: temporary Vulkan DXC for " + paths.Length + " sources / " + passes + " passes; original bytes and imports restored after build");
            }

            private static void ImportShaderSources(IEnumerable<string> paths)
            {
                List<Exception> failures = null;
                foreach (string path in paths)
                {
                    try { AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); }
                    catch (Exception error)
                    {
                        failures ??= new List<Exception>();
                        failures.Add(new InvalidOperationException("Shader source import failed: " + path, error));
                    }
                }
                if (failures != null) throw new AggregateException("Phone shader source imports failed", failures);
            }

            private void RestoreFilesAndShaderImports()
            {
                foreach (var file in _files) File.WriteAllBytes(file.Key, file.Value);
                try { ImportShaderSources(_shaderSourceImports); }
                finally
                {
                    // Reimport normally leaves settings alone. Retain the byte-exact settings guarantee even
                    // if an importer causes Unity to serialize settings while restoring its shader cache.
                    foreach (var file in _files)
                        if (file.Key.StartsWith("ProjectSettings/", StringComparison.Ordinal)) File.WriteAllBytes(file.Key, file.Value);
                }
            }

            private void DisableXr()
            {
                if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget targets) && targets != null)
                {
                    var general = targets.SettingsForBuildTarget(BuildTargetGroup.Android);
                    if (general != null)
                    {
                        Save(general); general.InitManagerOnStart = false; EditorUtility.SetDirty(general);
                        if (general.Manager != null)
                        {
                            Save(general.Manager);
                            general.Manager.automaticLoading = false; general.Manager.automaticRunning = false;
                            if (!general.Manager.TrySetLoaders(new List<XRLoader>())) throw new BuildFailedException("The Android XR loaders could not be removed for this build");
                            EditorUtility.SetDirty(general.Manager);
                        }
                    }
                }
                var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
                if (openXr == null) return;
                foreach (var feature in openXr.GetFeatures())
                { if (feature == null) continue; Save(feature); feature.enabled = false; EditorUtility.SetDirty(feature); }
            }

            private static void EnsureShaders()
            {
                var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
                var shaders = settings.FindProperty("m_AlwaysIncludedShaders");
                foreach (string name in new[] { "Zantetsu/VP Indexed Indirect Unlit", "Zantetsu/VP Indexed Indirect Shadow Caster", "Zantetsu/VP Stencil Init", "Zantetsu/VP Stencil Volume", "Zantetsu/VP Stencil Cap", "Hidden/Zantetsu/Android Draw Probe" })
                {
                    var shader = Shader.Find(name);
                    if (shader == null) throw new BuildFailedException("Shader missing: " + name);
                    bool found = false;
                    for (int i = 0; i < shaders.arraySize; i++) found |= shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader;
                    if (!found) shaders.GetArrayElementAtIndex(shaders.arraySize++).objectReferenceValue = shader;
                }
                settings.ApplyModifiedPropertiesWithoutUndo();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    for (int i = _saved.Count - 1; i >= 0; i--)
                    { EditorJsonUtility.FromJsonOverwrite(_saved[i].json, _saved[i].asset); EditorUtility.SetDirty(_saved[i].asset); }
                    QualitySettings.SetQualityLevel(_quality, false);
                }
                finally
                {
                    AndroidFlatShaderVariantAudit.Enabled = _shaderAudit;
                    PlayerSettings.Android.androidVulkanDeviceFilterListAsset = _originalVulkanFilters;
                    EditorUserBuildSettings.buildAppBundle = _bundle;
                    QuestDistributionManifest.NonXrBuildInProgress = _manifestSuppressed;
                    try { AssetDatabase.SaveAssets(); }
                    finally
                    {
                        // EditorJsonUtility can omit inactive-platform quality levels. Preserve the complete source
                        // settings byte-for-byte after the final native serialization, on success and on build failure.
                        try { RestoreFilesAndShaderImports(); }
                        finally
                        {
                            if (!string.IsNullOrEmpty(_vulkanFilterPath)) AssetDatabase.DeleteAsset(_vulkanFilterPath);
                            if (_vulkanFilters != null && !EditorUtility.IsPersistent(_vulkanFilters))
                                Object.DestroyImmediate(_vulkanFilters);
                        }
                    }
                }
            }
        }

#pragma warning disable CS0618 // Compatibility conversion preserves legacy user settings; new settings use VulkanDeviceFilterLists.
        private static VulkanDeviceFilterLists CreatePhoneVulkanFilters(VulkanDeviceFilterLists source,
            AndroidDeviceFilterData[] legacyAllow, AndroidDeviceFilterData[] legacyDeny)
        {
            // The constructor produces empty lists. Preserve the effective custom rules, or the pinned
            // engine defaults below, before adding the Pixel 6 family exception.
            legacyAllow ??= Array.Empty<AndroidDeviceFilterData>();
            legacyDeny ??= Array.Empty<AndroidDeviceFilterData>();
            var filters = new VulkanDeviceFilterLists("Android phone Vulkan filters");
            try
            {
                bool useDefaults = true;
                if (source != null)
                {
                    filters.vulkanGraphicsJobsDeviceFilters = source.vulkanGraphicsJobsDeviceFilters;
                    if (source.vulkanDeviceAllowFilters.Length + source.vulkanDeviceDenyFilters.Length > 0)
                    {
                        filters.vulkanDeviceAllowFilters = source.vulkanDeviceAllowFilters;
                        filters.vulkanDeviceDenyFilters = source.vulkanDeviceDenyFilters;
                        useDefaults = false;
                    }
                }
                // An empty asset falls through to the legacy pair, then to engine defaults at runtime.
                if (useDefaults && legacyAllow.Length + legacyDeny.Length > 0)
                {
                    filters.vulkanDeviceAllowFilters = legacyAllow.Select(ConvertVulkanFilter).ToArray();
                    filters.vulkanDeviceDenyFilters = legacyDeny.Select(ConvertVulkanFilter).ToArray();
                    useDefaults = false;
                }
                if (useDefaults)
                {
                    // Exact GetDefaultDeviceDenyList rule in this project's Android libunity.so.
                    // Unity exposes no public default-list getter. Recheck this snapshot on an engine upgrade
                    // so adding the Pixel 6 family never silently removes another device's default Deny.
                    if (Application.unityVersion != "6000.3.22f1")
                        throw new BuildFailedException("Revalidate Android phone Vulkan default Deny filters for Unity " + Application.unityVersion);
                    filters.vulkanDeviceDenyFilters = new[] {
                        new VulkanDeviceFilterData { brandName = "((G|g)oogle)", productName = "(oriole|raven|bluejay)" }
                    };
                }
                var allow = filters.vulkanDeviceAllowFilters.ToList();
                // Allow is evaluated before Deny. Match only Google Pixel 6 / 6 Pro / 6a.
                allow.Add(new VulkanDeviceFilterData { brandName = "^[Gg]oogle$", productName = "^(oriole|raven|bluejay)$" });
                filters.vulkanDeviceAllowFilters = allow.ToArray();
                filters.EnsureValidOrThrow();
                return filters;
            }
            catch { Object.DestroyImmediate(filters); throw; }
        }

        private static VulkanDeviceFilterData ConvertVulkanFilter(AndroidDeviceFilterData filter) => new VulkanDeviceFilterData {
            vendorName = filter.vendorName, deviceName = filter.deviceName, brandName = filter.brandName,
            productName = filter.productName, androidOsVersionString = filter.androidOsVersionString,
            vulkanApiVersionString = filter.vulkanApiVersionString, driverVersionString = filter.driverVersionString
        };
#pragma warning restore CS0618

        private static bool HasArgument(string key) => Array.IndexOf(Environment.GetCommandLineArgs(), key) >= 0;
        private static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException(key + " requires a value");
            return args[index + 1];
        }
        private static void CopyAsset(string source, string destination)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Source asset missing", source);
            if (File.Exists(destination)) AssetDatabase.DeleteAsset(destination);
            if (!AssetDatabase.CopyAsset(source, destination)) throw new BuildFailedException("Asset copy failed: " + source + " -> " + destination);
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
        }
    }
}
