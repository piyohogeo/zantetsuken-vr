using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Quest;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public class PcvrUnityPrewarmTests
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private QuestNpcUnityPrewarm _warm;
        private VpStencilCapMaterials _stencil;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _warm?.RetireWhenReady();
            for (int i = 0; i < 8; i++) yield return null;
            _stencil?.Dispose();
            foreach (UnityEngine.Object value in _objects)
                if (value != null) UnityEngine.Object.Destroy(value);
            _objects.Clear();
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            _objects.Add(value);
            return value;
        }

        private Camera MakeCamera()
        {
            Assert.That(SystemInfo.supportsGraphicsFence, Is.True, "the PCVR warmup requires an observable GPU completion");
            var camera = Track(new GameObject("PCVR warm camera")).AddComponent<Camera>();
            camera.transform.position = new Vector3(0, 10000, 0);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.2f, 0.3f, 1);
            camera.cullingMask = 1 << 30;
            camera.targetTexture = Track(new RenderTexture(64, 64, 24) { antiAliasing = 4 });
            camera.targetTexture.Create();
            var light = Track(new GameObject("PCVR warm directional light")).AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.cullingMask = 1 << 30;
            light.transform.rotation = Quaternion.Euler(45, 30, 0);
            Assert.That(VpStencilCapMaterials.TryCreate(1, out _stencil), Is.True);
            return camera;
        }

        private Material Material(string shader, string keyword)
        {
            var material = Track(new Material(Shader.Find(shader)));
            material.enableInstancing = true;
            if (keyword != null) material.EnableKeyword(keyword);
            return material;
        }

        private static void Render(Camera camera)
        {
            RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = camera.targetTexture });
        }

        private Color32[] Read(Camera camera)
        {
            RenderTexture previous = RenderTexture.active;
            var pixels = Track(new Texture2D(64, 64, TextureFormat.RGBA32, false));
            try
            {
                RenderTexture.active = camera.targetTexture;
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                pixels.Apply();
                return pixels.GetPixels32();
            }
            finally { RenderTexture.active = previous; }
        }

        [Test]
        public void DisplayWithoutGpuCulling_CopiesBodyAndCasterMaterialsForUnityPreparation()
        {
            Material body = Material("Zantetsu/VP Indexed Indirect Unlit", null);
            Material caster = Material("Zantetsu/VP Indexed Indirect Shadow Caster", null);
            Material provisionalCaster = Material("Zantetsu/VP Indexed Indirect Shadow Caster", null);
            var materials = new Dictionary<int, Material> { { 0, body }, { 1, body } };
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(1));
            var stencil = new VpStencilSettings(1, 0.01f, 1e-4f, Vector2.zero, 1);
            using (var storage = new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent))
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                Assert.That(VpLogicalCutDisplay.TryCreate(storage, table, ledger, materials, caster, provisionalCaster,
                    16, 16, 64, 512, 32, stencil, out VpLogicalCutDisplay display), Is.True, VpLogicalCutDisplay.LastCreationFailure);
                using (display)
                {
                    Assert.That(display.CullsOnGpu, Is.False, "the supported VP3 fallback has no GPU-culling materials");
                    var bodies = new List<Material>();
                    var casters = new List<Material>();
                    display.CopyUnityPrewarmMaterials(bodies, casters);
                    display.CopyUnityPrewarmMaterials(bodies, casters);
                    Assert.That(bodies, Is.EqualTo(new[] { body }));
                    Assert.That(casters, Is.EqualTo(new[] { caster, provisionalCaster }));
                }
            }
        }

        [UnityTest]
        public IEnumerator UnityBodiesClipsAndStencil_RenderThreeFrames_ThenRetireAfterTheGpuFence()
        {
            Camera camera = MakeCamera();
            Material body = Material("Zantetsu/VP Indexed Indirect Unlit", "VP_GPU_CULLED");
            Material clip = Material("Zantetsu/VP Indexed Indirect Unlit", null);
            Material caster = Material("Zantetsu/VP Indexed Indirect Shadow Caster", "VP_GPU_CULLED");
            Material clipCaster = Material("Zantetsu/VP Indexed Indirect Shadow Caster", "VP_CLIP_LIST_SLICES");
            Render(camera);
            Color32[] before = Read(camera);
            _warm = QuestNpcUnityPrewarm.Create(camera, _stencil, false, 30, null,
                new[] { body, clip }, new[] { caster, clipCaster });
            Assert.That(_warm.BodyMaterialCount, Is.EqualTo(2));
            Assert.That(_warm.CasterMaterialCount, Is.EqualTo(2));
            for (int i = 0; i < QuestDrawPrewarm.UnityWarmFrames; i++)
            {
                _warm.Issue();
                Render(camera);
                Assert.That(_warm.ReadyToDispose, Is.False);
                yield return null;
            }
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!_warm.ReadyToDispose && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(_warm.ReadyToDispose, Is.True);
            Assert.That(_warm.RenderedWarmFrames, Is.EqualTo(3));
            Render(camera);
            Assert.That(Read(camera), Is.EqualTo(before), "degenerate warm draws preserve the scene colour");
            _warm.Dispose();
            Assert.That(body.IsKeywordEnabled("VP_GPU_CULLED"), Is.True, "borrowed material state is retained");
            Assert.That(clip.IsKeywordEnabled("VP_GPU_CULLED"), Is.False);
        }

        [UnityTest]
        public IEnumerator AbandonedUnityPreparation_KeepsItsBuffersUntilTheCameraAndFenceHaveFinished()
        {
            Camera camera = MakeCamera();
            _warm = QuestNpcUnityPrewarm.Create(camera, _stencil, false, 30);
            _warm.Issue();
            Assert.Throws<InvalidOperationException>(() => _warm.Dispose());
            _warm.RetireWhenReady();
            Assert.Throws<ObjectDisposedException>(() => _warm.Issue());
            Render(camera);
            FieldInfo vertices = typeof(QuestNpcUnityPrewarm).GetField("_vertices", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(vertices.GetValue(_warm), Is.Not.Null);
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (vertices.GetValue(_warm) != null && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.That(vertices.GetValue(_warm), Is.Null, "retirement gives the GPU its full lifetime");
        }
    }
}
