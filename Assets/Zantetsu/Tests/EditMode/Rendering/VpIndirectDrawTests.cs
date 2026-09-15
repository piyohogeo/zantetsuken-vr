using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Stage 2 VP draw (DESIGN 4.5.5): the forward shader has only a colour pass and the shadow shader only a shadow
    /// caster pass; the batch's forward indirect call of several commands draws each geometry range, chosen by
    /// startVertex, at its own run of transforms, chosen by startInstance, and nowhere else; startCommand and commandCount
    /// select the same commands for the forward and the shadow call; the image matches Stage 1 Direct draws; depth is
    /// tested against a Unity mesh; the forward call alone casts no shadow, the shadow call casts one onto a Unity mesh
    /// without drawing any colour, and the draw receives a Unity mesh's shadow; both argument buffers read back as
    /// uploaded, with only the forward one doubled for Single Pass Instanced and the instance buffer holding logical
    /// instances; forward arguments uploaded for Single Pass Instanced still draw the same image and shadow in a
    /// single-view camera; rejected uploads, a physical instance overflow, zero commands and disposal change or issue
    /// nothing. The stereo pass itself needs an XR device and is not covered here. Coverage is counted per image region.
    /// </summary>
    public class VpIndirectDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indirect Shadow Caster";
        private const string DirectShaderName = "Zantetsu/VP Unlit";
        private const int Size = 64;
        private const int CellCoveredPixels = 60;
        private const int CoveredPixels = 200;
        private const int ShadowSize = 128;
        private const int ShadowedPixels = 300;
        private const float LitShadowedFraction = 0.7f;
        private const float VpShadowedFraction = 0.9f;

        // Camera regions: the image is split into quadrant cells, each into a left and a right half.
        private const int LeftHalf = 0;
        private const int RightHalf = 1;

        // Geometry A lies in the left half of a unit cell centred on its origin, geometry B in the right half.
        private static readonly Vector3[] TriangleA = { new Vector3(-0.45f, -0.4f, 0f), new Vector3(-0.25f, 0.4f, 0f), new Vector3(-0.05f, -0.4f, 0f) };
        private static readonly Vector3[] TriangleB = { new Vector3(0.05f, -0.4f, 0f), new Vector3(0.25f, 0.4f, 0f), new Vector3(0.45f, -0.4f, 0f) };
        private static readonly Bounds CellBounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));

        private static readonly Matrix4x4 CellTopLeft = Matrix4x4.Translate(new Vector3(-0.5f, 0.5f, 0f));
        private static readonly Matrix4x4 CellTopRight = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f));
        private static readonly Matrix4x4 CellBottomLeft = Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0f));
        private static readonly Matrix4x4 CellBottomRight = Matrix4x4.Translate(new Vector3(0.5f, -0.5f, 0f));

        // Two floating cubes seen from above: one left and one right of the image centre.
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

        private (Material forward, Material shadow) IndirectMaterials(Color color)
        {
            return (Material(ShaderName, color), Material(ShadowShaderName, color));
        }

        private Camera TestCamera(string name, RenderTexture target)
        {
            GameObject cameraObject = Track(new GameObject(name));
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            return camera;
        }

        /// <summary>An orthographic camera looking along +Z over [-1, 1] x [-1, 1] at 32 pixels per unit.</summary>
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

        private Mesh Triangle(Vector3[] positions)
        {
            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
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
                if (IsGreen(pixel))
                {
                    green++;
                }
            }

            return green;
        }

        /// <summary>Green pixels per quadrant cell and half: [cell, half].</summary>
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

        /// <summary>Asserts which half of each cell is covered: -1 none, LeftHalf or RightHalf.</summary>
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
            VpIndirectDrawBatch batch,
            Issue issue,
            Material forward,
            Material shadow,
            VpGpuGeometryBuffers buffers,
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

        /// <summary>
        /// Appends geometry A then B to one pool and uploads it, uploads the batch built from their ranges, issues the
        /// chosen commands, and returns the rendered pixels.
        /// </summary>
        private Color32[] RenderAB(
            Func<VpGeometryRange, VpGeometryRange, (VpIndirectCommand[] commands, Matrix4x4[] transforms)> layout,
            int startCommand = 0,
            int commandCount = -1,
            bool singlePassInstanced = false,
            Issue issue = Issue.Both)
        {
            (Material forward, Material shadow) = IndirectMaterials(Color.green);
            (Camera camera, RenderTexture target) = FrontView("VP Indirect Test Camera");
            using (var pool = new VpCpuGeometryPool(6, 6, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(6, 6))
            using (var batch = new VpIndirectDrawBatch(4, 8))
            {
                Assert.That(pool.TryAppend(Triangle(TriangleA), out VpGeometryRange a), Is.True, "append A");
                Assert.That(pool.TryAppend(Triangle(TriangleB), out VpGeometryRange b), Is.True, "append B");
                Assert.That(new[] { a.indexStart, b.indexStart }, Is.EqualTo(new[] { 0, 3 }), "B's indices follow A's");
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");
                (VpIndirectCommand[] commands, Matrix4x4[] transforms) = layout(a, b);
                Assert.That(batch.TryUpload(commands, transforms, singlePassInstanced), Is.True, "batch upload");
                IssueBatch(batch, issue, forward, shadow, buffers, startCommand, commandCount < 0 ? batch.CommandCount : commandCount, camera);
                return RenderAndRead(camera, target);
            }
        }

        /// <summary>A once at the top left; B at the other three cells.</summary>
        private static (VpIndirectCommand[] commands, Matrix4x4[] transforms) OneAThreeB(VpGeometryRange a, VpGeometryRange b)
        {
            return (
                new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) },
                new[] { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight });
        }

        /// <summary>Runs the body in a new empty scene, so its directional light is the main light, then restores the scenes.</summary>
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

        /// <summary>A low directional light with hard shadows and a top-down orthographic camera over the origin.</summary>
        private (Camera camera, RenderTexture target) LitTopDownView()
        {
            GameObject sun = Track(new GameObject("VP Indirect Shadow Test Sun"));
            sun.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera("VP Indirect Shadow Test Camera", target);
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            return (camera, target);
        }

        /// <summary>
        /// A lit Unity mesh ground seen from above. <paramref name="queueVp"/>, when given, queues VP draws for the camera
        /// and then calls the render it is handed while its buffers are alive.
        /// </summary>
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

        /// <summary>
        /// Queues a built-in mesh as one command drawn at the transforms, issued as chosen, for the camera, keeping its
        /// buffers alive for the render.
        /// </summary>
        private void RenderIndirectMesh(
            Mesh mesh,
            Matrix4x4[] objectToWorlds,
            Color color,
            Camera camera,
            Action render,
            Issue issue = Issue.Both,
            bool singlePassInstanced = false)
        {
            (Material forward, Material shadow) = IndirectMaterials(color);
            int indexCount = (int)mesh.GetIndexCount(0);
            using (var pool = new VpCpuGeometryPool(mesh.vertexCount, indexCount, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(mesh.vertexCount, indexCount))
            using (var batch = new VpIndirectDrawBatch(1, objectToWorlds.Length))
            {
                Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(range, mesh.bounds, objectToWorlds.Length) }, objectToWorlds, singlePassInstanced), Is.True);
                IssueBatch(batch, issue, forward, shadow, buffers, 0, batch.CommandCount, camera);
                render();
            }
        }

        private Color32[] LitGroundWithCubes(Issue issue, bool singlePassInstanced = false)
        {
            return LitGround((camera, render) => RenderIndirectMesh(
                Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                TwoCubes,
                Color.green,
                camera,
                render,
                issue,
                singlePassInstanced));
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

        /// <summary>Marker pixels and newly shadowed non-marker pixels of a render with VP draws, compared with one without.</summary>
        private static (int marker, int shadowed) MarkerAndShadow(float[] without, float[] with, float shadowedFraction, Func<int, bool> region = null)
        {
            float lit = LitBrightness(without);
            int marker = 0;
            int shadowed = 0;
            for (int i = 0; i < with.Length; i++)
            {
                if (region != null && !region(i))
                {
                    continue;
                }

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
            float lit = LitBrightness(without);
            int shadowedWithout = 0;
            foreach (float value in without)
            {
                if (value >= 0f && value < lit * shadowedFraction)
                {
                    shadowedWithout++;
                }
            }

            (int marker, int shadowed) = MarkerAndShadow(without, with, shadowedFraction);
            Assert.That(lit, Is.GreaterThan(40f), what + ": lit brightness");
            Assert.That(shadowedWithout, Is.Zero, what + ": shadowed pixels without the shadowing object");
            Assert.That(marker, Is.GreaterThan(CoveredPixels), what + ": marker pixels");
            Assert.That(shadowed, Is.GreaterThan(ShadowedPixels), what + ": shadowed pixels");
        }

        /// <summary>Reads back the first commands of an argument buffer: vertexCountPerInstance, instanceCount, startVertex, startInstance.</summary>
        private static void AssertArguments(GraphicsBuffer argumentBuffer, string label, params uint[][] expected)
        {
            var readback = new GraphicsBuffer.IndirectDrawArgs[expected.Length];
            argumentBuffer.GetData(readback, 0, 0, expected.Length);
            for (int c = 0; c < expected.Length; c++)
            {
                Assert.That(
                    new[] { readback[c].vertexCountPerInstance, readback[c].instanceCount, readback[c].startVertex, readback[c].startInstance },
                    Is.EqualTo(expected[c]),
                    label + " command " + c + ": vertexCountPerInstance, instanceCount, startVertex, startInstance");
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
        public void TheIndirectShaders_SplitTheColourAndTheShadowCasterPasses()
        {
            Shader forward = Shader.Find(ShaderName);
            Shader shadow = Shader.Find(ShadowShaderName);

            foreach ((Shader shader, string name) in new[] { (forward, ShaderName), (shadow, ShadowShaderName) })
            {
                Assert.That(shader, Is.Not.Null, name);
                Assert.That(ShaderUtil.ShaderHasError(shader), Is.False, name + " has no error");
                Assert.That(shader.isSupported, Is.True, name + " is supported");
            }

            Assert.That(LightModes(forward), Does.Contain("UNIVERSALFORWARD"), "forward shader colour pass");
            Assert.That(LightModes(forward), Does.Not.Contain("SHADOWCASTER"), "forward shader has no shadow caster pass");
            Assert.That(LightModes(shadow), Does.Contain("SHADOWCASTER"), "shadow shader shadow caster pass");
            Assert.That(LightModes(shadow), Does.Not.Contain("UNIVERSALFORWARD"), "shadow shader has no colour pass");
            Assert.That(shadow.passCount, Is.EqualTo(1), "shadow shader passes");
        }

        [Test]
        public void OneIndirectCallOfTwoCommands_DrawsEachRangeAtItsOwnTransformsOnly()
        {
            // A's command draws its range (startVertex 0) once at the top left (startInstance 0); B's draws its range
            // (startVertex 3) at the three other cells (startInstance 1). A mistaken vertex offset draws A's triangle for B,
            // and a mistaken instance offset draws B at the top left.
            int[,] counts = GreenRegions(RenderAB(OneAThreeB));

            AssertCells(counts, "one A, three B", LeftHalf, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void CommandsInTheOtherOrder_DrawTheSameCells()
        {
            // B's command first, so B's instances start at 0 and A's at 3.
            int[,] counts = GreenRegions(RenderAB((a, b) => (
                new[] { new VpIndirectCommand(b, CellBounds, 3), new VpIndirectCommand(a, CellBounds, 1) },
                new[] { CellTopRight, CellBottomLeft, CellBottomRight, CellTopLeft })));

            AssertCells(counts, "three B, one A", LeftHalf, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void StartCommandAndCommandCount_DrawOnlyTheSelectedCommand()
        {
            int[,] onlyA = GreenRegions(RenderAB(OneAThreeB, 0, 1));
            int[,] onlyB = GreenRegions(RenderAB(OneAThreeB, 1, 1));

            AssertCells(onlyA, "command 0 only", LeftHalf, -1, -1, -1);
            AssertCells(onlyB, "command 1 only", -1, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void TheIndirectImage_MatchesStage1DirectDrawsOfTheSameRangesAndTransforms()
        {
            Color32[] indirect = RenderAB(OneAThreeB);

            Material material = Material(DirectShaderName, Color.green);
            (Camera camera, RenderTexture target) = FrontView("VP Direct Comparison Camera");
            Color32[] direct;
            using (var pool = new VpCpuGeometryPool(6, 6, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(6, 6))
            {
                Assert.That(pool.TryAppend(Triangle(TriangleA), out VpGeometryRange a), Is.True);
                Assert.That(pool.TryAppend(Triangle(TriangleB), out VpGeometryRange b), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                var properties = new MaterialPropertyBlock();
                var bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
                VpDirectDraw.Render(material, properties, buffers, a, CellTopLeft, bounds, 0, camera);
                VpDirectDraw.Render(material, properties, buffers, b, CellTopRight, bounds, 0, camera);
                VpDirectDraw.Render(material, properties, buffers, b, CellBottomLeft, bounds, 0, camera);
                VpDirectDraw.Render(material, properties, buffers, b, CellBottomRight, bounds, 0, camera);
                direct = RenderAndRead(camera, target);
            }

            int green = 0;
            foreach (Color32 pixel in direct)
            {
                if (IsGreen(pixel))
                {
                    green++;
                    Assert.That(pixel.g, Is.GreaterThan(150), "Stage 1 shaded green");
                }
            }

            Assert.That(green, Is.GreaterThan(4 * CellCoveredPixels), "Stage 1 coverage");
            Assert.That(CountDiffering(indirect, direct), Is.Zero, "pixels differing from Stage 1");
        }

        [Test]
        public void IndirectDraws_AreDepthTestedAgainstAUnityMesh()
        {
            // A red Unity quad fills the view at z = 0. Geometry A is drawn at the top left in front of it and at the bottom
            // left behind it.
            InEmptyScene(() =>
            {
                GameObject quad = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
                Object.DestroyImmediate(quad.GetComponent<Collider>());
                quad.transform.localScale = new Vector3(4f, 4f, 1f);
                Material red = Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
                red.SetColor("_BaseColor", Color.red);
                quad.GetComponent<MeshRenderer>().sharedMaterial = red;

                int[,] counts = GreenRegions(RenderAB((a, b) => (
                    new[] { new VpIndirectCommand(a, CellBounds, 2) },
                    new[] { CellTopLeft * Matrix4x4.Translate(new Vector3(0f, 0f, -1f)), CellBottomLeft * Matrix4x4.Translate(new Vector3(0f, 0f, 1f)) })));

                AssertCells(counts, "in front of and behind the quad", LeftHalf, -1, -1, -1);
            });
        }

        [Test]
        public void TheForwardAndShadowCalls_CastAShadowOntoAUnityMesh()
        {
            InEmptyScene(() =>
            {
                float[] without = GreenMarkedLuminance(LitGround(null));
                DestroyObjects();
                float[] with = GreenMarkedLuminance(LitGroundWithCubes(Issue.Both));
                AssertShadowAppears(without, with, LitShadowedFraction, "Unity mesh ground under VP indirect cubes");
            });
        }

        [Test]
        public void TheForwardCallAlone_CastsNoShadow()
        {
            InEmptyScene(() =>
            {
                float[] without = GreenMarkedLuminance(LitGround(null));
                DestroyObjects();
                float[] forwardOnly = GreenMarkedLuminance(LitGroundWithCubes(Issue.ForwardOnly));

                (int marker, int shadowed) = MarkerAndShadow(without, forwardOnly, LitShadowedFraction);
                Assert.That(marker, Is.GreaterThan(CoveredPixels), "the forward call draws the cubes");
                Assert.That(shadowed, Is.Zero, "shadowed pixels from the forward call alone");
            });
        }

        [Test]
        public void TheShadowCallAlone_CastsAShadowWithoutDrawingColour()
        {
            InEmptyScene(() =>
            {
                float[] without = GreenMarkedLuminance(LitGround(null));
                DestroyObjects();
                Color32[] shadowOnlyPixels = LitGroundWithCubes(Issue.ShadowOnly);

                (int marker, int shadowed) = MarkerAndShadow(without, GreenMarkedLuminance(shadowOnlyPixels), LitShadowedFraction);
                Assert.That(marker, Is.Zero, "cube pixels drawn by the shadow call");
                Assert.That(shadowed, Is.GreaterThan(ShadowedPixels), "shadowed pixels from the shadow call alone");
            });
        }

        [Test]
        public void TheShadowCallAlone_DrawsNoGeometryIntoTheCameraImage()
        {
            Assert.That(CountGreen(RenderAB(OneAThreeB, issue: Issue.ShadowOnly)), Is.Zero, "shadow call alone");
            Assert.That(CountGreen(RenderAB(OneAThreeB, issue: Issue.ForwardOnly)), Is.GreaterThan(4 * CellCoveredPixels), "forward call alone");
            Assert.That(CountDiffering(RenderAB(OneAThreeB, issue: Issue.ForwardOnly), RenderAB(OneAThreeB)), Is.Zero, "adding the shadow call changes no colour pixel");
        }

        [Test]
        public void AnIndirectDraw_ReceivesTheMainLightShadowOfAUnityMesh()
        {
            // A white VP slab seen from above, with and without a red Unity mesh cube over it.
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
                RenderIndirectMesh(
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
                float[] with = VpGroundFromAbove(true);
                AssertShadowAppears(without, with, VpShadowedFraction, "VP indirect ground under a Unity mesh cube");
            });
        }

        [Test]
        public void StartCommandAndCommandCount_SelectTheSameCommandForTheForwardAndTheShadowCall()
        {
            // Two commands of the built-in cube, one instance each: command 0 left of the image centre, command 1 right of
            // it. Selecting one command must draw that cube and cast its shadow on its side only.
            Color32[] Selected(int startCommand)
            {
                return LitGround((camera, render) =>
                {
                    Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                    (Material forward, Material shadow) = IndirectMaterials(Color.green);
                    int indexCount = (int)cube.GetIndexCount(0);
                    using (var pool = new VpCpuGeometryPool(cube.vertexCount, indexCount, Allocator.Persistent))
                    using (var buffers = new VpGpuGeometryBuffers(cube.vertexCount, indexCount))
                    using (var batch = new VpIndirectDrawBatch(2, 2))
                    {
                        Assert.That(pool.TryAppend(cube, out VpGeometryRange range), Is.True);
                        Assert.That(buffers.TryUpload(pool), Is.True);
                        Assert.That(batch.TryUpload(
                            new[] { new VpIndirectCommand(range, cube.bounds, 1), new VpIndirectCommand(range, cube.bounds, 1) },
                            TwoCubes), Is.True);
                        batch.Render(forward, shadow, new MaterialPropertyBlock(), buffers, 0, startCommand, 1, camera);
                        render();
                    }
                });
            }

            bool Left(int pixel) => pixel % ShadowSize < ShadowSize / 2;
            bool Right(int pixel) => !Left(pixel);

            InEmptyScene(() =>
            {
                float[] without = GreenMarkedLuminance(LitGround(null));
                DestroyObjects();
                float[] leftCommand = GreenMarkedLuminance(Selected(0));
                DestroyObjects();
                float[] rightCommand = GreenMarkedLuminance(Selected(1));

                foreach ((float[] with, Func<int, bool> selected, Func<int, bool> other, string label) in new[]
                         {
                             (leftCommand, (Func<int, bool>)Left, (Func<int, bool>)Right, "command 0"),
                             (rightCommand, (Func<int, bool>)Right, (Func<int, bool>)Left, "command 1"),
                         })
                {
                    (int selectedMarker, int selectedShadow) = MarkerAndShadow(without, with, LitShadowedFraction, selected);
                    (int otherMarker, int otherShadow) = MarkerAndShadow(without, with, LitShadowedFraction, other);
                    Assert.That(selectedMarker, Is.GreaterThan(CoveredPixels), label + ": its cube");
                    Assert.That(selectedShadow, Is.GreaterThan(ShadowedPixels), label + ": its shadow");
                    Assert.That(otherMarker, Is.Zero, label + ": the other cube");
                    Assert.That(otherShadow, Is.Zero, label + ": the other shadow");
                }
            });
        }

        [Test]
        public void BothArgumentBuffers_ReadBackAsUploadedAndTheBoundsCoverEveryInstance()
        {
            var a = new VpGeometryRange(0, 3, 0, 3);
            var b = new VpGeometryRange(3, 3, 3, 6);
            using (var batch = new VpIndirectDrawBatch(3, 5))
            {
                Assert.That(batch.TryUpload(
                    new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) },
                    new[] { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight }), Is.True);

                AssertArguments(batch.ForwardArgumentBuffer, "forward", new uint[] { 3, 1, 0, 0 }, new uint[] { 6, 3, 3, 1 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow", new uint[] { 3, 1, 0, 0 }, new uint[] { 6, 3, 3, 1 });
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 2, 4 }));
                Assert.That(batch.SinglePassInstanced, Is.False);
                Assert.That(batch.WorldBounds.min, Is.EqualTo(new Vector3(-1f, -1f, -0.05f)), "bounds min");
                Assert.That(batch.WorldBounds.max, Is.EqualTo(new Vector3(1f, 1f, 0.05f)), "bounds max");
            }
        }

        [Test]
        public void ASinglePassInstancedUpload_DoublesOnlyTheForwardArguments()
        {
            var a = new VpGeometryRange(0, 3, 0, 3);
            var b = new VpGeometryRange(3, 3, 3, 6);
            Matrix4x4[] transforms = { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight };
            using (var batch = new VpIndirectDrawBatch(3, 5))
            {
                Assert.That(batch.TryUpload(
                    new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) },
                    transforms,
                    true), Is.True);

                AssertArguments(batch.ForwardArgumentBuffer, "forward", new uint[] { 3, 2, 0, 0 }, new uint[] { 6, 6, 3, 2 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow", new uint[] { 3, 1, 0, 0 }, new uint[] { 6, 3, 3, 1 });
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 2, 4 }), "logical commands and instances");
                Assert.That(batch.SinglePassInstanced, Is.True);
                Assert.That(batch.InstanceBuffer.count, Is.EqualTo(batch.InstanceCapacity), "instance buffer holds logical capacity");
                var uploaded = new Matrix4x4[transforms.Length];
                batch.InstanceBuffer.GetData(uploaded, 0, 0, transforms.Length);
                Assert.That(uploaded, Is.EqualTo(transforms), "one transform per logical instance");

                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(b, CellBounds, 2) }, new[] { CellTopLeft, CellTopRight }), Is.True, "upload without Single Pass Instanced");
                AssertArguments(batch.ForwardArgumentBuffer, "forward after the flat upload", new uint[] { 6, 2, 3, 0 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow after the flat upload", new uint[] { 6, 2, 3, 0 });
                Assert.That(batch.SinglePassInstanced, Is.False);
            }
        }

        [Test]
        public void APhysicalInstanceRangeThatDoesNotFit_IsRejectedWithoutChangingAnything()
        {
            // Physical instance indices must end by 10 in this batch: 4 logical instances fit doubled, 6 fit only flat.
            var a = new VpGeometryRange(0, 3, 0, 3);
            Matrix4x4[] four = { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight };
            Matrix4x4[] six = { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight, CellTopLeft, CellTopRight };
            using (var batch = new VpIndirectDrawBatch(2, 8, 10))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 4) }, four, true), Is.True, "4 doubled");
                Bounds bounds = batch.WorldBounds;

                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 6) }, six, true), Is.False, "6 doubled");

                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 1, 4 }), "commands and instances");
                Assert.That(batch.SinglePassInstanced, Is.True);
                Assert.That(batch.WorldBounds, Is.EqualTo(bounds));
                AssertArguments(batch.ForwardArgumentBuffer, "forward", new uint[] { 3, 8, 0, 0 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow", new uint[] { 3, 4, 0, 0 });
                var uploaded = new Matrix4x4[four.Length];
                batch.InstanceBuffer.GetData(uploaded, 0, 0, four.Length);
                Assert.That(uploaded, Is.EqualTo(four), "instance transforms");

                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 6) }, six, false), Is.True, "6 flat");
            }
        }

        [Test]
        public void RejectedUploads_KeepTheEarlierCommands()
        {
            var a = new VpGeometryRange(0, 3, 0, 3);
            using (var batch = new VpIndirectDrawBatch(2, 3))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(a, CellBounds, 2) }, new[] { CellTopLeft, CellTopRight }), Is.True);
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
                    Assert.That(batch.TryUpload(commands, transforms), Is.False, label);
                    Assert.That(batch.TryUpload(commands, transforms, true), Is.False, label + " for Single Pass Instanced");
                }

                AssertArguments(batch.ForwardArgumentBuffer, "forward", new uint[] { 3, 2, 0, 0 });
                AssertArguments(batch.ShadowArgumentBuffer, "shadow", new uint[] { 3, 2, 0, 0 });
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(batch.SinglePassInstanced, Is.False);
                Assert.That(batch.WorldBounds, Is.EqualTo(bounds));
            }
        }

        [Test]
        public void SinglePassInstancedArguments_DrawTheSameImageInASingleViewCamera()
        {
            // Forward arguments doubled as for Single Pass Instanced. A single-view camera keeps only the even physical
            // copies, so the image is the one drawn from flat arguments.
            Color32[] flat = RenderAB(OneAThreeB);
            Color32[] doubled = RenderAB(OneAThreeB, singlePassInstanced: true);

            AssertCells(GreenRegions(doubled), "one A, three B, doubled", LeftHalf, RightHalf, RightHalf, RightHalf);
            Assert.That(CountDiffering(flat, doubled), Is.Zero, "pixels differing from flat arguments");
        }

        [Test]
        public void SinglePassInstancedArguments_KeepStartCommandAndCommandCount()
        {
            AssertCells(GreenRegions(RenderAB(OneAThreeB, 0, 1, true)), "command 0 only, doubled", LeftHalf, -1, -1, -1);
            AssertCells(GreenRegions(RenderAB(OneAThreeB, 1, 1, true)), "command 1 only, doubled", -1, RightHalf, RightHalf, RightHalf);
        }

        [Test]
        public void SinglePassInstancedArguments_CastTheSameShadowOntoAUnityMesh()
        {
            InEmptyScene(() =>
            {
                Color32[] without = LitGround(null);
                DestroyObjects();
                Color32[] flat = LitGroundWithCubes(Issue.Both);
                DestroyObjects();
                Color32[] doubled = LitGroundWithCubes(Issue.Both, true);

                AssertShadowAppears(GreenMarkedLuminance(without), GreenMarkedLuminance(doubled), LitShadowedFraction, "Unity mesh ground under doubled VP indirect cubes");
                Assert.That(CountDiffering(flat, doubled), Is.Zero, "pixels differing from flat arguments");
            });
        }

        [Test]
        public void ZeroCommands_IssueNothing()
        {
            Assert.That(CountGreen(RenderAB((a, b) => (new VpIndirectCommand[0], new Matrix4x4[0]))), Is.Zero);
        }

        [Test]
        public void CommandsOutsideTheUploadedOnes_Throw()
        {
            (Material forward, Material shadow) = IndirectMaterials(Color.green);
            using (var buffers = new VpGpuGeometryBuffers(3, 3))
            using (var batch = new VpIndirectDrawBatch(2, 2))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), CellBounds, 1) }, new[] { CellTopLeft }), Is.True);
                var properties = new MaterialPropertyBlock();

                Assert.DoesNotThrow(() => batch.Render(forward, shadow, properties, buffers, 0, 1, 0), "zero commands after the last");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(forward, shadow, properties, buffers, 0, -1, 1), "negative start");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(forward, shadow, properties, buffers, 0, 0, 2), "beyond the uploaded commands");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(forward, shadow, properties, buffers, 0, 1, 1), "starting after the last");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.RenderForward(forward, properties, buffers, 0, 0, 2, null), "forward call beyond the uploaded commands");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.RenderShadows(shadow, properties, buffers, 0, 0, 2, null), "shadow call beyond the uploaded commands");
            }
        }

        [Test]
        public void TheConstructorRejectsEmptyCapacities_AndDisposeReleasesEveryBufferOnce()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndirectDrawBatch(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndirectDrawBatch(1, 0));

            (Material forward, Material shadow) = IndirectMaterials(Color.green);
            using (var buffers = new VpGpuGeometryBuffers(3, 3))
            {
                var batch = new VpIndirectDrawBatch(1, 1);
                GraphicsBuffer forwardArguments = batch.ForwardArgumentBuffer;
                GraphicsBuffer shadowArguments = batch.ShadowArgumentBuffer;
                GraphicsBuffer instances = batch.InstanceBuffer;
                Assert.That(new[] { forwardArguments.IsValid(), shadowArguments.IsValid(), instances.IsValid() }, Is.EqualTo(new[] { true, true, true }));

                batch.Dispose();

                Assert.That(new[] { forwardArguments.IsValid(), shadowArguments.IsValid(), instances.IsValid() }, Is.EqualTo(new[] { false, false, false }), "released");
                Assert.Throws<ObjectDisposedException>(() => batch.TryUpload(new VpIndirectCommand[0], new Matrix4x4[0]), "upload");
                Assert.Throws<ObjectDisposedException>(() => batch.TryUpload(new VpIndirectCommand[0], new Matrix4x4[0], true), "upload for Single Pass Instanced");
                Assert.Throws<ObjectDisposedException>(() => batch.Render(forward, shadow, new MaterialPropertyBlock(), buffers, 0), "render");
                Assert.Throws<ObjectDisposedException>(() => batch.Render(forward, shadow, new MaterialPropertyBlock(), buffers, 0, 0, 0), "render commands");
                Assert.Throws<ObjectDisposedException>(() => batch.RenderForward(forward, new MaterialPropertyBlock(), buffers, 0, 0, 0, null), "forward call");
                Assert.Throws<ObjectDisposedException>(() => batch.RenderShadows(shadow, new MaterialPropertyBlock(), buffers, 0, 0, 0, null), "shadow call");
                Assert.Throws<ObjectDisposedException>(() => _ = batch.ForwardArgumentBuffer, "forward argument buffer");
                Assert.Throws<ObjectDisposedException>(() => _ = batch.ShadowArgumentBuffer, "shadow argument buffer");
                Assert.Throws<ObjectDisposedException>(() => _ = batch.InstanceBuffer, "instance buffer");
                Assert.DoesNotThrow(batch.Dispose, "dispose again");
            }
        }
    }
}
