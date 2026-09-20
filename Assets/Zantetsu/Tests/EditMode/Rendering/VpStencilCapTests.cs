using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The stencil and provisional cap draw of DESIGN 5.2 and 5.6, on fixed synthetic input: a closed box clipped by
    /// one plane, a known cap polygon over the opening, a fixed clip descriptor and a fixed assignment of stencil
    /// colours. Nothing here is connected to the ledger or to the logical display — this is the low-level confirmation
    /// Phase 1.52 asks for, and it is not Phase 1.50's or 1.51's XR and MSAA confirmation, which are not done.
    /// <para>
    /// What is checked is the image and the counters: which pixels came out, whether depth kept them, how many draws
    /// were issued on the CPU as against how many the GPU ran, and how many times a buffer was written. Nothing here
    /// trusts that a draw happened because a method was called.
    /// </para>
    /// <para>
    /// The camera looks along +z from -z. The cut plane is world z = 0 kept on the +z side, so the body that remains is
    /// the far half of the box and its opening faces the camera: through the opening the ray leaves by the box's far
    /// wall, a back face, which is the crossing the winding count is left with.
    /// </para>
    /// </summary>
    public class VpStencilCapTests
    {
        private const int Size = 64;

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

        // ----- the fixture ----------------------------------------------------------------------------------------

        // A closed box about the origin, 1 x 1 x 1. The faces are wound so that they are front facing seen from
        // outside, which is what the colour pass already assumes of this project's geometry.
        private static readonly Vector3[] BoxCorners =
        {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
            new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
        };

        // Each face as a quad, wound clockwise seen from outside the box, which is Unity's front face.
        private static readonly int[][] BoxFaces =
        {
            new[] { 0, 3, 2, 1 }, // -z, seen from -z
            new[] { 5, 6, 7, 4 }, // +z
            new[] { 4, 7, 3, 0 }, // -x
            new[] { 1, 2, 6, 5 }, // +x
            new[] { 0, 1, 5, 4 }, // -y
            new[] { 3, 7, 6, 2 }, // +y
        };

        /// <summary>The cut plane in world: z = 0, with the positive side the far half the camera looks into.</summary>
        private static readonly Vector4 PlaneZ0 = new Vector4(0f, 0f, 1f, 0f);

        /// <summary>
        /// The cap polygon over the opening: the box's cross-section at z = 0, wound so that it is front facing for a
        /// camera on the -z side, which is the outward direction of the side that is kept.
        /// </summary>
        private static Vector3[] CapPolygon(float z = 0f, float scale = 1f)
        {
            float h = 0.5f * scale;
            return new[]
            {
                new Vector3(-h, -h, z), new Vector3(-h, h, z), new Vector3(h, h, z), new Vector3(h, -h, z),
            };
        }

        private static int[] FanIndices(int start, int vertexCount)
        {
            var indices = new List<int>((vertexCount - 2) * 3);
            for (int i = 1; i + 1 < vertexCount; i++)
            {
                indices.Add(start);
                indices.Add(start + i);
                indices.Add(start + i + 1);
            }

            return indices.ToArray();
        }

        private static VpCpuGeometryPool NewPool()
        {
            return new VpCpuGeometryPool(256, 512, Allocator.Persistent);
        }

        /// <summary>
        /// A closed box as a mesh, which is what the pool takes. <paramref name="inverted"/> reverses every face's
        /// winding, which is the wholly-turned-round component of DESIGN 5.2: its sign is kept as it is and nothing
        /// straightens it.
        /// </summary>
        private Mesh BoxMesh(Vector3 centre, float size, bool inverted = false)
        {
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            foreach (int[] face in BoxFaces)
            {
                int b = positions.Count;
                Vector3 a = (BoxCorners[face[0]] * size) + centre;
                Vector3 c = (BoxCorners[face[1]] * size) + centre;
                Vector3 d = (BoxCorners[face[2]] * size) + centre;
                Vector3 e = (BoxCorners[face[3]] * size) + centre;
                Vector3 normal = Vector3.Cross(c - a, d - a).normalized;
                foreach (Vector3 corner in new[] { a, c, d, e })
                {
                    positions.Add(corner);
                    normals.Add(normal);
                    uvs.Add(new Vector2(0.5f, 0.5f));
                }

                if (inverted)
                {
                    triangles.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                }
                else
                {
                    triangles.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }
            }

            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        /// <summary>
        /// A closed box with a square hole running through it along z: four outer walls, four inner walls facing into
        /// the hole, and each end a frame of four strips around it. Cut across, the opening this leaves is a frame and
        /// not a square, which is what a cap polygon that knows only the bounds cannot tell by itself.
        /// <para>
        /// Every quad is wound so that <c>Cross(b - a, c - a)</c> is that face's outward direction, which is the
        /// project's ordinary rule and the one the front-face convention follows.
        /// </para>
        /// </summary>
        private Mesh FrameMesh(float outer, float inner)
        {
            float o = outer * 0.5f;
            float i = inner * 0.5f;
            var positions = new List<Vector3>();
            var triangles = new List<int>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int at = positions.Count;
                positions.Add(a);
                positions.Add(b);
                positions.Add(c);
                positions.Add(d);
                triangles.AddRange(new[] { at, at + 1, at + 2, at, at + 2, at + 3 });
            }

            // A strip of one end face, at z, wound for the outward direction that end has.
            void End(float z, bool towardsPlusZ, float x0, float y0, float x1, float y1)
            {
                var a = new Vector3(x0, y0, z);
                var b = new Vector3(x1, y0, z);
                var c = new Vector3(x1, y1, z);
                var d = new Vector3(x0, y1, z);
                if (towardsPlusZ)
                {
                    Quad(a, b, c, d);
                }
                else
                {
                    Quad(d, c, b, a);
                }
            }

            // The outer walls, outward.
            Quad(new Vector3(-o, -o, -o), new Vector3(-o, -o, o), new Vector3(-o, o, o), new Vector3(-o, o, -o));
            Quad(new Vector3(o, -o, -o), new Vector3(o, o, -o), new Vector3(o, o, o), new Vector3(o, -o, o));
            Quad(new Vector3(-o, -o, -o), new Vector3(o, -o, -o), new Vector3(o, -o, o), new Vector3(-o, -o, o));
            Quad(new Vector3(-o, o, -o), new Vector3(-o, o, o), new Vector3(o, o, o), new Vector3(o, o, -o));

            // The inner walls: outward for the material around the hole is into the hole.
            Quad(new Vector3(-i, -i, -o), new Vector3(-i, i, -o), new Vector3(-i, i, o), new Vector3(-i, -i, o));
            Quad(new Vector3(i, -i, -o), new Vector3(i, -i, o), new Vector3(i, i, o), new Vector3(i, i, -o));
            Quad(new Vector3(-i, -i, -o), new Vector3(-i, -i, o), new Vector3(i, -i, o), new Vector3(i, -i, -o));
            Quad(new Vector3(-i, i, -o), new Vector3(i, i, -o), new Vector3(i, i, o), new Vector3(-i, i, o));

            // The two ends, each four strips around the hole.
            foreach (bool towardsPlusZ in new[] { false, true })
            {
                float z = towardsPlusZ ? o : -o;
                End(z, towardsPlusZ, -o, -o, -i, o);
                End(z, towardsPlusZ, i, -o, o, o);
                End(z, towardsPlusZ, -i, -o, i, -i);
                End(z, towardsPlusZ, -i, i, i, o);
            }

            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int v = 0; v < positions.Count; v++)
            {
                normals.Add(Vector3.forward);
                uvs.Add(new Vector2(0.5f, 0.5f));
            }

            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        private VpGeometryRange AppendBox(VpCpuGeometryPool pool, Vector3 centre, float size, bool inverted = false)
        {
            Assert.That(
                pool.TryAppend(BoxMesh(centre, size, inverted), out VpGeometryRange range), Is.True, "append box");
            return range;
        }

        private static Bounds BoxBounds(Vector3 centre, float size)
        {
            return new Bounds(centre, Vector3.one * (size + 0.1f));
        }

        // ----- rendering ------------------------------------------------------------------------------------------

        private RenderTexture StencilTarget()
        {
            var target = Track(new RenderTexture(Size, Size, 0, GraphicsFormat.R8G8B8A8_UNorm)
            {
                depthStencilFormat = VpStencilAttachment.EightBitStencilFormat,
                antiAliasing = 1,
            });
            target.Create();
            return target;
        }

        private Camera TestCamera(RenderTexture target)
        {
            Camera camera = Track(new GameObject("VP Stencil Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            return camera;
        }

        private Color32[] RenderAndRead(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True, "render request");
            RenderPipeline.SubmitRenderRequest(camera, request);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D readback = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels32();
        }

        private static bool IsBackground(Color32 p) => p.r < 16 && p.g < 16 && p.b < 16;
        private static bool IsRedish(Color32 p) => p.r > 100 && p.g < 80 && p.b < 80;
        private static bool IsBlueish(Color32 p) => p.b > 100 && p.r < 80 && p.g < 80;
        private static bool IsGreenish(Color32 p) => p.g > p.r + 30 && p.g > p.b + 30;

        private static int Count(Color32[] pixels, Func<Color32, bool> test)
        {
            int count = 0;
            foreach (Color32 pixel in pixels)
            {
                if (test(pixel))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>The pixel at a point of the image, indexed from the bottom left as the readback gives it.</summary>
        private static Color32 At(Color32[] pixels, int x, int y) => pixels[(y * Size) + x];

        // ----- what the format check decides, and what it does not --------------------------------------------

        /// <summary>
        /// The format check answers one question: does this target's depth-stencil format carry a stencil byte. It is
        /// not a confirmation that the eight bits are this path's alone — that rests on the renderer configuration and
        /// on what else is drawn, neither of which it sees.
        /// </summary>
        [Test]
        public void TheFormatCheck_AnswersForTheFormatAlone()
        {
            RenderTexture withStencil = StencilTarget();
            Assert.That(
                VpStencilAttachment.TryConfirmFormat(withStencil, out string reason), Is.True,
                "a depth-stencil format is what this path needs");
            Assert.That(reason, Is.Null);

            var depthOnly = Track(new RenderTexture(Size, Size, 0, GraphicsFormat.R8G8B8A8_UNorm)
            {
                depthStencilFormat = GraphicsFormat.D32_SFloat,
            });
            depthOnly.Create();
            Assert.That(
                VpStencilAttachment.TryConfirmFormat(depthOnly, out string depthReason), Is.False,
                "a format with no stencil cannot carry this");
            Assert.That(depthReason, Does.Contain("stencil"));

            var none = Track(new RenderTexture(Size, Size, 0, GraphicsFormat.R8G8B8A8_UNorm));
            none.Create();
            Assert.That(VpStencilAttachment.TryConfirmFormat(none, out string noneReason), Is.False);
            Assert.That(noneReason, Does.Contain("depth-stencil"));

            Assert.That(VpStencilAttachment.TryConfirmFormat(null, out _), Is.False, "and nothing at all is not a target");
        }

        /// <summary>
        /// What this fixture's own render target is, recorded because the format check cannot say it: the target
        /// carries a stencil byte, the camera draws into that target alone and only when asked, no other enabled
        /// camera draws into it, and there is one sample so no resolve stands between the counting and the drawing.
        /// <para>
        /// **This is not a check that the stencil byte is exclusively this path's.** It says what this fixture's
        /// target and camera are, which is one of the two things that has to hold. The other — that nothing else in
        /// the frame writes a stencil — is not something a test can establish by looking at its own objects, and it is
        /// recorded instead from the code and the settings: no tracked shader of this project declares a
        /// <c>Stencil</c> block except the three of this path, and both renderer assets have
        /// <c>overrideStencilState: 0</c>, so neither forces a stencil state of its own. Those were read, not asserted
        /// here. Nothing in this lane monitors exclusivity at run time.
        /// </para>
        /// </summary>
        [Test]
        public void TheFixtureTarget_IsDrawnByThisCameraAlone()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Assert.That(
                VpStencilAttachment.TryConfirmFormat(target, out _), Is.True, "eight stencil bits in the format");
            Assert.That(camera.targetTexture, Is.SameAs(target), "the camera draws into that target and no other");
            Assert.That(camera.enabled, Is.False, "and only when this test asks it to");
            Assert.That(target.antiAliasing, Is.EqualTo(1), "one sample, so no resolve stands between the two");

            foreach (Camera other in Camera.allCameras)
            {
                Assert.That(
                    other.targetTexture, Is.Not.SameAs(target),
                    "no other enabled camera draws into this target");
            }
        }

        /// <summary>
        /// The three materials this path draws with are the three shaders this path owns, and no others are made here.
        /// This is what the stencil state of the frame comes from, given the settings noted above; it is not itself a
        /// proof that nothing else writes one.
        /// </summary>
        [Test]
        public void TheMaterials_AreTheThreeShadersOfThisPath()
        {
            Assert.That(VpStencilCapMaterials.TryCreate(1, out VpStencilCapMaterials materials), Is.True);
            using (materials)
            {
                Assert.That(materials.Init(0).shader.name, Is.EqualTo(VpStencilCapMaterials.InitShaderName));
                Assert.That(materials.Volume(0).shader.name, Is.EqualTo(VpStencilCapMaterials.VolumeShaderName));
                Assert.That(materials.Cap(0).shader.name, Is.EqualTo(VpStencilCapMaterials.CapShaderName));
                Assert.That(materials.ColorCount, Is.EqualTo(1), "and one colour is one set of three");
            }
        }

        // ----- the order the materials put the draws in -------------------------------------------------------------

        /// <summary>
        /// The queues put the three draws of a colour in the order DESIGN 5.6 requires, and the colours after one
        /// another, all of them inside the opaque range so that one pass and one attachment carry the whole sequence.
        /// </summary>
        [Test]
        public void TheQueues_OrderInitThenVolumesThenCaps_ColourAfterColour()
        {
            Assert.That(VpStencilCapMaterials.TryCreate(2, out VpStencilCapMaterials materials), Is.True, "create");
            using (materials)
            {
                Assert.That(materials.ColorCount, Is.EqualTo(2));
                int previous = int.MinValue;
                for (int c = 0; c < 2; c++)
                {
                    Material[] stages = { materials.Init(c), materials.Volume(c), materials.Cap(c) };
                    for (int s = 0; s < stages.Length; s++)
                    {
                        Assert.That(
                            stages[s].renderQueue, Is.GreaterThan(previous),
                            "colour " + c + " stage " + s + " is drawn after everything before it");
                        Assert.That(
                            stages[s].renderQueue, Is.EqualTo(materials.QueueOf(c, s)), "the queue it says it is");
                        Assert.That(
                            stages[s].renderQueue, Is.LessThanOrEqualTo(VpStencilCapMaterials.LastOpaqueQueue),
                            "and it is an opaque queue, so the whole sequence shares one attachment");
                        previous = stages[s].renderQueue;
                    }
                }
            }
        }

        /// <summary>
        /// The colours stop where the opaque range does. The largest count that fits ends exactly on the last opaque
        /// queue and is made; one more would run past it and is refused, having made nothing.
        /// </summary>
        [Test]
        public void TheColourCount_StopsAtTheLastOpaqueQueue()
        {
            int most = VpStencilCapMaterials.MaxColors;
            Assert.That(
                VpStencilCapMaterials.FirstQueue + (most * VpStencilCapMaterials.QueuesPerColor) - 1,
                Is.LessThanOrEqualTo(VpStencilCapMaterials.LastOpaqueQueue),
                "the last queue of the largest count is still opaque");
            Assert.That(
                VpStencilCapMaterials.FirstQueue + ((most + 1) * VpStencilCapMaterials.QueuesPerColor) - 1,
                Is.GreaterThan(VpStencilCapMaterials.LastOpaqueQueue),
                "and one more colour would not be");

            Assert.That(
                VpStencilCapMaterials.TryCreate(most, out VpStencilCapMaterials materials), Is.True,
                "the largest count that fits is made");
            using (materials)
            {
                Assert.That(
                    materials.QueueOf(most - 1, VpStencilCapMaterials.QueuesPerColor - 1),
                    Is.LessThanOrEqualTo(VpStencilCapMaterials.LastOpaqueQueue),
                    "and its last draw is the last opaque queue or below");
            }

            Assert.That(
                VpStencilCapMaterials.TryCreate(most + 1, out VpStencilCapMaterials over), Is.False,
                "one more is refused");
            Assert.That(over, Is.Null, "and nothing was made for it");

            Assert.That(VpStencilCapMaterials.TryCreate(0, out _), Is.False, "as is none at all");
            Assert.That(VpStencilCapMaterials.TryCreate(-1, out _), Is.False);
            Assert.That(VpStencilCapMaterials.TryCreate(int.MaxValue, out _), Is.False, "and a count that would carry");
        }

        // ----- the picture ------------------------------------------------------------------------------------------

        private sealed class Fixture : IDisposable
        {
            public VpCpuGeometryPool pool;
            public VpGpuIndexedGeometryBuffers buffers;
            public VpStencilCapBatch batch;
            public VpStencilCapMaterials materials;

            public void Dispose()
            {
                batch?.Dispose();
                materials?.Dispose();
                buffers?.Dispose();
                pool?.Dispose();
            }
        }

        private Fixture NewFixture(int colorCount = 1)
        {
            var fixture = new Fixture
            {
                pool = NewPool(),
                buffers = new VpGpuIndexedGeometryBuffers(4096, 8192),
                batch = new VpStencilCapBatch(colorCount, 8, 8, 64, 128),
            };

            Assert.That(VpStencilCapMaterials.TryCreate(colorCount, out VpStencilCapMaterials materials), Is.True, "materials");
            fixture.materials = materials;
            return fixture;
        }

        /// <summary>
        /// The whole path on one colour: a clipped box leaves an opening, the volume count marks it, and the cap fills
        /// it. What proves the stencil did the work is the second image — the same cap polygon drawn with no volume
        /// counted has nothing above the base value to draw on, and none of it appears.
        /// </summary>
        [Test]
        public void TheCap_FillsTheOpeningOfAClippedBox_AndNothingWithoutTheVolume()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] withVolume;
            Color32[] withoutVolume;
            using (Fixture fixture = NewFixture())
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True, "geometry to the GPU");

                var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                var transforms = new[] { Matrix4x4.identity };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                Vector3[] capVertices = CapPolygon();
                int[] capIndices = FanIndices(0, capVertices.Length);
                var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        colors, 1),
                    Is.True,
                    "upload");

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                withVolume = RenderAndRead(camera, target);

                // The same cap, with the colour holding no volume at all: nothing marked the stencil, so nothing draws.
                var empty = new[] { new VpStencilCapColor(0, 0, 0, capIndices.Length, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        empty, 1),
                    Is.True);
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                withoutVolume = RenderAndRead(camera, target);
            }

            int capped = Count(withVolume, IsRedish);
            Assert.That(capped, Is.GreaterThan(400), "the opening is filled");
            Assert.That(At(withVolume, Size / 2, Size / 2), Is.Not.Null);
            Assert.That(IsRedish(At(withVolume, Size / 2, Size / 2)), Is.True, "the middle of the opening is capped");
            Assert.That(IsBackground(At(withVolume, 1, 1)), Is.True, "and the corner of the image is not");

            Assert.That(Count(withoutVolume, IsRedish), Is.Zero, "with nothing counted, the cap draws nowhere");
        }

        /// <summary>
        /// Uploaded for Single Pass Instanced but drawn by a monoscopic camera, the batch has to give the same image
        /// as the plain upload: the initialisation, the volumes and the caps are each issued as two instances, and
        /// the shaders' non-stereo variants reject the second one, so nothing is initialised, counted or capped
        /// twice. This is the non-XR half of the both-eyes change; what the stereo variants do is only seen in XR.
        /// </summary>
        [Test]
        public void TheCap_UploadedForSinglePassInstanced_DrawsTheSameToAMonoscopicCamera()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] plain;
            Color32[] instanced;
            using (Fixture fixture = NewFixture())
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True, "geometry to the GPU");

                var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                var transforms = new[] { Matrix4x4.identity };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                Vector3[] capVertices = CapPolygon();
                int[] capIndices = FanIndices(0, capVertices.Length);
                var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        colors, 1),
                    Is.True);
                Assert.That(fixture.batch.SinglePassInstanced, Is.False, "the plain overload asks for no doubling");
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                plain = RenderAndRead(camera, target);

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        colors, 1, true),
                    Is.True);
                Assert.That(fixture.batch.SinglePassInstanced, Is.True, "the upload settles the flag");
                int initsBefore = fixture.batch.StencilInitIssues;
                int volumesBefore = fixture.batch.VolumeIssues;
                int capsBefore = fixture.batch.CapIssues;
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                instanced = RenderAndRead(camera, target);

                Assert.That(fixture.batch.StencilInitIssues - initsBefore, Is.EqualTo(1), "still one init issue");
                Assert.That(fixture.batch.VolumeIssues - volumesBefore, Is.EqualTo(1), "still one volume issue");
                Assert.That(fixture.batch.CapIssues - capsBefore, Is.EqualTo(1), "still one cap issue");
            }

            int plainCapped = Count(plain, IsRedish);
            Assert.That(plainCapped, Is.GreaterThan(400), "the plain upload caps the opening");
            Assert.That(Count(instanced, IsRedish), Is.EqualTo(plainCapped), "so does the instanced one, no more and no less");
            for (int i = 0; i < plain.Length; i++)
            {
                if (plain[i].r != instanced[i].r || plain[i].g != instanced[i].g || plain[i].b != instanced[i].b)
                {
                    Assert.Fail("pixel " + i + " differs between the plain and the instanced upload");
                }
            }
        }

        /// <summary>
        /// The polygon is the cross-section of the body's bounds box and is larger than the body, so what keeps it
        /// inside the body is the stencil and nothing else. A cap polygon twice the width of the box is given, and only
        /// the box's own opening comes out filled.
        /// </summary>
        [Test]
        public void ACapLargerThanTheBody_IsKeptInsideItByTheStencil()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] pixels;
            using (Fixture fixture = NewFixture())
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                var transforms = new[] { Matrix4x4.identity };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };

                // Twice as wide as the box: without the stencil this would cover four times the area.
                Vector3[] capVertices = CapPolygon(0f, 2f);
                int[] capIndices = FanIndices(0, capVertices.Length);
                var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        colors, 1),
                    Is.True);

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                pixels = RenderAndRead(camera, target);
            }

            // The box is 1 unit across in a view 2 units across, so its opening is the middle quarter of the image.
            Assert.That(IsRedish(At(pixels, Size / 2, Size / 2)), Is.True, "the opening is filled");

            int quarter = Size / 4;
            Assert.That(
                IsBackground(At(pixels, quarter - 4, Size / 2)), Is.True,
                "and the polygon outside the body writes no colour");
            Assert.That(IsBackground(At(pixels, Size - quarter + 4, Size / 2)), Is.True);
            Assert.That(IsBackground(At(pixels, Size / 2, quarter - 4)), Is.True);
            Assert.That(IsBackground(At(pixels, Size / 2, Size - quarter + 4)), Is.True);

            int capped = Count(pixels, IsRedish);
            Assert.That(
                capped, Is.LessThan(Size * Size / 3),
                "the drawn area is the body's opening, not the polygon's own");
        }

        /// <summary>
        /// The counts themselves. A volume as it stands leaves +1 and a wholly turned-round one leaves -1, so a chosen
        /// number of each puts a chosen count in the byte: 127, 128, 129 and 130 for -1, 0, +1 and +2 against the base
        /// of 128. Each of the four is told from the others by what one more contribution does to it — a count that
        /// draws and stops drawing when one turned-round volume is added was +1 and not +2 — and only the ones above
        /// the base draw at all, which is `S &gt; 128` and nothing else.
        /// </summary>
        [Test]
        public void TheCountsMinusOneZeroPlusOneAndPlusTwo_DrawOnlyWhereTheyArePositive()
        {
            (int upright, int turned, int winding, bool expectCap, string what)[] cases =
            {
                (0, 0, 0, false, "nothing counted: 128"),
                (0, 1, -1, false, "one turned round: 127"),
                (1, 0, 1, true, "one as it stands: 129"),
                (2, 0, 2, true, "two as they stand: 130"),
                (1, 1, 0, false, "one of each cancels to 128"),
                (2, 1, 1, true, "two and one turned round is 129, so it still draws"),
                (1, 2, -1, false, "one and two turned round is 127, so it does not"),
            };

            foreach ((int upright, int turned, int winding, bool expectCap, string what) in cases)
            {
                RenderTexture target = StencilTarget();
                Camera camera = TestCamera(target);
                Color32[] pixels;

                using (Fixture fixture = NewFixture())
                {
                    var commands = new List<VpIndirectCommand>();
                    var transforms = new List<Matrix4x4>();
                    var clips = new List<VpInstanceClip>();
                    for (int b = 0; b < upright + turned; b++)
                    {
                        VpGeometryRange range = AppendBox(fixture.pool, Vector3.zero, 1f, b >= upright);
                        commands.Add(new VpIndirectCommand(range, BoxBounds(Vector3.zero, 1f), 1));
                        transforms.Add(Matrix4x4.identity);
                        clips.Add(VpInstanceClip.Keep(PlaneZ0, 1f));
                    }

                    Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                    Vector3[] capVertices = CapPolygon();
                    int[] capIndices = FanIndices(0, capVertices.Length);
                    var colors = new[] { new VpStencilCapColor(0, commands.Count, 0, capIndices.Length, Color.red) };
                    Assert.That(
                        fixture.batch.TryUpload(
                            commands.ToArray(), transforms.ToArray(), clips.ToArray(), capVertices, capVertices.Length,
                            capIndices, capIndices.Length, colors, 1),
                        Is.True,
                        what);

                    fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                    pixels = RenderAndRead(camera, target);
                }

                Assert.That(
                    winding > 0, Is.EqualTo(expectCap),
                    what + ": the case says what the arithmetic says");

                if (expectCap)
                {
                    Assert.That(IsRedish(At(pixels, Size / 2, Size / 2)), Is.True, what + ": the cap is drawn");
                    Assert.That(Count(pixels, IsRedish), Is.GreaterThan(400), what);
                }
                else
                {
                    Assert.That(
                        Count(pixels, IsRedish), Is.Zero,
                        what + ": not above the base, so nothing is drawn");
                }

                DestroyObjects();
            }
        }

        /// <summary>
        /// A mirroring transform turns the rasterizer's idea of which face is front, and nothing here puts it back:
        /// the same body under a placement whose determinant is negative counts the other way and its cap is not
        /// drawn, which DESIGN 5.2 allows as a quality exception of a wholly turned-round component. What would be
        /// wrong is straightening the sign, and this is where that would show.
        /// </summary>
        [Test]
        public void AMirroringTransform_KeepsTheSignItProduces()
        {
            (Matrix4x4 placement, bool expectCap, string what)[] cases =
            {
                (Matrix4x4.identity, true, "as it stands"),
                (Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)), false, "mirrored in x"),
            };

            foreach ((Matrix4x4 placement, bool expectCap, string what) in cases)
            {
                Assert.That(
                    placement.determinant < 0f, Is.Not.EqualTo(expectCap),
                    what + ": the mirroring case really does mirror");

                RenderTexture target = StencilTarget();
                Camera camera = TestCamera(target);
                Color32[] pixels;

                using (Fixture fixture = NewFixture())
                {
                    VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                    Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                    var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                    var transforms = new[] { placement };
                    var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                    Vector3[] capVertices = CapPolygon();
                    int[] capIndices = FanIndices(0, capVertices.Length);
                    var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };
                    Assert.That(
                        fixture.batch.TryUpload(
                            commands, transforms, clips, capVertices, capVertices.Length, capIndices,
                            capIndices.Length, colors, 1),
                        Is.True);

                    fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                    pixels = RenderAndRead(camera, target);
                }

                if (expectCap)
                {
                    Assert.That(Count(pixels, IsRedish), Is.GreaterThan(400), what + ": the cap is drawn");
                }
                else
                {
                    Assert.That(
                        Count(pixels, IsRedish), Is.Zero,
                        what + ": the sign came out negative and was left that way");
                }

                DestroyObjects();
            }
        }

        /// <summary>
        /// A body with a hole through it: the opening the cut leaves is a frame, and the cap polygon — which is the
        /// whole square and knows nothing of the hole — is drawn on the frame and not in the hole. The polygon's own
        /// bounds are not what decides; the count is.
        /// </summary>
        [Test]
        public void ABodyWithAHole_IsCappedOnlyWhereItHasMaterial()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] pixels;
            using (Fixture fixture = NewFixture())
            {
                Assert.That(
                    fixture.pool.TryAppend(FrameMesh(1f, 0.4f), out VpGeometryRange frame), Is.True, "append the frame");
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[] { new VpIndirectCommand(frame, BoxBounds(Vector3.zero, 1f), 1) };
                var transforms = new[] { Matrix4x4.identity };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                Vector3[] capVertices = CapPolygon();
                int[] capIndices = FanIndices(0, capVertices.Length);
                var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        colors, 1),
                    Is.True);

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                pixels = RenderAndRead(camera, target);
            }

            // The view is 2 units across: the body is the middle half and its hole the middle fifth.
            Assert.That(
                IsBackground(At(pixels, Size / 2, Size / 2)), Is.True,
                "the hole has no material, so nothing is capped there and nothing is written");

            int frameX = (Size / 2) + (int)(0.35f / 2f * Size);
            Assert.That(IsRedish(At(pixels, frameX, Size / 2)), Is.True, "the frame around it is capped");
            Assert.That(IsRedish(At(pixels, Size - frameX, Size / 2)), Is.True);
            Assert.That(IsRedish(At(pixels, Size / 2, frameX)), Is.True);

            Assert.That(
                IsBackground(At(pixels, 2, Size / 2)), Is.True, "and outside the body nothing is written either");
        }

        /// <summary>
        /// A sample the stencil rejects writes no depth either, not only no colour. A first colour is given a cap
        /// polygon far wider than its body; a second colour's cap lies further from the camera, inside the part of the
        /// first polygon that had no body under it. Had the rejected samples written depth, the further cap would be
        /// behind it and would not appear. It appears.
        /// </summary>
        [Test]
        public void ARejectedSample_WritesNeitherColourNorDepth()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] pixels;
            using (Fixture fixture = NewFixture(2))
            {
                // A small body in the middle, and a second one further away and off to the side.
                var near = new Vector3(0f, 0f, 0f);
                var far = new Vector3(0.6f, 0f, 1f);
                VpGeometryRange nearBox = AppendBox(fixture.pool, near, 0.5f);
                VpGeometryRange farBox = AppendBox(fixture.pool, far, 0.5f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[]
                {
                    new VpIndirectCommand(nearBox, BoxBounds(near, 0.5f), 1),
                    new VpIndirectCommand(farBox, BoxBounds(far, 0.5f), 1),
                };
                var transforms = new[] { Matrix4x4.identity, Matrix4x4.identity };
                var clips = new[]
                {
                    VpInstanceClip.Keep(PlaneZ0, 1f),
                    VpInstanceClip.Keep(new Vector4(0f, 0f, 1f, -1f), 1f),
                };

                // The first colour's polygon is wide enough to cover where the second colour's cap will be.
                var capVertices = new List<Vector3>();
                var capIndices = new List<int>();
                int start = capVertices.Count;
                capVertices.AddRange(CapPolygon(0f, 3f));
                capIndices.AddRange(FanIndices(start, 4));

                start = capVertices.Count;
                foreach (Vector3 corner in CapPolygon(1f, 0.5f))
                {
                    capVertices.Add(corner + new Vector3(0.6f, 0f, 0f));
                }

                capIndices.AddRange(FanIndices(start, 4));

                var colors = new[]
                {
                    new VpStencilCapColor(0, 1, 0, 6, Color.red),
                    new VpStencilCapColor(1, 1, 6, 6, Color.blue),
                };

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices.ToArray(), capVertices.Count, capIndices.ToArray(),
                        capIndices.Count, colors, 2),
                    Is.True);

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                pixels = RenderAndRead(camera, target);
            }

            Assert.That(IsRedish(At(pixels, Size / 2, Size / 2)), Is.True, "the first colour capped its own body");

            int farX = (Size / 2) + (int)(0.6f / 2f * Size);
            Assert.That(
                IsBlueish(At(pixels, farX, Size / 2)), Is.True,
                "and the further cap is there, so the rejected samples of the wide polygon wrote no depth");
        }

        /// <summary>
        /// The volume and the cap agree on where the side is. The side stands 0.6 to the right, which is its own
        /// placement and nothing added for the display, and the cap polygon is given in world space at that same
        /// place: the cap lands on the opening the volume marked. A cap left at the origin instead falls outside that
        /// region and mostly disappears, which is what makes this a check and not a repetition.
        /// </summary>
        [Test]
        public void TheSideAndItsPlacement_AgreeBetweenTheVolumeAndTheCap()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);
            var where = new Vector3(0.6f, 0f, 0f);

            Color32[] together;
            Color32[] apart;
            using (Fixture fixture = NewFixture())
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };

                // Where the side stands: its own placement, which is what the volume is drawn with. The cut plane is
                // z = 0, which this move along x leaves where it is, so the same half is kept.
                var transforms = new[] { Matrix4x4.Translate(where) };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                int[] capIndices = FanIndices(0, 4);
                var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };

                // The cap polygon is in world space, so it is given at the same place.
                Vector3[] moved = CapPolygon();
                for (int i = 0; i < moved.Length; i++)
                {
                    moved[i] += where;
                }

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, moved, moved.Length, capIndices, capIndices.Length, colors, 1),
                    Is.True);
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                together = RenderAndRead(camera, target);

                // The same volume, with the cap left behind at the origin.
                Vector3[] left = CapPolygon();
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, left, left.Length, capIndices, capIndices.Length, colors, 1),
                    Is.True);
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                apart = RenderAndRead(camera, target);
            }

            Assert.That(Count(together, IsRedish), Is.GreaterThan(400), "at the one place, the cap still fills it");

            // The view is 2 units across, so standing 0.6 to the right is a fifth of the image past the middle.
            int movedX = (Size / 2) + (int)(0.6f / 2f * Size);
            Assert.That(IsRedish(At(together, movedX, Size / 2)), Is.True, "and it is where the side stands");
            Assert.That(IsBackground(At(together, Size / 2 - 12, Size / 2)), Is.True, "not at the origin");

            Assert.That(
                Count(apart, IsRedish), Is.LessThan(Count(together, IsRedish) / 2),
                "a cap left at the origin is mostly outside what the volume marked");
        }

        /// <summary>
        /// Two colours: each starts from the base value again, so the first colour's count cannot draw the second
        /// colour's cap, and what the first colour drew keeps its colour and its depth through the second.
        /// </summary>
        [Test]
        public void TwoColours_StartFromTheBaseAgain_AndKeepWhatWasDrawnBefore()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] pixels;
            int stencilInits;
            int volumeIssues;
            int capIssues;
            int gpuDraws;
            int transfers;

            using (Fixture fixture = NewFixture(2))
            {
                // Two boxes side by side, one per colour.
                var left = new Vector3(-0.5f, 0f, 0f);
                var right = new Vector3(0.5f, 0f, 0f);
                VpGeometryRange leftBox = AppendBox(fixture.pool, left, 0.8f);
                VpGeometryRange rightBox = AppendBox(fixture.pool, right, 0.8f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[]
                {
                    new VpIndirectCommand(leftBox, BoxBounds(left, 0.8f), 1),
                    new VpIndirectCommand(rightBox, BoxBounds(right, 0.8f), 1),
                };
                var transforms = new[] { Matrix4x4.identity, Matrix4x4.identity };
                var clips = new[]
                {
                    VpInstanceClip.Keep(PlaneZ0, 1f),
                    VpInstanceClip.Keep(PlaneZ0, 1f),
                };

                var capVertices = new List<Vector3>();
                var capIndices = new List<int>();
                foreach (Vector3 centre in new[] { left, right })
                {
                    int start = capVertices.Count;
                    foreach (Vector3 corner in CapPolygon(0f, 0.8f))
                    {
                        capVertices.Add(corner + centre);
                    }

                    capIndices.AddRange(FanIndices(start, 4));
                }

                var colors = new[]
                {
                    new VpStencilCapColor(0, 1, 0, 6, Color.red),
                    new VpStencilCapColor(1, 1, 6, 6, Color.blue),
                };

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices.ToArray(), capVertices.Count, capIndices.ToArray(),
                        capIndices.Count, colors, 2),
                    Is.True,
                    "one transfer per kind, for both colours");
                transfers = fixture.batch.BufferWrites;

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                stencilInits = fixture.batch.StencilInitIssues;
                volumeIssues = fixture.batch.VolumeIssues;
                capIssues = fixture.batch.CapIssues;
                gpuDraws = fixture.batch.VolumeGpuDraws;
                pixels = RenderAndRead(camera, target);
            }

            Assert.That(
                transfers, Is.EqualTo(6),
                "the volume side wrote its two argument buffers, its transforms and its clips, and the caps their "
                + "vertices and their indices");
            Assert.That(stencilInits, Is.EqualTo(2), "one initialisation per colour");
            Assert.That(volumeIssues, Is.EqualTo(2), "one volume draw per colour");
            Assert.That(capIssues, Is.EqualTo(2), "one cap draw per colour");
            Assert.That(gpuDraws, Is.EqualTo(2), "which the GPU ran as one draw per command");

            int leftX = Size / 4;
            int rightX = Size - (Size / 4);
            Assert.That(IsRedish(At(pixels, leftX, Size / 2)), Is.True, "the first colour's cap");
            Assert.That(IsBlueish(At(pixels, rightX, Size / 2)), Is.True, "and the second colour's, in its own colour");
            Assert.That(
                Count(pixels, IsRedish), Is.GreaterThan(100),
                "the first colour survived the second colour's re-initialisation");
            Assert.That(Count(pixels, IsBlueish), Is.GreaterThan(100));

            // Between the two bodies there is no body, so neither colour's count marked it.
            Assert.That(IsBackground(At(pixels, Size / 2, 2)), Is.True, "and nothing was drawn where no body is");
        }

        /// <summary>
        /// Several volumes and several caps in one colour are still one CPU draw each, the ranges of the two colours do
        /// not run into one another, and the GPU draw count is the command count rather than the call count.
        /// </summary>
        [Test]
        public void SeveralVolumesAndCapsInOneColour_AreStillOneIssueEach()
        {
            using (Fixture fixture = NewFixture(2))
            {
                var centres = new[]
                {
                    new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f),
                    new Vector3(-0.5f, 0.6f, 0f), new Vector3(0.5f, 0.6f, 0f),
                };

                var commands = new List<VpIndirectCommand>();
                var transforms = new List<Matrix4x4>();
                var clips = new List<VpInstanceClip>();
                var capVertices = new List<Vector3>();
                var capIndices = new List<int>();
                foreach (Vector3 centre in centres)
                {
                    VpGeometryRange range = AppendBox(fixture.pool, centre, 0.4f);
                    commands.Add(new VpIndirectCommand(range, BoxBounds(centre, 0.4f), 1));
                    transforms.Add(Matrix4x4.identity);
                    clips.Add(VpInstanceClip.Keep(PlaneZ0, 1f));

                    int start = capVertices.Count;
                    foreach (Vector3 corner in CapPolygon(0f, 0.4f))
                    {
                        capVertices.Add(corner + centre);
                    }

                    capIndices.AddRange(FanIndices(start, 4));
                }

                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                // Two volumes and two caps in each of the two colours.
                var colors = new[]
                {
                    new VpStencilCapColor(0, 2, 0, 12, Color.red),
                    new VpStencilCapColor(2, 2, 12, 12, Color.blue),
                };

                Assert.That(
                    fixture.batch.TryUpload(
                        commands.ToArray(), transforms.ToArray(), clips.ToArray(), capVertices.ToArray(),
                        capVertices.Count, capIndices.ToArray(), capIndices.Count, colors, 2),
                    Is.True);

                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(6), "still six writes for everything");

                RenderTexture target = StencilTarget();
                Camera camera = TestCamera(target);
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);

                Assert.That(fixture.batch.VolumeIssues, Is.EqualTo(2), "one CPU draw per colour, not per volume");
                Assert.That(fixture.batch.CapIssues, Is.EqualTo(2), "one CPU draw per colour, not per cap");
                Assert.That(fixture.batch.StencilInitIssues, Is.EqualTo(2));
                Assert.That(
                    fixture.batch.VolumeGpuDraws, Is.EqualTo(4),
                    "the GPU ran one draw per command, which is not the CPU count");

                // The ranges are the ones each colour was given, and they do not overlap.
                Assert.That(fixture.batch.TryGetColor(0, out VpStencilCapColor first), Is.True);
                Assert.That(fixture.batch.TryGetColor(1, out VpStencilCapColor second), Is.True);
                Assert.That(first.volumeStart + first.volumeCount, Is.EqualTo(second.volumeStart));
                Assert.That(first.capIndexStart + first.capIndexCount, Is.EqualTo(second.capIndexStart));
                Assert.That(fixture.batch.TryGetColor(2, out _), Is.False, "and there is no third");

                Color32[] pixels = RenderAndRead(camera, target);
                Assert.That(Count(pixels, IsRedish), Is.GreaterThan(50), "the first colour's caps");
                Assert.That(Count(pixels, IsBlueish), Is.GreaterThan(50), "and the second's");
            }
        }

        /// <summary>
        /// Drawing writes to no buffer. Every write this path makes happens in the upload, before any draw is
        /// registered, and the shared geometry's own buffers are not written by any of it — the volumes reference them.
        /// The count is of writes actually made, which is not the number of calls that made them: an upload with
        /// volumes and caps writes six buffers, one with no caps writes four, and one with neither writes none.
        /// </summary>
        [Test]
        public void Drawing_WritesToNoBuffer_AndLeavesTheSharedGeometryAlone()
        {
            using (Fixture fixture = NewFixture(2))
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                var transforms = new[] { Matrix4x4.identity };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                Vector3[] capVertices = CapPolygon();
                int[] capIndices = FanIndices(0, 4);
                var colors = new[] { new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red) };

                Assert.That(fixture.batch.BufferWrites, Is.Zero, "nothing yet");
                Assert.That(fixture.batch.Uploads, Is.Zero);

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        colors, 1),
                    Is.True);
                Assert.That(fixture.batch.Uploads, Is.EqualTo(1), "one call");
                Assert.That(
                    fixture.batch.BufferWrites, Is.EqualTo(6),
                    "which wrote the two argument buffers, the transforms, the clips, the cap vertices and the cap "
                    + "indices");

                RenderTexture target = StencilTarget();
                Camera camera = TestCamera(target);
                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(6), "registering the draws wrote nothing");

                RenderAndRead(camera, target);
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(6), "and neither did drawing them");

                // An upload with volumes and no caps writes only the volume side's four.
                var noCaps = new[] { new VpStencilCapColor(0, 1, 0, 0, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, Array.Empty<Vector3>(), 0, Array.Empty<int>(), 0, noCaps, 1),
                    Is.True);
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(10), "four more, and no cap write for no caps");

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                RenderAndRead(camera, target);
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(10), "and drawing two colours wrote nothing");

                // And an upload with nothing at all writes nothing at all.
                Assert.That(
                    fixture.batch.TryUpload(
                        Array.Empty<VpIndirectCommand>(), Array.Empty<Matrix4x4>(), Array.Empty<VpInstanceClip>(),
                        Array.Empty<Vector3>(), 0, Array.Empty<int>(), 0, new[] { new VpStencilCapColor(0, 0, 0, 0, Color.red) }, 1),
                    Is.True);
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(10), "empty input wrote no buffer");
                Assert.That(fixture.batch.Uploads, Is.EqualTo(3), "though it was a call like the others");

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                RenderAndRead(camera, target);
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(10), "and drawing it wrote nothing either");

                // The shared geometry is what the volumes read: this path made no copy of it and wrote nothing to it.
                var vertices = new VpRenderVertex[4];
                fixture.buffers.VertexBuffer.GetData(vertices, 0, 0, 4);
                Assert.That(
                    (Vector3)vertices[0].position, Is.EqualTo(BoxCorners[0]).Using(Vector3EqualityComparer()),
                    "the shared vertices are the ones the pool uploaded");
            }
        }

        private static IEqualityComparer<Vector3> Vector3EqualityComparer()
        {
            return new VectorComparer();
        }

        private sealed class VectorComparer : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => (a - b).magnitude < 1e-4f;

            public int GetHashCode(Vector3 value) => value.GetHashCode();
        }

        /// <summary>
        /// The upload refuses what it cannot hold or what does not fit the ranges it was given, and refuses it whole:
        /// no colour is half uploaded and no range is quietly trimmed.
        /// </summary>
        [Test]
        public void TheUpload_RefusesRangesThatDoNotFit()
        {
            using (Fixture fixture = NewFixture())
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                var transforms = new[] { Matrix4x4.identity };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                Vector3[] capVertices = CapPolygon();
                int[] capIndices = FanIndices(0, 4);

                var pastCommands = new[] { new VpStencilCapColor(0, 2, 0, 6, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        pastCommands, 1),
                    Is.False,
                    "a colour cannot claim a command that was not uploaded");

                var pastIndices = new[] { new VpStencilCapColor(0, 1, 3, 6, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        pastIndices, 1),
                    Is.False,
                    "nor an index beyond the ones uploaded");

                var notTriangles = new[] { new VpStencilCapColor(0, 1, 0, 4, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        notTriangles, 1),
                    Is.False,
                    "and a cap range that is not whole triangles is not a cap range");

                // A count larger than the array it names.
                var one = new[] { new VpStencilCapColor(0, 1, 0, 6, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        one, 2),
                    Is.False,
                    "a colour count cannot run past the colours it was given");

                // Counts larger than the arrays they name.
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length + 1, capIndices,
                        capIndices.Length, one, 1),
                    Is.False,
                    "nor a vertex count past the vertices");
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices,
                        capIndices.Length + 1, one, 1),
                    Is.False,
                    "nor an index count past the indices");

                // Ranges whose start and count would carry past what an int holds if they were added.
                var carrying = new[] { new VpStencilCapColor(int.MaxValue, 3, 0, 6, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        carrying, 1),
                    Is.False,
                    "a volume range that would carry is not inside the commands");

                var carryingCaps = new[] { new VpStencilCapColor(0, 1, int.MaxValue - 2, 3, Color.red) };
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        carryingCaps, 1),
                    Is.False,
                    "and neither is a cap range that would");

                Assert.That(
                    fixture.batch.TryUpload(
                        commands, transforms, clips, capVertices, capVertices.Length, capIndices, capIndices.Length,
                        null, 1),
                    Is.False,
                    "and there must be colours at all");

                Assert.That(fixture.batch.ColorCount, Is.Zero, "nothing was settled by any of them");
                Assert.That(fixture.batch.BufferWrites, Is.Zero, "and no buffer was written");
                Assert.That(fixture.batch.Uploads, Is.Zero, "and none of them counted as an upload");
            }
        }

        /// <summary>
        /// The counted upload reads, checks, sends and draws only what it counts. Every array is longer than its count,
        /// and each tail holds what would be refused -- a command with a negative instance count, a transform that is
        /// not finite, cap indices far past the vertices, a colour with ranges nowhere -- yet counted to the one valid
        /// entry the upload is accepted and the opening is capped as by a plain upload. Counted into the tail, or given a
        /// count outside its array, it is refused, and the non-writing judgement says the same thing every time. The
        /// whole-array upload, handed the same arrays, still reads them whole. The volume batch underneath reports only
        /// what was counted.
        /// </summary>
        [Test]
        public void TheCountedUpload_ReadsOnlyWhatItCounts_AndIsJudgedTheSameWay()
        {
            RenderTexture target = StencilTarget();
            Camera camera = TestCamera(target);

            Color32[] image;
            using (Fixture fixture = NewFixture())
            {
                VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True, "geometry to the GPU");

                Matrix4x4 nan = NanMatrix();
                var commands = new[]
                {
                    new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1),
                    new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), -1),
                };
                var transforms = new[] { Matrix4x4.identity, nan, nan };
                var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f), VpInstanceClip.None, VpInstanceClip.None };
                Vector3[] polygon = CapPolygon();
                var capVertices = new Vector3[polygon.Length + 3];
                Array.Copy(polygon, capVertices, polygon.Length);
                int[] fan = FanIndices(0, polygon.Length);
                var capIndices = new int[fan.Length + 3];
                Array.Copy(fan, capIndices, fan.Length);
                capIndices[fan.Length] = capIndices[fan.Length + 1] = capIndices[fan.Length + 2] = 999;
                var colors = new[]
                {
                    new VpStencilCapColor(0, 1, 0, fan.Length, Color.red),
                    new VpStencilCapColor(5, 7, 100, 3, Color.blue),
                };

                var refused = new (int commands, int vertices, int indices, int colours, string what)[]
                {
                    (2, polygon.Length, fan.Length, 1, "a command counted into the tail"),
                    (3, polygon.Length, fan.Length, 1, "a command count past the array"),
                    (-1, polygon.Length, fan.Length, 1, "a negative command count"),
                    (1, polygon.Length, fan.Length + 3, 1, "cap indices counted into the tail"),
                    (1, polygon.Length, fan.Length, 2, "a colour counted into the tail"),
                };
                foreach ((int commandCount, int vertexCount, int indexCount, int colourCount, string what) in refused)
                {
                    Assert.That(
                        fixture.batch.CanUpload(
                            commands, commandCount, transforms, clips, capVertices, vertexCount, capIndices, indexCount,
                            colors, colourCount),
                        Is.False, "asked: " + what);
                    Assert.That(
                        fixture.batch.TryUpload(
                            commands, commandCount, transforms, clips, capVertices, vertexCount, capIndices, indexCount,
                            colors, colourCount, false),
                        Is.False, "uploaded: " + what);
                }

                var reachingPast = new[] { new VpStencilCapColor(0, 2, 0, fan.Length, Color.red) };
                Assert.That(
                    fixture.batch.CanUpload(
                        commands, 1, transforms, clips, capVertices, polygon.Length, capIndices, fan.Length, reachingPast, 1),
                    Is.False, "asked: a colour's volumes reaching past the count");
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, 1, transforms, clips, capVertices, polygon.Length, capIndices, fan.Length, reachingPast,
                        1, false),
                    Is.False, "uploaded: a colour's volumes reaching past the count");

                Assert.That(
                    fixture.batch.CanUpload(
                        commands, transforms, clips, capVertices, polygon.Length, capIndices, fan.Length, colors, 1),
                    Is.False, "the whole-array judgement still reads every command");
                Assert.That(fixture.batch.Uploads, Is.Zero, "nothing refused was uploaded");
                Assert.That(fixture.batch.BufferWrites, Is.Zero, "or written");

                Assert.That(
                    fixture.batch.CanUpload(
                        commands, 1, transforms, clips, capVertices, polygon.Length, capIndices, fan.Length, colors, 1),
                    Is.True, "asked: counted to the valid entries");
                Assert.That(
                    fixture.batch.TryUpload(
                        commands, 1, transforms, clips, capVertices, polygon.Length, capIndices, fan.Length, colors, 1,
                        false),
                    Is.True, "uploaded: counted to the valid entries");
                Assert.That(fixture.batch.ColorCount, Is.EqualTo(1), "one colour settled");
                Assert.That(fixture.batch.TryGetColor(1, out _), Is.False, "and not the one in the tail");
                Assert.That(fixture.batch.BufferWrites, Is.EqualTo(6), "the ordinary six writes");

                fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                image = RenderAndRead(camera, target);
            }

            Assert.That(Count(image, IsRedish), Is.GreaterThan(400), "the opening is filled");
            Assert.That(IsRedish(At(image, Size / 2, Size / 2)), Is.True, "the middle of the opening is capped");
            Assert.That(IsBackground(At(image, 1, 1)), Is.True, "and the corner of the image is not");

            using (var volumes = new VpIndexedIndirectDrawBatch(4, 4))
            {
                var three = new[]
                {
                    new VpIndirectCommand(new VpGeometryRange(0, 24, 0, 36), BoxBounds(Vector3.zero, 1f), 1),
                    new VpIndirectCommand(new VpGeometryRange(0, 24, 0, 36), BoxBounds(Vector3.zero, 1f), 2),
                    new VpIndirectCommand(new VpGeometryRange(0, 24, 0, 36), BoxBounds(Vector3.zero, 1f), -5),
                };
                var fourTransforms = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity, NanMatrix() };
                Assert.That(volumes.CanUpload(three, 2, fourTransforms, null), Is.True, "two commands, three instances");
                Assert.That(volumes.TryUpload(three, 2, fourTransforms, null, false), Is.True);
                Assert.That(volumes.CommandCount, Is.EqualTo(2), "only the counted commands");
                Assert.That(volumes.InstanceCount, Is.EqualTo(3), "and the instances they name");
                Assert.That(volumes.CanUpload(three, 3, fourTransforms, null), Is.False, "the third is read when counted");
                Assert.That(volumes.TryUpload(three, 3, fourTransforms, null, false), Is.False);
                Assert.That(volumes.CanUpload(three, 2, new[] { Matrix4x4.identity }, null), Is.False, "too few transforms");
                Assert.That(volumes.TryUpload(three, 2, new[] { Matrix4x4.identity }, null, false), Is.False);
                Assert.That(volumes.CanUpload(three, 2, fourTransforms, new VpInstanceClip[2]), Is.False, "too few clips");
                Assert.That(volumes.TryUpload(three, 2, fourTransforms, new VpInstanceClip[2], false), Is.False);
                Assert.That(volumes.CommandCount, Is.EqualTo(2), "a refusal changes nothing");
            }
        }

        private static Matrix4x4 NanMatrix()
        {
            var m = Matrix4x4.identity;
            m.m00 = float.NaN;
            return m;
        }

        /// <summary>
        /// The scene that was already drawn survives all of it. An ordinary lit quad stands behind the bodies; the
        /// stencil byte is set to 128 twice over, volumes are counted twice and caps are drawn twice, and the quad is
        /// still there in its own colour where no cap covered it, and still behind the caps where one did.
        /// </summary>
        [Test]
        public void TheSceneAlreadyDrawn_IsNotErasedByTheStencilWork()
        {
            Scene current = SceneManager.GetActiveScene();
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                GameObject wall = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
                Object.DestroyImmediate(wall.GetComponent<Collider>());
                wall.transform.position = new Vector3(0f, 0f, 2f);
                wall.transform.localScale = new Vector3(8f, 8f, 1f);
                Material green = Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
                green.SetColor("_BaseColor", Color.green);
                wall.GetComponent<MeshRenderer>().sharedMaterial = green;

                RenderTexture target = StencilTarget();
                Camera camera = TestCamera(target);
                Color32[] pixels;

                using (Fixture fixture = NewFixture(2))
                {
                    VpGeometryRange box = AppendBox(fixture.pool, Vector3.zero, 1f);
                    Assert.That(fixture.buffers.TryUpload(fixture.pool), Is.True);

                    var commands = new[] { new VpIndirectCommand(box, BoxBounds(Vector3.zero, 1f), 1) };
                    var transforms = new[] { Matrix4x4.identity };
                    var clips = new[] { VpInstanceClip.Keep(PlaneZ0, 1f) };
                    Vector3[] capVertices = CapPolygon();
                    int[] capIndices = FanIndices(0, capVertices.Length);

                    // The first colour has the body and its cap; the second has neither, and only re-initialises.
                    var colors = new[]
                    {
                        new VpStencilCapColor(0, 1, 0, capIndices.Length, Color.red),
                        new VpStencilCapColor(0, 0, 0, 0, Color.blue),
                    };

                    Assert.That(
                        fixture.batch.TryUpload(
                            commands, transforms, clips, capVertices, capVertices.Length, capIndices,
                            capIndices.Length, colors, 2),
                        Is.True);

                    fixture.batch.Render(fixture.materials, fixture.buffers, 0, camera);
                    pixels = RenderAndRead(camera, target);
                }

                Assert.That(IsRedish(At(pixels, Size / 2, Size / 2)), Is.True, "the cap is drawn over the wall");

                bool green1 = IsGreenish(At(pixels, 2, 2));
                bool green2 = IsGreenish(At(pixels, Size - 3, Size - 3));
                Assert.That(green1 && green2, Is.True, "and the wall is untouched where no cap covered it");
                Assert.That(
                    Count(pixels, IsGreenish), Is.GreaterThan(Size * Size / 2),
                    "most of the image is still the scene that was drawn before any of this");
            }
            finally
            {
                if (setup != null && setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
            }
        }
    }
}
