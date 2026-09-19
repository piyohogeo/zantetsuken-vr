using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The shadow side of DESIGN 5.4: a body drawn as a provisional split casts two-sided, every other body casts
    /// one-sided, and the two are separate draws of the same commands.
    /// <para>
    /// The fixture is a closed box cut by a vertical plane, with the light shining INTO the opening of the side that
    /// stays put. That arrangement is what makes the difference visible rather than asserted: with a one-sided caster
    /// the faces nearest the light have been clipped away and the shell's far faces are culled, so light passes
    /// through the body and its shadow has a hole; with a two-sided caster those far faces are written and the shadow
    /// is whole. The other side's opening faces away from the light, so it casts the same either way and stands as a
    /// control inside the same image.
    /// </para>
    /// <para>
    /// Nothing here reads a Cull value back off a material. Every judgement is made on the pixels of a lit ground.
    /// </para>
    /// </summary>
    public sealed class VpLogicalCutDisplayShadowTests
    {
        private const int Size = 160;
        private const int ControlPoints = 8;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const float Epsilon = 0.01f;

        /// <summary>The cut: the world plane x = 0, straight through the box.</summary>
        private static readonly float4 k_plane = new float4(1f, 0f, 0f, 0f);

        /// <summary>An anchor on the negative side, which fixes that side and leaves the positive one to move.</summary>
        private static readonly float3 k_negativeAnchor = new float3(-0.3f, 0f, 0f);

        private const float Separation = 1.1f;

        private readonly List<Object> _made = new List<Object>();
        private int _frame = 1;

        private T Track<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _made.Count - 1; i >= 0; i--)
            {
                if (_made[i] != null)
                {
                    Object.DestroyImmediate(_made[i]);
                }
            }

            _made.Clear();
            _frame = 1;
        }

        /// <summary>
        /// The split's fixed side, whose opening faces the light, is only properly occluded by the two-sided caster.
        /// Three images of one fixture decide it: the ground with no body at all, the ground with the split cast
        /// one-sided, and the ground with it cast two-sided. The one-sided image must let light through where the
        /// two-sided one does not.
        /// </summary>
        [Test]
        public void TheOpeningFacingTheLight_IsOccludedOnlyByTheTwoSidedCaster()
        {
            float lit = Mean(Draw(Scene.Empty));
            Color32[] oneSided = Draw(Scene.SplitCastOneSided);
            Color32[] twoSided = Draw(Scene.SplitCastTwoSided);
            float one = Mean(oneSided);
            float two = Mean(twoSided);

            // Over the whole image the difference can only be small -- the shadow is a few percent of a wide ground
            // -- so this says the direction only, and where it comes from is decided below.
            Assert.That(one, Is.LessThan(lit - 1f), "a split body casts some shadow even one-sided");
            Assert.That(
                two, Is.LessThan(one),
                "the two-sided caster occludes at least as much: lit " + lit.ToString("F1") + ", one-sided "
                + one.ToString("F1") + ", two-sided " + two.ToString("F1"));

            // And where. Rather than a mean over a guessed rectangle, the two images are differenced: the two-sided
            // caster must shadow ground the one-sided one leaves lit, and must never leave lit any ground the
            // one-sided one shadows. A one-way difference of real area is what "the opening leaks" looks like.
            int darker = 0;
            int brighter = 0;
            double sumX = 0;
            for (int i = 0; i < oneSided.Length; i++)
            {
                float difference = Luminance(oneSided[i]) - Luminance(twoSided[i]);
                if (difference > 20f)
                {
                    darker++;
                    sumX += WorldX(i % Size);
                }
                else if (difference < -20f)
                {
                    brighter++;
                }
            }

            Assert.That(
                darker, Is.GreaterThan(200),
                "the two-sided caster shadows ground the one-sided one leaves lit (" + darker + " pixels)");
            Assert.That(
                brighter, Is.Zero,
                "and leaves nothing lit that the one-sided one shadowed (" + brighter + " pixels)");

            // That ground lies away from the light, behind the side whose opening faces it: the light travels towards
            // -x, so what the opening stopped letting through falls on the -x side of the body.
            double centre = sumX / darker;
            Assert.That(
                centre, Is.LessThan(0.0), "the newly shadowed ground is behind the fixed side, at x = "
                + centre.ToString("F2"));
        }

        /// <summary>
        /// The shadow is of what is drawn: the side that moves takes its shadow with it, and the side that does not
        /// keeps its own where it was. Widening the separation may only change the ground under the moving side.
        /// </summary>
        [Test]
        public void TheSideAndItsOffset_MoveTheShadowWithTheBody()
        {
            Color32[] near = Draw(Scene.SplitCastTwoSided, separation: 0.6f);
            Color32[] far = Draw(Scene.SplitCastTwoSided, separation: 1.6f);

            float movingNear = Mean(near, UnderMovingSideFar);
            float movingFar = Mean(far, UnderMovingSideFar);
            Assert.That(
                movingFar, Is.LessThan(movingNear - 4f),
                "the moved side's shadow follows it outward: near " + movingNear.ToString("F1") + ", far "
                + movingFar.ToString("F1"));

            float fixedNear = Mean(near, UnderFixedSide);
            float fixedFar = Mean(far, UnderFixedSide);
            Assert.That(
                Mathf.Abs(fixedFar - fixedNear), Is.LessThan(3f),
                "the fixed side's shadow stays where it was: " + fixedNear.ToString("F1") + " -> "
                + fixedFar.ToString("F1"));
        }

        /// <summary>
        /// A body with no cut prepared is cast one-sided, exactly as before: the two-sided caster is never issued for
        /// it, and the picture is the one a display given only the one-sided caster draws.
        /// </summary>
        [Test]
        public void AWholeBody_KeepsTheOneSidedPath()
        {
            Color32[] both = Draw(Scene.WholeBody);
            Color32[] oneOnly = Draw(Scene.WholeBodyCastOneSided);
            for (int i = 0; i < both.Length; i++)
            {
                if (both[i].r != oneOnly[i].r || both[i].g != oneOnly[i].g || both[i].b != oneOnly[i].b)
                {
                    Assert.Fail("pixel " + i + " of the whole body changed: " + oneOnly[i] + " -> " + both[i]);
                }
            }
        }

        /// <summary>
        /// What each kind of draw costs, and what each command is marked as. One shadow call per run of commands on
        /// one side of the division -- so a whole body and a split body shown together are two calls, one of each --
        /// and the surfaces, the stencil work and the geometry transfers are what they were without any shadow at all.
        /// </summary>
        [Test]
        public void TheTwoCasters_AreIssuedPerRun_AndCostNothingElse()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId split = ledger.AddFragment(new List<float3> { k_negativeAnchor });
                LogicalFragmentId whole = ledger.AddFragment();
                VpStoredGeometry first = AppendBox(storage);
                VpStoredGeometry second = AppendBox(storage);
                Material one = ShadowMaterial(CullMode.Back);
                Material two = ShadowMaterial(CullMode.Off);
                Assert.That(TryCreate(storage, table, ledger, one, two, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = Separation;
                    Assert.That(display.TryShow(split, first, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryShow(whole, second, Matrix4x4.Translate(new Vector3(4f, 0f, 0f))), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    // Nothing is split yet: every command is on the one-sided side of the division.
                    Assert.That(display.CommandCount, Is.GreaterThan(0));
                    for (int c = 0; c < display.CommandCount; c++)
                    {
                        Assert.That(display.CommandCastsTwoSided(c), Is.False, "command " + c + " before any cut");
                    }

                    CutOperationId cut = Admit(ledger, split);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);

                    int twoSidedCommands = 0;
                    for (int c = 0; c < display.CommandCount; c++)
                    {
                        if (display.CommandCastsTwoSided(c))
                        {
                            twoSidedCommands++;
                        }
                    }

                    Assert.That(twoSidedCommands, Is.GreaterThan(0), "the split body's commands are marked");
                    Assert.That(
                        twoSidedCommands, Is.LessThan(display.CommandCount), "the whole body's commands are not");

                    Camera camera = TopDown();
                    Assert.That(display.TryRegisterCamera(camera), Is.True);
                    Assert.That(display.TryPrepareCamera(camera), Is.True);
                    Assert.That(
                        display.TryGetCameraStencil(camera, out VpStencilPreparation preparation, out VpStencilCameraCounts counts),
                        Is.True);
                    Assert.That(preparation.capRecords, Is.EqualTo(2), "the split is shown: one cap per side");

                    int uploads = display.CommandUploads;
                    int stencilUploads = display.StencilUploads;
                    int writes = display.StencilBufferWrites;
                    int vertices = display.VertexTransfers;
                    int indices = display.IndexTransfers;
                    int init = display.StencilInitIssues;
                    int caps = display.StencilCapIssues;
                    int oneSided = display.OneSidedShadowIssues;
                    int twoSided = display.TwoSidedShadowIssues;

                    display.Render(0, camera);

                    Assert.That(
                        display.OneSidedShadowIssues, Is.EqualTo(oneSided + 1),
                        "one call for the run of ordinary commands");
                    Assert.That(
                        display.TwoSidedShadowIssues, Is.EqualTo(twoSided + 1),
                        "and one for the run of provisionally split ones");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "drawing uploaded nothing");
                    Assert.That(display.StencilUploads, Is.EqualTo(stencilUploads));
                    Assert.That(display.StencilBufferWrites, Is.EqualTo(writes), "and wrote no buffer");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertices), "no geometry went again");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indices));
                    Assert.That(
                        display.StencilInitIssues, Is.EqualTo(init + preparation.colours),
                        "one initialisation per colour the preparation made");
                    Assert.That(display.StencilCapIssues, Is.EqualTo(caps + preparation.colours));
                    Read(camera);
                }

                // The display owns neither material: both are still here for the caller to destroy.
                Assert.That(one == null, Is.False, "the one-sided caster outlives the display");
                Assert.That(two == null, Is.False, "and so does the two-sided one");
            }
        }

        /// <summary>
        /// Casting at all means casting both ways, so the two casters come together or not at all. All four
        /// combinations are checked here, because either one alone fails quietly in its own way: with the one-sided
        /// caster alone a provisional split would cast a one-sided shadow that looks ordinary and is wrong, and with
        /// the two-sided caster alone nothing would cast at all, since a display with no one-sided caster issues no
        /// shadow call.
        /// </summary>
        [Test]
        public void EitherCasterAlone_IsRefused_AndNeitherOrBothIsAccepted()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                Material one = ShadowMaterial(CullMode.Back);
                Material two = ShadowMaterial(CullMode.Off);

                Assert.That(
                    TryCreate(storage, table, ledger, one, null, out VpLogicalCutDisplay withoutTwoSided), Is.False,
                    "the one-sided caster alone");
                Assert.That(withoutTwoSided, Is.Null);

                Assert.That(
                    TryCreate(storage, table, ledger, null, two, out VpLogicalCutDisplay withoutOneSided), Is.False,
                    "the two-sided caster alone, which would cast nothing at all");
                Assert.That(withoutOneSided, Is.Null);

                Assert.That(
                    TryCreate(storage, table, ledger, one, two, out VpLogicalCutDisplay both), Is.True,
                    "both casters");
                using (both)
                {
                    Assert.That(both.OneSidedShadowIssues, Is.Zero, "nothing is issued before anything is drawn");
                    Assert.That(both.TwoSidedShadowIssues, Is.Zero);
                }

                Assert.That(
                    TryCreate(storage, table, ledger, null, null, out VpLogicalCutDisplay noShadows), Is.True,
                    "casting no shadows at all is still allowed");
                using (noShadows)
                {
                    Assert.That(noShadows.OneSidedShadowIssues, Is.Zero);
                    Assert.That(noShadows.TwoSidedShadowIssues, Is.Zero);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------

        private enum Scene
        {
            Empty,
            WholeBody,
            WholeBodyCastOneSided,
            SplitCastOneSided,
            SplitCastTwoSided,
        }

        /// <summary>
        /// Draws one scene on a lit ground and reads it back. The caster the split is given is the only difference
        /// between the two split scenes: same geometry, same transform, same clips, same offsets.
        /// </summary>
        private Color32[] Draw(Scene scene, float separation = Separation)
        {
            Camera camera = TopDown();
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            var sun = new GameObject("VP Clip Shadow Test Sun");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            // Travelling towards -x and down, so it shines into the opening of the side that stays put.
            light.transform.rotation = Quaternion.Euler(55f, -90f, 0f);
            try
            {
                return DrawInto(scene, separation, camera);
            }
            finally
            {
                // The ground and the sun go with the drawing that made them. Left standing, the next drawing would
                // have two of each and the images would not be of the same scene at all.
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(sun);
            }
        }

        private Color32[] DrawInto(Scene scene, float separation, Camera camera)
        {
            if (scene == Scene.Empty)
            {
                return Read(camera);
            }

            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                bool splitting = scene == Scene.SplitCastOneSided || scene == Scene.SplitCastTwoSided;
                LogicalFragmentId source = splitting
                    ? ledger.AddFragment(new List<float3> { k_negativeAnchor })
                    : ledger.AddFragment();
                VpStoredGeometry geometry = AppendBox(storage);
                Material one = ShadowMaterial(CullMode.Back);
                Material provisional = scene == Scene.SplitCastTwoSided ? ShadowMaterial(CullMode.Off) : one;
                Assert.That(
                    TryCreate(storage, table, ledger, one, provisional, out VpLogicalCutDisplay display), Is.True);
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = separation;
                    Assert.That(display.TryShow(source, geometry, BodyPlacement), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    if (splitting)
                    {
                        CutOperationId cut = Admit(ledger, source);
                        Prepare(ledger, cut);
                        NextFrame();
                        Assert.That(display.TryBeginFrame(), Is.True);
                        Assert.That(
                            display.SideCount, Is.EqualTo(display.CommandCount * 2),
                            "a split draws each command twice, once per side: two sides per command");
                    }

                    Assert.That(display.TryRegisterCamera(camera), Is.True);
                    Assert.That(display.TryPrepareCamera(camera), Is.True);
                    display.Render(0, camera);
                    return Read(camera);
                }
            }
        }

        /// <summary>
        /// Where the box stands and how big it is. Big enough and high enough that what the opening lets through is a
        /// region of real area on the ground rather than a few pixels, and still inside the view at every separation
        /// these tests use.
        /// </summary>
        private static Matrix4x4 BodyPlacement =>
            Matrix4x4.TRS(new Vector3(0f, 1.4f, 0f), Quaternion.identity, Vector3.one * 1.6f);

        private Camera TopDown()
        {
            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            target.Create();
            Camera camera = Track(new GameObject("VP Clip Shadow Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 30f;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 12f, 0f), Quaternion.Euler(90f, 0f, 0f));
            return camera;
        }

        private Color32[] Read(Camera camera)
        {
            RenderTexture target = camera.targetTexture;
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                camera.Render();
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var read = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        private static float Luminance(Color32 p)
        {
            return (p.r + p.g + p.b) / 3f;
        }

        /// <summary>The world x of an image column, for an orthographic top-down view of size 4.</summary>
        private static float WorldX(int x)
        {
            return (x / (float)(Size - 1) - 0.5f) * 8f;
        }

        /// <summary>Mean brightness of the whole image, or of the pixels a region accepts.</summary>
        private static float Mean(Color32[] pixels, System.Func<int, int, bool> region = null)
        {
            double total = 0;
            int n = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (region != null && !region(i % Size, i / Size))
                {
                    continue;
                }

                total += (pixels[i].r + pixels[i].g + pixels[i].b) / 3.0;
                n++;
            }

            Assert.That(n, Is.GreaterThan(0), "the region holds pixels");
            return (float)(total / n);
        }

        /// <summary>
        /// The ground just beyond the fixed (negative) side, away from the light: where its shadow falls, and where a
        /// caster that lets light through the opening shows it.
        /// </summary>
        private static bool UnderFixedSide(int x, int y)
        {
            return InWorld(x, y, -2.2f, -0.9f, -0.5f, 0.5f);
        }

        /// <summary>The ground beyond where the moving side sits at the wider separation.</summary>
        private static bool UnderMovingSideFar(int x, int y)
        {
            return InWorld(x, y, 0.9f, 2.4f, -0.5f, 0.5f);
        }

        private static bool InWorld(int x, int y, float minX, float maxX, float minZ, float maxZ)
        {
            // Orthographic size 4 over 160 pixels, looking straight down with the camera's up along +z.
            float worldX = (x / (float)(Size - 1) - 0.5f) * 8f;
            float worldZ = (y / (float)(Size - 1) - 0.5f) * 8f;
            return worldX >= minX && worldX <= maxX && worldZ >= minZ && worldZ <= maxZ;
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }

        /// <summary>A closed unit box, the six faces wound outwards, in two submeshes so it resolves two materials.</summary>
        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage)
        {
            float3[] corners =
            {
                new float3(-0.5f, -0.5f, -0.5f), new float3(0.5f, -0.5f, -0.5f),
                new float3(0.5f, -0.5f, 0.5f), new float3(-0.5f, -0.5f, 0.5f),
                new float3(-0.5f, 0.5f, -0.5f), new float3(0.5f, 0.5f, -0.5f),
                new float3(0.5f, 0.5f, 0.5f), new float3(-0.5f, 0.5f, 0.5f),
            };
            (int[] cycle, int submesh)[] faces =
            {
                (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0),
                (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
                (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
            };

            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                foreach ((int[] cycle, int which) in faces)
                {
                    if (which != submesh)
                    {
                        continue;
                    }

                    float3 n = math.normalize(math.cross(
                        corners[cycle[1]] - corners[cycle[0]], corners[cycle[2]] - corners[cycle[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[]
                    {
                        new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f),
                    };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = corners[cycle[k]], normal = n, uv0 = uv[k] });
                        topology.Add(cycle[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), ControlPoints, submeshes.ToArray(),
                    out VpStoredGeometry geometry),
                Is.True);
            return geometry;
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null, "the VP unlit shader");
            var side = Track(new Material(shader) { name = "clip shadow side" });
            var end = Track(new Material(shader) { name = "clip shadow end" });
            side.SetColor("_BaseColor", new Color(0.7f, 0.7f, 0.7f));
            end.SetColor("_BaseColor", new Color(0.6f, 0.6f, 0.6f));
            return new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, end } };
        }

        private Material ShadowMaterial(CullMode cull)
        {
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
            Assert.That(shader, Is.Not.Null, "the VP indexed indirect shadow caster");
            var material = Track(new Material(shader) { name = "clip shadow caster " + cull });
            material.SetFloat("_Cull", (float)cull);
            return material;
        }

        private bool TryCreate(
            VpCpuGeometryStorage storage, VpGeometryReferenceTable table, LogicalCutLedger ledger,
            Material shadow, Material provisionalShadow, out VpLogicalCutDisplay display)
        {
            return VpLogicalCutDisplay.TryCreate(
                storage, table, ledger, Materials(), shadow, provisionalShadow, 16, 16,
                VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(),
                () => _frame, out display);
        }

        private void NextFrame()
        {
            _frame++;
        }

        private static LogicalCutLedger NewLedger(int limit = 4)
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(limit));
        }

        private static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId source)
        {
            Assert.That(
                ledger.Admit(source, k_plane, true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted));
            return operation;
        }

        private static void Prepare(LogicalCutLedger ledger, CutOperationId operation)
        {
            Assert.That(
                ledger.PrepareAnchorDistribution(operation, Epsilon, out _),
                Is.EqualTo(AnchorPreparationOutcome.Prepared));
        }
    }
}
