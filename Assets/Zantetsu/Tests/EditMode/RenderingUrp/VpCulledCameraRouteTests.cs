using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Urp.Tests
{
    /// <summary>
    /// The explicit VP3C route over a lit scene with 4 URP shadow cascades, VP cubes and cylinders, a Unity mesh occluding a
    /// VP cube, an instance behind the camera and a tower outside the camera frustum whose shadow falls into view. Queued
    /// draws last until the frame ends, so each step that must not see the previous step's issues runs in a new editor
    /// frame. By default the route issues only the single batch and receives no callback; enabled for one camera, that
    /// camera renders VP3C (culled forward view depth-tested against the Unity mesh, culled cascades with back faces
    /// culled) with the single batch's pixels while other cameras get the single batch, each camera once; enabling and
    /// disabling twice subscribe and release once; changes inside a frame neither draw twice nor drop the set; cameras
    /// without shadows, overlay cameras, mismatched stereo modes and uploads are refused, and a camera losing its shadows
    /// after enabling explicitly falls back; an unchanged view uploads no selection; disposing leaves the owner's sets.
    /// Left and right eye placement under Single Pass Instanced needs XR and is checked on Quest Link, not here.
    /// </summary>
    public class VpCulledCameraRouteTests
    {
        private const string ForwardShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const string CulledForwardShaderName = "Zantetsu/VP Culled Indexed Indirect Unlit";
        private const string CulledShadowShaderName = "Zantetsu/VP Culled Indexed Shadow Caster";
        private const int Size = 192;

        private readonly List<Object> _objects = new List<Object>();

        private sealed class Display : IDisposable
        {
            public VpCpuGeometryPool pool;
            public VpGpuIndexedGeometryBuffers buffers;
            public VpIndexedIndirectDrawBatch batch;
            public VpCulledInstanceSet set;
            public VpCulledCameraRoute route;
            public Material forward;
            public Material shadow;
            public Material culledForward;
            public Material culledShadow;
            public VpIndirectCommand[] commands;
            public Matrix4x4[] transforms;
            public int occluded;
            public int tower;

            public void Dispose()
            {
                route?.Dispose();
                set?.Dispose();
                batch?.Dispose();
                buffers?.Dispose();
                pool?.Dispose();
            }
        }

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private Material Material(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            Material material = Track(new Material(shader));
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.green);
            }

            return material;
        }

        /// <summary>Runs <paramref name="body"/> over editor frames in an empty scene holding the lit display.</summary>
        private IEnumerator InLitScene(Func<Display, IEnumerator> body)
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Display display = null;
            try
            {
                display = Build();
                IEnumerator steps = body(display);
                while (steps.MoveNext())
                {
                    yield return steps.Current;
                }
            }
            finally
            {
                display?.Dispose();
                DestroyObjects();
                if (previousSetup != null && previousSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        private Display Build()
        {
            Light light = Track(new GameObject("VP Route Test Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(45f, -40f, 0f);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.transform.position = new Vector3(0f, 0f, 20f);
            ground.transform.localScale = new Vector3(12f, 1f, 12f);

            // A Unity mesh between the camera and one VP cube.
            GameObject occluder = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            Object.DestroyImmediate(occluder.GetComponent<Collider>());
            occluder.transform.position = new Vector3(-1.5f, 2.25f, -3f);
            occluder.transform.localScale = new Vector3(4f, 4.5f, 0.5f);

            Mesh cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            Mesh cylinderMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            int vertices = cubeMesh.vertexCount + cylinderMesh.vertexCount;
            int indices = (int)(cubeMesh.GetIndexCount(0) + cylinderMesh.GetIndexCount(0));
            var display = new Display();
            try
            {
                display.pool = new VpCpuGeometryPool(vertices, indices, Allocator.Persistent);
                Assert.That(display.pool.TryAppend(cubeMesh, out VpGeometryRange cube), Is.True);
                Assert.That(display.pool.TryAppend(cylinderMesh, out VpGeometryRange cylinder), Is.True);
                display.buffers = new VpGpuIndexedGeometryBuffers(vertices, indices);
                Assert.That(display.buffers.TryUpload(display.pool), Is.True);

                var cubes = new List<Matrix4x4>();
                var cylinders = new List<Matrix4x4>();
                for (int row = 0; row < 8; row++)
                {
                    for (int column = 0; column < 5; column++)
                    {
                        float height = 1f + ((row * 3 + column * 5) % 5) * 0.5f;
                        var position = new Vector3(-6f + column * 3f, height * 0.5f, row * 4f);
                        if ((row + column) % 2 == 0)
                        {
                            cubes.Add(Matrix4x4.TRS(position, Quaternion.Euler(0f, row * 17f, 0f), new Vector3(1.2f, height, 1.2f)));
                        }
                        else
                        {
                            cylinders.Add(Matrix4x4.TRS(position, Quaternion.identity, new Vector3(1f, height * 0.5f, 1f)));
                        }
                    }
                }

                display.occluded = cubes.Count;
                cubes.Add(Matrix4x4.TRS(new Vector3(-1.5f, 1f, 1f), Quaternion.identity, new Vector3(1.5f, 2f, 1.5f)));
                display.tower = cubes.Count;
                cubes.Add(Matrix4x4.TRS(new Vector3(22f, 12.5f, 6f), Quaternion.identity, new Vector3(2f, 25f, 2f)));
                cubes.Add(Matrix4x4.TRS(new Vector3(0f, 1f, -25f), Quaternion.identity, new Vector3(2f, 2f, 2f)));
                display.commands = new[] { new VpIndirectCommand(cube, cubeMesh.bounds, cubes.Count), new VpIndirectCommand(cylinder, cylinderMesh.bounds, cylinders.Count) };
                display.transforms = cubes.Concat(cylinders).ToArray();
                display.batch = new VpIndexedIndirectDrawBatch(2, 128);
                Assert.That(display.batch.TryUpload(display.commands, display.transforms, false), Is.True);
                display.set = new VpCulledInstanceSet(2, 128);
                Assert.That(display.set.TryUpload(display.commands, display.transforms, false), Is.True);
                display.forward = Material(ForwardShaderName);
                display.shadow = Material(ShadowShaderName);
                display.culledForward = Material(CulledForwardShaderName);
                display.culledShadow = Material(CulledShadowShaderName);
                display.route = new VpCulledCameraRoute(display.batch, display.set, display.buffers, display.forward, display.shadow, display.culledForward, display.culledShadow, 0);
                return display;
            }
            catch
            {
                display.Dispose();
                throw;
            }
        }

        private Camera NewCamera(string name, out RenderTexture target)
        {
            target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Route Test Camera " + name)).AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.fieldOfView = 55f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 120f;
            camera.transform.position = new Vector3(0f, 8f, -12f);
            camera.transform.LookAt(new Vector3(0f, 0f, 14f));
            return camera;
        }

        private Color32[] RenderAndRead(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D readback = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels32();
        }

        private static int CountDiffering(Color32[] first, Color32[] second)
        {
            int differing = 0;
            for (int i = 0; i < first.Length; i++)
            {
                if (Math.Abs(first[i].r - second[i].r) > 2 || Math.Abs(first[i].g - second[i].g) > 2 || Math.Abs(first[i].b - second[i].b) > 2)
                {
                    differing++;
                }
            }

            return differing;
        }

        private static string Describe(VpCulledCameraRoute route)
        {
            return "callbacks " + route.CallbackCount + ", single issues " + route.SingleIssueCount + ", VP3C issues " + route.CulledIssueCount + ", fallbacks "
                + route.FallbackIssueCount + " (" + (route.LastFallbackReason ?? "none") + "), shadow records " + route.ShadowRecordCount + ", splits "
                + route.LastShadowSplitCount + ", skip " + (route.LastShadowSkipReason ?? "none") + ", cull front " + route.LastShadowCullFront;
        }

        [UnityTest]
        public IEnumerator ByDefault_TheRouteIssuesTheSingleBatch_WithNoCallbackPassOrVp3cIssue()
        {
            return InLitScene(ByDefault);
        }

        private IEnumerator ByDefault(Display display)
        {
            VpCulledCameraRoute route = display.route;
            Camera direct = NewCamera("Direct", out RenderTexture directTarget);
            display.batch.Render(display.forward, display.shadow, new MaterialPropertyBlock(), display.buffers, 0, direct);
            Color32[] reference = RenderAndRead(direct, directTarget);
            yield return null;

            Assert.That(route.IsEnabled, Is.False, "disabled by default");
            Assert.That(route.EnabledCamera, Is.Null);
            Camera routed = NewCamera("Routed", out RenderTexture routedTarget);
            route.Issue();
            route.Issue();
            Color32[] pixels = RenderAndRead(routed, routedTarget);
            TestContext.WriteLine("default: " + Describe(route));

            Assert.That(CountDiffering(reference, pixels), Is.Zero, "pixels differing from the batch issued directly");
            Assert.That(route.SingleIssueCount, Is.EqualTo(1), "one single batch issue for every camera, however often Issue is called in a frame");
            Assert.That(route.CallbackCount, Is.Zero, "no beginCameraRendering callback");
            Assert.That(route.CulledIssueCount, Is.Zero, "no VP3C forward or shadow caster bounds issue");
            Assert.That(route.ShadowRecordCount, Is.Zero, "no cascade pass");
            Assert.That(route.LastShadowSkipReason, Is.Null);
            Assert.That(display.set.ForwardUploadCount + display.set.ShadowUploadCount, Is.Zero, "no VP3C selection upload");
        }

        [UnityTest]
        public IEnumerator Enabled_TheTargetCameraRendersVp3c_OtherCamerasTheSingleBatch_EachOnce()
        {
            return InLitScene(TargetAndOthers);
        }

        private IEnumerator TargetAndOthers(Display display)
        {
            VpCulledCameraRoute route = display.route;
            Camera forwardOnlyCamera = NewCamera("Forward Only", out RenderTexture forwardOnlyTarget);
            display.batch.RenderForward(display.forward, new MaterialPropertyBlock(), display.buffers, 0, 0, display.batch.CommandCount, forwardOnlyCamera);
            Color32[] forwardOnly = RenderAndRead(forwardOnlyCamera, forwardOnlyTarget);
            route.Issue();
            Camera referenceCamera = NewCamera("Reference", out RenderTexture referenceTarget);
            Color32[] reference = RenderAndRead(referenceCamera, referenceTarget);
            yield return null;

            Camera target = NewCamera("Target", out RenderTexture targetTexture);
            Camera other = NewCamera("Other", out RenderTexture otherTexture);
            Assert.That(route.TryEnable(target, out string refusal), Is.True, refusal);
            Assert.That(refusal, Is.Null);
            Assert.That(route.EnabledCamera, Is.SameAs(target));
            route.Issue();
            Color32[] targetPixels = RenderAndRead(target, targetTexture);
            Color32[] otherPixels = RenderAndRead(other, otherTexture);
            Color32[] targetAgain = RenderAndRead(target, targetTexture);
            VpCulledInstanceSet set = display.set;
            TestContext.WriteLine("enabled: " + Describe(route) + "; forward selected " + set.ForwardSelectedInstances + " of " + set.InstanceCount
                + ", shadow selected per split " + string.Join("/", Enumerable.Range(0, set.SplitCount).Select(set.ShadowSelectedInstances)));

            Assert.That(CountDiffering(reference, forwardOnly), Is.GreaterThan(500), "shadows show in the single batch reference");
            Assert.That(CountDiffering(reference, targetPixels), Is.Zero, "target camera pixels differing from the single batch");
            Assert.That(CountDiffering(reference, otherPixels), Is.Zero, "other camera pixels differing from the single batch");
            Assert.That(CountDiffering(reference, targetAgain), Is.Zero, "pixels of the target's second render in the frame");
            Assert.That(route.CallbackCount, Is.EqualTo(3), "one callback per camera render");
            Assert.That(route.CulledIssueCount, Is.EqualTo(2), "one VP3C issue per render of the target camera only");
            Assert.That(route.SingleIssueCount, Is.EqualTo(2), "the disabled frame's issue and one for the other camera");
            Assert.That(route.FallbackIssueCount, Is.Zero, "no fallback");
            Assert.That(route.LastFallbackReason, Is.Null);
            Assert.That(route.ShadowRecordCount, Is.EqualTo(2), "the cascade pass is recorded for each render of the target only");
            Assert.That(route.LastShadowSkipReason, Is.Null, "the cascade pass drew");
            Assert.That(route.LastShadowSplitCount, Is.EqualTo(4), "cascades");
            Assert.That(route.LastShadowCullFront, Is.False, "back faces culled after the native pass boundary");
            Assert.That(set.ForwardSelectedInstances, Is.GreaterThan(0).And.LessThan(set.InstanceCount), "the forward view culls instances");
            Assert.That(set.IsForwardSelected(display.occluded), Is.True, "the cube behind the Unity mesh is drawn and depth-tested");
            Assert.That(set.IsForwardSelected(display.tower), Is.False, "the off-camera tower is not in the forward view");
            Assert.That(Enumerable.Range(0, set.SplitCount).Any(split => set.IsShadowSelected(split, display.tower)), Is.True, "the off-camera tower casts in a cascade");
        }

        [UnityTest]
        public IEnumerator EnablingAndDisablingTwice_SubscribeAndReleaseOnce_AndTheRouteCanBeEnabledAgain()
        {
            return InLitScene(EnableDisableTwice);
        }

        private IEnumerator EnableDisableTwice(Display display)
        {
            VpCulledCameraRoute route = display.route;
            route.Issue();
            Camera referenceCamera = NewCamera("Reference", out RenderTexture referenceTarget);
            Color32[] reference = RenderAndRead(referenceCamera, referenceTarget);
            yield return null;

            Camera target = NewCamera("Target", out RenderTexture targetTexture);
            Camera other = NewCamera("Other", out RenderTexture otherTexture);
            Assert.That(route.TryEnable(target, out string refusal), Is.True, refusal);
            Assert.That(route.TryEnable(target, out refusal), Is.True, "enabling the same camera again");
            Assert.That(route.TryEnable(other, out refusal), Is.False, "enabling another camera while enabled");
            StringAssert.Contains("already enabled", refusal);
            Assert.That(route.EnabledCamera, Is.SameAs(target), "a refused enable changes nothing");
            route.Issue();
            Assert.That(CountDiffering(reference, RenderAndRead(target, targetTexture)), Is.Zero, "enabled pixels");
            Assert.That(route.CallbackCount, Is.EqualTo(1), "enabling twice subscribes once");
            Assert.That(route.CulledIssueCount, Is.EqualTo(1));
            Assert.That(route.ShadowRecordCount, Is.EqualTo(1), "the pass is enqueued once");
            int forwardUploads = display.set.ForwardUploadCount;
            int shadowUploads = display.set.ShadowUploadCount;
            yield return null;

            route.Disable();
            route.Disable();
            Assert.That(route.IsEnabled, Is.False);
            Assert.That(route.EnabledCamera, Is.Null);
            Assert.That(route.LastShadowSplitCount, Is.Zero, "the pass is dropped");
            int singleIssues = route.SingleIssueCount;
            route.Issue();
            Assert.That(CountDiffering(reference, RenderAndRead(target, targetTexture)), Is.Zero, "disabled pixels of the former target");
            Assert.That(CountDiffering(reference, RenderAndRead(other, otherTexture)), Is.Zero, "disabled pixels of another camera");
            TestContext.WriteLine("disabled: " + Describe(route));
            Assert.That(route.CallbackCount, Is.EqualTo(1), "disabling twice leaves no callback");
            Assert.That(route.CulledIssueCount, Is.EqualTo(1), "no VP3C issue once disabled");
            Assert.That(route.ShadowRecordCount, Is.EqualTo(1), "no cascade pass once disabled");
            Assert.That(route.SingleIssueCount, Is.EqualTo(singleIssues + 1), "one single batch issue for every camera");
            Assert.That(display.set.ForwardUploadCount, Is.EqualTo(forwardUploads), "no forward upload once disabled");
            Assert.That(display.set.ShadowUploadCount, Is.EqualTo(shadowUploads), "no shadow upload once disabled");
            yield return null;

            Assert.That(route.TryEnable(target, out refusal), Is.True, refusal);
            route.Issue();
            Assert.That(CountDiffering(reference, RenderAndRead(target, targetTexture)), Is.Zero, "re-enabled pixels");
            TestContext.WriteLine("re-enabled: " + Describe(route));
            Assert.That(route.CallbackCount, Is.EqualTo(2), "re-enabling subscribes once");
            Assert.That(route.CulledIssueCount, Is.EqualTo(2));
            Assert.That(route.ShadowRecordCount, Is.EqualTo(2), "the new pass is recorded");
            Assert.That(route.LastShadowSkipReason, Is.Null, "the new pass drew");
            Assert.That(route.LastShadowSplitCount, Is.EqualTo(4));
            yield return null;

            route.Dispose();
            route.Dispose();
            RenderAndRead(target, targetTexture);
            Assert.That(route.CallbackCount, Is.EqualTo(2), "disposing an enabled route unsubscribes");
            Assert.Throws<ObjectDisposedException>(() => route.Issue());
            Assert.Throws<ObjectDisposedException>(() => route.TryEnable(target, out _));
            Assert.DoesNotThrow(() => route.Disable(), "disabling a disposed route does nothing");
            Assert.DoesNotThrow(() => _ = display.set.VisibleBuffer, "the owner's set stays alive");
            Assert.DoesNotThrow(() => display.batch.Render(display.forward, display.shadow, new MaterialPropertyBlock(), display.buffers, 0, other), "the owner's batch stays alive");
        }

        [UnityTest]
        public IEnumerator ChangesWithinAFrame_NeitherDrawTheSetTwiceNorDropIt()
        {
            return InLitScene(ChangesWithinAFrame);
        }

        private IEnumerator ChangesWithinAFrame(Display display)
        {
            VpCulledCameraRoute route = display.route;
            route.Issue();
            Camera referenceCamera = NewCamera("Reference", out RenderTexture referenceTarget);
            Color32[] reference = RenderAndRead(referenceCamera, referenceTarget);
            yield return null;

            Camera target = NewCamera("Target", out RenderTexture targetTexture);
            route.Issue();
            Assert.That(route.TryEnable(target, out string refusal), Is.True, refusal);
            Assert.That(CountDiffering(reference, RenderAndRead(target, targetTexture)), Is.Zero, "enabled after the frame's issue: pixels");
            Assert.That(route.CulledIssueCount, Is.Zero, "enabling after the frame's single batch issue waits for the next frame");
            Assert.That(route.CallbackCount, Is.EqualTo(1));
            Assert.That(route.SingleIssueCount, Is.EqualTo(2));
            yield return null;

            route.Issue();
            Assert.That(CountDiffering(reference, RenderAndRead(target, targetTexture)), Is.Zero, "enabled frame: pixels");
            Assert.That(route.CulledIssueCount, Is.EqualTo(1), "the enabled frame issues VP3C");
            route.Disable();
            Assert.That(route.SingleIssueCount, Is.EqualTo(3), "disabling after an enabled issue queues the single batch");
            Camera later = NewCamera("Later", out RenderTexture laterTarget);
            Assert.That(CountDiffering(reference, RenderAndRead(later, laterTarget)), Is.Zero, "a camera rendering after Disable in the frame: pixels");
            Assert.That(route.CallbackCount, Is.EqualTo(2), "no callback after Disable");
            yield return null;

            route.Issue();
            Assert.That(CountDiffering(reference, RenderAndRead(target, targetTexture)), Is.Zero, "next frame: pixels");
            TestContext.WriteLine("changes within a frame: " + Describe(route));
            Assert.That(route.SingleIssueCount, Is.EqualTo(4));
            Assert.That(route.CulledIssueCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CamerasThatCannotShowVp3cShadows_AreRefused_OrFallBackExplicitly()
        {
            return InLitScene(RefusalsAndFallback);
        }

        private IEnumerator RefusalsAndFallback(Display display)
        {
            VpCulledCameraRoute route = display.route;
            Camera shadowless = NewCamera("Shadowless", out RenderTexture shadowlessTarget);
            UniversalAdditionalCameraData shadowlessData = shadowless.GetUniversalAdditionalCameraData();
            shadowlessData.renderShadows = false;
            Assert.That(route.TryEnable(shadowless, out string refusal), Is.False, "a camera not rendering shadows");
            StringAssert.Contains("does not render shadows", refusal);

            Camera overlay = NewCamera("Overlay", out _);
            overlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
            Assert.That(route.TryEnable(overlay, out refusal), Is.False, "an overlay camera");
            StringAssert.Contains("overlay", refusal);

            Camera target = NewCamera("Target", out RenderTexture targetTexture);
            Assert.That(display.batch.TryUpload(display.commands, display.transforms, true), Is.True);
            Assert.That(display.set.TryUpload(display.commands, display.transforms, true), Is.True);
            Assert.That(route.TryEnable(target, out refusal), Is.False, "uploads for Single Pass Instanced and a camera rendering one view");
            StringAssert.Contains("stereo mode", refusal);

            Assert.That(display.batch.TryUpload(display.commands, display.transforms, false), Is.True);
            VpIndirectCommand[] firstCommand = { display.commands[0] };
            Assert.That(display.set.TryUpload(firstCommand, display.transforms.Take(firstCommand[0].instanceCount).ToArray(), false), Is.True);
            Assert.That(route.TryEnable(target, out refusal), Is.False, "a set holding other uploads than the batch");
            StringAssert.Contains("different uploads", refusal);
            Assert.That(display.set.TryUpload(display.commands, display.transforms, false), Is.True);

            Assert.That(route.IsEnabled, Is.False, "refusals enable nothing");
            Assert.Throws<ArgumentException>(
                () => new VpCulledCameraRoute(display.batch, display.set, display.buffers, display.forward, display.shadow, display.culledForward, display.shadow, 0),
                "a shadow material without the culled shadow caster's pass");

            route.Issue();
            Color32[] shadowlessReference = RenderAndRead(shadowless, shadowlessTarget);
            Assert.That(route.CallbackCount, Is.Zero, "refusals subscribe nothing");
            yield return null;

            shadowlessData.renderShadows = true;
            Assert.That(route.TryEnable(shadowless, out refusal), Is.True, refusal);
            shadowlessData.renderShadows = false;
            LogAssert.Expect(LogType.Warning, new Regex("gets the VP3 single batch instead of VP3C: the camera does not render shadows"));
            route.Issue();
            Color32[] fallback = RenderAndRead(shadowless, shadowlessTarget);
            TestContext.WriteLine("fallback: " + Describe(route));
            Assert.That(CountDiffering(shadowlessReference, fallback), Is.Zero, "fallback pixels differing from the single batch without shadows");
            Assert.That(route.FallbackIssueCount, Is.EqualTo(1), "the enabled camera explicitly gets the single batch");
            Assert.That(route.LastFallbackReason, Is.EqualTo("the camera does not render shadows"));
            Assert.That(route.CulledIssueCount, Is.Zero, "no VP3C issue while falling back");
            Assert.That(route.ShadowRecordCount, Is.Zero, "no cascade pass while falling back");
            yield return null;

            shadowlessData.renderShadows = true;
            route.Issue();
            RenderAndRead(shadowless, shadowlessTarget);
            Assert.That(route.LastFallbackReason, Is.Null, "VP3C again once the camera renders shadows");
            Assert.That(route.CulledIssueCount, Is.EqualTo(1));
            Assert.That(route.LastShadowSkipReason, Is.Null, "the cascade pass drew");
            yield return null;

            LogAssert.Expect(LogType.Warning, new Regex("was destroyed"));
            Object.DestroyImmediate(shadowless.gameObject);
            int singleIssues = route.SingleIssueCount;
            route.Issue();
            Assert.That(route.IsEnabled, Is.False, "a destroyed camera returns the route to the single batch");
            Assert.That(route.SingleIssueCount, Is.EqualTo(singleIssues + 1), "the single batch is issued in that frame");
            Assert.That(route.TryEnable(target, out refusal), Is.True, refusal);
        }

        [UnityTest]
        public IEnumerator AnUnchangedView_UploadsNoSelectionAgain()
        {
            return InLitScene(SteadyView);
        }

        private IEnumerator SteadyView(Display display)
        {
            VpCulledCameraRoute route = display.route;
            VpCulledInstanceSet set = display.set;
            Camera target = NewCamera("Target", out RenderTexture targetTexture);
            Assert.That(route.TryEnable(target, out string refusal), Is.True, refusal);
            route.Issue();
            Color32[] first = RenderAndRead(target, targetTexture);
            int forwardUploads = set.ForwardUploadCount;
            int shadowUploads = set.ShadowUploadCount;
            Assert.That(forwardUploads, Is.EqualTo(1), "the first frame uploads the forward selection");
            Assert.That(shadowUploads, Is.GreaterThanOrEqualTo(1), "the first frame uploads the cascade selections");
            yield return null;

            route.Issue();
            Color32[] second = RenderAndRead(target, targetTexture);
            TestContext.WriteLine("steady: forward uploads " + set.ForwardUploadCount + ", shadow uploads " + set.ShadowUploadCount + "; " + Describe(route));
            Assert.That(route.CulledIssueCount, Is.EqualTo(2));
            Assert.That(route.LastShadowSkipReason, Is.Null, "the cascade pass drew");
            Assert.That(set.ForwardUploadCount, Is.EqualTo(forwardUploads), "an unchanged forward view uploads nothing");
            Assert.That(set.ShadowUploadCount, Is.EqualTo(shadowUploads), "unchanged cascades upload nothing");
            Assert.That(CountDiffering(first, second), Is.Zero, "pixels of the frame without uploads");
            yield return null;

            target.transform.Rotate(0f, 12f, 0f, Space.World);
            route.Issue();
            RenderAndRead(target, targetTexture);
            Assert.That(set.ForwardUploadCount, Is.EqualTo(forwardUploads + 1), "a turned view uploads its forward selection");
        }
    }
}
