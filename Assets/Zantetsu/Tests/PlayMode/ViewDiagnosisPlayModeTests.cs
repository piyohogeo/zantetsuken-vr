using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The view diagnosis (2026-10-02, diagnosis only): off unless asked -- nothing made, nothing recorded; on, it records to
    /// the development logger and changes nothing it reads (the camera's pose, the enabled actions, the cameras); its record
    /// has an explicit end after which nothing more is tried. What the saved records hold is checked after the run against
    /// the "[view diagnosis] ended" lines of the log (check_writer_jsonl.py); here, what the recorder tried and was accepted.
    /// </summary>
    public class ViewDiagnosisPlayModeTests
    {
        [TearDown]
        public void StopIfRunning()
        {
            ViewDiagnosis.Stop();
        }

        [UnityTest]
        public IEnumerator ViewDiagnosis_Off_MakesNothing_AndRecordsNothing()
        {
            Assume.That(Environment.GetCommandLineArgs().Contains(ViewDiagnosis.Argument), Is.False, "this session was started with the diagnosis on");
            for (int i = 0; i < 10; i++) yield return null;

            Assert.That(ViewDiagnosis.StartedFromArgument, Is.False, "the session's start made nothing without the argument");
            Assert.That(ViewDiagnosis.Active, Is.Null);
            Assert.That(GameObject.Find("View diagnosis"), Is.Null, "no host while off");
        }

        // The camera the session holds as Camera.main at the start -- whatever it is, possibly none or another fixture's
        // (2026-10-02: a scene loaded by an earlier test was still there in a full run) -- is the one observed, held by
        // reference and named in the record by its instance id; its pose is only read.
        [UnityTest]
        public IEnumerator ViewDiagnosis_On_ObservesTheCameraMainOfItsStart_AndChangesNothingItReads()
        {
            Camera main = Camera.main;
            Quaternion rotation = main != null ? main.transform.localRotation : Quaternion.identity;
            Vector3 position = main != null ? main.transform.localPosition : Vector3.zero;
            int actions = ViewDiagnosis.EnabledActionCount();
            int cameras = Camera.allCamerasCount;
            ViewDiagnosis.Recorder r = ViewDiagnosis.Start();
            Assert.That(ViewDiagnosis.Active, Is.SameAs(r));
            Assert.That(ReferenceEquals(r.ObservedCamera, main) || (main == null && r.ObservedCamera == null), Is.True, "the camera observed is the Camera.main of the start");
            for (int i = 0; i < 30; i++) yield return null;

            if (main != null)
            {
                Assert.That(main.transform.localRotation == rotation && main.transform.localPosition == position, Is.True, "the observed camera's pose is only read");
            }

            Assert.That(ViewDiagnosis.EnabledActionCount(), Is.EqualTo(actions), "no action enabled");
            Assert.That(Camera.allCamerasCount, Is.EqualTo(cameras), "no camera made");
            Assert.That(r.Updates, Is.GreaterThanOrEqualTo(25), "an Update sample a frame");
            // Application.onBeforeRender is called only when something renders: a batch-mode test run renders nothing (vd-play, 2026-10-02).
            if (!Application.isBatchMode) Assert.That(r.BeforeRenders, Is.GreaterThan(0), "BeforeRender samples");
            ViewDiagnosis.Stop();
            Assert.That(ViewDiagnosis.Active, Is.Null);
            Assert.That(r == null, Is.True, "the recorder is gone");
        }

        // A camera of the test's own, in a scene of its own, given to the diagnosis explicitly: independent of any other camera
        // in the session. It is observed and its pose is only read.
        [UnityTest]
        public IEnumerator ViewDiagnosis_On_WithAGivenCamera_InAnIsolatedScene_ReadsItAndChangesNothing()
        {
            Scene before = SceneManager.GetActiveScene();
            Scene fixture = SceneManager.CreateScene("view diagnosis fixture " + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(fixture);
            var cameraObject = new GameObject("diagnosis fixture camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f));
            Quaternion rotation = cameraObject.transform.localRotation;
            Vector3 position = cameraObject.transform.localPosition;
            int actions = ViewDiagnosis.EnabledActionCount();
            try
            {
                ViewDiagnosis.Recorder r = ViewDiagnosis.Start(camera);
                Assert.That(r.ObservedCamera, Is.SameAs(camera), "the camera given is the one observed");
                Debug.Log("[view diagnosis test] given camera instance " + camera.GetInstanceID());
                for (int i = 0; i < 30; i++) yield return null;

                Assert.That(cameraObject.transform.localRotation == rotation && cameraObject.transform.localPosition == position, Is.True, "the camera's pose is only read");
                Assert.That(ViewDiagnosis.EnabledActionCount(), Is.EqualTo(actions), "no action enabled");
                Assert.That(r.Updates, Is.GreaterThanOrEqualTo(25));
                Assert.That(r.Accepted, Is.EqualTo(r.Attempted), "every record accepted");
            }
            finally
            {
                ViewDiagnosis.Stop();
                UnityEngine.Object.Destroy(cameraObject);
                if (before.IsValid() && before.isLoaded) SceneManager.SetActiveScene(before);
            }

            yield return SceneManager.UnloadSceneAsync(fixture);
        }

        [Test]
        public void ViewDiagnosis_AlreadyRecording_IsRefused_AndTheRunningOneIsKept()
        {
            ViewDiagnosis.Recorder r = ViewDiagnosis.Start();
            int created = ViewDiagnosis.Created;
            Assert.Throws<InvalidOperationException>(() => ViewDiagnosis.Start());
            Assert.That(ViewDiagnosis.Active, Is.SameAs(r));
            Assert.That(ViewDiagnosis.Created, Is.EqualTo(created), "nothing more made");
        }

        // Focus, pause, the person's marks and device changes are records of their own, told apart by their tag: made here
        // directly on the recorder (the window's focus and the application's pause are not moved; the device is the test's
        // own, removed again). Their content is checked in the saved file after the run.
        [Test]
        public void ViewDiagnosis_Events_AreRecordsOfTheirOwn()
        {
            ViewDiagnosis.Recorder r = ViewDiagnosis.Start();
            long before = r.Attempted;
            r.gameObject.SendMessage("OnApplicationFocus", false);
            r.gameObject.SendMessage("OnApplicationFocus", true);
            r.gameObject.SendMessage("OnApplicationPause", true);
            r.gameObject.SendMessage("OnApplicationPause", false);
            r.MarkEdge(false);
            r.MarkEdge(true);
            r.MarkEdge(true);
            r.MarkEdge(false);
            r.MarkEdge(true);
            Gamepad pad = InputSystem.AddDevice<Gamepad>("view diagnosis test pad");
            InputSystem.RemoveDevice(pad);
            Debug.Log("[view diagnosis test] events: focus 2, pause 2, mark 2, device >= 2 from seq " + (before + 1));
            Assert.That(r.Attempted - before, Is.GreaterThanOrEqualTo(8), "focus 2, pause 2, marks 2, device added and removed");
            Assert.That(r.Accepted, Is.EqualTo(r.Attempted));
        }

        // The end stops the record: after it no sample, event or second summary is tried, though the recorder is still there
        // and its stages still come (counted, not recorded).
        [UnityTest]
        public IEnumerator ViewDiagnosis_AfterTheEnd_NothingMoreIsRecorded()
        {
            ViewDiagnosis.Recorder r = ViewDiagnosis.Start();
            for (int i = 0; i < 10; i++) yield return null;

            r.End();
            Assert.That(r.Ended, Is.True);
            long attempted = r.Attempted, accepted = r.Accepted;
            int updates = r.Updates;
            Assert.That(accepted, Is.EqualTo(attempted), "every record up to the summary accepted");
            for (int i = 0; i < 10; i++) yield return null;

            r.gameObject.SendMessage("OnApplicationFocus", false);
            r.MarkEdge(false);
            r.MarkEdge(true);
            r.End();
            Assert.That(r.Updates, Is.GreaterThan(updates), "the stages still come");
            Assert.That(r.Attempted, Is.EqualTo(attempted), "nothing tried after the end");
            Assert.That(r.Accepted, Is.EqualTo(accepted));
            ViewDiagnosis.Stop();
            Assert.That(ViewDiagnosis.Active, Is.Null);
        }

        // The quit request ends the record (before Application.quitting, where the logger's session stops) and does not hold the quit.
        [Test]
        public void ViewDiagnosis_TheQuitRequest_EndsTheRecord_AndLetsTheQuitGoOn()
        {
            ViewDiagnosis.Recorder r = ViewDiagnosis.Start();
            Assert.That(r.EndOnQuitRequest(), Is.True, "the quit is not held");
            Assert.That(r.Ended, Is.True);
        }
    }
}
