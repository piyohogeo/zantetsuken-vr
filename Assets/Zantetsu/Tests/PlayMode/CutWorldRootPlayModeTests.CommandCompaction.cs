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
    /// The command compaction of the Direct3D 12 plugin's route (DESIGN 4.5.11) on the product's own path: a world made
    /// by <see cref="CutWorldRoot"/>, drawn by <see cref="CutWorldCameraDrawing"/> through the plugin with the
    /// compaction asked for, against the same route's fixed draw.
    /// <para>
    /// **The GPU culling scenes a fourth time** (<see cref="GpuCull_14_ThePluginsRoute_WithTheCommandCompaction_TakesTheSameScenes"/>):
    /// the seven stages and five views of the cases before it -- two materials' runs and two casters', the cut held
    /// back (its commands of the clip route, drawn from the clip list), the commit, the row across every split of the
    /// shadow map, the retirement that leaves commands of no instance, the late body, the replaced GPU buffers and the
    /// display's own compaction -- with every ordinary run compacted on the GPU and drawn by its count, and the
    /// compaction's check reading each view's frames. Its pictures are then held against the fixed draw's
    /// (<see cref="GpuCull_15_TheCompactedPictures_AreThePluginsFixedOnes"/>).
    /// </para>
    /// <para>
    /// **Two cameras, and the switch within one world** (<see cref="GpuCull_16_TwoCameras_TheSwitchWithinOneWorld_AndADrawingAndACameraRested_DrawTheSamePictures"/>):
    /// each camera has its own selection and so its own compaction set; the same state is drawn fixed, compacted and
    /// fixed again; then the drawing is disabled and enabled (a new route over the sets the batch kept) and one camera
    /// rests for some frames (its set comes back after frames of no zeroing).
    /// </para>
    /// Direct3D 12 only: on another device the cases are ignored.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest, Order(14)]
        public IEnumerator GpuCull_14_ThePluginsRoute_WithTheCommandCompaction_TakesTheSameScenes()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            yield return GpuCullScenes(true, true, true);
        }

        /// <summary>
        /// The compacted pictures against the fixed draw's of the same route, scene by scene and view by view. The two
        /// run the same pipelines over the same selection; what differs is which entries reach the GPU's command
        /// processor (none of no instance) and in what order. Held: no pixel differs by more than 2 of 255.
        /// </summary>
        [Test, Order(15)]
        public void GpuCull_15_TheCompactedPictures_AreThePluginsFixedOnes()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType);
            }

            Assert.That(s_gpuCullNativeImages, Is.Not.Null, "the plugin's fixed scenes were taken in this run");
            Assert.That(s_gpuCullCompactedImages, Is.Not.Null, "the plugin's compacted scenes were taken in this run");
            List<Color32[]> fixedDraw = s_gpuCullNativeImages;
            List<Color32[]> compacted = s_gpuCullCompactedImages;
            Assert.That(fixedDraw.Count, Is.EqualTo(s_gpuCullStages.Length * GpuCullViews));
            Assert.That(compacted.Count, Is.EqualTo(fixedDraw.Count));
            var lines = new List<string>();
            int worst = 0;
            for (int i = 0; i < fixedDraw.Count; i++)
            {
                int differing = CountDifferingPixels(fixedDraw[i], compacted[i], 2, out int largest, out int lit);
                lines.Add(s_gpuCullStages[i / GpuCullViews] + ", " + s_gpuCullViewNames[i % GpuCullViews] + ": pixels differing by more than 2: " + differing
                    + " (largest difference " + largest + "; drawn pixels " + lit + ")");
                worst = Math.Max(worst, differing);
            }

            TestContext.Out.WriteLine(string.Join("\n", lines));
            Assert.That(worst, Is.Zero, "pixels differing between the plugin's fixed draw and its compacted one:\n" + string.Join("\n", lines));
        }

        private static int CountDifferingPixels(Color32[] first, Color32[] second, int tolerance, out int largest, out int lit)
        {
            Assert.That(second.Length, Is.EqualTo(first.Length));
            int differing = 0;
            largest = 0;
            lit = 0;
            for (int p = 0; p < first.Length; p++)
            {
                Color32 a = first[p];
                Color32 b = second[p];
                int d = Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
                differing += d > tolerance ? 1 : 0;
                largest = Math.Max(largest, d);
                lit += a.r + a.g + a.b > 24 ? 1 : 0;
            }

            return differing;
        }

        [UnityTest, Order(16)]
        public IEnumerator GpuCull_16_TwoCameras_TheSwitchWithinOneWorld_AndADrawingAndACameraRested_DrawTheSamePictures()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            bool requestedBefore = VpGpuCullSetup.Requested;
            VpBodyRouteRequest nativeBefore = VpNativeDrawSetup.Request;
            bool compactBefore = VpNativeDrawPlugin.Direct3D12IndirectCount, checkBefore = VpCommandCompaction.VerificationEnabled;
            VpNativeDrawSetup.Request = VpBodyRouteRequest.Native;
            VpGpuCullSetup.Requested = true;
            VpNativeDrawPlugin.Direct3D12IndirectCount = false;
            VpCommandCompaction.VerificationEnabled = true;   // checks only what is compacted
            try
            {
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
                root.Display.CompactionMinimumInterval = int.MaxValue;
                Assert.That(root.NativeDrawFailure, Is.Null, root.NativeDrawFailure);
                Assert.That(root.Display.NativeArguments, Is.True, "the display's batch writes the plugin's argument entries");

                // The stage of the shadow cases with its camera, and a second camera of the same kind with its own
                // target: one looks at the bodies, the other down the row. Both render every frame.
                ShadowStage stage = NewShadowStage();
                GameObject ground = GameObject.Find("Plane");
                Assert.That(ground, Is.Not.Null, "the stage's ground");
                ground.transform.localScale = new Vector3(14f, 1f, 14f);
                Camera first = stage.camera;
                first.orthographic = false;
                first.fieldOfView = 50f;
                first.nearClipPlane = 0.1f;
                first.farClipPlane = 60f;
                first.transform.position = new Vector3(0f, 5f, -9f);
                first.transform.LookAt(new Vector3(1.2f, 2f, 0f));
                var secondTarget = Track(new RenderTexture(stage.target.width, stage.target.height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
                secondTarget.Create();
                Camera second = TrackActor(new GameObject("Shadow Stage Second Camera")).AddComponent<Camera>();
                second.clearFlags = CameraClearFlags.SolidColor;
                second.backgroundColor = Color.black;
                second.fieldOfView = 50f;
                second.nearClipPlane = 0.1f;
                second.farClipPlane = 60f;
                second.targetTexture = secondTarget;
                second.transform.position = new Vector3(2f, 4f, -8f);
                second.transform.LookAt(new Vector3(-3f, 1.5f, 20f));
                first.enabled = true;
                second.enabled = true;
                var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
                SetPrivate(drawing, "world", root);
                SetPrivate(drawing, "cameras", new[] { first, second });

                // A body to cut, and a row along the second camera's view through the shadow map's splits and past
                // the far plane.
                LogicalFragmentId body = AddBody(root, new Vector3(0f, 4f, 0f));
                root.Lifetime.MarkLineage(body);
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner whole), Is.True);
                whole.Body.isKinematic = true;
                foreach (float z in new[] { -2f, 2f, 6f, 10f, 14f, 18f, 22f, 26f, 30f, 34f, 38f, 42f, 75f, 95f })
                {
                    LogicalFragmentId rowBody = AddBody(root, new Vector3(-3f, 1.5f, z));
                    Assert.That(root.Owners.TryGet(rowBody, out PhysicsFragmentOwner rowOwner), Is.True);
                    rowOwner.Body.isKinematic = true;
                }

                yield return null;
                yield return null;
                yield return null;

                var lines = new List<string>();
                var compactedLast = new Color32[2][];
                var targets = new[] { stage.target, secondTarget };
                string[] cameraNames = { "the camera at the bodies", "the camera down the row" };

                // One state drawn fixed, compacted and fixed again by the same route: the pictures of each camera.
                IEnumerator Compare(string state)
                {
                    var taken = new Color32[3][][];
                    uint[] checkAtFirst = new uint[VpCommandCompaction.VerificationWords], check = new uint[VpCommandCompaction.VerificationWords];
                    for (int pass = 0; pass < 3; pass++)
                    {
                        VpNativeDrawPlugin.Direct3D12IndirectCount = pass == 1;
                        if (pass == 1) VpCommandCompaction.TryReadVerification(checkAtFirst);
                        yield return null;
                        yield return null;
                        yield return null;
                        taken[pass] = new[] { ReadGpuCullTarget(targets[0]), ReadGpuCullTarget(targets[1]) };
                        if (pass == 1) Assert.That(VpCommandCompaction.TryReadVerification(check), Is.True, state + ": the compacted frames were checked");
                    }

                    int per = VpCommandCompaction.VerificationWordsPerColumn;
                    uint mismatched = check[0] - checkAtFirst[0] + check[per] - checkAtFirst[per];
                    uint beyond = check[4] - checkAtFirst[4] + check[per + 4] - checkAtFirst[per + 4];
                    string line = state + ": compaction check body runs " + (check[3] - checkAtFirst[3]) + " kept " + (check[1] - checkAtFirst[1])
                        + ", caster runs " + (check[per + 3] - checkAtFirst[per + 3]) + " kept " + (check[per + 1] - checkAtFirst[per + 1]) + ", mismatched " + mismatched + ", beyond a run " + beyond;
                    for (int c = 0; c < 2; c++)
                    {
                        int compactedDiffers = CountDifferingPixels(taken[0][c], taken[1][c], 2, out int largest, out int lit);
                        int fixedAgainDiffers = CountDifferingPixels(taken[0][c], taken[2][c], 2, out int _, out int _);
                        line += "; " + cameraNames[c] + ": drawn pixels " + lit + ", compacted differs in " + compactedDiffers + " (largest " + largest + "), fixed again in " + fixedAgainDiffers;
                        Assert.That(lit, Is.GreaterThan(500), state + ", " + cameraNames[c] + ": the view shows the scene");
                        Assert.That(compactedDiffers, Is.Zero, line);
                        Assert.That(fixedAgainDiffers, Is.Zero, line);
                        compactedLast[c] = taken[1][c];
                    }

                    lines.Add(line);
                    TestContext.Out.WriteLine(line);
                    Assert.That(mismatched, Is.Zero, line);
                    Assert.That(beyond, Is.Zero, line);
                    Assert.That(check[3] - checkAtFirst[3], Is.GreaterThan(0u), state + ": the body's runs were compacted, for both cameras");
                }

                yield return Compare("whole, with the row");
                Assert.That(Zantetsu.Rendering.Urp.VpNativeDrawRoute.ClipDrawsUnavailable, Is.Null, "the clip list's variants are in this project");

                // The cut, with its geometry held back: the body's two commands are of the clip route.
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
                upper.Body.isKinematic = true;
                lower.Body.isKinematic = true;
                lower.Root.transform.SetPositionAndRotation(new Vector3(0f, 4f, 0f), Quaternion.identity);
                upper.Root.transform.SetPositionAndRotation(new Vector3(2.5f, 4f, 0f), Quaternion.identity);
                yield return null;
                yield return null;
                Assert.That(root.Display.SeparatesClipDraws, Is.True, root.Display.ClipSeparationRefusal);
                Assert.That(root.Display.ClipRouteCommandCount, Is.EqualTo(2), "the cut body's two commands are of the clip route");
                yield return Compare("one body cut and held back (clip route), with the row");

                // The geometry commits; then one piece is retired, leaving command slots of no instance.
                geometryPool.HoldEverything = false;
                geometryPool.ReleaseEverything();
                yield return Until(
                    () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation now) && now.state == LogicalCutOperationState.Completed,
                    "the cut's geometry committed");
                yield return null;
                yield return null;
                yield return Compare("committed");
                Assert.That(root.Lifetime.TryRetire(record.positive, out string refusal), Is.True, refusal);
                yield return null;
                yield return null;
                yield return null;
                Assert.That(root.Display.DrawCommandEnd, Is.GreaterThan(root.Display.CommandCount), "the retired piece's command slots are still gone through");
                yield return Compare("one piece retired");

                // The drawing disabled and enabled while the compaction is asked for: the route ends (its events, its
                // pipelines and its buffer of zeroes wait for their release) and a new one draws over the sets the
                // batch kept. Then the second camera rests: its view's set is not zeroed for those frames, and is
                // again in the frame it comes back in.
                VpNativeDrawPlugin.Direct3D12IndirectCount = true;
                yield return null;
                yield return null;
                VpNativeDrawRoute before = drawing.NativeRoute;
                Assert.That(before, Is.Not.Null);
                Assert.That(before.IndirectCountIssues, Is.GreaterThan(0L), "the first route drew by counts");
                Assert.That(new[] { before.Refused, before.RingFull, before.PipelineFailures, before.IndirectCountSlotsShort, before.IndirectCountZeroMissed }, Is.EqualTo(new long[5]), before.LastFailure);
                drawing.enabled = false;
                yield return null;
                yield return null;
                yield return null;
                drawing.enabled = true;
                yield return null;
                yield return null;
                yield return null;
                VpNativeDrawRoute after = drawing.NativeRoute;
                Assert.That(after, Is.Not.Null.And.Not.SameAs(before), "a new route after the drawing was enabled again");
                for (int c = 0; c < 2; c++)
                {
                    int differs = CountDifferingPixels(compactedLast[c], ReadGpuCullTarget(targets[c]), 2, out int largest, out int _);
                    Assert.That(differs, Is.Zero, "after the drawing was disabled and enabled, " + cameraNames[c] + " differs in " + differs + " pixels (largest " + largest + ")");
                }

                second.enabled = false;
                for (int i = 0; i < 6; i++) yield return null;
                int firstDiffers = CountDifferingPixels(compactedLast[0], ReadGpuCullTarget(targets[0]), 2, out int _, out int _);
                Assert.That(firstDiffers, Is.Zero, "while the second camera rests the first draws as before");
                second.enabled = true;
                yield return null;
                yield return null;
                yield return null;
                for (int c = 0; c < 2; c++)
                {
                    int differs = CountDifferingPixels(compactedLast[c], ReadGpuCullTarget(targets[c]), 2, out int largest, out int _);
                    Assert.That(differs, Is.Zero, "after the second camera rested, " + cameraNames[c] + " differs in " + differs + " pixels (largest " + largest + ")");
                }

                TestContext.Out.WriteLine("the route after the drawing was enabled again: " + drawing.DescribeNativeRoute(out bool drewEverything) + "; drew everything " + drewEverything);
                Assert.That(after.CommandCompactionChoice, Does.StartWith("compaction:"));
                Assert.That(after.IndirectCountIssues, Is.GreaterThan(0L), "the new route drew by counts");
                Assert.That(after.IndirectCountIssues, Is.EqualTo(after.BodiesIssued + after.CastersIssued), "every issue of the selection's own commands");
                Assert.That(new[] { after.Refused, after.RingFull, after.PipelineFailures, after.IndirectCountSlotsShort, after.IndirectCountZeroMissed }, Is.EqualTo(new long[5]), after.LastFailure);
                Assert.That(drawing.NativeRefusals, Is.Zero, "no camera went undrawn for want of the plugin's pass");
                Assert.That(drewEverything, Is.True);
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);

                first.enabled = false;
                second.enabled = false;
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
                VpNativeDrawSetup.Request = nativeBefore;
                VpNativeDrawPlugin.Direct3D12IndirectCount = compactBefore;
                VpCommandCompaction.VerificationEnabled = checkBefore;
            }
        }
    }
}
