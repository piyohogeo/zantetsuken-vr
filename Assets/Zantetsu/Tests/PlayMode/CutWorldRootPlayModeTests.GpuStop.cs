using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The stop stages through the real registration path (unit 8-C, TL review 2): at each stage the draw-compare scene's
    /// world is made, its renderers held off, its inventory taken, its targets shown (or, at stage 4, the registration
    /// left out as the runner leaves it), and frames pass with a camera drawing -- and the run reaches the state the
    /// measurement waits in without an exception, with the stage's work not done and the tallies saying so.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator GpuStop_Stage1_NoDraw_TheRenderersHeldFromTheFirstFrame_EverythingElseRuns() => GpuStopStage(1);

        [UnityTest]
        public IEnumerator GpuStop_Stage2_NoFrameWorkAfterTheRegistration() => GpuStopStage(2);

        [UnityTest]
        public IEnumerator GpuStop_Stage3_NoInitialUpload_TheBuffersKept() => GpuStopStage(3);

        [UnityTest]
        public IEnumerator GpuStop_Stage4_NoAllocation_NoRegistration_TheRunGoesOn() => GpuStopStage(4);

        [UnityTest]
        public IEnumerator GpuStop_TheRunner_Stage0_Control_ReachesItsEnd() => GpuStopRunner(0);

        [UnityTest]
        public IEnumerator GpuStop_TheRunner_Stage1_ReachesItsEnd() => GpuStopRunner(1);

        [UnityTest]
        public IEnumerator GpuStop_TheRunner_Stage2_ReachesItsEnd() => GpuStopRunner(2);

        [UnityTest]
        public IEnumerator GpuStop_TheRunner_Stage3_ReachesItsEnd() => GpuStopRunner(3);

        [UnityTest]
        public IEnumerator GpuStop_TheRunner_Stage4_ReachesItsEnd_WithoutARegistration() => GpuStopRunner(4);

        /// <summary>
        /// The real runner (SandboxDrawComparePlayerCheck) at a stage, as the static scene runs it: preparation,
        /// registration (none at stage 4), warm-up, measurement and end -- completed, no exception, the stage's tallies.
        /// </summary>
        private IEnumerator GpuStopRunner(int stage)
        {
            VpGpuStop.Reset();
            VpGpuStop.Set(stage);
            string folder = System.IO.Path.Combine(Application.temporaryCachePath, "gpu-stop-runner-" + stage + "-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Assert.That(SceneDrawComparePath.TryParse("start,0,2,-6,0" + "\n" + "hold,0.2" + "\n" + "turn,25,0.2" + "\n", out SceneDrawComparePath path, out string failure), Is.True, failure);
                DrawCompareScene scene = NewDrawCompareScene();
                CutWorldRoot root = scene.root;
                int held = VpGpuStop.SuppressRenderers(Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                if (stage >= 1) Assert.That(held, Is.GreaterThanOrEqualTo(3), "the scene's renderers are held off from the first frame (stage 1)");
                else Assert.That(held, Is.Zero, "the control holds nothing");
                yield return null;
                ShadowStage stage3d = NewShadowStage();
                yield return null;
                var settings = new SandboxDrawComparePlayerCheck.Settings
                {
                    directory = System.IO.Path.Combine(folder, "vp3c"), mode = SandboxDrawComparePlayerCheck.Mode.Vp3c, path = path, pathFile = "(the test's)", warmupLaps = 1,
                };
                SandboxDrawComparePlayerCheck.Runner runner = SandboxDrawComparePlayerCheck.Begin(settings, root, scene.instances, stage3d.camera, null);
                TrackActor(runner.gameObject);
                int code = -1;
                runner.quit = c => code = c;
                float deadline = Time.realtimeSinceStartup + 60f;
                while (!runner.Done && Time.realtimeSinceStartup < deadline) yield return null;
                string resultPath = System.IO.Path.Combine(settings.directory, "draw-compare-result.txt");
                string result = System.IO.File.Exists(resultPath) ? System.IO.File.ReadAllText(resultPath) : "(no result file)";
                TestContext.Out.WriteLine(result);
                Debug.Log("GPU STOP RUNNER stage " + stage + ": " + VpGpuStop.Describe() + " shown=" + root.Display.ShownCount + " code=" + code
                    + " halted=" + root.Display.IsHalted + " haltReason=" + root.Display.HaltReason + " invalidInput=" + root.Display.HaltInvalidInput);
                Assert.That(runner.Done, Is.True, "the run ended");
                Assert.That(runner.Completed, Is.True, "as a completed run (not refused, not failed): " + result);
                StringAssert.Contains("run: completed", result);
                if (stage >= 1) Assert.That(VpGpuStop.DrawsIssued, Is.Zero, "no draw of the display at any stage");
                Assert.That(VpGpuStop.RegistrationDone, Is.True, "the runner said when the registration was over");
                if (stage >= 2) Assert.That(VpGpuStop.DisplayUpdatesSkipped, Is.GreaterThan(0), "the display's frame update was left once the registration was over");
                if (stage >= 3) Assert.That(VpGpuStop.InitialUploadsIssued, Is.Zero);
                if (stage >= 4)
                {
                    Assert.That(VpGpuStop.AllocationsMade, Is.Zero);
                    Assert.That(root.Display.ShownCount, Is.Zero, "no registration was attempted (the runner notes 'registration skipped' in its log)");
                }
                else
                {
                    Assert.That(root.Display.ShownCount, Is.EqualTo(3), "the three targets were shown");
                }

                // Not asserted: IsHalted. It is read once, after the run; the stage 0 control of this very run showed the
                // same halt (InvalidInput / InputContract), so it is not a condition the stop stages add -- when it happens
                // and why is not settled by this test. What this test settles: the runner reaches its end and writes its result.
                yield return EndWorld(root);
            }
            finally
            {
                VpGpuStop.Reset();
                try { if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, true); } catch (System.Exception) { }
            }
        }

        private IEnumerator GpuStopStage(int stage)
        {
            VpGpuStop.Reset();
            VpGpuStop.Set(stage);
            try
            {
                DrawCompareScene scene = NewDrawCompareScene();
                CutWorldRoot root = scene.root;
                Assert.That(root.Display, Is.Not.Null, "the display object exists at every stage");
                if (stage >= 4)
                {
                    Assert.That(VpGpuStop.AllocationsMade, Is.Zero, "stage 4: no display buffer was made");
                    Assert.That(VpGpuStop.AllocationsWithheld, Is.GreaterThan(0));
                }
                else
                {
                    Assert.That(VpGpuStop.AllocationsMade, Is.GreaterThan(0), "the display's buffers were made");
                    Assert.That(VpGpuStop.AllocationsWithheld, Is.Zero);
                }

                // Stage 1: the scene's renderers are held off before anything draws, and the inventory still lists them.
                Renderer[] all = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                int held = VpGpuStop.SuppressRenderers(all);
                Assert.That(held, Is.GreaterThanOrEqualTo(3), "the body, its part and the turned one are held");
                Assert.That(scene.body.forceRenderingOff && scene.child.forceRenderingOff && scene.turned.forceRenderingOff, Is.True);
                SceneDrawInventory inventory = SceneDrawInventory.Collect(scene.instances, root.MaterialBindings, new SceneDrawMeshBank());
                Assert.That(inventory.Targets.Count, Is.EqualTo(3), "held renderers are still targets: " + inventory.Describe());
                Assert.That(inventory.NotDrawn, Is.Zero, "none left out for the hold");
                yield return null;

                // The registration, as the runner does it; at stage 4 left out, as the runner leaves it.
                SceneDrawVpDisplay display = null;
                if (stage < 4)
                {
                    display = new SceneDrawVpDisplay(root);
                    foreach (SceneDrawTarget target in inventory.Targets) Assert.That(display.TryShow(target, out string why), Is.True, why);
                    Assert.That(root.Display.ShownCount, Is.EqualTo(3));
                    yield return null;   // the collection after the showing uploads their commands (initial data to stage 2)
                    yield return null;
                }

                // The camera, registered with the display before the registration is over at EVERY stage (as the app's
                // camera is at its first frame): the stencil batch's buffers and whole writes are then the same initial
                // resources at stages 1 and 2 (TL review 3).
                ShadowStage stage3d = NewShadowStage();
                stage3d.camera.transform.SetPositionAndRotation(new Vector3(-2f, 4f, -3f), Quaternion.LookRotation(new Vector3(2f, -3.6f, 3f)));
                Assert.That(root.Display.TryRegisterCamera(stage3d.camera), Is.True, "the camera is registered at every stage");
                stage3d.registered = true;
                yield return null;

                long initialBeforeDone = VpGpuStop.InitialUploadsIssued;
                long allocationsBeforeDone = VpGpuStop.AllocationsMade;
                VpGpuStop.NoteRegistrationDone();
                long updatesEnteredAtDone = VpGpuStop.DisplayUpdatesEntered;
                long dispatchesAtDone = VpGpuStop.DispatchesIssued;
                long frameUploadsAtDone = VpGpuStop.FrameUploadsIssued;

                // Frames with a camera drawing the stage: at stage 1 through the display's Render (the frame is open); from
                // stage 2 the display's frame is not begun, so the camera renders alone, as the app's camera does then.
                // The camera's first render is its own (as the picture test does before the display draws through it).
                RenderPipeline.SubmitRenderRequest(stage3d.camera, new RenderPipeline.StandardRequest { destination = stage3d.target });
                yield return null;
                for (int frame = 0; frame < 4; frame++)
                {
                    Debug.Log("GPU STOP TEST frame " + frame + ": " + VpGpuStop.Describe() + " cullIssued=" + root.Display.CullSelectionsIssued + " cullMissed=" + root.Display.CullSelectionsMissed);
                    if (stage == 1)
                    {
                        Color32[] picture = null;
                        yield return DrawStageAfterTheCollection(root, stage3d, LightShadows.Hard, d => picture = d);
                        Assert.That(picture, Is.Not.Null);
                    }
                    else
                    {
                        Assert.That(root.Display.IsFrameOpen, Is.False, "stage " + stage + ": the display's frame is not begun once the registration is over");
                        var request = new RenderPipeline.StandardRequest { destination = stage3d.target };
                        RenderPipeline.SubmitRenderRequest(stage3d.camera, request);
                        yield return null;
                    }
                }

                TestContext.Out.WriteLine(VpGpuStop.Describe());
                // What every stage stops: the display's draws; what each stage adds.
                Assert.That(VpGpuStop.DrawsIssued, Is.Zero, "no draw of the display at any stage");
                if (stage == 1)
                {
                    Assert.That(VpGpuStop.DrawsSkipped, Is.GreaterThan(0), "the display asked to draw and was stopped");
                    Assert.That(VpGpuStop.DispatchesIssued, Is.GreaterThan(dispatchesAtDone), "stage 1 keeps the cull dispatch");
                    Assert.That(VpGpuStop.DisplayUpdatesEntered, Is.GreaterThan(updatesEnteredAtDone), "stage 1 keeps the display's update");
                    Assert.That(VpGpuStop.InitialUploadsIssued, Is.GreaterThan(0), "stage 1 keeps the initial uploads");
                }

                if (stage >= 2)
                {
                    Assert.That(VpGpuStop.DisplayUpdatesEntered, Is.EqualTo(updatesEnteredAtDone), "no display update once the registration is over");
                    Assert.That(VpGpuStop.DisplayUpdatesSkipped, Is.GreaterThan(0));
                    Assert.That(VpGpuStop.DispatchesIssued, Is.EqualTo(dispatchesAtDone), "no cull dispatch recorded");
                    Assert.That(VpGpuStop.CommandWritesIssued, Is.Zero);
                    Assert.That(VpGpuStop.FrameUploadsIssued, Is.EqualTo(frameUploadsAtDone), "no per-frame upload after the registration");
                }

                if (stage == 2)
                {
                    Assert.That(initialBeforeDone, Is.GreaterThan(0), "stage 2 keeps the initial uploads (the registration's data included)");
                }

                if (stage <= 3)
                {
                    Assert.That(VpGpuStop.AllocationsMade, Is.EqualTo(allocationsBeforeDone).And.EqualTo(28), "the body batch's 22 buffers and the camera's stencil batch's 6, the same at every stage that allocates");
                }

                if (stage >= 3)
                {
                    Assert.That(VpGpuStop.InitialUploadsIssued, Is.Zero, "no initial upload");
                    if (stage == 3) Assert.That(VpGpuStop.InitialUploadsSkipped, Is.GreaterThan(0), "the whole writes and the registration's data were asked and stopped");
                }

                if (stage >= 4)
                {
                    Assert.That(root.Display.ShownCount, Is.Zero);
                    Assert.That(VpGpuStop.AllocationsMade, Is.Zero);
                }

                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                display?.Dispose();
                yield return EndWorld(root);
            }
            finally
            {
                VpGpuStop.Reset();
            }
        }
    }
}
