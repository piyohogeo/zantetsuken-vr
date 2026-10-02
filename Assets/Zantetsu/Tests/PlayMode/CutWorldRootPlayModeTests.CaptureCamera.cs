using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Input;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's capture camera (Sandbox/CheckCaptureCamera, TL 2026-10-01) beside a game camera it never touches. With
    /// one fixed input -- the same blade samples, one stroke along the view and one against it -- a katana reading the
    /// game camera gives the same view forward, the same begin decision and the same waves with and without the capture
    /// camera (made, and moved before every sample); Camera.main, the lifetime's camera and its eyes' planes, and the game
    /// camera's parent, tag, stereo target, target and pose stay as they were. The capture camera is drawn for by the
    /// display (its own stencil caps, the building's display in its picture); its sink saves what it is asked to, and the
    /// two leave nothing behind (the camera at once, the sink when it has settled, its thread ended).
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private sealed class KatanaReading
        {
            public readonly List<string> steps = new List<string>();
            public int waves;
        }

        private static readonly System.Reflection.FieldInfo s_katanaViewField = typeof(SandboxRightHandKatana).GetField("viewForwardReference", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>The same blade samples into a fresh katana reading <paramref name="view"/>: one stroke along the view, a pause, one against it.</summary>
        private static KatanaReading FeedKatana(Transform view, System.Action beforeEachSample)
        {
            var rig = new GameObject("Capture Test Katana Rig");
            var blade = new GameObject("Capture Test Katana");
            try
            {
                var katana = rig.AddComponent<SandboxRightHandKatana>();
                katana.Katana = blade.transform;
                s_katanaViewField.SetValue(katana, view);
                Assert.That(katana.TrySetBeginBladeAxisViewDotMinimum(0.5f), Is.True, "the begin view check made to matter");
                var reading = new KatanaReading();
                long frame = 0;
                double time = 0.0;
                foreach (Quaternion grip in new[] { Quaternion.LookRotation(Vector3.back), Quaternion.LookRotation(Vector3.forward) })
                {
                    for (int i = 0; i < 8; i++)
                    {
                        beforeEachSample?.Invoke();
                        var sample = new BladePoseSample(++frame, time += 0.011, new Vector3(0f, 1.6f - 0.12f * i, 2.5f), grip, BladeTrackingState.Position | BladeTrackingState.Rotation);
                        Vector3 viewForward = katana.CurrentViewForward;
                        bool taken = katana.TryRecordSample(sample);
                        reading.steps.Add(frame + ": view " + viewForward.ToString("R") + ", taken " + taken + ", waves " + katana.WaveCount + ", begin view " + katana.StrokeBeginViewForward.ToString("R"));
                        reading.waves = System.Math.Max(reading.waves, katana.WaveCount);
                    }

                    time += 0.5;   // a pause: the stroke ends
                }

                return reading;
            }
            finally
            {
                Object.DestroyImmediate(rig);
                Object.DestroyImmediate(blade);
            }
        }

        /// <summary>
        /// **The capture camera leaves the game's view alone: the same view, begin decisions and waves for one fixed input;
        /// Camera.main, the lifetime's camera and eyes, and the game camera itself unchanged; drawn for by the display;
        /// nothing left behind.**
        /// </summary>
        [UnityTest]
        public IEnumerator CaptureCamera_LeavesTheGameViewAlone_AndIsDrawnForByTheDisplay()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);

            // The game's camera: under its tracking offset, tagged, looking at the building.
            var offset = new GameObject("Capture Test Camera Offset");
            var gameObject = new GameObject("Capture Test Game Camera");
            gameObject.transform.SetParent(offset.transform, false);
            gameObject.transform.SetLocalPositionAndRotation(new Vector3(0f, 1.6f, 4f), Quaternion.LookRotation(Vector3.back));
            gameObject.tag = "MainCamera";
            Camera game = gameObject.AddComponent<Camera>();
            // The world's camera drawing, for the game camera (the capture camera is added to it, as in the Player).
            var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
            typeof(CutWorldCameraDrawing).GetField("world", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(drawing, root);
            typeof(CutWorldCameraDrawing).GetField("cameras", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(drawing, new[] { game });
            yield return null;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return UntilHull(root, () => h.GroupCuts == 1 && Quiet(h), 30f, "the building cut and settled");

            Assert.That(Camera.main, Is.SameAs(game), "the game camera is Camera.main");
            int camerasBefore = Camera.allCamerasCount;
            Transform parent = game.transform.parent;
            Vector3 position = game.transform.localPosition;
            Quaternion rotation = game.transform.localRotation;
            StereoTargetEyeMask eyes = game.stereoTargetEye;
            var planesBefore = new Plane[VpInstanceCulling.EyePlaneCount * 2];
            int planeCount = VpInstanceCulling.GetEyePlanes(game, planesBefore);
            Camera lifetimeBefore = root.Lifetime.ViewCamera != null ? root.Lifetime.ViewCamera : Camera.main;

            // The fixed input without a capture camera.
            KatanaReading without = FeedKatana(game.transform, null);

            // The capture camera made, saving, moved before every sample; the same input again.
            string dir = Path.Combine(Application.temporaryCachePath, "capture-camera-test");
            if (Directory.Exists(dir)) foreach (string f in Directory.GetFiles(dir)) File.Delete(f);
            int workersBefore = CheckFrameSink.LiveWorkers;
            var sink = new CheckFrameSink("capture test", dir, 320, 180, 3, new CheckJpegFrameWriter());
            var capture = new CheckCaptureCamera(root, game, sink, 3f, 1f, -10f, 70f, 320, 180);   // set back 3 m, up 1 m, pitched down to the small test box
            capture.Camera.clearFlags = CameraClearFlags.SolidColor;
            capture.Camera.backgroundColor = Color.black;
            sink.StartSaving();
            Vector3 captureAt = capture.Camera.transform.position;
            KatanaReading with = FeedKatana(game.transform, () => capture.Place());
            TestContext.Out.WriteLine("the fixed input: waves without " + without.waves + ", with " + with.waves);
            for (int i = 0; i < without.steps.Count; i++) TestContext.Out.WriteLine("  " + without.steps[i] + (without.steps[i] == with.steps[i] ? "" : "  <> " + with.steps[i]));
            Assert.That(without.waves, Is.GreaterThan(0), "the stroke along the view made a wave (the check engaged)");
            Assert.That(with.steps, Is.EqualTo(without.steps), "the same view forward, begin decision and waves at every sample");

            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(Camera.main, Is.SameAs(game), "Camera.main still the game camera");
            Assert.That(game.transform.parent, Is.SameAs(parent));
            Assert.That(game.tag, Is.EqualTo("MainCamera"));
            Assert.That(game.stereoTargetEye, Is.EqualTo(eyes));
            Assert.That(game.targetTexture, Is.Null);
            Assert.That(game.transform.localPosition, Is.EqualTo(position));
            Assert.That(game.transform.localRotation, Is.EqualTo(rotation));
            Assert.That(root.Lifetime.ViewCamera != null ? root.Lifetime.ViewCamera : Camera.main, Is.SameAs(lifetimeBefore), "the lifetime's camera");
            var planesAfter = new Plane[VpInstanceCulling.EyePlaneCount * 2];
            Assert.That(VpInstanceCulling.GetEyePlanes(game, planesAfter), Is.EqualTo(planeCount));
            for (int i = 0; i < planeCount; i++) Assert.That(planesAfter[i].normal == planesBefore[i].normal && planesAfter[i].distance == planesBefore[i].distance, Is.True, "the eyes' plane " + i);
            Assert.That(capture.Camera.CompareTag("MainCamera"), Is.False, "the capture camera is untagged");
            Assert.That(capture.Camera.transform.position, Is.Not.EqualTo(game.transform.position), "placed apart from the game camera");

            // Drawn for by the display: its own stencil caps, and the building's display in its picture (black where nothing is drawn).
            Assert.That(capture.ListedWithDrawing && capture.TakenByDisplay, Is.True);
            Assert.That(root.Display.TryGetCameraStencil(capture.Camera, out _, out VpStencilCameraCounts counts), Is.True);
            Assert.That(root.Display.TryGetCameraStencil(game, out _, out VpStencilCameraCounts gameCounts), Is.True);
            RenderTexture.active = capture.Camera.targetTexture;
            var picture = new Texture2D(capture.width, capture.height, TextureFormat.RGB24, false);
            picture.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
            picture.Apply();
            RenderTexture.active = null;
            int drawn = 0;
            foreach (Color32 c in picture.GetPixels32()) if (c.r + c.g + c.b > 30) drawn++;
            Object.Destroy(picture);
            TestContext.Out.WriteLine("the capture camera: " + capture.Describe() + "; its stencil uploads " + counts.uploads + ", cap issues " + counts.capIssues + ", volume issues " + counts.volumeIssues + " (the game camera's: uploads " + gameCounts.uploads + ", cap issues " + gameCounts.capIssues + "); pixels drawn " + drawn + " of " + (capture.width * capture.height));
            Assert.That(capture.Rendered, Is.GreaterThan(0), "rendered by the pipeline");
            Assert.That(counts.uploads, Is.GreaterThan(0), "its own stencil arrangement prepared and uploaded, frame by frame, like the game camera's");
            Assert.That(drawn, Is.GreaterThan(capture.width * capture.height / 100), "the building's display in its picture");

            Camera captureCamera = capture.Camera;
            RenderTexture target = capture.Target;
            capture.Dispose();
            sink.BeginFinish("the test's end");
            for (int i = 0; i < 600 && !sink.Settled; i++) yield return null;
            TestContext.Out.WriteLine("the sink at the end: " + sink.Describe());
            Assert.That(sink.Now, Is.EqualTo(CheckFrameSink.State.Finished));
            Assert.That(sink.ledger.Requested, Is.EqualTo(3), "the frames asked");
            Assert.That(sink.ledger.AllAccounted && sink.ledger.Errors == 0, Is.True, "each written or skipped by the bounds, none in error");
            Assert.That(sink.ledger.Written, Is.GreaterThan(0), "written");
            Assert.That(Directory.GetFiles(dir, "*.jpg").Length, Is.EqualTo(sink.ledger.Written));
            Assert.That(File.Exists(Path.Combine(dir, "frames.csv")), Is.True);
            Assert.That(CheckFrameSink.LiveWorkers, Is.EqualTo(workersBefore), "its thread ended");
            yield return null;
            Assert.That(target == null, Is.True, "the capture texture released by the sink");
            Assert.That(GameObject.Find("Check Capture Camera"), Is.Null, "the capture camera destroyed");
            Assert.That(Camera.allCamerasCount, Is.EqualTo(camerasBefore), "no camera left behind");
            Assert.That(root.Display.TryGetCameraStencil(captureCamera, out _, out _), Is.False, "unregistered from the display");
            Object.Destroy(drawing);
            Object.Destroy(offset);
            yield return EndWorld(root);
        }
    }
}
