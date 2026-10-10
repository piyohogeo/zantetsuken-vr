using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The draw comparison's split scene (Zantetsu.Sandbox.SandboxDrawCompareSplitScene) in the Editor, before it is
    /// run in a Player: the stages a Player pictures round the spatial split of DESIGN 7.9.2 are made here by the
    /// same code -- the body made for it, the level cut through the trunk and the hand, the split held off and let
    /// through, the two pieces replaced by two objects each and put where the piece stood, one of them cut again with
    /// its geometry held back -- and pictured by this fixture's camera with the light's shadows. Held: every stage is
    /// made; **the picture after the split's publications is the picture before them** (no more of it differs than of
    /// two pictures of the one state before); and the stages that move or cut something are in their pictures.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const int SplitSceneViews = 2;

        [UnityTest]
        public IEnumerator SplitScene_ByUnitysCalls_TheSplitChangesNothingOfThePicture()
        {
            yield return SplitSceneCase(false, false);
        }

        /// <summary>The same by the plugin's route. Direct3D 12 only.</summary>
        [UnityTest]
        public IEnumerator SplitScene_ByThePlugin_TheSplitChangesNothingOfThePicture()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return SplitSceneCase(true, false);
        }

        /// <summary>The display uploads two instances a body, as under a Single Pass Instanced device; a single-view camera draws.</summary>
        [UnityTest]
        public IEnumerator SplitScene_UploadedForBothEyes_ByUnitysCalls_TheSplitChangesNothingOfThePicture()
        {
            yield return SplitSceneCase(false, true);
        }

        /// <summary>The same by the plugin's route. Direct3D 12 only.</summary>
        [UnityTest]
        public IEnumerator SplitScene_UploadedForBothEyes_ByThePlugin_TheSplitChangesNothingOfThePicture()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return SplitSceneCase(true, true);
        }

        /// <summary>
        /// **With another body whose cut is held** (2026-10-11): a second body beside the first is cut with its geometry
        /// held back -- provisional caps in the picture -- and the split is let through while it is held. A cut whose
        /// geometry is still to come does not hold the split: both pieces are split beside those caps. Held: the
        /// picture after the publications is the picture before them, the caps provisional in both; and what the
        /// commit that follows changes lies where the other body's own cut had changed the picture.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitScene_WithAnotherCutHeld_ByUnitysCalls_TheSplitBesideProvisionalCapsChangesNothingOfThePicture()
        {
            yield return SplitSceneCase(false, false, true);
        }

        /// <summary>The same by the plugin's route, uploaded for both eyes. Direct3D 12 only.</summary>
        [UnityTest]
        public IEnumerator SplitScene_WithAnotherCutHeld_UploadedForBothEyes_ByThePlugin_TheSplitBesideProvisionalCapsChangesNothingOfThePicture()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return SplitSceneCase(true, true, true);
        }

        private IEnumerator SplitSceneCase(bool native, bool doubled, bool another = false)
        {
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpBodyRouteRequest nativeBefore = VpNativeDrawSetup.Request;
            bool? stereoBefore = ProvisionalCutDriver.SinglePassInstancedForTest;
            VpNativeDrawSetup.Request = native ? VpBodyRouteRequest.Native : VpBodyRouteRequest.UnityApi;
            VpGpuCullSetup.Requested = true;
            if (doubled) ProvisionalCutDriver.SinglePassInstancedForTest = true;
            try
            {
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
                        // The body's display input names source material 0, as a placed prop's does.
                        BindSourceZero(world);
                    });
                Assert.That(geometryPool, Is.Not.Null);
                root.Driver.RemainingMainSeconds = () => 1.0;
                root.Display.CompactionMinimumInterval = int.MaxValue;
                root.SpatialSplit.Settings = SplitOn();
                root.EmptyOwners.Settings = EmptyOwnersOff();
                root.Groups.Settings = GroupsOff();
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

                var detector = new SlashHitDetector(root, in k_hitSettings);
                Assert.That(Zantetsu.Sandbox.SandboxDrawCompareSplitScene.TryMake(root, Vector3.zero, Quaternion.identity, out Zantetsu.Sandbox.SandboxDrawCompareSplitScene.Made body, out string refusal), Is.True, refusal);
                _disposables.Add(body);   // its shape is the world's to read until the world is released
                Zantetsu.Sandbox.SandboxDrawCompareSplitScene.Made other = null;
                if (another)
                {
                    Assert.That(Zantetsu.Sandbox.SandboxDrawCompareSplitScene.TryMake(root, Zantetsu.Sandbox.SandboxDrawCompareSplitScene.AnotherAt, Quaternion.identity, out other, out string otherRefusal, false), Is.True, otherRefusal);
                    _disposables.Add(other);
                }

                TestContext.Out.WriteLine(body.described);
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
                    pictures = name => SplitScenePictures(stage, images, another),
                    log = line => TestContext.Out.WriteLine(line),
                };
                CutSpatialSplit split = root.SpatialSplit;
                yield return Zantetsu.Sandbox.SandboxDrawCompareSplitScene.Stages(io, body, states, failures, other);
                string[] names = another ? Zantetsu.Sandbox.SandboxDrawCompareSplitScene.StageNamesWithAnother : Zantetsu.Sandbox.SandboxDrawCompareSplitScene.StageNames;
                TestContext.Out.WriteLine((native ? "plugin's route" : "Unity's calls") + (doubled ? ", uploaded for Single Pass Instanced" : "") + ":\n" + string.Join("\n", states));
                Assert.That(root.Display.DrawsSinglePassInstanced, Is.EqualTo(doubled), "the body was uploaded " + (doubled ? "two instances a body" : "one instance a body"));
                Assert.That(failures, Is.Empty, string.Join("; ", failures));
                Assert.That(states.Count, Is.EqualTo(names.Length), "every stage was made");
                Assert.That(images.Count, Is.EqualTo(states.Count * SplitSceneViews));
                Assert.That(split.Published, Is.EqualTo(2), "both pieces were split: " + split.Describe());

                // What each stage's pictures differ by from the stage's before it, in the view that differs most.
                var any = new int[names.Length];
                var large = new int[names.Length];
                for (int i = 1; i < names.Length; i++)
                {
                    for (int v = 0; v < SplitSceneViews; v++)
                    {
                        Color32[] now = images[(i * SplitSceneViews) + v], before = images[((i - 1) * SplitSceneViews) + v];
                        int differing = 0, more = 0;
                        for (int q = 0; q < now.Length; q++)
                        {
                            int d = Math.Max(Math.Abs(now[q].r - before[q].r), Math.Max(Math.Abs(now[q].g - before[q].g), Math.Abs(now[q].b - before[q].b)));
                            differing += d > 0 ? 1 : 0;
                            more += d > 16 ? 1 : 0;
                        }

                        any[i] = Math.Max(any[i], differing);
                        large[i] = Math.Max(large[i], more);
                    }

                    TestContext.Out.WriteLine(names[i] + ": pixels differing from the stage before, in the view that differs most: at all " + any[i] + ", by more than 16: " + large[i]);
                }

                int drawn = 0;
                foreach (Color32 p in images[SplitSceneViews]) drawn += p.r + p.g + p.b > 24 ? 1 : 0;
                Assert.That(drawn, Is.GreaterThan(400), "the layout: the cut body is in the first view's picture");
                Assert.That(large[1], Is.GreaterThan(40), names[1] + " is in the picture");
                Assert.That(large[2], Is.Zero, "the layout: two pictures of one state are the same picture");
                if (!another)
                {
                    // The split: nothing of the picture changed that two pictures of one state do not differ by.
                    Assert.That(large[3], Is.Zero, "the split's publications changed the picture (by more than 16 of 255)");
                    Assert.That(any[3], Is.LessThanOrEqualTo(any[2]), "the split's publications changed the picture by more than two pictures of one state differ");
                    Assert.That(large[4], Is.GreaterThan(40), names[4] + " is in the picture: the hands are objects of their own");
                    Assert.That(large[5], Is.GreaterThan(40), names[5] + " is in the picture");
                }
                else
                {
                    Assert.That(large[3], Is.GreaterThan(40), names[3] + " is in the picture: the other body cut, its geometry held");
                    Assert.That(large[4], Is.Zero, "the layout: two pictures of the held state are the same picture");
                    // The split, published beside the other body's provisional caps: nothing of the picture changed
                    // that two pictures of the held state do not differ by.
                    Assert.That(large[5], Is.Zero, "the split's publications beside provisional caps changed the picture (by more than 16 of 255)");
                    Assert.That(any[5], Is.LessThanOrEqualTo(any[4]), "the split's publications beside provisional caps changed the picture by more than two pictures of the held state differ");
                    // The commit that follows: what differs lies where the other body's own cut changed the picture
                    // (s2 -> s3, its pieces and their shadows), and nowhere else -- so nowhere the split's pieces stand.
                    // "Where" is taken three pixels wider than the pixels that changed: an edge drawn anew moves by less.
                    int elsewhere = 0, there = 0, width = s_gpuCullImageWidth, height = s_gpuCullImageHeight;
                    for (int v = 0; v < SplitSceneViews; v++)
                    {
                        Color32[] whole = images[(2 * SplitSceneViews) + v], cut = images[(3 * SplitSceneViews) + v];
                        Color32[] before = images[(5 * SplitSceneViews) + v], now = images[(6 * SplitSceneViews) + v];
                        for (int q = 0; q < now.Length; q++)
                        {
                            if (now[q].r == before[q].r && now[q].g == before[q].g && now[q].b == before[q].b) continue;
                            bool others = false;
                            for (int y = Math.Max(0, (q / width) - 3); y <= Math.Min(height - 1, (q / width) + 3) && !others; y++)
                            {
                                for (int x = Math.Max(0, (q % width) - 3); x <= Math.Min(width - 1, (q % width) + 3) && !others; x++)
                                {
                                    int n = (y * width) + x;
                                    others = whole[n].r != cut[n].r || whole[n].g != cut[n].g || whole[n].b != cut[n].b;
                                }
                            }

                            elsewhere += others ? 0 : 1;
                            there += others ? 1 : 0;
                        }
                    }

                    TestContext.Out.WriteLine(names[6] + ": pixels differing from the stage before, both views: where the other body's cut had changed the picture " + there + ", elsewhere " + elsewhere);
                    Assert.That(elsewhere, Is.Zero, "the other body's commit changed the picture outside what its cut had changed");
                    Assert.That(large[7], Is.GreaterThan(40), names[7] + " is in the picture: the hands are objects of their own");
                    Assert.That(large[8], Is.GreaterThan(40), names[8] + " is in the picture");
                }

                Assert.That(root.GeometryFaults, Is.Zero);
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
                }

                stage.camera.enabled = false;
                yield return null;
                Object.Destroy(drawing);
                yield return null;
                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
                VpNativeDrawSetup.Request = nativeBefore;
                ProvisionalCutDriver.SinglePassInstancedForTest = stereoBefore;
            }
        }

        // One stage of the split scene from two views: the body from the front, and from the right and above.
        private IEnumerator SplitScenePictures(ShadowStage stage, List<Color32[]> images, bool another = false)
        {
            for (int view = 0; view < SplitSceneViews; view++)
            {
                // With the other body (to the left) the same two directions, from further back, at the middle of the two.
                float middle = another ? -0.5f : 0.6f, back = another ? 1.6f : 0f;
                stage.camera.transform.position = view == 0 ? new Vector3(middle, 1.7f, -4.8f - back) : new Vector3(middle + 3.2f + back, 2.6f, -3.2f - back);
                stage.camera.transform.LookAt(new Vector3(middle, 1.1f, 0f));
                yield return null;
                yield return null;
                yield return null;
                images.Add(ReadGpuCullTarget(stage.target));
            }
        }
    }
}
