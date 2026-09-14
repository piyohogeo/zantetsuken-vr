using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.Core.Input;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Phase 0.5 / T-014 focused tests. Values are pushed straight at the
    /// input boundary (<c>SandboxRightHandKatana.TryApplySample</c>), so no XR
    /// hardware emulation framework is involved. Everything is checked through
    /// the katana's visibility and transform, which is the whole observable
    /// result of the component.
    /// </summary>
    public class SandboxRightHandKatanaTests
    {
        private const string SandboxScenePath = "Assets/Scenes/Sandbox.unity";
        private const float PositionTolerance = 1e-4f;
        private const float AngleTolerance = 1e-3f;
        private const float BladeLength = 0.9f;

        private GameObject rigObject;
        private GameObject katanaObject;
        private SandboxRightHandKatana follower;

        [SetUp]
        public void SetUp()
        {
            rigObject = new GameObject("Sandbox Katana Rig");
            katanaObject = new GameObject("Katana");
            follower = rigObject.AddComponent<SandboxRightHandKatana>();
            follower.Katana = katanaObject.transform;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // If a test failed while in Play Mode (before reaching ExitPlayMode),
            // leave Play Mode so subsequent tests run from a clean EditMode state.
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            // A test that opens the sandbox scene or enters Play Mode destroys
            // these along with the scene they were created in.
            if (katanaObject != null)
            {
                Object.DestroyImmediate(katanaObject);
            }

            if (rigObject != null)
            {
                Object.DestroyImmediate(rigObject);
            }
        }

        private static BladePoseSample Sample(Vector3 position, Quaternion rotation, BladeTrackingState state)
        {
            return new BladePoseSample(7, 1.25, position, rotation, state);
        }

        private static BladePoseSample Tracked(Vector3 position, Quaternion rotation)
        {
            return Sample(position, rotation, BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(PositionTolerance),
                "expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
        }

        private void AssertKatanaShows(Vector3 gripPosition, Quaternion gripRotation)
        {
            Assert.That(katanaObject.activeSelf, Is.True, "The katana is hidden.");
            Pose offset = follower.GripToKatanaOffset;
            AssertVector(katanaObject.transform.position, gripPosition + gripRotation * offset.position);
            Assert.That(Quaternion.Angle(katanaObject.transform.rotation, gripRotation * offset.rotation),
                Is.LessThan(AngleTolerance));
        }

        [Test]
        public void BeforeAnyValidSample_TheKatanaIsHidden()
        {
            Assert.That(katanaObject.activeSelf, Is.False);
        }

        [Test]
        public void SandboxScene_SavesTheKatanaHidden()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SandboxScenePath, OpenSceneMode.Single);
                List<GameObject> katanas = new List<GameObject>();
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (child.name == "Katana")
                        {
                            katanas.Add(child.gameObject);
                        }
                    }
                }

                Assert.That(katanas.Count, Is.EqualTo(1));
                Assert.That(katanas[0].activeSelf, Is.False,
                    "The saved scene must not show the katana before the first usable grip pose.");
            }
            finally
            {
                if (setup != null && setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        [Test]
        public void FullyTrackedGripPose_IsAppliedToKatanaTransform()
        {
            Vector3 gripPosition = new Vector3(0.4f, 1.1f, 0.35f);
            Quaternion gripRotation = Quaternion.Euler(12f, 34f, 56f);

            Assert.That(follower.TryApplySample(Tracked(gripPosition, gripRotation)), Is.True);

            AssertKatanaShows(gripPosition, gripRotation);
        }

        [Test]
        public void PositionOnly_LeavesKatanaTransformUntouchedAndHidesIt()
        {
            Vector3 before = katanaObject.transform.position;
            Quaternion beforeRotation = katanaObject.transform.rotation;

            bool applied = follower.TryApplySample(
                Sample(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), BladeTrackingState.Position));

            Assert.That(applied, Is.False);
            Assert.That(katanaObject.activeSelf, Is.False);
            AssertVector(katanaObject.transform.position, before);
            Assert.That(Quaternion.Angle(katanaObject.transform.rotation, beforeRotation), Is.LessThan(AngleTolerance));
        }

        [Test]
        public void RotationOnly_LeavesKatanaTransformUntouchedAndHidesIt()
        {
            Vector3 before = katanaObject.transform.position;
            Quaternion beforeRotation = katanaObject.transform.rotation;

            bool applied = follower.TryApplySample(
                Sample(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), BladeTrackingState.Rotation));

            Assert.That(applied, Is.False);
            Assert.That(katanaObject.activeSelf, Is.False);
            AssertVector(katanaObject.transform.position, before);
            Assert.That(Quaternion.Angle(katanaObject.transform.rotation, beforeRotation), Is.LessThan(AngleTolerance));
        }

        [Test]
        public void UntrackedSample_HidesTheKatanaWithoutOverwritingTheAppliedPose()
        {
            Vector3 gripPosition = new Vector3(0.4f, 1.1f, 0.35f);
            Quaternion gripRotation = Quaternion.Euler(12f, 34f, 56f);
            Assert.That(follower.TryApplySample(Tracked(gripPosition, gripRotation)), Is.True);

            Vector3 applied = katanaObject.transform.position;
            Quaternion appliedRotation = katanaObject.transform.rotation;

            Assert.That(
                follower.TryApplySample(Sample(new Vector3(9f, 9f, 9f), Quaternion.Euler(90f, 0f, 0f), BladeTrackingState.None)),
                Is.False);

            Assert.That(katanaObject.activeSelf, Is.False);
            AssertVector(katanaObject.transform.position, applied);
            Assert.That(Quaternion.Angle(katanaObject.transform.rotation, appliedRotation), Is.LessThan(AngleTolerance));
        }

        [Test]
        public void AfterTrackingLoss_FollowingResumesFromTheNextValidGripPose()
        {
            Assert.That(follower.TryApplySample(Tracked(Vector3.zero, Quaternion.identity)), Is.True);
            Assert.That(follower.TryApplySample(Sample(Vector3.zero, Quaternion.identity, BladeTrackingState.None)), Is.False);
            Assert.That(katanaObject.activeSelf, Is.False);

            Vector3 recoveredPosition = new Vector3(-0.5f, 1.4f, 0.9f);
            Quaternion recoveredRotation = Quaternion.Euler(0f, 90f, 0f);

            Assert.That(follower.TryApplySample(Tracked(recoveredPosition, recoveredRotation)), Is.True);

            AssertKatanaShows(recoveredPosition, recoveredRotation);
        }

        // OnEnable / OnDisable only run in Play Mode, so the disable/enable
        // lifecycle is checked there. No frame is allowed to pass between the
        // toggles and the assertions, so Update never interferes.
        [UnityTest]
        public IEnumerator ReEnabling_HidesTheKatanaUntilTheNextValidGripPose()
        {
            yield return new EnterPlayMode();

            GameObject rig = new GameObject("Sandbox Katana Rig");
            GameObject katana = new GameObject("Katana");
            SandboxRightHandKatana playModeFollower = rig.AddComponent<SandboxRightHandKatana>();
            playModeFollower.Katana = katana.transform;

            Vector3 gripPosition = new Vector3(0.4f, 1.1f, 0.35f);
            Quaternion gripRotation = Quaternion.Euler(12f, 34f, 56f);
            Pose offset = playModeFollower.GripToKatanaOffset;

            Assert.That(playModeFollower.TryApplySample(Tracked(gripPosition, gripRotation)), Is.True);
            Assert.That(katana.activeSelf, Is.True);

            Vector3 applied = katana.transform.position;
            Quaternion appliedRotation = katana.transform.rotation;

            playModeFollower.enabled = false;
            Assert.That(katana.activeSelf, Is.False, "A disabled component must not leave the katana on screen.");

            playModeFollower.enabled = true;
            Assert.That(katana.activeSelf, Is.False,
                "A re-enabled component must not show the pose it was following before.");
            AssertVector(katana.transform.position, applied);
            Assert.That(Quaternion.Angle(katana.transform.rotation, appliedRotation), Is.LessThan(AngleTolerance));

            Vector3 nextPosition = new Vector3(0.1f, 1.0f, 0.2f);
            Quaternion nextRotation = Quaternion.Euler(0f, 45f, 0f);
            Assert.That(playModeFollower.TryApplySample(Tracked(nextPosition, nextRotation)), Is.True);
            Assert.That(katana.activeSelf, Is.True);
            AssertVector(katana.transform.position, nextPosition + nextRotation * offset.position);
            Assert.That(Quaternion.Angle(katana.transform.rotation, nextRotation * offset.rotation),
                Is.LessThan(AngleTolerance));

            yield return new ExitPlayMode();
        }

        [Test]
        public void GripToKatanaOffset_IsNonIdentityAndAppliedToPositionAndRotation()
        {
            Pose offset = follower.GripToKatanaOffset;
            Assert.That(offset.position, Is.Not.EqualTo(Vector3.zero));
            Assert.That(Quaternion.Angle(offset.rotation, Quaternion.identity), Is.GreaterThan(1f));

            // A 90 degree yaw turns the offset's local +Z into world +X, which
            // separates "offset applied in grip space" from "offset added in
            // world space".
            Quaternion gripRotation = Quaternion.Euler(0f, 90f, 0f);
            Assert.That(follower.TryApplySample(Tracked(Vector3.zero, gripRotation)), Is.True);

            AssertVector(katanaObject.transform.position, new Vector3(offset.position.z, offset.position.y, -offset.position.x));
            Assert.That(Quaternion.Angle(katanaObject.transform.rotation, gripRotation * offset.rotation),
                Is.LessThan(AngleTolerance));
        }

        [Test]
        public void BladeFrame_IsTheKatanaLocalAxisTriple()
        {
            BladeFrame frame = follower.BladeFrame;

            Assert.That(frame.IsValid, Is.True);
            AssertVector(frame.BladeAxis, Vector3.forward);
            AssertVector(frame.EdgeDirection, Vector3.down);
            AssertVector(frame.SideNormal, Vector3.right);
            AssertVector(frame.CutSamplePoint, Vector3.forward * (BladeLength * 0.7f));
            AssertVector(Vector3.Cross(frame.BladeAxis, frame.EdgeDirection), frame.SideNormal);
        }

        [Test]
        public void SandboxScene_PutsTheEdgeStripeOnTheEdgeDirectionSide()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SandboxScenePath, OpenSceneMode.Single);
                SandboxRightHandKatana sceneFollower = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    SandboxRightHandKatana candidate = root.GetComponentInChildren<SandboxRightHandKatana>(true);
                    if (candidate != null)
                    {
                        sceneFollower = candidate;
                        break;
                    }
                }

                Assert.That(sceneFollower, Is.Not.Null, "The sandbox scene has no katana rig.");
                Assert.That(sceneFollower.Katana, Is.Not.Null, "The katana rig has no katana assigned.");

                Transform stripe = sceneFollower.Katana.Find("Edge Stripe");
                Assert.That(stripe, Is.Not.Null, "The katana has no Edge Stripe marker.");

                Vector3 edgeDirection = sceneFollower.BladeFrame.EdgeDirection;
                Assert.That(Vector3.Dot(stripe.localPosition, edgeDirection), Is.GreaterThan(0f),
                    "The red edge stripe sits on the side away from EdgeDirection.");

                Transform blade = sceneFollower.Katana.Find("Blade");
                Assert.That(blade, Is.Not.Null, "The katana has no Blade body.");
                Assert.That(Vector3.Dot(blade.localPosition, edgeDirection), Is.LessThan(0f),
                    "The blade body must sit on the spine side, opposite the edge stripe.");
            }
            finally
            {
                if (setup != null && setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        [Test]
        public void BladeFrameAxes_ReachExpectedWorldValuesThroughBladePoseAdapter()
        {
            Vector3 gripPosition = new Vector3(0.2f, 1.3f, -0.4f);
            Quaternion gripRotation = Quaternion.Euler(0f, 90f, 0f);
            BladePoseSample sample = Tracked(gripPosition, gripRotation);

            Assert.That(follower.TryApplySample(sample), Is.True);

            Assert.That(
                BladePoseAdapter.TryEvaluate(sample, follower.GripToKatanaOffset, follower.BladeFrame, out EvaluatedBladePose expected),
                Is.True);

            // The displayed katana is what the adapter computed.
            Transform katana = katanaObject.transform;
            AssertVector(katana.position, expected.KatanaPose.position);
            Assert.That(Quaternion.Angle(katana.rotation, expected.KatanaPose.rotation), Is.LessThan(AngleTolerance));
            AssertVector(katana.forward, expected.BladeAxis);
            AssertVector(-katana.up, expected.EdgeDirection);
            AssertVector(katana.right, expected.SideNormal);
            AssertVector(katana.position + katana.forward * (BladeLength * 0.7f), expected.CutSamplePosition);

            // The same values, stated without the adapter: a 90 degree yaw of
            // an offset that only pitches the blade keeps the side normal
            // horizontal and turns the blade axis toward world +X.
            AssertVector(katana.right, Vector3.back);
            Assert.That(katana.forward.x, Is.GreaterThan(0.9f));
        }

        [Test]
        public void WithoutAKatanaTransform_NoPoseIsApplied()
        {
            follower.Katana = null;

            Assert.That(follower.TryApplySample(Tracked(Vector3.one, Quaternion.identity)), Is.False);
        }
    }
}
