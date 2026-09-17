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
    /// The provisional split display of DESIGN 5.1: the same parent geometry drawn twice, each instance keeping one
    /// half of a world cut plane and moved apart by a world offset. What is checked is the image — nothing here trusts
    /// that a draw call was issued.
    /// <para>
    /// The fixture is one quad in a pool, drawn by the Stage 3 indexed indirect batch. The plane is world x = 0, so the
    /// positive side keeps the right half of the screen and the negative side the left. Geometry is never duplicated or
    /// re-meshed for a side: both instances address the same range of the same buffers.
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
                Assert.That(readback[0].Side, Is.EqualTo(1f), "the earlier clip record still stands");
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
    }
}
