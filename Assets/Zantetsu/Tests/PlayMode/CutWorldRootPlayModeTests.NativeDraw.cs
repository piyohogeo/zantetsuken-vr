using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The Direct3D 12 plugin's route (DESIGN 4.5.8) on the draw comparison's world: asked for, the display's bodies
    /// are drawn by the plugin -- one ExecuteIndirect a material run -- and the picture is the renderers' picture; and
    /// asked for where the plugin cannot draw, the world ends rather than draw by another route. The first case needs
    /// an Editor started with -force-d3d12 and says so when it is not; the second needs one that was not.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator NativeDraw_TheSameView_ByTheRenderers_AndByTheDisplayThroughThePlugin_IsTheSamePicture()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            bool requestedBefore = VpNativeDrawSetup.Requested;
            VpNativeDrawSetup.Requested = true;

            // An ambient probe with linear and quadratic terms and a default reflection with a picture in it, as a city
            // under a skybox gives (the mirror shots of natS2/vpS2 differed on the lit canopies, which a flat ambient
            // and a black reflection cannot show): what the plugin's per-draw constants pack must match Unity's.
            AmbientMode ambientBefore = RenderSettings.ambientMode;
            SphericalHarmonicsL2 probeBefore = RenderSettings.ambientProbe;
            DefaultReflectionMode reflectionBefore = RenderSettings.defaultReflectionMode;
            Texture customReflectionBefore = RenderSettings.customReflectionTexture;
            // Dim enough that the sun's shadows stay visible on the white ground and on the boxes (play-34, 2026-10-08:
            // with 0.25 + 1.2 + 0.7 the pictures were lit to white where shadows should be, and compared blind).
            var probe = new SphericalHarmonicsL2();
            probe.AddAmbientLight(new Color(0.06f, 0.07f, 0.09f));
            probe.AddDirectionalLight(new Vector3(0.3f, 1f, 0.2f).normalized, new Color(0.9f, 0.6f, 0.3f), 0.25f);
            probe.AddDirectionalLight(new Vector3(-0.8f, -0.2f, 0.5f).normalized, new Color(0.2f, 0.5f, 0.9f), 0.15f);
            var reflection = new Cubemap(16, TextureFormat.RGBA32, true);
            Color[] faceColours = { new Color(0.9f, 0.3f, 0.2f), new Color(0.2f, 0.9f, 0.3f), new Color(0.8f, 0.8f, 0.2f), new Color(0.2f, 0.2f, 0.2f), new Color(0.3f, 0.5f, 0.9f), new Color(0.9f, 0.9f, 0.9f) };
            var facePixels = new Color[16 * 16];
            for (int face = 0; face < 6; face++)
            {
                for (int i = 0; i < facePixels.Length; i++) facePixels[i] = faceColours[face] * (0.5f + 0.5f * (i % 16) / 15f);
                reflection.SetPixels(facePixels, (CubemapFace)face);
            }

            reflection.Apply(true);
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = probe;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = reflection;
            try
            {
                // The GPU geometry starts with room for exactly the two boxes (48 vertices, 72 indices): a third one
                // shown later replaces both buffers -- the update and the replacement the pictures below must follow.
                DrawCompareScene scene = NewDrawCompareScene(profile =>
                {
                    SetPrivate(profile, "gpuVertexInitialCapacity", 48);
                    SetPrivate(profile, "gpuIndexInitialCapacity", 72);
                });
                CutWorldRoot root = scene.root;
                yield return null;
                Assert.That(root.NativeDrawFailure, Is.Null, root.NativeDrawFailure);
                Assert.That(root.TerminationRequested, Is.False);
                Assert.That(root.Display.NativeArguments, Is.True, "the display's batch writes the plugin's argument entries");
                Assert.That(root.Display.CullsOnGpu, Is.True);

                ShadowStage stage = NewShadowStage();
                // The sun from the side (the stage's own sits behind the camera: every shadow fell behind the boxes, out
                // of the picture, and the pictures with shadows and without were the same -- play-37, 2026-10-08).
                stage.light.transform.rotation = Quaternion.Euler(50f, 120f, 0f);

                // The stage's ground casts nothing (NewShadowStage): the display's bodies are the scene's only casters,
                // so URP renders its shadow map only because the route tells Unity's culling where they are.
                Assert.That(GameObject.Find("Plane").GetComponent<MeshRenderer>().shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
                // A perspective camera: the view-dependent part of the shading (the specular highlight, through the
                // camera's position) is compared too; an orthographic one never reads the position.
                stage.camera.orthographic = false;
                // Three poses: near (the objects in shadow cascade 0, 5 m off), far (cascade 2, 12 m off, a narrower
                // view) and farther (cascade 3, 25 m off, narrower still). The near pictures alone could not show a
                // caster split whose constants were never assembled (play-27, 2026-10-08: splits 1..3 all zero).
                void Near() { stage.camera.fieldOfView = 38f; stage.camera.transform.SetPositionAndRotation(new Vector3(-2f, 4f, -3f), Quaternion.LookRotation(new Vector3(2f, -3.6f, 3f))); }
                void Far() { stage.camera.fieldOfView = 22f; stage.camera.transform.SetPositionAndRotation(new Vector3(-6f, 5.4f, -9f), Quaternion.LookRotation(new Vector3(6f, -4.9f, 9f))); }
                void Farther() { stage.camera.fieldOfView = 12f; stage.camera.transform.SetPositionAndRotation(new Vector3(-12.4f, 11.2f, -18.6f), Quaternion.LookRotation(new Vector3(12.4f, -10.7f, 18.6f))); }
                Near();
                Assert.That(root.Display.ShownCount, Is.Zero);
                yield return null;

                // The renderers' pictures, with the light's shadows and without.
                Color32[] byRenderersShadowed = DrawCompareRenderersPicture(stage, LightShadows.Hard);
                yield return null;
                Color32[] byRenderersSoft = DrawCompareRenderersPicture(stage, LightShadows.Soft);
                yield return null;
                Far();
                Color32[] byRenderersSoftFar = DrawCompareRenderersPicture(stage, LightShadows.Soft);
                yield return null;
                Farther();
                Color32[] byRenderersSoftFarther = DrawCompareRenderersPicture(stage, LightShadows.Soft);
                yield return null;
                Near();
                // The display's surfaces alone receiving (the ground kept but not receiving shadows -- a frame without any Unity
                // renderer ended the Editor in play-35/36): what the boxes receive of each other's shadows.
                MeshRenderer groundRenderer = GameObject.Find("Plane").GetComponent<MeshRenderer>();
                groundRenderer.receiveShadows = false;
                Color32[] byRenderersNoGroundShadowed = DrawCompareRenderersPicture(stage, LightShadows.Hard);
                yield return null;
                Color32[] byRenderersNoGround = DrawCompareRenderersPicture(stage, LightShadows.None);
                groundRenderer.receiveShadows = true;
                yield return null;
                Color32[] byRenderers = DrawCompareRenderersPicture(stage, LightShadows.None);
                SceneDrawInventory inventory = SceneDrawInventory.Collect(scene.instances, root.MaterialBindings, new SceneDrawMeshBank());
                Assert.That(inventory.Refusals, Is.Empty);
                var display = new SceneDrawVpDisplay(root);
                foreach (SceneDrawTarget target in inventory.Targets) Assert.That(display.TryShow(target, out string why), Is.True, why);
                yield return null;

                // The plugin's: the body pass alone (no shadow map this frame), then the casters into URP's shadow map
                // and the body reading it.
                Color32[] byPlugin = null, byPluginShadowed = null;
                Debug.Log("native picture: byPlugin (None)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => byPlugin = d);
                yield return null;
                Zantetsu.Rendering.Urp.VpNativeDrawRoute route = stage.nativeRoute;
                Assert.That(route, Is.Not.Null, "the stage drew through the plugin's route");
                long bodiesUnshadowed = route.BodiesIssued, castersUnshadowed = route.CastersIssued;
                Assert.That(route.LastBodyVariant, Is.EqualTo("forward-plugin-noshadow"), "no shadow keyword while the light casts none");
                Assert.That(castersUnshadowed, Is.Zero, "no shadow map to cast into: " + route.LastCasterSkipReason);
                Debug.Log("native picture: byPluginShadowed (Hard)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byPluginShadowed = d);
                yield return null;
                yield return null;

                // Soft shadows too (the city's light is soft; the mirror shots of natS2/vpS2 differed on self-shadowed
                // canopies): the soft variant, the filtered sampling and the offsets URP sets.
                Color32[] byPluginSoft = null;
                Debug.Log("native picture: byPluginSoft (Soft)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Soft, d => byPluginSoft = d);
                yield return null;
                string softVariant = stage.nativeRoute.LastBodyVariant;
                yield return null;

                // The display's surfaces alone, by the plugin (the ground off), with the light's shadows and without.
                Color32[] byPluginNoGroundShadowed = null, byPluginNoGround = null;
                groundRenderer.receiveShadows = false;
                Debug.Log("native picture: byPluginNoGroundShadowed (Hard)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byPluginNoGroundShadowed = d);
                yield return null;
                Debug.Log("native picture: byPluginNoGround (None)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => byPluginNoGround = d);
                yield return null;
                groundRenderer.receiveShadows = true;
                yield return null;

                // The far poses: the plugin's casters of cascades 2 and 3 received by the plugin's bodies and the ground.
                Color32[] byPluginSoftFar = null, byPluginSoftFarther = null;
                Far();
                Debug.Log("native picture: byPluginSoftFar (Soft)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Soft, d => byPluginSoftFar = d);
                yield return null;
                Farther();
                Debug.Log("native picture: byPluginSoftFarther (Soft)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Soft, d => byPluginSoftFarther = d);
                yield return null;
                yield return null;

                // Diagnosis of the shadow path, as the shot run writes it (native-gpu-blocks.txt): the body's blocks as
                // the GPU assembled them for the farther soft picture (its cascade spheres say which cascade the
                // objects, 25 m off, lie in), and the caster's blocks of every split.
                {
                    string blocks = stage.nativeRoute.DescribeGpuBlocksForDiagnosis(stage.camera);
                    Assert.That(blocks, Is.Not.Null, "the diagnosis reads the body's GPU constants back");
                    TestContext.Out.WriteLine(blocks);
                    Assert.That(blocks, Does.Contain("Pixel LightShadows").And.Contain("_CascadeShadowSplitSpheres0=("), "the body's LightShadows block is described");
                    Assert.That(blocks, Does.Contain("caster variant caster-plugin").And.Contain("split 0 Vertex $Globals").And.Contain("split 3 Vertex $Globals"), "every caster split's block is described");
                    Assert.That(blocks, Does.Not.Contain("could not be read"), blocks);

                    // Every split's block must hold its own view-projection: an unwritten block (all zero) means the
                    // casters of that cascade were drawn with no projection at all, which the pictures above cannot
                    // show while their objects lie in cascade 0 (play-27, 2026-10-08: splits 1..3 read back as zero).
                    for (int split = 0; split < 4; split++)
                    {
                        int at = blocks.IndexOf("split " + split + " Vertex $Globals", System.StringComparison.Ordinal);
                        Assert.That(at, Is.GreaterThanOrEqualTo(0), "split " + split + " described");
                        int end = blocks.IndexOf('\n', at);
                        string line = blocks.Substring(at, end - at);
                        Assert.That(line, Does.Contain("unity_MatrixVP=(").And.Not.Contain("unity_MatrixVP=(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0)"), "split " + split + " has its view-projection");
                        Assert.That(line, Does.Not.Contain("_LightDirection=(0,0,0)"), "split " + split + " has the light direction");
                    }
                }

                Near();
                route.CollectResults();
                TestContext.Out.WriteLine(route.Describe());
                Assert.That(route.BodiesTaken, Is.EqualTo(7), "one body run a picture (near: none, hard, soft; ground off: hard, none; far and farther: soft)");
                Assert.That(softVariant, Does.EndWith("-soft"), "the soft picture's body used the soft variant");
                Assert.That(route.PipelineFailures, Is.Zero, route.LastFailure);
                Assert.That(route.BodiesIssued, Is.EqualTo(route.BodiesTaken), route.LastFailure);
                Assert.That(route.LastBodyVariant, Does.StartWith("forward-plugin-").And.Not.EqualTo("forward-plugin-noshadow"), "the shadowed picture's body reads the shadow map");
                Assert.That(route.CastersTaken, Is.EqualTo(7), "one caster run a picture (one-sided; nothing is cut)");
                Assert.That(route.LastSplitCount, Is.EqualTo(4), "four cascades, as the city's light has");
                Assert.That(route.CastersIssued, Is.EqualTo(5 * route.LastSplitCount), "the five shadowed pictures: one issue a split, each one ExecuteIndirect");
                Assert.That(route.CasterRunsNotDrawn, Is.EqualTo(2), "only the two unshadowed pictures' runs, for want of a shadow map");
                Assert.That(route.CasterBoundsDeclared, Is.EqualTo(7), "the display's caster bounds were told to Unity's culling for each picture");
                Assert.That(route.PointerAcquisitions, Is.LessThan(route.PointerHits + 40), "native pointers are asked once a resource, then kept");
                Assert.That(route.ShadowMapFormatRefusals, Is.Zero);
                Assert.That(route.RingFull, Is.Zero);
                Assert.That(route.Refused, Is.Zero, route.LastFailure);
                Assert.That(route.ResultsByKind[(int)VpNativeDrawPlugin.Result.Recorded], Is.EqualTo(route.BodiesIssued + route.CastersIssued), "the plugin recorded every issue");
                VpNativeDrawPlugin.Counters counters = VpNativeDrawPlugin.ReadCounters();
                TestContext.Out.WriteLine("plugin: " + counters);
                Assert.That(counters.drawsRecorded, Is.GreaterThanOrEqualTo((ulong)(route.BodiesIssued + route.CastersIssued)));

                // The receiving of shadows by the display's own surfaces, apart from the ground's (a Unity renderer):
                // the pixels the renderers' shadowed picture darkens against its unshadowed one, on object pixels that
                // are not ground, must be darkened in the plugin's shadowed picture too. The whole-picture statistics
                // below cannot show this (2026-10-08: the Player's plugin surfaces received no shadow while the ground did).
                {
                    int receiverPixels = 0, pluginReceives = 0, pluginMisses = 0;
                    for (int i = 0; i < byRenderersNoGround.Length; i++)
                    {
                        int unshadowed = Grey(byRenderersNoGround[i]), shadowedR = Grey(byRenderersNoGroundShadowed[i]);
                        int pluginUnshadowed = Grey(byPluginNoGround[i]), pluginShadowed = Grey(byPluginNoGroundShadowed[i]);
                        if (unshadowed <= 8 || unshadowed - shadowedR < 20) continue;
                        receiverPixels++;
                        if (pluginUnshadowed - pluginShadowed >= 20) pluginReceives++; else pluginMisses++;
                    }

                    TestContext.Out.WriteLine("display surfaces shadowed by the renderers' picture (ground off): " + receiverPixels + " pixels; shadowed in the plugin's too " + pluginReceives + ", not " + pluginMisses);
                    string shotDir = System.IO.Path.Combine(Application.temporaryCachePath, "native-pictures");
                    System.IO.Directory.CreateDirectory(shotDir);
                    foreach ((string name, Color32[] pixels) in new[] { ("renderers-hard", byRenderersShadowed), ("plugin-hard", byPluginShadowed), ("renderers-none", byRenderers), ("plugin-none", byPlugin),
                                 ("renderers-noground-hard", byRenderersNoGroundShadowed), ("plugin-noground-hard", byPluginNoGroundShadowed), ("renderers-noground-none", byRenderersNoGround), ("plugin-noground-none", byPluginNoGround) })
                    {
                        var tex = new Texture2D(ShadowImageSize, ShadowImageSize, TextureFormat.RGBA32, false);
                        tex.SetPixels32(pixels);
                        tex.Apply(false);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(shotDir, name + ".png"), tex.EncodeToPNG());
                        UnityEngine.Object.Destroy(tex);
                    }

                    TestContext.Out.WriteLine("pictures written to " + shotDir);
                    Assert.That(receiverPixels, Is.GreaterThan(20), "the renderers' picture has a display surface in a display caster's shadow");
                    Assert.That(pluginMisses, Is.LessThanOrEqualTo(receiverPixels / 5), "the plugin's own surfaces receive the shadows the renderers' do");
                }

                (int compared, int onlyOne, float mean, int p95, int largest) picture = ComparePictures(byRenderers, byPlugin);
                (int compared, int onlyOne, float mean, int p95, int largest) shadowed = ComparePictures(byRenderersShadowed, byPluginShadowed);
                TestContext.Out.WriteLine("renderers vs plugin, without shadows: object pixels " + picture.compared + ", in one picture only " + picture.onlyOne
                    + ", mean channel difference " + picture.mean.ToString("F2") + ", 95th percentile " + picture.p95 + ", largest " + picture.largest);
                TestContext.Out.WriteLine("renderers vs plugin, with the light's shadows: object pixels " + shadowed.compared + ", in one picture only " + shadowed.onlyOne
                    + ", mean channel difference " + shadowed.mean.ToString("F2") + ", 95th percentile " + shadowed.p95 + ", largest " + shadowed.largest);
                Assert.That(picture.compared, Is.GreaterThan(400), "the objects cover the picture");
                Assert.That(picture.onlyOne, Is.LessThanOrEqualTo(picture.compared / 10), "the same shapes at the same places (edges aside)");
                Assert.That(picture.mean, Is.LessThanOrEqualTo(2f), "the same colour on average");
                Assert.That(picture.p95, Is.LessThanOrEqualTo(6), "and nearly everywhere");
                Assert.That(shadowed.mean, Is.LessThanOrEqualTo(2f), "the same with the light's shadows, cast by the plugin's issues and received through URP's map");
                Assert.That(shadowed.p95, Is.LessThanOrEqualTo(6));
                (int compared, int onlyOne, float mean, int p95, int largest) soft = ComparePictures(byRenderersSoft, byPluginSoft);
                TestContext.Out.WriteLine("renderers vs plugin, with soft shadows: object pixels " + soft.compared + ", in one picture only " + soft.onlyOne
                    + ", mean channel difference " + soft.mean.ToString("F2") + ", 95th percentile " + soft.p95 + ", largest " + soft.largest);
                Assert.That(soft.mean, Is.LessThanOrEqualTo(2f), "the same with soft shadows");
                Assert.That(soft.p95, Is.LessThanOrEqualTo(6));
                (int compared, int onlyOne, float mean, int p95, int largest) far = ComparePictures(byRenderersSoftFar, byPluginSoftFar);
                (int compared, int onlyOne, float mean, int p95, int largest) farther = ComparePictures(byRenderersSoftFarther, byPluginSoftFarther);
                TestContext.Out.WriteLine("renderers vs plugin, soft shadows, far (cascade 2): object pixels " + far.compared + ", in one picture only " + far.onlyOne
                    + ", mean channel difference " + far.mean.ToString("F2") + ", 95th percentile " + far.p95 + ", largest " + far.largest);
                TestContext.Out.WriteLine("renderers vs plugin, soft shadows, farther (cascade 3): object pixels " + farther.compared + ", in one picture only " + farther.onlyOne
                    + ", mean channel difference " + farther.mean.ToString("F2") + ", 95th percentile " + farther.p95 + ", largest " + farther.largest);
                Assert.That(far.compared, Is.GreaterThan(300), "the objects still cover the far picture");
                Assert.That(far.mean, Is.LessThanOrEqualTo(2f), "the same soft shadows 12 m off: the plugin's casters are in cascade 2's slice too");
                Assert.That(far.p95, Is.LessThanOrEqualTo(6));
                Assert.That(farther.compared, Is.GreaterThan(300), "the objects still cover the farther picture");
                Assert.That(farther.mean, Is.LessThanOrEqualTo(2f), "the same soft shadows 25 m off: the plugin's casters are in cascade 3's slice too");
                Assert.That(farther.p95, Is.LessThanOrEqualTo(6));

                // The slice shader the eye-picture pass draws an XR eye texture's slice with (no XR here): a two-slice
                // array, one colour a slice, drawn slice by slice into a 2D target and read back.
                {
                    // A linear array: sampled as sRGB, the bytes came back converted (play-30: 200 -> 147).
                    var array = new Texture2DArray(4, 4, 2, TextureFormat.RGBA32, false, true) { name = "eye slices" };
                    Track(array);
                    var red = new Color32[16];
                    var green = new Color32[16];
                    for (int i = 0; i < 16; i++) { red[i] = new Color32(200, 10, 20, 255); green[i] = new Color32(10, 180, 30, 255); }
                    array.SetPixels32(red, 0);
                    array.SetPixels32(green, 1);
                    array.Apply(false);
                    var sliceTarget = new RenderTexture(4, 4, 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm) { name = "eye slice copy" };
                    Track(sliceTarget);
                    sliceTarget.Create();
                    var sliceRead = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    Track(sliceRead);
                    for (int slice = 0; slice < 2; slice++)
                    {
                        var commands = new CommandBuffer { name = "eye slice test" };
                        Assert.That(Zantetsu.Rendering.Urp.VpEyeShot.TryDrawSlice(commands, array, slice, sliceTarget), Is.True, "the slice shader is in the build");
                        Graphics.ExecuteCommandBuffer(commands);
                        commands.Release();
                        RenderTexture previous = RenderTexture.active;
                        RenderTexture.active = sliceTarget;
                        sliceRead.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                        sliceRead.Apply(false);
                        RenderTexture.active = previous;
                        Color32 centre = sliceRead.GetPixels32()[5];
                        Color32 expected = slice == 0 ? red[0] : green[0];
                        TestContext.Out.WriteLine("eye slice " + slice + ": drawn " + centre + ", expected " + expected);
                        Assert.That(Mathf.Abs(centre.r - expected.r) + Mathf.Abs(centre.g - expected.g) + Mathf.Abs(centre.b - expected.b), Is.LessThanOrEqualTo(6), "slice " + slice + " is drawn, not the other or black");
                    }
                }

                // The eye-picture pass (both eyes' textures in a Player): here the stage camera's one target, taken
                // as the picture leaves URP and compared with the pixels read from the target itself.
                string eyePrefix = System.IO.Path.Combine(Application.temporaryCachePath, "native-eye-" + System.Guid.NewGuid().ToString("N"));
                Assert.That(Zantetsu.Rendering.Urp.VpEyeShot.TryRequest(stage.camera, eyePrefix, out string eyeFailure), Is.True, eyeFailure);
                // And the shadow map of the same frame (the plugin's casters alone are in it): the picture the shot
                // run takes to see each cascade's slice.
                string mapFile = eyePrefix + "-shadow-map.png";
                Assert.That(Zantetsu.Rendering.Urp.VpEyeShot.TryRequestShadowMap(stage.camera, mapFile, out string mapFailure), Is.True, mapFailure);
                Color32[] byPluginAgain = null;
                Debug.Log("native picture: byPluginAgain (Hard)");
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byPluginAgain = d);
                for (int i = 0; i < 30 && Zantetsu.Rendering.Urp.VpEyeShot.Pending > 0; i++) yield return null;
                Assert.That(Zantetsu.Rendering.Urp.VpEyeShot.Pending, Is.Zero, Zantetsu.Rendering.Urp.VpEyeShot.LastFailure);
                Assert.That(System.IO.File.Exists(mapFile), Is.True, "the shadow map picture was written: " + Zantetsu.Rendering.Urp.VpEyeShot.LastFailure);
                {
                    var map = new Texture2D(2, 2);
                    Track(map);
                    Assert.That(map.LoadImage(System.IO.File.ReadAllBytes(mapFile)), Is.True);
                    Color32[] mapPixels = map.GetPixels32();
                    // Four cascades in a 2x2 atlas: each quadrant must hold depth values that are not one flat value
                    // (the casters' depths differ from the cleared depth), else that slice got no caster.
                    int half = map.width / 2, halfH = map.height / 2;
                    int quadrantsWithCasters = 0;
                    for (int q = 0; q < 4; q++)
                    {
                        int x0 = (q % 2) * half, y0 = (q / 2) * halfH;
                        var values = new HashSet<byte>();
                        for (int y = y0; y < y0 + halfH; y += 4) for (int x = x0; x < x0 + half; x += 4) values.Add(mapPixels[y * map.width + x].r);
                        TestContext.Out.WriteLine("shadow map " + map.width + "x" + map.height + " quadrant " + q + ": distinct depth values " + values.Count);
                        if (values.Count > 1) quadrantsWithCasters++;
                    }

                    // From the near pose the far cascades may hold nothing at this sampling; the far poses above showed them.
                    Assert.That(quadrantsWithCasters, Is.GreaterThanOrEqualTo(2), "the near cascades of the shadow map hold casters");
                    System.IO.File.Delete(mapFile);
                }

                string eyeFile = eyePrefix + "-eye0.png";
                Assert.That(System.IO.File.Exists(eyeFile), Is.True, "the picture was written: " + Zantetsu.Rendering.Urp.VpEyeShot.LastFailure);
                var eye = new Texture2D(2, 2);
                Track(eye);
                Assert.That(eye.LoadImage(System.IO.File.ReadAllBytes(eyeFile)), Is.True);
                Assert.That(new[] { eye.width, eye.height }, Is.EqualTo(new[] { ShadowImageSize, ShadowImageSize }));
                // The readback is the target's bytes as the GPU holds them (linear values, rows bottom first on
                // Direct3D); ReadPixels gives the display's encoding, top row first. Compared by shape: the object
                // pixels (not the black ground) must be the same set, in one row order or the other.
                Color32[] eyePixels = eye.GetPixels32();
                int size = ShadowImageSize;
                int eyeObject = 0, readObject = 0, agreeAsIs = 0, agreeFlipped = 0;
                for (int i = 0; i < eyePixels.Length; i++)
                {
                    // The readback holds linear values, ReadPixels the display encoding: the same threshold after
                    // taking the readback to the display encoding (the dimmer scene of play-42 put the shadowed ground
                    // below 8 linear and above 8 encoded).
                    int eyeGrey = Mathf.RoundToInt(255f * Mathf.LinearToGammaSpace(Grey(eyePixels[i]) / 255f));
                    bool e = eyeGrey > 8, r = Grey(byPluginAgain[i]) > 8;
                    int flipped = (size - 1 - i / size) * size + i % size;
                    bool rFlipped = Grey(byPluginAgain[flipped]) > 8;
                    if (e) eyeObject++;
                    if (r) readObject++;
                    if (e == r) agreeAsIs++;
                    if (e == rFlipped) agreeFlipped++;
                }

                int agree = Mathf.Max(agreeAsIs, agreeFlipped);
                TestContext.Out.WriteLine("eye picture: object pixels " + eyeObject + " (ReadPixels " + readObject + "), same classification as is " + agreeAsIs + ", rows flipped " + agreeFlipped + " of " + eyePixels.Length);
                Assert.That(eyeObject, Is.GreaterThan(eyePixels.Length / 20), "the picture is not blank (the lit ground fills the view; the sky is black)");
                Assert.That(agree, Is.GreaterThanOrEqualTo(eyePixels.Length * 97 / 100), "the same objects at the same places (one row order or the other)");
                System.IO.File.Delete(eyeFile);

                // An update after the plugin has drawn: a third box is shown -- its geometry appended (vertex and index
                // SetData), the commands and instances written, and, the GPU geometry being at its capacity, both
                // geometry buffers replaced (the old ones given up to the route). The picture must then be the
                // renderers' again: the new content drawn, through pointers taken again at the owners' new
                // generations and the new objects, never the old ones.
                long acquisitionsBefore = route.PointerAcquisitions, retiredBefore = route.BuffersRetired;
                int vertexGrowthBefore = root.Display.GpuVertexCapacity;
                GameObject third = TrackActor(new GameObject("placed third"));
                third.transform.SetPositionAndRotation(new Vector3(-1.4f, 0.41f, 1.2f), Quaternion.Euler(0f, -20f, 0f));
                // A sphere (515 vertices): more than the GPU geometry has room for after the two boxes, so that both
                // geometry buffers are replaced by the show -- after the plugin has drawn from the old ones.
                Mesh sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                Assert.That(sphere, Is.Not.Null.And.Property("vertexCount").GreaterThan(root.Display.GpuVertexCapacity), "the sphere does not fit the GPU geometry as it stands");
                third.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
                DrawCompareRenderer(third, sphere, scene.surface);
                third.AddComponent<SphereCollider>();
                scene.instances.Add(new KeyValuePair<string, Renderer[]>("third", third.GetComponentsInChildren<Renderer>(true)));
                yield return null;
                Near();
                // The renderers' picture with the third box: the display's two are hidden, so their renderers are shown again for it.
                foreach (SceneDrawTarget shownTarget in inventory.Targets) shownTarget.renderer.enabled = true;
                Color32[] byRenderersThree = DrawCompareRenderersPicture(stage, LightShadows.Hard);
                foreach (SceneDrawTarget shownTarget in inventory.Targets) shownTarget.renderer.enabled = false;
                yield return null;
                SceneDrawInventory more = SceneDrawInventory.Collect(scene.instances, root.MaterialBindings, new SceneDrawMeshBank());
                int shownMore = 0;
                foreach (SceneDrawTarget target in more.Targets)
                {
                    if (target.renderer != null && target.renderer.gameObject == third)
                    {
                        Assert.That(display.TryShow(target, out string whyNot), Is.True, whyNot);
                        shownMore++;
                    }
                }

                Assert.That(shownMore, Is.EqualTo(1), "the third box was shown");
                yield return null;
                Color32[] byPluginThree = null;
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byPluginThree = d);
                yield return null;
                (int compared, int onlyOne, float mean, int p95, int largest) three = ComparePictures(byRenderersThree, byPluginThree);
                TestContext.Out.WriteLine("renderers vs plugin after the third box (geometry grown " + vertexGrowthBefore + " -> " + root.Display.GpuVertexCapacity
                    + " vertices; pointers asked " + (route.PointerAcquisitions - acquisitionsBefore) + " more; buffers retired through the route " + (route.BuffersRetired - retiredBefore)
                    + "): object pixels " + three.compared + ", in one picture only " + three.onlyOne + ", mean channel difference " + three.mean.ToString("F2") + ", 95th percentile " + three.p95 + ", largest " + three.largest);
                Assert.That(root.Display.GpuVertexCapacity, Is.GreaterThan(vertexGrowthBefore), "the vertex buffer was replaced by a larger one");
                Assert.That(route.BuffersRetired - retiredBefore, Is.GreaterThanOrEqualTo(2), "the replaced vertex and index buffers were given up to the route, not disposed by the owner");
                Assert.That(three.compared, Is.GreaterThan(picture.compared), "the third box is in the picture");
                Assert.That(three.onlyOne, Is.LessThanOrEqualTo(three.compared / 10), "the same shapes at the same places (the new box drawn, the old ones still)");
                Assert.That(three.mean, Is.LessThanOrEqualTo(2f), "the same picture after the update and the replacement");
                Assert.That(three.p95, Is.LessThanOrEqualTo(6));
                Assert.That(route.PointerAcquisitions - acquisitionsBefore, Is.GreaterThan(0).And.LessThanOrEqualTo(16), "the written and replaced buffers were asked again, and only those");
                Assert.That(route.Refused, Is.Zero, route.LastFailure);

                // The route disposed right after an issue (a camera disabled or destroyed the frame it drew): nothing
                // is freed while the issued events may still be read; what they need goes to the deferred release and
                // is released once every block is consumed or confirmed never to run (here within a frame: the
                // Editor's rendering is not threaded, so the events have run by the time the draw returns; the case
                // with unconsumed events is the next test's, with the plugin's consumption held).
                long releasedBefore = Zantetsu.Rendering.VpNativeDrawRelease.Released;
                int pendingBefore = Zantetsu.Rendering.VpNativeDrawRelease.Pending;
                Color32[] byPluginLast = null;
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byPluginLast = d);
                route.Dispose();
                Assert.That(byPluginLast, Is.Not.Null);
                for (int i = 0; i < 4 && Zantetsu.Rendering.VpNativeDrawRelease.Pending > pendingBefore; i++) yield return null;
                Zantetsu.Rendering.VpNativeDrawRelease.Tick();
                TestContext.Out.WriteLine(Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore), "the disposed route's ring and resources were released once its events were consumed");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore + 1));
                Assert.That(route.Refused, Is.Zero, "no event was refused for a pipeline released early: " + route.LastFailure);

                // The route made again (the camera drawing enabled again after a disposal): the display's sink is the
                // new route, which draws the same picture; the old one's events are over.
                stage.nativeRoute = null;
                Color32[] byPluginAgainRoute = null;
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byPluginAgainRoute = d);
                Zantetsu.Rendering.Urp.VpNativeDrawRoute route2 = stage.nativeRoute;
                Assert.That(route2, Is.Not.Null.And.Not.SameAs(route), "a new route was made");
                (int compared, int onlyOne, float mean, int p95, int largest) againRoute = ComparePictures(byRenderersThree, byPluginAgainRoute);
                TestContext.Out.WriteLine("renderers vs the new route: object pixels " + againRoute.compared + ", in one picture only " + againRoute.onlyOne + ", mean channel difference " + againRoute.mean.ToString("F2") + ", 95th percentile " + againRoute.p95);
                Assert.That(againRoute.mean, Is.LessThanOrEqualTo(2f), "the new route draws the same picture");
                Assert.That(route2.Refused, Is.Zero, route2.LastFailure);

                // The world ending before the route: the display gives the buffers its events may still name to the
                // live route, which releases them once those events are consumed or confirmed never to run -- not
                // now. Then the route ends; everything is released, nothing twice.
                long retiredBeforeEnd = route2.BuffersRetired;
                int pendingBeforeEnd = Zantetsu.Rendering.VpNativeDrawRelease.Pending;
                long releasedBeforeEnd = Zantetsu.Rendering.VpNativeDrawRelease.Released;
                display.Dispose();
                yield return EndWorld(root);
                Assert.That(route2.BuffersRetired, Is.GreaterThan(retiredBeforeEnd), "the display's buffers went through the live route at the world's end");
                TestContext.Out.WriteLine("at the world's end: " + Zantetsu.Rendering.VpNativeDrawRelease.Describe() + "; buffers retired through the route " + route2.BuffersRetired);
                for (int i = 0; i < 6 && Zantetsu.Rendering.VpNativeDrawRelease.Pending > pendingBeforeEnd; i++) { yield return null; Zantetsu.Rendering.VpNativeDrawRelease.Tick(); }
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBeforeEnd), "released once their events were consumed");
                route2.Dispose();
                for (int i = 0; i < 6 && Zantetsu.Rendering.VpNativeDrawRelease.Pending > pendingBeforeEnd; i++) { yield return null; Zantetsu.Rendering.VpNativeDrawRelease.Tick(); }
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBeforeEnd), "the route's own went too");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.GreaterThan(releasedBeforeEnd + 1), "the retired buffers' entries and the route's entry were released");

                TestContext.Out.WriteLine("at the end: " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
            }
            finally
            {
                VpNativeDrawSetup.Requested = requestedBefore;
                RenderSettings.ambientMode = ambientBefore;
                RenderSettings.ambientProbe = probeBefore;
                RenderSettings.defaultReflectionMode = reflectionBefore;
                RenderSettings.customReflectionTexture = customReflectionBefore;
                UnityEngine.Object.Destroy(reflection);
            }
        }

        /// <summary>
        /// The lifetimes with events SUBMITTED AND UNCONSUMED (TL, 2026-10-08), made on the Editor's unthreaded
        /// rendering by the plugin's test control that withholds the consumed marks: the events run (the pictures are
        /// drawn) but their blocks stay with the plugin. In that state: (1) the route is disposed -- its ring, pipelines
        /// and buffers are held; (2) a new route draws the same display -- the display remembers both; (3) the display
        /// and the world end -- the display's buffers wait on both routes' events, and the new route's clean state
        /// alone frees nothing; (4) the marks given in the order the events ran release everything, once, with no
        /// refusal. The keeper's journal is the order evidence. Named to sort before the world-ending case below (the
        /// fixture holds back every case that follows it in a run on Direct3D 11).
        /// </summary>
        [UnityTest]
        public IEnumerator NativeDraw_ConsumptionHeld_HoldsWhatOldAndNewRoutesUnconsumedEventsName_AndReleasesItOnceConsumed()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            bool requestedBefore = VpNativeDrawSetup.Requested;
            bool journalBefore = Zantetsu.Rendering.VpNativeDrawRelease.JournalOn;
            VpNativeDrawSetup.Requested = true;
            Zantetsu.Rendering.VpNativeDrawRelease.JournalOn = true;
            Zantetsu.Rendering.VpNativeDrawRelease.ClearJournal();
            try
            {
                DrawCompareScene scene = NewDrawCompareScene();
                CutWorldRoot root = scene.root;
                yield return null;
                Assert.That(root.NativeDrawFailure, Is.Null, root.NativeDrawFailure);
                Assert.That(root.Display.NativeArguments, Is.True, "the display's batch writes the plugin's argument entries");
                ShadowStage stage = NewShadowStage();
                stage.light.transform.rotation = Quaternion.Euler(50f, 120f, 0f);
                stage.camera.orthographic = false;
                stage.camera.fieldOfView = 38f;
                stage.camera.transform.SetPositionAndRotation(new Vector3(-2f, 4f, -3f), Quaternion.LookRotation(new Vector3(2f, -3.6f, 3f)));
                yield return null;
                SceneDrawInventory inventory = SceneDrawInventory.Collect(scene.instances, root.MaterialBindings, new SceneDrawMeshBank());
                Assert.That(inventory.Refusals, Is.Empty);
                var display = new SceneDrawVpDisplay(root);
                foreach (SceneDrawTarget target in inventory.Targets) Assert.That(display.TryShow(target, out string why), Is.True, why);
                yield return null;
                var cutDisplay = root.Display;
                GraphicsBuffer vertices = cutDisplay.GpuVertexBufferForDiagnosis;
                Assert.That(vertices, Is.Not.Null);
                Assert.That(vertices.IsValid(), Is.True);
                int pendingBefore = Zantetsu.Rendering.VpNativeDrawRelease.Pending;
                long releasedBefore = Zantetsu.Rendering.VpNativeDrawRelease.Released;
                long sentinelsBefore = Zantetsu.Rendering.VpNativeDrawRelease.SentinelsIssued;

                // 0. A frame drawn with the marks withheld: the events ran (the picture has the boxes) and are unconsumed.
                VpNativeDrawPlugin.HoldConsumptionForTest(true);
                Zantetsu.Rendering.VpNativeDrawRelease.Note("test 0: consumption held; the first route draws");
                Color32[] first = null;
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => first = d);
                Zantetsu.Rendering.Urp.VpNativeDrawRoute route1 = stage.nativeRoute;
                Assert.That(route1, Is.Not.Null);
                yield return null;
                int heldAfterFirst = VpNativeDrawPlugin.HeldCountForTest();
                TestContext.Out.WriteLine("after the first route's frame: events held " + heldAfterFirst + ", blocks in flight " + route1.BlocksInFlight
                    + " (recorded " + route1.BlocksInFlightIn(VpNativeDrawData.BlockState.Recorded) + ", submitted " + route1.BlocksInFlightIn(VpNativeDrawData.BlockState.Submitted) + "); " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                Assert.That(heldAfterFirst, Is.GreaterThan(0), "the plugin ran the events and withheld their marks");
                Assert.That(route1.BlocksInFlight, Is.EqualTo(heldAfterFirst), "every event that ran is a block still in flight: submitted, not consumed");
                Assert.That(route1.BodiesIssued, Is.GreaterThan(0));
                Assert.That(first.Any(c => Grey(c) != Grey(first[0])), Is.True, "the picture was drawn");

                // 1. The route disposed with those events unconsumed: its entry waits, nothing is released.
                Zantetsu.Rendering.VpNativeDrawRelease.Note("test 1: the first route is disposed with unconsumed events");
                route1.Dispose();
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1), "the disposed route's ring and resources wait");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore));
                Assert.That(route1.BlocksInFlight, Is.EqualTo(heldAfterFirst), "the ring lives on, its blocks with the plugin");
                yield return null;
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1), "a frame later, still waiting (no frame count releases anything)");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore));

                // 2. A new route (the component enabled again) draws the same display, the same picture; its events are
                // held too. The display now remembers both routes. At the end of the camera's rendering a sentinel was
                // issued (an entry waits): the recorded blocks are Submitted under it -- and it is held as well.
                stage.nativeRoute = null;
                Zantetsu.Rendering.VpNativeDrawRelease.Note("test 2: a new route draws");
                Color32[] second = null;
                yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => second = d);
                Zantetsu.Rendering.Urp.VpNativeDrawRoute route2 = stage.nativeRoute;
                Assert.That(route2, Is.Not.Null.And.Not.SameAs(route1));
                yield return null;
                (int compared, int onlyOne, float mean, int p95, int largest) same = ComparePictures(first, second);
                TestContext.Out.WriteLine("first route vs new route: object pixels " + same.compared + ", in one picture only " + same.onlyOne + ", mean channel difference " + same.mean.ToString("F2") + "; held " + VpNativeDrawPlugin.HeldCountForTest()
                    + "; route 2 blocks in flight " + route2.BlocksInFlight + " (submitted " + route2.BlocksInFlightIn(VpNativeDrawData.BlockState.Submitted) + "); " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                Assert.That(same.mean, Is.LessThanOrEqualTo(2f), "the new route draws the same picture");
                Assert.That(route2.BlocksInFlight, Is.GreaterThan(0), "the new route's events are unconsumed too");
                Assert.That(cutDisplay.SinksUsed.Count, Is.EqualTo(2), "the display remembers both routes");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.SentinelsIssued, Is.GreaterThan(sentinelsBefore), "a sentinel was issued at the end of a camera's rendering while an entry waited");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.SentinelsConsumed, Is.EqualTo(sentinelsBefore), "the sentinel is held with the events: nothing consumed");
                Assert.That(route1.BlocksInFlightIn(VpNativeDrawData.BlockState.Submitted), Is.EqualTo(heldAfterFirst), "the old route's blocks are Submitted under the sentinel, not released");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1));
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore));

                // 3. The display and the world end while both routes' events are unconsumed: the display's buffers are
                // given up against both routes (not the new one alone); nothing is freed.
                Zantetsu.Rendering.VpNativeDrawRelease.Note("test 3: the display and the world end");
                display.Dispose();
                yield return EndWorld(root);
                TestContext.Out.WriteLine("at the world's end: retired across routes " + cutDisplay.BuffersRetiredAcrossRoutes + ", through the new route alone " + route2.BuffersRetired + "; " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                TestContext.Out.WriteLine(Zantetsu.Rendering.VpNativeDrawRelease.DescribePending());
                Assert.That(cutDisplay.BuffersRetiredAcrossRoutes, Is.GreaterThan(0), "the display's buffers wait on both routes' events");
                Assert.That(route2.BuffersRetired, Is.Zero, "none was given to the new route alone");
                Assert.That(vertices.IsValid(), Is.True, "the vertex buffer both routes drew from is held");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.GreaterThan(pendingBefore + 1));
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore), "nothing released while every mark is withheld");
                int pendingAtEnd = Zantetsu.Rendering.VpNativeDrawRelease.Pending;

                // 4a. The marks of the first route's events alone (the first `heldAfterFirst` that ran): the first
                // route's entry is released; the display's buffers still wait on the new route's events.
                int given = VpNativeDrawPlugin.ReleaseHeldForTest(heldAfterFirst);
                Zantetsu.Rendering.VpNativeDrawRelease.Note("test 4a: " + given + " marks given (the first route's events)");
                Assert.That(given, Is.EqualTo(heldAfterFirst));
                Zantetsu.Rendering.VpNativeDrawRelease.Tick();
                TestContext.Out.WriteLine("after the first route's marks: " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                Assert.That(route1.BlocksInFlight, Is.Zero, "the first route's blocks are consumed");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore + 1), "the first route's entry alone");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingAtEnd - 1));
                Assert.That(vertices.IsValid(), Is.True, "still held: the new route's events may read it");

                // 4b. Every mark: the new route's events and the sentinel. Everything waiting is released, once.
                int givenAll = VpNativeDrawPlugin.ReleaseHeldForTest(-1);
                VpNativeDrawPlugin.HoldConsumptionForTest(false);
                Zantetsu.Rendering.VpNativeDrawRelease.Note("test 4b: " + givenAll + " marks given (all); hold off");
                Zantetsu.Rendering.VpNativeDrawRelease.Tick();
                TestContext.Out.WriteLine("after every mark: " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore), "every entry released: " + Zantetsu.Rendering.VpNativeDrawRelease.DescribePending());
                Assert.That(vertices.IsValid(), Is.False, "the vertex buffer was disposed once both routes' events were consumed");
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.SentinelsConsumed, Is.GreaterThan(sentinelsBefore), "the sentinel was consumed with them");
                Assert.That(route2.BlocksInFlight, Is.Zero);
                Assert.That(route1.Refused, Is.Zero, route1.LastFailure);
                Assert.That(route2.Refused, Is.Zero, route2.LastFailure);
                route2.Dispose();
                Zantetsu.Rendering.VpNativeDrawRelease.Tick();
                Assert.That(Zantetsu.Rendering.VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore), "the new route's own went at once: its events are over");

                // The journal, in order: the first route's events submitted under the sentinel before the display's
                // buffers were deferred against both routes, and released only after the sentinel was consumed.
                string journal = Zantetsu.Rendering.VpNativeDrawRelease.JournalText();
                TestContext.Out.WriteLine(journal);
                int iDisposed = journal.IndexOf("test 1:", System.StringComparison.Ordinal);
                int iSubmitted = journal.IndexOf("submitted under sentinel", System.StringComparison.Ordinal);
                int iAcross = journal.IndexOf("waits on all of [", System.StringComparison.Ordinal);
                int iGivenAll = journal.IndexOf("test 4b:", System.StringComparison.Ordinal);
                int iConsumedSentinel = journal.IndexOf("consumed: every command submitted before it has run", System.StringComparison.Ordinal);
                int iReleasedAcross = journal.IndexOf("(VP display buffer (several routes)) released", System.StringComparison.Ordinal);
                Assert.That(iDisposed, Is.GreaterThanOrEqualTo(0).And.LessThan(iSubmitted), "the route was disposed before its blocks were submitted under a sentinel");
                Assert.That(iSubmitted, Is.LessThan(iAcross), "the display's buffers were deferred against both routes after that");
                Assert.That(iAcross, Is.LessThan(iGivenAll).And.LessThan(iReleasedAcross));
                Assert.That(iGivenAll, Is.LessThan(iReleasedAcross), "released only after every mark was given");
                Assert.That(iConsumedSentinel, Is.GreaterThan(iGivenAll), "the sentinel's consumption was read after the marks were given");
            }
            finally
            {
                VpNativeDrawPlugin.ReleaseHeldForTest(-1);
                VpNativeDrawPlugin.HoldConsumptionForTest(false);
                Zantetsu.Rendering.VpNativeDrawRelease.JournalOn = journalBefore;
                VpNativeDrawSetup.Requested = requestedBefore;
            }
        }

        // Named to run after the picture case: a world ended by contract is kept uncollected by the fixture, which
        // holds back whatever case of the fixture follows it in the same run.
        [UnityTest]
        public IEnumerator NativeDraw_WhereThePluginCannotDraw_AskedFor_EndsTheWorld_RatherThanDrawByAnotherRoute()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin can draw on this Editor's device; this case needs one it cannot (the Editor's default, Direct3D 11)");
            }

            bool requestedBefore = VpNativeDrawSetup.Requested;
            VpNativeDrawSetup.Requested = true;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("VP3C-NATIVE REQUESTED .* BUT NOT AVAILABLE"));
                LogAssert.ignoreFailingMessages = true;
                ExpectTermination();   // the world's end is what this case is about

                // The scaffold asserts a world built in Awake; this world ends in Awake instead, by design.
                CutWorldRoot root = null;
                try
                {
                    root = NewDrawCompareScene().root;
                }
                catch (AssertionException e)
                {
                    StringAssert.Contains("built the world in Awake", e.Message);
                    root = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
                }

                Assert.That(root, Is.Not.Null, "the root exists, ended");
                yield return null;
                Assert.That(root.NativeDrawFailure, Is.Not.Null, "the refusal is recorded");
                TestContext.Out.WriteLine(root.NativeDrawFailure);
                Assert.That(root.NativeDrawFailure, Does.Contain("Direct3D 12"));
                Assert.That(root.TerminationRequested, Is.True, "the world ends");
                Assert.That(root.Display, Is.Null, "no display by another route");

                // A world ended by contract is not ended again here: the fixture keeps it, as it does for every
                // termination a case means.
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                VpNativeDrawSetup.Requested = requestedBefore;
            }
        }

        // Object pixels (not the ground's value) compared channel by channel, as the draw comparison's picture test does.
        private static (int compared, int onlyOne, float mean, int p95, int largest) ComparePictures(Color32[] x, Color32[] y)
        {
            int ground = Mode(x), onlyOne = 0;
            var differences = new List<int>();
            for (int i = 0; i < x.Length; i++)
            {
                bool gx = Mathf.Abs(Grey(x[i]) - ground) <= 6, gy = Mathf.Abs(Grey(y[i]) - ground) <= 6;
                if (gx != gy) onlyOne++;
                if (gx || gy) continue;
                differences.Add(Mathf.Max(Mathf.Abs(x[i].r - y[i].r), Mathf.Max(Mathf.Abs(x[i].g - y[i].g), Mathf.Abs(x[i].b - y[i].b))));
            }

            differences.Sort();
            return (differences.Count, onlyOne, differences.Count > 0 ? (float)differences.Average() : 0f,
                differences.Count > 0 ? differences[(int)(0.95f * (differences.Count - 1))] : 0, differences.Count > 0 ? differences[differences.Count - 1] : 0);
        }
    }
}
