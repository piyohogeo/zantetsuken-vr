using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The provisional split display of DESIGN 5.1 and 5.2: the same parent geometry drawn per side, each instance
    /// keeping the intersection of up to eight world cut planes and moved apart by one world offset. What is checked
    /// is the image — nothing here trusts that a draw call was issued.
    /// <para>
    /// The fixture is one quad in a pool, drawn by the Stage 3 indexed indirect batch. The single plane is world
    /// x = 0, so the positive side keeps the right half of the screen and the negative side the left; the eight-plane
    /// tests use <see cref="Octagon"/>, whose every plane cuts a real piece, so that a plane which did not take
    /// effect shows up as coverage that did not change. Geometry is never duplicated or re-meshed for a side or for a
    /// plane count: every instance addresses the same range of the same buffers.
    /// </para>
    /// </summary>
    public class VpIndexedIndirectClipTests
    {
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const int Size = 64;
        private const int ShadowSize = 128;
        private const int CoveredPixels = 150;

        // A quad in the XY plane, 1 x 1 about the origin: the camera sees it filling the middle half of the image. The
        // corners run clockwise in XY, which is the front face for a camera on the -z side under Cull Back — the same
        // winding the existing draw tests use for their visible triangles.
        private static readonly Vector3[] Quad =
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
        };

        // A wide, short rectangle, wound the same way: turning it a quarter turn changes its footprint, so a
        // world-space clip cannot be mistaken for an object-space one.
        private static readonly Vector3[] WideRectangle =
        {
            new Vector3(-0.8f, -0.2f, 0f), new Vector3(-0.8f, 0.2f, 0f),
            new Vector3(0.8f, 0.2f, 0f), new Vector3(0.8f, -0.2f, 0f),
        };

        private static readonly int[] QuadTriangles = { 0, 1, 2, 0, 2, 3 };

        // The quad's own extent, not a box big enough to hide a mistake: culling bounds that ignored the
        // separation offset would let a moved instance fall outside these.
        private static readonly Bounds QuadBounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.05f));
        private static readonly Bounds WideRectangleBounds = new Bounds(Vector3.zero, new Vector3(1.6f, 0.4f, 0.05f));

        // The plane of the anchor distribution, the same plane as PlaneX0 in the form FixedSupportAnchors takes.
        private static readonly float4 AnchorPlaneX0 = new float4(1f, 0f, 0f, 0f);
        private const float AnchorEpsilon = 0.01f;

        /// <summary>The plane x = 0 in world space: the positive side is screen right.</summary>
        private static readonly Vector4 PlaneX0 = new Vector4(1f, 0f, 0f, 0f);

        /// <summary>The plane y = 0 in world space: the positive side is screen up.</summary>
        private static readonly Vector4 PlaneY0 = new Vector4(0f, 1f, 0f, 0f);

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

        private Texture2D SolidTexture(Color colour)
        {
            var texture = Track(new Texture2D(2, 2, TextureFormat.RGBA32, false));
            var pixels = new Color[4];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = colour;
            }

            texture.SetPixels(pixels);
            texture.Apply(false);
            return texture;
        }

        private Material ForwardMaterial(Color colour, Color textureColour)
        {
            Shader shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null, ShaderName);
            Material material = Track(new Material(shader));
            material.SetColor("_BaseColor", colour);
            material.SetTexture("_BaseMap", SolidTexture(textureColour));
            return material;
        }

        private Material ShadowMaterial()
        {
            Shader shader = Shader.Find(ShadowShaderName);
            Assert.That(shader, Is.Not.Null, ShadowShaderName);
            return Track(new Material(shader));
        }

        private Mesh QuadMesh(Vector3[] positions)
        {
            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new[] { new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f) });
            mesh.SetTriangles(QuadTriangles, 0);
            return mesh;
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

        private (Camera camera, RenderTexture target) FrontView()
        {
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera("VP Clip Test Camera", target);
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            return (camera, target);
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
        private static bool IsGreenish(Color32 p) => p.g > p.r + 30 && p.g > p.b + 30;
        private static bool IsRedish(Color32 p) => p.r > p.g + 30 && p.r > p.b + 30;

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

        /// <summary>Counts pixels satisfying <paramref name="test"/> in the left or the right half of the image.</summary>
        private static int CountIn(Color32[] pixels, bool leftHalf, Func<Color32, bool> test)
        {
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                bool isLeft = i % Size < Size / 2;
                if (isLeft == leftHalf && test(pixels[i]))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Counts pixels satisfying <paramref name="test"/> in the bottom or the top half of the image.</summary>
        private static int CountInRow(Color32[] pixels, bool bottomHalf, Func<Color32, bool> test)
        {
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                bool isBottom = i / Size < Size / 2;
                if (isBottom == bottomHalf && test(pixels[i]))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountDiffering(Color32[] first, Color32[] second)
        {
            Assert.That(first.Length, Is.EqualTo(second.Length));
            int differing = 0;
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i].r != second[i].r || first[i].g != second[i].g || first[i].b != second[i].b)
                {
                    differing++;
                }
            }

            return differing;
        }

        /// <summary>
        /// Draws <paramref name="positions"/> once per entry of <paramref name="transforms"/>, with the matching clip
        /// record, through the Stage 3 batch. One command, one range, one pool: both sides address the same geometry.
        /// </summary>
        private Color32[] RenderClipped(
            Vector3[] positions, Matrix4x4[] transforms, VpInstanceClip[] clips, Material forward = null, bool issueShadows = false)
        {
            Material material = forward ?? ForwardMaterial(Color.green, Color.white);
            (Camera camera, RenderTexture target) = FrontView();
            Bounds localBounds = positions == WideRectangle ? WideRectangleBounds : QuadBounds;
            using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
            {
                Assert.That(pool.TryAppend(QuadMesh(positions), out VpGeometryRange range), Is.True, "append the quad");
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");

                var commands = new[] { new VpIndirectCommand(range, localBounds, transforms.Length) };
                Assert.That(batch.TryUpload(commands, transforms, clips, false), Is.True, "batch upload");

                var properties = new MaterialPropertyBlock();
                if (issueShadows)
                {
                    batch.Render(material, ShadowMaterial(), properties, buffers, 0, camera);
                }
                else
                {
                    batch.RenderForward(material, properties, buffers, 0, 0, batch.CommandCount, camera);
                }

                return RenderAndRead(camera, target);
            }
        }

        private static VpInstanceClip[] Sides(float positiveOffsetX, float negativeOffsetX)
        {
            return new[]
            {
                VpInstanceClip.Keep(PlaneX0, 1f, new Vector3(positiveOffsetX, 0f, 0f)),
                VpInstanceClip.Keep(PlaneX0, -1f, new Vector3(negativeOffsetX, 0f, 0f)),
            };
        }

        /// <summary>
        /// Distributes <paramref name="anchors"/> across the cut plane with the existing owner anchor code, and
        /// turns the result into the offsets the display takes: a side that inherited an anchor is fixed and gets
        /// none, a side that inherited nothing gets <paramref name="separation"/>. This is the test acting as the
        /// caller would; no dependency on it is added to the renderer.
        /// </summary>
        private static (float positive, float negative) OffsetsFromAnchors(
            float3[] anchors, float separation, out bool positiveFixed, out bool negativeFixed)
        {
            var positive = new List<float3>();
            var negative = new List<float3>();
            Assert.That(
                Zantetsu.MeshCut.FixedSupportAnchors.TryDistribute(anchors, AnchorPlaneX0, AnchorEpsilon, positive, negative, out _),
                Is.True,
                "the anchors are distributed");

            positiveFixed = Zantetsu.MeshCut.FixedSupportAnchors.IsFixed(positive.Count);
            negativeFixed = Zantetsu.MeshCut.FixedSupportAnchors.IsFixed(negative.Count);
            return (positiveFixed ? 0f : separation, negativeFixed ? 0f : -separation);
        }

        private static readonly Matrix4x4[] OnePlace = { Matrix4x4.identity };
        private static readonly Matrix4x4[] TwoPlaces = { Matrix4x4.identity, Matrix4x4.identity };

        // ----- eight planes at once ------------------------------------------------------------------------------

        /// <summary>The quad scaled up to nearly fill the front view, so eight planes each have room to cut a piece.</summary>
        private static readonly Matrix4x4[] Scaled = { Matrix4x4.Scale(new Vector3(1.8f, 1.8f, 1f)) };

        /// <summary>The axis-aligned bound of <see cref="Octagon"/>: it keeps |x - cx| and |y - cy| below this.</summary>
        private const float OctagonAxis = 0.65f;

        /// <summary>The diagonal bound of <see cref="Octagon"/>: it keeps |x - cx| + |y - cy| below this.</summary>
        private const float OctagonDiagonal = 0.92f;

        /// <summary>
        /// Eight half-spaces, each keeping the inside: the regular octagon around <paramref name="centre"/> with the
        /// given axis-aligned bound and diagonal bound. Every one of the eight binds — the four axis planes cut the
        /// sides, the four diagonal ones cut the corners — which is what makes it possible to see whether a
        /// particular plane took effect: removing any single one of them enlarges the surviving region.
        /// </summary>
        private static VpClipHalfSpace[] Octagon(Vector2 centre, float axis, float diagonal)
        {
            var halfSpaces = new VpClipHalfSpace[VpInstanceClip.PlaneCapacity];
            for (int i = 0; i < halfSpaces.Length; i++)
            {
                float angle = i * Mathf.PI / 4f;
                var normal = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

                // An axis plane bounds one coordinate; a diagonal one bounds |dx| + |dy|, which along its own normal
                // is that bound over root two. The side is negative because each keeps the inside.
                float distance = i % 2 == 0 ? axis : diagonal / Mathf.Sqrt(2f);
                float alongNormal = distance + Vector2.Dot(normal, centre);
                halfSpaces[i] = new VpClipHalfSpace(new Vector4(normal.x, normal.y, 0f, -alongNormal), -1f);
            }

            return halfSpaces;
        }

        /// <summary>Whether a world point is inside the octagon <see cref="Octagon"/> describes, within a margin.</summary>
        private static bool InsideOctagon(Vector2 point, Vector2 centre, float axis, float diagonal, float margin)
        {
            float dx = Mathf.Abs(point.x - centre.x);
            float dy = Mathf.Abs(point.y - centre.y);
            return dx <= axis + margin && dy <= axis + margin && dx + dy <= diagonal + 2f * margin;
        }

        /// <summary>The world position of the centre of pixel <paramref name="index"/> of a <see cref="FrontView"/> image.</summary>
        private static Vector2 WorldAt(int index)
        {
            float x = ((index % Size) + 0.5f) / Size * 2f - 1f;
            float y = ((index / Size) + 0.5f) / Size * 2f - 1f;
            return new Vector2(x, y);
        }

        /// <summary>Counts pixels satisfying <paramref name="test"/> whose world position satisfies <paramref name="where"/>.</summary>
        private static int CountWhere(Color32[] pixels, Func<Vector2, bool> where, Func<Color32, bool> test)
        {
            int count = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (where(WorldAt(i)) && test(pixels[i]))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>The record for a whole set of half-spaces; a set the record refuses fails the test outright.</summary>
        private static VpInstanceClip KeepSet(IReadOnlyList<VpClipHalfSpace> halfSpaces, Vector3 offset)
        {
            Assert.That(VpInstanceClip.TryKeep(halfSpaces, offset, out VpInstanceClip clip), Is.True, "the plane set is accepted");
            return clip;
        }

        /// <summary>The same set of half-spaces without the one at <paramref name="index"/>.</summary>
        private static VpClipHalfSpace[] Without(VpClipHalfSpace[] halfSpaces, int index)
        {
            var remaining = new List<VpClipHalfSpace>(halfSpaces);
            remaining.RemoveAt(index);
            return remaining.ToArray();
        }

        /// <summary>
        /// One upload and one forward render through an **existing** batch, so that successive records overwrite each
        /// other in the one buffer the draw reads — which is what a stale constraint would show up in.
        /// </summary>
        private Color32[] RenderOnce(
            VpIndexedIndirectDrawBatch batch, VpIndirectCommand[] commands, Matrix4x4[] transforms, VpInstanceClip[] clips,
            Material material, MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, Camera camera, RenderTexture target)
        {
            Assert.That(batch.TryUpload(commands, transforms, clips, false), Is.True, "batch upload");
            batch.RenderForward(material, properties, buffers, 0, 0, batch.CommandCount, camera);
            return RenderAndRead(camera, target);
        }

        [Test]
        public void WithoutAClip_TheDisplayIsWhatItWasBefore()
        {
            Color32[] byOmittingClips = RenderClipped(Quad, OnePlace, null);
            Color32[] byPassingNone = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.None });

            Assert.That(Count(byOmittingClips, IsGreenish), Is.GreaterThan(CoveredPixels), "the quad is drawn");
            Assert.That(CountDiffering(byOmittingClips, byPassingNone), Is.Zero, "a record that clips nothing draws exactly the same image");
            Assert.That(CountIn(byOmittingClips, true, IsGreenish), Is.GreaterThan(0), "covering both halves");
            Assert.That(CountIn(byOmittingClips, false, IsGreenish), Is.GreaterThan(0));
        }

        [Test]
        public void EachSide_DrawsOnlyItsOwnHalf()
        {
            Color32[] whole = RenderClipped(Quad, OnePlace, null);
            Color32[] positive = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) });
            Color32[] negative = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, -1f, Vector3.zero) });

            Assert.That(CountIn(positive, false, IsGreenish), Is.GreaterThan(CoveredPixels / 2), "the positive side keeps the right half");
            Assert.That(CountIn(positive, true, IsGreenish), Is.Zero, "and draws nothing on the left");
            Assert.That(CountIn(negative, true, IsGreenish), Is.GreaterThan(CoveredPixels / 2), "the negative side keeps the left half");
            Assert.That(CountIn(negative, false, IsGreenish), Is.Zero, "and draws nothing on the right");

            // the two halves together are the whole, with a kerf of zero: no column of the original is lost
            int wholeCovered = Count(whole, IsGreenish);
            int halves = Count(positive, IsGreenish) + Count(negative, IsGreenish);
            Assert.That(halves, Is.EqualTo(wholeCovered).Within(Size), "the two halves cover what the uncut quad covered");
        }

        [Test]
        public void AMovedAndTurnedObject_IsClippedByTheWorldPlane()
        {
            // a quarter turn about Z makes the wide rectangle tall, and the move puts its middle above the plane y = 0
            Matrix4x4 turned = Matrix4x4.TRS(new Vector3(0f, 0.3f, 0f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);
            var placed = new[] { turned };

            Color32[] whole = RenderClipped(WideRectangle, placed, null);
            Assert.That(CountInRow(whole, true, IsGreenish), Is.GreaterThan(0), "the turned rectangle reaches below y = 0");
            Assert.That(CountInRow(whole, false, IsGreenish), Is.GreaterThan(0), "and above it");

            Color32[] above = RenderClipped(WideRectangle, placed, new[] { VpInstanceClip.Keep(PlaneY0, 1f, Vector3.zero) });
            Color32[] below = RenderClipped(WideRectangle, placed, new[] { VpInstanceClip.Keep(PlaneY0, -1f, Vector3.zero) });

            Assert.That(CountInRow(above, false, IsGreenish), Is.GreaterThan(0), "the positive side keeps what is above the world plane");
            Assert.That(CountInRow(above, true, IsGreenish), Is.Zero, "and nothing below it");
            Assert.That(CountInRow(below, true, IsGreenish), Is.GreaterThan(0), "the negative side keeps what is below");
            Assert.That(CountInRow(below, false, IsGreenish), Is.Zero, "and nothing above");
        }

        [Test]
        public void ChangingTheOffset_DoesNotChangeWhatSurvives()
        {
            var atRest = new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) };
            var moved = new[] { VpInstanceClip.Keep(PlaneX0, 1f, new Vector3(0.4f, 0f, 0f)) };

            Color32[] still = RenderClipped(Quad, OnePlace, atRest);
            Color32[] apart = RenderClipped(Quad, OnePlace, moved);

            Assert.That(Count(apart, IsGreenish), Is.EqualTo(Count(still, IsGreenish)).Within(Size), "the same part of the quad survives, wherever it is drawn");
            Assert.That(CountIn(apart, true, IsGreenish), Is.Zero, "and it is still only the positive side");
            Assert.That(CountDiffering(still, apart), Is.GreaterThan(0), "while the image did move");
        }

        /// <summary>
        /// The fixity comes from the existing owner anchor distribution, not from the test naming a side: one
        /// anchor on the negative side makes that child fixed, so it takes a zero offset, and the positive child,
        /// which inherited none, is the one that moves.
        /// </summary>
        [Test]
        public void OnlyTheDynamicSideMoves_AndTheFixedSideStaysWhereItWas()
        {
            (float positiveOffset, float negativeOffset) = OffsetsFromAnchors(
                new[] { new float3(-0.3f, 0f, 0f) }, 0.35f, out bool positiveFixed, out bool negativeFixed);
            Assert.That(negativeFixed, Is.True, "the anchor went to the negative side, so that child is fixed");
            Assert.That(positiveFixed, Is.False, "and the positive child inherited none");
            Assert.That(negativeOffset, Is.Zero, "a fixed side takes no offset");
            Assert.That(positiveOffset, Is.Not.Zero, "and the dynamic side is the one that moves");

            Color32[] together = RenderClipped(Quad, TwoPlaces, Sides(0f, 0f));
            Color32[] separated = RenderClipped(Quad, TwoPlaces, Sides(positiveOffset, negativeOffset));

            Assert.That(CountIn(together, true, IsGreenish), Is.GreaterThan(CoveredPixels / 2), "both sides are drawn when neither moves");
            Assert.That(CountIn(together, false, IsGreenish), Is.GreaterThan(CoveredPixels / 2));

            // the fixed (negative, left) side is untouched by the other side moving
            Color32[] negativeAlone = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, -1f, Vector3.zero) });
            for (int i = 0; i < separated.Length; i++)
            {
                if (i % Size < Size / 2)
                {
                    Assert.That(
                        separated[i].g == negativeAlone[i].g && separated[i].r == negativeAlone[i].r,
                        Is.True,
                        "the fixed side did not move: pixel " + i);
                }
            }

            Assert.That(Count(separated, IsGreenish), Is.EqualTo(Count(together, IsGreenish)).Within(2 * Size), "both sides are still fully drawn");
        }

        /// <summary>
        /// An anchor on each side makes both children fixed, so both take a zero offset — and being fixed is still
        /// no reason to skip either side's clipped display (DESIGN 5.1).
        /// </summary>
        [Test]
        public void BothSidesFixed_AreStillClipped()
        {
            (float positiveOffset, float negativeOffset) = OffsetsFromAnchors(
                new[] { new float3(-0.3f, 0f, 0f), new float3(0.3f, 0f, 0f) }, 0.35f, out bool positiveFixed, out bool negativeFixed);
            Assert.That(positiveFixed && negativeFixed, Is.True, "an anchor on each side fixes both children");
            Assert.That(positiveOffset, Is.Zero, "so neither side moves");
            Assert.That(negativeOffset, Is.Zero);

            Color32[] bothFixed = RenderClipped(Quad, TwoPlaces, Sides(positiveOffset, negativeOffset));
            Color32[] positiveOnly = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) });
            Color32[] negativeOnly = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, -1f, Vector3.zero) });

            // neither side was skipped for being fixed, and each still covers only its own half
            Assert.That(CountIn(bothFixed, false, IsGreenish), Is.EqualTo(CountIn(positiveOnly, false, IsGreenish)).Within(Size), "the positive half is drawn");
            Assert.That(CountIn(bothFixed, true, IsGreenish), Is.EqualTo(CountIn(negativeOnly, true, IsGreenish)).Within(Size), "and so is the negative half");
        }

        [Test]
        public void TheTextureAndTheBaseColour_SurviveTheClip()
        {
            Material red = ForwardMaterial(Color.white, Color.red);
            Color32[] clipped = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) }, red);

            Assert.That(CountIn(clipped, false, IsRedish), Is.GreaterThan(CoveredPixels / 2), "the kept half is the texture colour");
            Assert.That(CountIn(clipped, true, p => !IsBackground(p)), Is.Zero, "and the clipped half is nothing at all");
        }

        [Test]
        public void TwoRangesInOneBatch_AreEachClippedByTheirOwnRecord()
        {
            Material material = ForwardMaterial(Color.green, Color.white);
            (Camera camera, RenderTexture target) = FrontView();
            using (var pool = new VpCpuGeometryPool(12, 24, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(12, 24))
            using (var batch = new VpIndexedIndirectDrawBatch(4, 8))
            {
                // two ranges with different vertex and index starts, as two commands
                Assert.That(pool.TryAppend(QuadMesh(Quad), out VpGeometryRange first), Is.True, "append the first quad");
                Assert.That(pool.TryAppend(QuadMesh(WideRectangle), out VpGeometryRange second), Is.True, "append the second");
                Assert.That(first.vertexStart, Is.Not.EqualTo(second.vertexStart), "the ranges differ");
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");

                var commands = new[]
                {
                    new VpIndirectCommand(first, QuadBounds, 1),
                    new VpIndirectCommand(second, WideRectangleBounds, 1),
                };
                var clips = new[]
                {
                    VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero),
                    VpInstanceClip.Keep(PlaneX0, -1f, Vector3.zero),
                };
                Assert.That(batch.TryUpload(commands, new[] { Matrix4x4.identity, Matrix4x4.identity }, clips, false), Is.True, "batch upload");

                var properties = new MaterialPropertyBlock();
                batch.RenderForward(material, properties, buffers, 0, 0, batch.CommandCount, camera);
                Color32[] pixels = RenderAndRead(camera, target);

                Assert.That(CountIn(pixels, false, IsGreenish), Is.GreaterThan(CoveredPixels / 2), "the first range kept its positive half");
                Assert.That(CountIn(pixels, true, IsGreenish), Is.GreaterThan(CoveredPixels / 2), "the second range kept its negative half");
            }
        }

        [Test]
        public void ARejectedUpload_KeepsTheEarlierClips()
        {
            using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
            {
                var range = new VpGeometryRange(0, 4, 0, 6);
                var commands = new[] { new VpIndirectCommand(range, QuadBounds, 1) };
                Assert.That(batch.TryUpload(commands, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) }, false), Is.True);

                // a clip count that is neither zero nor the instance count is refused
                Assert.That(
                    batch.TryUpload(commands, OnePlace, new[] { VpInstanceClip.None, VpInstanceClip.None }, false),
                    Is.False,
                    "a mismatched clip count is refused");
                Assert.That(batch.InstanceCount, Is.EqualTo(1), "and nothing about the batch changed");

                var readback = new VpInstanceClip[1];
                batch.InstanceClipBuffer.GetData(readback, 0, 0, 1);
                Assert.That(readback[0].PlaneCount, Is.EqualTo(1), "the earlier clip record still stands");
                Assert.That(readback[0].SignedPlane(0), Is.EqualTo(PlaneX0), "with the plane and the side it had");
            }
        }

        /// <summary>
        /// The culling bounds follow the separation: an instance placed outside the view and offset back into it is
        /// drawn, which it would not be if the bounds still described where it used to be.
        /// </summary>
        [Test]
        public void AnInstanceOffsetBackIntoView_IsNotCulledAway()
        {
            // far to the right of a camera that sees x in [-1, 1]
            var farAway = new[] { Matrix4x4.Translate(new Vector3(4f, 0f, 0f)) };

            Color32[] leftWhereItIs = RenderClipped(Quad, farAway, new[] { VpInstanceClip.None });
            Assert.That(Count(leftWhereItIs, IsGreenish), Is.Zero, "where it stands, it is off screen");

            // The plane goes through the instance, not through the origin: x = 4, keeping the half beyond it. The
            // offset then brings that half back to x in [0, 0.5], which is the right of the screen.
            var planeThroughIt = new Vector4(1f, 0f, 0f, -4f);
            Color32[] broughtBack = RenderClipped(Quad, farAway, new[] { VpInstanceClip.Keep(planeThroughIt, 1f, new Vector3(-4f, 0f, 0f)) });
            Assert.That(CountIn(broughtBack, false, IsGreenish), Is.GreaterThan(CoveredPixels / 4), "offset back into view, it is drawn");
            Assert.That(CountIn(broughtBack, true, IsGreenish), Is.Zero, "and still only the half the plane left it");
        }

        /// <summary>The batch reports bounds that hold every instance where it is actually drawn.</summary>
        [Test]
        public void TheWorldBounds_HoldEveryInstanceWhereItIsDrawn()
        {
            using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
            {
                var range = new VpGeometryRange(0, 4, 0, 6);
                var commands = new[] { new VpIndirectCommand(range, QuadBounds, 2) };
                var places = new[] { Matrix4x4.identity, Matrix4x4.identity };

                Assert.That(batch.TryUpload(commands, places, null, false), Is.True, "without clips");
                Bounds unmoved = batch.WorldBounds;
                Assert.That(unmoved.center.magnitude, Is.LessThan(1e-4f), "both instances sit at the origin");

                var apart = new[]
                {
                    VpInstanceClip.Keep(PlaneX0, 1f, new Vector3(2f, 0f, 0f)),
                    VpInstanceClip.Keep(PlaneX0, -1f, new Vector3(-2f, 0f, 0f)),
                };
                Assert.That(batch.TryUpload(commands, places, apart, false), Is.True, "with clips that separate them");
                Bounds moved = batch.WorldBounds;

                Assert.That(moved.size.x, Is.GreaterThan(unmoved.size.x + 3f), "the bounds grew to hold both offsets");
                Assert.That(moved.Contains(new Vector3(2f, 0f, 0f)), Is.True, "and hold where the positive side is drawn");
                Assert.That(moved.Contains(new Vector3(-2f, 0f, 0f)), Is.True, "and where the negative side is drawn");
            }
        }

        /// <summary>
        /// The forms that existed before the capacity grew convert into the eight-plane record without changing what
        /// they mean: no planes, one plane with either side, and a side of zero which is no clipping but still moves.
        /// </summary>
        [Test]
        public void TheOldSinglePlaneForms_ConvertIntoTheRecordUnchanged()
        {
            Assert.That(VpInstanceClip.None.PlaneCount, Is.Zero, "no clip carries no plane");
            Assert.That(VpInstanceClip.None.IsClipped, Is.False);
            Assert.That(VpInstanceClip.None.Offset, Is.EqualTo(Vector3.zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => VpInstanceClip.None.SignedPlane(0), "and no plane to read");

            VpInstanceClip positive = VpInstanceClip.Keep(PlaneX0, 1f, new Vector3(0.25f, 0f, 0f));
            Assert.That(positive.PlaneCount, Is.EqualTo(1), "one plane, whatever the capacity is");
            Assert.That(positive.IsClipped, Is.True);
            Assert.That(positive.SignedPlane(0), Is.EqualTo(PlaneX0));
            Assert.That(positive.Offset, Is.EqualTo(new Vector3(0.25f, 0f, 0f)));

            VpInstanceClip negative = VpInstanceClip.Keep(PlaneX0, -1f, Vector3.zero);
            Assert.That(negative.PlaneCount, Is.EqualTo(1));
            Assert.That(negative.SignedPlane(0), Is.EqualTo(-PlaneX0), "the side kept is folded into the plane");

            // a side of zero was never clipping, and still moved the instance
            VpInstanceClip lifted = VpInstanceClip.Keep(PlaneX0, 0f, new Vector3(0f, 0.5f, 0f));
            Assert.That(lifted.IsClipped, Is.False, "a side of zero is still no clipping");
            Assert.That(lifted.Offset, Is.EqualTo(new Vector3(0f, 0.5f, 0f)), "and the offset still applies");

            // an empty set is the same thing, said the other way
            Assert.That(VpInstanceClip.TryKeep(new VpClipHalfSpace[0], Vector3.zero, out VpInstanceClip empty), Is.True);
            Assert.That(empty.PlaneCount, Is.Zero);
        }

        /// <summary>
        /// A set larger than the capacity is refused whole, before anything is built from it, and never quietly
        /// reduced to the first eight — a truncated intersection would keep more than the caller asked for.
        /// </summary>
        [Test]
        public void APlaneSetBeyondTheCapacity_IsRefusedWholeAndNotTruncated()
        {
            var nine = new VpClipHalfSpace[VpInstanceClip.PlaneCapacity + 1];
            for (int i = 0; i < nine.Length; i++)
            {
                nine[i] = new VpClipHalfSpace(new Vector4(1f, 0f, 0f, -0.1f * i), 1f);
            }

            Assert.That(VpInstanceClip.TryKeep(nine, new Vector3(0.5f, 0f, 0f), out VpInstanceClip refused), Is.False, "nine planes are refused");
            Assert.That(refused.PlaneCount, Is.Zero, "and not truncated to the capacity");
            Assert.That(refused.IsClipped, Is.False);

            Assert.That(VpInstanceClip.TryKeep(null, Vector3.zero, out _), Is.False, "so is no set at all");

            var withoutASide = new[] { new VpClipHalfSpace(PlaneX0, 1f), new VpClipHalfSpace(PlaneY0, 0f) };
            Assert.That(
                VpInstanceClip.TryKeep(withoutASide, Vector3.zero, out _), Is.False,
                "and so is a half-space with no side, which would quietly drop a constraint");

            VpClipHalfSpace[] eight = Octagon(Vector2.zero, OctagonAxis, OctagonDiagonal);
            Assert.That(VpInstanceClip.TryKeep(eight, Vector3.zero, out VpInstanceClip full), Is.True, "the capacity itself is accepted");
            Assert.That(full.PlaneCount, Is.EqualTo(VpInstanceClip.PlaneCapacity));
            Assert.Throws<ArgumentOutOfRangeException>(() => full.SignedPlane(VpInstanceClip.PlaneCapacity));
        }

        /// <summary>Eight planes leave the intersection of their half-spaces, and nothing outside it.</summary>
        [Test]
        public void EightPlanes_DrawOnlyTheirIntersection()
        {
            VpClipHalfSpace[] octagon = Octagon(Vector2.zero, OctagonAxis, OctagonDiagonal);
            Color32[] clipped = RenderClipped(Quad, Scaled, new[] { KeepSet(octagon, Vector3.zero) });
            Color32[] whole = RenderClipped(Quad, Scaled, new[] { VpInstanceClip.None });

            Assert.That(Count(clipped, IsGreenish), Is.GreaterThan(CoveredPixels * 4), "the middle of the quad survives");
            Assert.That(Count(whole, IsGreenish), Is.GreaterThan(Count(clipped, IsGreenish)), "and the planes removed the rest");

            // one pixel of the front view is 2 / Size wide in world units
            float margin = 2f / Size;
            Assert.That(
                CountWhere(clipped, p => !InsideOctagon(p, Vector2.zero, OctagonAxis, OctagonDiagonal, margin), IsGreenish),
                Is.Zero,
                "nothing survives outside the intersection of the eight half-spaces");
        }

        /// <summary>
        /// Every one of the eight planes takes effect, the four in the second clip distance register included:
        /// dropping any single plane from the set enlarges what survives. A set whose back half never reached the
        /// hardware would show no change for those four.
        /// </summary>
        [Test]
        public void RemovingAnyOneOfTheEightPlanes_ChangesWhatSurvives()
        {
            VpClipHalfSpace[] octagon = Octagon(Vector2.zero, OctagonAxis, OctagonDiagonal);
            int all = Count(RenderClipped(Quad, Scaled, new[] { KeepSet(octagon, Vector3.zero) }), IsGreenish);
            Assert.That(all, Is.GreaterThan(CoveredPixels * 4), "the eight planes leave a region to compare against");

            for (int i = 0; i < octagon.Length; i++)
            {
                int without = Count(RenderClipped(Quad, Scaled, new[] { KeepSet(Without(octagon, i), Vector3.zero) }), IsGreenish);
                Assert.That(
                    without, Is.GreaterThan(all + 30),
                    "plane " + i + " of eight has to bind: without it " + without + " px survive, with it " + all);
            }
        }

        /// <summary>
        /// Several instances in one draw each take their own plane set, their own sides and their own offset: one
        /// kept to an octagon about its own middle and lifted, one kept to half of a plane through its own middle and
        /// moved the other way. The gap between the two regions stays empty, so neither took the other's planes.
        /// </summary>
        [Test]
        public void SeveralInstances_AreEachClippedByTheirOwnPlaneSetAndOffset()
        {
            var places = new[]
            {
                Matrix4x4.Translate(new Vector3(-0.5f, 0f, 0f)),
                Matrix4x4.Translate(new Vector3(0.5f, 0f, 0f)),
            };

            var leftCentre = new Vector2(-0.5f, 0f);
            var leftLift = new Vector3(0f, 0.2f, 0f);
            const float leftAxis = 0.35f;
            const float leftDiagonal = 0.5f;
            var clips = new[]
            {
                KeepSet(Octagon(leftCentre, leftAxis, leftDiagonal), leftLift),
                VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, -0.5f), 1f, new Vector3(-0.1f, 0f, 0f)),
            };

            Color32[] pixels = RenderClipped(Quad, places, clips);
            float margin = 2f / Size;

            Assert.That(CountWhere(pixels, p => p.x < 0f, IsGreenish), Is.GreaterThan(CoveredPixels * 2), "the first instance kept its octagon");
            Assert.That(
                CountWhere(
                    pixels,
                    p => p.x < 0f && !InsideOctagon(p - (Vector2)leftLift, leftCentre, leftAxis, leftDiagonal, margin),
                    IsGreenish),
                Is.Zero,
                "and nothing of it outside that set, lifted by its own offset");

            Assert.That(CountWhere(pixels, p => p.x > 0f, IsGreenish), Is.GreaterThan(CoveredPixels * 2), "the second instance kept its own half");
            Assert.That(
                CountWhere(
                    pixels,
                    p => p.x > 0f && (p.x < 0.4f - margin || p.x > 0.9f + margin || Mathf.Abs(p.y) > 0.5f + margin),
                    IsGreenish),
                Is.Zero,
                "and nothing of it outside that half, moved by its own offset");

            Assert.That(
                CountWhere(pixels, p => p.x > -0.1f && p.x < 0.35f, IsGreenish), Is.Zero,
                "and the gap between the two regions is empty: neither instance took the other's planes");
        }

        /// <summary>
        /// Eight planes, then one, then none, through the one buffer the draw reads: each record replaces the last
        /// whole, so no plane of an earlier upload is left in force. What the batch shows after the earlier uploads
        /// is compared pixel for pixel with the same display built from nothing.
        /// </summary>
        [Test]
        public void EightPlanesThenOneThenNone_LeaveNoStaleConstraint()
        {
            Material material = ForwardMaterial(Color.green, Color.white);
            var octagon = new[] { KeepSet(Octagon(Vector2.zero, OctagonAxis, OctagonDiagonal), Vector3.zero) };
            var onePlane = new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) };
            var noPlane = new[] { VpInstanceClip.None };

            (Camera camera, RenderTexture target) = FrontView();
            Color32[] eight;
            Color32[] one;
            Color32[] none;
            using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
            {
                Assert.That(pool.TryAppend(QuadMesh(Quad), out VpGeometryRange range), Is.True, "append the quad");
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");
                var commands = new[] { new VpIndirectCommand(range, QuadBounds, 1) };
                var properties = new MaterialPropertyBlock();

                eight = RenderOnce(batch, commands, Scaled, octagon, material, properties, buffers, camera, target);
                one = RenderOnce(batch, commands, Scaled, onePlane, material, properties, buffers, camera, target);
                none = RenderOnce(batch, commands, Scaled, noPlane, material, properties, buffers, camera, target);
            }

            Color32[] freshOne = RenderClipped(Quad, Scaled, onePlane, material);
            Color32[] freshNone = RenderClipped(Quad, Scaled, noPlane, material);

            Assert.That(Count(eight, IsGreenish), Is.LessThan(Count(one, IsGreenish)), "eight planes keep less than one does");
            Assert.That(CountDiffering(one, freshOne), Is.Zero, "after eight planes, one plane shows exactly one plane's worth");
            Assert.That(CountDiffering(none, freshNone), Is.Zero, "and then no plane shows the whole quad, with nothing left in force");
        }

        /// <summary>
        /// A plane set out of range is refused when the record is built, which is before any draw data is touched:
        /// the record the draw reads is still the earlier one, and the display it produced is unchanged.
        /// </summary>
        [Test]
        public void ARejectedPlaneSet_KeepsTheDisplayItHad()
        {
            Material material = ForwardMaterial(Color.green, Color.white);
            var three = new[]
            {
                new VpClipHalfSpace(PlaneX0, 1f),                          // keep x >= 0
                new VpClipHalfSpace(PlaneY0, 1f),                          // keep y >= 0
                new VpClipHalfSpace(new Vector4(1f, 1f, 0f, -1f), -1f),    // keep x + y <= 1
            };
            var nine = new VpClipHalfSpace[VpInstanceClip.PlaneCapacity + 1];
            for (int i = 0; i < nine.Length; i++)
            {
                nine[i] = new VpClipHalfSpace(new Vector4(0f, 1f, 0f, -0.1f * i), 1f);
            }

            (Camera camera, RenderTexture target) = FrontView();
            using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
            {
                Assert.That(pool.TryAppend(QuadMesh(Quad), out VpGeometryRange range), Is.True, "append the quad");
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");
                var commands = new[] { new VpIndirectCommand(range, QuadBounds, 1) };
                var properties = new MaterialPropertyBlock();
                var kept = new[] { KeepSet(three, Vector3.zero) };

                Color32[] before = RenderOnce(batch, commands, Scaled, kept, material, properties, buffers, camera, target);
                Assert.That(Count(before, IsGreenish), Is.GreaterThan(CoveredPixels * 2), "the three planes leave a corner of the quad");

                Assert.That(VpInstanceClip.TryKeep(nine, Vector3.zero, out VpInstanceClip refused), Is.False, "the larger set is refused");
                Assert.That(refused.PlaneCount, Is.Zero, "and nothing was built from it");

                var readback = new VpInstanceClip[1];
                batch.InstanceClipBuffer.GetData(readback, 0, 0, 1);
                Assert.That(readback[0].PlaneCount, Is.EqualTo(3), "the record the draw reads is still the earlier one");

                Color32[] after = RenderOnce(batch, commands, Scaled, kept, material, properties, buffers, camera, target);
                Assert.That(CountDiffering(before, after), Is.Zero, "and the display it produced is unchanged");
            }
        }

        // ----- the other passes ----------------------------------------------------------------------------------

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
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }

        /// <summary>
        /// The clipped-away half writes no depth either: a red Unity quad behind the VP geometry shows through exactly
        /// where the clip removed it, rather than being hidden by an invisible surface.
        /// </summary>
        [Test]
        public void TheClippedAwayHalf_WritesNoDepthEither()
        {
            InEmptyScene(() =>
            {
                GameObject wall = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
                Object.DestroyImmediate(wall.GetComponent<Collider>());
                wall.transform.position = new Vector3(0f, 0f, 1f);
                wall.transform.localScale = new Vector3(4f, 4f, 1f);
                Material red = Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
                red.SetColor("_BaseColor", Color.red);
                wall.GetComponent<MeshRenderer>().sharedMaterial = red;

                Color32[] pixels = RenderClipped(Quad, OnePlace, new[] { VpInstanceClip.Keep(PlaneX0, 1f, Vector3.zero) });

                Assert.That(CountIn(pixels, false, IsGreenish), Is.GreaterThan(CoveredPixels / 2), "the kept half is in front of the wall");
                Assert.That(CountIn(pixels, true, IsGreenish), Is.Zero, "the clipped half draws no colour");
                Assert.That(CountIn(pixels, true, IsRedish), Is.GreaterThan(CoveredPixels), "and the wall behind it is visible there");
            });
        }

        /// <summary>
        /// The shadow caster reads the same record: only the kept, moved half casts a shadow. The ground is checked to
        /// be visible and lit first, so a missing shadow cannot pass as a dark image.
        /// </summary>
        [Test]
        public void TheShadowFollowsTheClipAndTheOffset()
        {
            InEmptyScene(() =>
            {
                // Lit from above but at a slant. Straight down would not do: the quad faces the camera, which looks
                // straight down, so a light on the same axis would meet its back face and the Cull Back shadow caster
                // would write nothing. The slant also throws the shadow clear of the quad itself, so the darkening
                // that is measured is the shadow and not the caster.
                Light light = Track(new GameObject("VP Clip Test Sun")).AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                light.shadows = LightShadows.Hard;
                light.transform.rotation = Quaternion.Euler(50f, 0f, 0f);

                GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
                Object.DestroyImmediate(ground.GetComponent<Collider>());
                ground.transform.position = new Vector3(0f, -1f, 0f);
                ground.transform.localScale = new Vector3(2f, 1f, 2f);
                MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
                Material lit = Track(new Material(Shader.Find("Universal Render Pipeline/Lit")));
                lit.SetColor("_BaseColor", Color.white);
                groundRenderer.sharedMaterial = lit;
                groundRenderer.receiveShadows = true;
                groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

                RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
                Camera camera = TestCamera("VP Clip Shadow Camera", target);
                camera.transform.position = new Vector3(0f, 4f, 0f);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize = 1.5f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 20f;

                // the ground alone: it must be visible and lit before a shadow means anything
                Color32[] bare = RenderAndRead(camera, target);
                int litGround = Count(bare, p => p.r > 60 && p.g > 60 && p.b > 60);
                Assert.That(litGround, Is.GreaterThan(ShadowSize * ShadowSize / 4), "the ground is visible and exposed");

                // a horizontal quad above the ground, clipped to its positive half and moved further right
                Material material = ForwardMaterial(Color.green, Color.white);
                Matrix4x4 lying = Matrix4x4.TRS(new Vector3(0f, 0.5f, 0f), Quaternion.Euler(90f, 0f, 0f), Vector3.one);
                using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
                using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
                using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
                {
                    Assert.That(pool.TryAppend(QuadMesh(Quad), out VpGeometryRange range), Is.True, "append the quad");
                    Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");
                    var commands = new[] { new VpIndirectCommand(range, QuadBounds, 1) };
                    var clips = new[] { VpInstanceClip.Keep(PlaneX0, 1f, new Vector3(0.5f, 0f, 0f)) };
                    Assert.That(batch.TryUpload(commands, new[] { lying }, clips, false), Is.True, "batch upload");

                    var properties = new MaterialPropertyBlock();
                    batch.Render(material, ShadowMaterial(), properties, buffers, 0, camera);
                    Color32[] shadowed = RenderAndRead(camera, target);

                    int darkenedRight = 0;
                    int darkenedLeft = 0;
                    long darkenedColumnSum = 0;
                    for (int i = 0; i < shadowed.Length; i++)
                    {
                        bool isLeft = i % ShadowSize < ShadowSize / 2;
                        bool darker = shadowed[i].g + 20 < bare[i].g;
                        if (darker)
                        {
                            darkenedColumnSum += i % ShadowSize;
                        }

                        if (darker && isLeft)
                        {
                            darkenedLeft++;
                        }
                        else if (darker)
                        {
                            darkenedRight++;
                        }
                    }

                    int darkened = darkenedLeft + darkenedRight;
                    string where = darkened == 0
                        ? "nothing was darkened at all"
                        : "darkened " + darkened + " px, mean column " + (darkenedColumnSum / darkened) + " of " + ShadowSize;
                    int greenOnScreen = Count(shadowed, IsGreenish);
                    string what = where + "; the VP quad itself covers " + greenOnScreen + " px";

                    Assert.That(darkenedRight, Is.GreaterThan(100), "the kept, moved half casts a shadow on the right — " + what);
                    Assert.That(darkenedLeft, Is.LessThan(darkenedRight / 4), "and the clipped-away half casts almost none on the left — " + what);
                }
            });
        }

        /// <summary>
        /// What several planes remove writes no depth either: a red Unity quad behind the VP geometry shows through
        /// in all three quarters two planes took away, rather than being hidden by an invisible surface.
        /// </summary>
        [Test]
        public void TheRegionSeveralPlanesRemove_WritesNoDepthEither()
        {
            InEmptyScene(() =>
            {
                GameObject wall = Track(GameObject.CreatePrimitive(PrimitiveType.Quad));
                Object.DestroyImmediate(wall.GetComponent<Collider>());
                wall.transform.position = new Vector3(0f, 0f, 1f);
                wall.transform.localScale = new Vector3(4f, 4f, 1f);
                Material red = Track(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
                red.SetColor("_BaseColor", Color.red);
                wall.GetComponent<MeshRenderer>().sharedMaterial = red;

                // keep x >= 0 and y >= 0: one quarter of the quad
                var quarter = new[] { new VpClipHalfSpace(PlaneX0, 1f), new VpClipHalfSpace(PlaneY0, 1f) };
                Color32[] pixels = RenderClipped(Quad, OnePlace, new[] { KeepSet(quarter, Vector3.zero) });

                Assert.That(
                    CountWhere(pixels, p => p.x > 0f && p.y > 0f, IsGreenish), Is.GreaterThan(CoveredPixels),
                    "the quarter both planes keep is in front of the wall");
                Assert.That(
                    CountWhere(pixels, p => p.x < 0f || p.y < 0f, IsGreenish), Is.Zero,
                    "the three quarters they removed draw no colour");
                Assert.That(
                    CountWhere(pixels, p => p.x < 0f || p.y < 0f, IsRedish), Is.GreaterThan(CoveredPixels),
                    "and the wall behind them is visible there");
            });
        }

        /// <summary>
        /// The shadow caster reads the whole record, not just its first plane: the same set and the same offset. The
        /// ground is checked to be visible and lit first, and the two-plane shadow is compared with the shadow of the
        /// same set minus its second plane — a caster that ignored that plane would cast the larger, farther shadow.
        /// </summary>
        [Test]
        public void TheShadowFollowsEveryPlaneOfTheSet()
        {
            InEmptyScene(() =>
            {
                Light light = Track(new GameObject("VP Clip Test Sun")).AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                light.shadows = LightShadows.Hard;
                light.transform.rotation = Quaternion.Euler(50f, 0f, 0f);

                GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
                Object.DestroyImmediate(ground.GetComponent<Collider>());
                ground.transform.position = new Vector3(0f, -1f, 0f);
                ground.transform.localScale = new Vector3(2f, 1f, 2f);
                MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
                Material lit = Track(new Material(Shader.Find("Universal Render Pipeline/Lit")));
                lit.SetColor("_BaseColor", Color.white);
                groundRenderer.sharedMaterial = lit;
                groundRenderer.receiveShadows = true;
                groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

                RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
                Camera camera = TestCamera("VP Clip Shadow Camera", target);
                camera.transform.position = new Vector3(0f, 4f, 0f);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize = 1.5f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 20f;

                Color32[] bare = RenderAndRead(camera, target);
                Assert.That(
                    Count(bare, p => p.r > 60 && p.g > 60 && p.b > 60), Is.GreaterThan(ShadowSize * ShadowSize / 4),
                    "the ground is visible and exposed");

                // A quad lying above the ground, so its world x and z are the screen's two axes, and the light is
                // slanted towards +z: what the shadow of the far half would cover is nearer the top of the image.
                Material material = ForwardMaterial(Color.green, Color.white);
                Matrix4x4 lying = Matrix4x4.TRS(new Vector3(0f, 0.5f, 0f), Quaternion.Euler(90f, 0f, 0f), Vector3.one);
                var moved = new Vector3(0.5f, 0f, 0f);
                var alongX = new[] { new VpClipHalfSpace(PlaneX0, 1f) };
                var alongXAndZ = new[]
                {
                    new VpClipHalfSpace(PlaneX0, 1f),                          // keep x >= 0
                    new VpClipHalfSpace(new Vector4(0f, 0f, 1f, 0f), -1f),     // and keep z <= 0
                };

                using (var pool = new VpCpuGeometryPool(8, 12, Allocator.Persistent))
                using (var buffers = new VpGpuIndexedGeometryBuffers(8, 12))
                using (var batch = new VpIndexedIndirectDrawBatch(2, 4))
                {
                    Assert.That(pool.TryAppend(QuadMesh(Quad), out VpGeometryRange range), Is.True, "append the quad");
                    Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");
                    var commands = new[] { new VpIndirectCommand(range, QuadBounds, 1) };
                    var properties = new MaterialPropertyBlock();

                    int Darkened(VpClipHalfSpace[] set, out int onTheLeft, out int meanRow)
                    {
                        Assert.That(
                            batch.TryUpload(commands, new[] { lying }, new[] { KeepSet(set, moved) }, false), Is.True, "batch upload");
                        batch.Render(material, ShadowMaterial(), properties, buffers, 0, camera);
                        Color32[] shadowed = RenderAndRead(camera, target);

                        int darkened = 0;
                        long rowSum = 0;
                        onTheLeft = 0;
                        for (int i = 0; i < shadowed.Length; i++)
                        {
                            if (shadowed[i].g + 20 >= bare[i].g)
                            {
                                continue;
                            }

                            darkened++;
                            rowSum += i / ShadowSize;
                            if (i % ShadowSize < ShadowSize / 2)
                            {
                                onTheLeft++;
                            }
                        }

                        meanRow = darkened == 0 ? 0 : (int)(rowSum / darkened);
                        return darkened;
                    }

                    int one = Darkened(alongX, out int oneLeft, out int oneRow);
                    int both = Darkened(alongXAndZ, out int bothLeft, out int bothRow);
                    string what =
                        "one plane: " + one + " px, mean row " + oneRow + " (" + oneLeft + " left); two planes: " +
                        both + " px, mean row " + bothRow + " (" + bothLeft + " left); of " + ShadowSize;

                    Assert.That(both, Is.GreaterThan(120), "what the set keeps still casts a shadow — " + what);
                    Assert.That(bothLeft, Is.LessThan(both / 4), "on the side the first plane keeps, where the offset moved it — " + what);
                    Assert.That(both, Is.LessThan(one * 0.85f), "the second plane cut the shadow down as well — " + what);
                    Assert.That(bothRow, Is.LessThan(oneRow), "and it is the far part of the shadow that went — " + what);
                }
            });
        }
    }
}
