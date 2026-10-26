using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>One-command Unity calls keep both colour and caster images, including absolute command offsets.</summary>
    public class VpUnityMultiDrawIndirectTests
    {
        private const int Size = 128;
        private const int First = 1;
        private const int Count = 3;
        private readonly List<Object> _objects = new List<Object>();

        [TestCase(false)]
        [TestCase(true)]
        public void SingleCommandCalls_MatchMultiDraw_ForForwardAndCasters_WithANonzeroStart(bool clipList)
        {
            SceneSetup[] previousScenes = EditorSceneManager.GetSceneManagerSetup();
            bool previousMultiDraw = VpNativeDrawSetup.UnityMultiDrawIndirect;
            bool previousSliceSelection = VpGpuCullSetup.ShadowSliceSelection;
            bool? previousPixelClip = VpClipShaderPolicy.PixelClipOverrideForTests;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                // Exercise the portable Android clipping path even on a desktop that supports clip distances.
                VpClipShaderPolicy.PixelClipOverrideForTests = true;
                VpGpuCullSetup.ShadowSliceSelection = true;
                Assert.That(VpGpuStop.SkipDraws, Is.False, "draw submission must be enabled for the image oracle");
                (Camera camera, RenderTexture target) = Scene();
                Color32[] ground = Read(camera, target);
                DestroyObjects();
                (camera, target) = Scene();
                Color32[] grouped = Draw(camera, target, clipList, true);
                DestroyObjects();
                (camera, target) = Scene();
                Color32[] individual = Draw(camera, target, clipList, false);
                int green = 0, shadow = 0, differing = 0;
                for (int i = 0; i < grouped.Length; i++)
                {
                    Color32 a = grouped[i], b = individual[i], g = ground[i];
                    bool marker = a.g > a.r + 50;
                    if (marker) green++;
                    if (!marker && a.r + a.g + a.b < (g.r + g.g + g.b) * 0.8f) shadow++;
                    if (Math.Abs(a.r - b.r) > 2 || Math.Abs(a.g - b.g) > 2 || Math.Abs(a.b - b.b) > 2) differing++;
                }

                Assert.That(green, Is.GreaterThan(200), "the reference includes visible forward geometry");
                Assert.That(shadow, Is.GreaterThan(100), "and real shadows on the ground, so caster equality is observable");
                Assert.That(differing, Is.Zero, "splitting the selected command range preserves colour, depth and shadows");
            }
            finally
            {
                VpNativeDrawSetup.UnityMultiDrawIndirect = previousMultiDraw;
                VpGpuCullSetup.ShadowSliceSelection = previousSliceSelection;
                VpClipShaderPolicy.PixelClipOverrideForTests = previousPixelClip;
                DestroyObjects();
                if (previousScenes != null && previousScenes.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previousScenes);
                else EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }

        private void DestroyObjects()
        {
            foreach (Object item in _objects) if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
        }

        private T Track<T>(T item) where T : Object
        {
            _objects.Add(item);
            return item;
        }

        private (Camera camera, RenderTexture target) Scene()
        {
            Light light = Track(new GameObject("VP Single Indirect Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Single Indirect Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 4f;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.transform.localScale = Vector3.one * 3f;
            MeshRenderer receiver = ground.GetComponent<MeshRenderer>();
            receiver.receiveShadows = true;
            receiver.shadowCastingMode = ShadowCastingMode.Off;
            return (camera, target);
        }

        private Material Material(string shaderName, bool clipList, bool caster)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            Material material = Track(new Material(shader));
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.green);
            if (clipList)
            {
                if (caster) material.EnableKeyword(VpGpuCullSetup.ClipListSliceKeyword);
            }
            else
            {
                material.EnableKeyword(VpGpuCullSetup.Keyword);
                material.EnableKeyword(VpGpuCullSetup.NoClipKeyword);
            }
            return material;
        }

        private Color32[] Draw(Camera camera, RenderTexture target, bool clipList, bool multiDraw)
        {
            VpNativeDrawSetup.UnityMultiDrawIndirect = multiDraw;
            Material forward = Material("Zantetsu/VP Indexed Indirect Unlit", clipList, false);
            Material shadow = Material("Zantetsu/VP Indexed Indirect Shadow Caster", clipList, true);
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            int vertices = cube.vertexCount * (Count + 1), indices = (int)cube.GetIndexCount(0) * (Count + 1);
            Assert.That(VpGpuCullSetup.TryCreate(1, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            using (var pool = new VpCpuGeometryPool(vertices, indices, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(vertices, indices))
            using (var batch = new VpIndexedIndirectDrawBatch(Count + 1, Count + 1, null, setup))
            {
                var commands = new VpIndirectCommand[Count + 1];
                var transforms = new Matrix4x4[Count + 1];
                var clips = new VpInstanceClip[Count + 1];
                var entries = new VpIndexedIndirectDrawBatch.ClipEntry[Count + 1];
                for (int i = 0; i < commands.Length; i++)
                {
                    // Distinct local bounds make a wrong GetCommandID select another caster's bounds. The first
                    // visible cube is a sentinel outside the submitted range; every selected draw starts after it.
                    Vector3 localOffset = new Vector3(i * 10f, 0f, i * 4f);
                    Vector3 centre = new Vector3(i == 0 ? -3.2f : (i - 2) * 1.8f, 1.2f, 0.6f);
                    Mesh mesh = Track(Object.Instantiate(cube));
                    Vector3[] positions = mesh.vertices;
                    for (int v = 0; v < positions.Length; v++) positions[v] += localOffset;
                    mesh.vertices = positions;
                    mesh.RecalculateBounds();
                    Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                    commands[i] = new VpIndirectCommand(range, mesh.bounds, 1, clipList);
                    transforms[i] = Matrix4x4.Translate(centre - localOffset);
                    clips[i] = VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, -centre.x), i % 2 == 0 ? 1f : -1f);
                    entries[i] = new VpIndexedIndirectDrawBatch.ClipEntry(range, i, mesh.bounds);
                }

                Assert.That(buffers.TryUpload(pool), Is.True);
                Assert.That(batch.TryUpload(commands, transforms, clips, false), Is.True);
                var properties = new MaterialPropertyBlock();
                long before = VpGpuStop.DrawsIssued;
                if (clipList)
                {
                    batch.UploadClipEntries(entries, entries.Length);
                    batch.RenderClipForward(forward, properties, buffers, 0, First, Count, camera, 0, batch.WorldBounds);
                    batch.RenderClipShadows(shadow, properties, buffers, 0, First, Count, camera, 0, batch.WorldBounds);
                }
                else
                {
                    var conditions = new VpCullConditions();
                    conditions.KeepEverything();
                    using (var cull = new CommandBuffer())
                    {
                        batch.IssueCull(cull, 0, conditions);
                        Graphics.ExecuteCommandBuffer(cull);
                    }
                    batch.RenderForward(forward, properties, buffers, 0, First, Count, camera, 0);
                    batch.RenderShadows(shadow, properties, buffers, 0, First, Count, camera, 0);
                }
                Assert.That(VpGpuStop.DrawsIssued - before, Is.EqualTo(multiDraw ? 2L : 2L * Count),
                    "the switch changes actual Unity API submission count for both passes");
                return Read(camera, target);
            }
        }

        private Color32[] Read(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                Texture2D pixels = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
                pixels.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0);
                pixels.Apply(false);
                return pixels.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }
    }
}
