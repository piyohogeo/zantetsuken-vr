using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Runs the established colour, depth and shadow image checks with pixel clipping forced even on hardware that
    /// supports clip distances. The additional perspective cases compare coverage to ray/plane geometry, rather
    /// than to hardware clipping's edge samples.
    /// </summary>
    public class VpPixelClipTests : VpIndexedIndirectClipTests
    {
        private const int PerspectiveSize = 128;
        private const float Axis = 0.34f;
        private const float Diagonal = 0.48f;
        private const float Margin = 0.025f;
        private static readonly Vector3[] PerspectiveQuad =
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
        };
        private static readonly Matrix4x4 Placement = Matrix4x4.TRS(
            new Vector3(0.1f, -0.02f, 0.1f), Quaternion.Euler(22f, 52f, 17f), Vector3.one * 1.9f);

        [SetUp]
        public void ForcePixelClipping()
        {
            VpClipShaderPolicy.PixelClipOverrideForTests = true;
            Assert.That(Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword), Is.True);
        }

        [TearDown]
        public void RestoreClipPolicy()
        {
            VpClipShaderPolicy.PixelClipOverrideForTests = null;
        }

        [TestCase(1)]
        [TestCase(4)]
        public void PerspectiveEightPlanes_KeepTheirIntersection_OnAnObliqueQuad(int samples)
        {
            InEmptyScene(() => WithPipelineSamples(samples, () =>
            {
                (Camera camera, RenderTexture target) = PerspectiveView(samples);
                Mesh mesh = QuadMesh(PerspectiveQuad);
                Material material = ForwardMaterial(Color.green, Color.white);
                using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
                using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
                using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
                {
                    Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                    Assert.That(buffers.TryUpload(pool), Is.True);
                    var commands = new[] { new VpIndirectCommand(range, mesh.bounds, 1) };
                    Assert.That(batch.TryUpload(commands, new[] { Placement }, new[] { PerspectiveOctagon() }, false), Is.True);
                    batch.RenderForward(material, new MaterialPropertyBlock(), buffers, 0, 0, 1, camera);
                    Color32[] pixels = RenderResolved(camera, target);

                    int kept = 0, removed = 0, wrongKept = 0, wrongRemoved = 0;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (!TryLocalPoint(camera, i, out Vector3 local)) continue;
                        float x = Mathf.Abs(local.x), y = Mathf.Abs(local.y);
                        // Stay clear of geometry and clip boundaries, including the MSAA sample footprint. Exact
                        // agreement of hardware clipping and pixel clipping on an edge is not the requirement.
                        if (x >= 0.5f - Margin || y >= 0.5f - Margin) continue;
                        if (x < Axis - Margin && y < Axis - Margin && x + y < Diagonal - 2f * Margin)
                        {
                            kept++;
                            if (!Green(pixels[i])) wrongKept++;
                        }
                        else if (x > Axis + Margin || y > Axis + Margin || x + y > Diagonal + 2f * Margin)
                        {
                            removed++;
                            if (!Background(pixels[i])) wrongRemoved++;
                        }
                    }

                    Assert.That(kept, Is.GreaterThan(200), "the oracle includes a visible interior");
                    Assert.That(removed, Is.GreaterThan(200), "and a visible region cut off the original quad");
                    Assert.That(wrongKept, Is.Zero, "all eight planes keep the interior under perspective interpolation");
                    Assert.That(wrongRemoved, Is.Zero, "all eight planes remove their outside regions");
                    TestContext.Out.WriteLine("Pixel Clip perspective coverage: samples=" + samples
                        + ", interior=" + kept + ", removed=" + removed);
                }
            }));
        }

        [Test]
        public void NoClip_WithThePixelKeyword_DrawsTheWholeObjectDespiteAnExcludingClipRecord()
        {
            InEmptyScene(() => WithPipelineSamples(1, () =>
            {
                (Camera camera, RenderTexture target) = PerspectiveView(1);
                Mesh mesh = QuadMesh(PerspectiveQuad);
                Material clippedMaterial = ForwardMaterial(Color.green, Color.white);
                Material ordinaryMaterial = ForwardMaterial(Color.green, Color.white);
                ordinaryMaterial.EnableKeyword("VP_NO_CLIP");
                Assert.That(ordinaryMaterial.IsKeywordEnabled("VP_NO_CLIP"), Is.True);
                Assert.That(Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword), Is.True);

                using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
                using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
                using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
                {
                    Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                    Assert.That(buffers.TryUpload(pool), Is.True);
                    var commands = new[] { new VpIndirectCommand(range, mesh.bounds, 1) };
                    var transforms = new[] { Placement };
                    var properties = new MaterialPropertyBlock();
                    Assert.That(batch.TryUpload(commands, transforms, new[] { VpInstanceClip.None }, false), Is.True);
                    batch.RenderForward(clippedMaterial, properties, buffers, 0, 0, 1, camera);
                    Color32[] whole = RenderResolved(camera, target);

                    var excluding = new[] { VpInstanceClip.Keep(new Vector4(0f, 0f, 1f, -100f), 1f) };
                    Assert.That(batch.TryUpload(commands, transforms, excluding, false), Is.True);
                    batch.RenderForward(clippedMaterial, properties, buffers, 0, 0, 1, camera);
                    Color32[] removed = RenderResolved(camera, target);
                    batch.RenderForward(ordinaryMaterial, properties, buffers, 0, 0, 1, camera);
                    Color32[] ordinary = RenderResolved(camera, target);

                    int drawn = 0, incorrectlyDrawn = 0, differing = 0;
                    for (int i = 0; i < whole.Length; i++)
                    {
                        if (Green(whole[i])) drawn++;
                        if (!Background(removed[i])) incorrectlyDrawn++;
                        if (ordinary[i].r != whole[i].r || ordinary[i].g != whole[i].g || ordinary[i].b != whole[i].b)
                            differing++;
                    }

                    Assert.That(drawn, Is.GreaterThan(500), "the reference object is visible");
                    Assert.That(incorrectlyDrawn, Is.Zero, "the same uploaded record really excludes it in the Clip variant");
                    Assert.That(differing, Is.Zero, "NO_CLIP ignores that record and retains the full reference image");
                }
            }));
        }

        private (Camera camera, RenderTexture target) PerspectiveView(int samples)
        {
            var target = Track(new RenderTexture(PerspectiveSize, PerspectiveSize, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = samples,
            });
            if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(target.descriptor) < samples)
                Assert.Ignore("The current graphics device does not support " + samples + "x MSAA for this target.");
            target.Create();
            Assert.That(target.antiAliasing, Is.EqualTo(samples));
            Camera camera = TestCamera("VP Pixel Clip Perspective Camera", target);
            camera.orthographic = false;
            camera.transform.position = new Vector3(0f, 0f, -2.6f);
            camera.fieldOfView = 45f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            camera.allowHDR = false;
            camera.allowMSAA = samples > 1;
            return (camera, target);
        }

        private static VpInstanceClip PerspectiveOctagon()
        {
            var planes = new VpClipHalfSpace[VpInstanceClip.PlaneCapacity];
            Matrix4x4 toWorldPlane = Placement.inverse.transpose;
            for (int i = 0; i < planes.Length; i++)
            {
                float angle = i * Mathf.PI / 4f;
                float distance = i % 2 == 0 ? Axis : Diagonal / Mathf.Sqrt(2f);
                Vector4 plane = toWorldPlane * new Vector4(Mathf.Cos(angle), Mathf.Sin(angle), 0f, -distance);
                plane /= new Vector3(plane.x, plane.y, plane.z).magnitude;
                planes[i] = new VpClipHalfSpace(plane, -1f);
            }

            Assert.That(VpInstanceClip.TryKeep(planes, out VpInstanceClip clip), Is.True);
            return clip;
        }

        private static bool TryLocalPoint(Camera camera, int index, out Vector3 local)
        {
            var uv = new Vector3(((index % PerspectiveSize) + 0.5f) / PerspectiveSize,
                ((index / PerspectiveSize) + 0.5f) / PerspectiveSize, 0f);
            Ray ray = camera.ViewportPointToRay(uv);
            var plane = new Plane(Placement.MultiplyVector(Vector3.back).normalized,
                Placement.MultiplyPoint3x4(Vector3.zero));
            if (!plane.Raycast(ray, out float enter))
            {
                local = default;
                return false;
            }

            local = Placement.inverse.MultiplyPoint3x4(ray.GetPoint(enter));
            return true;
        }

        private Color32[] RenderResolved(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture readable = target;
            if (target.antiAliasing > 1)
            {
                readable = Track(new RenderTexture(target.width, target.height, 0, target.format));
                readable.Create();
                target.ResolveAntiAliasedSurface(readable);
            }

            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = readable;
                var readback = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
                readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                readback.Apply(false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static void WithPipelineSamples(int samples, Action body)
        {
            // This fixture lives in the core rendering test assembly, which deliberately has no URP assembly
            // dependency. Set URP's public property without changing or saving the asset's serialized state.
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            Assert.That(pipeline, Is.Not.Null);
            var property = pipeline.GetType().GetProperty("msaaSampleCount");
            Assert.That(property, Is.Not.Null, "the current pipeline exposes the URP MSAA setting");
            int previous = (int)property.GetValue(pipeline);
            try
            {
                property.SetValue(pipeline, samples);
                body();
            }
            finally
            {
                property.SetValue(pipeline, previous);
            }
        }

        private static bool Green(Color32 pixel) => pixel.g > pixel.r + 30 && pixel.g > pixel.b + 30;
        private static bool Background(Color32 pixel) => pixel.r < 16 && pixel.g < 16 && pixel.b < 16;
    }

    /// <summary>The established stencil winding, cap containment and depth tests in the pixel-discard mode.</summary>
    public class VpPixelClipStencilTests : VpStencilCapTests
    {
        [SetUp]
        public void ForcePixelClipping()
        {
            VpClipShaderPolicy.PixelClipOverrideForTests = true;
            Assert.That(Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword), Is.True);
        }

        [TearDown]
        public void RestoreClipPolicy()
        {
            VpClipShaderPolicy.PixelClipOverrideForTests = null;
        }
    }
}
