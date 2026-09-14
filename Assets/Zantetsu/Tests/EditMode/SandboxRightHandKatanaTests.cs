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
        // 11 ms rather than 10: three intervals are 33 ms, clear of the 30 ms
        // window floor, so accumulated double rounding cannot drop a span just
        // under the boundary and silently skip a whole sweep.
        private const double SampleInterval = 0.011;

        // 0.06 m per step is about 5.5 m/s: past the 1.5 m/s floor, well under
        // the 20 m/s ceiling, and 0.18 m across the shortest window.
        private static readonly Vector3 EdgeStep = new Vector3(0f, -0.06f, 0f);

        private GameObject rigObject;
        private GameObject katanaObject;
        private SandboxRightHandKatana follower;

        private long strokeFrameId;
        private double strokeTime;
        private Vector3 strokePosition;

        [SetUp]
        public void SetUp()
        {
            rigObject = new GameObject("Sandbox Katana Rig");
            katanaObject = new GameObject("Katana");
            follower = rigObject.AddComponent<SandboxRightHandKatana>();
            follower.Katana = katanaObject.transform;

            strokeFrameId = 0;
            strokeTime = 0.0;
            strokePosition = new Vector3(0f, 1.4f, 0.3f);
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

        private static BladePoseSample TrackedAt(long frameId, double timestampSeconds, Vector3 position)
        {
            return new BladePoseSample(frameId, timestampSeconds, position, Quaternion.identity,
                BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static BladePoseSample UntrackedAt(long frameId, double timestampSeconds)
        {
            return new BladePoseSample(frameId, timestampSeconds, Vector3.zero, Quaternion.identity, BladeTrackingState.None);
        }

        // Frames 1..count at 0.00, 0.01, ... seconds, moving along +X.
        private void RecordValidSequence(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.That(follower.TryRecordSample(TrackedAt(i + 1, 0.01 * i, new Vector3(0.1f * i, 1f, 0f))), Is.True);
            }

            Assert.That(follower.RecordedPoseCount, Is.EqualTo(count));
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(PositionTolerance),
                "expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
        }

        // Cancels the fixed grip-to-katana offset, so the katana's blade frame
        // lands on the world axes: blade axis +Z, edge direction -Y, side
        // normal +X. Moving the grip along -Y is then an edge-leading sweep.
        private static Quaternion UprightGrip(SandboxRightHandKatana target)
        {
            return Quaternion.Inverse(target.GripToKatanaOffset.rotation);
        }

        // The same katana rolled over: the edge now points +Y.
        private static Quaternion FlippedGrip(SandboxRightHandKatana target)
        {
            return Quaternion.Euler(0f, 0f, 180f) * Quaternion.Inverse(target.GripToKatanaOffset.rotation);
        }

        private static void RecordSweep(
            SandboxRightHandKatana target,
            Quaternion gripRotation,
            Vector3 step,
            int count,
            ref long frameId,
            ref double time,
            ref Vector3 position)
        {
            for (int i = 0; i < count; i++)
            {
                frameId++;
                time += SampleInterval;
                position += step;
                Assert.That(
                    target.TryRecordSample(new BladePoseSample(frameId, time, position, gripRotation,
                        BladeTrackingState.Position | BladeTrackingState.Rotation)),
                    Is.True);
            }
        }

        private void Sweep(Quaternion gripRotation, Vector3 step, int count)
        {
            RecordSweep(follower, gripRotation, step, count, ref strokeFrameId, ref strokeTime, ref strokePosition);
        }

        // Every plane the component reports as a success must satisfy this.
        private static void AssertFinitePlane(Plane plane)
        {
            Assert.That(float.IsFinite(plane.normal.x), Is.True, "plane normal x is not finite");
            Assert.That(float.IsFinite(plane.normal.y), Is.True, "plane normal y is not finite");
            Assert.That(float.IsFinite(plane.normal.z), Is.True, "plane normal z is not finite");
            Assert.That(float.IsFinite(plane.distance), Is.True, "plane distance is not finite");
            Assert.That(plane.normal.magnitude, Is.EqualTo(1f).Within(PositionTolerance), "plane normal is not unit length");
        }

        private Vector3 CurrentCutSamplePosition()
        {
            Transform katana = katanaObject.transform;
            return katana.position + katana.forward * (BladeLength * 0.7f);
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
        public IEnumerator ReEnabling_HidesTheKatanaAndDropsTheStrokeUntilTheNextValidGripPose()
        {
            yield return new EnterPlayMode();

            GameObject rig = new GameObject("Sandbox Katana Rig");
            GameObject katana = new GameObject("Katana");
            SandboxRightHandKatana playModeFollower = rig.AddComponent<SandboxRightHandKatana>();
            playModeFollower.Katana = katana.transform;

            Pose offset = playModeFollower.GripToKatanaOffset;

            long frameId = 0;
            double time = 0.0;
            Vector3 position = new Vector3(0.4f, 1.1f, 0.35f);
            RecordSweep(playModeFollower, UprightGrip(playModeFollower), EdgeStep, 5,
                ref frameId, ref time, ref position);

            Assert.That(katana.activeSelf, Is.True);
            Assert.That(playModeFollower.RecordedPoseCount, Is.EqualTo(5));
            Assert.That(playModeFollower.AcceptedSampleCount, Is.GreaterThan(0));
            Assert.That(playModeFollower.TryGetSourceSlashPlaneCandidate(out _), Is.True);

            Vector3 applied = katana.transform.position;
            Quaternion appliedRotation = katana.transform.rotation;

            playModeFollower.enabled = false;
            Assert.That(katana.activeSelf, Is.False, "A disabled component must not leave the katana on screen.");
            Assert.That(playModeFollower.RecordedPoseCount, Is.EqualTo(0),
                "A disabled component must not keep the history it was building.");
            Assert.That(playModeFollower.AcceptedSampleCount, Is.EqualTo(0),
                "A disabled component must not keep the stroke it was building.");

            playModeFollower.enabled = true;
            Assert.That(katana.activeSelf, Is.False,
                "A re-enabled component must not show the pose it was following before.");
            Assert.That(playModeFollower.RecordedPoseCount, Is.EqualTo(0),
                "A re-enabled component must not carry the history from before it was disabled.");
            Assert.That(playModeFollower.AcceptedSampleCount, Is.EqualTo(0),
                "A re-enabled component must not carry the stroke from before it was disabled.");
            Assert.That(playModeFollower.TryGetSourceSlashPlaneCandidate(out _), Is.False,
                "The plane candidate goes with the stroke it was derived from.");
            AssertVector(katana.transform.position, applied);
            Assert.That(Quaternion.Angle(katana.transform.rotation, appliedRotation), Is.LessThan(AngleTolerance));

            Vector3 nextPosition = new Vector3(0.1f, 1.0f, 0.2f);
            Quaternion nextRotation = Quaternion.Euler(0f, 45f, 0f);
            Assert.That(playModeFollower.TryRecordSample(Tracked(nextPosition, nextRotation)), Is.True);
            Assert.That(katana.activeSelf, Is.True);
            Assert.That(playModeFollower.RecordedPoseCount, Is.EqualTo(1),
                "Following resumes as a new history, not a continuation of the old one.");
            Assert.That(playModeFollower.AcceptedSampleCount, Is.EqualTo(0),
                "One pose cannot span a window, so no stroke is under way yet.");
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

        // The recorded span's numbers are BladePoseWindow's and
        // BladeMotionEvaluator's contract; here only the count matters.
        [Test]
        public void ValidSampleSequence_AccumulatesInTheHistory()
        {
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));

            for (int i = 0; i < 3; i++)
            {
                Assert.That(follower.TryRecordSample(TrackedAt(i + 1, 0.01 * i, new Vector3(0.1f * i, 1f, 0f))), Is.True);
                Assert.That(follower.RecordedPoseCount, Is.EqualTo(i + 1));
            }
        }

        [TestCase(BladeTrackingState.Position)]
        [TestCase(BladeTrackingState.Rotation)]
        [TestCase(BladeTrackingState.None)]
        public void PartiallyTrackedSample_EmptiesTheHistory(BladeTrackingState state)
        {
            RecordValidSequence(3);

            Assert.That(
                follower.TryRecordSample(new BladePoseSample(10, 0.03, new Vector3(1f, 1f, 1f), Quaternion.identity, state)),
                Is.False);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));
        }

        [Test]
        public void NonFiniteSample_EmptiesTheHistory()
        {
            RecordValidSequence(3);

            Assert.That(follower.TryRecordSample(TrackedAt(10, 0.03, new Vector3(float.NaN, 1f, 0f))), Is.False);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));
        }

        [Test]
        public void TimestampThatDoesNotMoveForward_EmptiesTheHistoryButStillShowsThePose()
        {
            RecordValidSequence(3);

            Vector3 position = new Vector3(0.5f, 1f, 0.2f);
            Assert.That(follower.TryRecordSample(TrackedAt(10, 0.005, position)), Is.False);

            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));
            AssertKatanaShows(position, Quaternion.identity);
        }

        [Test]
        public void AfterTrackingLoss_TheFirstValidSampleStartsANewHistoryOfOne()
        {
            RecordValidSequence(3);
            Assert.That(follower.TryRecordSample(UntrackedAt(10, 0.03)), Is.False);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));

            Assert.That(follower.TryRecordSample(TrackedAt(11, 0.04, new Vector3(1f, 1f, 0f))), Is.True);

            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1),
                "A single pose cannot span the tracking gap it follows.");
        }

        [Test]
        public void AfterTrackingLoss_TheHistoryRebuildsFromSamplesRecordedAfterRecovery()
        {
            RecordValidSequence(3);
            Assert.That(follower.TryRecordSample(UntrackedAt(10, 0.03)), Is.False);

            Assert.That(follower.TryRecordSample(TrackedAt(11, 0.04, new Vector3(1f, 1f, 0f))), Is.True);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1));

            Assert.That(follower.TryRecordSample(TrackedAt(12, 0.05, new Vector3(1.1f, 1f, 0f))), Is.True);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(2));
        }

        [Test]
        public void BeforeRenderDisplayUpdate_DoesNotAppendASecondSampleForTheSameFrame()
        {
            Assert.That(follower.TryRecordSample(TrackedAt(1, 0.0, new Vector3(0f, 1f, 0f))), Is.True);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1));

            Vector3 renderPosition = new Vector3(0.05f, 1f, 0f);
            Assert.That(follower.TryApplySample(TrackedAt(1, 0.004, renderPosition)), Is.True);

            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1));
            AssertKatanaShows(renderPosition, Quaternion.identity);
        }

        // A tracking gap that falls between two Updates is only ever seen by
        // Before Render. It must still break the span.
        [Test]
        public void BeforeRenderTrackingLoss_EmptiesTheHistoryAndTheNextUpdateRestartsAtOne()
        {
            RecordValidSequence(3);

            // A usable Before Render sample shows but never appends.
            Assert.That(follower.TryApplySample(TrackedAt(4, 0.025, new Vector3(0.25f, 1f, 0f))), Is.True);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(3), "Before Render must not append.");

            // An unusable one marks the gap.
            Assert.That(follower.TryApplySample(UntrackedAt(4, 0.028)), Is.False);
            Assert.That(katanaObject.activeSelf, Is.False, "An unusable pose must still be hidden.");
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0),
                "A tracking loss seen on Before Render must empty the history.");

            // The next Update starts a fresh history rather than continuing.
            Assert.That(follower.TryRecordSample(TrackedAt(5, 0.03, new Vector3(0.3f, 1f, 0f))), Is.True);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1));
        }

        [Test]
        public void EdgeLeadingSweep_IsAcceptedAndItsFirstSampleIsTheStrokeBegin()
        {
            // Three intervals are the shortest window the gate accepts, so the
            // fourth sample is the first one that can be judged at all.
            Sweep(UprightGrip(follower), EdgeStep, 3);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));

            Sweep(UprightGrip(follower), EdgeStep, 1);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);
            Assert.That(begin.FrameId, Is.EqualTo(strokeFrameId));
        }

        [Test]
        public void ContinuingTheSameSweep_DoesNotReplaceTheStrokeBegin()
        {
            Sweep(UprightGrip(follower), EdgeStep, 4);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);

            Sweep(UprightGrip(follower), EdgeStep, 4);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(5));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose stillBegin), Is.True);
            Assert.That(stillBegin.FrameId, Is.EqualTo(begin.FrameId));
        }

        [Test]
        public void DiagonalSweep_IsAcceptedWhenTheScoreClearsTheThreshold()
        {
            // 60 degrees off the edge direction: score 0.5, above the 0.15 gate.
            Vector3 diagonalStep = new Vector3(0.866f, -0.5f, 0f) * 0.06f;

            Sweep(UprightGrip(follower), diagonalStep, 4);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
        }

        [Test]
        public void SpineLeadingSweep_IsNotAcceptedAndKeepsTheRawHistory()
        {
            Sweep(UprightGrip(follower), -EdgeStep, 8);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.TryGetStrokeBeginSample(out _), Is.False);

            // With no stroke under way there is no return half to end, so the
            // raw history is only rejected from, never wiped.
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(8));
        }

        [Test]
        public void ReturnSweep_DropsTheAcceptedSamplesAndTheRawHistoryOnTheSameSample()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 5);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));

            bool reArmed = false;
            for (int i = 0; i < 12 && !reArmed; i++)
            {
                Sweep(upright, -EdgeStep, 1);
                if (follower.AcceptedSampleCount == 0)
                {
                    reArmed = true;
                    Assert.That(follower.RecordedPoseCount, Is.EqualTo(0),
                        "the raw history goes with the accepted samples, on the same sample");
                }
            }

            Assert.That(reArmed, Is.True, "a return sweep must re-arm the stroke");
        }

        [Test]
        public void ReturnSweepOnTheSameBlade_ReArmsWithoutBeingAccepted()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 5);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose firstBegin), Is.True);

            // Same blade orientation, opposite direction: the spine leads.
            Sweep(upright, -EdgeStep, 8);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.TryGetStrokeBeginSample(out _), Is.False);
            long lastReturnFrameId = strokeFrameId;

            // Roll the katana over and sweep the same way again: now the edge
            // leads, so this begins a new stroke.
            Sweep(FlippedGrip(follower), -EdgeStep, 4);

            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose newBegin), Is.True);
            Assert.That(newBegin.FrameId, Is.GreaterThan(lastReturnFrameId));
            Assert.That(newBegin.FrameId, Is.Not.EqualTo(firstBegin.FrameId));
        }

        [Test]
        public void TooFewSamplesForTheWindow_NeitherAcceptsNorReArms()
        {
            Sweep(UprightGrip(follower), EdgeStep, 3);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(3), "the raw history is kept");
        }

        [Test]
        public void TooSmallADisplacement_NeitherAcceptsNorReArms()
        {
            // Fast enough, but only 0.09 m across the shortest window.
            Sweep(UprightGrip(follower), new Vector3(0f, -0.03f, 0f), 4);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(4), "the raw history is kept");
        }

        [Test]
        public void SlowingDownMidSweep_DoesNotReArm()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 5);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);
            int acceptedBefore = follower.AcceptedSampleCount;

            // Same direction, far too slow to be a swing: nothing is decided.
            Sweep(upright, new Vector3(0f, -0.001f, 0f), 8);

            Assert.That(follower.AcceptedSampleCount, Is.GreaterThanOrEqualTo(acceptedBefore));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose stillBegin), Is.True);
            Assert.That(stillBegin.FrameId, Is.EqualTo(begin.FrameId));
        }

        [Test]
        public void ImpossibleSpeed_DropsTheRawHistoryAndTheStroke()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 5);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));

            // A 1.5 m jump in one step is not a swing.
            Sweep(upright, new Vector3(0f, -1.5f, 0f), 1);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));

            // One sample alone cannot span a window, so nothing is accepted yet.
            Sweep(upright, EdgeStep, 1);

            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1));
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
        }

        [Test]
        public void AcceptedSamples_StayWithinTheFixedCapacity()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 4);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));

            // Twenty more accepted samples for eight slots.
            Sweep(upright, EdgeStep, 20);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(8));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose stillBegin), Is.True);
            Assert.That(stillBegin.FrameId, Is.EqualTo(begin.FrameId),
                "the begin sample survives the capacity limit");
        }

        [Test]
        public void TrackingLossOnUpdate_DropsTheStroke()
        {
            Sweep(UprightGrip(follower), EdgeStep, 5);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));
        }

        [Test]
        public void AfterTrackingLoss_ANewSweepBeginsANewStroke()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 5);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose firstBegin), Is.True);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            long lossFrameId = strokeFrameId;

            // One pose after the gap cannot span a window.
            Sweep(upright, EdgeStep, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));

            // Enough poses recorded after the gap, and the stroke starts again.
            Sweep(upright, EdgeStep, 3);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose newBegin), Is.True);
            Assert.That(newBegin.FrameId, Is.GreaterThan(lossFrameId),
                "the new stroke begins after the gap, not before it");
            Assert.That(newBegin.FrameId, Is.Not.EqualTo(firstBegin.FrameId));
        }

        [Test]
        public void TrackingLossOnBeforeRender_DropsTheStroke()
        {
            Sweep(UprightGrip(follower), EdgeStep, 5);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryApplySample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));
        }

        [Test]
        public void WithFewerThanTwoAcceptedSamples_ThereIsNoPlaneCandidate()
        {
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane none), Is.False);
            Assert.That(none.normal, Is.EqualTo(Vector3.zero));

            Sweep(UprightGrip(follower), EdgeStep, 4);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.False);
        }

        [Test]
        public void PlanarSweep_YieldsAUnitNormalOnTheSideTheBladeFaces()
        {
            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThanOrEqualTo(2));

            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane plane), Is.True);

            AssertFinitePlane(plane);
            Assert.That(Vector3.Dot(plane.normal, katanaObject.transform.right), Is.GreaterThan(0f),
                "the normal takes the side the newest accepted sample faces");
        }

        [Test]
        public void PlanarSweep_PutsTheStrokeBeginAndTheNewestPointOnThePlane()
        {
            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane plane), Is.True);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);

            AssertFinitePlane(plane);
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(begin.CutSamplePosition)), Is.LessThan(PositionTolerance));
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(CurrentCutSamplePosition())), Is.LessThan(PositionTolerance));
        }

        [Test]
        public void DiagonalSweep_YieldsAFinitePlaneCandidate()
        {
            Vector3 diagonalStep = new Vector3(0.866f, -0.5f, 0f) * 0.06f;

            Sweep(UprightGrip(follower), diagonalStep, 6);

            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane plane), Is.True);

            AssertFinitePlane(plane);
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(CurrentCutSamplePosition())), Is.LessThan(PositionTolerance));
        }

        [Test]
        public void RotatingTheWholeStroke_RotatesThePlaneCandidate()
        {
            Quaternion rotation = Quaternion.Euler(23f, 41f, 17f);
            Vector3 origin = strokePosition;

            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane upright), Is.True);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose uprightBegin), Is.True);
            AssertFinitePlane(upright);

            GameObject rotatedRig = new GameObject("Rotated Rig");
            GameObject rotatedKatana = new GameObject("Rotated Katana");
            try
            {
                SandboxRightHandKatana rotatedFollower = rotatedRig.AddComponent<SandboxRightHandKatana>();
                rotatedFollower.Katana = rotatedKatana.transform;

                long frameId = 0;
                double time = 0.0;
                Vector3 position = rotation * origin;
                RecordSweep(rotatedFollower, rotation * UprightGrip(rotatedFollower), rotation * EdgeStep, 6,
                    ref frameId, ref time, ref position);

                Assert.That(rotatedFollower.TryGetSourceSlashPlaneCandidate(out Plane rotated), Is.True);
                Assert.That(rotatedFollower.TryGetStrokeBeginSample(out EvaluatedBladePose rotatedBegin), Is.True);

                AssertFinitePlane(rotated);
                AssertVector(rotated.normal, rotation * upright.normal);
                AssertVector(rotatedBegin.CutSamplePosition, rotation * uprightBegin.CutSamplePosition);
                Assert.That(Mathf.Abs(rotated.GetDistanceToPoint(rotatedBegin.CutSamplePosition)), Is.LessThan(PositionTolerance));
            }
            finally
            {
                Object.DestroyImmediate(rotatedKatana);
                Object.DestroyImmediate(rotatedRig);
            }
        }

        [Test]
        public void BeforeRenderDisplayUpdate_DoesNotMoveThePlaneCandidate()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 6);
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane before), Is.True);
            AssertFinitePlane(before);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(
                follower.TryApplySample(new BladePoseSample(strokeFrameId, strokeTime,
                    strokePosition + new Vector3(0.2f, 0.1f, 0f), upright,
                    BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.True);

            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane after), Is.True);
            AssertFinitePlane(after);
            AssertVector(after.normal, before.normal);
            Assert.That(after.distance, Is.EqualTo(before.distance).Within(PositionTolerance));
        }

        [Test]
        public void PastCapacity_TheNewestAcceptedSampleStillMovesThePlane()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 12);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(8), "the accepted samples are at capacity");
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane before), Is.True);
            AssertFinitePlane(before);

            // Leave the swept plane while still leading with the edge. The
            // newest sample can only reach the result through the last slot.
            Sweep(upright, new Vector3(0.06f, -0.06f, 0f), 2);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(8));
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out Plane after), Is.True);
            AssertFinitePlane(after);
            Assert.That(Vector3.Angle(before.normal, after.normal), Is.GreaterThan(1f));
        }

        [Test]
        public void TrackingLossOnUpdate_RemovesThePlaneCandidate()
        {
            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.True);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.False);
        }

        [Test]
        public void TrackingLossOnBeforeRender_RemovesThePlaneCandidate()
        {
            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.True);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryApplySample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.False);
        }

        [Test]
        public void ReturnSweep_RemovesThePlaneCandidate()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 6);
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.True);

            bool reArmed = false;
            for (int i = 0; i < 12 && !reArmed; i++)
            {
                Sweep(upright, -EdgeStep, 1);
                reArmed = follower.AcceptedSampleCount == 0;
            }

            Assert.That(reArmed, Is.True, "a return sweep must re-arm the stroke");
            Assert.That(follower.TryGetSourceSlashPlaneCandidate(out _), Is.False);
        }

        [Test]
        public void WithoutAKatanaTransform_NoPoseIsApplied()
        {
            follower.Katana = null;

            Assert.That(follower.TryApplySample(Tracked(Vector3.one, Quaternion.identity)), Is.False);
        }
    }
}
