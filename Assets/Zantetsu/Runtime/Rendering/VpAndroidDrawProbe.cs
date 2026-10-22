#if ZANTETSU_ANDROID_FLAT && UNITY_ANDROID
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>Opt-in phone diagnostic. Fixed geometry, no GPU culling, one image per draw API/clip case.</summary>
    public sealed class VpAndroidDrawProbe : MonoBehaviour
    {
        private const int Size = 96;
        private const int Layer = 31;
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        private readonly List<string> _report = new List<string>();
        private string _directory;
        private Camera _camera;
        private RenderTexture _target;
        private Light _light;
        private MeshRenderer _baseline;
        private Material _material, _noClip, _noClipInstanced, _simple;
        private readonly List<KeyValuePair<string, Material>> _diagnostics = new List<KeyValuePair<string, Material>>();
        private VpCpuGeometryPool _pool;
        private VpGpuIndexedGeometryBuffers _buffers;
        private VpIndexedIndirectDrawBatch _batch;
        private VpGeometryRange _range;
        private Bounds _bounds;
        private bool _disposed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-zantetsuVpDrawProbe", StringComparison.OrdinalIgnoreCase))) return;
            var host = new GameObject("VP Android draw probe (opt-in)");
            DontDestroyOnLoad(host);
            host.AddComponent<VpAndroidDrawProbe>();
        }

        private IEnumerator Start()
        {
            // Let the phone scene, camera and SRP initialize before submitting an isolated render request.
            for (int frame = 0; frame < 180 && (frame < 12 || Camera.main == null || GraphicsSettings.currentRenderPipeline == null); frame++)
                yield return null;
            _directory = Path.Combine(Application.persistentDataPath, "vp-draw-probe", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_directory);
            try
            {
                if (!Initialize()) yield break;
                Run("01-mesh-baseline", () => { _baseline.enabled = true; }, false, _baseline.sharedMaterial);
                yield return null;
                Run("02-noclip-first0", () => Draw(false, 1, 0, 1, false), true, _noClip);
                yield return null;
                Run("03-noclip-first1", () => Draw(false, 2, 1, 1, false), true, _noClip);
                yield return null;
                Run("04-noclip-start1-count2", () => Draw(false, 3, 1, 2, false), true, _noClip);
                yield return null;
                Run("05-pixelclip-plane0", () => Draw(true, 1, 0, 1, false), true);
                yield return null;
                Run("06-pixelclip-half", () => Draw(true, 1, 0, 1, true), true);
                yield return null;
                Run("07-legacy-indexed-indirect", DrawLegacy, true, _noClip);
                yield return null;
                Run("08-simple-shader-indexed-indirect", DrawSimple, false, _simple);
                yield return null;
                foreach (var diagnostic in _diagnostics)
                {
                    Material diagnosticMaterial = diagnostic.Value;
                    Run(diagnostic.Key, () => DrawMaterial(diagnosticMaterial, false), false, diagnosticMaterial);
                    yield return null;
                }
                Run("18-product-noclip-global-off", () => Draw(false, 1, 0, 1, false), false, _noClip);
                yield return null;
                Run("22-product-noclip-instancing-on", () => DrawMaterial(_noClipInstanced, false), true, _noClipInstanced);
                yield return null;
                Log("COMPLETE; images=" + _directory);
            }
            finally
            {
                DisposeProbe();
                Destroy(gameObject);
            }
        }

        private bool Initialize()
        {
            try
            {
                Log("device=" + SystemInfo.deviceModel + "; gpu=" + SystemInfo.graphicsDeviceName
                    + "; api=" + SystemInfo.graphicsDeviceType + "; shaderLevel=" + SystemInfo.graphicsShaderLevel
                    + "; instancing=" + SystemInfo.supportsInstancing + "; indirect=" + SystemInfo.supportsIndirectArgumentsBuffer
                    + "; compute=" + SystemInfo.supportsComputeShaders + "; argsSize=" + GraphicsBuffer.IndirectDrawIndexedArgs.size
                    + "; vertexStride=" + VpRenderVertex.Stride + "; pixelGlobal=" + Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword)
                    + "; legacy32Global=" + Shader.IsKeywordEnabled("VP_DIAGNOSTIC_LEGACY32"));
                Log("caps: activeTier=" + Graphics.activeTier + "; arrayTextures=" + SystemInfo.supports2DArrayTextures
                    + "; randomWriteTargets=" + SystemInfo.supportedRandomWriteTargetCount
                    + "; multisampledTextures=" + SystemInfo.supportsMultisampledTextures);
                Shader shader = Shader.Find(ShaderName);
                Log("shader=" + ShaderName + "; found=" + (shader != null) + "; supported=" + (shader != null && shader.isSupported)
                    + "; pipeline=" + GraphicsSettings.currentRenderPipeline);
                if (shader == null) throw new InvalidOperationException("VP shader missing");
                _material = Own(new Material(shader));
                _material.SetColor("_BaseColor", Color.green);
                _material.SetTexture("_BaseMap", Texture2D.whiteTexture);
                _material.SetFloat("_VpUsePaletteAtlas", 0f);
                _noClip = Own(new Material(_material));
                _noClip.EnableKeyword(VpGpuCullSetup.NoClipKeyword);
                _noClipInstanced = Own(new Material(_noClip));
                _noClipInstanced.enableInstancing = true;
                Shader simpleShader = Shader.Find("Hidden/Zantetsu/Android Draw Probe");
                Log("simpleShader=" + simpleShader + "; supported=" + (simpleShader != null && simpleShader.isSupported));
                if (simpleShader != null)
                {
                    _simple = Own(new Material(simpleShader));
                    ConfigureMaterial(_simple);
                    AddDiagnostic("09-simple-cull-back", null, CullMode.Back);
                    AddDiagnostic("10-simple-cull-front", null, CullMode.Front);
                    AddDiagnostic("11-simple-unity-indirect", "VP_PROBE_INDIRECT");
                    AddDiagnostic("12-simple-instancing-off", "VP_PROBE_INSTANCING");
                    AddDiagnostic("13-simple-instancing-on", "VP_PROBE_INSTANCING", CullMode.Off, true);
                    AddDiagnostic("14-product-vertex-green", "VP_PROBE_PRODUCT_VERTEX");
                    AddDiagnostic("15-simple-packed-decode", "VP_PROBE_DECODE");
                    AddDiagnostic("16-product-vertex-packed-decode", "VP_PROBE_PRODUCT_DECODE");
                    AddDiagnostic("17-simple-product-lighting", "VP_PROBE_LIGHTING");
                }
                _target = Own(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
                _target.Create();
                _camera = Own(new GameObject("VP draw probe camera")).AddComponent<Camera>();
                _camera.enabled = false;
                _camera.cullingMask = 1 << Layer;
                _camera.orthographic = true;
                _camera.orthographicSize = 1f;
                _camera.aspect = 1f;
                _camera.transform.position = new Vector3(0f, 0f, -5f);
                _camera.nearClipPlane = 0.1f;
                _camera.farClipPlane = 10f;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.25f, 0f, 0f, 1f);
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                _camera.targetTexture = _target;
                _light = Own(new GameObject("VP draw probe light")).AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.color = Color.white;
                _light.intensity = 2f;
                _light.shadows = LightShadows.None;
                _light.cullingMask = 1 << Layer;
                _light.enabled = false;

                var mesh = Own(new Mesh { name = "VP draw probe quad" });
                mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f) };
                mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                mesh.uv = new[] { Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                _bounds = mesh.bounds;
                var baselineObject = Own(new GameObject("VP draw probe baseline") { layer = Layer });
                baselineObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                _baseline = baselineObject.AddComponent<MeshRenderer>();
                _baseline.enabled = false;
                _baseline.shadowCastingMode = ShadowCastingMode.Off;
                _baseline.receiveShadows = false;
                Shader baselineShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Zantetsu/VP Mesh Surface");
                Log("baselineShader=" + baselineShader + "; supported=" + (baselineShader != null && baselineShader.isSupported));
                if (baselineShader == null) throw new InvalidOperationException("Baseline mesh shader missing");
                var baselineMaterial = Own(new Material(baselineShader));
                baselineMaterial.SetColor("_BaseColor", Color.green);
                baselineMaterial.SetTexture("_BaseMap", Texture2D.whiteTexture);
                _baseline.sharedMaterial = baselineMaterial;

                _pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent);
                _buffers = new VpGpuIndexedGeometryBuffers(8, 12);
                _batch = new VpIndexedIndirectDrawBatch(3, 3);
                if (!_pool.TryAppend(mesh, out _range) || !_buffers.TryUpload(_pool)) throw new InvalidOperationException("Probe geometry upload failed");
                Log("fixture=4 vertices / 6 indices; RT=" + Size + "x" + Size + "; layer=" + Layer + "; MSAA=" + _target.antiAliasing);
                Log("camera: orthographic=" + _camera.orthographic + "; orthoSize=" + _camera.orthographicSize
                    + "; aspect=" + _camera.aspect + "; near=" + _camera.nearClipPlane + "; far=" + _camera.farClipPlane
                    + "; position=" + _camera.transform.position + "; targetFormat=" + _target.graphicsFormat
                    + "; screen=" + Screen.width + "x" + Screen.height);
                return true;
            }
            catch (Exception error) { Log("INITIALIZE ERROR: " + error); return false; }
        }

        private static void ConfigureMaterial(Material material)
        {
            material.SetColor("_BaseColor", Color.green);
            material.SetTexture("_BaseMap", Texture2D.whiteTexture);
            material.SetTextureScale("_BaseMap", Vector2.one);
            material.SetTextureOffset("_BaseMap", Vector2.zero);
            if (material.HasProperty("_VpUsePaletteAtlas")) material.SetFloat("_VpUsePaletteAtlas", 0f);
        }

        private void AddDiagnostic(string name, string keyword, CullMode cull = CullMode.Off, bool instancing = false)
        {
            var material = Own(new Material(_simple));
            material.SetInteger("_VpProbeCull", (int)cull);
            material.enableInstancing = instancing;
            if (keyword != null) material.EnableKeyword(keyword);
            _diagnostics.Add(new KeyValuePair<string, Material>(name, material));
        }

        private void Upload(int commands, bool half)
        {
            var draws = new VpIndirectCommand[commands];
            var transforms = new Matrix4x4[commands];
            var clips = new VpInstanceClip[commands];
            for (int i = 0; i < commands; i++)
            {
                draws[i] = new VpIndirectCommand(_range, _bounds, 1);
                transforms[i] = commands == 1 ? Matrix4x4.identity : Matrix4x4.Translate(new Vector3(-100f, 0f, 0f));
                clips[i] = half ? VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, 0f), 1f) : VpInstanceClip.None;
            }
            if (commands == 2) transforms[1] = Matrix4x4.identity;
            if (commands == 3)
            {
                transforms[1] = Matrix4x4.TRS(new Vector3(-0.35f, 0f, 0f), Quaternion.identity, Vector3.one * 0.6f);
                transforms[2] = Matrix4x4.TRS(new Vector3(0.35f, 0f, 0f), Quaternion.identity, Vector3.one * 0.6f);
            }
            if (!_batch.TryUpload(draws, transforms, clips, false)) throw new InvalidOperationException("Probe batch upload failed");
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs[commands];
            _batch.ForwardArgumentBuffer.GetData(args, 0, 0, commands);
            for (int i = 0; i < args.Length; i++)
                Log("args[" + i + "]=" + args[i].indexCountPerInstance + "," + args[i].instanceCount + "," + args[i].startIndex
                    + "," + args[i].baseVertexIndex + "," + args[i].startInstance);
        }

        private void Draw(bool clip, int uploadedCommands, int startCommand, int commandCount, bool half)
        {
            Upload(uploadedCommands, half);
            _batch.RenderForward(clip ? _material : _noClip, new MaterialPropertyBlock(), _buffers, Layer, startCommand, commandCount, _camera);
        }

        private void DrawLegacy()
        {
            Upload(1, false);
            var properties = new MaterialPropertyBlock();
            properties.SetBuffer("_VpVertices", _buffers.VertexBuffer);
            properties.SetBuffer("_VpInstanceObjectToWorld", _batch.RetirementFence);
            properties.SetInteger("_VpInstanceMultiplier", 1);
            properties.SetBuffer("unity_IndirectDrawArgs", _batch.ForwardArgumentBuffer);
            properties.SetInteger("unity_BaseCommandID", 0);
#pragma warning disable CS0618 // Intentional comparison with the older indexed indirect API.
            Graphics.DrawProceduralIndirect(_noClip, _batch.WorldBounds, MeshTopology.Triangles, _buffers.IndexBuffer,
                _batch.ForwardArgumentBuffer, 0, _camera, properties, ShadowCastingMode.Off, false, Layer);
#pragma warning restore CS0618
        }

        private void DrawSimple()
        {
            DrawMaterial(_simple, false);
        }

        private void DrawMaterial(Material material, bool half)
        {
            if (material == null) throw new InvalidOperationException("Diagnostic shader missing");
            Upload(1, half);
            var properties = new MaterialPropertyBlock();
            properties.SetInteger("_VpProbeInstanceOffset", 0);
            _batch.RenderForward(material, properties, _buffers, Layer, 0, 1, _camera);
        }

        private void Run(string name, Action issue, bool pixelClip, Material reportedMaterial = null)
        {
            bool previousPixel = Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword);
            Light previousSun = RenderSettings.sun;
            Vector4 previousColor = Shader.GetGlobalVector("_VpSurfaceColorScale");
            Vector4 previousSurface = Shader.GetGlobalVector("_VpSurfaceMaterial");
            try
            {
                if (pixelClip) Shader.EnableKeyword(VpClipShaderPolicy.PixelClipKeyword);
                else Shader.DisableKeyword(VpClipShaderPolicy.PixelClipKeyword);
                _baseline.enabled = false;
                _light.enabled = true;
                RenderSettings.sun = _light;
                Shader.SetGlobalVector("_VpSurfaceColorScale", Vector4.one);
                Shader.SetGlobalVector("_VpSurfaceMaterial", Vector4.zero);
                Material reported = reportedMaterial != null ? reportedMaterial : _material;
                Log("BEGIN " + name + "; shader=" + reported.shader.name + "; supported=" + reported.shader.isSupported + "; passes=" + reported.passCount
                    + "; forwardPass=" + reported.FindPass("VpIndexedIndirectForward")
                    + "; pixelGlobal=" + Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword)
                    + "; noClipLocal=" + reported.IsKeywordEnabled(VpGpuCullSetup.NoClipKeyword)
                    + "; instancing=" + reported.enableInstancing + "; keywords=" + string.Join(",", reported.shaderKeywords));
                issue();
                var request = new RenderPipeline.StandardRequest { destination = _target };
                if (!RenderPipeline.SupportsRenderRequest(_camera, request)) throw new InvalidOperationException("SRP StandardRequest unsupported");
                RenderPipeline.SubmitRenderRequest(_camera, request);
                SaveImage(name);
            }
            catch (Exception error) { Log("ERROR " + name + ": " + error); }
            finally
            {
                _baseline.enabled = false;
                _light.enabled = false;
                RenderSettings.sun = previousSun;
                Shader.SetGlobalVector("_VpSurfaceColorScale", previousColor);
                Shader.SetGlobalVector("_VpSurfaceMaterial", previousSurface);
                if (previousPixel) Shader.EnableKeyword(VpClipShaderPolicy.PixelClipKeyword);
                else Shader.DisableKeyword(VpClipShaderPolicy.PixelClipKeyword);
            }
        }

        private void SaveImage(string name)
        {
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = _target;
                image.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                image.Apply(false);
                Color32[] pixels = image.GetPixels32();
                int green = 0, left = 0, right = 0, nonBlack = 0, changed = 0, black = 0;
                // The quad cannot cover the corner; use its actual RT color to avoid linear/sRGB assumptions.
                Color32 background = pixels[0];
                foreach (Color32 p in pixels)
                {
                    if (p.r > 16 || p.g > 16 || p.b > 16) nonBlack++; else black++;
                    if (Math.Abs(p.r - background.r) > 3 || Math.Abs(p.g - background.g) > 3 || Math.Abs(p.b - background.b) > 3) changed++;
                }
                for (int i = 0; i < pixels.Length; i++)
                    if (pixels[i].g > pixels[i].r + 30 && pixels[i].g > pixels[i].b + 30)
                    { green++; if (i % Size < Size / 2) left++; else right++; }
                File.WriteAllBytes(Path.Combine(_directory, name + ".png"), image.EncodeToPNG());
                Color32 center = pixels[(Size / 2) * Size + Size / 2];
                Log("RESULT " + name + "; green=" + green + "; left=" + left + "; right=" + right + "; nonBlack=" + nonBlack
                    + "; changed=" + changed + "; black=" + black + "; background=" + background.r + "," + background.g + "," + background.b
                    + "; center=" + center.r + "," + center.g + "," + center.b);
            }
            finally { RenderTexture.active = previous; Destroy(image); }
        }

        private T Own<T>(T value) where T : UnityEngine.Object { _owned.Add(value); return value; }
        private void Log(string text)
        {
            _report.Add(text);
            Debug.Log("VP DRAW PROBE: " + text);
            if (_directory != null) File.WriteAllLines(Path.Combine(_directory, "report.txt"), _report);
        }
        private void OnDestroy() { DisposeProbe(); }
        private void DisposeProbe()
        {
            if (_disposed) return;
            _disposed = true;
            _batch?.Dispose(); _buffers?.Dispose(); _pool?.Dispose();
            if (_camera != null) _camera.targetTexture = null;
            if (_target != null) _target.Release();
            for (int i = _owned.Count - 1; i >= 0; i--) if (_owned[i] != null) Destroy(_owned[i]);
            _owned.Clear();
        }
    }
}
#endif
