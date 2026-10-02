using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The view diagnosis (2026-10-02, diagnosis only): off unless asked -- nothing made, nothing written; on, it records
    /// and changes nothing it reads (the camera's pose, the enabled actions, the cameras), and it refuses an existing file.
    /// </summary>
    public class ViewDiagnosisPlayModeTests
    {
        private string _dir;

        [TearDown]
        public void StopIfRunning()
        {
            ViewDiagnosis.Stop();
        }

        [UnityTest]
        public IEnumerator ViewDiagnosis_Off_MakesNothing_AndWritesNothing()
        {
            Assume.That(Environment.GetCommandLineArgs().Contains(ViewDiagnosis.Argument), Is.False, "this session was started with the diagnosis on");
            for (int i = 0; i < 10; i++) yield return null;

            Assert.That(ViewDiagnosis.StartedFromArgument, Is.False, "the session's start made nothing without the argument");
            Assert.That(ViewDiagnosis.Active, Is.Null);
            Assert.That(GameObject.Find("View diagnosis"), Is.Null, "no host while off");
        }

        // The camera the session holds as Camera.main at the start -- whatever it is, possibly none or another fixture's
        // (2026-10-02: a scene loaded by an earlier test was still there in a full run) -- is the one observed, held by
        // reference and matched in the record by its instance id; its pose is only read.
        [UnityTest]
        public IEnumerator ViewDiagnosis_On_ObservesTheCameraMainOfItsStart_AndChangesNothingItReads()
        {
            Camera main = Camera.main;
            Quaternion rotation = main != null ? main.transform.localRotation : Quaternion.identity;
            Vector3 position = main != null ? main.transform.localPosition : Vector3.zero;
            int actions = ViewDiagnosis.EnabledActionCount();
            int cameras = Camera.allCamerasCount;
            _dir = Path.Combine(Application.temporaryCachePath, "view-diagnosis-" + Guid.NewGuid().ToString("N"));
            ViewDiagnosis.Recorder r = ViewDiagnosis.Start(_dir);
            Assert.That(ViewDiagnosis.Active, Is.SameAs(r));
            Assert.That(ReferenceEquals(r.ObservedCamera, main) || (main == null && r.ObservedCamera == null), Is.True, "the camera observed is the Camera.main of the start");
            for (int i = 0; i < 30; i++) yield return null;

            if (main != null)
            {
                Assert.That(main.transform.localRotation == rotation && main.transform.localPosition == position, Is.True, "the observed camera's pose is only read");
            }

            Assert.That(ViewDiagnosis.EnabledActionCount(), Is.EqualTo(actions), "no action enabled");
            Assert.That(Camera.allCamerasCount, Is.EqualTo(cameras), "no camera made");
            ViewDiagnosis.Stop();
            Assert.That(ViewDiagnosis.Active, Is.Null);

            string[] samples = File.ReadAllLines(Path.Combine(_dir, "view-samples.csv"));
            string state = File.ReadAllText(Path.Combine(_dir, "view-state.txt"));
            Assert.That(samples[0], Does.StartWith("kind,frame,ms,seq"));
            Assert.That(samples.Count(l => l.StartsWith("U,", StringComparison.Ordinal)), Is.GreaterThanOrEqualTo(25), "an Update row a frame");
            // Application.onBeforeRender is called only when something renders: a batch-mode test run renders nothing (vd-play, 2026-10-02).
            if (!Application.isBatchMode) Assert.That(samples.Count(l => l.StartsWith("B,", StringComparison.Ordinal)), Is.GreaterThan(0), "BeforeRender rows");
            string found = "found: Camera.main " + (main != null ? main.name + " (instance " + main.GetInstanceID() + ")" : "none");
            Assert.That(state, Does.Contain("start:").And.Contain(found).And.Contain("stop:"));
        }

        // A camera of the test's own, in a scene of its own, given to the diagnosis explicitly: independent of any other camera
        // in the session. It is observed (the record has it, every Update row reads it) and its pose is only read.
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
            _dir = Path.Combine(Application.temporaryCachePath, "view-diagnosis-" + Guid.NewGuid().ToString("N"));
            try
            {
                ViewDiagnosis.Recorder r = ViewDiagnosis.Start(_dir, camera);
                Assert.That(r.ObservedCamera, Is.SameAs(camera), "the camera given is the one observed");
                for (int i = 0; i < 30; i++) yield return null;

                Assert.That(cameraObject.transform.localRotation == rotation && cameraObject.transform.localPosition == position, Is.True, "the camera's pose is only read");
                Assert.That(ViewDiagnosis.EnabledActionCount(), Is.EqualTo(actions), "no action enabled");
                ViewDiagnosis.Stop();

                string[] samples = File.ReadAllLines(Path.Combine(_dir, "view-samples.csv"));
                int camFound = Array.IndexOf(samples[0].Split(','), "camFound");
                string[] updates = samples.Where(l => l.StartsWith("U,", StringComparison.Ordinal)).ToArray();
                Assert.That(updates.Length, Is.GreaterThanOrEqualTo(25));
                Assert.That(updates.All(l => l.Split(',')[camFound] == "1"), Is.True, "every Update row read the camera");
                Assert.That(File.ReadAllText(Path.Combine(_dir, "view-state.txt")),
                    Does.Contain("found: the camera given diagnosis fixture camera (instance " + camera.GetInstanceID() + ")"));
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
        public void ViewDiagnosis_AnExistingFile_IsRefused_AndNothingIsLeftRunning()
        {
            _dir = Path.Combine(Application.temporaryCachePath, "view-diagnosis-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "view-samples.csv"), "kept");
            Assert.Throws<IOException>(() => ViewDiagnosis.Start(_dir));
            Assert.That(ViewDiagnosis.Active, Is.Null);
            Assert.That(GameObject.Find("View diagnosis"), Is.Null);
            Assert.That(File.ReadAllText(Path.Combine(_dir, "view-samples.csv")), Is.EqualTo("kept"), "the existing file is untouched");
        }
    }
}
