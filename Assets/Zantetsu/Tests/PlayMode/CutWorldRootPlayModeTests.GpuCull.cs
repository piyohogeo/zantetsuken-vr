using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// VP Stage 3C on the product's own path (DESIGN 4.5.7): a world made by <see cref="CutWorldRoot"/> with the GPU
    /// selection asked for, drawn by <see cref="CutWorldCameraDrawing"/> for an ordinary camera that renders every
    /// frame, so the selection is put into URP's frame by the product's own route and nothing here issues it.
    /// <para>
    /// **The same scenes, taken twice.** One world draws as VP Stage 3 and one through the GPU selection; each goes
    /// through the same seven stages -- the whole body; the cut while its geometry is held back (the sides clipped,
    /// two-sided casters, the cut face drawn by the stencil caps); the committed children; a row of further bodies
    /// registered along the view, across every split of the shadow map and past its range; one piece retired, which
    /// leaves commands of no instance among the others (DESIGN 5.6); a body registered after it, taken at the end; and
    /// the compaction of the instance regions (D-202), run by the display on the product's own path once its interval,
    /// held off until then, is lowered for the case (the retirement and the cut left holes) -- and each stage is taken
    /// from the same five views: the bodies in view, the bodies out of view with their shadow
    /// in it, nothing in view, the first view again, and down the row. The display starts with room for two commands
    /// and two instances, so the body's GPU buffers are replaced by larger ones on the way. The fixture holds one world
    /// a case, so the two worlds are two cases and a third compares what they took; it fails, rather than passing, when
    /// either was not taken in this run.
    /// </para>
    /// <para>
    /// The second case also reads back -- which the product never does -- what the selection kept for each view: every
    /// instance where all are in view, none for the body and some as casters where only the shadow is in view, none
    /// for the body where nothing is, and fewer than all of both down the row, where bodies stand past the far plane
    /// and past the shadow's range.
    /// </para>
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const int GpuCullViews = 5;
        private const int GpuCullRowStage = 3;
        private static readonly string[] s_gpuCullStages = { "whole", "immediate", "committed", "row registered", "piece retired", "registered after the retirement", "compacted" };
        private static readonly string[] s_gpuCullViewNames = { "bodies in view", "only the shadow in view", "nothing in view", "bodies in view again", "down the row" };
        private static readonly Dictionary<bool, List<Color32[]>> s_gpuCullImages = new Dictionary<bool, List<Color32[]>>();

        // The same scenes through the Direct3D 12 plugin's route (DESIGN 4.5.8), taken in this run (null when not).
        private static List<Color32[]> s_gpuCullNativeImages;
        private static int s_gpuCullImageWidth, s_gpuCullImageHeight;

        [UnityTest, Order(1)]
        public IEnumerator GpuCull_1_VpStage3_TakesTheScenes()
        {
            yield return GpuCullScenes(false);
        }

        [UnityTest, Order(2)]
        public IEnumerator GpuCull_2_TheGpuSelection_TakesTheSameScenes_KeepingACasterTheCameraDoesNotSee()
        {
            yield return GpuCullScenes(true);
        }

        [Test, Order(3)]
        public void GpuCull_3_TheGpuSelectionsImages_AreVpStage3s()
        {
            Assert.That(s_gpuCullImages.ContainsKey(false), Is.True, "VP Stage 3's scenes were taken in this run");
            Assert.That(s_gpuCullImages.ContainsKey(true), Is.True, "the GPU selection's scenes were taken in this run");
            List<Color32[]> vp3 = s_gpuCullImages[false];
            List<Color32[]> culled = s_gpuCullImages[true];
            Assert.That(vp3.Count, Is.EqualTo(s_gpuCullStages.Length * GpuCullViews));
            Assert.That(culled.Count, Is.EqualTo(vp3.Count));
            var lines = new List<string>();
            int worst = 0;
            for (int i = 0; i < vp3.Count; i++)
            {
                int differing = 0;
                for (int p = 0; p < vp3[i].Length; p++)
                {
                    Color32 a = vp3[i][p];
                    Color32 b = culled[i][p];
                    if (Math.Abs(a.r - b.r) > 2 || Math.Abs(a.g - b.g) > 2 || Math.Abs(a.b - b.b) > 2)
                    {
                        differing++;
                    }
                }

                lines.Add(s_gpuCullStages[i / GpuCullViews] + ", " + s_gpuCullViewNames[i % GpuCullViews] + ": pixels differing " + differing);
                worst = Math.Max(worst, differing);
            }

            TestContext.Out.WriteLine(string.Join("\n", lines));
            Assert.That(worst, Is.Zero, "pixels differing between VP Stage 3 and the GPU selection:\n" + string.Join("\n", lines));
        }

        /// <summary>
        /// **What cannot select says so and draws as VP Stage 3.** A world asked for the GPU selection with a surface
        /// material whose shader has no variant for it is not made with it: the world is VP Stage 3's, and it names the
        /// reason, so that nothing measured in it is taken for the GPU selection's.
        /// </summary>
        [UnityTest, Order(4)]
        public IEnumerator GpuCull_4_AMaterialWithoutTheVariant_IsVpStage3_AndTheWorldSaysWhy()
        {
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpGpuCullSetup.Requested = true;
            try
            {
                Shader plain = Shader.Find("Universal Render Pipeline/Unlit");
                Assert.That(plain, Is.Not.Null);
                Shader vp = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
                CutWorldRoot root = NewWorld(
                    out Shader _, null, null, null,
                    world => SetPrivate(
                        world, "materials",
                        new[]
                        {
                            new CutWorldRoot.MaterialBinding { sourceIndex = SideMaterial, material = Track(new Material(vp) { name = "side" }) },
                            new CutWorldRoot.MaterialBinding { sourceIndex = EndMaterial, material = Track(new Material(plain) { name = "end without the variant" }) },
                        }));
                Assert.That(root.Display.CullsOnGpu, Is.False, "the world draws as VP Stage 3");
                Assert.That(root.GpuCullFallback, Does.Contain("end without the variant").And.Contain(VpGpuCullSetup.Keyword), "and says why");
                AddBody(root, new Vector3(0f, 1.5f, 0f));
                yield return null;
                yield return null;
                Assert.That(root.Display.CommandCount, Is.GreaterThan(0), "and goes on as a world does");
                Assert.That(GpuCullMaterialCopies(), Is.Zero, "nothing made for the selection is left");
                yield return EndWorld(root);
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
            }
        }

        /// <summary>
        /// **The same scenes a third time, through the plugin** (2026-10-08, before the first city walk by the plugin's
        /// route): the body and its casters drawn by the Direct3D 12 plugin from the GPU selection's argument entries,
        /// the stencil caps by Unity's draws as ever -- through the cut held back, the commit, the row, the retirement,
        /// the late body, the replaced GPU buffers and the compaction. Direct3D 12 only.
        /// </summary>
        [UnityTest, Order(5)]
        public IEnumerator GpuCull_5_ThePluginsRoute_TakesTheSameScenes()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return GpuCullScenes(true, true);
        }

        /// <summary>
        /// The plugin's pictures against the Unity-API route's, scene by scene and view by view: how many pixels differ
        /// by more than 2 of 255 in a channel, and by more than 16. The two routes run the same shaders over the same
        /// geometry and selection; what may differ is the casters' depth bias (the plugin's is its own, DESIGN 4.5.8),
        /// which moves shadow edges by a pixel. Held: no view differs in more than a hundredth of its pixels by more
        /// than 16 -- a body, a clipped side or a cap missing or misplaced is far more than that. The counts are written
        /// for every view, and both sets of pictures are saved for looking at.
        /// </summary>
        [Test, Order(6)]
        public void GpuCull_6_ThePluginsImages_AreTheGpuSelections()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType);
            }

            Assert.That(s_gpuCullImages.ContainsKey(true), Is.True, "the GPU selection's scenes were taken in this run");
            Assert.That(s_gpuCullNativeImages, Is.Not.Null, "the plugin's scenes were taken in this run");
            List<Color32[]> unity = s_gpuCullImages[true];
            List<Color32[]> plugin = s_gpuCullNativeImages;
            Assert.That(plugin.Count, Is.EqualTo(unity.Count));
            string folder = System.IO.Path.Combine(Application.temporaryCachePath, "gpu-cull-plugin-pictures");
            System.IO.Directory.CreateDirectory(folder);
            var lines = new List<string>();
            double worstShare = 0;
            for (int i = 0; i < unity.Count; i++)
            {
                int small = 0, large = 0, litUnity = 0, litPlugin = 0;
                for (int q = 0; q < unity[i].Length; q++)
                {
                    Color32 a = unity[i][q];
                    Color32 b = plugin[i][q];
                    int d = Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
                    small += d > 2 ? 1 : 0;
                    large += d > 16 ? 1 : 0;
                    litUnity += a.r + a.g + a.b > 24 ? 1 : 0;
                    litPlugin += b.r + b.g + b.b > 24 ? 1 : 0;
                }

                double share = (double)large / unity[i].Length;
                worstShare = Math.Max(worstShare, share);
                string label = s_gpuCullStages[i / GpuCullViews] + ", " + s_gpuCullViewNames[i % GpuCullViews];
                lines.Add(label + ": pixels differing by more than 2: " + small + ", by more than 16: " + large + " of " + unity[i].Length
                    + " (" + (100.0 * share).ToString("F3") + "%); drawn pixels Unity-API " + litUnity + " / plugin " + litPlugin);
                if (s_gpuCullImageWidth > 0)
                {
                    foreach ((string name, Color32[] pixels) in new[] { ("unity", unity[i]), ("plugin", plugin[i]) })
                    {
                        var picture = new Texture2D(s_gpuCullImageWidth, s_gpuCullImageHeight, TextureFormat.RGBA32, false);
                        picture.SetPixels32(pixels);
                        picture.Apply(false);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, (i / GpuCullViews) + "-" + (i % GpuCullViews) + "-" + name + ".png"), picture.EncodeToPNG());
                        Object.DestroyImmediate(picture);
                    }
                }
            }

            TestContext.Out.WriteLine("pictures saved in " + folder + " (<stage>-<view>-unity|plugin.png)\n" + string.Join("\n", lines));
            Assert.That(worstShare, Is.LessThan(0.01), "a view differs in a hundredth of its pixels or more by more than 16:\n" + string.Join("\n", lines));
        }

        // The cut scene of the draw comparison (Zantetsu.Sandbox.SandboxDrawCompareCutScene), by route (native: the
        // plugin's) and by what the display uploaded for (doubled: Single Pass Instanced, two instances a body).
        private const int CutSceneViews = 2;
        // placed: the two bodies are placed props, in the world from their first cut (a city's); otherwise registered beforehand.
        private static readonly Dictionary<(bool native, bool doubled, bool placed), List<Color32[]>> s_cutSceneImages = new Dictionary<(bool, bool, bool), List<Color32[]>>();
        private static readonly Dictionary<(bool native, bool doubled, bool placed), List<string>> s_cutSceneStates = new Dictionary<(bool, bool, bool), List<string>>();

        /// <summary>
        /// **The draw comparison's cut scene, in the Editor** (2026-10-09, before it is run in a Player for D-215): the
        /// stages a Player pictures of two props of a city are made here of two bodies by the same code -- synthetic
        /// sweeps through the ordinary detector, the cuts' geometry held back and let go, every piece made kinematic
        /// and put where the scene says, one piece cut again, one retired -- by Unity's calls. Held: every stage is
        /// made, and the picture of each cut, move and retirement differs from the stage's before it (one the picture
        /// does not show would compare nothing; a commit draws the committed faces where the provisional caps were,
        /// and is not held to differ).
        /// </summary>
        [UnityTest, Order(7)]
        public IEnumerator GpuCull_7_TheCutScene_ByUnitysCalls_TakesItsStages()
        {
            yield return CutSceneCase(false);
        }

        /// <summary>The same stages by the plugin's route. Direct3D 12 only.</summary>
        [UnityTest, Order(8)]
        public IEnumerator GpuCull_8_TheCutScene_ByThePlugin_TakesTheSameStages()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return CutSceneCase(true);
        }

        /// <summary>
        /// The two routes' cut scenes against each other: the same line of state at every stage (what the ledger and
        /// the display hold, where every piece was put), and the same picture by the rule of the scenes above (no view
        /// differs in a hundredth of its pixels or more by more than 16 of 255).
        /// </summary>
        [Test, Order(9)]
        public void GpuCull_9_TheCutScenesPictures_AreTheSameByBothRoutes()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType);
            }

            CompareCutScenes((false, false, false), "unity", (true, false, false), "plugin");
        }

        /// <summary>
        /// **The cut scene as a picture camera of an XR session takes it, by Unity's calls** (2026-10-09, after a city
        /// walk by the plugin took its own pictures without the bodies): the display uploads for Single Pass Instanced
        /// -- two instances a body, as under an XR device -- and the camera that draws is this fixture's, a single
        /// view. Held: every stage is made, and its pictures are those of the scene uploaded one instance a body
        /// (the shader drops the second copy of each instance in a single view).
        /// </summary>
        [UnityTest, Order(10)]
        public IEnumerator GpuCull_10_TheCutScene_UploadedForBothEyes_InASingleViewCamera_ByUnitysCalls_IsTheSamePicture()
        {
            yield return CutSceneCase(false, true);
            CompareCutScenes((false, false, false), "unity", (false, true, false), "unity-doubled");
        }

        /// <summary>
        /// The same by the plugin's route, which until 2026-10-09 issued no body for such a camera: its pictures are the
        /// plugin's of the scene uploaded one instance a body, and those of Unity's calls from the doubled upload; the
        /// route counts the bodies as issued from doubled arguments and no failure. Direct3D 12 only.
        /// </summary>
        [UnityTest, Order(11)]
        public IEnumerator GpuCull_11_TheCutScene_UploadedForBothEyes_InASingleViewCamera_ByThePlugin_IsTheSamePicture()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return CutSceneCase(true, true);
            CompareCutScenes((true, false, false), "plugin", (true, true, false), "plugin-doubled");
            CompareCutScenes((false, true, false), "unity-doubled", (true, true, false), "plugin-doubled");
        }

        /// <summary>
        /// **The cut scene of two placed props, by Unity's calls** (2026-10-09, after the first run in a Player stopped at
        /// its second body): the bodies are placed props that are cut targets only until their first cut, as a city's
        /// are -- nothing of them in the world's storage, display or physics before it, their display geometry appended
        /// by the hit. The cases above register their bodies beforehand and could not show that the storage takes no
        /// new geometry while a cut's output is outstanding. Held: every stage is made. Ignored where the licensed
        /// input is absent.
        /// </summary>
        [UnityTest, Order(12)]
        public IEnumerator GpuCull_12_TheCutScene_OfPlacedPropsCutAtTheirFirstHit_ByUnitysCalls_TakesItsStages()
        {
            if (!System.IO.File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            yield return CutSceneCase(false, false, true);
        }

        /// <summary>The same by the plugin's route, and against the pictures of Unity's calls. Direct3D 12 only.</summary>
        [UnityTest, Order(13)]
        public IEnumerator GpuCull_13_TheCutScene_OfPlacedPropsCutAtTheirFirstHit_ByThePlugin_IsTheSamePicture()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            if (!System.IO.File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            yield return CutSceneCase(true, false, true);
            CompareCutScenes((false, false, true), "unity-placed", (true, false, true), "plugin-placed");
        }

        // Two takings of the cut scene against each other: the same line of state at every stage, and no view differing
        // in a hundredth of its pixels or more by more than 16 of 255. Both must have been taken in this run.
        private static void CompareCutScenes((bool native, bool doubled, bool placed) first, string firstName, (bool native, bool doubled, bool placed) second, string secondName)
        {
            Assert.That(s_cutSceneImages.ContainsKey(first), Is.True, "the cut scene '" + firstName + "' was taken in this run");
            Assert.That(s_cutSceneImages.ContainsKey(second), Is.True, "the cut scene '" + secondName + "' was taken in this run");
            Assert.That(s_cutSceneStates[second], Is.EqualTo(s_cutSceneStates[first]), "the same states were pictured (" + firstName + " / " + secondName + ")");
            List<Color32[]> a = s_cutSceneImages[first], b = s_cutSceneImages[second];
            Assert.That(b.Count, Is.EqualTo(a.Count));
            string folder = System.IO.Path.Combine(Application.temporaryCachePath, "cut-scene-pictures");
            System.IO.Directory.CreateDirectory(folder);
            var lines = new List<string>();
            double worstShare = 0;
            for (int i = 0; i < a.Count; i++)
            {
                int small = 0, large = 0, drawn = 0;
                for (int q = 0; q < a[i].Length; q++)
                {
                    Color32 x = a[i][q];
                    Color32 y = b[i][q];
                    int d = Math.Max(Math.Abs(x.r - y.r), Math.Max(Math.Abs(x.g - y.g), Math.Abs(x.b - y.b)));
                    small += d > 2 ? 1 : 0;
                    large += d > 16 ? 1 : 0;
                    drawn += y.r + y.g + y.b > 24 ? 1 : 0;
                }

                double share = (double)large / a[i].Length;
                worstShare = Math.Max(worstShare, share);
                string stage = Zantetsu.Sandbox.SandboxDrawCompareCutScene.StageNames[i / CutSceneViews];
                lines.Add(stage + ", view " + (i % CutSceneViews) + ": pixels differing by more than 2: " + small + ", by more than 16: " + large + " of " + a[i].Length + "; drawn pixels of " + secondName + " " + drawn);
                foreach ((string name, Color32[] pixels) in new[] { (firstName, a[i]), (secondName, b[i]) })
                {
                    var picture = new Texture2D(s_gpuCullImageWidth, s_gpuCullImageHeight, TextureFormat.RGBA32, false);
                    picture.SetPixels32(pixels);
                    picture.Apply(false);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, stage + "-view" + (i % CutSceneViews) + "-" + name + ".png"), picture.EncodeToPNG());
                    Object.DestroyImmediate(picture);
                }
            }

            TestContext.Out.WriteLine(firstName + " against " + secondName + "; pictures saved in " + folder + " (<stage>-view<n>-<name>.png)\n" + string.Join("\n", lines));
            Assert.That(worstShare, Is.LessThan(0.01), firstName + " against " + secondName + ": a view differs in a hundredth of its pixels or more by more than 16:\n" + string.Join("\n", lines));
        }

        private IEnumerator CutSceneCase(bool native, bool doubled = false, bool placed = false)
        {
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpBodyRouteRequest nativeBefore = VpNativeDrawSetup.Request;
            bool? stereoBefore = ProvisionalCutDriver.SinglePassInstancedForTest;
            VpNativeDrawSetup.Request = native ? VpBodyRouteRequest.Native : VpBodyRouteRequest.UnityApi;
            VpGpuCullSetup.Requested = true;
            // The display uploads two instances a body, as under a Single Pass Instanced device, when the case says so.
            if (doubled) ProvisionalCutDriver.SinglePassInstancedForTest = true;
            try
            {
                s_cutSceneImages.Remove((native, doubled, placed));
                s_cutSceneStates.Remove((native, doubled, placed));
                Shader casterShader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Assert.That(casterShader, Is.Not.Null, "the display's shadow caster is in this project");
                Material stable = Track(new Material(casterShader) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(casterShader) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);

                HoldingExecutor geometryPool = null;
                CutWorldRoot root = NewWorld(
                    out Shader _,
                    destination => destination == WorkDestination.GeometryPool
                        ? geometryPool = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                        : null,
                    null,
                    SmallDisplayRoom(1024),
                    world =>
                    {
                        SetPrivate(world, "shadowMaterial", stable);
                        SetPrivate(world, "provisionalShadowMaterial", immediate);
                        // A placed prop's display input names source material 0, as the walk city's world binds it.
                        if (placed) BindSourceZero(world);
                    });
                Assert.That(geometryPool, Is.Not.Null);
                root.Driver.RemainingMainSeconds = () => 1.0;
                root.Display.CompactionMinimumInterval = int.MaxValue;
                Assert.That(root.GpuCullFallback, Is.Null, "the world did not fall back");
                Assert.That(root.Display.NativeArguments, Is.EqualTo(native), "the world draws by the route this case asked for: " + root.BodyRoute + " (" + root.BodyRouteReason + ")");
                if (native) Assert.That(root.NativeDrawFailure, Is.Null, root.NativeDrawFailure);

                ShadowStage stage = NewShadowStage();
                GameObject ground = GameObject.Find("Plane");
                Assert.That(ground, Is.Not.Null, "the stage's ground");
                ground.transform.localScale = new Vector3(14f, 1f, 14f);
                stage.camera.orthographic = false;
                stage.camera.fieldOfView = 50f;
                stage.camera.nearClipPlane = 0.1f;
                stage.camera.farClipPlane = 60f;
                stage.camera.enabled = true;
                var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
                SetPrivate(drawing, "world", root);
                SetPrivate(drawing, "cameras", new[] { stage.camera });

                var bodies = new Zantetsu.Sandbox.SandboxDrawCompareCutScene.Body[2];
                Vector3[] at = { new Vector3(-2.5f, 3f, 0f), new Vector3(2.5f, 3f, 0f) };
                var detector = new SlashHitDetector(root, in k_hitSettings);
                for (int i = 0; placed && i < 2; i++)
                {
                    // The prop as placed: a cut target only (nothing of it in the world), the box its convexes fill.
                    var data = JsonUtility.FromJson<Zantetsu.Sandbox.PlacedCuttableInput>(System.IO.File.ReadAllText(RefusalPropInputPath));
                    at[i] = new Vector3(i == 0 ? -1.6f : 1.6f, 1.2f, 0f);
                    GameObject instance = RefusalInstance("bench_001 cut scene " + i, at[i], out Renderer[] renderers, out Collider[] colliders);
                    Zantetsu.Sandbox.PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                    detector.AddPlaced(candidate);
                    Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
                    foreach (Zantetsu.Sandbox.PlacedCuttableInput.Hull hull in data.hulls)
                    {
                        foreach (Vector3 v in hull.vertices)
                        {
                            lo = Vector3.Min(lo, v);
                            hi = Vector3.Max(hi, v);
                        }
                    }

                    var box = new Bounds(at[i] + 0.5f * (lo + hi), hi - lo);
                    bodies[i] = new Zantetsu.Sandbox.SandboxDrawCompareCutScene.Body { name = i == 0 ? "first" : "second", position = at[i], rotation = Quaternion.identity, bounds = box };
                    Assert.That(candidate.Source.IsSet, Is.False, "no fragment of the prop before a hit");
                }

                for (int i = 0; !placed && i < 2; i++)
                {
                    LogicalFragmentId fragment = AddBody(root, at[i]);
                    Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True);
                    owner.Body.isKinematic = true;
                    Assert.That(Zantetsu.Sandbox.SandboxDrawCompareCutScene.TryWorldBounds(root, fragment, out Bounds box), Is.True, "the body has a collider to sweep through");
                    bodies[i] = new Zantetsu.Sandbox.SandboxDrawCompareCutScene.Body { name = i == 0 ? "first" : "second", position = owner.Root.transform.position, rotation = owner.Root.transform.rotation, bounds = box };
                }

                yield return null;
                yield return null;
                var images = new List<Color32[]>();
                var states = new List<string>();
                var failures = new List<string>();
                var io = new Zantetsu.Sandbox.SandboxDrawCompareCutScene.Io
                {
                    world = root,
                    detector = detector,
                    hold = holding =>
                    {
                        geometryPool.HoldEverything = holding;
                        if (!holding) geometryPool.ReleaseEverything();
                    },
                    held = () => geometryPool.HoldingCount,
                    pictures = name => CutScenePictures(stage, images, placed),
                    log = line => TestContext.Out.WriteLine(line),
                };
                yield return Zantetsu.Sandbox.SandboxDrawCompareCutScene.Stages(io, bodies[0], bodies[1], states, failures);
                TestContext.Out.WriteLine((native ? "plugin's route" : "Unity's calls") + (doubled ? ", uploaded for Single Pass Instanced" : "") + ":\n" + string.Join("\n", states));
                Assert.That(root.Display.DrawsSinglePassInstanced, Is.EqualTo(doubled), "the body was uploaded " + (doubled ? "two instances a body" : "one instance a body"));
                Assert.That(failures, Is.Empty, string.Join("; ", failures));
                Assert.That(states.Count, Is.EqualTo(Zantetsu.Sandbox.SandboxDrawCompareCutScene.StageNames.Length), "every stage was made");
                Assert.That(images.Count, Is.EqualTo(states.Count * CutSceneViews));

                // Each stage is in its pictures: one view at least differs from the stage's before it.
                for (int i = 1; i < states.Count; i++)
                {
                    int most = 0;
                    for (int v = 0; v < CutSceneViews; v++)
                    {
                        Color32[] now = images[i * CutSceneViews + v], before = images[(i - 1) * CutSceneViews + v];
                        int differing = 0;
                        for (int q = 0; q < now.Length; q++)
                        {
                            differing += Math.Abs(now[q].r - before[q].r) > 16 || Math.Abs(now[q].g - before[q].g) > 16 || Math.Abs(now[q].b - before[q].b) > 16 ? 1 : 0;
                        }

                        most = Math.Max(most, differing);
                    }

                    // A commit alone is not held to this: the committed faces are drawn where the provisional caps were,
                    // and a picture that hardly changes is what it should give (its line of state is what tells it
                    // happened). The third stage is more than the commit: the second body is cut in it.
                    bool commit = i == 5;
                    TestContext.Out.WriteLine(Zantetsu.Sandbox.SandboxDrawCompareCutScene.StageNames[i] + ": pixels differing from the stage before, in the view that differs most: " + most + (commit ? " (a commit: not held to differ)" : ""));
                    if (!commit) Assert.That(most, Is.GreaterThan(40), Zantetsu.Sandbox.SandboxDrawCompareCutScene.StageNames[i] + " is in the picture");
                }

                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                if (native)
                {
                    Zantetsu.Rendering.Urp.VpNativeDrawRoute pluginRoute = drawing.NativeRoute;
                    Assert.That(pluginRoute, Is.Not.Null, "the drawing put the plugin's pass in the frame");
                    TestContext.Out.WriteLine("plugin's route: " + drawing.DescribeNativeRoute(out bool drewEverything) + "; drew everything " + drewEverything);
                    Assert.That(pluginRoute.Refused, Is.Zero, pluginRoute.LastFailure);
                    Assert.That(pluginRoute.RingFull, Is.Zero);
                    Assert.That(pluginRoute.PipelineFailures, Is.Zero);
                    Assert.That(drawing.NativeRefusals, Is.Zero, "no camera went undrawn for want of the plugin's pass");
                    Assert.That(drewEverything, Is.True, "every body taken was issued");
                    Assert.That(pluginRoute.BodiesIssued, Is.GreaterThan(10), "bodies were issued for the frames drawn");
                    if (doubled)
                    {
                        Assert.That(pluginRoute.SingleViewBodiesFromDoubled, Is.EqualTo(pluginRoute.BodiesIssued), "every body was issued for a single view from doubled arguments");
                    }
                    else
                    {
                        Assert.That(pluginRoute.SingleViewBodiesFromDoubled, Is.Zero, "no body was issued from doubled arguments");
                    }
                }

                s_cutSceneImages[(native, doubled, placed)] = images;
                s_cutSceneStates[(native, doubled, placed)] = states;
                stage.camera.enabled = false;
                yield return null;
                Object.Destroy(drawing);
                yield return null;
                yield return EndWorld(root);
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
                VpNativeDrawSetup.Request = nativeBefore;
                ProvisionalCutDriver.SinglePassInstancedForTest = stereoBefore;
                if (placed) DisposeCandidates();
            }
        }

        // One stage of the cut scene from two views: both bodies from the front, and from the side and above.
        private IEnumerator CutScenePictures(ShadowStage stage, List<Color32[]> images, bool near = false)
        {
            for (int view = 0; view < CutSceneViews; view++)
            {
                if (near)
                {
                    // The placed props are small: the same two directions, from nearer.
                    stage.camera.transform.position = view == 0 ? new Vector3(0.3f, 2.2f, -5.5f) : new Vector3(4.5f, 3.8f, -3f);
                    stage.camera.transform.LookAt(new Vector3(0.3f, 1.3f, 0f));
                }
                else
                {
                    stage.camera.transform.position = view == 0 ? new Vector3(0.5f, 4f, -11f) : new Vector3(9f, 7f, -6f);
                    stage.camera.transform.LookAt(new Vector3(0.5f, 2.5f, 0f));
                }

                yield return null;
                yield return null;
                yield return null;
                images.Add(ReadGpuCullTarget(stage.target));
            }
        }

        private static int GpuCullMaterialCopies()
        {
            int copies = 0;
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                copies += material.name.EndsWith(" (GPU culled)", StringComparison.Ordinal) ? 1 : 0;
            }

            return copies;
        }

        private IEnumerator GpuCullScenes(bool gpu, bool native = false)
        {
            // The route is the one a Player's arguments give (D-198): its ordinary arguments are the GPU selection, and
            // VP Stage 3 is asked for by its own argument. The plugin's route is asked for beside the GPU selection.
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpBodyRouteRequest nativeBefore = VpNativeDrawSetup.Request;
            VpNativeDrawSetup.Request = native ? VpBodyRouteRequest.Native : VpBodyRouteRequest.UnityApi;
            bool readBack = gpu && !native;   // the selection's counts are read back from the Unity-API route's arguments only
            VpGpuCullSetup.Read(
                gpu ? new[] { "player.exe", "-zantetsuCityWalk" } : new[] { "player.exe", "-zantetsuCityWalk", VpGpuCullSetup.Vp3Argument },
                out bool requested, out bool _);
            Assert.That(requested, Is.EqualTo(gpu), gpu ? "no argument of its own: the GPU selection" : "VP Stage 3 asked for");
            VpGpuCullSetup.Requested = requested;
            try
            {
                if (native) s_gpuCullNativeImages = null; else s_gpuCullImages.Remove(gpu);
                Shader casterShader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Assert.That(casterShader, Is.Not.Null, "the display's shadow caster is in this project");
                Material stable = Track(new Material(casterShader) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(casterShader) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);

                HoldingExecutor geometryPool = null;
                CutWorldRoot root = NewWorld(
                    out Shader _,
                    destination => destination == WorkDestination.GeometryPool
                        ? geometryPool = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                        : null,
                    null,
                    SmallDisplayRoom(1024),
                    world =>
                    {
                        SetPrivate(world, "shadowMaterial", stable);
                        SetPrivate(world, "provisionalShadowMaterial", immediate);
                    });
                Assert.That(geometryPool, Is.Not.Null);
                geometryPool.HoldEverything = true;
                root.Driver.RemainingMainSeconds = () => 1.0;
                root.Display.CompactionMinimumInterval = int.MaxValue;   // held off until the stage that is about it
                Assert.That(root.GpuCullFallback, Is.Null, "the world did not fall back");
                Assert.That(root.Display.CullsOnGpu, Is.EqualTo(gpu), "the world draws by the route this case asked for");
                if (native)
                {
                    Assert.That(root.NativeDrawFailure, Is.Null, root.NativeDrawFailure);
                    Assert.That(root.TerminationRequested, Is.False);
                    Assert.That(root.Display.NativeArguments, Is.True, "the display's batch writes the plugin's argument entries");
                    TestContext.Out.WriteLine("plugin's route: the selection's own materials " + GpuCullMaterialCopies());
                }
                else
                {
                    Assert.That(GpuCullMaterialCopies(), gpu ? Is.EqualTo(4) : Is.Zero, "the selection's own materials: two surfaces and two casters");
                }

                // The stage of the shadow cases, its ground made large enough to take the row's shadows, with a camera
                // of the ordinary kind: enabled, drawn by the frame.
                ShadowStage stage = NewShadowStage();
                GameObject ground = GameObject.Find("Plane");
                Assert.That(ground, Is.Not.Null, "the stage's ground");
                ground.transform.localScale = new Vector3(14f, 1f, 14f);
                stage.camera.orthographic = false;
                stage.camera.fieldOfView = 50f;
                stage.camera.nearClipPlane = 0.1f;
                stage.camera.farClipPlane = 60f;
                stage.camera.enabled = true;
                var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
                SetPrivate(drawing, "world", root);
                SetPrivate(drawing, "cameras", new[] { stage.camera });

                LogicalFragmentId body = AddBody(root, new Vector3(0f, 4f, 0f));
                root.Lifetime.MarkLineage(body);
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner whole), Is.True);
                whole.Body.isKinematic = true;
                yield return null;
                yield return null;
                int replacementsAtFirst = root.Display.GpuReplacements;

                var images = new List<Color32[]>();
                var kept = new List<string>();
                yield return GpuCullTakeStage(root, stage, readBack, 0, images, kept);

                // The cut, with its geometry held back: the display keeps the two sides clipped (immediate). Two more
                // instances than the first room holds: the body's GPU buffers are replaced here.
                Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
                CutOperationId operation = default;
                yield return Until(
                    () =>
                    {
                        foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                        {
                            operation = candidate.Operation;
                        }

                        return operation.IsSet && geometryPool.HoldingCount > 0
                            && root.Ledger.TryGetOperation(operation, out LogicalCutOperation published)
                            && published.positive.IsSet;
                    },
                    "the children are published while the cut's geometry is held back");
                Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
                Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner upper), Is.True);
                Assert.That(root.Owners.TryGet(record.negative, out PhysicsFragmentOwner lower), Is.True);
                // Both sides are held where this case puts them, not where the frames between the publication and now
                // left them: the two worlds must draw the same scene, whatever their frame times were.
                upper.Body.isKinematic = true;
                lower.Body.isKinematic = true;
                lower.Root.transform.SetPositionAndRotation(new Vector3(0f, 4f, 0f), Quaternion.identity);
                upper.Root.transform.SetPositionAndRotation(new Vector3(2.5f, 4f, 0f), Quaternion.identity);
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, readBack, 1, images, kept);
                Assert.That(
                    root.Ledger.TryGetOperation(operation, out LogicalCutOperation stillOut)
                    && stillOut.state != LogicalCutOperationState.Completed, Is.True, "still immediate: not committed");
                Assert.That(CountTwoSidedCommands(root), Is.GreaterThan(0), "the clipped sides cast two-sided");
                Assert.That(root.Display.CapRecordCount, Is.GreaterThan(0), "and the cut face is a cap the stencil work draws");
                Assert.That(root.Display.GpuReplacements, Is.GreaterThan(replacementsAtFirst), "the body's GPU buffers were replaced by larger ones");

                // The geometry commits: the children are closed shapes.
                geometryPool.HoldEverything = false;
                geometryPool.ReleaseEverything();
                yield return Until(
                    () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation now)
                        && now.state == LogicalCutOperationState.Completed,
                    "the cut's geometry committed");
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, readBack, 2, images, kept);
                Assert.That(CountTwoSidedCommands(root), Is.Zero, "after the commit nothing casts two-sided");

                // A row of bodies registered along the view: from in front of the camera, through every split of the
                // shadow map, to past the far plane and past the shadow's range.
                float[] rowZ = { -2f, 2f, 6f, 10f, 14f, 18f, 22f, 26f, 30f, 34f, 38f, 42f, 75f, 95f };
                foreach (float z in rowZ)
                {
                    LogicalFragmentId rowBody = AddBody(root, new Vector3(-3f, 1.5f, z));
                    Assert.That(root.Owners.TryGet(rowBody, out PhysicsFragmentOwner rowOwner), Is.True);
                    rowOwner.Body.isKinematic = true;
                }

                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, readBack, GpuCullRowStage, images, kept);

                // One piece retired: what was drawn for it is drawn no more.
                int commandsBefore = root.Display.CommandCount;
                Assert.That(root.Lifetime.TryRetire(record.positive, out string refusal), Is.True, refusal);
                yield return null;
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, readBack, 4, images, kept);
                Assert.That(root.Display.CommandCount, Is.LessThan(commandsBefore), "the retired piece's commands are gone");

                // A body registered after the retirement (DESIGN 5.6): the retired piece left command slots among the
                // others' that are drawn through -- and selected through -- as commands of no instance; the new body is
                // taken at the end, not in them, and nothing of the retired piece is drawn again.
                int endBefore = root.Display.DrawCommandEnd;
                Assert.That(endBefore, Is.GreaterThan(root.Display.CommandCount), "the retired piece's command slots are still gone through, drawing nothing");
                // It stands to the side, in the first view and with its shadow outside the narrow one: that view's
                // measure of shadow takes the value most of the picture has for the lit ground.
                LogicalFragmentId late = AddBody(root, new Vector3(6f, 1.5f, 2f));
                Assert.That(root.Owners.TryGet(late, out PhysicsFragmentOwner lateOwner), Is.True);
                lateOwner.Body.isKinematic = true;
                yield return null;
                yield return null;
                yield return null;
                Assert.That(
                    root.Display.TryGetDrawSlots(late, out int lateStart, out int lateCommands, out int _, out int _, out int _), Is.True,
                    "the body registered after the retirement is drawn");
                TestContext.Out.WriteLine(
                    (gpu ? "GPU selection" : "VP Stage 3") + ": the late body's " + lateCommands + " commands from slot " + lateStart + "; the end was " + endBefore
                    + " and is " + root.Display.DrawCommandEnd + "; commands that draw " + root.Display.CommandCount);
                Assert.That(lateStart, Is.EqualTo(endBefore), "at the end, not in the slots the retired piece left");
                Assert.That(root.Display.DrawCommandEnd, Is.EqualTo(endBefore + lateCommands));
                Assert.That(root.Display.DrawCommandEnd, Is.GreaterThan(root.Display.CommandCount), "which are still gone through, drawing nothing");
                yield return GpuCullTakeStage(root, stage, readBack, 5, images, kept);

                // The compaction of the instance regions (D-202), on the product's path: the world's gate decides the
                // frame, the display whether it is a candidate -- the interval, held off until here, lowered to one
                // adopted collection; the retirement and the cut's regions left holes. The live records are laid out
                // again from the first in the order they were last written in, the tail comes down to them, every
                // command stays in its slot, and both routes draw the same five views as before.
                int instanceEndBefore = root.Display.DrawInstanceEnd, commandEndBefore = root.Display.DrawCommandEnd;
                Assert.That(root.Display.Compactions, Is.Zero, "the layout: none before this stage");
                Assert.That(instanceEndBefore, Is.GreaterThan(root.Display.DrawInstanceCount), "the layout: holes among the instance records");
                root.Display.CompactionMinimumInterval = 1;
                yield return Until(() => root.Display.Compactions > 0 || root.Display.CompactionsSkipped > 30, "the display compacts (or keeps being refused)");
                root.Display.CompactionMinimumInterval = int.MaxValue;
                TestContext.Out.WriteLine((gpu ? "GPU selection" : "VP Stage 3") + ": " + root.Display.DescribeCompaction());
                Assert.That(root.Display.Compactions, Is.EqualTo(1), "one compaction, on the product's path: " + root.Display.LastCompactionSkipReason);
                Assert.That(root.Display.DrawInstanceEnd, Is.EqualTo(root.Display.DrawInstanceCount), "the tail is the live records");
                Assert.That(root.Display.DrawInstanceEnd, Is.LessThan(instanceEndBefore), "and below where it was");
                Assert.That(root.Display.DrawCommandEnd, Is.EqualTo(commandEndBefore), "the command slots are not compacted");
                Assert.That(root.Display.LastCompactionOldestFrame, Is.LessThanOrEqualTo(root.Display.LastCompactionNewestFrame), "ordered by the frame each was last written in");
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, readBack, 6, images, kept);
                Assert.That(root.Display.Compactions, Is.EqualTo(1), "and not again within the interval");

                TestContext.Out.WriteLine((native ? "plugin's route" : gpu ? "GPU selection" : "VP Stage 3") + ":\n" + string.Join("\n", kept));
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                if (native)
                {
                    Zantetsu.Rendering.Urp.VpNativeDrawRoute pluginRoute = drawing.NativeRoute;
                    Assert.That(pluginRoute, Is.Not.Null, "the drawing put the plugin's pass in the frame");
                    TestContext.Out.WriteLine("plugin's route: " + drawing.DescribeNativeRoute(out bool drewEverything) + "; drew everything " + drewEverything
                        + "; display GPU replacements " + root.Display.GpuReplacements + ", compactions " + root.Display.Compactions);
                    Assert.That(pluginRoute.BodiesIssued, Is.GreaterThan(10), "bodies were issued for the frames drawn");
                    Assert.That(pluginRoute.Refused, Is.Zero, pluginRoute.LastFailure);
                    Assert.That(pluginRoute.RingFull, Is.Zero);
                    Assert.That(pluginRoute.PipelineFailures, Is.Zero);
                    Assert.That(drawing.NativeRefusals, Is.Zero, "no camera went undrawn for want of the plugin's pass");
                    Assert.That(drewEverything, Is.True, "every body taken was issued");
                }
                else if (gpu)
                {
                    VpGpuCullCameraRoute route = drawing.CullRoute;
                    Assert.That(route, Is.Not.Null, "the drawing put the selection in the frame");
                    TestContext.Out.WriteLine(
                        "route: enqueued " + route.Enqueued + ", recorded " + route.Recorded + ", refused " + route.Refused
                        + ", eyes " + route.LastEyeCount + ", splits " + route.LastSplitCount + ", body kept everything "
                        + route.BodyKeptEverything + ", casters kept everything " + route.CastersKeptEverything + " ("
                        + route.LastCastersKeptEverythingReason + "), frames without a main light shadow map "
                        + route.FramesWithoutMainShadowMap + " (" + route.LastWithoutMainShadowMapReason
                        + "), largest view difference " + route.LargestViewDifference
                        + "; display issued " + root.Display.CullSelectionsIssued + ", missed " + root.Display.CullSelectionsMissed
                        + ", dispatches " + root.Display.CullDispatches + ", bytes " + root.Display.CullViewBytes
                        + ", GPU replacements " + root.Display.GpuReplacements);
                    Assert.That(route.Enqueued, Is.GreaterThan(10), "a selection was enqueued for the frames drawn");
                    Assert.That(route.Recorded, Is.EqualTo(route.Enqueued), "and every one was recorded by the pipeline");
                    Assert.That(route.Refused, Is.Zero, "and issued by the display");
                    Assert.That(root.Display.CullSelectionsMissed, Is.Zero, "no frame drew from an earlier frame's selection");
                    Assert.That(drawing.CullRefusals, Is.Zero, "no camera went undrawn for want of a selection");
                    Assert.That(route.LastEyeCount, Is.EqualTo(1), "one eye: a camera with no XR pass");
                    Assert.That(route.LastSplitCount, Is.GreaterThan(0), "the casters were selected by the shadow map's splits");
                    Assert.That(route.CastersKeptEverything, Is.Zero, "in every frame that had a shadow map to select for");
                    Assert.That(
                        route.FramesWithoutMainShadowMap, Is.InRange(3L, 30L),
                        "the frames of the view with nothing in it: URP draws no main light shadow map for them");
                    Assert.That(route.BodyKeptEverything, Is.Zero);
                    Assert.That(route.LargestViewDifference, Is.Zero, "the selection's view was the rendered one");
                    Assert.That(root.Display.CullViewBytes, Is.GreaterThan(0L));
                }
                else
                {
                    Assert.That(drawing.CullRoute, Is.Null, "VP Stage 3 puts no selection in the frame");
                    Assert.That(root.Display.CullDispatches, Is.Zero);
                    Assert.That(root.Display.CullViewBytes, Is.Zero);
                }

                if (native) s_gpuCullNativeImages = images; else s_gpuCullImages[gpu] = images;
                stage.camera.enabled = false;
                yield return null;
                Object.Destroy(drawing);
                yield return null;
                yield return EndWorld(root);
                Assert.That(root.Display.IsDisposed, Is.True);
                Assert.That(root.Display.RetiredGpuObjects, Is.Zero, "every replaced GPU object released at the ending");
                yield return null;
                Assert.That(GpuCullMaterialCopies(), Is.Zero, "and the selection's own materials with them");
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
                VpNativeDrawSetup.Request = nativeBefore;
            }
        }

        // One stage from the five views. Each view is set, drawn by the frame, and read from the camera's target at the
        // next update; under the GPU selection what the selection kept for it is read back as well.
        private IEnumerator GpuCullTakeStage(CutWorldRoot root, ShadowStage stage, bool gpu, int stageIndex, List<Color32[]> images, List<string> kept)
        {
            for (int view = 0; view < GpuCullViews; view++)
            {
                stage.camera.fieldOfView = view == 1 ? 30f : 50f;
                switch (view)
                {
                    case 1:
                        // A narrow view from the side, down at where the light throws the bodies' shadow on the ground:
                        // the bodies themselves stand above its upper edge.
                        stage.camera.transform.position = new Vector3(1.15f, 2.5f, 9f);
                        stage.camera.transform.LookAt(new Vector3(1.15f, 0f, 2f));
                        break;
                    case 2:
                        // Up at the sky, away from everything.
                        stage.camera.transform.SetPositionAndRotation(new Vector3(0f, 5f, -9f), Quaternion.Euler(-60f, 180f, 0f));
                        break;
                    case 4:
                        stage.camera.transform.position = new Vector3(2f, 4f, -8f);
                        stage.camera.transform.LookAt(new Vector3(-3f, 1.5f, 20f));
                        break;
                    default:
                        stage.camera.transform.position = new Vector3(0f, 5f, -9f);
                        stage.camera.transform.LookAt(new Vector3(1.2f, 2f, 0f));
                        break;
                }

                yield return null;
                yield return null;
                yield return null;
                Color32[] image = ReadGpuCullTarget(stage.target);
                images.Add(image);
                string label = s_gpuCullStages[stageIndex] + ", " + s_gpuCullViewNames[view];
                int lit = 0;
                var histogram = new int[256];
                foreach (Color32 p in image)
                {
                    lit += p.r + p.g + p.b > 24 ? 1 : 0;
                    histogram[(p.r + p.g + p.b) / 3]++;
                }

                // The lit ground's value is the one most of the image has; what is clearly darker and not black is in shadow.
                int ground = 1;
                for (int v = 2; v < 256; v++)
                {
                    if (histogram[v] > histogram[ground]) ground = v;
                }

                int shadowed = 0;
                foreach (Color32 p in image)
                {
                    int value = (p.r + p.g + p.b) / 3;
                    shadowed += value > 8 && ground - value > 20 ? 1 : 0;
                }

                string line = label + ": drawn pixels " + lit + ", darker than the ground " + shadowed;
                TestContext.Out.WriteLine(
                    line + " (ground value " + ground + "; draw bounds " + root.Display.BodyBatchForTest.WorldBounds + "; commands " + root.Display.CommandCount
                    + " in " + root.Display.DrawCommandEnd + " slots, instances " + root.Display.DrawInstanceCount + " in " + root.Display.DrawInstanceEnd + " records)");
                if (view == 2)
                {
                    Assert.That(lit, Is.Zero, label + ": nothing is in this view");
                }
                else
                {
                    Assert.That(lit, Is.GreaterThan(500), label + ": the view shows the scene");
                }

                if (view == 1)
                {
                    Assert.That(shadowed, Is.GreaterThan(30), label + ": the shadow of bodies the camera does not see is in the view");
                }

                if (gpu)
                {
                    Assert.That(
                        root.Display.TryReadCullCountsForDiagnosis(stage.camera, out int instances, out int forwardKept, out int shadowKept),
                        Is.True, label + ": a selection was issued for the camera");
                    line += "; instances " + instances + ", body kept " + forwardKept + ", casters kept " + shadowKept;
                    Assert.That(instances, Is.GreaterThan(0), label);
                    bool row = stageIndex >= GpuCullRowStage;
                    switch (view)
                    {
                        case 1:
                            if (!row)
                            {
                                Assert.That(forwardKept, Is.Zero, label + ": the camera sees no body");
                            }

                            Assert.That(shadowKept, Is.GreaterThan(0), label + ": but what casts into its view is kept as a caster");
                            break;
                        case 2:
                            Assert.That(forwardKept, Is.Zero, label + ": the camera sees no body");
                            break;
                        case 4:
                            if (row)
                            {
                                Assert.That(forwardKept, Is.InRange(8, instances - 4), label + ": the row in view is kept, the bodies past the far plane are not");
                                Assert.That(shadowKept, Is.InRange(8, instances - 4), label + ": the casters in the shadow's range are kept, those past it are not");
                            }

                            break;
                        default:
                            if (!row)
                            {
                                Assert.That(forwardKept, Is.EqualTo(instances), label + ": every instance is in view");
                                Assert.That(shadowKept, Is.EqualTo(instances), label + ": and casts into it");
                            }
                            else
                            {
                                Assert.That(forwardKept, Is.GreaterThan(0), label);
                            }

                            break;
                    }
                }

                kept.Add(line);
            }
        }

        private Color32[] ReadGpuCullTarget(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var read = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            s_gpuCullImageWidth = target.width;
            s_gpuCullImageHeight = target.height;
            return read.GetPixels32();
        }
    }
}
