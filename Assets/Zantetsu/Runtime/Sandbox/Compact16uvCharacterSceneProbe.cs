using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>Explicit diagnostic scene: actual skin renderer -> baked Mesh -> compact VP; no physics owner.</summary>
    public sealed class Compact16uvCharacterSceneProbe : MonoBehaviour
    {
        public GameObject[] models;
        public TextAsset intake;
        public Material meshMaterial, vpMaterial;
        public Texture2D normalAtlas, debugAtlas;
        public bool normalizeFixedScale;
        public AnimationClip[] animationClips;
        const int Size = 512;
        readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        readonly Report report = new Report();
        Camera camera;
        RenderTexture target;
        string output;
        bool finished;
        T Own<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }
        void OnGUI() { if (target != null) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), target, ScaleMode.ScaleToFit); }
        IEnumerator Start()
        {
            output = Environment.GetEnvironmentVariable("VP_CHARACTER_SCENE_DIAGNOSTICS");
            if (string.IsNullOrEmpty(output)) { Debug.LogError("Set VP_CHARACTER_SCENE_DIAGNOSTICS"); Application.Quit(2); yield break; }
            Directory.CreateDirectory(output);
            var routine = Run();
            while (true)
            {
                bool next;
                try { next = routine.MoveNext(); }
                catch (Exception error) { report.failure = error.ToString(); Debug.LogException(error); break; }
                if (!next) break;
                yield return routine.Current;
            }
            (routine as IDisposable)?.Dispose();
            VpCutSurfaceAtlas.Clear(); VpCutSurfaceColour.SetDebugEnabled(false);
            foreach (var item in owned) if (item != null) Destroy(item);
            report.passed = report.failure == null && report.comparisons.Count == (normalizeFixedScale ? 28 : 24) && report.comparisons.All(x => x.passed);
            report.fixedScalePrepared=normalizeFixedScale;
            report.unity = Application.unityVersion; report.graphics = SystemInfo.graphicsDeviceType.ToString();
            report.vertexStride = VpRenderVertex.Stride; report.development = Debug.isDebugBuild;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
            Debug.Log("CHARACTER SCENE END passed=" + report.passed + " comparisons=" + report.comparisons.Count);
            finished = true; Application.Quit(report.passed ? 0 : 1);
        }
        IEnumerator Run()
        {
            Require(VpRenderVertex.Stride == 16, "Expected normal 16B build");
            VpCutSurfaceAtlas.Bind(normalAtlas, debugAtlas);
            var manifest = JsonUtility.FromJson<Input>(intake.text);
            using var scaleCache=new VpFixedScaleSkinCache();
            target = Own(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32)); target.antiAliasing = 1; target.Create();
            camera = Own(new GameObject("Current pose diagnostic camera")).AddComponent<Camera>();
            camera.enabled = false; camera.orthographic = true; camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear; camera.cullingMask = 1 << 30;
            for (int family = 0; family < 2; family++)
            for (int pose = 0; pose < 2; pose++)
            {
                string familyName = family == 0 ? "character-casual" : "character-professional";
                string label = familyName + "-pose" + pose;
                var entry = manifest.assets.Single(e => e.family == familyName);
                var parent = Own(new GameObject(label));
                var instance = Instantiate(models[family], parent.transform);
                foreach (var t in instance.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 30;
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                foreach (var r in instance.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                var skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh != null && r.sharedMesh.name == entry.objectName);
                skin.enabled = true; skin.updateWhenOffscreen = true;
                skin.shadowCastingMode = ShadowCastingMode.Off; skin.receiveShadows = false;
                skin.sharedMaterials = Enumerable.Repeat(meshMaterial, skin.sharedMesh.subMeshCount).ToArray();
                var originalSkin=skin;
                using var preparedInput=normalizeFixedScale ? scaleCache.Prepare(skin,parent.transform) : null;
                if (preparedInput!=null) skin=preparedInput.Renderer;
                for (int b = 0; b < skin.bones.Length; b++) skin.bones[b].localRotation *= Quaternion.Euler(0, (b % 3 - 1) * 4f, (b % 5 - 2) * 3f);
                if (pose == 1)
                {
                    parent.transform.SetPositionAndRotation(new Vector3(3, -2, 5), Quaternion.Euler(17, 31, -12));
                    if (!normalizeFixedScale) parent.transform.localScale = new Vector3(1.3f, .8f, 1.1f);
                    skin.rootBone.localScale = Vector3.Scale(skin.rootBone.localScale, new Vector3(1.05f, .95f, 1.1f));
                }
                if (normalizeFixedScale)
                {
                    var clip=animationClips!=null && family<animationClips.Length ? animationClips[family] : null;
                    if (clip!=null) clip.SampleAnimation(instance,clip.length*(pose==0 ? .23f : .67f));
                    report.poses.Add(new PoseInfo { family=familyName,pose=pose,clip=clip!=null ? clip.name : null,
                        sampledTime=clip!=null ? clip.length*(pose==0 ? .23f : .67f) : 0,scale=preparedInput.FixedScale });
                    if (pose==0) MeasureInput(familyName,originalSkin,preparedInput,parent.transform);
                }
                yield return null; yield return null;
                var baked = Own(new Mesh());
                if (preparedInput!=null) preparedInput.BakeCurrentPose(baked); else skin.BakeMesh(baked,true);
                var source = skin.sharedMesh;
                Require(baked.vertexCount == entry.topologyMap.Length, "Pinned topology length");
                Require(source.uv.SequenceEqual(baked.uv) && source.triangles.SequenceEqual(baked.triangles), "Bake topology/UV changed");
                int count = baked.vertexCount, indicesCount = baked.triangles.Length;
                var packed = new NativeArray<VpRenderVertex>(count, Allocator.Temp);
                var indices = new NativeArray<uint>(indicesCount, Allocator.Temp);
                VpRenderVertex[] vertices; uint[] localIndices;
                try
                {
                    Require(VpMeshConverter.TryConvert(baked, packed, indices, out _, out _), "Current pose conversion");
                    vertices = packed.ToArray(); localIndices = indices.ToArray();
                }
                finally { packed.Dispose(); indices.Dispose(); }
                using var storage = new VpCpuGeometryStorage(65536, 262144, 128, 256, 256, Allocator.Persistent);
                int offset = 0;
                var submeshes = Enumerable.Range(0, baked.subMeshCount).Select(s => { int n = (int)baked.GetIndexCount(s); var sub = new VpGeometrySubmesh(offset, n, s); offset += n; return sub; }).ToArray();
                Require(storage.TryAppendCuttable(vertices, localIndices, entry.topologyMap, entry.topologyCount, submeshes, out var geometry, out var verdict), "Cuttable input " + verdict);
                using var gpu = new VpGpuIndexedGeometryBuffers(65536, 262144);
                using var batch = new VpIndexedIndirectDrawBatch(1, 1);
                var properties = new MaterialPropertyBlock();
                var reference = new GameObject("Baked / expanded geometry oracle"); reference.layer = 30;
                reference.transform.SetParent(skin.transform, false);
                var filter = reference.AddComponent<MeshFilter>(); filter.sharedMesh = baked;
                var meshRenderer = reference.AddComponent<MeshRenderer>(); meshRenderer.enabled = false;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off; meshRenderer.receiveShadows = false;
                meshRenderer.sharedMaterials = Enumerable.Repeat(meshMaterial, baked.subMeshCount).ToArray();
                var matrix = skin.transform.localToWorldMatrix;
                var bounds = baked.bounds;
                Vector3 centre = matrix.MultiplyPoint3x4(bounds.center);
                float radius = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    var sign = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
                    radius = Mathf.Max(radius, Vector3.Distance(centre, matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(sign, bounds.extents))));
                }
                camera.transform.position = centre + new Vector3(1, .5f, -2).normalized * radius * 4;
                camera.transform.LookAt(centre); camera.orthographicSize = radius * 1.05f;
                camera.nearClipPlane = radius * .01f; camera.farClipPlane = radius * 10;
                VpCutSurfaceColour.SetDebugEnabled(false);
                if (preparedInput!=null)
                {
                    skin.enabled=false; originalSkin.enabled=true;
                    yield return null;
                    var originalImage=Capture(label+"-original-skin");
                    originalSkin.enabled=false; skin.enabled=true;
                    yield return null;
                    var preparedImage=Capture(label+"-prepared-skin");
                    Compare(label+"-original-vs-prepared",originalImage,preparedImage);
                }
                var skinImage = Capture(label + "-skin");
                skin.enabled = false; meshRenderer.enabled = true;
                yield return null;
                var bakeImage = Capture(label + "-bake");
                Compare(label + "-skin-vs-bake", skinImage, bakeImage);
                meshRenderer.enabled = false;
                for (int stage = 0; stage < 3; stage++)
                {
                    if (stage > 0)
                    {
                        Require(storage.TryGetPublishedExtent(geometry, out _, out _, out var oldBounds), "Cut bounds");
                        int axis = stage == 1 ? 1 : 0; float3 n = default; n[axis] = 1;
                        Require(VpStorageCutInput.TryAcquire(storage, geometry, out var input), "Cut acquire");
                        VpStorageCutResult cut;
                        using (input) Require(VpStorageCut.TryExecute(storage, input, new float4(n, -oldBounds.center[axis] - .137f * oldBounds.extents[axis]), default, out cut), "Cut execute");
                        Require(cut.status == VpStorageCutStatus.Ok && cut.kernel.openContourCount == 0 && cut.kernel.executedManaged == 0, "Cut result");
                        geometry = cut.positive.geometry;
                        filter.sharedMesh = Expand(storage, geometry);
                        meshRenderer.sharedMaterials = new[] { meshMaterial };
                    }
                    Require(storage.TryGetPublishedExtent(geometry, out int start, out int length, out var localBounds), "Published extent");
                    if (stage > 0)
                    {
                        // Observe the outward-facing cap of the retained positive half.
                        Vector3 view = stage == 1 ? new Vector3(.25f, -1, .3f) : new Vector3(-1, .25f, .3f);
                        Vector3 cutCentre = matrix.MultiplyPoint3x4(localBounds.center);
                        camera.transform.position = cutCentre + matrix.MultiplyVector(view).normalized * radius * 4;
                        camera.transform.LookAt(cutCentre);
                    }
                    Require(storage.TryGetIndexState(geometry.indexRange, out _, out int indexStart, out int indexCount), "Index state");
                    Require(VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, gpu.VertexBuffer, 0, storage.VertexCount, out _), "Vertex upload");
                    Require(VpStoredGeometryTransfer.TryUploadPublishedIndices(storage, gpu.IndexBuffer, geometry.indexRange, out _), "Index upload");
                    Require(batch.TryUpload(new[] { new VpIndirectCommand(new VpGeometryRange(start, length, indexStart, indexCount), localBounds, 1) }, new[] { matrix }, false), "Draw upload");
                    for (int debug = 0; debug < (stage == 0 ? 1 : 2); debug++)
                    {
                        VpCutSurfaceColour.SetDebugEnabled(debug == 1);
                        meshRenderer.enabled = true;
                        yield return null;
                        var meshImage = Capture(label + "-stage" + stage + "-debug" + debug + "-mesh");
                        meshRenderer.enabled = false;
                        yield return null;
                        batch.RenderForward(vpMaterial, properties, gpu, 30, 0, 1, camera);
                        var vpImage = Capture(label + "-stage" + stage + "-debug" + debug + "-vp");
                        Compare(label + "-stage" + stage + "-debug" + debug, meshImage, vpImage);
                    }
                    Debug.Log($"CHARACTER GEOMETRY {label} stage={stage} committedVertices={storage.VertexCount} liveIndices={indexCount}");
                    yield return null;
                }
                parent.SetActive(false);
                yield return null;
            }
            Require(scaleCache.ActiveInputCount==0,"Prepared inputs released");
            report.preparedMeshBuilds=scaleCache.PreparedMeshBuildCount;
            report.cachedMeshes=scaleCache.CachedMeshCount;
        }

        void MeasureInput(string family,SkinnedMeshRenderer original,VpFixedScaleSkinInput input,Transform parent)
        {
            // Diagnostic only: isolated CPU API costs, not frame time or the physics/commit pipeline.
            var row=new PreparationCost { family=family,sourceMeshBytes=UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(original.sharedMesh),
                additionalPreparedMeshBytes=input.Renderer.sharedMesh==original.sharedMesh ? 0 : UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(input.Renderer.sharedMesh) };
            var bake=new Mesh();
            var clone=Instantiate(original.gameObject,parent);
            var source=clone.GetComponent<SkinnedMeshRenderer>(); source.sharedMesh=original.sharedMesh;
            source.bones=original.bones; source.rootBone=original.rootBone; source.enabled=false;
            // A detached diagnostic copy retains the original world frame explicitly.
            clone.transform.SetPositionAndRotation(original.transform.position,original.transform.rotation);
            clone.transform.localScale=Vector3.one*input.FixedScale;
            using var cache=new VpFixedScaleSkinCache();
            try
            {
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                var first=cache.Prepare(source,parent);
                row.coldPrepareUs=ElapsedUs(start);
                first.Dispose();
                var warm=new double[32];
                for (int i=0;i<warm.Length;i++)
                {
                    start=System.Diagnostics.Stopwatch.GetTimestamp();
                    using var next=cache.Prepare(source,parent);
                    warm[i]=ElapsedUs(start);
                }
                row.cachedPrepareMedianUs=Median(warm);
                for (int i=0;i<32;i++) { original.BakeMesh(bake,true); input.BakeCurrentPose(bake); }
                var baseline=new double[256]; var prepared=new double[256];
                for (int i=0;i<baseline.Length;i++)
                {
                    // Alternate order to reduce a fixed-order cache advantage.
                    if ((i&1)==0)
                    {
                        start=System.Diagnostics.Stopwatch.GetTimestamp(); original.BakeMesh(bake,true); baseline[i]=ElapsedUs(start);
                        start=System.Diagnostics.Stopwatch.GetTimestamp(); input.BakeCurrentPose(bake); prepared[i]=ElapsedUs(start);
                    }
                    else
                    {
                        start=System.Diagnostics.Stopwatch.GetTimestamp(); input.BakeCurrentPose(bake); prepared[i]=ElapsedUs(start);
                        start=System.Diagnostics.Stopwatch.GetTimestamp(); original.BakeMesh(bake,true); baseline[i]=ElapsedUs(start);
                    }
                }
                row.originalBakeMedianUs=Median(baseline); row.preparedBakeMedianUs=Median(prepared);
                Require(cache.PreparedMeshBuildCount==(input.FixedScale==1 ? 0 : 1),"Cached preparations must not rebuild mesh");
                row.preparedMeshBuilds=cache.PreparedMeshBuildCount;
                report.preparation.Add(row);
            }
            finally { Destroy(bake); Destroy(clone); }
        }
        static double ElapsedUs(long start) => (System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000000.0/System.Diagnostics.Stopwatch.Frequency;
        static double Median(double[] samples) { Array.Sort(samples); return (samples[samples.Length/2-1]+samples[samples.Length/2])*.5; }
        Color32[] Capture(string name)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Require(RenderPipeline.SupportsRenderRequest(camera, request), "URP render request unsupported");
            RenderPipeline.SubmitRenderRequest(camera, request);
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); texture.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), texture.EncodeToPNG());
                return texture.GetPixels32();
            }
            finally { RenderTexture.active = previous; Destroy(texture); }
        }
        Mesh Expand(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            var mesh = Own(new Mesh { indexFormat = IndexFormat.UInt32 });
            var vertices = storage.Vertices.ToArray();
            mesh.vertices = vertices.Select(v => v.position).ToArray(); mesh.normals = vertices.Select(v => v.normal).ToArray(); mesh.uv = vertices.Select(v => v.uv0).ToArray();
            Require(storage.TryAcquireIndexReadLease(geometry.indexRange, out var lease, out var indices), "Expand indices");
            try { mesh.triangles = indices.ToArray().Select(i => (int)i).ToArray(); }
            finally { storage.TryReleaseIndexReadLease(lease); }
            return mesh;
        }
        void Compare(string name, Color32[] a, Color32[] b)
        {
            var diff = new Comparison { name = name }; long sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if ((a[i].a > 127) != (b[i].a > 127)) diff.silhouette++;
                if (a[i].a <= 127 && b[i].a <= 127) continue;
                diff.covered++;
                int r = Math.Abs(a[i].r - b[i].r), g = Math.Abs(a[i].g - b[i].g), bl = Math.Abs(a[i].b - b[i].b);
                diff.maxRgb = Math.Max(diff.maxRgb, Math.Max(r, Math.Max(g, bl))); sum += r + g + bl;
                if (r + g + bl != 0) diff.changed++;
            }
            diff.meanRgb = diff.covered == 0 ? 0 : (double)sum / (3 * diff.covered);
            diff.passed = diff.covered > 100 && diff.silhouette == 0 && diff.maxRgb <= 3;
            report.comparisons.Add(diff);
            Debug.Log("CHARACTER COMPARE " + JsonUtility.ToJson(diff));
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void OnDestroy() { if (!finished) VpCutSurfaceAtlas.Clear(); }
        [Serializable] sealed class Input { public Entry[] assets; }
        [Serializable] sealed class Entry { public string family, objectName; public int topologyCount; public int[] topologyMap; }
        [Serializable] sealed class Report { public string unity, graphics, failure; public bool passed, development,fixedScalePrepared; public int vertexStride,preparedMeshBuilds,cachedMeshes; public List<Comparison> comparisons = new List<Comparison>(); public List<PoseInfo> poses=new List<PoseInfo>(); public List<PreparationCost> preparation=new List<PreparationCost>(); }
        [Serializable] sealed class PoseInfo { public string family,clip; public int pose; public float sampledTime,scale; }
        [Serializable] sealed class PreparationCost { public string family; public long sourceMeshBytes,additionalPreparedMeshBytes; public int preparedMeshBuilds; public double coldPrepareUs,cachedPrepareMedianUs,originalBakeMedianUs,preparedBakeMedianUs; }
        [Serializable] sealed class Comparison { public string name; public bool passed; public int covered, silhouette, maxRgb, changed; public double meanRgb; }
    }
}
