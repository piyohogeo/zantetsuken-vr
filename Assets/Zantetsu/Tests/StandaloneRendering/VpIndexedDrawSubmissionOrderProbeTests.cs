using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.StandaloneTests
{
    /// <summary>
    /// VP Stage 3 submission-order probe, run in a graphics-enabled non-XR Standalone Player. It does NOT implement
    /// or exercise any buffer lifetime mechanism and never frees a buffer while a draw may still reference it.
    /// <para>
    /// What it establishes: that the VP3 forward and shadow indexed indirect draws actually render in a Player, and
    /// what an async readback of each of the five buffers reports when it is requested at a stated position in the
    /// frame. The image result and the buffer readbacks are recorded separately and are NOT combined into a claim:
    /// a readback of the render target proves the draws that produced that image ran, but it does not prove that a
    /// separately issued readback of a buffer is ordered after the particular draw that used it.
    /// </para>
    /// <para>
    /// The completion boundary used is <see cref="WaitForEndOfFrame"/>, which resumes after every camera has
    /// rendered; the return of a Render call is not treated as a boundary.
    /// </para>
    /// </summary>
    public class VpIndexedDrawSubmissionOrderProbeTests
    {
        private const string ForwardMaterialResource = "VpIndexedIndirectForwardProbe";
        private const string ShadowMaterialResource = "VpIndexedIndirectShadowProbe";
        private const int ShadowSize = 128;
        private const int MarkerPixels = 200;
        private const int ShadowedPixels = 300;
        private const float VpShadowedFraction = 0.9f;
        private const double ReadbackTimeoutSeconds = 10.0;

        private static readonly Vector3[] PaddingQuad =
        {
            new Vector3(100f, 0f, 0f), new Vector3(101f, 0f, 0f), new Vector3(101f, 1f, 0f), new Vector3(100f, 1f, 0f),
        };

        private static readonly Matrix4x4[] TwoCubes =
        {
            Matrix4x4.Translate(new Vector3(-1.5f, 1.2f, 0f)),
            Matrix4x4.Translate(new Vector3(1.5f, 1.2f, 0f)),
        };

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.Destroy(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        /// <summary>
        /// The materials must come from Resources: a Player contains a shader only when something in the build
        /// references it, and Shader.Find alone would return null here.
        /// </summary>
        private Material LoadMaterial(string resourceName)
        {
            var loaded = Resources.Load<Material>(resourceName);
            Assert.That(loaded, Is.Not.Null, "Resources.Load<Material>(\"" + resourceName + "\")");
            Assert.That(loaded.shader, Is.Not.Null, resourceName + ": shader");
            Assert.That(loaded.shader.isSupported, Is.True, resourceName + ": shader " + loaded.shader.name + " is supported");
            TestContext.Out.WriteLine(resourceName + ": shader " + loaded.shader.name + ", passes " + loaded.shader.passCount);
            return Track(new Material(loaded));
        }

        /// <summary>
        /// A cube mesh that is certain to exist in a Player. Resources.GetBuiltinResource is what the EditMode tests
        /// use, but that has only been exercised in the Editor here; a primitive's shared mesh is a built-in asset the
        /// Player carries, and the ground plane already relies on the same route.
        /// </summary>
        private Mesh CubeMesh()
        {
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(primitive);
            Assert.That(mesh, Is.Not.Null, "cube mesh from a primitive");
            return mesh;
        }

        private Mesh FlatMesh(Vector3[] positions, int[] triangles)
        {
            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            var normals = new Vector3[positions.Length];
            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = Vector3.back;
            }

            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        private (Camera camera, RenderTexture target) LitTopDownView(Action<MeshRenderer, Light> inspect = null)
        {
            Light light = Track(new GameObject("VP Probe Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Probe Camera")).AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.Destroy(ground.GetComponent<Collider>());
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            inspect?.Invoke(groundRenderer, light);
            return (camera, target);
        }

        /// <summary>Reads the render target at the current point, through an async readback, and waits for it.</summary>
        private static IEnumerator ReadTarget(RenderTexture target, Action<Color32[], bool> onDone)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!request.done)
            {
                Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(ReadbackTimeoutSeconds), "render target readback completes");
                yield return null;
            }

            if (request.hasError)
            {
                onDone(null, true);
                yield break;
            }

            onDone(request.GetData<Color32>().ToArray(), false);
        }

        private static float[] GreenMarkedLuminance(Color32[] pixels)
        {
            var luminance = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                luminance[i] = p.g > p.r + 50 ? -1f : (p.r + p.g + p.b) / 3f;
            }

            return luminance;
        }

        private static float LitBrightness(float[] without)
        {
            float lit = 0f;
            foreach (float value in without)
            {
                lit += Mathf.Max(0f, value);
            }

            return lit / without.Length;
        }

        private static (int marker, int shadowed) MarkerAndShadow(float[] without, float[] with)
        {
            float lit = LitBrightness(without);
            int marker = 0;
            int shadowed = 0;
            for (int i = 0; i < with.Length; i++)
            {
                if (with[i] < 0f)
                {
                    marker++;
                }
                else if (with[i] < lit * VpShadowedFraction && !(without[i] >= 0f && without[i] < lit * VpShadowedFraction))
                {
                    shadowed++;
                }
            }

            return (marker, shadowed);
        }

        private sealed class ShadowCapture
        {
            public Color32[] pixels;
            public RenderTexture atlasCopy;
            public int frame;
        }

        private static void DiagnosticLog(string directory, string line)
        {
            TestContext.Out.WriteLine(line);
            if (!string.IsNullOrEmpty(directory))
            {
                File.AppendAllText(Path.Combine(directory, "measurements.txt"), line + Environment.NewLine);
            }
        }

        /// <summary>
        /// Fails before any shadow pixel is counted when the receiver cannot show a shadow at all: no shader, the
        /// error shader, an unsupported shader, or a renderer or material configured not to receive shadows. A zero
        /// shadow count from such a receiver would say nothing about the caster.
        /// </summary>
        private static void AssertReceivesShadows(string directory, string label, MeshRenderer renderer, Material material)
        {
            Shader shader = material == null ? null : material.shader;
            string shaderName = shader == null ? "<null>" : shader.name;
            bool receiveShadowsProperty = !material.HasProperty("_ReceiveShadows") || material.GetFloat("_ReceiveShadows") != 0f;
            DiagnosticLog(directory, label + ": receiver check shader=" + shaderName
                + ", supported=" + (shader != null && shader.isSupported)
                + ", renderer.receiveShadows=" + renderer.receiveShadows
                + ", _RECEIVE_SHADOWS_OFF=" + material.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF")
                + ", _ReceiveShadows=" + (material.HasProperty("_ReceiveShadows") ? material.GetFloat("_ReceiveShadows").ToString() : "absent"));
            Assert.That(shader, Is.Not.Null, label + ": the ground material has no shader");
            Assert.That(shaderName, Is.Not.EqualTo("Hidden/InternalErrorShader"), label + ": the ground fell back to the error shader");
            Assert.That(shader.isSupported, Is.True, label + ": the ground shader " + shaderName + " is not supported");
            Assert.That(renderer.receiveShadows, Is.True, label + ": the ground renderer does not receive shadows");
            Assert.That(material.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF"), Is.False, label + ": the ground material disables receiving shadows");
            Assert.That(receiveShadowsProperty, Is.True, label + ": the ground material has _ReceiveShadows = 0");
        }

        private static void DescribeMaterial(string directory, string label, Material material)
        {
            string passes = "";
            for (int i = 0; i < material.passCount; i++) passes += " " + material.GetPassName(i);
            DiagnosticLog(directory, label + ": material=" + material.name + ", shader=" + material.shader.name
                + ", supported=" + material.shader.isSupported + ", pipeline=" + material.GetTag("RenderPipeline", false, "<none>")
                + ", keywords=[" + string.Join(",", material.shaderKeywords) + "], passes=" + passes
                + ", _ReceiveShadows=" + (material.HasProperty("_ReceiveShadows") ? material.GetFloat("_ReceiveShadows").ToString() : "absent")
                + ", _RECEIVE_SHADOWS_OFF=" + material.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF")
                + ", color=" + (material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor").ToString()
                    : material.HasProperty("_Color") ? material.GetColor("_Color").ToString() : "absent"));
        }

        private void SaveColor(string directory, string label, Color32[] pixels, int width, int height)
        {
            Texture2D image = Track(new Texture2D(width, height, TextureFormat.RGBA32, false));
            image.SetPixels32(pixels);
            image.Apply(false);
            File.WriteAllBytes(Path.Combine(directory, label + ".png"), image.EncodeToPNG());
        }

        // Copy the atlas separately, at this camera's end event. This observes an existing global texture;
        // neither the presence of a shader nor shadow keywords alone establishes caster rasterization.
        private RenderTexture CopyAtlas(string directory, string label, Material copyDepth)
        {
            Texture atlas = Shader.GetGlobalTexture("_MainLightShadowmapTexture");
            if (atlas == null)
            {
                DiagnosticLog(directory, label + ": atlas unavailable at endCameraRendering; rasterization unconfirmed");
                return null;
            }

            DiagnosticLog(directory, label + ": atlas source=" + atlas.name + " " + atlas.width + "x" + atlas.height
                + ", id=" + atlas.GetInstanceID() + ", format=" + atlas.graphicsFormat);
            RenderTexture copy = Track(new RenderTexture(atlas.width, atlas.height, 0, RenderTextureFormat.RFloat));
            copy.Create();
            var properties = new MaterialPropertyBlock();
            properties.SetTexture("_CameraDepthAttachment", atlas);
            properties.SetVector("_BlitScaleBias", new Vector4(1f, 1f, 0f, 0f));
            using (var commands = new CommandBuffer { name = "VP test-only shadow atlas observation" })
            {
                // URP's earlier CopyDepth pass leaves these GLOBAL keywords set. Select color output and
                // single-sample input for this observation, then restore every prior global state.
                string[] keywords = { "_OUTPUT_DEPTH", "_DEPTH_MSAA_2", "_DEPTH_MSAA_4", "_DEPTH_MSAA_8" };
                var enabled = new bool[keywords.Length];
                for (int i = 0; i < keywords.Length; i++)
                {
                    enabled[i] = Shader.IsKeywordEnabled(keywords[i]);
                    commands.DisableShaderKeyword(keywords[i]);
                }
                commands.SetRenderTarget(copy);
                commands.SetViewport(new Rect(0, 0, copy.width, copy.height));
                commands.ClearRenderTarget(false, true, new Color(-1f, 0f, 0f, 0f));
                commands.DrawProcedural(Matrix4x4.identity, copyDepth, 0, MeshTopology.Triangles, 3, 1, properties);
                for (int i = 0; i < keywords.Length; i++)
                    if (enabled[i]) commands.EnableShaderKeyword(keywords[i]);
                Graphics.ExecuteCommandBuffer(commands);
            }
            return copy;
        }

        private IEnumerator CaptureShadowCase(string directory, string label, Camera camera, RenderTexture target,
            Material copyDepth, Action submit, bool explicitRequest, Action<ShadowCapture> done)
        {
            // Resume in a fresh frame: a previous async readback may have completed at end of frame.
            yield return null;
            var capture = new ShadowCapture();
            bool observed = false;
            Action<ScriptableRenderContext, Camera> endCamera = (context, renderedCamera) =>
            {
                if (renderedCamera != camera || observed) return;
                observed = true;
                capture.frame = Time.frameCount;
                DiagnosticLog(directory, label + ": rendered frame=" + capture.frame + ", enabled=" + camera.enabled
                    + ", stereoEnabled=" + camera.stereoEnabled + ", explicitRequest=" + explicitRequest
                    + ", mainLightPosition=" + Shader.GetGlobalVector("_MainLightPosition")
                    + ", mainLightColor=" + Shader.GetGlobalVector("_MainLightColor")
                    + ", shadowParams=" + Shader.GetGlobalVector("_MainLightShadowParams")
                    + ", mapSize=" + Shader.GetGlobalVector("_MainLightShadowmapSize")
                    + ", keywords(main,cascade,screen)=" + Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS") + ","
                    + Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_CASCADE") + "," + Shader.IsKeywordEnabled("_MAIN_LIGHT_SHADOWS_SCREEN"));
                for (int i = 0; i < 4; i++)
                    DiagnosticLog(directory, label + ": cascadeSphere" + i + "=" + Shader.GetGlobalVector("_CascadeShadowSplitSpheres" + i));
                capture.atlasCopy = CopyAtlas(directory, label, copyDepth);
            };
            RenderPipelineManager.endCameraRendering += endCamera;
            try
            {
                DiagnosticLog(directory, label + ": submit frame=" + Time.frameCount);
                submit();
                if (explicitRequest)
                {
                    var request = new RenderPipeline.StandardRequest { destination = target };
                    Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                yield return new WaitForEndOfFrame();
            }
            finally
            {
                RenderPipelineManager.endCameraRendering -= endCamera;
            }
            Assert.That(observed, Is.True, label + ": target camera rendered");
            DiagnosticLog(directory, label + ": final image readback requested frame=" + Time.frameCount);
            yield return ReadTarget(target, (pixels, error) =>
            {
                Assert.That(error, Is.False, label + ": color readback");
                capture.pixels = pixels;
            });
            SaveColor(directory, label, capture.pixels, target.width, target.height);
            if (capture.atlasCopy != null)
            {
                var request = AsyncGPUReadback.Request(capture.atlasCopy, 0, TextureFormat.RFloat);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!request.done)
                {
                    Assert.That(timer.Elapsed.TotalSeconds, Is.LessThan(ReadbackTimeoutSeconds), label + ": atlas readback timeout");
                    yield return null;
                }
                if (request.hasError)
                    DiagnosticLog(directory, label + ": atlas readback failed; rasterization unconfirmed");
                else
                {
                    File.WriteAllBytes(Path.Combine(directory, label + ".atlas.rfloat"), request.GetData<byte>().ToArray());
                    var depth = request.GetData<float>();
                    float min = float.MaxValue, max = float.MinValue;
                    int occupied = 0;
                    var preview = new Color32[depth.Length];
                    for (int i = 0; i < depth.Length; i++)
                    {
                        min = Mathf.Min(min, depth[i]); max = Mathf.Max(max, depth[i]);
                        bool nonClear = Mathf.Abs(depth[i] - (SystemInfo.usesReversedZBuffer ? 0f : 1f)) > 0.000001f;
                        if (nonClear) occupied++;
                        byte value = nonClear ? (byte)255 : (byte)0;
                        preview[i] = new Color32(value, value, value, 255);
                    }
                    SaveColor(directory, label + ".atlas-occupancy", preview, capture.atlasCopy.width, capture.atlasCopy.height);
                    DiagnosticLog(directory, label + ": atlas raw min=" + min + ", max=" + max + ", nonClear=" + occupied
                        + "/" + depth.Length + "; occupancy preview is binary; raw values saved separately");
                    if (min < 0f || max > 1f)
                        DiagnosticLog(directory, label + ": INVALID atlas copy (sentinel or out-of-range depth); rasterization unconfirmed");
                }
            }
            done(capture);
        }

        private static int CompareGround(string directory, string label, Color32[] before, Color32[] after, Camera camera)
        {
            int ground = 0, changed = 0, dark2 = 0, dark5 = 0, dark10 = 0, magentaBefore = 0, magentaAfter = 0;
            float maxDrop = 0f, totalDrop = 0f;
            int minX = ShadowSize, minY = ShadowSize, maxX = -1, maxY = -1;
            for (int i = 0; i < before.Length; i++)
            {
                Color32 a = before[i], b = after[i];
                if (a.r > 250 && a.b > 250 && a.g < 5) magentaBefore++;
                if (b.r > 250 && b.b > 250 && b.g < 5) magentaAfter++;
                int x = i % ShadowSize, y = i / ShadowSize;
                Ray ray = camera.ViewportPointToRay(new Vector3((x + 0.5f) / ShadowSize, (y + 0.5f) / ShadowSize));
                Vector3 hit = ray.origin + ray.direction * (-ray.origin.y / ray.direction.y);
                // The plane fills the RT. Exclude the fixed projected cube footprints AND markers in either image.
                // The footprint is symmetric in z, so this exclusion also holds for a vertically flipped readback.
                bool cubeFootprint = Mathf.Abs(hit.z) <= 0.51f && Mathf.Abs(Mathf.Abs(hit.x) - 1.5f) <= 0.51f;
                if (Mathf.Abs(hit.x) > 5f || Mathf.Abs(hit.z) > 5f || cubeFootprint || a.g > a.r + 50 || b.g > b.r + 50) continue;
                ground++;
                float drop = (a.r + a.g + a.b - b.r - b.g - b.b) / 3f;
                totalDrop += drop; maxDrop = Mathf.Max(maxDrop, drop);
                if (Math.Abs(a.r - b.r) > 2 || Math.Abs(a.g - b.g) > 2 || Math.Abs(a.b - b.b) > 2) changed++;
                if (drop > 2f) dark2++;
                if (drop > 5f)
                {
                    dark5++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                if (drop > 10f) dark10++;
            }
            (int marker, int oldShadowed) = MarkerAndShadow(GreenMarkedLuminance(before), GreenMarkedLuminance(after));
            DiagnosticLog(directory, label + ": sameGround=" + ground + ", changedRGB>2=" + changed
                + ", darker>2/5/10=" + dark2 + "/" + dark5 + "/" + dark10 + ", maxDrop=" + maxDrop
                + ", meanDrop=" + totalDrop / Math.Max(1, ground) + ", dark5Bounds=" + minX + "," + minY + ".." + maxX + "," + maxY
                + ", magentaBefore/After=" + magentaBefore + "/" + magentaAfter + ", oldMean=" + LitBrightness(GreenMarkedLuminance(before))
                + ", oldShadowed=" + oldShadowed + ", marker=" + marker);
            return dark5;
        }

        /// <summary>
        /// One Player build separates receiver validity from caster submission, with the native mesh as control.
        /// No scene assets, pipeline settings, shaders, runtime code, or buffer lifetime policy are changed.
        /// </summary>
        [UnityTest]
        public IEnumerator ShadowReceiverAndCasterControls_RecordFinalImagesAndAtlasSeparately()
        {
            string directory = Environment.GetEnvironmentVariable("VP3_SHADOW_DIAGNOSTICS");
            Assert.That(directory, Is.Not.Null.And.Not.Empty, "set VP3_SHADOW_DIAGNOSTICS to a fresh output directory");
            Directory.CreateDirectory(directory);
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True);
            DiagnosticLog(directory, "device=" + SystemInfo.graphicsDeviceType + ", gpu=" + SystemInfo.graphicsDeviceName
                + ", quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()] + ", colorSpace=" + QualitySettings.activeColorSpace
                + ", reversedZ=" + SystemInfo.usesReversedZBuffer + ", pipeline=" + GraphicsSettings.currentRenderPipeline);
            DiagnosticLog(directory, "pipeline data=" + JsonUtility.ToJson(GraphicsSettings.currentRenderPipeline));
            DiagnosticLog(directory, "RenderSettings.sun=" + RenderSettings.sun + ", ambient=" + RenderSettings.ambientMode
                + ", ambientIntensity=" + RenderSettings.ambientIntensity);
            foreach (Light existing in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                DiagnosticLog(directory, "existing light=" + existing.name + ", enabled=" + existing.enabled + ", type=" + existing.type
                    + ", intensity=" + existing.intensity + ", direction=" + existing.transform.forward);

            Material forward = LoadMaterial(ForwardMaterialResource);
            Material shadow = LoadMaterial(ShadowMaterialResource);
            Material lit = LoadMaterial("VpShadowDiagnosticLit");
            Material copyDepth = LoadMaterial("VpShadowDiagnosticCopyDepth");
            copyDepth.SetFloat("_ZWrite", 0f);
            MeshRenderer ground = null;
            Light sun = null;
            (Camera camera, RenderTexture target) = LitTopDownView((renderer, light) => { ground = renderer; sun = light; });
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            DescribeMaterial(directory, "primitive ground", ground.sharedMaterial);
            DescribeMaterial(directory, "explicit Lit ground", lit);
            DescribeMaterial(directory, "VP shadow", shadow);
            DiagnosticLog(directory, "sun=" + sun.name + ", direction=" + sun.transform.forward + ", shadows=" + sun.shadows
                + ", strength=" + sun.shadowStrength + ", bias=" + sun.shadowBias + ", normalBias=" + sun.shadowNormalBias
                + ", cullingMask=" + sun.cullingMask + "; camera position=" + camera.transform.position + ", rotation=" + camera.transform.eulerAngles
                + ", ortho=" + camera.orthographicSize + ", near/far=" + camera.nearClipPlane + "/" + camera.farClipPlane
                + ", mask=" + camera.cullingMask + ", groundBounds=" + ground.bounds);
            DiagnosticLog(directory, "geometric ground shadow: x=[-2,-1] or [1,2], z=[0.4997,2.9279]; view x,z=[-4,4]; theoretical area~1243 pixels");

            Mesh cube = CubeMesh();
            var native = new MeshRenderer[TwoCubes.Length];
            Material greenLit = Track(new Material(lit));
            greenLit.SetColor("_BaseColor", Color.green);
            for (int i = 0; i < native.Length; i++)
            {
                GameObject obj = Track(new GameObject("VP Probe Native Cube " + i));
                obj.transform.position = TwoCubes[i].GetColumn(3);
                obj.AddComponent<MeshFilter>().sharedMesh = cube;
                native[i] = obj.AddComponent<MeshRenderer>();
                native[i].sharedMaterial = greenLit;
                native[i].receiveShadows = false;
                native[i].enabled = false;
            }

            int indexCount = (int)cube.GetIndexCount(0);
            using (var pool = new VpCpuGeometryPool(cube.vertexCount + 4, indexCount + 6, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(cube.vertexCount + 4, indexCount + 6))
            using (var batch = new VpIndexedIndirectDrawBatch(1, TwoCubes.Length))
            {
                Assert.That(pool.TryAppend(FlatMesh(PaddingQuad, new[] { 0, 1, 2, 0, 2, 3 }), out _), Is.True);
                Assert.That(pool.TryAppend(cube, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(range, cube.bounds, TwoCubes.Length) }, TwoCubes, false), Is.True);
                DiagnosticLog(directory, "VP worldBounds=" + batch.WorldBounds + ", commands=" + batch.CommandCount
                    + ", vertices=" + cube.vertexCount + ", indices=" + indexCount + ", range=" + range.vertexStart + "/" + range.indexStart);
                Action forwardOnly = () => batch.RenderForward(forward, new MaterialPropertyBlock(), buffers, 0, 0, batch.CommandCount, camera);
                Action both = () => batch.Render(forward, shadow, new MaterialPropertyBlock(), buffers, 0, 0, batch.CommandCount, camera);
                ShadowCapture before = null, after = null, repeated = null;
                yield return CaptureShadowCase(directory, "01-default-vp-forward", camera, target, copyDepth, forwardOnly, false, value => before = value);
                yield return CaptureShadowCase(directory, "02-default-vp-both", camera, target, copyDepth, both, false, value => after = value);
                CompareGround(directory, "DEFAULT VP", before.pixels, after.pixels, camera);
                foreach (MeshRenderer renderer in native) { renderer.enabled = true; renderer.shadowCastingMode = ShadowCastingMode.Off; }
                yield return CaptureShadowCase(directory, "03-default-mesh-off", camera, target, copyDepth, () => { }, false, value => before = value);
                foreach (MeshRenderer renderer in native) renderer.shadowCastingMode = ShadowCastingMode.On;
                yield return CaptureShadowCase(directory, "04-default-mesh-on", camera, target, copyDepth, () => { }, false, value => after = value);
                CompareGround(directory, "DEFAULT MESH", before.pixels, after.pixels, camera);

                ground.sharedMaterial = lit;
                AssertReceivesShadows(directory, "explicit Lit ground", ground, ground.sharedMaterial);
                foreach (MeshRenderer renderer in native) renderer.shadowCastingMode = ShadowCastingMode.Off;
                yield return CaptureShadowCase(directory, "05-lit-mesh-off", camera, target, copyDepth, () => { }, false, value => before = value);
                foreach (MeshRenderer renderer in native) renderer.shadowCastingMode = ShadowCastingMode.On;
                yield return CaptureShadowCase(directory, "06-lit-mesh-on", camera, target, copyDepth, () => { }, false, value => after = value);
                int meshDark = CompareGround(directory, "LIT MESH", before.pixels, after.pixels, camera);

                foreach (MeshRenderer renderer in native) renderer.enabled = false;
                yield return CaptureShadowCase(directory, "07-lit-vp-forward", camera, target, copyDepth, forwardOnly, false, value => before = value);
                yield return CaptureShadowCase(directory, "08-lit-vp-both", camera, target, copyDepth, both, false, value => after = value);
                int vpDark = CompareGround(directory, "LIT VP", before.pixels, after.pixels, camera);
                yield return CaptureShadowCase(directory, "09-lit-vp-forward-repeat", camera, target, copyDepth, forwardOnly, false, value => repeated = value);
                int drift = CompareGround(directory, "LIT VP BASELINE REPEAT", before.pixels, repeated.pixels, camera);

                camera.enabled = false;
                yield return CaptureShadowCase(directory, "10-lit-request-forward", camera, target, copyDepth, forwardOnly, true, value => before = value);
                yield return CaptureShadowCase(directory, "11-lit-request-both", camera, target, copyDepth, both, true, value => after = value);
                int requestDark = CompareGround(directory, "LIT VP EXPLICIT REQUEST", before.pixels, after.pixels, camera);
                DiagnosticLog(directory, "classification=" + (meshDark <= ShadowedPixels ? "lighting/receiver/probe control failed"
                    : vpDark <= ShadowedPixels ? "native control passed; investigate VP caster path" : "native and VP shadows verified with explicit Lit receiver"));
                Assert.That(meshDark, Is.GreaterThan(ShadowedPixels), "native Mesh control must cast onto explicit URP Lit ground");
                Assert.That(vpDark, Is.GreaterThan(ShadowedPixels), "VP must cast onto explicit URP Lit ground");
                Assert.That(requestDark, Is.GreaterThan(ShadowedPixels), "disabled camera with explicit request");
                Assert.That(drift, Is.Zero, "repeated forward baseline has no darkened ground pixels");
            }
        }

        /// <summary>
        /// Forward and shadow draws render in the Player, and a readback of each of the five buffers requested after
        /// the end-of-frame boundary is accepted and completes. The two findings are reported separately.
        /// </summary>
        [UnityTest]
        public IEnumerator TheForwardAndShadowDraws_Render_AndBufferReadbacksRequestedAfterEndOfFrame_AreRecordedSeparately()
        {
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True, SystemInfo.graphicsDeviceType.ToString());
            TestContext.Out.WriteLine("graphics device: " + SystemInfo.graphicsDeviceType + ", shadows: " + SystemInfo.supportsShadows);

            // The same output directory as the diagnostic test when it is set; null only disables the file copy.
            string directory = Environment.GetEnvironmentVariable("VP3_SHADOW_DIAGNOSTICS");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Material forward = LoadMaterial(ForwardMaterialResource);
            Material shadow = LoadMaterial(ShadowMaterialResource);
            Material lit = LoadMaterial("VpShadowDiagnosticLit");
            MeshRenderer ground = null;
            (Camera camera, RenderTexture target) = LitTopDownView((renderer, light) => ground = renderer);

            // The receiver decides whether a shadow can appear at all. The primitive's default material was the
            // condition under which this probe measured zero shadow pixels, so the ground gets an explicit URP Lit
            // material and its ability to receive a shadow is established before any shadow pixel is counted.
            ground.sharedMaterial = lit;
            AssertReceivesShadows(directory, "explicit Lit ground", ground, ground.sharedMaterial);

            Mesh cube = CubeMesh();
            int indexCount = (int)cube.GetIndexCount(0);

            var pool = new VpCpuGeometryPool(cube.vertexCount + 4, indexCount + 6, Allocator.Persistent);
            var buffers = new VpGpuIndexedGeometryBuffers(cube.vertexCount + 4, indexCount + 6);
            var batch = new VpIndexedIndirectDrawBatch(1, TwoCubes.Length);
            try
            {
                Assert.That(pool.TryAppend(FlatMesh(PaddingQuad, new[] { 0, 1, 2, 0, 2, 3 }), out _), Is.True, "append padding");
                Assert.That(pool.TryAppend(cube, out VpGeometryRange range), Is.True, "append cube");
                Assert.That(buffers.TryUpload(pool), Is.True, "upload geometry");
                Assert.That(
                    batch.TryUpload(new[] { new VpIndirectCommand(range, cube.bounds, TwoCubes.Length) }, TwoCubes, false),
                    Is.True,
                    "upload commands");

                // Frame 1: the forward call only, so the ground is lit and the cubes are marked but cast no shadow.
                var properties = new MaterialPropertyBlock();
                batch.RenderForward(forward, properties, buffers, 0, 0, batch.CommandCount, camera);
                yield return new WaitForEndOfFrame();
                Color32[] withoutShadow = null;
                bool withoutError = false;
                yield return ReadTarget(target, (pixels, error) => { withoutShadow = pixels; withoutError = error; });
                Assert.That(withoutError, Is.False, "forward-only render target readback has no error");

                // Frame 2: forward and shadow, so the cubes also cast onto the ground.
                var bothProperties = new MaterialPropertyBlock();
                batch.Render(forward, shadow, bothProperties, buffers, 0, 0, batch.CommandCount, camera);
                yield return new WaitForEndOfFrame();

                // Position of the buffer readback requests: after the end-of-frame boundary of the frame whose
                // cameras rendered the forward and shadow calls above. Recorded as a position only.
                int requestFrame = Time.frameCount;
                var results = new List<string>();
                // Only two of the five buffers can be observed from here. The batch's forward argument, shadow
                // argument and instance buffers are internal to Zantetsu.Rendering, which exposes its internals
                // only to Zantetsu.Core.EditModeTests; widening that would mean editing the runtime assembly,
                // which this probe must not do. The two below are the ones a draw references directly: the index
                // buffer is passed to Graphics.RenderPrimitivesIndexedIndirect by value, and the vertex buffer is
                // bound through the MaterialPropertyBlock.
                (GraphicsBuffer buffer, int stride, string label)[] cases =
                {
                    (buffers.VertexBuffer, VpRenderVertex.Stride, "vertices (Structured, bound via MaterialPropertyBlock)"),
                    (buffers.IndexBuffer, VpGpuIndexedGeometryBuffers.IndexStride, "indices (Index, direct draw argument)"),
                };

                var requests = new AsyncGPUReadbackRequest[cases.Length];
                for (int i = 0; i < cases.Length; i++)
                {
                    bool accepted;
                    string failure = null;
                    try
                    {
                        requests[i] = AsyncGPUReadback.Request(cases[i].buffer, cases[i].stride, 0);
                        accepted = true;
                    }
                    catch (Exception error)
                    {
                        accepted = false;
                        failure = error.GetType().Name + ": " + error.Message;
                    }

                    results.Add(cases[i].label + ": requested at frame " + requestFrame + " (after WaitForEndOfFrame), "
                        + (accepted ? "accepted" : "REJECTED " + failure));
                    Assert.That(accepted, Is.True, cases[i].label + ": readback request accepted (" + failure + ")");
                }

                Color32[] withShadow = null;
                bool withError = false;
                yield return ReadTarget(target, (pixels, error) => { withShadow = pixels; withError = error; });
                Assert.That(withError, Is.False, "forward+shadow render target readback has no error");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < requests.Length; i++)
                {
                    while (!requests[i].done)
                    {
                        Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(ReadbackTimeoutSeconds), cases[i].label + ": readback completes");
                        yield return null;
                    }

                    results[i] += ", done at frame " + Time.frameCount + ", hasError=" + requests[i].hasError;
                }

                TestContext.Out.WriteLine("--- buffer readbacks (request position and result, recorded separately from the image) ---");
                foreach (string line in results)
                {
                    TestContext.Out.WriteLine("  " + line);
                }

                for (int i = 0; i < requests.Length; i++)
                {
                    Assert.That(requests[i].hasError, Is.False, cases[i].label + ": readback completed without error");
                }

                // The image result, reported and asserted on its own. The darkening is counted over the same ground
                // pixels in both frames, with the cubes' projected footprints and the green markers excluded, so a
                // shadow is what is measured rather than the cubes themselves. The criterion stays functional
                // (more than ShadowedPixels darkened pixels); no pixel count is fixed as an expected value.
                float[] without = GreenMarkedLuminance(withoutShadow);
                float[] with = GreenMarkedLuminance(withShadow);
                (int marker, int _) = MarkerAndShadow(without, with);
                DiagnosticLog(directory, "--- image result (original probe) ---");
                DiagnosticLog(directory, "  lit brightness (forward only) = " + LitBrightness(without)
                    + ", marker (VP forward) pixels = " + marker);
                int vpShadowed = CompareGround(directory, "ORIGINAL PROBE VP FORWARD vs BOTH", withoutShadow, withShadow, camera);
                Assert.That(LitBrightness(without), Is.GreaterThan(40f), "the ground is lit in the forward-only frame");
                Assert.That(marker, Is.GreaterThan(MarkerPixels), "the VP forward draw covers pixels");
                Assert.That(vpShadowed, Is.GreaterThan(ShadowedPixels), "the VP shadow draw darkens the explicit Lit ground");
            }
            finally
            {
                batch.Dispose();
                buffers.Dispose();
                pool.Dispose();
            }
        }
    }
}
