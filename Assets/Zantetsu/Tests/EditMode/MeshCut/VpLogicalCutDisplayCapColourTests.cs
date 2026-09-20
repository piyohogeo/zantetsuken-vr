using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The colour a temporary cut face is drawn in (DESIGN 5.3), connected to the display: the shared ordinary colour in
    /// an ordinary view, red while the one debug switch is on, and the same colour in every colour of a preparation,
    /// the last one included. The choice is read once per camera preparation, so it reaches a camera at its next
    /// preparation. Nothing about the classification, what is issued, the geometry or the sections changes with it.
    /// The colour then goes through the one shading the body and the real caps use, with each cap's own outward normal;
    /// the common toon shading of 5.3 -- its steps, outline and light response -- is still to come.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private static readonly Color OrdinaryUnderTest = new Color(0.1f, 0.2f, 0.85f, 1f);
        private static readonly Color RealCapDebugUnderTest = new Color(0.1f, 0.8f, 0.2f, 1f);

        /// <summary>
        /// One cut, one cap, one camera. With the switch off the cap is drawn in the ordinary cut surface colour -- set
        /// here to a blue of its own, so the pixel says which colour definition was read -- and never red. With the
        /// switch on the same cap is red, and the real caps' debug colour (green) is not what it takes. Off again, it is
        /// the ordinary colour once more. The colour reaches the camera at its next preparation.
        /// </summary>
        [Test]
        public void TheProvisionalCap_IsTheOrdinaryColour_AndRedOnlyWhileTheSwitchIsOn()
        {
            using (Scene scene = OneCapScene(out Camera camera))
            {
                VpCutSurfaceColour.SetColours(OrdinaryUnderTest, RealCapDebugUnderTest);
                VpCutSurfaceColour.SetDebugEnabled(false);
                Assert.That(Same(VpCutSurfaceColour.CurrentProvisional, OrdinaryUnderTest), Is.True, "off: the shared ordinary colour");

                Color32 ordinary = CapPixel(scene, camera, "the switch off");
                Assert.That(IsRed(ordinary), Is.False, "the ordinary view is not red");
                AssertLooksLike(ordinary, OrdinaryUnderTest, "the switch off");

                VpCutSurfaceColour.SetDebugEnabled(true);
                Assert.That(Same(VpCutSurfaceColour.CurrentProvisional, VpCutSurfaceColour.DefaultProvisionalDebugColour), Is.True);
                Assert.That(Same(VpCutSurfaceColour.Current, RealCapDebugUnderTest), Is.True, "a real cap still reads its own debug colour");
                Color32 debug = CapPixel(scene, camera, "the switch on");
                Assert.That(IsRed(debug), Is.True, "the debug view is red");

                VpCutSurfaceColour.SetDebugEnabled(false);
                Color32 back = CapPixel(scene, camera, "the switch off again");
                AssertLooksLike(back, OrdinaryUnderTest, "the switch off again");
            }
        }

        /// <summary>
        /// Changing the ordinary colour changes what both kinds of cut surface read, and changing the real caps' debug
        /// colour never reaches a temporary one: with the switch on it is red whatever that other colour is. Asking for
        /// initialisation does not take back what was set.
        /// </summary>
        [Test]
        public void TheOrdinaryColourIsShared_TheRealCapsDebugColourIsNot()
        {
            using (Scene scene = OneCapScene(out Camera camera))
            {
                var ordinary = new Color(0.9f, 0.85f, 0.1f, 1f);
                VpCutSurfaceColour.SetColours(ordinary, RealCapDebugUnderTest);
                VpCutSurfaceColour.SetDebugEnabled(false);
                Assert.That(Same(VpCutSurfaceColour.CurrentProvisional, ordinary), Is.True, "the temporary face follows the ordinary colour");
                Assert.That(Same(VpCutSurfaceColour.Current, ordinary), Is.True, "and so does the real cap");
                AssertLooksLike(CapPixel(scene, camera, "a changed ordinary colour"), ordinary, "a changed ordinary colour");

                VpCutSurfaceColour.SetColours(ordinary, new Color(0f, 1f, 0f, 1f));
                VpCutSurfaceColour.SetDebugEnabled(true);
                Assert.That(Same(VpCutSurfaceColour.CurrentProvisional, VpCutSurfaceColour.DefaultProvisionalDebugColour), Is.True,
                    "the real caps' debug colour is not the temporary face's");
                Assert.That(IsRed(CapPixel(scene, camera, "the real caps' debug colour changed")), Is.True);

                // Initialisation leaves what has been set alone, so the colour the next preparation reads does not move.
                VpCutSurfaceColour.EnsureInitialized();
                Assert.That(VpCutSurfaceColour.DebugEnabled, Is.True);
                Assert.That(Same(VpCutSurfaceColour.Current, new Color(0f, 1f, 0f, 1f)), Is.True);
                Assert.That(Same(VpCutSurfaceColour.CurrentProvisional, VpCutSurfaceColour.DefaultProvisionalDebugColour), Is.True);
                Assert.That(IsRed(CapPixel(scene, camera, "after EnsureInitialized")), Is.True);
            }
        }

        /// <summary>
        /// Every colour of a preparation carries the same temporary-cut-face colour -- one ordinary colour, an ordinary
        /// colour with the last one (T3 under a limit of 2), and everything in the last colour (a limit of 1) -- and
        /// switching the colour changes nothing about the classification or what is issued, nor does it transfer any
        /// geometry or take any section again.
        /// </summary>
        [Test]
        public void EveryColourOfAPreparation_TakesTheSameColour_AndNothingElseChangesWithIt()
        {
            foreach (int colours in new[] { 4, 2, 1 })
            {
                using (Scene scene = T3Scene(colours, out _))
                {
                    VpLogicalCutDisplay display = scene.display;
                    Camera camera = T3Camera();
                    Assert.That(display.TryRegisterCamera(camera), Is.True);
                    string what = "limit " + colours;

                    VpCutSurfaceColour.SetColours(OrdinaryUnderTest, RealCapDebugUnderTest);
                    VpCutSurfaceColour.SetDebugEnabled(false);
                    Assert.That(display.TryPrepareCamera(camera), Is.True);
                    VpStencilPreparation ordinary = PreparationOf(display, camera);
                    AssertEveryPreparedColourIs(display, camera, ordinary, OrdinaryUnderTest, what + ", the switch off");
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int sections = display.CapPolygonBuilds;

                    VpCutSurfaceColour.SetDebugEnabled(true);
                    Assert.That(display.TryPrepareCamera(camera), Is.True);
                    VpStencilPreparation debug = PreparationOf(display, camera);
                    AssertEveryPreparedColourIs(display, camera, debug, VpCutSurfaceColour.DefaultProvisionalDebugColour, what + ", the switch on");

                    Assert.That(debug.jobs, Is.EqualTo(ordinary.jobs), what + ": the same jobs");
                    Assert.That(debug.volumeGroups, Is.EqualTo(ordinary.volumeGroups), what);
                    Assert.That(debug.ordinaryVolumeGroups, Is.EqualTo(ordinary.ordinaryVolumeGroups), what);
                    Assert.That(debug.lastColourRenderFragments, Is.EqualTo(ordinary.lastColourRenderFragments), what);
                    Assert.That(debug.lastColourCaps, Is.EqualTo(ordinary.lastColourCaps), what);
                    Assert.That(debug.colours, Is.EqualTo(ordinary.colours), what);
                    Assert.That(debug.volumeCommands, Is.EqualTo(ordinary.volumeCommands), what);
                    Assert.That(debug.capsDrawn, Is.EqualTo(ordinary.capsDrawn), what);
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), what + ": no geometry transferred for a colour");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), what);
                    Assert.That(display.CapPolygonBuilds, Is.EqualTo(sections), what + ": no section taken again");
                    Assert.That(ordinary.colours, Is.GreaterThan(0), what + ": the layout: something is drawn");
                    if (colours == 1)
                    {
                        Assert.That(ordinary.lastColourCaps, Is.GreaterThan(0), what + ": the layout: the last colour is used");
                    }

                    display.Render(0, camera);
                    Read(camera);
                }
            }
        }

        /// <summary>Every colour the camera is prepared with carries <paramref name="expected"/> as its cap colour.</summary>
        private static void AssertEveryPreparedColourIs(
            VpLogicalCutDisplay display, Camera camera, in VpStencilPreparation preparation, Color expected, string what)
        {
            Assert.That(preparation.colours, Is.GreaterThan(0), what);
            for (int c = 0; c < preparation.colours; c++)
            {
                Assert.That(display.TryGetPreparedColor(camera, c, out VpStencilCapColor colour), Is.True, what + ": colour " + c);
                Assert.That(Same(colour.capColour, expected), Is.True, what + ": colour " + c + " is the temporary cut face's colour");
            }
        }

        /// <summary>
        /// Two caps of one body facing different ways, seen in one view. With the debug switch on they are the only red
        /// pixels in the image, so they can be found without guessing where they are: their brightnesses fall into more
        /// than one value, which is the shared shading applied to each cap with its own outward normal, and none is the
        /// flat base colour. A normal is on the GPU for every cap vertex of the adopted snapshot, and turning the colour
        /// on and off does not change which pixels are covered.
        /// </summary>
        [Test]
        public void CapsFacingDifferentWays_AreShadedApart_AndNoneIsTheFlatColour()
        {
            using (Scene scene = TwoFacingCapsScene(out Camera camera))
            {
                VpLogicalCutDisplay display = scene.display;
                VpCutSurfaceColour.SetColours(OrdinaryUnderTest, RealCapDebugUnderTest);
                VpCutSurfaceColour.SetDebugEnabled(false);
                Color32[] ordinary = DrawAgain(scene, camera, "the switch off");
                Assert.That(display.CapNormalCount, Is.EqualTo(display.AdoptedSnapshot.CapVertexCount), "a normal per cap vertex");
                Assert.That(display.CapNormalCount, Is.GreaterThan(0), "the layout: caps are prepared");

                VpCutSurfaceColour.SetDebugEnabled(true);
                Color32[] debug = DrawAgain(scene, camera, "the switch on");

                // The caps are the only red in the image while the switch is on: every shade they take, and how many
                // pixels each covers.
                var shades = new Dictionary<int, int>();
                foreach (Color32 pixel in debug)
                {
                    if (IsRed(pixel))
                    {
                        shades[pixel.r] = shades.TryGetValue(pixel.r, out int n) ? n + 1 : 1;
                    }
                }

                Assert.That(shades.Count, Is.GreaterThan(0), "the layout: caps are seen in the debug colour");
                int brightest = 0;
                int darkest = 255;
                foreach (int shade in shades.Keys)
                {
                    brightest = Mathf.Max(brightest, shade);
                    darkest = Mathf.Min(darkest, shade);
                }

                Assert.That(shades.Count, Is.GreaterThan(1), "shaded apart: one shade only (" + brightest + ")");
                Assert.That(brightest - darkest, Is.GreaterThan(8), "the shades are apart: " + darkest + " to " + brightest);
                Assert.That(brightest, Is.LessThan(255), "the debug colour is shaded, not the flat base colour");
                Assert.That(darkest, Is.GreaterThan(60), "and not black");

                int ordinaryDrawn = 0;
                int debugDrawn = 0;
                for (int i = 0; i < ordinary.Length; i++)
                {
                    ordinaryDrawn += IsBackground(ordinary[i]) ? 0 : 1;
                    debugDrawn += IsBackground(debug[i]) ? 0 : 1;
                }

                Assert.That(debugDrawn, Is.EqualTo(ordinaryDrawn), "the same pixels are covered either way");
                Assert.That(NoneIsRed(ordinary), Is.True, "and nothing is red with the switch off");
            }
        }

        /// <summary>
        /// The cap normals reach the GPU with the body's upload, before the candidate is taken up: a write that throws
        /// leaves the display broken with the snapshot it had, not a new body beside normals of another build. The
        /// collection that throws adopts nothing -- the snapshot, its generation and the camera's preparation are the
        /// ones from before -- and the display refuses everything afterwards.
        /// </summary>
        [Test]
        public void TheCapNormalsUploadThrowing_StopsTheDisplay_WithoutAdoptingTheCandidate()
        {
            using (Scene scene = OneCapScene(out Camera camera))
            {
                VpLogicalCutDisplay display = scene.display;
                VpCutSurfaceColour.SetDebugEnabled(true);
                Collect(scene);
                Assert.That(display.TryPrepareCamera(camera), Is.True);
                display.Render(0, camera);
                Read(camera);
                VpMultiCutSnapshot adopted = display.AdoptedSnapshot;
                long generation = adopted.BuildGeneration;
                int caps = display.CapRecordCount;
                int normals = display.CapNormalCount;
                int uploads = display.CommandUploads;

                // The next collection: its normals fail to reach the GPU.
                _frame++;
                display.RefuseCapNormalUploadForTest = () => true;
                Assert.Throws<InvalidOperationException>(() => display.TryBeginFrame());
                display.RefuseCapNormalUploadForTest = null;

                Assert.That(display.IsBroken, Is.True, "the display stops: what reached the GPU cannot be established");
                Assert.That(display.AdoptedSnapshot, Is.SameAs(adopted), "the snapshot it had is still the adopted one");
                Assert.That(display.AdoptedSnapshot.BuildGeneration, Is.EqualTo(generation), "and its generation did not move");
                Assert.That(display.CapRecordCount, Is.EqualTo(caps));
                Assert.That(display.CapNormalCount, Is.EqualTo(normals), "no normal count from the candidate");
                Assert.That(display.CommandUploads, Is.EqualTo(uploads), "the body was not counted as uploaded");
                Assert.Throws<InvalidOperationException>(() => display.TryPrepareCamera(camera), "a broken display prepares nothing");
                Assert.Throws<InvalidOperationException>(() => display.Render(0, camera), "and draws nothing");
            }
        }

        private static bool NoneIsRed(Color32[] image)
        {
            foreach (Color32 pixel in image)
            {
                if (IsRed(pixel))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// T3 seen from below along (1, 1, 1): the corner piece shows its three caps at once, facing -x, -y and -z, and
        /// the other pieces show theirs. Caps of several different outward normals are therefore in one view, which is
        /// what the shading is read from.
        /// </summary>
        private Scene TwoFacingCapsScene(out Camera camera)
        {
            // Opened: the corner's caps are only in this view because that piece stands away from the others.
            Scene scene = T3SceneOpened(4, out _);
            camera = T3Camera();
            Assert.That(scene.display.TryRegisterCamera(camera), Is.True);
            return scene;
        }

        /// <summary>Settles the next frame, prepares and draws, and gives back the image.</summary>
        private Color32[] DrawAgain(Scene scene, Camera camera, string what)
        {
            Collect(scene);
            Assert.That(scene.display.TryPrepareCamera(camera), Is.True, what + ": prepared");
            scene.display.Render(0, camera);
            return Read(camera);
        }

        private static bool IsBackground(Color32 c)
        {
            return c.r < 12 && c.g < 12 && c.b < 12;
        }

        /// <summary>Two colours, channel by channel, as the globals hold them.</summary>
        private static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 1e-5f && Mathf.Abs(a.g - b.g) < 1e-5f && Mathf.Abs(a.b - b.b) < 1e-5f
                && Mathf.Abs(a.a - b.a) < 1e-5f;
        }

        /// <summary>A cut cube with one cap the camera sees, prepared and drawn afresh by <see cref="CapPixel"/>.</summary>
        private Scene OneCapScene(out Camera camera)
        {
            Scene scene = NewScene();
            LogicalCutLedger ledger = scene.ledger;
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            Assert.That(scene.display.TryShow(root, AppendCube(scene.storage, false), Matrix4x4.identity), Is.True);
            Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            Collect(scene);
            camera = Looking(new Vector3(0f, 0.5f, 0f), Vector3.down, 1.5f);
            Assert.That(scene.display.TryRegisterCamera(camera), Is.True);
            return scene;
        }

        /// <summary>
        /// Settles the next frame -- a camera that has drawn is not prepared again in the same one -- prepares the camera,
        /// which is when the colour is read, draws, and gives back the pixel at the middle of the cap it looks down on.
        /// </summary>
        private Color32 CapPixel(Scene scene, Camera camera, string what)
        {
            VpLogicalCutDisplay display = scene.display;
            Collect(scene);
            Assert.That(display.TryPrepareCamera(camera), Is.True, what + ": prepared");
            display.Render(0, camera);
            Color32[] image = Read(camera);
            return At(image, camera, new Vector3(0f, 0f, 0f));
        }

        /// <summary>
        /// The pixel is the colour asked for, as the pipeline wrote it: the channels are compared in the order of their
        /// sizes and the brightest channel is where the colour's is, which is what tells one test colour from another
        /// without turning this into a check of the colour space.
        /// </summary>
        private static void AssertLooksLike(Color32 pixel, Color expected, string what)
        {
            int[] channels = { pixel.r, pixel.g, pixel.b };
            float[] wanted = { expected.r, expected.g, expected.b };
            int brightest = 0;
            int wantedBrightest = 0;
            for (int i = 1; i < 3; i++)
            {
                brightest = channels[i] > channels[brightest] ? i : brightest;
                wantedBrightest = wanted[i] > wanted[wantedBrightest] ? i : wantedBrightest;
            }

            Assert.That(brightest, Is.EqualTo(wantedBrightest), what + ": the brightest channel of " + pixel + " is the colour's");
            Assert.That(channels[brightest], Is.GreaterThan(60), what + ": the cap is drawn at all (" + pixel + ")");
        }
    }
}
