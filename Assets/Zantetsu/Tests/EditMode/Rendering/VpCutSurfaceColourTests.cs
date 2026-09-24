using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The cut surface colour of DESIGN 5.3 as it actually renders: a cap, marked by a negative raw uv0, is drawn in
    /// the cut surface colour with no texture of its own, while everything else is drawn as before. What is checked
    /// here is the image, not the intent — every case renders the Stage 3 indexed indirect pass and reads the pixels
    /// back.
    /// <para>
    /// The colours and the one debug switch are global shader constants, so this fixture takes what it found at the
    /// start and puts it back at the end: a test must not leave the editor's globals changed.
    /// </para>
    /// </summary>
    public class VpCutSurfaceColourTests
    {
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const int Size = 64;

        // The marker every generated cap vertex carries (Zantetsu.MeshCut.RenderCutMarker); written here as the raw
        // value so that this test does not depend on the MeshCut assembly.
        private static readonly Vector2 CapUv = new Vector2(247.5f / 256f, 247.5f / 256f);
        private static readonly Vector2 SurfaceUv = new Vector2(0.5f, 0.5f);

        private static readonly Vector3[] k_leftTriangle =
        {
            new Vector3(-0.9f, -0.8f, 0f), new Vector3(-0.9f, 0.8f, 0f), new Vector3(-0.15f, 0f, 0f),
        };

        private static readonly Vector3[] k_rightTriangle =
        {
            new Vector3(0.15f, -0.8f, 0f), new Vector3(0.15f, 0.8f, 0f), new Vector3(0.9f, 0f, 0f),
        };

        private readonly List<Object> _objects = new List<Object>();
        private VpCutSurfaceColour.State _globalsAtStart;

        /// <summary>
        /// Only takes what is there. It deliberately does **not** apply the defaults: that is the product's own
        /// initialization to do, and a fixture that did it here would hide its absence.
        /// <para>
        /// It is still not a check on the product's startup: <see cref="VpCutSurfaceColour.Capture"/> initializes if
        /// nothing else has yet, so what the tests below see is "the defaults are in place", not "this or that startup
        /// attribute fired". The initialization behaviour itself is tested directly, by calling the entry points.
        /// </para>
        /// </summary>
        [SetUp]
        public void TakeTheGlobals()
        {
            _globalsAtStart = VpCutSurfaceColour.Capture();
        }

        [TearDown]
        public void PutBackTheGlobalsAndDestroyObjects()
        {
            VpCutSurfaceColour.Restore(_globalsAtStart);
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

        /// <summary>A flat triangle whose every vertex carries <paramref name="uv"/>, so the whole of it is marked or not.</summary>
        private Mesh Triangle(Vector3[] positions, Vector2 uv)
        {
            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new[] { uv, uv, uv });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
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

        private Material ForwardMaterial(Color textureColour, Color baseColour)
        {
            Shader shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null, ShaderName);
            Material material = Track(new Material(shader));
            material.SetTexture("_BaseMap", SolidTexture(textureColour));
            material.SetColor("_BaseColor", baseColour);
            return material;
        }

        private Camera FrontView(out RenderTexture target)
        {
            target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Cut Surface Colour Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, 0f, -5f);
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

        private static bool IsBackground(Color32 p)
        {
            return p.r < 16 && p.g < 16 && p.b < 16;
        }

        private static bool IsGrey(Color32 p)
        {
            return !IsBackground(p) && Mathf.Abs(p.r - p.g) <= 12 && Mathf.Abs(p.g - p.b) <= 12 && Mathf.Abs(p.r - p.b) <= 12;
        }

        private static bool IsGreenish(Color32 p)
        {
            return p.g > p.r + 30 && p.g > p.b + 30;
        }

        private static bool IsRedish(Color32 p)
        {
            return p.r > p.g + 30 && p.r > p.b + 30;
        }

        /// <summary>Counts the pixels of one half of the image that satisfy <paramref name="test"/>.</summary>
        private static int CountIn(Color32[] pixels, bool leftHalf, System.Func<Color32, bool> test)
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

        private static int Covered(Color32[] pixels, bool leftHalf)
        {
            return CountIn(pixels, leftHalf, p => !IsBackground(p));
        }

        /// <summary>
        /// One surface triangle on the left and one capped triangle on the right, drawn through the Stage 3 pass. The
        /// body runs with the two uploaded once, so a caller can change globals and render again without transferring
        /// anything.
        /// </summary>
        private void WithSurfaceAndCap(Material material, System.Action<System.Func<Color32[]>, VpGpuIndexedGeometryBuffers> body)
        {
            Camera camera = FrontView(out RenderTexture target);
            using (var pool = new VpCpuGeometryPool(8, 8, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(8, 8))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 2))
            {
                Assert.That(pool.TryAppend(Triangle(k_leftTriangle, SurfaceUv), out VpGeometryRange surface), Is.True, "append the surface");
                Assert.That(pool.TryAppend(Triangle(k_rightTriangle, CapUv), out VpGeometryRange cap), Is.True, "append the cap");
                Assert.That(buffers.TryUpload(pool), Is.True, "geometry upload");

                var bounds = new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f));
                var commands = new[] { new VpIndirectCommand(surface, bounds, 1), new VpIndirectCommand(cap, bounds, 1) };
                var transforms = new[] { Matrix4x4.identity, Matrix4x4.identity };
                Assert.That(batch.TryUpload(commands, transforms, false), Is.True, "batch upload");

                var properties = new MaterialPropertyBlock();
                body(
                    () =>
                    {
                        batch.RenderForward(material, properties, buffers, 0, 0, batch.CommandCount, camera);
                        return RenderAndRead(camera, target);
                    },
                    buffers);
            }
        }

        [Test]
        public void ASurfaceUv_IsTextured_AndTheReservedSlotUsesTheCutSurfaceColour()
        {
            Material material = ForwardMaterial(Color.red, Color.white);
            WithSurfaceAndCap(material, (render, buffers) =>
            {
                Color32[] pixels = render();
                Assert.That(Covered(pixels, true), Is.GreaterThan(100), "the surface covers pixels");
                Assert.That(Covered(pixels, false), Is.GreaterThan(100), "and so does the cap");

                Assert.That(CountIn(pixels, true, IsRedish), Is.GreaterThan(100), "the surface is the texture's red");
                Assert.That(CountIn(pixels, false, IsRedish), Is.Zero, "the cap takes none of it");
                Assert.That(CountIn(pixels, false, IsGrey), Is.GreaterThan(100), "the cap is the grey cut surface colour");
            });
        }

        /// <summary>The cap's colour comes from neither the texture nor the material's own colour.</summary>
        [Test]
        public void TheCapColour_IgnoresTheTextureAndTheMaterialColour()
        {
            Material material = ForwardMaterial(Color.red, Color.white);
            Color32[] first = null;
            WithSurfaceAndCap(material, (render, buffers) => first = render());

            // a different texture and a different material colour
            Material changed = ForwardMaterial(Color.blue, new Color(0.2f, 0.9f, 1f));
            Color32[] second = null;
            WithSurfaceAndCap(changed, (render, buffers) => second = render());

            Assert.That(CountIn(first, false, IsGrey), Is.GreaterThan(100), "the cap was grey");
            Assert.That(CountIn(second, false, IsGrey), Is.GreaterThan(100), "and is still grey");
            Assert.That(CountIn(second, false, IsRedish), Is.Zero, "with nothing of either texture");
            Assert.That(CountIn(first, true, IsRedish), Is.GreaterThan(100), "while the surface followed the first texture");
            Assert.That(CountIn(second, true, IsRedish), Is.Zero, "and then the second");
        }

        /// <summary>
        /// The marker is the raw uv0. A UV transform that pushes the transformed coordinates negative does not make a
        /// surface into a cap, and one that pushes a cap's coordinates positive does not make it a surface.
        /// </summary>
        [Test]
        public void TheUvTransform_DoesNotChangeWhatCountsAsACap()
        {
            Material material = ForwardMaterial(Color.red, Color.white);
            Color32[] plain = null;
            WithSurfaceAndCap(material, (render, buffers) => plain = render());

            Material transformed = ForwardMaterial(Color.red, Color.white);
            transformed.SetTextureOffset("_BaseMap", new Vector2(-8f, -8f));
            transformed.SetTextureScale("_BaseMap", new Vector2(-1f, -1f));
            Color32[] moved = null;
            WithSurfaceAndCap(transformed, (render, buffers) => moved = render());

            Assert.That(CountIn(moved, true, IsRedish), Is.GreaterThan(100), "the surface is still the texture, however its UVs are transformed");
            Assert.That(CountIn(moved, false, IsGrey), Is.GreaterThan(100), "and the cap is still the cut surface colour");
            Assert.That(CountIn(moved, false, IsRedish), Is.Zero, "with no texture on it");
            Assert.That(CountIn(plain, true, IsRedish), Is.GreaterThan(100), "as it was without the transform");
        }

        /// <summary>
        /// The one debug switch turns every real cap green and leaves ordinary surfaces alone — and it does so without
        /// anything being uploaded again: the same buffers, unchanged, draw a different colour.
        /// </summary>
        [Test]
        public void TheDebugSwitch_TurnsCapsGreen_WithoutTransferringAnything()
        {
            Material material = ForwardMaterial(Color.red, Color.white);
            WithSurfaceAndCap(material, (render, buffers) =>
            {
                Color32[] normal = render();
                Assert.That(CountIn(normal, false, IsGrey), Is.GreaterThan(100), "the cap starts grey");
                Assert.That(CountIn(normal, false, IsGreenish), Is.Zero, "and not green");

                // what the buffers hold before the switch
                var verticesBefore = new VpRenderVertex[buffers.VertexCapacity];
                var indicesBefore = new uint[buffers.IndexCapacity];
                buffers.VertexBuffer.GetData(verticesBefore);
                buffers.IndexBuffer.GetData(indicesBefore);

                VpCutSurfaceColour.SetDebugEnabled(true);
                Color32[] debug = render();

                Assert.That(CountIn(debug, false, IsGreenish), Is.GreaterThan(100), "the cap is green while debugging");
                Assert.That(CountIn(debug, false, IsGrey), Is.Zero, "and no longer grey");
                Assert.That(CountIn(debug, true, IsRedish), Is.GreaterThan(100), "the ordinary surface is untouched");
                Assert.That(CountIn(debug, true, IsGreenish), Is.Zero, "and is certainly not green");

                // nothing was transferred to say so: the buffers are bit for bit what they were
                var verticesAfter = new VpRenderVertex[buffers.VertexCapacity];
                var indicesAfter = new uint[buffers.IndexCapacity];
                buffers.VertexBuffer.GetData(verticesAfter);
                buffers.IndexBuffer.GetData(indicesAfter);
                Assert.That(indicesAfter, Is.EqualTo(indicesBefore), "the indices are unchanged");
                for (int v = 0; v < verticesAfter.Length; v++)
                {
                    Assert.That(verticesAfter[v].position, Is.EqualTo(verticesBefore[v].position), "vertex " + v + " position");
                    Assert.That(verticesAfter[v].uv0, Is.EqualTo(verticesBefore[v].uv0), "vertex " + v + " uv0");
                }

                VpCutSurfaceColour.SetDebugEnabled(false);
                Color32[] back = render();
                Assert.That(CountIn(back, false, IsGrey), Is.GreaterThan(100), "and it goes back to grey");
            });
        }

        /// <summary>
        /// Nobody in this test applies the defaults: by the time it runs they are already in place through the
        /// product's own path, and a cut face drawn now is grey rather than the black of a fresh domain's zeros.
        /// <para>
        /// What this does **not** show is which entry point put them there — the fixture's <c>[SetUp]</c> would have
        /// been enough on its own. The entry points' own behaviour is what the three tests after it check.
        /// </para>
        /// </summary>
        [Test]
        public void TheDefaults_AreInPlaceWithoutAnyoneApplyingThem()
        {
            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceColor"), Is.EqualTo(VpCutSurfaceColour.DefaultColour), "the ordinary colour is set");
            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceDebugColor"), Is.EqualTo(VpCutSurfaceColour.DefaultDebugColour), "and the debug colour");
            Assert.That(Shader.GetGlobalFloat("_VpCutSurfaceDebug"), Is.Zero, "with the switch off");

            Material material = ForwardMaterial(Color.red, Color.white);
            WithSurfaceAndCap(material, (render, buffers) =>
            {
                Color32[] pixels = render();
                int covered = Covered(pixels, false);
                Assert.That(covered, Is.GreaterThan(100), "the cap covers pixels");
                Assert.That(CountIn(pixels, false, IsGrey), Is.EqualTo(covered), "and every one of them is grey, not the black of an unset global");
            });
        }

        /// <summary>
        /// The switch is not reset by anything that comes later: another display drawn afterwards is still green, and
        /// the product's own initialization, asked again, leaves what was set alone.
        /// </summary>
        [Test]
        public void AddingAnotherDisplay_KeepsTheDebugSwitch()
        {
            Material material = ForwardMaterial(Color.red, Color.white);
            VpCutSurfaceColour.SetDebugEnabled(true);

            WithSurfaceAndCap(material, (render, buffers) =>
            {
                Assert.That(CountIn(render(), false, IsGreenish), Is.GreaterThan(100), "the first display draws its cap green");
            });

            // a second display, built and drawn after the switch was set
            VpCutSurfaceColour.EnsureInitialized();
            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True, "initialization does not take the switch back");

            Material second = ForwardMaterial(Color.blue, Color.white);
            WithSurfaceAndCap(second, (render, buffers) =>
            {
                Color32[] pixels = render();
                Assert.That(CountIn(pixels, false, IsGreenish), Is.GreaterThan(100), "and so does the one added after it");
                Assert.That(CountIn(pixels, true, IsGreenish), Is.Zero, "while its ordinary surface is not green");
            });

            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True, "and drawing did not take it back either");
        }

        /// <summary>
        /// Turning the switch on can be the first thing that ever happens to this class. It still has to leave all
        /// three globals at values the product chose: a caller that asks only about the switch must not end up with
        /// caps drawn in whatever the debug colour happened to hold.
        /// </summary>
        [Test]
        public void TheSwitchTurnedOnFirst_StillGetsTheDefaultColours()
        {
            // somewhere no default could be mistaken for, and then forget that anything was ever set
            Shader.SetGlobalColor("_VpCutSurfaceColor", Color.black);
            Shader.SetGlobalColor("_VpCutSurfaceDebugColor", Color.black);
            Shader.SetGlobalFloat("_VpCutSurfaceDebug", 0f);
            VpCutSurfaceColour.ForgetInitializationForTests();

            VpCutSurfaceColour.SetDebugEnabled(true);

            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceDebugColor"), Is.EqualTo(VpCutSurfaceColour.DefaultDebugColour), "the default green went in first");
            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceColor"), Is.EqualTo(VpCutSurfaceColour.DefaultColour), "and the ordinary colour with it");
            Assert.That(Shader.GetGlobalFloat("_VpCutSurfaceDebug"), Is.EqualTo(1f), "with the switch the caller asked for");

            Material material = ForwardMaterial(Color.red, Color.white);
            WithSurfaceAndCap(material, (render, buffers) =>
            {
                Assert.That(CountIn(render(), false, IsGreenish), Is.GreaterThan(100), "so the cap draws green, not black");
            });
        }

        /// <summary>
        /// The same the other way round: setting the colours first must not mark everything initialized while the
        /// switch is still whatever was left in the globals. It goes to the default — off — and only the two colours
        /// asked for are the caller's.
        /// </summary>
        [Test]
        public void TheColoursSetFirst_LeaveTheSwitchAtAKnownValue()
        {
            Shader.SetGlobalColor("_VpCutSurfaceColor", Color.black);
            Shader.SetGlobalColor("_VpCutSurfaceDebugColor", Color.black);
            Shader.SetGlobalFloat("_VpCutSurfaceDebug", 1f);
            VpCutSurfaceColour.ForgetInitializationForTests();

            VpCutSurfaceColour.SetColours(Color.magenta, Color.cyan);

            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceColor"), Is.EqualTo((Color)Color.magenta), "the colour asked for");
            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceDebugColor"), Is.EqualTo((Color)Color.cyan), "and the debug colour asked for");
            Assert.That(Shader.GetGlobalFloat("_VpCutSurfaceDebug"), Is.Zero, "with the switch at the default rather than the leftover");
        }

        /// <summary>
        /// A play session starts from the defaults even when this class is already initialized — which is how it comes
        /// out of edit mode when Domain Reload is off and the statics survive. Asking for initialization does nothing
        /// in that state, so the session start cannot be built on that.
        /// </summary>
        [Test]
        public void APlaySessionStart_GoesBackToTheDefaults_EvenWhenAlreadyInitialized()
        {
            VpCutSurfaceColour.SetColours(Color.magenta, Color.cyan);
            VpCutSurfaceColour.SetDebugEnabled(true);
            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True, "edit mode left the switch on");

            VpCutSurfaceColour.EnsureInitialized();
            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True, "and asking for initialization leaves it on");
            Assert.That(VpCutSurfaceColour.Current, Is.EqualTo((Color)Color.cyan), "with the colours as they were set");

            VpCutSurfaceColour.BeginPlaySession();

            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceColor"), Is.EqualTo(VpCutSurfaceColour.DefaultColour), "the session starts grey");
            Assert.That(Shader.GetGlobalColor("_VpCutSurfaceDebugColor"), Is.EqualTo(VpCutSurfaceColour.DefaultDebugColour), "with the default green");
            Assert.That(Shader.GetGlobalFloat("_VpCutSurfaceDebug"), Is.Zero, "and the switch off, so edit mode's does not leak in");
        }

        [Test]
        public void TheOwner_KeepsTheColoursAndTheSwitch_AndPutsThemBack()
        {
            VpCutSurfaceColour.State before = VpCutSurfaceColour.Capture();
            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.False, "the defaults start with the switch off");
            Assert.That(VpCutSurfaceColour.Current, Is.EqualTo(VpCutSurfaceColour.DefaultColour), "and the ordinary colour");

            VpCutSurfaceColour.SetColours(Color.magenta, Color.cyan);
            VpCutSurfaceColour.SetDebugEnabled(true);
            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True);
            Assert.That(VpCutSurfaceColour.Current, Is.EqualTo((Color)Color.cyan), "the debug colour is what a cap takes now");

            VpCutSurfaceColour.Restore(before);
            Assert.That(VpCutSurfaceColour.DebugEnabled, Is.False, "the switch is back");
            Assert.That(VpCutSurfaceColour.Current, Is.EqualTo(before.colour), "and so is the colour");
        }
    }
}
