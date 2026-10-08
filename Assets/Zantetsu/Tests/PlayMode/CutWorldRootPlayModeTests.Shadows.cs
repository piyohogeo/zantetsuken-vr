using System;
using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The cut display's shadow in a world given the two casters of DESIGN 5.4, stage by stage: the whole body before
    /// the cut, the two sides while the cut is still immediate (its geometry held back, so the display clips), and the
    /// two children once their geometry has committed.
    /// <para>
    /// **What is measured is the ground the shadow covers.** Each stage is drawn looking straight down twice, with the
    /// light's shadows on and off, and only ground pixels -- those at the lit ground's own value in the image without
    /// shadows -- that are darker with them are counted. The sides are carried apart along the ground and held there
    /// through the commit, so the immediate (clipped, two-sided) shadow is compared with the one the two closed halves
    /// cast afterwards from the same places: a caster that drew each side as the whole body it came from would cover
    /// about twice the ground the whole body did, and far more than the closed halves. The bounds are loose on
    /// purpose: this is an approximation (DESIGN 5.4), not a measurement of any shadow's exact size.
    /// </para>
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const int ShadowImageSize = 128;

        [UnityTest]
        public IEnumerator TheCutsShadow_IsWhole_ThenClippedAndTwoSidedWhileImmediate_ThenOneSidedAfterTheCommit()
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
                null,
                world =>
                {
                    SetPrivate(world, "shadowMaterial", stable);
                    SetPrivate(world, "provisionalShadowMaterial", immediate);
                });
            Assert.That(geometryPool, Is.Not.Null);
            geometryPool.HoldEverything = true;
            ShadowStage stage = NewShadowStage();
            LogicalFragmentId body = AddBody(root, new Vector3(0f, 1.5f, 0f));
            yield return null;
            yield return null;

            // Before the cut: the whole body, one-sided.
            int whole = 0;
            yield return ShadowArea(root, stage, area => whole = area);
            Assert.That(whole, Is.GreaterThan(40), "the whole body casts a shadow on the ground: " + whole);
            Assert.That(CountTwoSidedCommands(root), Is.Zero, "and nothing casts two-sided before the cut");

            // The cut, with its geometry held back: the display keeps the two sides clipped (immediate).
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

            // The upper side is carried along the ground, away from the lower one; both are held still.
            upper.Body.isKinematic = true;
            lower.Body.isKinematic = true;
            upper.Root.transform.position += new Vector3(2.5f, 0f, 0f);
            yield return null;
            yield return null;

            int twoSidedIssues = root.Display.TwoSidedShadowIssues;
            int clipped = 0;
            yield return ShadowArea(root, stage, area => clipped = area);
            Assert.That(
                root.Ledger.TryGetOperation(operation, out LogicalCutOperation stillOut)
                && stillOut.state != LogicalCutOperationState.Completed, Is.True, "still immediate: not committed");
            Assert.That(CountTwoSidedCommands(root), Is.GreaterThan(0), "the clipped sides cast two-sided");
            Assert.That(
                root.Display.TwoSidedShadowIssues, Is.GreaterThan(twoSidedIssues), "and the two-sided caster was issued");
            Assert.That(
                clipped, Is.LessThan(whole * 9 / 5),
                "the two sides do not each cast the whole body they came from: whole " + whole + ", both sides apart "
                + clipped);
            Assert.That(clipped, Is.GreaterThan(whole / 2), "and together they still cast a shadow of that order");

            // The geometry commits: the children are closed shapes and cast one-sided again.
            geometryPool.ReleaseEverything();
            yield return Until(
                () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation now)
                    && now.state == LogicalCutOperationState.Completed,
                "the cut's geometry committed");
            yield return null;
            yield return null;

            int oneSidedIssues = root.Display.OneSidedShadowIssues;
            twoSidedIssues = root.Display.TwoSidedShadowIssues;
            int committed = 0;
            yield return ShadowArea(root, stage, area => committed = area);
            TestContext.Out.WriteLine(
                "shadow ground pixels: whole " + whole + ", immediate (sides apart) " + clipped + ", committed " + committed
                + "; two-sided issues " + root.Display.TwoSidedShadowIssues + ", one-sided issues " + root.Display.OneSidedShadowIssues);
            Assert.That(CountTwoSidedCommands(root), Is.Zero, "after the commit nothing casts two-sided");
            Assert.That(root.Display.TwoSidedShadowIssues, Is.EqualTo(twoSidedIssues), "the two-sided caster is not issued");
            Assert.That(root.Display.OneSidedShadowIssues, Is.GreaterThan(oneSidedIssues), "the one-sided one is");
            Assert.That(
                committed, Is.InRange(clipped * 17 / 20, clipped * 23 / 20),
                "the two closed children, where the clipped sides stood, cover what the clipped sides did (within 15%): "
                + clipped + " then " + committed);

            yield return EndWorld(root);
        }

        private sealed class ShadowStage
        {
            internal Camera camera;
            internal Light light;
            internal RenderTexture target;
            internal bool registered;

            // For a world that selects on the GPU (the default, D-198): what puts the selection into the frame, as the
            // world's own camera drawing does. This stage draws the display itself, so it does that itself too.
            internal readonly Zantetsu.Rendering.Urp.VpGpuCullCameraRoute cullRoute = new Zantetsu.Rendering.Urp.VpGpuCullCameraRoute();

            // For a world drawn by the Direct3D 12 plugin (DESIGN 4.5.8, asked for by VpNativeDrawSetup): the route
            // that takes the display's draws, as the world's own camera drawing does. Made when first needed.
            internal Zantetsu.Rendering.Urp.VpNativeDrawRoute nativeRoute;
        }

        private ShadowStage NewShadowStage()
        {
            Shader surfaceShader = Shader.Find("Zantetsu/VP Mesh Surface");
            Assert.That(surfaceShader, Is.Not.Null);
            Material surface = Track(new Material(surfaceShader) { name = "shadow ground" });
            surface.SetFloat("_VpUsePaletteAtlas", 0f);
            surface.SetColor("_BaseColor", Color.white);

            GameObject ground = TrackActor(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.Destroy(ground.GetComponent<Collider>());
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.sharedMaterial = surface;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            groundRenderer.receiveShadows = true;

            Light light = TrackActor(new GameObject("Shadow Stage Sun")).AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;
            light.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var target = Track(new RenderTexture(ShadowImageSize, ShadowImageSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            target.Create();
            Camera camera = TrackActor(new GameObject("Shadow Stage Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 40f;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 15f, 0f), Quaternion.Euler(90f, 0f, 0f));
            return new ShadowStage { camera = camera, light = light, target = target };
        }

        /// <summary>
        /// The ground a stage's shadow covers: this frame drawn with the light's shadows, the next without, and the ground
        /// pixels darker in the first counted.
        /// </summary>
        private IEnumerator ShadowArea(CutWorldRoot root, ShadowStage stage, Action<int> result)
        {
            Color32[] with = null;
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, drawn => with = drawn);
            Color32[] without = null;
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, drawn => without = drawn);
            stage.light.shadows = LightShadows.Hard;

            // The lit ground's value is the one most of the image without shadows has.
            var histogram = new int[256];
            foreach (Color32 p in without)
            {
                histogram[(p.r + p.g + p.b) / 3]++;
            }

            int ground = 0;
            for (int v = 1; v < 256; v++)
            {
                if (histogram[v] > histogram[ground]) ground = v;
            }

            int shadowed = 0;
            for (int i = 0; i < with.Length; i++)
            {
                int off = (without[i].r + without[i].g + without[i].b) / 3;
                int on = (with[i].r + with[i].g + with[i].b) / 3;
                if (Mathf.Abs(off - ground) <= 4 && off - on > 20)
                {
                    shadowed++;
                }
            }

            result(shadowed);
        }

        /// <summary>
        /// Draws the stage in this frame after the world's collection -- the display's frame is open from the collection
        /// right after the late updates until the frame ends -- from a system of this test's, added at the end of the
        /// player loop's PreLateUpdate for one frame and taken out again, and hands the image over at the next frame. An
        /// exception there is passed on here.
        /// </summary>
        private IEnumerator DrawStageAfterTheCollection(
            CutWorldRoot root, ShadowStage stage, LightShadows shadows, Action<Color32[]> result)
        {
            Color32[] drawn = null;
            Exception failure = null;
            bool done = false;
            PlayerLoopSystem original = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem withDraw = original;
            withDraw.subSystemList = (PlayerLoopSystem[])original.subSystemList.Clone();
            bool added = false;
            for (int i = 0; i < withDraw.subSystemList.Length; i++)
            {
                if (withDraw.subSystemList[i].type != typeof(PreLateUpdate))
                {
                    continue;
                }

                PlayerLoopSystem stageSystem = withDraw.subSystemList[i];
                var systems = new System.Collections.Generic.List<PlayerLoopSystem>(stageSystem.subSystemList)
                {
                    new PlayerLoopSystem
                    {
                        type = typeof(ShadowStageDraw),
                        updateDelegate = () =>
                        {
                            if (done)
                            {
                                return;
                            }

                            done = true;
                            try
                            {
                                Assert.That(root.Display.IsFrameOpen, Is.True, "the display's frame is open after the collection");
                                stage.light.shadows = shadows;
                                drawn = DrawStage(root, stage);
                            }
                            catch (Exception e)
                            {
                                failure = e;
                            }
                        },
                    },
                };
                stageSystem.subSystemList = systems.ToArray();
                withDraw.subSystemList[i] = stageSystem;
                added = true;
            }

            Assert.That(added, Is.True, "the player loop has its PreLateUpdate");
            PlayerLoop.SetPlayerLoop(withDraw);
            try
            {
                // A player loop set now may take effect from the next frame: wait for the system to have run.
                for (int frames = 0; !done && frames < 5; frames++)
                {
                    yield return null;
                }
            }
            finally
            {
                PlayerLoop.SetPlayerLoop(original);
            }

            if (failure != null)
            {
                throw failure;
            }

            Assert.That(drawn, Is.Not.Null, "the stage was drawn after a collection");
            result(drawn);
        }

        /// <summary>The type of this test's one-frame drawing system in the player loop.</summary>
        private struct ShadowStageDraw
        {
        }

        private Color32[] DrawStage(CutWorldRoot root, ShadowStage stage)
        {
            if (!stage.registered)
            {
                Assert.That(root.Display.TryRegisterCamera(stage.camera), Is.True, "the stage's camera is registered");
                stage.registered = true;
            }

            Assert.That(root.Display.TryPrepareCamera(stage.camera), Is.True, "the stage's camera is prepared");
            if (root.Display.CullsOnGpu)
            {
                // The selection first, then the draws that read it: the pass is taken by the render request below.
                Assert.That(stage.cullRoute.TryEnqueue(stage.camera, root.Display, out string cullFailure), Is.True, cullFailure);
            }

            if (root.Display.NativeArguments)
            {
                // The plugin's pass after the selection and before the draws it takes.
                stage.nativeRoute ??= new Zantetsu.Rendering.Urp.VpNativeDrawRoute();
                Assert.That(stage.nativeRoute.TryEnqueue(stage.camera, root.Display, out string nativeFailure), Is.True, nativeFailure);
            }

            root.Display.Render(0, stage.camera);
            var request = new RenderPipeline.StandardRequest { destination = stage.target };
            Assert.That(RenderPipeline.SupportsRenderRequest(stage.camera, request), Is.True, "render request");
            RenderPipeline.SubmitRenderRequest(stage.camera, request);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = stage.target;
            var read = Track(new Texture2D(ShadowImageSize, ShadowImageSize, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, ShadowImageSize, ShadowImageSize), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        private static int CountTwoSidedCommands(CutWorldRoot root)
        {
            int twoSided = 0;
            for (int c = 0; c < root.Display.CommandCount; c++)
            {
                if (root.Display.CommandCastsTwoSided(c)) twoSided++;
            }

            return twoSided;
        }
    }
}
