using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's guard of the game's view (Sandbox/GameViewGuard, TL 2026-10-02): the game camera's parent is the Camera
    /// Offset until the check's own eye level hangs it under the pivot it made there -- noted where it is made, checked by
    /// reference -- and that pivot after. With the eye level, with and without a capture camera: passed. Without it, a
    /// re-parenting is a failure; so are an unrelated parent, another pivot of the same name, a return to the Camera
    /// Offset after the eye level, and an eye level whose pivot is not under the original Camera Offset.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private sealed class ViewRig
        {
            public GameObject offset, cameraObject;
            public Camera game;
            public readonly List<GameObject> made = new List<GameObject>();

            public static ViewRig Make()
            {
                var rig = new ViewRig { offset = new GameObject("Guard Test Camera Offset") };
                rig.cameraObject = new GameObject("Guard Test Game Camera");
                rig.cameraObject.transform.SetParent(rig.offset.transform, false);
                rig.cameraObject.transform.localPosition = new Vector3(0f, 1.6f, 0f);
                rig.cameraObject.tag = "MainCamera";
                rig.game = rig.cameraObject.AddComponent<Camera>();
                return rig;
            }

            public Transform NewPivot(string name, Transform under)
            {
                var go = new GameObject(name);
                go.transform.SetParent(under, false);
                made.Add(go);
                return go.transform;
            }

            public void Destroy()
            {
                foreach (GameObject go in made) Object.Destroy(go);
                Object.Destroy(cameraObject);
                Object.Destroy(offset);
            }
        }

        /// <summary>A few frames of the guard reading the rig as the game would have it read.</summary>
        private static IEnumerator Read(GameViewGuard guard, ViewRig rig, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                guard.Frame(rig.game.transform, new[] { Camera.main }, Camera.main);
            }
        }

        private static IEnumerator EyeLevelRun(bool withCapture, List<string> outcome)
        {
            ViewRig rig = ViewRig.Make();
            CheckCaptureCamera capture = null;
            if (withCapture) capture = new CheckCaptureCamera(null, rig.game, null, 6f, 4f, 3f, 80f, 64, 36);
            var guard = new GameViewGuard(rig.game);
            yield return Read(guard, rig, 3);
            Transform pivot = rig.NewPivot("Check View Level", rig.offset.transform);
            rig.game.transform.SetParent(pivot, false);
            guard.NoteEyeLevelReparent(pivot);
            yield return Read(guard, rig, 3);
            outcome.Add(guard.Passed + " | " + guard.Describe());
            capture?.Dispose();
            rig.Destroy();
        }

        /// <summary>**With the eye level, with and without a capture camera: passed, the one re-parenting recorded.**</summary>
        [UnityTest]
        public IEnumerator GameViewGuard_TheEyeLevelsOwnPivot_Passes_WithAndWithoutACaptureCamera()
        {
            var outcomes = new List<string>();
            yield return EyeLevelRun(false, outcomes);
            yield return EyeLevelRun(true, outcomes);
            foreach (string o in outcomes) TestContext.Out.WriteLine(o);
            Assert.That(outcomes[0], Does.StartWith("True"), "without a capture camera");
            Assert.That(outcomes[1], Does.StartWith("True"), "with a capture camera");
            StringAssert.Contains("the eye level hung the game camera from Guard Test Camera Offset to Check View Level", outcomes[0]);
        }

        /// <summary>**A capture camera fixed in the world stays where it was put while the head moves and turns; the game still reads its own camera.**</summary>
        [UnityTest]
        public IEnumerator CaptureCamera_FixedInTheWorld_StaysPut_WhileTheHeadMoves()
        {
            ViewRig rig = ViewRig.Make();
            var at = new Vector3(-10f, 14f, -74f);
            var look = new Vector3(-38f, 7f, -88f);
            var capture = new CheckCaptureCamera(null, rig.game, null, 6f, 4f, 3f, 62f, 64, 36,
                new[] { new CheckCaptureCamera.Shot { name = "fixed", position = at, lookAt = look, fieldOfView = 62f } });
            var guard = new GameViewGuard(rig.game);
            for (int i = 0; i < 10; i++)
            {
                rig.game.transform.localPosition += new Vector3(0.3f, 0.05f, -0.2f);
                rig.game.transform.localRotation *= Quaternion.Euler(2f, 15f, 1f);
                capture.Place();
                yield return null;
                guard.Frame(rig.game.transform, new[] { Camera.main }, Camera.main);
            }

            TestContext.Out.WriteLine(capture.Describe() + " | the head now at " + rig.game.transform.position + " | guard " + guard.Describe());
            Assert.That(capture.Rendered, Is.GreaterThan(0));
            Assert.That((capture.Camera.transform.position - at).magnitude, Is.LessThan(1e-5f), "where it was fixed");
            Assert.That(Quaternion.Angle(capture.Camera.transform.rotation, Quaternion.LookRotation(look - at, Vector3.up)), Is.LessThan(1e-3f), "facing where it was fixed");
            Assert.That(capture.MaxFixedOffset, Is.LessThan(1e-4));
            Assert.That(capture.MaxFixedTurn, Is.LessThan(1e-3));
            Assert.That(guard.Passed, Is.True, "the game read its own camera throughout");
            Assert.That(Camera.main, Is.SameAs(rig.game));
            capture.Dispose();
            rig.Destroy();
        }

        /// <summary>**Fixed shots switched: each switch places the camera at its shot and records it; between switches it does not move, whatever the head does.**</summary>
        [UnityTest]
        public IEnumerator CaptureCamera_FixedShotsSwitched_EachStaysPutUntilTheNext()
        {
            ViewRig rig = ViewRig.Make();
            var shots = new[]
            {
                new CheckCaptureCamera.Shot { name = "seam", position = new Vector3(-22f, 12f, -97f), lookAt = new Vector3(-32.3f, 10.5f, -88f), fieldOfView = 45f },
                new CheckCaptureCamera.Shot { name = "base", position = new Vector3(-24f, 7f, -95f), lookAt = new Vector3(-32f, 1.5f, -89f), fieldOfView = 55f },
                new CheckCaptureCamera.Shot { name = "npc", position = new Vector3(-37f, 12f, -66f), lookAt = new Vector3(-37f, 1f, -79f), fieldOfView = 50f },
            };
            var capture = new CheckCaptureCamera(null, rig.game, null, 6f, 4f, 3f, 62f, 64, 36, shots);
            var guard = new GameViewGuard(rig.game);
            foreach (int next in new[] { 1, 2, 1 })
            {
                for (int i = 0; i < 4; i++)
                {
                    rig.game.transform.localPosition += new Vector3(0.2f, 0f, 0.1f);
                    rig.game.transform.localRotation *= Quaternion.Euler(0f, 20f, 0f);
                    capture.Place();
                    yield return null;
                    guard.Frame(rig.game.transform, new[] { Camera.main }, Camera.main);
                }

                CheckCaptureCamera.Shot now = shots[capture.ShotIndex];
                Assert.That((capture.Camera.transform.position - now.position).magnitude, Is.LessThan(1e-5f), now.name + ": where its shot put it");
                Assert.That(capture.Camera.fieldOfView, Is.EqualTo(now.fieldOfView), now.name + ": its field of view");
                capture.SetShot(next, "test");
            }

            TestContext.Out.WriteLine(capture.Describe() + " | switches: " + string.Join(" / ", capture.Switches));
            Assert.That(capture.Switches.Count, Is.EqualTo(4), "made at the first, then three switches");
            Assert.That(capture.MaxFixedOffset, Is.LessThan(1e-4), "never moved within a shot");
            Assert.That(capture.MaxFixedTurn, Is.LessThan(1e-3));
            Assert.That(guard.Passed, Is.True, "the game read its own camera throughout");
            capture.Dispose();
            rig.Destroy();
        }

        /// <summary>**A re-parenting the eye level did not make fails: none at all, an unrelated parent, another pivot of the same name, a return to the Camera Offset after it, a pivot not under the Camera Offset.**</summary>
        [UnityTest]
        public IEnumerator GameViewGuard_AnyOtherReparenting_Fails()
        {
            var cases = new List<(string name, System.Func<ViewRig, GameViewGuard, Transform> act)>
            {
                ("no eye level, hung under a pivot anyway", (rig, guard) => { Transform p = rig.NewPivot("Check View Level", rig.offset.transform); rig.game.transform.SetParent(p, false); return null; }),
                ("an unrelated parent", (rig, guard) => { Transform p = rig.NewPivot("Somewhere Else", null); rig.game.transform.SetParent(p, false); return null; }),
                ("another pivot of the same name after the eye level", (rig, guard) =>
                {
                    Transform p = rig.NewPivot("Check View Level", rig.offset.transform);
                    rig.game.transform.SetParent(p, false);
                    guard.NoteEyeLevelReparent(p);
                    Transform twin = rig.NewPivot("Check View Level", rig.offset.transform);
                    rig.game.transform.SetParent(twin, false);
                    return p;
                }),
                ("back to the Camera Offset after the eye level", (rig, guard) =>
                {
                    Transform p = rig.NewPivot("Check View Level", rig.offset.transform);
                    rig.game.transform.SetParent(p, false);
                    guard.NoteEyeLevelReparent(p);
                    rig.game.transform.SetParent(rig.offset.transform, false);
                    return p;
                }),
                ("an eye level whose pivot is not under the Camera Offset", (rig, guard) =>
                {
                    Transform p = rig.NewPivot("Check View Level", null);
                    rig.game.transform.SetParent(p, false);
                    guard.NoteEyeLevelReparent(p);
                    return p;
                }),
            };

            foreach ((string name, System.Func<ViewRig, GameViewGuard, Transform> act) in cases)
            {
                ViewRig rig = ViewRig.Make();
                var guard = new GameViewGuard(rig.game);
                yield return Read(guard, rig, 2);
                act(rig, guard);
                yield return Read(guard, rig, 2);
                TestContext.Out.WriteLine(name + ": " + guard.Passed + " | " + guard.Describe());
                Assert.That(guard.Passed, Is.False, name + ": a failure");
                Assert.That(guard.ParentOther + guard.BadReparents, Is.GreaterThan(0), name + ": counted as a parent or a re-parenting not the eye level's");
                rig.Destroy();
                yield return null;
            }
        }
    }
}
