using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Urp.Tests
{
    /// <summary>
    /// Stage 3 per-cascade instance culling probe: in a perspective view with 4 URP shadow cascades, VP cubes and
    /// cylinders, slabs crossing the cascade splits, a large rotated box, cubes beside and behind the camera and a tall
    /// tower outside the camera frustum whose shadow falls into view, these issues render exactly the pixels of the Stage 3
    /// single batch (forward and shadow calls): the Stage 3 forward call with <see cref="VpCascadeShadowPass"/> drawing
    /// every instance into every cascade, the same with each cascade's culled selection, and the culled forward call of
    /// <see cref="VpCulledInstanceSet"/>, issued from beginCameraRendering, with the culled cascades. The culled runs must
    /// actually leave instances out, select the off-camera tower in some cascade but not in the forward view, select a
    /// crossing slab in two or more cascades, and select every instance the camera frustum touches.
    /// <para>
    /// Face culling: after the native pass boundary, the pass culls back faces for both the actual URP atlas and a
    /// diagnostic target. With the same submitted geometry and no other casters its map equals URP's texel for texel,
    /// while forced front-face culling differs. The image comparisons also cover culled selections, ground shadows,
    /// post-processing and a SceneView-type camera.
    /// </para>
    /// </summary>
    public class VpCascadeShadowPassTests
    {
        private const string ForwardShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const string CulledForwardShaderName = "Zantetsu/VP Culled Indexed Indirect Unlit";
        private const string CulledShadowShaderName = "Zantetsu/VP Culled Indexed Shadow Caster";
        private const int Size = 256;

        private readonly List<Object> _objects = new List<Object>();

        private enum Draw
        {
            SingleBatch,
            ForwardOnly,
            CascadePassAll,
            CascadePassCulled,
            CulledForwardAndCascades,
            /// <summary>The single batch, and the cascade pass drawing every instance into its own diagnostic target for comparison.</summary>
            SingleBatchAndDiagnosticTarget,
        }

        private sealed class Options
        {
            public bool withTower = true;
            public bool softShadows;
            public float instanceScale = 1f;
            public bool postProcessing;
            public CameraType cameraType = CameraType.Game;
            public VpShadowCullMode cullMode = VpShadowCullMode.Auto;
            public bool groundCastsShadows = true;
            public bool captureShadowMap;
            public bool cullSplits = true;
        }

        private sealed class Result
        {
            public Color32[] pixels;
            public float[] shadowTexels;
            public int splitCount;
            public string skipReason;
            public readonly int[] selected = new int[VpCulledInstanceSet.MaxSplits];
            public readonly int[] draws = new int[VpCulledInstanceSet.MaxSplits];
            public readonly int[] planes = new int[VpCulledInstanceSet.MaxSplits];
            public readonly bool[] towerSelected = new bool[VpCulledInstanceSet.MaxSplits];
            public readonly int[] slabSplits = new int[3];
            public int instanceCount;
            public bool towerOutsideCamera;
            public int forwardSelected = -1;
            public int forwardDraws = -1;
            public bool towerForwardSelected;
            public int touchedButNotForwardSelected;
            public bool cullFront;
            public TextureUVOrigin shadowOrigin;
            public TextureUVOrigin cameraTargetOrigin;
            public bool xrEnabled;
            public bool activeTargetBackBuffer;
            public bool postProcessEnabled;
            public CameraType cameraType;
            public VpShadowMapCopyPass.Comparison comparison;
            public bool copied;
            public Bounds casterBounds;
            public Bounds vpBounds;
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

        private Material Material(string shaderName, Color color)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            Material material = Track(new Material(shader));
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            return material;
        }

        private void InEmptyScene(Action body)
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                body();
            }
            finally
            {
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

        private (Camera camera, RenderTexture target) LitPerspectiveView(Options options)
        {
            Light light = Track(new GameObject("VP Cascade Test Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(45f, -40f, 0f);
            light.type = LightType.Directional;
            light.shadows = options.softShadows ? LightShadows.Soft : LightShadows.Hard;
            light.intensity = 1f;

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.transform.position = new Vector3(0f, 0f, 20f);
            ground.transform.localScale = new Vector3(12f, 1f, 12f);
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            // Exact depth comparisons isolate VP casters; the image tests keep URP's ground shadows enabled.
            groundRenderer.shadowCastingMode = options.groundCastsShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;

            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Cascade Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.cameraType = options.cameraType;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.fieldOfView = 55f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 120f;
            camera.transform.position = new Vector3(0f, 8f, -12f);
            camera.transform.LookAt(new Vector3(0f, 0f, 14f));
            if (options.postProcessing)
            {
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            }

            return (camera, target);
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

        /// <summary>
        /// Cubes first, then cylinders. The cubes end with three slabs along Z at x = 7.5, a large rotated box, cubes at
        /// x = -18 and behind the camera, and (when requested) the tower at x = 22, whose shadow falls towards -X, +Z into
        /// view.
        /// </summary>
        private static (VpIndirectCommand[] commands, Matrix4x4[] transforms, int[] slabs, int tower) Layout(
            VpGeometryRange cube, Bounds cubeBounds, VpGeometryRange cylinder, Bounds cylinderBounds, bool withTower, float instanceScale)
        {
            var cubes = new List<Matrix4x4>();
            var cylinders = new List<Matrix4x4>();
            for (int row = 0; row < 11; row++)
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

            var slabs = new List<int>();
            foreach (float z in new[] { 1f, 9f, 20f })
            {
                slabs.Add(cubes.Count);
                cubes.Add(Matrix4x4.TRS(new Vector3(7.5f, 0.75f, z), Quaternion.identity, new Vector3(1.5f, 1.5f, 12f)));
            }

            cubes.Add(Matrix4x4.TRS(new Vector3(-9f, 2.5f, 26f), Quaternion.Euler(20f, 35f, 10f), new Vector3(8f, 4f, 3f)));
            foreach (float z in new[] { 0f, 10f, 20f, 30f })
            {
                cubes.Add(Matrix4x4.TRS(new Vector3(-18f, 1f, z), Quaternion.identity, new Vector3(2f, 2f, 2f)));
            }

            cubes.Add(Matrix4x4.TRS(new Vector3(0f, 1f, -25f), Quaternion.identity, new Vector3(2f, 2f, 2f)));
            cubes.Add(Matrix4x4.TRS(new Vector3(6f, 1f, -22f), Quaternion.Euler(0f, 30f, 0f), new Vector3(2f, 2f, 2f)));
            int tower = -1;
            if (withTower)
            {
                tower = cubes.Count;
                cubes.Add(Matrix4x4.TRS(new Vector3(22f, 12.5f, 6f), Quaternion.identity, new Vector3(2f, 25f, 2f)));
            }

            return (
                new[] { new VpIndirectCommand(cube, cubeBounds, cubes.Count), new VpIndirectCommand(cylinder, cylinderBounds, cylinders.Count) },
                cubes.Concat(cylinders).Select(m => m * Matrix4x4.Scale(Vector3.one * instanceScale)).ToArray(),
                slabs.ToArray(),
                tower);
        }

        private Result Render(Draw draw, Options options = null)
        {
            options = options ?? new Options();
            var result = new Result();
            (Camera camera, RenderTexture target) = LitPerspectiveView(options);
            Material forward = Material(ForwardShaderName, Color.green);
            Material shadow = Material(ShadowShaderName, Color.green);
            Material culledForward = Material(CulledForwardShaderName, Color.green);
            Material culledShadow = Material(CulledShadowShaderName, Color.green);
            Mesh cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            Mesh cylinderMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            int vertices = cubeMesh.vertexCount + cylinderMesh.vertexCount;
            int indices = (int)(cubeMesh.GetIndexCount(0) + cylinderMesh.GetIndexCount(0));
            RTHandle diagnosticTarget = null;
            VpShadowMapCopyPass copyPass = null;
            using (var pool = new VpCpuGeometryPool(vertices, indices, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(vertices, indices))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 128))
            using (var set = new VpCulledInstanceSet(2, 128))
            {
                try
                {
                    Assert.That(pool.TryAppend(cubeMesh, out VpGeometryRange cube), Is.True);
                    Assert.That(pool.TryAppend(cylinderMesh, out VpGeometryRange cylinder), Is.True);
                    Assert.That(buffers.TryUpload(pool), Is.True);
                    (VpIndirectCommand[] commands, Matrix4x4[] transforms, int[] slabs, int tower) = Layout(cube, cubeMesh.bounds, cylinder, cylinderMesh.bounds, options.withTower, options.instanceScale);
                    Assert.That(batch.TryUpload(commands, transforms, false), Is.True);
                    Assert.That(set.TryUpload(commands, transforms, false), Is.True);
                    result.instanceCount = transforms.Length;
                    Plane[] cameraPlanes = GeometryUtility.CalculateFrustumPlanes(camera);
                    if (tower >= 0)
                    {
                        result.towerOutsideCamera = !GeometryUtility.TestPlanesAABB(cameraPlanes, set.InstanceWorldBounds(tower));
                    }

                    VpCascadeShadowPass pass = null;
                    if (draw != Draw.SingleBatch && draw != Draw.ForwardOnly)
                    {
                        pass = new VpCascadeShadowPass(set, buffers, culledShadow)
                        {
                            CullSplits = options.cullSplits && (draw == Draw.CascadePassCulled || draw == Draw.CulledForwardAndCascades),
                            CullMode = options.cullMode,
                        };
                    }

                    if (draw == Draw.SingleBatchAndDiagnosticTarget || options.captureShadowMap)
                    {
                        var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
                        int cascades = pipeline.shadowCascadeCount;
                        int size = pipeline.mainLightShadowmapResolution;
                        int height = cascades == 2 ? size >> 1 : size;
                        if (draw == Draw.SingleBatchAndDiagnosticTarget)
                        {
                            diagnosticTarget = ShadowUtils.AllocShadowRT(size, height, 16, 1, 0f, "VP Probe Diagnostic Shadow Target");
                            pass.DiagnosticTarget = diagnosticTarget;
                        }

                        copyPass = new VpShadowMapCopyPass(pass, size, height);
                    }

                    var properties = new MaterialPropertyBlock();
                    var eyePlanes = new Plane[2 * VpInstanceCulling.EyePlaneCount];

                    void OnBeginCamera(ScriptableRenderContext context, Camera rendering)
                    {
                        if (rendering != camera)
                        {
                            return;
                        }

                        if (draw == Draw.CulledForwardAndCascades)
                        {
                            int eyes = VpInstanceCulling.GetEyePlanes(rendering, eyePlanes);
                            set.SelectForward(eyePlanes, eyes);
                            set.UploadForward();
                            set.RenderForward(culledForward, properties, buffers, 0, rendering);
                            set.RenderShadowCasterBounds(culledShadow, buffers, 0, rendering);
                        }

                        ScriptableRenderer renderer = rendering.GetUniversalAdditionalCameraData().scriptableRenderer;
                        if (pass != null)
                        {
                            renderer.EnqueuePass(pass);
                        }

                        if (copyPass != null)
                        {
                            renderer.EnqueuePass(copyPass);
                        }
                    }

                    if (draw == Draw.SingleBatch || draw == Draw.SingleBatchAndDiagnosticTarget)
                    {
                        batch.Render(forward, shadow, properties, buffers, 0, camera);
                    }
                    else if (draw != Draw.CulledForwardAndCascades)
                    {
                        batch.RenderForward(forward, properties, buffers, 0, 0, batch.CommandCount, camera);
                    }

                    RenderPipelineManager.beginCameraRendering += OnBeginCamera;
                    try
                    {
                        result.pixels = RenderAndRead(camera, target);
                    }
                    finally
                    {
                        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
                    }

                    if (pass != null)
                    {
                        result.splitCount = pass.LastSplitCount;
                        result.skipReason = pass.LastSkipReason;
                        result.cullFront = pass.LastCullFront;
                        result.shadowOrigin = pass.LastShadowOrigin;
                        result.cameraTargetOrigin = pass.LastCameraTargetOrigin;
                        result.xrEnabled = pass.LastXrEnabled;
                        result.activeTargetBackBuffer = pass.LastActiveTargetBackBuffer;
                        result.postProcessEnabled = pass.LastPostProcessEnabled;
                        result.cameraType = pass.LastCameraType;
                        result.casterBounds = pass.LastCasterBounds;
                        result.vpBounds = set.WorldBounds;
                        for (int split = 0; split < pass.LastSplitCount; split++)
                        {
                            result.selected[split] = set.ShadowSelectedInstances(split);
                            result.draws[split] = set.ShadowDrawCount(split);
                            result.planes[split] = pass.SplitPlaneCount(split);
                            result.towerSelected[split] = tower >= 0 && set.IsShadowSelected(split, tower);
                            for (int s = 0; s < slabs.Length; s++)
                            {
                                result.slabSplits[s] += set.IsShadowSelected(split, slabs[s]) ? 1 : 0;
                            }
                        }
                    }

                    if (copyPass != null)
                    {
                        result.copied = copyPass.LastCopied;
                        if (copyPass.LastCopied)
                        {
                            if (options.captureShadowMap)
                            {
                                result.shadowTexels = copyPass.ReadUrpCopy();
                            }
                            else
                            {
                                result.comparison = copyPass.Compare();
                            }
                        }
                    }

                    if (draw == Draw.CulledForwardAndCascades)
                    {
                        result.forwardSelected = set.ForwardSelectedInstances;
                        result.forwardDraws = set.ForwardDrawCount;
                        result.towerForwardSelected = tower >= 0 && set.IsForwardSelected(tower);
                        for (int i = 0; i < transforms.Length; i++)
                        {
                            if (GeometryUtility.TestPlanesAABB(cameraPlanes, set.InstanceWorldBounds(i)) && !set.IsForwardSelected(i))
                            {
                                result.touchedButNotForwardSelected++;
                            }
                        }
                    }
                }
                finally
                {
                    copyPass?.Release();
                    diagnosticTarget?.Release();
                }
            }

            return result;
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

        private static int CountDiffering(float[] first, float[] second)
        {
            Assert.That(first, Is.Not.Null, "reference shadow readback");
            Assert.That(second, Is.Not.Null, "shadow readback");
            Assert.That(second.Length, Is.EqualTo(first.Length), "shadow map size");
            int differing = 0;
            for (int i = 0; i < first.Length; i++)
            {
                differing += first[i] != second[i] ? 1 : 0;
            }

            return differing;
        }

        private static string Describe(Result result)
        {
            return "splits " + result.splitCount + ", skip " + (result.skipReason ?? "none") + ", selected per split " + string.Join("/", result.selected.Take(result.splitCount))
                + " of " + result.instanceCount + ", draws per split " + string.Join("/", result.draws.Take(result.splitCount)) + ", planes per split "
                + string.Join("/", result.planes.Take(result.splitCount)) + ", tower selected " + string.Join("/", result.towerSelected.Take(result.splitCount))
                + ", slab split counts " + string.Join("/", result.slabSplits) + ", forward selected " + result.forwardSelected + " in " + result.forwardDraws
                + " draws, tower forward selected " + result.towerForwardSelected + "; " + DescribeCull(result);
        }

        private static string DescribeCull(Result result)
        {
            return "cull front " + result.cullFront + ", shadow origin " + result.shadowOrigin + ", camera target origin " + result.cameraTargetOrigin
                + ", xr " + result.xrEnabled + ", active target backbuffer " + result.activeTargetBackBuffer + ", post-processing " + result.postProcessEnabled
                + ", camera type " + result.cameraType;
        }

        [Test]
        public void TheCascadePass_DrawingEveryInstanceIntoEveryCascade_MatchesTheSingleShadowBatch()
        {
            InEmptyScene(() =>
            {
                Result reference = Render(Draw.SingleBatch);
                DestroyObjects();
                Result forwardOnly = Render(Draw.ForwardOnly);
                DestroyObjects();
                Result all = Render(Draw.CascadePassAll);
                TestContext.WriteLine("all instances: " + Describe(all));

                Assert.That(all.skipReason, Is.Null, "the pass drew");
                Assert.That(all.splitCount, Is.EqualTo(4), "cascades");
                Assert.That(all.cullFront, Is.False, "a render texture target culls back faces");
                Assert.That(CountDiffering(reference.pixels, forwardOnly.pixels), Is.GreaterThan(500), "shadows are visible in the reference");
                Assert.That(CountDiffering(reference.pixels, all.pixels), Is.Zero, "pixels differing from the single shadow batch");
            });
        }

        [Test]
        public void TheCascadePass_DrawingEachCascadesCulledSelection_MatchesTheSingleShadowBatch()
        {
            InEmptyScene(() =>
            {
                Result withoutTower = Render(Draw.SingleBatch, new Options { withTower = false });
                DestroyObjects();
                Result reference = Render(Draw.SingleBatch);
                DestroyObjects();
                Result culled = Render(Draw.CascadePassCulled);
                TestContext.WriteLine("culled: " + Describe(culled));

                Assert.That(culled.towerOutsideCamera, Is.True, "the tower is outside the camera frustum");
                Assert.That(CountDiffering(withoutTower.pixels, reference.pixels), Is.GreaterThan(100), "the off-camera tower's shadow shows in the reference");
                Assert.That(culled.skipReason, Is.Null, "the pass drew");
                Assert.That(culled.splitCount, Is.EqualTo(4), "cascades");
                Assert.That(culled.selected.Take(culled.splitCount).Sum(), Is.LessThan(culled.splitCount * culled.instanceCount), "some instances are culled from some cascade");
                Assert.That(culled.towerSelected.Any(selected => selected), Is.True, "the off-camera tower is selected in a cascade");
                Assert.That(culled.slabSplits.Max(), Is.GreaterThanOrEqualTo(2), "a slab crossing a cascade split is selected in two or more cascades");
                Assert.That(CountDiffering(reference.pixels, culled.pixels), Is.Zero, "pixels differing from the single shadow batch");
            });
        }

        [Test]
        public void SoftShadowsAndSmallInstances_TheCascadePathMatchesTheSingleBatch()
        {
            InEmptyScene(() =>
            {
                var options = new Options { softShadows = true, instanceScale = 0.4f };
                Result reference = Render(Draw.SingleBatch, options);
                DestroyObjects();
                Result all = Render(Draw.CascadePassAll, options);
                DestroyObjects();
                Result culled = Render(Draw.CulledForwardAndCascades, options);
                int allDiffering = CountDiffering(reference.pixels, all.pixels);
                int culledDiffering = CountDiffering(reference.pixels, culled.pixels);
                TestContext.WriteLine("soft, scale 0.4: every instance differing " + allDiffering + ", culled differing " + culledDiffering + "; " + Describe(culled));

                Assert.That(culled.skipReason, Is.Null, "the pass drew");
                Assert.That(allDiffering, Is.Zero, "pixels differing with every instance in every cascade");
                Assert.That(culledDiffering, Is.Zero, "pixels differing with the culled forward view and cascades");
            });
        }

        [Test]
        public void TheCulledForwardView_WithCulledCascades_MatchesTheSingleBatch()
        {
            InEmptyScene(() =>
            {
                Result reference = Render(Draw.SingleBatch);
                DestroyObjects();
                Result culled = Render(Draw.CulledForwardAndCascades);
                TestContext.WriteLine("culled forward and cascades: " + Describe(culled));

                Assert.That(culled.skipReason, Is.Null, "the pass drew");
                Assert.That(culled.splitCount, Is.EqualTo(4), "cascades");
                Assert.That(culled.forwardSelected, Is.GreaterThan(0).And.LessThan(culled.instanceCount), "some instances are culled from the forward view");
                Assert.That(culled.forwardDraws, Is.EqualTo(2), "forward draws");
                Assert.That(culled.touchedButNotForwardSelected, Is.Zero, "instances the camera frustum touches but the forward view does not select");
                Assert.That(culled.towerForwardSelected, Is.False, "the off-camera tower is not in the forward view");
                Assert.That(culled.towerSelected.Any(selected => selected), Is.True, "the off-camera tower is selected in a cascade");
                Assert.That(culled.slabSplits.Max(), Is.GreaterThanOrEqualTo(2), "a slab crossing a cascade split is selected in two or more cascades");
                Bounds grown = culled.casterBounds;
                grown.Expand(1e-3f);
                TestContext.WriteLine("caster bounds " + culled.casterBounds + ", VP bounds " + culled.vpBounds);
                Assert.That(grown.Contains(culled.vpBounds.min) && grown.Contains(culled.vpBounds.max), Is.True, "URP's shadow caster bounds enclose the VP instances");
                Assert.That(CountDiffering(reference.pixels, culled.pixels), Is.Zero, "pixels differing from the single batch");
            });
        }

        [Test]
        public void WithPostProcessing_AndASceneViewCamera_TheCulledPathMatchesTheSingleBatch()
        {
            InEmptyScene(() =>
            {
                foreach (Options options in new[] { new Options { postProcessing = true }, new Options { cameraType = CameraType.SceneView } })
                {
                    string label = options.postProcessing ? "post-processing" : "scene view camera";
                    Result reference = Render(Draw.SingleBatch, options);
                    DestroyObjects();
                    Result culled = Render(Draw.CulledForwardAndCascades, options);
                    DestroyObjects();
                    TestContext.WriteLine(label + ": " + Describe(culled));

                    Assert.That(culled.skipReason, Is.Null, label + ": the pass drew");
                    Assert.That(culled.cullFront, Is.False, label + ": a render texture target culls back faces");
                    if (options.postProcessing)
                    {
                        Assert.That(culled.postProcessEnabled, Is.True, label + ": URP post-processing is active");
                    }
                    else
                    {
                        Assert.That(culled.cameraType, Is.EqualTo(CameraType.SceneView), label + ": URP sees a scene view camera");
                    }

                    Assert.That(CountDiffering(reference.pixels, culled.pixels), Is.Zero, label + ": pixels differing from the single batch");
                }
            });
        }

        [Test]
        public void TheCascadePassShadowMap_EqualsUrpsTexelForTexel_WithTheRulesCullAndNotWithTheOther()
        {
            InEmptyScene(() =>
            {
                var results = new Dictionary<VpShadowCullMode, Result>();
                foreach (VpShadowCullMode mode in new[] { VpShadowCullMode.Auto, VpShadowCullMode.Back, VpShadowCullMode.Front })
                {
                    results[mode] = Render(Draw.SingleBatchAndDiagnosticTarget, new Options { cullMode = mode, groundCastsShadows = false });
                    DestroyObjects();
                    TestContext.WriteLine(mode + ": copied " + results[mode].copied + ", " + results[mode].comparison + "; " + DescribeCull(results[mode]));
                    Assert.That(results[mode].copied, Is.True, mode + ": both maps copied");
                    Assert.That(results[mode].comparison.readbackError, Is.False, mode + ": readback");
                    Assert.That(results[mode].comparison.urpWritten, Is.GreaterThan(10000), mode + ": URP's map holds the Stage 3 shadows");
                }

                Assert.That(results[VpShadowCullMode.Auto].cullFront, Is.False, "Auto picks back faces after the native pass boundary");
                Assert.That(results[VpShadowCullMode.Auto].comparison.differing, Is.Zero, "texels differing from URP with Auto culling");
                Assert.That(results[VpShadowCullMode.Back].comparison.differing, Is.Zero, "texels differing from URP with back faces culled");
                Assert.That(results[VpShadowCullMode.Front].comparison.differing, Is.GreaterThan(1000), "texels differing from URP with front faces culled");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheCascadePass_WritingUrpsShadowMap_MatchesTheSingleBatchTexelForTexel(bool postProcessing)
        {
            InEmptyScene(() =>
            {
                var options = new Options
                {
                    postProcessing = postProcessing,
                    // Other URP casters can differ by one D16 step even before the winding fix; isolate VP depth here.
                    // The existing image tests retain the ground caster and the culled selections.
                    groundCastsShadows = false,
                    captureShadowMap = true,
                    // Match the single batch's submitted geometry. Split culling may omit unused atlas texels.
                    cullSplits = false,
                };
                Result reference = Render(Draw.SingleBatch, options);
                DestroyObjects();
                Assert.That(reference.copied, Is.True, "baseline URP shadow map copied");
                Assert.That(reference.shadowTexels, Is.Not.Null, "baseline readback");
                float far = SystemInfo.usesReversedZBuffer ? 0f : 1f;
                Assert.That(reference.shadowTexels.Count(depth => depth != far), Is.GreaterThan(10000), "baseline has shadow depth");

                foreach (VpShadowCullMode mode in new[] { VpShadowCullMode.Auto, VpShadowCullMode.Back, VpShadowCullMode.Front })
                {
                    options.cullMode = mode;
                    Result injected = Render(Draw.CulledForwardAndCascades, options);
                    DestroyObjects();
                    Assert.That(injected.skipReason, Is.Null, mode + ": the injected pass drew");
                    Assert.That(injected.splitCount, Is.EqualTo(4), mode + ": cascades");
                    Assert.That(injected.copied, Is.True, mode + ": actual URP shadow map copied");
                    Assert.That(injected.postProcessEnabled, Is.EqualTo(postProcessing), mode + ": post-processing setting");
                    int differing = CountDiffering(reference.shadowTexels, injected.shadowTexels);
                    TestContext.WriteLine(mode + ": URP target differing texels=" + differing + ", ground caster=" + options.groundCastsShadows
                        + ", post-processing=" + postProcessing + "; " + Describe(injected));

                    if (mode == VpShadowCullMode.Front)
                    {
                        Assert.That(differing, Is.GreaterThan(1000), "forced front-face culling changes the actual URP shadow map");
                    }
                    else
                    {
                        Assert.That(injected.cullFront, Is.False, mode + ": back faces culled");
                        Assert.That(differing, Is.Zero, mode + ": actual URP shadow map matches the single batch exactly");
                        Assert.That(CountDiffering(reference.pixels, injected.pixels), Is.Zero, mode + ": forward rendering remains intact");
                    }
                }
            });
        }
    }
}
