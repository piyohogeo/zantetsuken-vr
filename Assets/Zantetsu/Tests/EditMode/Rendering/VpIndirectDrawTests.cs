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
    /// Stage 2 VP draw (DESIGN 4.5.5): the Indirect shader compiles with its colour and shadow caster passes; one
    /// Graphics.RenderPrimitivesIndirect call of several commands draws each geometry range, chosen by startVertex, at
    /// its own run of transforms, chosen by startInstance, and nowhere else; startCommand and commandCount select one
    /// command; the image matches Stage 1 Direct draws of the same ranges and transforms; depth is tested against a Unity
    /// mesh; the draw casts a shadow onto a Unity mesh and receives one; the argument buffer reads back as uploaded;
    /// rejected uploads, zero commands and disposal change or issue nothing. Coverage is counted per region of the image.
    /// </summary>
    public class VpIndirectDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Indirect Unlit";
        private const string DirectShaderName = "Zantetsu/VP Unlit";
        private const int Size = 64;
        private const int CellCoveredPixels = 60;
        private const int CoveredPixels = 200;
        private const int ShadowSize = 128;
        private const int ShadowedPixels = 300;
        private const float LitShadowedFraction = 0.7f;
        private const float VpShadowedFraction = 0.9f;

        // Camera regions: the image is split into quadrant cells, each into a left and a right half.
        private const int TopLeft = 0;
        private const int TopRight = 1;
        private const int BottomLeft = 2;
        private const int BottomRight = 3;
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

        private readonly List<Object> _objects = new List<Object>();

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
            material.SetColor("_BaseColor", color);
            return material;
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

        private static readonly string[] CellNames = { "top left", "top right", "bottom left", "bottom right" };

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

        /// <summary>
        /// Appends geometry A then B to one pool and uploads it, uploads the batch built from their ranges, issues the
        /// chosen commands in one indirect call, and returns the rendered pixels.
        /// </summary>
        private Color32[] RenderAB(
            Func<VpGeometryRange, VpGeometryRange, (VpIndirectCommand[] commands, Matrix4x4[] transforms)> layout,
            int startCommand = 0,
            int commandCount = -1)
        {
            Material material = Material(ShaderName, Color.green);
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
                Assert.That(batch.TryUpload(commands, transforms), Is.True, "batch upload");
                batch.Render(material, new MaterialPropertyBlock(), buffers, 0, startCommand, commandCount < 0 ? batch.CommandCount : commandCount, camera);
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

        /// <summary>Queues one indirect call of a built-in mesh at the transforms for the camera, keeping its buffers alive for the render.</summary>
        private static void RenderIndirectMesh(Material material, Mesh mesh, Matrix4x4[] objectToWorlds, Camera camera, Action render)
        {
            int indexCount = (int)mesh.GetIndexCount(0);
            using (var pool = new VpCpuGeometryPool(mesh.vertexCount, indexCount, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(mesh.vertexCount, indexCount))
            using (var batch = new VpIndirectDrawBatch(1, objectToWorlds.Length))
            {
                Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(range, mesh.bounds, objectToWorlds.Length) }, objectToWorlds), Is.True);
                batch.Render(material, new MaterialPropertyBlock(), buffers, 0, camera);
                render();
            }
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

        private static void AssertShadowAppears(float[] without, float[] with, float shadowedFraction, string what)
        {
            float lit = 0f;
            foreach (float value in without)
            {
                lit += Mathf.Max(0f, value);
            }

            lit /= without.Length;
            int shadowedWithout = 0;
            int marker = 0;
            int shadowedWith = 0;
            for (int i = 0; i < with.Length; i++)
            {
                if (without[i] >= 0f && without[i] < lit * shadowedFraction)
                {
                    shadowedWithout++;
                }

                if (with[i] < 0f)
                {
                    marker++;
                }
                else if (with[i] < lit * shadowedFraction)
                {
                    shadowedWith++;
                }
            }

            Assert.That(lit, Is.GreaterThan(40f), what + ": lit brightness");
            Assert.That(shadowedWithout, Is.Zero, what + ": shadowed pixels without the shadowing object");
            Assert.That(marker, Is.GreaterThan(CoveredPixels), what + ": marker pixels");
            Assert.That(shadowedWith, Is.GreaterThan(ShadowedPixels), what + ": shadowed pixels");
        }

        [Test]
        public void TheIndirectShader_CompilesWithAColourAndAShadowCasterPass()
        {
            Shader shader = Shader.Find(ShaderName);

            Assert.That(shader, Is.Not.Null, ShaderName);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(shader.isSupported, Is.True);
            Material material = Material(ShaderName, Color.white);
            Assert.That(material.FindPass("VpIndirectForward"), Is.GreaterThanOrEqualTo(0), "colour pass");
            int shadowPass = material.FindPass("ShadowCaster");
            Assert.That(shadowPass, Is.GreaterThanOrEqualTo(0), "shadow caster pass");
            Assert.That(shader.FindPassTagValue(shadowPass, new ShaderTagId("LightMode")).name, Is.EqualTo("ShadowCaster").IgnoreCase);
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
            int differing = 0;
            for (int i = 0; i < indirect.Length; i++)
            {
                if (IsGreen(direct[i]))
                {
                    green++;
                    Assert.That(direct[i].g, Is.GreaterThan(150), "Stage 1 shaded green");
                }

                if (Math.Abs(indirect[i].r - direct[i].r) > 2 || Math.Abs(indirect[i].g - direct[i].g) > 2 || Math.Abs(indirect[i].b - direct[i].b) > 2)
                {
                    differing++;
                }
            }

            Assert.That(green, Is.GreaterThan(4 * CellCoveredPixels), "Stage 1 coverage");
            Assert.That(differing, Is.Zero, "pixels differing from Stage 1");
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
        public void AnIndirectDraw_CastsAShadowOntoAUnityMesh()
        {
            // A lit Unity mesh ground seen from above, with and without two green VP cubes of one indirect command over it.
            float[] GroundFromAbove(bool drawVpCubes)
            {
                (Camera camera, RenderTexture target) = LitTopDownView();
                GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
                Object.DestroyImmediate(ground.GetComponent<Collider>());
                MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
                groundRenderer.receiveShadows = true;
                groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

                Color32[] pixels = null;
                if (drawVpCubes)
                {
                    RenderIndirectMesh(
                        Material(ShaderName, Color.green),
                        Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                        new[] { Matrix4x4.Translate(new Vector3(-1.5f, 1.2f, 0f)), Matrix4x4.Translate(new Vector3(1.5f, 1.2f, 0f)) },
                        camera,
                        () => pixels = RenderAndRead(camera, target));
                }
                else
                {
                    pixels = RenderAndRead(camera, target);
                }

                return Luminance(pixels, p => p.g > p.r + 50);
            }

            InEmptyScene(() =>
            {
                float[] without = GroundFromAbove(false);
                DestroyObjects();
                float[] with = GroundFromAbove(true);
                AssertShadowAppears(without, with, LitShadowedFraction, "Unity mesh ground under VP indirect cubes");
            });
        }

        [Test]
        public void AnIndirectDraw_ReceivesTheMainLightShadowOfAUnityMesh()
        {
            // A white VP slab of one indirect command seen from above, with and without a red Unity mesh cube over it.
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
                    Material(ShaderName, Color.white),
                    Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                    new[] { Matrix4x4.Scale(new Vector3(10f, 0.1f, 10f)) },
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
        public void TheArgumentBuffer_ReadsBackAsUploadedAndTheBoundsCoverEveryInstance()
        {
            var a = new VpGeometryRange(0, 3, 0, 3);
            var b = new VpGeometryRange(3, 3, 3, 6);
            using (var batch = new VpIndirectDrawBatch(3, 5))
            {
                Assert.That(batch.TryUpload(
                    new[] { new VpIndirectCommand(a, CellBounds, 1), new VpIndirectCommand(b, CellBounds, 3) },
                    new[] { CellTopLeft, CellTopRight, CellBottomLeft, CellBottomRight }), Is.True);

                var readback = new GraphicsBuffer.IndirectDrawArgs[2];
                batch.ArgumentBuffer.GetData(readback, 0, 0, 2);

                Assert.That(
                    new[] { readback[0].vertexCountPerInstance, readback[0].instanceCount, readback[0].startVertex, readback[0].startInstance },
                    Is.EqualTo(new uint[] { 3, 1, 0, 0 }),
                    "A: vertexCountPerInstance, instanceCount, startVertex, startInstance");
                Assert.That(
                    new[] { readback[1].vertexCountPerInstance, readback[1].instanceCount, readback[1].startVertex, readback[1].startInstance },
                    Is.EqualTo(new uint[] { 6, 3, 3, 1 }),
                    "B: vertexCountPerInstance, instanceCount, startVertex, startInstance");
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 2, 4 }));
                Assert.That(batch.WorldBounds.min, Is.EqualTo(new Vector3(-1f, -1f, -0.05f)), "bounds min");
                Assert.That(batch.WorldBounds.max, Is.EqualTo(new Vector3(1f, 1f, 0.05f)), "bounds max");
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
                }

                var readback = new GraphicsBuffer.IndirectDrawArgs[1];
                batch.ArgumentBuffer.GetData(readback, 0, 0, 1);
                Assert.That(new[] { readback[0].vertexCountPerInstance, readback[0].instanceCount }, Is.EqualTo(new uint[] { 3, 2 }), "the earlier command");
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(batch.WorldBounds, Is.EqualTo(bounds));
            }
        }

        [Test]
        public void ZeroCommands_IssueNothing()
        {
            Color32[] pixels = RenderAB((a, b) => (new VpIndirectCommand[0], new Matrix4x4[0]));

            int green = 0;
            foreach (Color32 pixel in pixels)
            {
                if (IsGreen(pixel))
                {
                    green++;
                }
            }

            Assert.That(green, Is.Zero);
        }

        [Test]
        public void CommandsOutsideTheUploadedOnes_Throw()
        {
            Material material = Material(ShaderName, Color.green);
            using (var buffers = new VpGpuGeometryBuffers(3, 3))
            using (var batch = new VpIndirectDrawBatch(2, 2))
            {
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), CellBounds, 1) }, new[] { CellTopLeft }), Is.True);
                var properties = new MaterialPropertyBlock();

                Assert.DoesNotThrow(() => batch.Render(material, properties, buffers, 0, 1, 0), "zero commands after the last");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(material, properties, buffers, 0, -1, 1), "negative start");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(material, properties, buffers, 0, 0, 2), "beyond the uploaded commands");
                Assert.Throws<ArgumentOutOfRangeException>(() => batch.Render(material, properties, buffers, 0, 1, 1), "starting after the last");
            }
        }

        [Test]
        public void TheConstructorRejectsEmptyCapacities_AndAfterDisposeTheBatchThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndirectDrawBatch(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndirectDrawBatch(1, 0));

            Material material = Material(ShaderName, Color.green);
            using (var buffers = new VpGpuGeometryBuffers(3, 3))
            {
                var batch = new VpIndirectDrawBatch(1, 1);
                batch.Dispose();

                Assert.Throws<ObjectDisposedException>(() => batch.TryUpload(new VpIndirectCommand[0], new Matrix4x4[0]), "upload");
                Assert.Throws<ObjectDisposedException>(() => batch.Render(material, new MaterialPropertyBlock(), buffers, 0), "render");
                Assert.Throws<ObjectDisposedException>(() => batch.Render(material, new MaterialPropertyBlock(), buffers, 0, 0, 0), "render commands");
                Assert.Throws<ObjectDisposedException>(() => _ = batch.ArgumentBuffer, "argument buffer");
                Assert.DoesNotThrow(batch.Dispose, "dispose again");
            }
        }
    }
}
