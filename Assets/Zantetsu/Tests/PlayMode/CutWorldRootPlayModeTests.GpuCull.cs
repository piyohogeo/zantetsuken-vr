using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// VP Stage 3C on the product's own path (DESIGN 4.5.7): a world made by <see cref="CutWorldRoot"/> with the GPU
    /// selection asked for, drawn by <see cref="CutWorldCameraDrawing"/> for an ordinary camera that renders every
    /// frame, so the selection is put into URP's frame by the product's own route and nothing here issues it.
    /// <para>
    /// **The same scenes, taken twice.** One world draws as VP Stage 3 and one through the GPU selection; each goes
    /// through the same five stages -- the whole body; the cut while its geometry is held back (the sides clipped,
    /// two-sided casters, the cut face drawn by the stencil caps); the committed children; a row of further bodies
    /// registered along the view, across every split of the shadow map and past its range; and one piece retired --
    /// and each stage is taken from the same five views: the bodies in view, the bodies out of view with their shadow
    /// in it, nothing in view, the first view again, and down the row. The display starts with room for two commands
    /// and two instances, so the body's GPU buffers are replaced by larger ones on the way. The fixture holds one world
    /// a case, so the two worlds are two cases and a third compares what they took; it fails, rather than passing, when
    /// either was not taken in this run.
    /// </para>
    /// <para>
    /// The second case also reads back -- which the product never does -- what the selection kept for each view: every
    /// instance where all are in view, none for the body and some as casters where only the shadow is in view, none
    /// for the body where nothing is, and fewer than all of both down the row, where bodies stand past the far plane
    /// and past the shadow's range.
    /// </para>
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const int GpuCullViews = 5;
        private const int GpuCullRowStage = 3;
        private static readonly string[] s_gpuCullStages = { "whole", "immediate", "committed", "row registered", "piece retired" };
        private static readonly string[] s_gpuCullViewNames = { "bodies in view", "only the shadow in view", "nothing in view", "bodies in view again", "down the row" };
        private static readonly Dictionary<bool, List<Color32[]>> s_gpuCullImages = new Dictionary<bool, List<Color32[]>>();

        [UnityTest, Order(1)]
        public IEnumerator GpuCull_1_VpStage3_TakesTheScenes()
        {
            yield return GpuCullScenes(false);
        }

        [UnityTest, Order(2)]
        public IEnumerator GpuCull_2_TheGpuSelection_TakesTheSameScenes_KeepingACasterTheCameraDoesNotSee()
        {
            yield return GpuCullScenes(true);
        }

        [Test, Order(3)]
        public void GpuCull_3_TheGpuSelectionsImages_AreVpStage3s()
        {
            Assert.That(s_gpuCullImages.ContainsKey(false), Is.True, "VP Stage 3's scenes were taken in this run");
            Assert.That(s_gpuCullImages.ContainsKey(true), Is.True, "the GPU selection's scenes were taken in this run");
            List<Color32[]> vp3 = s_gpuCullImages[false];
            List<Color32[]> culled = s_gpuCullImages[true];
            Assert.That(vp3.Count, Is.EqualTo(s_gpuCullStages.Length * GpuCullViews));
            Assert.That(culled.Count, Is.EqualTo(vp3.Count));
            var lines = new List<string>();
            int worst = 0;
            for (int i = 0; i < vp3.Count; i++)
            {
                int differing = 0;
                for (int p = 0; p < vp3[i].Length; p++)
                {
                    Color32 a = vp3[i][p];
                    Color32 b = culled[i][p];
                    if (Math.Abs(a.r - b.r) > 2 || Math.Abs(a.g - b.g) > 2 || Math.Abs(a.b - b.b) > 2)
                    {
                        differing++;
                    }
                }

                lines.Add(s_gpuCullStages[i / GpuCullViews] + ", " + s_gpuCullViewNames[i % GpuCullViews] + ": pixels differing " + differing);
                worst = Math.Max(worst, differing);
            }

            TestContext.Out.WriteLine(string.Join("\n", lines));
            Assert.That(worst, Is.Zero, "pixels differing between VP Stage 3 and the GPU selection:\n" + string.Join("\n", lines));
        }

        /// <summary>
        /// **What cannot select says so and draws as VP Stage 3.** A world asked for the GPU selection with a surface
        /// material whose shader has no variant for it is not made with it: the world is VP Stage 3's, and it names the
        /// reason, so that nothing measured in it is taken for the GPU selection's.
        /// </summary>
        [UnityTest, Order(4)]
        public IEnumerator GpuCull_4_AMaterialWithoutTheVariant_IsVpStage3_AndTheWorldSaysWhy()
        {
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpGpuCullSetup.Requested = true;
            try
            {
                Shader plain = Shader.Find("Universal Render Pipeline/Unlit");
                Assert.That(plain, Is.Not.Null);
                Shader vp = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
                CutWorldRoot root = NewWorld(
                    out Shader _, null, null, null,
                    world => SetPrivate(
                        world, "materials",
                        new[]
                        {
                            new CutWorldRoot.MaterialBinding { sourceIndex = SideMaterial, material = Track(new Material(vp) { name = "side" }) },
                            new CutWorldRoot.MaterialBinding { sourceIndex = EndMaterial, material = Track(new Material(plain) { name = "end without the variant" }) },
                        }));
                Assert.That(root.Display.CullsOnGpu, Is.False, "the world draws as VP Stage 3");
                Assert.That(root.GpuCullFallback, Does.Contain("end without the variant").And.Contain(VpGpuCullSetup.Keyword), "and says why");
                AddBody(root, new Vector3(0f, 1.5f, 0f));
                yield return null;
                yield return null;
                Assert.That(root.Display.CommandCount, Is.GreaterThan(0), "and goes on as a world does");
                Assert.That(GpuCullMaterialCopies(), Is.Zero, "nothing made for the selection is left");
                yield return EndWorld(root);
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
            }
        }

        private static int GpuCullMaterialCopies()
        {
            int copies = 0;
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                copies += material.name.EndsWith(" (GPU culled)", StringComparison.Ordinal) ? 1 : 0;
            }

            return copies;
        }

        private IEnumerator GpuCullScenes(bool gpu)
        {
            // The route is the one a Player's arguments give (D-198): its ordinary arguments are the GPU selection, and
            // VP Stage 3 is asked for by its own argument.
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpGpuCullSetup.Read(
                gpu ? new[] { "player.exe", "-zantetsuCityWalk" } : new[] { "player.exe", "-zantetsuCityWalk", VpGpuCullSetup.Vp3Argument },
                out bool requested, out bool _);
            Assert.That(requested, Is.EqualTo(gpu), gpu ? "no argument of its own: the GPU selection" : "VP Stage 3 asked for");
            VpGpuCullSetup.Requested = requested;
            try
            {
                s_gpuCullImages.Remove(gpu);
                Shader casterShader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Assert.That(casterShader, Is.Not.Null, "the display's shadow caster is in this project");
                Material stable = Track(new Material(casterShader) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(casterShader) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);

                HoldingExecutor geometryPool = null;
                CutWorldRoot root = NewWorld(
                    out Shader _,
                    destination => destination == WorkDestination.GeometryPool
                        ? geometryPool = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                        : null,
                    null,
                    SmallDisplayRoom(1024),
                    world =>
                    {
                        SetPrivate(world, "shadowMaterial", stable);
                        SetPrivate(world, "provisionalShadowMaterial", immediate);
                    });
                Assert.That(geometryPool, Is.Not.Null);
                geometryPool.HoldEverything = true;
                root.Driver.RemainingMainSeconds = () => 1.0;
                Assert.That(root.GpuCullFallback, Is.Null, "the world did not fall back");
                Assert.That(root.Display.CullsOnGpu, Is.EqualTo(gpu), "the world draws by the route this case asked for");
                Assert.That(GpuCullMaterialCopies(), gpu ? Is.EqualTo(4) : Is.Zero, "the selection's own materials: two surfaces and two casters");

                // The stage of the shadow cases, its ground made large enough to take the row's shadows, with a camera
                // of the ordinary kind: enabled, drawn by the frame.
                ShadowStage stage = NewShadowStage();
                GameObject ground = GameObject.Find("Plane");
                Assert.That(ground, Is.Not.Null, "the stage's ground");
                ground.transform.localScale = new Vector3(14f, 1f, 14f);
                stage.camera.orthographic = false;
                stage.camera.fieldOfView = 50f;
                stage.camera.nearClipPlane = 0.1f;
                stage.camera.farClipPlane = 60f;
                stage.camera.enabled = true;
                var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
                SetPrivate(drawing, "world", root);
                SetPrivate(drawing, "cameras", new[] { stage.camera });

                LogicalFragmentId body = AddBody(root, new Vector3(0f, 4f, 0f));
                root.Lifetime.MarkLineage(body);
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner whole), Is.True);
                whole.Body.isKinematic = true;
                yield return null;
                yield return null;
                int replacementsAtFirst = root.Display.GpuReplacements;

                var images = new List<Color32[]>();
                var kept = new List<string>();
                yield return GpuCullTakeStage(root, stage, gpu, 0, images, kept);

                // The cut, with its geometry held back: the display keeps the two sides clipped (immediate). Two more
                // instances than the first room holds: the body's GPU buffers are replaced here.
                Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
                CutOperationId operation = default;
                yield return Until(
                    () =>
                    {
                        foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                        {
                            operation = candidate.Operation;
                        }

                        return operation.IsSet && geometryPool.HoldingCount > 0
                            && root.Ledger.TryGetOperation(operation, out LogicalCutOperation published)
                            && published.positive.IsSet;
                    },
                    "the children are published while the cut's geometry is held back");
                Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
                Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner upper), Is.True);
                Assert.That(root.Owners.TryGet(record.negative, out PhysicsFragmentOwner lower), Is.True);
                // Both sides are held where this case puts them, not where the frames between the publication and now
                // left them: the two worlds must draw the same scene, whatever their frame times were.
                upper.Body.isKinematic = true;
                lower.Body.isKinematic = true;
                lower.Root.transform.SetPositionAndRotation(new Vector3(0f, 4f, 0f), Quaternion.identity);
                upper.Root.transform.SetPositionAndRotation(new Vector3(2.5f, 4f, 0f), Quaternion.identity);
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, gpu, 1, images, kept);
                Assert.That(
                    root.Ledger.TryGetOperation(operation, out LogicalCutOperation stillOut)
                    && stillOut.state != LogicalCutOperationState.Completed, Is.True, "still immediate: not committed");
                Assert.That(CountTwoSidedCommands(root), Is.GreaterThan(0), "the clipped sides cast two-sided");
                Assert.That(root.Display.CapRecordCount, Is.GreaterThan(0), "and the cut face is a cap the stencil work draws");
                Assert.That(root.Display.GpuReplacements, Is.GreaterThan(replacementsAtFirst), "the body's GPU buffers were replaced by larger ones");

                // The geometry commits: the children are closed shapes.
                geometryPool.HoldEverything = false;
                geometryPool.ReleaseEverything();
                yield return Until(
                    () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation now)
                        && now.state == LogicalCutOperationState.Completed,
                    "the cut's geometry committed");
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, gpu, 2, images, kept);
                Assert.That(CountTwoSidedCommands(root), Is.Zero, "after the commit nothing casts two-sided");

                // A row of bodies registered along the view: from in front of the camera, through every split of the
                // shadow map, to past the far plane and past the shadow's range.
                float[] rowZ = { -2f, 2f, 6f, 10f, 14f, 18f, 22f, 26f, 30f, 34f, 38f, 42f, 75f, 95f };
                foreach (float z in rowZ)
                {
                    LogicalFragmentId rowBody = AddBody(root, new Vector3(-3f, 1.5f, z));
                    Assert.That(root.Owners.TryGet(rowBody, out PhysicsFragmentOwner rowOwner), Is.True);
                    rowOwner.Body.isKinematic = true;
                }

                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, gpu, GpuCullRowStage, images, kept);

                // One piece retired: what was drawn for it is drawn no more.
                int commandsBefore = root.Display.CommandCount;
                Assert.That(root.Lifetime.TryRetire(record.positive, out string refusal), Is.True, refusal);
                yield return null;
                yield return null;
                yield return null;
                yield return GpuCullTakeStage(root, stage, gpu, 4, images, kept);
                Assert.That(root.Display.CommandCount, Is.LessThan(commandsBefore), "the retired piece's commands are gone");

                TestContext.Out.WriteLine((gpu ? "GPU selection" : "VP Stage 3") + ":\n" + string.Join("\n", kept));
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                if (gpu)
                {
                    VpGpuCullCameraRoute route = drawing.CullRoute;
                    Assert.That(route, Is.Not.Null, "the drawing put the selection in the frame");
                    TestContext.Out.WriteLine(
                        "route: enqueued " + route.Enqueued + ", recorded " + route.Recorded + ", refused " + route.Refused
                        + ", eyes " + route.LastEyeCount + ", splits " + route.LastSplitCount + ", body kept everything "
                        + route.BodyKeptEverything + ", casters kept everything " + route.CastersKeptEverything + " ("
                        + route.LastCastersKeptEverythingReason + "), frames without a main light shadow map "
                        + route.FramesWithoutMainShadowMap + " (" + route.LastWithoutMainShadowMapReason
                        + "), largest view difference " + route.LargestViewDifference
                        + "; display issued " + root.Display.CullSelectionsIssued + ", missed " + root.Display.CullSelectionsMissed
                        + ", dispatches " + root.Display.CullDispatches + ", bytes " + root.Display.CullViewBytes
                        + ", GPU replacements " + root.Display.GpuReplacements);
                    Assert.That(route.Enqueued, Is.GreaterThan(10), "a selection was enqueued for the frames drawn");
                    Assert.That(route.Recorded, Is.EqualTo(route.Enqueued), "and every one was recorded by the pipeline");
                    Assert.That(route.Refused, Is.Zero, "and issued by the display");
                    Assert.That(root.Display.CullSelectionsMissed, Is.Zero, "no frame drew from an earlier frame's selection");
                    Assert.That(drawing.CullRefusals, Is.Zero, "no camera went undrawn for want of a selection");
                    Assert.That(route.LastEyeCount, Is.EqualTo(1), "one eye: a camera with no XR pass");
                    Assert.That(route.LastSplitCount, Is.GreaterThan(0), "the casters were selected by the shadow map's splits");
                    Assert.That(route.CastersKeptEverything, Is.Zero, "in every frame that had a shadow map to select for");
                    Assert.That(
                        route.FramesWithoutMainShadowMap, Is.InRange(3L, 30L),
                        "the frames of the view with nothing in it: URP draws no main light shadow map for them");
                    Assert.That(route.BodyKeptEverything, Is.Zero);
                    Assert.That(route.LargestViewDifference, Is.Zero, "the selection's view was the rendered one");
                    Assert.That(root.Display.CullViewBytes, Is.GreaterThan(0L));
                }
                else
                {
                    Assert.That(drawing.CullRoute, Is.Null, "VP Stage 3 puts no selection in the frame");
                    Assert.That(root.Display.CullDispatches, Is.Zero);
                    Assert.That(root.Display.CullViewBytes, Is.Zero);
                }

                s_gpuCullImages[gpu] = images;
                stage.camera.enabled = false;
                yield return null;
                Object.Destroy(drawing);
                yield return null;
                yield return EndWorld(root);
                Assert.That(root.Display.IsDisposed, Is.True);
                Assert.That(root.Display.RetiredGpuObjects, Is.Zero, "every replaced GPU object released at the ending");
                yield return null;
                Assert.That(GpuCullMaterialCopies(), Is.Zero, "and the selection's own materials with them");
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
            }
        }

        // One stage from the five views. Each view is set, drawn by the frame, and read from the camera's target at the
        // next update; under the GPU selection what the selection kept for it is read back as well.
        private IEnumerator GpuCullTakeStage(CutWorldRoot root, ShadowStage stage, bool gpu, int stageIndex, List<Color32[]> images, List<string> kept)
        {
            for (int view = 0; view < GpuCullViews; view++)
            {
                stage.camera.fieldOfView = view == 1 ? 30f : 50f;
                switch (view)
                {
                    case 1:
                        // A narrow view from the side, down at where the light throws the bodies' shadow on the ground:
                        // the bodies themselves stand above its upper edge.
                        stage.camera.transform.position = new Vector3(1.15f, 2.5f, 9f);
                        stage.camera.transform.LookAt(new Vector3(1.15f, 0f, 2f));
                        break;
                    case 2:
                        // Up at the sky, away from everything.
                        stage.camera.transform.SetPositionAndRotation(new Vector3(0f, 5f, -9f), Quaternion.Euler(-60f, 180f, 0f));
                        break;
                    case 4:
                        stage.camera.transform.position = new Vector3(2f, 4f, -8f);
                        stage.camera.transform.LookAt(new Vector3(-3f, 1.5f, 20f));
                        break;
                    default:
                        stage.camera.transform.position = new Vector3(0f, 5f, -9f);
                        stage.camera.transform.LookAt(new Vector3(1.2f, 2f, 0f));
                        break;
                }

                yield return null;
                yield return null;
                yield return null;
                Color32[] image = ReadGpuCullTarget(stage.target);
                images.Add(image);
                string label = s_gpuCullStages[stageIndex] + ", " + s_gpuCullViewNames[view];
                int lit = 0;
                var histogram = new int[256];
                foreach (Color32 p in image)
                {
                    lit += p.r + p.g + p.b > 24 ? 1 : 0;
                    histogram[(p.r + p.g + p.b) / 3]++;
                }

                // The lit ground's value is the one most of the image has; what is clearly darker and not black is in shadow.
                int ground = 1;
                for (int v = 2; v < 256; v++)
                {
                    if (histogram[v] > histogram[ground]) ground = v;
                }

                int shadowed = 0;
                foreach (Color32 p in image)
                {
                    int value = (p.r + p.g + p.b) / 3;
                    shadowed += value > 8 && ground - value > 20 ? 1 : 0;
                }

                string line = label + ": drawn pixels " + lit + ", darker than the ground " + shadowed;
                if (view == 2)
                {
                    Assert.That(lit, Is.Zero, label + ": nothing is in this view");
                }
                else
                {
                    Assert.That(lit, Is.GreaterThan(500), label + ": the view shows the scene");
                }

                if (view == 1)
                {
                    Assert.That(shadowed, Is.GreaterThan(30), label + ": the shadow of bodies the camera does not see is in the view");
                }

                if (gpu)
                {
                    Assert.That(
                        root.Display.TryReadCullCountsForDiagnosis(stage.camera, out int instances, out int forwardKept, out int shadowKept),
                        Is.True, label + ": a selection was issued for the camera");
                    line += "; instances " + instances + ", body kept " + forwardKept + ", casters kept " + shadowKept;
                    Assert.That(instances, Is.GreaterThan(0), label);
                    bool row = stageIndex >= GpuCullRowStage;
                    switch (view)
                    {
                        case 1:
                            if (!row)
                            {
                                Assert.That(forwardKept, Is.Zero, label + ": the camera sees no body");
                            }

                            Assert.That(shadowKept, Is.GreaterThan(0), label + ": but what casts into its view is kept as a caster");
                            break;
                        case 2:
                            Assert.That(forwardKept, Is.Zero, label + ": the camera sees no body");
                            break;
                        case 4:
                            if (row)
                            {
                                Assert.That(forwardKept, Is.InRange(8, instances - 4), label + ": the row in view is kept, the bodies past the far plane are not");
                                Assert.That(shadowKept, Is.InRange(8, instances - 4), label + ": the casters in the shadow's range are kept, those past it are not");
                            }

                            break;
                        default:
                            if (!row)
                            {
                                Assert.That(forwardKept, Is.EqualTo(instances), label + ": every instance is in view");
                                Assert.That(shadowKept, Is.EqualTo(instances), label + ": and casts into it");
                            }
                            else
                            {
                                Assert.That(forwardKept, Is.GreaterThan(0), label);
                            }

                            break;
                    }
                }

                kept.Add(line);
            }
        }

        private Color32[] ReadGpuCullTarget(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var read = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }
    }
}
