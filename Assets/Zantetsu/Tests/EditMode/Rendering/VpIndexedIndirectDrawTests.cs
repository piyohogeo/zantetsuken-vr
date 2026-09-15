using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// VP Stage 3 (indexed indirect VP draw): the hardware index buffer holds the pool's global vertex numbers; a
    /// command with indexCountPerInstance = range.indexCount, startIndex = range.indexStart and baseVertexIndex = 0 draws a
    /// later range (whose index start, vertex start and index count all differ) from its own vertices, so SV_VertexID is
    /// the global vertex number; commands draw each range at its own transforms and startCommand / commandCount select
    /// them; the image matches Stage 2 exactly; depth is tested against a Unity mesh; the shadow call casts and the forward
    /// call receives main light shadows; the argument buffers read back as uploaded with only the forward one doubled for
    /// Single Pass Instanced; rejected uploads keep the earlier arguments, transforms, bounds and Single Pass Instanced
    /// mode, and a geometry upload rejected for capacity keeps the buffers' earlier contents; zero commands,
    /// out-of-range commands and disposal behave as in Stage 2. The stereo pass itself needs an XR device and is not
    /// covered here.
    /// </summary>
    public class VpIndexedIndirectDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const string Stage2ShaderName = "Zantetsu/VP Indirect Unlit";
        private const string Stage2ShadowShaderName = "Zantetsu/VP Indirect Shadow Caster";
        private const int Size = 64;
        private const int CellCoveredPixels = 60;
        private const int CoveredPixels = 200;
        private const int ShadowSize = 128;
        private const int ShadowedPixels = 300;
        private const float LitShadowedFraction = 0.7f;
        private const float VpShadowedFraction = 0.9f;
        private const int LeftHalf = 0;
        private const int RightHalf = 1;

        // An off-screen quad appended first: 4 vertices and 6 indices, so the later ranges' index starts, vertex starts
        // and index counts all differ (A: vertices from 4, indices from 6; B: vertices from 7, indices from 9).
        private static readonly Vector3[] PaddingQuad = { new Vector3(100f, 0f, 0f), new Vector3(101f, 0f, 0f), new Vector3(101f, 1f, 0f), new Vector3(100f, 1f, 0f) };
        private static readonly Vector3[] TriangleA = { new Vector3(-0.45f, -0.4f, 0f), new Vector3(-0.25f, 0.4f, 0f), new Vector3(-0.05f, -0.4f, 0f) };
        private static readonly Vector3[] TriangleB = { new Vector3(0.05f, -0.4f, 0f), new Vector3(0.25f, 0.4f, 0f), new Vector3(0.45f, -0.4f, 0f) };
        private static readonly Bounds CellBounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));
        private static readonly Matrix4x4 CellTopLeft = Matrix4x4.Translate(new Vector3(-0.5f, 0.5f, 0f));
        private static readonly Matrix4x4 CellTopRight = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f));
        private static readonly Matrix4x4 CellBottomLeft = Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0f));
        private static readonly Matrix4x4 CellBottomRight = Matrix4x4.Translate(new Vector3(0.5f, -0.5f, 0f));
        private static readonly Matrix4x4[] AllCells = { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight };
        private static readonly Matrix4x4[] TwoCubes = { Matrix4x4.Translate(new Vector3(-1.5f, 1.2f, 0f)), Matrix4x4.Translate(new Vector3(1.5f, 1.2f, 0f)) };
        private static readonly string[] CellNames = { "top left", "top right", "bottom left", "bottom right" };

        private readonly List<Object> _objects = new List<Object>();

        private enum Issue
        {
            Both,
            ForwardOnly,
            ShadowOnly,
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

        private Camera TestCamera(string name, RenderTexture target)
        {
            Camera camera = Track(new GameObject(name)).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            return camera;
        }

        private (Camera camera, RenderTexture target) FrontView(string name)
        {
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera(name, target);
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
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

        private Mesh Triangle(Vector3[] positions)
        {
            return FlatMesh(positions, new[] { 0, 1, 2 });
        }

        private static bool IsGreen(Color32 pixel)
        {
            return pixel.g > 64 && pixel.r < 64;
        }

        private static int CountGreen(Color32[] pixels)
        {
            int green = 0;
            foreach (Color32 pixel in pixels)
            {
                green += IsGreen(pixel) ? 1 : 0;
            }

            return green;
        }

        private static int[,] GreenRegions(Color32[] pixels)
        {
            var counts = new int[4, 2];
            for (int i = 0; i < pixels.Length; i++)
            {
                if (!IsGreen(pixels[i]))
                {
                    continue;
                }

                int row = i / Size;
                int column = i % Size;
                int cell = (row >= Size / 2 ? 0 : 2) + (column >= Size / 2 ? 1 : 0);
                int half = column % (Size / 2) < Size / 4 ? LeftHalf : RightHalf;
                counts[cell, half]++;
            }

            return counts;
        }

        private static void AssertCells(int[,] counts, string label, params int[] coveredHalfPerCell)
        {
            for (int cell = 0; cell < 4; cell++)
            {
                for (int half = 0; half < 2; half++)
                {
                    string where = label + ", " + CellNames[cell] + (half == LeftHalf ? " left half" : " right half");
                    if (coveredHalfPerCell[cell] == half)
                    {
                        Assert.That(counts[cell, half], Is.GreaterThan(CellCoveredPixels), where + " covered");
                    }
                    else
                    {
                        Assert.That(counts[cell, half], Is.Zero, where + " empty");
                    }
                }
            }
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

        private static void IssueBatch(
            VpIndexedIndirectDrawBatch batch,
            Issue issue,
            Material forward,
            Material shadow,
            VpGpuIndexedGeometryBuffers buffers,
            int startCommand,
            int commandCount,
            Camera camera)
        {
            var properties = new MaterialPropertyBlock();
            switch (issue)
            {
                case Issue.ForwardOnly:
                    batch.RenderForward(forward, properties, buffers, 0, startCommand, commandCount, camera);
                    break;
                case Issue.ShadowOnly:
                    batch.RenderShadows(shadow, properties, buffers, 0, startCommand, commandCount, camera);
                    break;
                default:
                    batch.Render(forward, shadow, properties, buffers, 0, startCommand, commandCount, camera);
                    break;
            }
        }

        /// <summary>Appends the padding quad, A and B to a pool, checking where their ranges land.</summary>
        private (VpGeometryRange a, VpGeometryRange b) AppendPaddedAB(VpCpuGeometryPool pool)
        {
            Assert.That(pool.TryAppend(FlatMesh(PaddingQuad, new[] { 0, 1, 2, 0, 2, 3 }), out VpGeometryRange padding), Is.True, "append padding");
            Assert.That(pool.TryAppend(Triangle(TriangleA), out VpGeometryRange a), Is.True, "append A");
            Assert.That(pool.TryAppend(Triangle(TriangleB), out VpGeometryRange b), Is.True, "append B");
            Assert.That(
                new[] { padding.vertexStart, padding.indexStart, a.vertexStart, a.indexStart, b.vertexStart, b.indexStart },
                Is.EqualTo(new[] { 0, 0, 4, 6, 7, 9 }),
                "vertex and index starts of padding, A and B");
            return (a, b);
        }

        /// <summary>Renders the padded A / B pool with the Stage 3 batch built from the layout, issuing the chosen commands.</summary>
        private Color32[] RenderAB(
            Func<VpGeometryRange, VpGeometryRange, (VpIndirectCommand[] commands, Matrix4x4[] transforms)> layout,
            int startCommand = 0,
            int commandCount = -1,
            bool singlePassInstanced = false,
            Issue issue = Issue.Both)
        {
            Material forward = Material(ShaderName, Color.green);
            Material shadow = Material(ShadowShaderName, Color.green);
            (Camera camera, RenderTexture target) = FrontView("VP Indexed Indirect Test Camera");
            using (var pool = new VpCpuGeometryPool(10, 12, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(10, 12))
            using (var batch = new VpIndexedIndirectDrawBatch(4, 8))
            {
                (VpGeometryRange a, VpGeometryRange b) = AppendPaddedAB(pool);
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");
                (VpIndirectCommand[] commands, Matrix4x4[] transforms) = layout(a, b);
                Assert.That(batch.TryUpload(commands, transforms, singlePassInstanced), Is.True, "batch upload");
                IssueBatch(batch, issue, forward, shadow, buffers, startCommand, commandCount < 0 ? batch.CommandCount : commandCount, camera);
                return RenderAndRead(camera, target);
            }
        }

        private static (VpIndirectCommand[] commands, Matrix4x4[] transforms) OneAThreeB(VpGeometryRange a, VpGeometryRange b)
        {
            return (new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) }, AllCells);
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

        private (Camera camera, RenderTexture target) LitTopDownView()
        {
            Light light = Track(new GameObject("VP Indexed Shadow Test Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera("VP Indexed Shadow Test Camera", target);
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            return (camera, target);
        }

        private Color32[] LitGround(Action<Camera, Action> queueVp)
        {
            (Camera camera, RenderTexture target) = LitTopDownView();
            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            if (queueVp == null)
            {
                return RenderAndRead(camera, target);
            }

            Color32[] pixels = null;
            queueVp(camera, () => pixels = RenderAndRead(camera, target));
            return pixels;
        }

        /// <summary>Queues a built-in mesh, appended after the padding quad, as one Stage 3 command at the transforms.</summary>
        private void RenderIndexedMesh(Mesh mesh, Matrix4x4[] objectToWorlds, Color color, Camera camera, Action render, Issue issue = Issue.Both, bool singlePassInstanced = false)
        {
            Material forward = Material(ShaderName, color);
            Material shadow = Material(ShadowShaderName, color);
            int indexCount = (int)mesh.GetIndexCount(0);
            using (var pool = new VpCpuGeometryPool(mesh.vertexCount + 4, indexCount + 6, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(mesh.vertexCount + 4, indexCount + 6))
            using (var batch = new VpIndexedIndirectDrawBatch(1, objectToWorlds.Length))
            {
                Assert.That(pool.TryAppend(FlatMesh(PaddingQuad, new[] { 0, 1, 2, 0, 2, 3 }), out _), Is.True);
                Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(range, mesh.bounds, objectToWorlds.Length) }, objectToWorlds, singlePassInstanced), Is.True);
                IssueBatch(batch, issue, forward, shadow, buffers, 0, batch.CommandCount, camera);
                render();
            }
        }

        private Color32[] LitGroundWithCubes(Issue issue, bool singlePassInstanced = false)
        {
            return LitGround((camera, render) => RenderIndexedMesh(Resources.GetBuiltinResource<Mesh>("Cube.fbx"), TwoCubes, Color.green, camera, render, issue, singlePassInstanced));
        }

        private static float[] Luminance(Color32[] pixels, Func<Color32, bool> isMarker)
        {
            var luminance = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                luminance[i] = isMarker(p) ? -1f : (p.r + p.g + p.b) / 3f;
            }

            return luminance;
        }

        private static float[] GreenMarkedLuminance(Color32[] pixels)
        {
            return Luminance(pixels, p => p.g > p.r + 50);
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

        private static (int marker, int shadowed) MarkerAndShadow(float[] without, float[] with, float shadowedFraction)
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
                else if (with[i] < lit * shadowedFraction && !(without[i] >= 0f && without[i] < lit * shadowedFraction))
                {
                    shadowed++;
                }
            }

            return (marker, shadowed);
        }

        private static void AssertShadowAppears(float[] without, float[] with, float shadowedFraction, string what)
        {
            (int marker, int shadowed) = MarkerAndShadow(without, with, shadowedFraction);
            Assert.That(LitBrightness(without), Is.GreaterThan(40f), what + ": lit brightness");
            Assert.That(marker, Is.GreaterThan(CoveredPixels), what + ": marker pixels");
            Assert.That(shadowed, Is.GreaterThan(ShadowedPixels), what + ": shadowed pixels");
        }

        /// <summary>Reads back indexed arguments: indexCountPerInstance, instanceCount, startIndex, baseVertexIndex, startInstance.</summary>
        private static void AssertArguments(GraphicsBuffer argumentBuffer, string label, params uint[][] expected)
        {
            var readback = new GraphicsBuffer.IndirectDrawIndexedArgs[expected.Length];
            argumentBuffer.GetData(readback, 0, 0, expected.Length);
            for (int c = 0; c < expected.Length; c++)
            {
                Assert.That(
                    new[] { readback[c].indexCountPerInstance, readback[c].instanceCount, readback[c].startIndex, readback[c].baseVertexIndex, readback[c].startInstance },
                    Is.EqualTo(expected[c]),
                    label + " command " + c + ": indexCountPerInstance, instanceCount, startIndex, baseVertexIndex, startInstance");
            }
        }

        private static HashSet<string> LightModes(Shader shader)
        {
            var lightModes = new HashSet<string>();
            for (int pass = 0; pass < shader.passCount; pass++)
            {
                lightModes.Add(shader.FindPassTagValue(pass, new ShaderTagId("LightMode")).name.ToUpperInvariant());
            }

            return lightModes;
        }

        [Test]
        public void TheIndexedShaders_SplitTheColourAndTheShadowCasterPasses()
        {
            Shader forward = Shader.Find(ShaderName);
            Shader shadow = Shader.Find(ShadowShaderName);
            foreach ((Shader shader, string name) in new[] { (forward, ShaderName), (shadow, ShadowShaderName) })
            {
                Assert.That(shader, Is.Not.Null, name);
                Assert.That(ShaderUtil.ShaderHasError(shader), Is.False, name + " has no error");
                Assert.That(shader.isSupported, Is.True, name + " is supported");
            }

            Assert.That(LightModes(forward), Does.Contain("UNIVERSALFORWARD"));
            Assert.That(LightModes(forward), Does.Not.Contain("SHADOWCASTER"));
            Assert.That(LightModes(shadow), Does.Contain("SHADOWCASTER"));
            Assert.That(LightModes(shadow), Does.Not.Contain("UNIVERSALFORWARD"));
            Assert.That(shadow.passCount, Is.EqualTo(1));
        }

        [Test]
        public void TheIndexBuffer_IsAHardwareIndexBufferHoldingTheGlobalVertexNumbers()
        {
            using (var pool = new VpCpuGeometryPool(10, 12, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(10, 12))
            {
                AppendPaddedAB(pool);
                Assert.That(buffers.TryUpload(pool), Is.True);

                Assert.That(buffers.IndexBuffer.target & GraphicsBuffer.Target.Index, Is.EqualTo(GraphicsBuffer.Target.Index), "index target");
                Assert.That(buffers.IndexBuffer.target & GraphicsBuffer.Target.Structured, Is.EqualTo((GraphicsBuffer.Target)0), "not structured");
                Assert.That(buffers.VertexBuffer.target & GraphicsBuffer.Target.Structured, Is.EqualTo(GraphicsBuffer.Target.Structured), "structured vertices");
                Assert.That(new[] { buffers.IndexBuffer.stride, buffers.IndexBuffer.count, buffers.VertexBuffer.stride }, Is.EqualTo(new[] { 4, 12, 32 }));

                var indices = new uint[12];
                buffers.IndexBuffer.GetData(indices);
                Assert.That(indices, Is.EqualTo(new uint[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 7, 8, 9 }), "global vertex numbers");
            }
        }

        [Test]
        public void ALaterRange_IsDrawnFromItsGlobalVertexNumbers()
        {
            // B (vertices 7..9, indices 9..11) alone at every cell. A local vertex ID would read the padding quad
            // (off-screen) and an index-position ID would read past the pool; only the global vertex number draws B, in
            // the right half of every cell. A alone (vertices 4..6, indices 6..8) fills the left halves.
            int[,] onlyB = GreenRegions(RenderAB((a, b) => (new[] { new VpIndirectCommand(b, CellBounds, 4) }, AllCells)));
            int[,] onlyA = GreenRegions(RenderAB((a, b) => (new[] { new VpIndirectCommand(a, CellBounds, 4) }, AllCells)));

            AssertCells(onlyB, "B alone", RightHalf, RightHalf, RightHalf, RightHalf);
            AssertCells(onlyA, "A alone", LeftHalf, LeftHalf, LeftHalf, LeftHalf);
        }

        [Test]
        public void OneIndexedCallOfTwoCommands_DrawsEachRangeAtItsOwnTransformsOnly()
        {
            AssertCells(GreenRegions(RenderAB(OneAThreeB)), "one A, three B", LeftHalf, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void CommandsInTheOtherOrder_DrawTheSameCells()
        {
            int[,] counts = GreenRegions(RenderAB((a, b) => (
                new[] { new VpIndirectCommand(b, CellBounds, 3), new VpIndirectCommand(a, CellBounds, 1) },
                new[] { CellTopRight, CellBottomLeft, CellBottomRight, CellTopLeft })));

            AssertCells(counts, "three B, one A", LeftHalf, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void StartCommandAndCommandCount_DrawOnlyTheSelectedCommand()
        {
            AssertCells(GreenRegions(RenderAB(OneAThreeB, 0, 1)), "command 0 only", LeftHalf, -1, -1, -1);
            AssertCells(GreenRegions(RenderAB(OneAThreeB, 1, 1)), "command 1 only", -1, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void TheIndexedImage_MatchesStage2OfTheSameRangesAndTransforms()
        {
            Color32[] indexed = RenderAB(OneAThreeB);

            Material forward = Material(Stage2ShaderName, Color.green);
            Material shadow = Material(Stage2ShadowShaderName, Color.green);
            (Camera camera, RenderTexture target) = FrontView("VP Stage 2 Comparison Camera");
            Color32[] stage2;
            using (var pool = new VpCpuGeometryPool(10, 12, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(10, 12))
            using (var batch = new VpIndirectDrawBatch(4, 8))
            {
                (VpGeometryRange a, VpGeometryRange b) = AppendPaddedAB(pool);
                Assert.That(buffers.TryUpload(pool), Is.True);
                (VpIndirectCommand[] commands, Matrix4x4[] transforms) = OneAThreeB(a, b);
                Assert.That(batch.TryUpload(commands, transforms), Is.True);
                batch.Render(forward, shadow, new MaterialPropertyBlock(), buffers, 0, camera);
                stage2 = RenderAndRead(camera, target);
            }

            Assert.That(CountGreen(stage2), Is.GreaterThan(4 * CellCoveredPixels), "Stage 2 coverage");
            Assert.That(CountDiffering(indexed, stage2), Is.Zero, "pixels differing from Stage 2");
        }

        [Test]
        public void IndexedDraws_AreDepthTestedAgainstAUnityMesh()
        {
            InEmptyScene(() =>
            {
                GameObject quad = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                quad.transform.localScale = new Vector3(4f, 4f, 1f);
                Material red = Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
                red.SetColor("_BaseColor", Color.red);
                quad.GetComponent<MeshRenderer>().sharedMaterial = red;

                int[,] counts = GreenRegions(RenderAB((a, b) => (
                    new[] { new VpIndirectCommand(b, CellBounds, 2) },
                    new[] { CellTopLeft * Matrix4x4.Translate(new Vector3(0f, 0f, -1f)), CellBottomLeft * Matrix4x4.Translate(new Vector3(0f, 0f, 1f)) })));

                AssertCells(counts, "B in front of and behind the quad", RightHalf, -1, -1, -1);
            });
        }

        [Test]
        public void TheForwardAndShadowCalls_CastAShadowOntoAUnityMesh()
        {
            InEmptyScene(() =>
            {
                float[] without = GreenMarkedLuminance(LitGround(null));
                DestroyObjects();
                AssertShadowAppears(without, GreenMarkedLuminance(LitGroundWithCubes(Issue.Both)), LitShadowedFraction, "Unity mesh ground under VP indexed cubes");
            });
        }

        [Test]
        public void TheForwardCallAlone_CastsNoShadow_AndTheShadowCallAlone_DrawsNoColour()
        {
            InEmptyScene(() =>
            {
                float[] without = GreenMarkedLuminance(LitGround(null));
                DestroyObjects();
                (int forwardMarker, int forwardShadowed) = MarkerAndShadow(without, GreenMarkedLuminance(LitGroundWithCubes(Issue.ForwardOnly)), LitShadowedFraction);
                DestroyObjects();
                (int shadowMarker, int shadowShadowed) = MarkerAndShadow(without, GreenMarkedLuminance(LitGroundWithCubes(Issue.ShadowOnly)), LitShadowedFraction);

                Assert.That(forwardMarker, Is.GreaterThan(CoveredPixels), "the forward call draws the cubes");
                Assert.That(forwardShadowed, Is.Zero, "shadowed pixels from the forward call alone");
                Assert.That(shadowMarker, Is.Zero, "cube pixels drawn by the shadow call");
                Assert.That(shadowShadowed, Is.GreaterThan(ShadowedPixels), "shadowed pixels from the shadow call alone");
            });
        }

        [Test]
        public void AnIndexedDraw_ReceivesTheMainLightShadowOfAUnityMesh()
        {
            float[] VpGroundFromAbove(bool drawOccluder)
            {
                (Camera camera, RenderTexture target) = LitTopDownView();
                if (drawOccluder)
                {
                    GameObject occluder = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
                    Object.DestroyImmediate(occluder.GetComponent<Collider>());
                    occluder.transform.position = new Vector3(0f, 1.2f, 0f);
                    Material red = Track(new Material(Shader.Find("Universal Render Pipeline/Lit")));
                    red.SetColor("_BaseColor", Color.red);
                    MeshRenderer occluderRenderer = occluder.GetComponent<MeshRenderer>();
                    occluderRenderer.sharedMaterial = red;
                    occluderRenderer.shadowCastingMode = ShadowCastingMode.On;
                }

                Color32[] pixels = null;
                RenderIndexedMesh(
                    Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                    new[] { Matrix4x4.Scale(new Vector3(10f, 0.1f, 10f)) },
                    Color.white,
                    camera,
                    () => pixels = RenderAndRead(camera, target));
                return Luminance(pixels, p => p.r > p.g + 50);
            }

            InEmptyScene(() =>
            {
                float[] without = VpGroundFromAbove(false);
                DestroyObjects();
                AssertShadowAppears(without, VpGroundFromAbove(true), VpShadowedFraction, "VP indexed ground under a Unity mesh cube");
            });
        }

        [Test]
        public void TheArgumentBuffers_ReadBackAsUploaded_WithOnlyTheForwardOneDoubledForSinglePassInstanced()
        {
            var a = new VpGeometryRange(4, 3, 6, 3);
            var b = new VpGeometryRange(7, 3, 9, 3);
            using (var batch = new VpIndexedIndirectDrawBatch(3, 5))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) }, AllCells, false), Is.True);
                AssertArguments(batch.ForwardArgumentBuffer, "forward", new uint[] { 3, 1, 6, 0, 0 }, new uint[] { 3, 3, 9, 0, 1 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow", new uint[] { 3, 1, 6, 0, 0 }, new uint[] { 3, 3, 9, 0, 1 });
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 2, 4 }));
                Assert.That(batch.WorldBounds.min, Is.EqualTo(new Vector3(-1f, -1f, -0.05f)));
                Assert.That(batch.WorldBounds.max, Is.EqualTo(new Vector3(1f, 1f, 0.05f)));

                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) }, AllCells, true), Is.True);
                AssertArguments(batch.ForwardArgumentBuffer, "forward doubled", new uint[] { 3, 2, 6, 0, 0 }, new uint[] { 3, 6, 9, 0, 2 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow logical", new uint[] { 3, 1, 6, 0, 0 }, new uint[] { 3, 3, 9, 0, 1 });
                Assert.That(batch.SinglePassInstanced, Is.True);
                var uploaded = new Matrix4x4[4];
                batch.InstanceBuffer.GetData(uploaded, 0, 0, 4);
                Assert.That(uploaded, Is.EqualTo(AllCells), "one transform per logical instance");
            }
        }

        [Test]
        public void SinglePassInstancedArguments_DrawTheSameImageAndShadowInASingleViewCamera()
        {
            Color32[] flat = RenderAB(OneAThreeB);
            Color32[] doubled = RenderAB(OneAThreeB, singlePassInstanced: true);
            AssertCells(GreenRegions(doubled), "one A, three B, doubled", LeftHalf, RightHalf, RightHalf, RightHalf);
            Assert.That(CountDiffering(flat, doubled), Is.Zero, "pixels differing from flat arguments");
            AssertCells(GreenRegions(RenderAB(OneAThreeB, 1, 1, true)), "command 1 only, doubled", -1, RightHalf, RightHalf, RightHalf);

            InEmptyScene(() =>
            {
                Color32[] flatShadow = LitGroundWithCubes(Issue.Both);
                DestroyObjects();
                Color32[] doubledShadow = LitGroundWithCubes(Issue.Both, true);
                Assert.That(CountDiffering(flatShadow, doubledShadow), Is.Zero, "shadow pixels differing from flat arguments");
            });
        }

        [Test]
        public void RejectedUploads_KeepTheEarlierCommands_AndZeroCommandsIssueNothing()
        {
            var a = new VpGeometryRange(4, 3, 6, 3);
            using (var batch = new VpIndexedIndirectDrawBatch(2, 3))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 2) }, new[] { CellTopLeft, CellTopRight }, false), Is.True);
                Bounds bounds = batch.WorldBounds;
                foreach ((VpIndirectCommand[] commands, Matrix4x4[] transforms, string label) in new[]
                         {
                             (new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(a, CellBounds, 1) },
                                 new[] { CellTopLeft, CellTopLeft, CellTopLeft }, "more commands than capacity"),
                             (new[] { new VpIndirectCommand(a, CellBounds, 4) }, new[] { CellTopLeft, CellTopLeft, CellTopLeft, CellTopLeft }, "more instances than capacity"),
                             (new[] { new VpIndirectCommand(a, CellBounds, 2) }, new[] { CellTopLeft }, "fewer transforms than instances"),
                             (new[] { new VpIndirectCommand(a, CellBounds, -1) }, new Matrix4x4[0], "a negative instance count"),
                             (new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, -1, 3), CellBounds, 1) }, new[] { CellTopLeft }, "a negative index start"),
                         })
                {
                    Assert.That(batch.TryUpload(commands, transforms, false), Is.False, label);
                    Assert.That(batch.TryUpload(commands, transforms, true), Is.False, label + " for Single Pass Instanced");
                }

                AssertArguments(batch.ForwardArgumentBuffer, "forward", new uint[] { 3, 2, 6, 0, 0 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow", new uint[] { 3, 2, 6, 0, 0 });
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(batch.WorldBounds, Is.EqualTo(bounds));
                Assert.That(batch.SinglePassInstanced, Is.False, "Single Pass Instanced mode kept");
                var keptTransforms = new Matrix4x4[2];
                batch.InstanceBuffer.GetData(keptTransforms, 0, 0, 2);
                Assert.That(keptTransforms, Is.EqualTo(new[] { CellTopLeft, CellTopRight }), "transforms kept");
            }

            Assert.That(CountGreen(RenderAB((a2, b2) => (new VpIndirectCommand[0], new Matrix4x4[0]))), Is.Zero, "zero commands");
        }

        [Test]
        public void CommandsOutsideTheUploadedOnes_Throw()
        {
            Material forward = Material(ShaderName, Color.green);
            Material shadow = Material(ShadowShaderName, Color.green);
            using (var buffers = new VpGpuIndexedGeometryBuffers(3, 3))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 2))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), CellBounds, 1) }, new[] { CellTopLeft }, false), Is.True);
                var properties = new MaterialPropertyBlock();
                Assert.DoesNotThrow(() => batch.Render(forward, shadow, properties, buffers, 0, 1, 0), "zero commands after the last");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(forward, shadow, properties, buffers, 0, -1, 1), "negative start");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(forward, shadow, properties, buffers, 0, 0, 2), "beyond the uploaded commands");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.RenderForward(forward, properties, buffers, 0, 0, 2, null), "forward call beyond");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.RenderShadows(shadow, properties, buffers, 0, 0, 2, null), "shadow call beyond");
            }
        }

        [Test]
        public void TheGeometryBuffers_RejectAPoolLargerThanTheirCapacity_KeepingTheirContents()
        {
            using (var pool = new VpCpuGeometryPool(10, 12, Allocator.Persistent))
            using (var quadPool = new VpCpuGeometryPool(4, 6, Allocator.Persistent))
            using (var small = new VpGpuIndexedGeometryBuffers(9, 12))
            using (var fewIndices = new VpGpuIndexedGeometryBuffers(10, 11))
            using (var empty = new VpGpuIndexedGeometryBuffers(1, 1))
            using (var emptyPool = new VpCpuGeometryPool(1, 1, Allocator.Persistent))
            {
                Assert.That(empty.TryUpload(emptyPool), Is.True, "empty pool");
                Assert.That(quadPool.TryAppend(FlatMesh(PaddingQuad, new[] { 0, 1, 2, 0, 2, 3 }), out _), Is.True, "append the quad");
                Assert.That(small.TryUpload(quadPool), Is.True, "a pool that fits");
                Assert.That(fewIndices.TryUpload(quadPool), Is.True, "a pool that fits");

                AppendPaddedAB(pool);
                Assert.That(small.TryUpload(pool), Is.False, "too few vertices");
                Assert.That(fewIndices.TryUpload(pool), Is.False, "too few indices");
                Assert.Throws<ArgumentNullException>(() => small.TryUpload(null));

                foreach ((VpGpuIndexedGeometryBuffers buffers, string label) in new[] { (small, "too few vertices"), (fewIndices, "too few indices") })
                {
                    var keptIndices = new uint[6];
                    buffers.IndexBuffer.GetData(keptIndices, 0, 0, 6);
                    Assert.That(keptIndices, Is.EqualTo(new uint[] { 0, 1, 2, 0, 2, 3 }), label + ": indices kept after the rejected upload");
                    var keptVertices = new VpRenderVertex[4];
                    buffers.VertexBuffer.GetData(keptVertices, 0, 0, 4);
                    Assert.That(Array.ConvertAll(keptVertices, vertex => vertex.position), Is.EqualTo(PaddingQuad), label + ": vertices kept after the rejected upload");
                }
            }
        }

        [Test]
        public void TheConstructorRejectsEmptyCapacities_AndDisposeReleasesEveryBufferOnce()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexedIndirectDrawBatch(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexedIndirectDrawBatch(1, 0));

            Material forward = Material(ShaderName, Color.green);
            Material shadow = Material(ShadowShaderName, Color.green);
            var buffers = new VpGpuIndexedGeometryBuffers(3, 3);
            var batch = new VpIndexedIndirectDrawBatch(1, 1);
            GraphicsBuffer[] owned = { batch.ForwardArgumentBuffer, batch.ShadowArgumentBuffer, batch.InstanceBuffer, buffers.VertexBuffer, buffers.IndexBuffer };
            Assert.That(Array.TrueForAll(owned, buffer => buffer.IsValid()), Is.True, "valid before disposal");

            batch.Dispose();
            Assert.That(new[] { owned[0].IsValid(), owned[1].IsValid(), owned[2].IsValid() }, Is.EqualTo(new[] { false, false, false }), "batch buffers released");
            Assert.That(new[] { owned[3].IsValid(), owned[4].IsValid() }, Is.EqualTo(new[] { true, true }), "geometry buffers kept");
            Assert.Throws<ObjectDisposedException>(() => batch.TryUpload(new VpIndirectCommand[0], new Matrix4x4[0], false), "upload");
            Assert.Throws<ObjectDisposedException>(() => batch.Render(forward, shadow, new MaterialPropertyBlock(), buffers, 0), "render");
            Assert.Throws<ObjectDisposedException>(() => batch.RenderForward(forward, new MaterialPropertyBlock(), buffers, 0, 0, 0, null), "forward call");
            Assert.Throws<ObjectDisposedException>(() => batch.RenderShadows(shadow, new MaterialPropertyBlock(), buffers, 0, 0, 0, null), "shadow call");
            Assert.Throws<ObjectDisposedException>(() => _ = batch.ForwardArgumentBuffer, "forward argument buffer");
            Assert.DoesNotThrow(batch.Dispose, "dispose the batch again");

            buffers.Dispose();
            Assert.That(new[] { owned[3].IsValid(), owned[4].IsValid() }, Is.EqualTo(new[] { false, false }), "geometry buffers released");
            Assert.Throws<ObjectDisposedException>(() => _ = buffers.IndexBuffer, "index buffer");
            Assert.Throws<ObjectDisposedException>(() => _ = buffers.VertexBuffer, "vertex buffer");
            Assert.Throws<ObjectDisposedException>(() => buffers.TryUpload(null), "upload");
            Assert.DoesNotThrow(buffers.Dispose, "dispose the buffers again");
        }
    }
}
