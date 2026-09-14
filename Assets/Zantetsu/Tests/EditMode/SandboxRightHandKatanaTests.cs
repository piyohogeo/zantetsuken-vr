using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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

        // The span capture timeout handed to a bare wave store in tests; the
        // katana hands in its own tuning value.
        private const float StoreTestSpanCaptureTimeout = 0.15f;
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

        private GameObject waveVisualRoot;
        private Transform[] waveVisuals;

        private GameObject recorderObject;

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
            if (recorderObject != null)
            {
                Object.DestroyImmediate(recorderObject);
            }

            if (waveVisualRoot != null)
            {
                Object.DestroyImmediate(waveVisualRoot);
            }

            waveVisuals = null;

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

        // Every slash frame the component reports as a success must satisfy
        // this: finite throughout, both emitters on the plane, both axes unit
        // vectors lying in the plane, and the span equal to the emitter chord.
        private static void AssertValidSlashFrame(
            Plane plane, Vector3 beginEmitter, Vector3 latestEmitter, Vector3 travelAxis, Vector3 spanAxis, float span)
        {
            AssertFinitePlane(plane);
            AssertFiniteVector(beginEmitter, "begin emitter");
            AssertFiniteVector(latestEmitter, "latest emitter");
            AssertFiniteVector(travelAxis, "travel axis");
            AssertFiniteVector(spanAxis, "span axis");
            Assert.That(float.IsFinite(span), Is.True, "span is not finite");

            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(beginEmitter)), Is.LessThan(PositionTolerance),
                "the begin emitter is off the plane");
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(latestEmitter)), Is.LessThan(PositionTolerance),
                "the latest emitter is off the plane");

            Assert.That(travelAxis.magnitude, Is.EqualTo(1f).Within(PositionTolerance), "travel axis is not unit length");
            Assert.That(spanAxis.magnitude, Is.EqualTo(1f).Within(PositionTolerance), "span axis is not unit length");
            Assert.That(Mathf.Abs(Vector3.Dot(travelAxis, plane.normal)), Is.LessThan(PositionTolerance),
                "travel axis is not in the plane");
            Assert.That(Mathf.Abs(Vector3.Dot(spanAxis, plane.normal)), Is.LessThan(PositionTolerance),
                "span axis is not in the plane");

            Assert.That(span, Is.EqualTo(Vector3.Distance(beginEmitter, latestEmitter)).Within(PositionTolerance));
            Assert.That(span, Is.GreaterThan(0f));
        }

        private static void AssertFiniteVector(Vector3 v, string what)
        {
            Assert.That(float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z), Is.True, what + " is not finite");
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

        // A fresh edge-leading stroke, swept until it latches: the seventh
        // sample is the first with an emitter chord past the latch distance.
        private void SweepUntilLatch()
        {
            Sweep(UprightGrip(follower), EdgeStep, 7);
        }

        // Sweeps back until the stroke re-arms, so the next forward sweep is a
        // new stroke rather than a continuation.
        private void ReArmStroke()
        {
            Quaternion upright = UprightGrip(follower);
            for (int i = 0; i < 20 && follower.AcceptedSampleCount > 0; i++)
            {
                Sweep(upright, -EdgeStep, 1);
            }

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "the stroke did not re-arm");
        }

        // Moves time on without disturbing the stroke: the jump is far outside
        // the gate's window, so nothing is accepted and nothing is re-armed,
        // but expiry sees the new time.
        private void SkipTime(double seconds)
        {
            strokeFrameId++;
            strokeTime += seconds;
            Assert.That(
                follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                    UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.True);
        }

        // The display slots live in a private serialized field, assigned in the
        // scene. Tests reach it the way the editor does rather than asking the
        // component for an accessor it has no other use for.
        private const string WaveVisualsField = "waveVisuals";

        private static void AssignWaveVisuals(SandboxRightHandKatana target, Transform[] visuals)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(WaveVisualsField);
            Assert.That(property, Is.Not.Null, "the display slot field is gone");
            property.arraySize = visuals.Length;
            for (int i = 0; i < visuals.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = visuals[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static int ReadWaveVisualCount(SandboxRightHandKatana target)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(WaveVisualsField);
            Assert.That(property, Is.Not.Null, "the display slot field is gone");
            return property.arraySize;
        }

        private static Transform ReadWaveVisual(SandboxRightHandKatana target, int index)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(WaveVisualsField);
            Assert.That(property, Is.Not.Null, "the display slot field is gone");
            Assert.That(index, Is.InRange(0, property.arraySize - 1));
            return property.GetArrayElementAtIndex(index).objectReferenceValue as Transform;
        }

        // A recorder on its own object. Its katana is a private serialized
        // field set in the scene, so tests set it the way the editor does.
        private SandboxSlashPoseRecorder CreateRecorder(SandboxRightHandKatana target)
        {
            recorderObject = new GameObject("Slash Diagnostics");
            SandboxSlashPoseRecorder recorder = recorderObject.AddComponent<SandboxSlashPoseRecorder>();
            if (target != null)
            {
                AssignRecorderKatana(recorder, target);
            }

            return recorder;
        }

        private static void AssignRecorderKatana(SandboxSlashPoseRecorder recorder, SandboxRightHandKatana target)
        {
            SerializedObject serialized = new SerializedObject(recorder);
            SerializedProperty property = serialized.FindProperty("katana");
            Assert.That(property, Is.Not.Null, "the recorder's katana field is gone");
            property.objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Appends tracked samples of a straight sweep to a recording, on the
        // recording's own clock.
        private static void AppendSweepToRecording(
            SandboxSlashPoseRecorder recorder, Quaternion gripRotation, Vector3 step, int count,
            ref double time, ref Vector3 position)
        {
            for (int i = 0; i < count; i++)
            {
                time += SampleInterval;
                position += step;
                Assert.That(
                    recorder.TryAppendRecordedSample(new BladePoseSample(0, time, position, gripRotation,
                        BladeTrackingState.Position | BladeTrackingState.Rotation)),
                    Is.True);
            }
        }

        // Feeds the next replayed samples into a katana the way the recorder's
        // Update does.
        private static void ReplayInto(SandboxSlashPoseRecorder recorder, SandboxRightHandKatana target, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.That(recorder.TryTakeNextReplaySample(i, out BladePoseSample sample, out Vector3 viewForward), Is.True);
                target.TryRecordSample(sample, viewForward);
            }
        }

        // Four display slots under a world-fixed root with identity scale, the
        // way the sandbox scene places them.
        private void GiveTheFollowerWaveVisuals()
        {
            waveVisualRoot = new GameObject("Slash Wave VFX");
            waveVisualRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            waveVisualRoot.transform.localScale = Vector3.one;

            waveVisuals = new Transform[SandboxSlashWaveStore.Capacity];
            GameObject template = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Mesh sharedMesh = template.GetComponent<MeshFilter>().sharedMesh;
            Material sharedMaterial = template.GetComponent<MeshRenderer>().sharedMaterial;
            Object.DestroyImmediate(template);

            for (int i = 0; i < waveVisuals.Length; i++)
            {
                GameObject slot = new GameObject("Slash Wave " + i, typeof(MeshFilter), typeof(MeshRenderer));
                slot.transform.SetParent(waveVisualRoot.transform, false);
                slot.GetComponent<MeshFilter>().sharedMesh = sharedMesh;
                slot.GetComponent<MeshRenderer>().sharedMaterial = sharedMaterial;
                slot.SetActive(false);
                waveVisuals[i] = slot.transform;
            }

            AssignWaveVisuals(follower, waveVisuals);
        }

        private static void AssertVisualIsHidden(Transform visual)
        {
            Assert.That(visual.gameObject.activeSelf, Is.False, visual.name + " should be hidden");
        }

        // The signed in-plane cross product 19.1.5.1 writes as cross2_N.
        private static float InPlaneCross(Vector3 normal, Vector3 x, Vector3 y)
        {
            return Vector3.Dot(normal, Vector3.Cross(x, y));
        }

        // The sweep is the closed convex hull of these four points, so all four
        // have to lie on the wave's plane for it to be a plane figure at all.
        private static void AssertSweepLiesOnThePlane(
            Plane plane, Vector3 previousA, Vector3 previousB, Vector3 currentA, Vector3 currentB)
        {
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(previousA)), Is.LessThan(PositionTolerance), "previous A is off the plane");
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(previousB)), Is.LessThan(PositionTolerance), "previous B is off the plane");
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(currentA)), Is.LessThan(PositionTolerance), "current A is off the plane");
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(currentB)), Is.LessThan(PositionTolerance), "current B is off the plane");
        }

        // Degenerate to a segment: no three of the four points span any area.
        private static void AssertSweepIsCollinear(
            Vector3 normal, Vector3 previousA, Vector3 previousB, Vector3 currentA, Vector3 currentB)
        {
            Vector3 along = currentB - previousA;
            Assert.That(InPlaneCross(normal, along, previousB - previousA), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(InPlaneCross(normal, along, currentA - previousA), Is.EqualTo(0f).Within(1e-4f));
        }

        // One pose with a rotation, step and interval of its own. The gate may
        // well turn it away; what matters here is that it was recorded.
        private bool RecordPose(Quaternion gripRotation, Vector3 step, double deltaSeconds)
        {
            strokeFrameId++;
            strokeTime += deltaSeconds;
            strokePosition += step;
            return follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                gripRotation, BladeTrackingState.Position | BladeTrackingState.Rotation));
        }

        // The katana's emission control point right now, projected onto a wave's
        // plane: the live guide origin that wave sees.
        private Vector3 CurrentGuideOrigin(Plane plane)
        {
            Transform katana = katanaObject.transform;
            return plane.ClosestPointOnPlane(katana.position + katana.forward * (BladeLength * 0.5f));
        }

        // Where a wave's A end is at this time, straight from its latch
        // snapshot -- the same arithmetic the store does.
        private static Vector3 ExpectedA(Vector3 waveOrigin, Vector3 travelAxis, double latchedAt, double nowSeconds)
        {
            return waveOrigin + travelAxis * (float)(SandboxSlashWaveStore.WaveSpeed * (nowSeconds - latchedAt));
        }

        private bool HasWaveLatchedAt(double latchedAt)
        {
            for (int i = 0; i < follower.WaveCount; i++)
            {
                if (follower.TryGetWave(i, out double at, out _, out _, out _, out _, out _, out _, out _, out _, out _)
                    && at == latchedAt)
                {
                    return true;
                }
            }

            return false;
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
            Assert.That(playModeFollower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.True);

            GameObject visualRoot = new GameObject("Slash Wave VFX");
            GameObject visual = new GameObject("Slash Wave 0");
            visual.transform.SetParent(visualRoot.transform, false);
            visual.SetActive(false);
            AssignWaveVisuals(playModeFollower, new[] { visual.transform, null, null, null });

            RecordSweep(playModeFollower, UprightGrip(playModeFollower), EdgeStep, 3,
                ref frameId, ref time, ref position);
            Assert.That(playModeFollower.WaveCount, Is.EqualTo(1), "the stroke latched a wave");
            Assert.That(visual.activeSelf, Is.True, "and it is on screen");

            Vector3 applied = katana.transform.position;
            Quaternion appliedRotation = katana.transform.rotation;

            playModeFollower.enabled = false;
            Assert.That(katana.activeSelf, Is.False, "A disabled component must not leave the katana on screen.");
            Assert.That(playModeFollower.WaveCount, Is.EqualTo(0),
                "A disabled component ends the waves it owns.");
            Assert.That(visual.activeSelf, Is.False, "and takes their display with them.");
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
            Assert.That(playModeFollower.IsLatchReady, Is.False);
            Assert.That(playModeFollower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False,
                "The slash frame goes with the stroke it was derived from.");
            Assert.That(playModeFollower.WaveCount, Is.EqualTo(0),
                "A re-enabled component does not carry waves across the gap.");
            Assert.That(visual.activeSelf, Is.False,
                "and shows nothing until it has a wave again.");
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
        public void WithFewerThanTwoAcceptedSamples_ThereIsNoLatchOrFrame()
        {
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);

            Sweep(UprightGrip(follower), EdgeStep, 4);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void LatchReady_WaitsUntilTheEmitterChordReachesTheLatchDistance()
        {
            // 0.052 m per sample: the shortest window still clears the gate's
            // 0.15 m displacement, and the chord grows one step per accepted
            // sample, so the 0.15 m latch distance falls between two of them.
            Vector3 step = new Vector3(0f, -0.052f, 0f);
            Quaternion upright = UprightGrip(follower);

            Sweep(upright, step, 5);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(2));
            Assert.That(follower.IsLatchReady, Is.False, "one step of chord is short of the latch distance");

            Sweep(upright, step, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(3));
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out float shortSpan), Is.True);
            Assert.That(shortSpan, Is.LessThan(0.15f));
            Assert.That(follower.IsLatchReady, Is.False, "two steps of chord are still short");

            Sweep(upright, step, 1);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out float longSpan), Is.True);
            Assert.That(longSpan, Is.GreaterThanOrEqualTo(0.15f));
            Assert.That(follower.IsLatchReady, Is.True, "three steps of chord reach the latch distance");
        }

        [Test]
        public void TheEmitterComesFromHalfwayAlongTheBladeNotTheCutSamplePoint()
        {
            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);
            Assert.That(follower.TryGetSlashFrameCandidate(
                out _, out Vector3 beginEmitter, out _, out _, out _, out _), Is.True);

            AssertVector(beginEmitter, begin.KatanaPose.position + begin.BladeAxis * (BladeLength * 0.5f));

            // The cut sample point sits at 70%, a fifth of a blade further on.
            Assert.That(Vector3.Distance(beginEmitter, begin.CutSamplePosition),
                Is.EqualTo(BladeLength * 0.2f).Within(PositionTolerance));
        }

        [Test]
        public void SlashFrameCandidate_IsFiniteAndLivesInThePlane()
        {
            Sweep(UprightGrip(follower), EdgeStep, 7);

            Assert.That(follower.TryGetSlashFrameCandidate(
                out Plane plane, out Vector3 beginEmitter, out Vector3 latestEmitter,
                out Vector3 travelAxis, out Vector3 spanAxis, out float span), Is.True);

            AssertValidSlashFrame(plane, beginEmitter, latestEmitter, travelAxis, spanAxis, span);
        }

        [Test]
        public void SpanAxisPointsFromTheBeginEmitterToTheLatest()
        {
            Sweep(UprightGrip(follower), EdgeStep, 7);

            Assert.That(follower.TryGetSlashFrameCandidate(
                out _, out Vector3 beginEmitter, out Vector3 latestEmitter,
                out _, out Vector3 spanAxis, out float span), Is.True);

            AssertVector(beginEmitter + spanAxis * span, latestEmitter);
            Assert.That(Vector3.Dot(spanAxis, latestEmitter - beginEmitter), Is.GreaterThan(0f));
        }

        [Test]
        public void TravelAxisPointsAlongTheBeginSampleBladeTipDirection()
        {
            Sweep(UprightGrip(follower), EdgeStep, 7);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);

            Assert.That(follower.TryGetSlashFrameCandidate(
                out Plane plane, out _, out _, out Vector3 travelAxis, out _, out _), Is.True);

            Assert.That(Vector3.Dot(travelAxis, begin.BladeAxis), Is.GreaterThan(0f));

            // The blade axis of this sweep already lies in the plane, so the
            // projection leaves it alone.
            Assert.That(Mathf.Abs(Vector3.Dot(begin.BladeAxis, plane.normal)), Is.LessThan(PositionTolerance));
            AssertVector(travelAxis, begin.BladeAxis);
        }

        [Test]
        public void ASweepAlongTheBladeGivesNonOrthogonalAxesAndIsStillValid()
        {
            // Down and forward along the blade: movement along the blade adds
            // nothing to the plane normal but tilts the emitter chord.
            Vector3 step = new Vector3(0f, -0.06f, 0.03f);

            Sweep(UprightGrip(follower), step, 7);

            Assert.That(follower.TryGetSlashFrameCandidate(
                out Plane plane, out Vector3 beginEmitter, out Vector3 latestEmitter,
                out Vector3 travelAxis, out Vector3 spanAxis, out float span), Is.True);

            AssertValidSlashFrame(plane, beginEmitter, latestEmitter, travelAxis, spanAxis, span);
            Assert.That(Mathf.Abs(Vector3.Dot(spanAxis, travelAxis)), Is.GreaterThan(0.1f),
                "the axes are not orthogonalised");
        }

        [Test]
        public void MovingAndRotatingTheWholeStroke_MovesTheSlashFrameWithIt()
        {
            Quaternion rotation = Quaternion.Euler(23f, 41f, 17f);
            Vector3 translation = new Vector3(-2.5f, 0.75f, 4f);
            Vector3 origin = strokePosition;

            Sweep(UprightGrip(follower), EdgeStep, 7);
            Assert.That(follower.TryGetSlashFrameCandidate(
                out Plane plane, out Vector3 beginEmitter, out Vector3 latestEmitter,
                out Vector3 travelAxis, out Vector3 spanAxis, out float span), Is.True);
            Assert.That(follower.IsLatchReady, Is.True);

            GameObject movedRig = new GameObject("Moved Rig");
            GameObject movedKatana = new GameObject("Moved Katana");
            try
            {
                SandboxRightHandKatana moved = movedRig.AddComponent<SandboxRightHandKatana>();
                moved.Katana = movedKatana.transform;

                long frameId = 0;
                double time = 0.0;
                Vector3 position = rotation * origin + translation;
                RecordSweep(moved, rotation * UprightGrip(moved), rotation * EdgeStep, 7,
                    ref frameId, ref time, ref position);

                Assert.That(moved.TryGetSlashFrameCandidate(
                    out Plane movedPlane, out Vector3 movedBegin, out Vector3 movedLatest,
                    out Vector3 movedTravel, out Vector3 movedSpanAxis, out float movedSpan), Is.True);

                AssertValidSlashFrame(movedPlane, movedBegin, movedLatest, movedTravel, movedSpanAxis, movedSpan);
                AssertVector(movedPlane.normal, rotation * plane.normal);
                AssertVector(movedBegin, rotation * beginEmitter + translation);
                AssertVector(movedLatest, rotation * latestEmitter + translation);
                AssertVector(movedTravel, rotation * travelAxis);
                AssertVector(movedSpanAxis, rotation * spanAxis);
                Assert.That(movedSpan, Is.EqualTo(span).Within(PositionTolerance));
                Assert.That(moved.IsLatchReady, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(movedKatana);
                Object.DestroyImmediate(movedRig);
            }
        }

        [Test]
        public void BeforeRenderDisplayUpdate_DoesNotMoveTheSlashFrame()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 7);
            Assert.That(follower.TryGetSlashFrameCandidate(
                out _, out Vector3 beginEmitter, out Vector3 latestEmitter,
                out Vector3 travelAxis, out Vector3 spanAxis, out float span), Is.True);
            bool latchReady = follower.IsLatchReady;

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(
                follower.TryApplySample(new BladePoseSample(strokeFrameId, strokeTime,
                    strokePosition + new Vector3(0.2f, 0.1f, 0f), upright,
                    BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.True);

            Assert.That(follower.TryGetSlashFrameCandidate(
                out _, out Vector3 afterBegin, out Vector3 afterLatest,
                out Vector3 afterTravel, out Vector3 afterSpanAxis, out float afterSpan), Is.True);

            AssertVector(afterBegin, beginEmitter);
            AssertVector(afterLatest, latestEmitter);
            AssertVector(afterTravel, travelAxis);
            AssertVector(afterSpanAxis, spanAxis);
            Assert.That(afterSpan, Is.EqualTo(span).Within(PositionTolerance));
            Assert.That(follower.IsLatchReady, Is.EqualTo(latchReady));
        }

        [Test]
        public void TrackingLossOnUpdate_RemovesTheLatchAndTheFrame()
        {
            Sweep(UprightGrip(follower), EdgeStep, 7);
            Assert.That(follower.IsLatchReady, Is.True);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void TrackingLossOnBeforeRender_RemovesTheLatchAndTheFrame()
        {
            Sweep(UprightGrip(follower), EdgeStep, 7);
            Assert.That(follower.IsLatchReady, Is.True);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryApplySample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void ReturnSweep_RemovesTheLatchAndTheFrame()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 7);
            Assert.That(follower.IsLatchReady, Is.True);

            bool reArmed = false;
            for (int i = 0; i < 12 && !reArmed; i++)
            {
                Sweep(upright, -EdgeStep, 1);
                reArmed = follower.AcceptedSampleCount == 0;
            }

            Assert.That(reArmed, Is.True, "a return sweep must re-arm the stroke");
            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void BeforeTheLatchDistance_NoWaveIsPublished()
        {
            Sweep(UprightGrip(follower), EdgeStep, 6);

            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
        }

        [Test]
        public void LatchReadyWithAnInvalidFrame_PublishesNothingUntilTheFrameBecomesValid()
        {
            Quaternion upright = UprightGrip(follower);
            Sweep(upright, EdgeStep, 4);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));

            // Two samples straight along the blade. The window still has the
            // lateral motion the gate wants, so both are accepted, but the
            // movement between accepted samples is parallel to the blade axis
            // and contributes no plane normal.
            Sweep(upright, new Vector3(0f, 0f, 0.10f), 2);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(3));
            Assert.That(follower.IsLatchReady, Is.True, "the emitter chord is past the latch distance");
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(0));

            // One sample back across the blade gives the plane a normal again.
            Sweep(upright, EdgeStep, 1);

            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.True,
                "a later sample of the same stroke may still give a frame");
            Assert.That(follower.WaveCount, Is.EqualTo(1), "the stroke was not spent by the invalid frame");
        }

        [Test]
        public void APublishedWaveMatchesTheFrameItLatchedOn()
        {
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            Assert.That(follower.TryGetSlashFrameCandidate(
                out Plane plane, out Vector3 beginEmitter, out Vector3 latestEmitter,
                out Vector3 travelAxis, out Vector3 spanAxis, out float span), Is.True);

            Assert.That(follower.TryGetWave(0,
                out double latchedAt, out Plane wavePlane, out Vector3 waveOrigin,
                out Vector3 waveTravel, out Vector3 waveSpanAxis, out float waveSpan,
                out Vector3 previousStart, out Vector3 previousEnd,
                out Vector3 currentStart, out Vector3 currentEnd), Is.True);

            Assert.That(latchedAt, Is.EqualTo(strokeTime).Within(1e-9),
                "the wave latched at the current sample, not an earlier one");
            AssertVector(wavePlane.normal, plane.normal);
            Assert.That(wavePlane.distance, Is.EqualTo(plane.distance).Within(PositionTolerance));
            AssertVector(waveOrigin, beginEmitter);
            AssertVector(waveTravel, travelAxis);
            AssertVector(waveSpanAxis, spanAxis);
            Assert.That(waveSpan, Is.EqualTo(span).Within(PositionTolerance));

            // At latch both segments are the same degenerate sweep from A to B.
            AssertVector(previousStart, beginEmitter);
            AssertVector(previousEnd, latestEmitter);
            AssertVector(currentStart, previousStart);
            AssertVector(currentEnd, previousEnd);
            Assert.That(waveSpan, Is.EqualTo(Vector3.Distance(previousStart, previousEnd)).Within(PositionTolerance));
        }

        [Test]
        public void ContinuingTheSameStroke_PublishesNoSecondWave()
        {
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            Sweep(UprightGrip(follower), EdgeStep, 8);

            Assert.That(follower.IsLatchReady, Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
        }

        [Test]
        public void AWaveOutlivesTheStrokeThatPublishedIt()
        {
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "the stroke is gone");
            Assert.That(follower.WaveCount, Is.EqualTo(1), "the wave is not");
        }

        [Test]
        public void ARearmedStroke_PublishesAlongsideTheLivingWave()
        {
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            ReArmStroke();
            Assert.That(follower.WaveCount, Is.EqualTo(1), "re-arming leaves the earlier wave alone");

            SweepUntilLatch();

            Assert.That(follower.WaveCount, Is.EqualTo(2));
        }

        [Test]
        public void AWaveExpiresOnceItsLifetimeIsUp()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out _, out _, out _, out _, out _, out _, out _, out _), Is.True);

            // Just short of the lifetime the wave is still alive.
            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds - 0.001;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            // At the lifetime exactly it is gone, before anything else in the update.
            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
        }

        [Test]
        public void AFullStore_PublishesNothingAndDoesNotEvictOrRewriteLatchSnapshots()
        {
            for (int i = 0; i < SandboxSlashWaveStore.Capacity; i++)
            {
                SweepUntilLatch();
                ReArmStroke();
            }

            Assert.That(follower.WaveCount, Is.EqualTo(SandboxSlashWaveStore.Capacity));
            Assert.That(follower.TryGetWave(0, out double firstLatchedAt, out Plane firstPlane, out Vector3 firstOrigin,
                out Vector3 firstTravel, out Vector3 firstSpanAxis, out _, out _, out _, out _, out _), Is.True);

            SweepUntilLatch();

            Assert.That(follower.WaveCount, Is.EqualTo(SandboxSlashWaveStore.Capacity), "no wave was added");

            // The living waves keep flying and may still take the live guide;
            // what a full store must not do is retire one or rewrite its latch.
            Assert.That(follower.TryGetWave(0, out double stillLatchedAt, out Plane stillPlane, out Vector3 stillOrigin,
                out Vector3 stillTravel, out Vector3 stillSpanAxis, out _, out _, out _, out _, out _), Is.True);
            Assert.That(stillLatchedAt, Is.EqualTo(firstLatchedAt));
            AssertVector(stillPlane.normal, firstPlane.normal);
            AssertVector(stillOrigin, firstOrigin);
            AssertVector(stillTravel, firstTravel);
            AssertVector(stillSpanAxis, firstSpanAxis);
        }

        [Test]
        public void AStrokeTurnedAwayByAFullStore_DoesNotRetryWhenASlotFrees()
        {
            for (int i = 0; i < SandboxSlashWaveStore.Capacity; i++)
            {
                SweepUntilLatch();
                ReArmStroke();
            }

            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(SandboxSlashWaveStore.Capacity));
            Assert.That(follower.IsLatchReady, Is.True, "the turned-away stroke is still under way");

            // Let the oldest waves expire without disturbing that stroke.
            SkipTime(SandboxSlashWaveStore.WaveLifetimeSeconds + 0.1);

            Assert.That(follower.WaveCount, Is.EqualTo(0), "every wave expired");
            Assert.That(follower.IsLatchReady, Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(0), "the spent stroke does not latch into the free slot");

            // Continuing it still publishes nothing.
            Sweep(UprightGrip(follower), EdgeStep, 4);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
        }

        [Test]
        public void ExpiryFreesCapacityForALatchInTheSameUpdate()
        {
            for (int i = 0; i < SandboxSlashWaveStore.Capacity; i++)
            {
                SweepUntilLatch();
                ReArmStroke();
            }

            Assert.That(follower.WaveCount, Is.EqualTo(SandboxSlashWaveStore.Capacity));
            Assert.That(follower.TryGetWave(0, out double oldestLatchedAt,
                out _, out _, out _, out _, out _, out _, out _, out _, out _), Is.True);
            double oldestExpiry = oldestLatchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds;

            // Start the next stroke shortly before the oldest wave runs out.
            // The jump itself is far outside the gate's window, so it only
            // seeds the history.
            strokeFrameId++;
            strokeTime = oldestExpiry - 0.080;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);

            // Sweep to one accepted sample short of the latch distance, all of
            // it still inside the oldest wave's lifetime.
            Quaternion upright = UprightGrip(follower);
            for (int i = 0; i < 12 && follower.AcceptedSampleCount < 3; i++)
            {
                Sweep(upright, EdgeStep, 1);
            }

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(3));
            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(SandboxSlashWaveStore.Capacity), "nothing has expired yet");

            // Land the sample that completes the latch exactly on the oldest
            // wave's expiry, so both happen in the one update.
            double finalDelta = oldestExpiry - strokeTime;
            Assert.That(finalDelta, Is.GreaterThan(0.0).And.LessThanOrEqualTo(0.05),
                "the final sample has to stay inside the gate window");

            strokeFrameId++;
            strokeTime = oldestExpiry;
            strokePosition += EdgeStep;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);

            Assert.That(HasWaveLatchedAt(oldestLatchedAt), Is.False, "the oldest wave expired in this update");
            Assert.That(HasWaveLatchedAt(strokeTime), Is.True,
                "the slot the expiry freed was used by this update's latch");
            Assert.That(follower.WaveCount, Is.EqualTo(SandboxSlashWaveStore.Capacity));
        }

        [Test]
        public void WaveStore_RefusesAStateThatCannotStayFinite()
        {
            SandboxSlashWaveStore store = new SandboxSlashWaveStore();
            Plane plane = new Plane(Vector3.right, Vector3.zero);

            Assert.That(store.TryLatch(double.NaN, plane, Vector3.zero, Vector3.up, Vector3.forward, Vector3.up, 1f, StoreTestSpanCaptureTimeout), Is.False);
            Assert.That(store.TryLatch(0.0, plane, new Vector3(float.NaN, 0f, 0f), Vector3.up, Vector3.forward, Vector3.up, 1f, StoreTestSpanCaptureTimeout), Is.False);
            Assert.That(store.TryLatch(0.0, plane, Vector3.zero, Vector3.up, Vector3.forward, Vector3.up, float.PositiveInfinity, StoreTestSpanCaptureTimeout), Is.False);

            // A finite origin whose travel over the lifetime overflows.
            Assert.That(
                store.TryLatch(0.0, plane, new Vector3(float.MaxValue, 0f, 0f), Vector3.up,
                    new Vector3(float.MaxValue, 0f, 0f), Vector3.up, 1f, StoreTestSpanCaptureTimeout),
                Is.False);

            // Both ends have to survive the lifetime, not just A. This travel
            // lands A well inside the finite range and takes B past it.
            Vector3 travelAxis = new Vector3(1.8e37f, 0f, 0f);
            Vector3 travel = travelAxis * (SandboxSlashWaveStore.WaveSpeed * SandboxSlashWaveStore.WaveLifetimeSeconds);
            Vector3 aEnd = new Vector3(-3.0e38f, 0f, 0f);
            Vector3 bEnd = new Vector3(1.0e38f, 0f, 0f);
            Assert.That(float.IsFinite(travel.x), Is.True, "the travel itself must stay finite for this case to bite");
            Assert.That(float.IsFinite((aEnd + travel).x), Is.True, "the A end stays finite");
            Assert.That(float.IsFinite((bEnd + travel).x), Is.False, "the B end does not");

            Assert.That(store.TryLatch(0.0, plane, aEnd, bEnd, travelAxis, Vector3.up, 1f, StoreTestSpanCaptureTimeout), Is.False);

            // The span capture timeout has to be positive and shorter than the lifetime.
            Assert.That(store.TryLatch(0.0, plane, Vector3.zero, Vector3.up, Vector3.forward, Vector3.up, 1f, float.NaN), Is.False);
            Assert.That(store.TryLatch(0.0, plane, Vector3.zero, Vector3.up, Vector3.forward, Vector3.up, 1f, 0f), Is.False);
            Assert.That(store.TryLatch(0.0, plane, Vector3.zero, Vector3.up, Vector3.forward, Vector3.up, 1f,
                SandboxSlashWaveStore.WaveLifetimeSeconds), Is.False);

            Assert.That(store.Count, Is.EqualTo(0));

            // The same call with a state that stays finite does publish.
            Assert.That(store.TryLatch(0.0, plane, Vector3.zero, Vector3.up, Vector3.forward, Vector3.up, 1f, StoreTestSpanCaptureTimeout), Is.True);
            Assert.That(store.Count, Is.EqualTo(1));
        }

        [Test]
        public void TheLatchUpdate_LeavesTheNewWaveAtItsInitialSegment()
        {
            SweepUntilLatch();

            Assert.That(follower.TryGetWave(0, out _, out _, out Vector3 origin, out _, out Vector3 spanAxis,
                out float span, out Vector3 previousStart, out Vector3 previousEnd,
                out Vector3 currentStart, out Vector3 currentEnd), Is.True);

            AssertVector(currentStart, origin);
            AssertVector(currentEnd, origin + spanAxis * span);
            AssertVector(previousStart, currentStart);
            AssertVector(previousEnd, currentEnd);
        }

        [Test]
        public void TheNextUpdate_MovesTheWaveAlongItsTravelAxisAndKeepsEverythingElse()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out Plane plane, out Vector3 origin,
                out Vector3 travelAxis, out Vector3 spanAxis, out float span,
                out _, out _, out Vector3 initialStart, out Vector3 initialEnd), Is.True);

            SkipTime(0.1);

            Assert.That(follower.TryGetWave(0, out double stillLatchedAt, out Plane movedPlane, out Vector3 movedOrigin,
                out Vector3 movedTravel, out Vector3 movedSpanAxis, out float movedSpan,
                out Vector3 previousStart, out Vector3 previousEnd,
                out Vector3 currentStart, out Vector3 currentEnd), Is.True);

            Vector3 expectedA = ExpectedA(origin, travelAxis, latchedAt, strokeTime);
            AssertVector(currentStart, expectedA);
            AssertVector(currentEnd, expectedA + spanAxis * span);

            // The segment it was showing is now the previous one.
            AssertVector(previousStart, initialStart);
            AssertVector(previousEnd, initialEnd);

            // Length and the latch snapshot are untouched.
            Assert.That(Vector3.Distance(currentStart, currentEnd), Is.EqualTo(span).Within(PositionTolerance));
            Assert.That(stillLatchedAt, Is.EqualTo(latchedAt));
            Assert.That(movedSpan, Is.EqualTo(span).Within(PositionTolerance));
            AssertVector(movedPlane.normal, plane.normal);
            Assert.That(movedPlane.distance, Is.EqualTo(plane.distance).Within(PositionTolerance));
            AssertVector(movedOrigin, origin);
            AssertVector(movedTravel, travelAxis);
            AssertVector(movedSpanAxis, spanAxis);
        }

        [Test]
        public void RepeatedUpdates_ComeFromTheLatchRatherThanAddingUp()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out Vector3 origin,
                out Vector3 travelAxis, out _, out _, out _, out _, out _, out _), Is.True);

            SkipTime(0.1);
            double firstNow = strokeTime;
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 firstStart, out Vector3 firstEnd), Is.True);
            AssertVector(firstStart, ExpectedA(origin, travelAxis, latchedAt, firstNow));

            SkipTime(0.3);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out Vector3 previousStart, out Vector3 previousEnd,
                out Vector3 secondStart, out Vector3 secondEnd), Is.True);

            AssertVector(secondStart, ExpectedA(origin, travelAxis, latchedAt, strokeTime));

            // The previous segment is the one the update before was showing.
            AssertVector(previousStart, firstStart);
            AssertVector(previousEnd, firstEnd);

            // Adding each update's travel on top of the last would have taken
            // it much further than the elapsed time allows.
            float analytic = (float)(SandboxSlashWaveStore.WaveSpeed * (strokeTime - latchedAt));
            Assert.That(Vector3.Distance(origin, secondStart), Is.EqualTo(analytic).Within(1e-3f));
            float accumulated = analytic + (float)(SandboxSlashWaveStore.WaveSpeed * (firstNow - latchedAt));
            Assert.That(Vector3.Distance(origin, secondStart), Is.LessThan(accumulated - 0.5f));
            Assert.That(Vector3.Distance(secondStart, secondEnd), Is.EqualTo(Vector3.Distance(firstStart, firstEnd)).Within(PositionTolerance));
        }

        [Test]
        public void AMissedUpdate_StillLandsTheWaveWhereTheTimeSaysItShouldBe()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out Vector3 origin,
                out Vector3 travelAxis, out _, out _, out _, out _, out _, out _), Is.True);

            // One long step instead of many short ones.
            SkipTime(0.4);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 currentStart, out _), Is.True);
            AssertVector(currentStart, ExpectedA(origin, travelAxis, latchedAt, strokeTime));
        }

        [Test]
        public void AnUpdateThatLosesTracking_StillFliesTheWave()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out Vector3 origin,
                out Vector3 travelAxis, out _, out _, out _, out _, out Vector3 beforeStart, out _), Is.True);

            strokeFrameId++;
            strokeTime += 0.1;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "the gesture is reset as before");
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 afterStart, out _), Is.True);
            Assert.That(afterStart, Is.Not.EqualTo(beforeStart));
            AssertVector(afterStart, ExpectedA(origin, travelAxis, latchedAt, strokeTime));
        }

        [Test]
        public void BeforeRenderDoesNotFlyTheWave()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float beforeSpan,
                out Vector3 beforePreviousStart, out _, out Vector3 beforeStart, out Vector3 beforeEnd), Is.True);

            strokeFrameId++;
            strokeTime += 0.1;
            Assert.That(
                follower.TryApplySample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition + EdgeStep * 4f,
                    UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.True);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float afterSpan,
                out Vector3 afterPreviousStart, out _, out Vector3 afterStart, out Vector3 afterEnd), Is.True);
            AssertVector(afterStart, beforeStart);
            AssertVector(afterEnd, beforeEnd);
            AssertVector(afterPreviousStart, beforePreviousStart);
            Assert.That(afterSpan, Is.EqualTo(beforeSpan), "Before Render is not a live guide");
        }

        [Test]
        public void InOneUpdate_TheOlderWaveFliesAndTheNewOneStaysAtItsInitialSegment()
        {
            SweepUntilLatch();
            ReArmStroke();

            // One sample short of the next latch.
            Sweep(UprightGrip(follower), EdgeStep, 6);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out double olderLatchedAt, out _, out Vector3 olderOrigin,
                out Vector3 olderTravel, out _, out _, out _, out _, out Vector3 olderBefore, out _), Is.True);

            Sweep(UprightGrip(follower), EdgeStep, 1);

            Assert.That(follower.WaveCount, Is.EqualTo(2));

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out Vector3 olderPreviousStart, out _, out Vector3 olderAfter, out _), Is.True);
            Assert.That(olderAfter, Is.Not.EqualTo(olderBefore), "the wave that was already there flew");
            AssertVector(olderAfter, ExpectedA(olderOrigin, olderTravel, olderLatchedAt, strokeTime));
            AssertVector(olderPreviousStart, olderBefore);

            Assert.That(follower.TryGetWave(1, out _, out _, out Vector3 newOrigin, out _, out Vector3 newSpanAxis,
                out float newSpan, out Vector3 newPreviousStart, out Vector3 newPreviousEnd,
                out Vector3 newCurrentStart, out Vector3 newCurrentEnd), Is.True);
            AssertVector(newCurrentStart, newOrigin);
            AssertVector(newCurrentEnd, newOrigin + newSpanAxis * newSpan);
            AssertVector(newPreviousStart, newCurrentStart);
            AssertVector(newPreviousEnd, newCurrentEnd);
        }

        [Test]
        public void EachWaveFliesFromItsOwnLatch()
        {
            SweepUntilLatch();
            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));

            Assert.That(follower.TryGetWave(0, out double firstLatchedAt, out _, out Vector3 firstOrigin,
                out Vector3 firstTravel, out _, out _, out _, out _, out _, out _), Is.True);
            Assert.That(follower.TryGetWave(1, out double secondLatchedAt, out _, out Vector3 secondOrigin,
                out Vector3 secondTravel, out _, out _, out _, out _, out _, out _), Is.True);
            Assert.That(secondLatchedAt, Is.GreaterThan(firstLatchedAt));

            SkipTime(0.2);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 firstCurrent, out _), Is.True);
            Assert.That(follower.TryGetWave(1, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 secondCurrent, out _), Is.True);

            AssertVector(firstCurrent, ExpectedA(firstOrigin, firstTravel, firstLatchedAt, strokeTime));
            AssertVector(secondCurrent, ExpectedA(secondOrigin, secondTravel, secondLatchedAt, strokeTime));
            Assert.That(Vector3.Distance(firstOrigin, firstCurrent),
                Is.GreaterThan(Vector3.Distance(secondOrigin, secondCurrent)),
                "the older wave has travelled further");
        }

        [Test]
        public void AWaveAtExactlyItsLifetime_ExpiresInsteadOfFlying()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out Vector3 origin,
                out Vector3 travelAxis, out _, out _, out _, out _, out _, out _), Is.True);

            // Just short of the lifetime it is still there, and it has flown.
            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds - 0.001;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 lastSeen, out _), Is.True);
            AssertVector(lastSeen, ExpectedA(origin, travelAxis, latchedAt, strokeTime));

            // At the lifetime it is gone rather than moved one last time.
            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
        }

        [Test]
        public void ATimeBeforeTheLatch_LeavesTheSegmentAlone()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out _, out _, out _, out _,
                out Vector3 beforePreviousStart, out _, out Vector3 beforeStart, out Vector3 beforeEnd), Is.True);

            strokeFrameId++;
            double backwards = latchedAt - 0.05;
            follower.TryRecordSample(new BladePoseSample(strokeFrameId, backwards, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation));

            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out Vector3 afterPreviousStart, out _, out Vector3 afterStart, out Vector3 afterEnd), Is.True);
            AssertVector(afterStart, beforeStart);
            AssertVector(afterEnd, beforeEnd);
            AssertVector(afterPreviousStart, beforePreviousStart);
        }

        [Test]
        public void ANonFiniteTime_LeavesTheSegmentAlone()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out Vector3 beforePreviousStart, out _, out Vector3 beforeStart, out Vector3 beforeEnd), Is.True);

            strokeFrameId++;
            follower.TryRecordSample(new BladePoseSample(strokeFrameId, double.NaN, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation));

            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out Vector3 afterPreviousStart, out _, out Vector3 afterStart, out Vector3 afterEnd), Is.True);
            AssertVector(afterStart, beforeStart);
            AssertVector(afterEnd, beforeEnd);
            AssertVector(afterPreviousStart, beforePreviousStart);
        }

        [Test]
        public void TheLatchUpdate_KeepsTheInitialSpanWithoutEvaluatingAGuide()
        {
            SweepUntilLatch();

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 currentStart, out Vector3 currentEnd), Is.True);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out float frameSpan), Is.True);

            Assert.That(span, Is.EqualTo(frameSpan).Within(PositionTolerance));
            Assert.That(Vector3.Distance(currentStart, currentEnd), Is.EqualTo(frameSpan).Within(PositionTolerance));
        }

        [Test]
        public void TheNextLiveGuide_WidensTheAcceptedSpanToTheIntersection()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out Vector3 origin, out Vector3 travelAxis,
                out Vector3 spanAxis, out float initialSpan, out _, out _, out _, out _), Is.True);

            Assert.That(RecordPose(UprightGrip(follower), EdgeStep, SampleInterval), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out Plane planeAfter, out Vector3 originAfter,
                out Vector3 travelAfter, out Vector3 spanAxisAfter, out float span,
                out _, out _, out Vector3 currentStart, out Vector3 currentEnd), Is.True);

            Assert.That(span, Is.GreaterThan(initialSpan));

            // The same intersection, worked out from the wave's own frame and
            // the katana's current pose.
            Vector3 guideOrigin = CurrentGuideOrigin(plane);
            Vector3 guideDirection = Vector3.ProjectOnPlane(katanaObject.transform.forward, plane.normal).normalized;
            float denominator = Vector3.Dot(plane.normal, Vector3.Cross(spanAxis, guideDirection));
            float expectedR = Vector3.Dot(plane.normal, Vector3.Cross(guideOrigin - currentStart, guideDirection)) / denominator;
            Assert.That(span, Is.EqualTo(expectedR).Within(1e-3f));

            // And independently: for this sweep the span line is world -Y, so
            // the intersection is just how far the emitter is below A.
            Assert.That(span, Is.EqualTo(currentStart.y - guideOrigin.y).Within(1e-3f));

            AssertVector(currentEnd, currentStart + spanAxis * span);

            // The latch snapshot is untouched by the wider span.
            AssertVector(planeAfter.normal, plane.normal);
            Assert.That(planeAfter.distance, Is.EqualTo(plane.distance).Within(PositionTolerance));
            AssertVector(originAfter, origin);
            AssertVector(travelAfter, travelAxis);
            AssertVector(spanAxisAfter, spanAxis);
        }

        [Test]
        public void ASmallerOrBackwardCandidate_LeavesTheAcceptedSpanAtItsMaximum()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);
            Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out Vector3 spanAxis, out float widest,
                out _, out _, out Vector3 startBefore, out _), Is.True);

            // Sweep back up past the wave origin: the candidate first shrinks
            // and then goes behind the wave entirely.
            for (int i = 0; i < 10; i++)
            {
                RecordPose(upright, -EdgeStep, SampleInterval);
            }

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 startAfter, out Vector3 endAfter), Is.True);

            Assert.That(span, Is.EqualTo(widest), "the accepted span is a running maximum");
            Assert.That(startAfter, Is.Not.EqualTo(startBefore), "the wave kept flying");
            AssertVector(endAfter, startAfter + spanAxis * span);
        }

        [Test]
        public void ANearParallelGuide_LeavesTheSpanAndKeepsTheWaveFlying()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 startBefore, out _), Is.True);

            // Point the blade along the span axis: the guide ray and the span
            // line are parallel, so there is no usable intersection.
            Quaternion alongSpan = Quaternion.Euler(90f, 0f, 0f) * Quaternion.Inverse(follower.GripToKatanaOffset.rotation);
            Assert.That(RecordPose(alongSpan, EdgeStep, SampleInterval), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanAfter,
                out _, out _, out Vector3 startAfter, out _), Is.True);
            Assert.That(spanAfter, Is.EqualTo(span));
            Assert.That(startAfter, Is.Not.EqualTo(startBefore));
        }

        [Test]
        public void AGuideBehindTheWave_LeavesTheSpanAndKeepsTheWaveFlying()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 startBefore, out _), Is.True);

            // Far along the blade axis: the intersection is behind the guide
            // ray's origin, even though the span candidate itself grew.
            RecordPose(upright, new Vector3(0f, -0.5f, 5f), SampleInterval);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanAfter,
                out _, out _, out Vector3 startAfter, out _), Is.True);
            Assert.That(spanAfter, Is.EqualTo(span));
            Assert.That(startAfter, Is.Not.EqualTo(startBefore));
        }

        [Test]
        public void APoseTheGateTurnedAway_IsStillALiveGuide()
        {
            SweepUntilLatch();
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out _, out _), Is.True);

            // Rolled over, the same downward motion leads with the spine, so
            // the gate takes none of it -- but the poses are still usable.
            Quaternion flipped = FlippedGrip(follower);
            Assert.That(RecordPose(flipped, EdgeStep, SampleInterval), Is.True);
            Assert.That(RecordPose(flipped, EdgeStep, SampleInterval), Is.True);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "the gate accepted none of them");
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanAfter,
                out _, out _, out _, out _), Is.True);
            Assert.That(spanAfter, Is.GreaterThan(span));
        }

        [Test]
        public void AnUpdateThatLosesTracking_KeepsTheSpanAndKeepsFlying()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out Vector3 spanAxis, out float span,
                out _, out _, out Vector3 startBefore, out _), Is.True);

            strokeFrameId++;
            strokeTime += SampleInterval;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanAfter,
                out _, out _, out Vector3 startAfter, out Vector3 endAfter), Is.True);
            Assert.That(spanAfter, Is.EqualTo(span), "no guide, so the span stands");
            Assert.That(startAfter, Is.Not.EqualTo(startBefore), "the wave flies on that fixed span");
            AssertVector(endAfter, startAfter + spanAxis * spanAfter);
        }

        [Test]
        public void InOneUpdate_TheNewWaveKeepsItsInitialSpanWhileTheOlderOneFlies()
        {
            SweepUntilLatch();
            ReArmStroke();
            Sweep(UprightGrip(follower), EdgeStep, 6);

            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 olderBefore, out _), Is.True);

            Sweep(UprightGrip(follower), EdgeStep, 1);

            Assert.That(follower.WaveCount, Is.EqualTo(2));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 olderAfter, out _), Is.True);
            Assert.That(olderAfter, Is.Not.EqualTo(olderBefore), "the wave already flying moved");

            Assert.That(follower.TryGetWave(1, out _, out _, out Vector3 newOrigin, out _, out Vector3 newSpanAxis,
                out float newSpan, out Vector3 newPreviousStart, out Vector3 newPreviousEnd,
                out Vector3 newCurrentStart, out Vector3 newCurrentEnd), Is.True);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out float frameSpan), Is.True);

            Assert.That(newSpan, Is.EqualTo(frameSpan).Within(PositionTolerance), "no guide ran for the new wave");
            AssertVector(newCurrentStart, newOrigin);
            AssertVector(newCurrentEnd, newOrigin + newSpanAxis * newSpan);
            AssertVector(newPreviousStart, newCurrentStart);
            AssertVector(newPreviousEnd, newCurrentEnd);
        }

        [Test]
        public void EachWaveEvaluatesTheSameGuideAgainstItsOwnFrame()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));

            for (int i = 0; i < 3; i++)
            {
                Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);
            }

            Assert.That(follower.TryGetWave(0, out _, out Plane firstPlane, out _, out _, out Vector3 firstSpanAxis,
                out float firstSpan, out _, out _, out Vector3 firstStart, out Vector3 firstEnd), Is.True);
            Assert.That(follower.TryGetWave(1, out _, out Plane secondPlane, out _, out _, out Vector3 secondSpanAxis,
                out float secondSpan, out _, out _, out Vector3 secondStart, out Vector3 secondEnd), Is.True);

            // One pose, but each wave measures from its own A along its own
            // span axis -- and against the guide that wave is actually using,
            // which for one past its capture window is the frozen one.
            Vector3 firstGuide = follower.TryGetWaveSpanClose(0, out _, out Vector3 firstFrozen, out _)
                ? firstFrozen
                : CurrentGuideOrigin(firstPlane);
            Vector3 secondGuide = follower.TryGetWaveSpanClose(1, out _, out Vector3 secondFrozen, out _)
                ? secondFrozen
                : CurrentGuideOrigin(secondPlane);

            Assert.That(firstSpan, Is.EqualTo(firstStart.y - firstGuide.y).Within(1e-3f));
            Assert.That(secondSpan, Is.EqualTo(secondStart.y - secondGuide.y).Within(1e-3f));
            AssertVector(firstEnd, firstStart + firstSpanAxis * firstSpan);
            AssertVector(secondEnd, secondStart + secondSpanAxis * secondSpan);
        }

        [Test]
        public void ATimeBehindTheWaveTravel_LeavesTheWaveExactlyAsItWas()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out _, out _, out _,
                out _, out _, out _, out _, out _), Is.True);

            // Fly it well past its latch.
            SkipTime(0.4);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out Vector3 previousStart, out Vector3 previousEnd,
                out Vector3 currentStart, out Vector3 currentEnd), Is.True);

            // A time after the latch but before that: the pose history refuses
            // it and the stroke resets, but the wave must not go backwards.
            double backwards = (latchedAt + strokeTime) * 0.5;
            Assert.That(backwards, Is.GreaterThan(latchedAt).And.LessThan(strokeTime));

            strokeFrameId++;
            Assert.That(
                follower.TryRecordSample(new BladePoseSample(strokeFrameId, backwards, strokePosition,
                    UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.False,
                "the pose history refuses a timestamp that does not move forward");
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "the stroke is reset as before");

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanAfter,
                out Vector3 previousStartAfter, out Vector3 previousEndAfter,
                out Vector3 currentStartAfter, out Vector3 currentEndAfter), Is.True);
            Assert.That(spanAfter, Is.EqualTo(span));
            AssertVector(previousStartAfter, previousStart);
            AssertVector(previousEndAfter, previousEnd);
            AssertVector(currentStartAfter, currentStart);
            AssertVector(currentEndAfter, currentEnd);
        }

        [Test]
        public void BeforeTheCaptureWindowRunsOut_TheSpanStaysOpenOnTheLiveGuide()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float initialSpan,
                out _, out _, out _, out _), Is.True);

            Assert.That(RecordPose(UprightGrip(follower), EdgeStep, SampleInterval), Is.True);

            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.False, "the span is still open");
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out _, out _), Is.True);
            Assert.That(span, Is.GreaterThan(initialSpan), "the live guide still steers it");
        }

        [Test]
        public void TheCaptureWindowCloses_AfterTakingThatUpdatesLiveCandidate()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out Plane plane, out _, out _,
                out Vector3 spanAxis, out float initialSpan, out _, out _, out _, out _), Is.True);

            // One pose a capture window later: it steers the span and closes it.
            Assert.That(RecordPose(UprightGrip(follower), EdgeStep * 4f, 0.2), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 currentStart, out Vector3 currentEnd), Is.True);
            Assert.That(span, Is.GreaterThan(initialSpan), "this update's live candidate was not lost");

            Vector3 expectedOrigin = CurrentGuideOrigin(plane);
            Vector3 expectedDirection = Vector3.ProjectOnPlane(katanaObject.transform.forward, plane.normal).normalized;
            Assert.That(span, Is.EqualTo(currentStart.y - expectedOrigin.y).Within(1e-3f));
            AssertVector(currentEnd, currentStart + spanAxis * span);

            Assert.That(follower.TryGetWaveSpanClose(0, out double closedAt, out Vector3 frozenOrigin,
                out Vector3 frozenDirection), Is.True);
            AssertVector(frozenOrigin, expectedOrigin);
            AssertVector(frozenDirection, expectedDirection);
            Assert.That(frozenDirection.magnitude, Is.EqualTo(1f).Within(PositionTolerance));
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(frozenOrigin)), Is.LessThan(PositionTolerance));
            Assert.That(Mathf.Abs(Vector3.Dot(frozenDirection, plane.normal)), Is.LessThan(PositionTolerance));

            // The close is stamped with this update, not the moment the window
            // technically ran out.
            Assert.That(closedAt, Is.EqualTo(strokeTime));
            Assert.That(closedAt, Is.GreaterThan(latchedAt + follower.SpanCaptureTimeoutSeconds));

            Assert.That(follower.WaveCount, Is.EqualTo(1), "closing is not expiring");
        }

        [Test]
        public void ClosingKeepsTheLatchSnapshot()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out Plane plane, out Vector3 origin,
                out Vector3 travelAxis, out Vector3 spanAxis, out _, out _, out _, out _, out _), Is.True);

            Assert.That(RecordPose(UprightGrip(follower), EdgeStep * 4f, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);

            Assert.That(follower.TryGetWave(0, out double latchedAtAfter, out Plane planeAfter, out Vector3 originAfter,
                out Vector3 travelAfter, out Vector3 spanAxisAfter, out _, out _, out _, out _, out _), Is.True);
            Assert.That(latchedAtAfter, Is.EqualTo(latchedAt));
            AssertVector(planeAfter.normal, plane.normal);
            Assert.That(planeAfter.distance, Is.EqualTo(plane.distance).Within(PositionTolerance));
            AssertVector(originAfter, origin);
            AssertVector(travelAfter, travelAxis);
            AssertVector(spanAxisAfter, spanAxis);
        }

        [Test]
        public void AfterClosing_TheCurrentPoseNoLongerTouchesTheFrozenGuide()
        {
            SweepUntilLatch();
            Assert.That(RecordPose(UprightGrip(follower), EdgeStep * 4f, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out double closedAt, out Vector3 frozenOrigin,
                out Vector3 frozenDirection), Is.True);

            // Move and turn the katana somewhere else entirely.
            Quaternion turned = Quaternion.Euler(0f, 90f, 30f) * Quaternion.Inverse(follower.GripToKatanaOffset.rotation);
            Assert.That(RecordPose(turned, new Vector3(0.4f, 0.3f, -0.2f), SampleInterval), Is.True);

            Assert.That(follower.TryGetWaveSpanClose(0, out double closedAtAfter, out Vector3 originAfter,
                out Vector3 directionAfter), Is.True);
            Assert.That(closedAtAfter, Is.EqualTo(closedAt));
            AssertVector(originAfter, frozenOrigin);
            AssertVector(directionAfter, frozenDirection);
        }

        [Test]
        public void AfterClosing_TheFrozenGuideCanStillWidenTheSpan()
        {
            SweepUntilLatch();

            // Close with the blade tilted, so the frozen guide is not parallel
            // to the travel axis and the intersection keeps moving outward.
            Quaternion tilted = Quaternion.Euler(45f, 0f, 0f) * Quaternion.Inverse(follower.GripToKatanaOffset.rotation);
            Assert.That(RecordPose(tilted, EdgeStep, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out Vector3 spanAxis, out float atClose,
                out _, out _, out Vector3 startAtClose, out _), Is.True);

            // Lose tracking: there is no live guide left, and none is needed.
            strokeFrameId++;
            strokeTime += 0.05;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float afterLoss,
                out _, out _, out Vector3 startAfter, out Vector3 endAfter), Is.True);
            Assert.That(startAfter, Is.Not.EqualTo(startAtClose), "the wave kept flying");
            Assert.That(afterLoss, Is.GreaterThan(atClose), "the frozen guide kept widening it");
            AssertVector(endAfter, startAfter + spanAxis * afterLoss);
        }

        [Test]
        public void AfterClosing_ASmallerOrInvalidFrozenCandidateHoldsTheSpan()
        {
            SweepUntilLatch();

            // Tilted the other way, the frozen intersection lands behind A, so
            // every later candidate is refused.
            Quaternion tilted = Quaternion.Euler(-45f, 0f, 0f) * Quaternion.Inverse(follower.GripToKatanaOffset.rotation);
            Assert.That(RecordPose(tilted, EdgeStep, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out Vector3 spanAxis, out float atClose,
                out _, out _, out Vector3 startAtClose, out _), Is.True);

            SkipTime(0.05);
            SkipTime(0.05);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float afterwards,
                out _, out _, out Vector3 startAfter, out Vector3 endAfter), Is.True);
            Assert.That(afterwards, Is.EqualTo(atClose), "the accepted span is still a running maximum");
            Assert.That(startAfter, Is.Not.EqualTo(startAtClose), "and the wave still flies");
            AssertVector(endAfter, startAfter + spanAxis * afterwards);
        }

        [Test]
        public void WithNoGuideAtTheTimeout_TheSpanClosesOnTheNextUsableUpdate()
        {
            SweepUntilLatch();

            // The capture window runs out on an update with no usable pose.
            strokeFrameId++;
            strokeTime += 0.2;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.False,
                "there was no guide to freeze");

            Assert.That(RecordPose(UprightGrip(follower), EdgeStep, SampleInterval), Is.True);

            Assert.That(follower.TryGetWaveSpanClose(0, out double closedAt, out _, out _), Is.True);
            Assert.That(closedAt, Is.EqualTo(strokeTime), "it closed on the update that had one");
        }

        [Test]
        public void BeforeRenderDoesNotCloseTheSpan()
        {
            SweepUntilLatch();

            strokeFrameId++;
            strokeTime += 0.2;
            Assert.That(
                follower.TryApplySample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                    UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.True);

            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.False);
        }

        [Test]
        public void ANewlyLatchedWave_IsOpenAndUnevaluated()
        {
            SweepUntilLatch();

            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out float frameSpan), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out _, out _), Is.True);
            Assert.That(span, Is.EqualTo(frameSpan).Within(PositionTolerance));
        }

        [Test]
        public void EachWaveClosesOnItsOwnCaptureWindow()
        {
            SweepUntilLatch();
            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));
            Assert.That(follower.TryGetWave(0, out double firstLatchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);
            Assert.That(follower.TryGetWave(1, out double secondLatchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);
            Assert.That(secondLatchedAt, Is.GreaterThan(firstLatchedAt));

            // Far enough on for the older wave's window but not the newer one's.
            strokeFrameId++;
            strokeTime = firstLatchedAt + follower.SpanCaptureTimeoutSeconds + 0.01;
            Assert.That(strokeTime, Is.LessThan(secondLatchedAt + follower.SpanCaptureTimeoutSeconds));
            strokePosition += EdgeStep;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);

            Assert.That(follower.TryGetWaveSpanClose(0, out double firstClosedAt, out _, out _), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(1, out _, out _, out _), Is.False);

            // And on again for the newer one.
            Assert.That(RecordPose(UprightGrip(follower), EdgeStep, 0.2), Is.True);

            Assert.That(follower.TryGetWaveSpanClose(1, out double secondClosedAt, out _, out _), Is.True);
            Assert.That(secondClosedAt, Is.GreaterThan(firstClosedAt));
        }

        [Test]
        public void AClosedWaveStillExpiresOnItsLifetimeAndNotBefore()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);

            Assert.That(RecordPose(UprightGrip(follower), EdgeStep, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds - 0.001;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
        }

        [Test]
        public void AtLatch_TheSweepIsTheInitialSegmentTwiceOver()
        {
            SweepUntilLatch();

            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out _, out _, out Vector3 spanAxis,
                out float span, out Vector3 previousA, out Vector3 previousB,
                out Vector3 currentA, out Vector3 currentB), Is.True);

            AssertVector(previousA, currentA);
            AssertVector(previousB, currentB);
            AssertSweepLiesOnThePlane(plane, previousA, previousB, currentA, currentB);
            AssertSweepIsCollinear(plane.normal, previousA, previousB, currentA, currentB);
            AssertVector(currentB, currentA + spanAxis * span);
            Assert.That(Vector3.Distance(currentA, currentB), Is.EqualTo(span).Within(PositionTolerance));
        }

        [Test]
        public void FlyingAtAFixedSpan_MakesTheSweepAParallelogram()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out Vector3 travelAxis, out _, out float span,
                out _, out _, out Vector3 startBefore, out Vector3 endBefore), Is.True);

            // The pose does not move, so the candidate stays where it was and
            // the span does not change.
            SkipTime(0.05);

            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out _, out _, out _, out float spanAfter,
                out Vector3 previousA, out Vector3 previousB,
                out Vector3 currentA, out Vector3 currentB), Is.True);

            Assert.That(spanAfter, Is.EqualTo(span));
            AssertVector(previousA, startBefore);
            AssertVector(previousB, endBefore);
            Assert.That(Vector3.Dot(currentA - previousA, travelAxis), Is.GreaterThan(0f), "the wave travelled");
            AssertSweepLiesOnThePlane(plane, previousA, previousB, currentA, currentB);

            // Both span vectors are the same, which is what makes it a
            // parallelogram rather than a trapezoid.
            AssertVector(currentB - currentA, previousB - previousA);
            AssertVector(currentB - previousB, currentA - previousA);
        }

        [Test]
        public void AWideningSpan_MakesTheSweepATrapezoidThatReachesPastTheOldSpan()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out Vector3 spanAxis, out float spanBefore,
                out _, out _, out Vector3 startBefore, out Vector3 endBefore), Is.True);

            Assert.That(RecordPose(upright, EdgeStep * 3f, SampleInterval), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out _, out _, out _, out float spanAfter,
                out Vector3 previousA, out Vector3 previousB,
                out Vector3 currentA, out Vector3 currentB), Is.True);

            Assert.That(spanAfter, Is.GreaterThan(spanBefore));
            AssertSweepLiesOnThePlane(plane, previousA, previousB, currentA, currentB);

            // The previous segment keeps the length it had; only the current
            // one uses the wider span.
            AssertVector(previousA, startBefore);
            AssertVector(previousB, endBefore);
            Assert.That(Vector3.Distance(previousA, previousB), Is.EqualTo(spanBefore).Within(PositionTolerance));
            Assert.That(Vector3.Distance(currentA, currentB), Is.EqualTo(spanAfter).Within(PositionTolerance));

            // Parallel sides of different lengths: a trapezoid.
            Assert.That(InPlaneCross(plane.normal, previousB - previousA, currentB - currentA),
                Is.EqualTo(0f).Within(1e-4f));
            Assert.That(InPlaneCross(plane.normal, currentB - currentA, currentA - previousA),
                Is.Not.EqualTo(0f).Within(1e-4f), "the sweep has area");

            // A point past the old span, on the current segment, is inside the
            // hull: this update's widening is part of the swept region.
            float beyondOldSpan = (spanBefore + spanAfter) * 0.5f;
            Assert.That(beyondOldSpan, Is.GreaterThan(spanBefore));
            Assert.That(beyondOldSpan, Is.LessThan(spanAfter));
            Vector3 addedPoint = currentA + spanAxis * beyondOldSpan;
            Assert.That(Mathf.Abs(plane.GetDistanceToPoint(addedPoint)), Is.LessThan(PositionTolerance));
            Assert.That(InPlaneCross(plane.normal, currentB - currentA, addedPoint - currentA),
                Is.EqualTo(0f).Within(1e-4f), "it lies on the current segment, an edge of the hull");

            // A later update does not stretch that previous segment to the
            // newer span.
            Assert.That(RecordPose(upright, EdgeStep * 3f, SampleInterval), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanLater,
                out Vector3 laterPreviousA, out Vector3 laterPreviousB, out _, out _), Is.True);
            Assert.That(spanLater, Is.GreaterThan(spanAfter));
            Assert.That(Vector3.Distance(laterPreviousA, laterPreviousB), Is.EqualTo(spanAfter).Within(PositionTolerance));
        }

        [Test]
        public void ARefusedCandidate_StillSweepsAFixedSpanParallelogram()
        {
            Quaternion upright = UprightGrip(follower);
            SweepUntilLatch();
            Assert.That(RecordPose(upright, EdgeStep, SampleInterval), Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out Vector3 travelAxis, out _, out float span,
                out _, out _, out Vector3 startBefore, out Vector3 endBefore), Is.True);

            // Back up the way it came: the candidate shrinks, so it is refused.
            Assert.That(RecordPose(upright, -EdgeStep * 2f, SampleInterval), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out _, out _, out _, out float spanAfter,
                out Vector3 previousA, out Vector3 previousB,
                out Vector3 currentA, out Vector3 currentB), Is.True);

            Assert.That(spanAfter, Is.EqualTo(span));
            AssertVector(previousA, startBefore);
            AssertVector(previousB, endBefore);
            Assert.That(Vector3.Dot(currentA - previousA, travelAxis), Is.GreaterThan(0f));
            AssertSweepLiesOnThePlane(plane, previousA, previousB, currentA, currentB);
            AssertVector(currentB - currentA, previousB - previousA);
        }

        [Test]
        public void NonOrthogonalAxes_SweepNormallyWithoutCorrection()
        {
            // Down and forward along the blade, so the emitter chord is not
            // perpendicular to the travel axis.
            Sweep(UprightGrip(follower), new Vector3(0f, -0.06f, 0.03f), 7);
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out Vector3 travelAxis, out Vector3 spanAxis,
                out _, out _, out _, out Vector3 startBefore, out _), Is.True);
            Assert.That(Mathf.Abs(Vector3.Dot(spanAxis, travelAxis)), Is.GreaterThan(0.1f),
                "the axes are not orthogonal and were not made so");

            SkipTime(0.05);

            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out _, out Vector3 travelAfter,
                out Vector3 spanAxisAfter, out _, out Vector3 previousA, out Vector3 previousB,
                out Vector3 currentA, out Vector3 currentB), Is.True);

            AssertVector(travelAfter, travelAxis);
            AssertVector(spanAxisAfter, spanAxis);
            AssertSweepLiesOnThePlane(plane, previousA, previousB, currentA, currentB);
            Assert.That(Vector3.Dot(currentA - startBefore, travelAxis), Is.GreaterThan(0f));
        }

        [TestCase(1f, TestName = "ParallelAxes_AreAcceptedAndSweepDegeneratesToASegment")]
        [TestCase(-1f, TestName = "AntiParallelAxes_AreAcceptedAndSweepDegeneratesToASegment")]
        public void AxesAlongTheSameLine_AreAcceptedAndSweepDegeneratesToASegment(float spanSign)
        {
            // Straight at the store: no pose sequence puts the span axis on the
            // travel axis, and the contract still has to hold if one did.
            SandboxSlashWaveStore store = new SandboxSlashWaveStore();
            Plane plane = new Plane(Vector3.right, Vector3.zero);
            Vector3 travelAxis = Vector3.forward;
            Vector3 spanAxis = travelAxis * spanSign;
            Vector3 beginEmitter = Vector3.zero;
            Vector3 latestEmitter = beginEmitter + spanAxis * 0.5f;

            Assert.That(store.TryLatch(0.0, plane, beginEmitter, latestEmitter, travelAxis, spanAxis, 0.5f, StoreTestSpanCaptureTimeout), Is.True,
                "axes on one line are not a reason to refuse a wave");
            Assert.That(store.Count, Is.EqualTo(1));

            store.Advance(0.2, 1, false, Vector3.zero, Vector3.zero);

            Assert.That(store.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out Vector3 previousA, out Vector3 previousB,
                out Vector3 currentA, out Vector3 currentB), Is.True);

            Assert.That(store.Count, Is.EqualTo(1), "it was not clipped or retired");
            Assert.That(span, Is.EqualTo(0.5f).Within(PositionTolerance));
            AssertSweepLiesOnThePlane(plane, previousA, previousB, currentA, currentB);
            AssertSweepIsCollinear(plane.normal, previousA, previousB, currentA, currentB);
            Assert.That(Vector3.Dot(currentA - previousA, travelAxis), Is.GreaterThan(0f), "it still flew");
            AssertVector(currentB, currentA + spanAxis * span);
        }

        [Test]
        public void AnExpiredWave_ProducesNoSweepEndpoints()
        {
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double latchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);

            strokeFrameId++;
            strokeTime = latchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);

            Assert.That(follower.WaveCount, Is.EqualTo(0));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.False, "an expired wave has no sweep to evaluate");
        }

        [Test]
        public void TheSandboxScene_HasFourHiddenWaveSlotsSharingOneMeshAndMaterial()
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

                Assert.That(sceneFollower, Is.Not.Null);
                Assert.That(sceneFollower.Katana, Is.Not.Null);

                Assert.That(ReadWaveVisualCount(sceneFollower), Is.EqualTo(SandboxSlashWaveStore.Capacity),
                    "there is one display slot per wave the store can hold");

                Transform displayRoot = null;
                Mesh sharedMesh = null;
                Material sharedMaterial = null;
                for (int i = 0; i < SandboxSlashWaveStore.Capacity; i++)
                {
                    Transform slot = ReadWaveVisual(sceneFollower, i);
                    Assert.That(slot, Is.Not.Null, "display slot " + i + " is not assigned");
                    Assert.That(slot.gameObject.activeSelf, Is.False, "display slot " + i + " is not hidden");

                    // It must not hang off the katana, which disappears with
                    // tracking while the waves carry on.
                    Assert.That(slot.IsChildOf(sceneFollower.Katana), Is.False,
                        "display slot " + i + " is under the katana");

                    Assert.That(slot.GetComponentInChildren<Collider>(true), Is.Null,
                        "display slot " + i + " has a collider");

                    MeshFilter filter = slot.GetComponent<MeshFilter>();
                    MeshRenderer renderer = slot.GetComponent<MeshRenderer>();
                    Assert.That(filter, Is.Not.Null);
                    Assert.That(renderer, Is.Not.Null);

                    if (i == 0)
                    {
                        displayRoot = slot.parent;
                        sharedMesh = filter.sharedMesh;
                        sharedMaterial = renderer.sharedMaterial;

                        Assert.That(displayRoot, Is.Not.Null, "the slots have no root");
                        Assert.That(displayRoot.parent, Is.Null, "the display root is not a scene root");
                        AssertVector(displayRoot.position, Vector3.zero);
                        Assert.That(Quaternion.Angle(displayRoot.rotation, Quaternion.identity), Is.LessThan(AngleTolerance));
                        AssertVector(displayRoot.localScale, Vector3.one);

                        Assert.That(sharedMesh, Is.Not.Null);
                        Assert.That(sharedMaterial, Is.Not.Null);
                        Assert.That(sharedMaterial.shader.name, Is.EqualTo("Universal Render Pipeline/Unlit"));
                        Assert.That(sharedMaterial.GetFloat("_Cull"),
                            Is.EqualTo((float)UnityEngine.Rendering.CullMode.Off),
                            "the slash wave material is not double sided");
                    }
                    else
                    {
                        Assert.That(slot.parent, Is.SameAs(displayRoot), "slot " + i + " is under another root");
                        Assert.That(filter.sharedMesh, Is.SameAs(sharedMesh), "slot " + i + " uses another mesh");
                        Assert.That(renderer.sharedMaterial, Is.SameAs(sharedMaterial), "slot " + i + " uses another material");
                    }
                }
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
        public void ALatchedWave_IsShownOnTheFirstSlotWhereItsSegmentIs()
        {
            GiveTheFollowerWaveVisuals();

            SweepUntilLatch();

            Assert.That(waveVisuals[0].gameObject.activeSelf, Is.True);
            for (int i = 1; i < waveVisuals.Length; i++)
            {
                AssertVisualIsHidden(waveVisuals[i]);
            }

            Assert.That(follower.TryGetWave(0, out _, out Plane plane, out _, out _, out Vector3 spanAxis,
                out float span, out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd), Is.True);

            Transform visual = waveVisuals[0];
            AssertVector(visual.position, (segmentStart + segmentEnd) * 0.5f);
            AssertVector(visual.right, spanAxis);
            AssertVector(visual.forward, plane.normal);
            AssertVector(visual.up, Vector3.Cross(plane.normal, spanAxis).normalized);
            Assert.That(visual.localScale.x, Is.EqualTo(span).Within(PositionTolerance));
            Assert.That(visual.localScale.y, Is.EqualTo(0.35f).Within(PositionTolerance));
            Assert.That(visual.localScale.z, Is.EqualTo(1f).Within(PositionTolerance));
        }

        [Test]
        public void TheVisual_FollowsTheWaveAsItFliesAndWidens()
        {
            GiveTheFollowerWaveVisuals();
            SweepUntilLatch();
            Vector3 positionAtLatch = waveVisuals[0].position;
            float widthAtLatch = waveVisuals[0].localScale.x;

            SkipTime(0.05);
            Assert.That(waveVisuals[0].position, Is.Not.EqualTo(positionAtLatch), "the visual travelled");
            Assert.That(waveVisuals[0].localScale.x, Is.EqualTo(widthAtLatch).Within(PositionTolerance));

            Assert.That(RecordPose(UprightGrip(follower), EdgeStep * 3f, SampleInterval), Is.True);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd), Is.True);
            Assert.That(span, Is.GreaterThan(widthAtLatch));
            Assert.That(waveVisuals[0].localScale.x, Is.EqualTo(span).Within(PositionTolerance));
            AssertVector(waveVisuals[0].position, (segmentStart + segmentEnd) * 0.5f);
        }

        [Test]
        public void AfterTheSpanCloses_TheVisualStillFollowsTheFrozenResult()
        {
            GiveTheFollowerWaveVisuals();
            SweepUntilLatch();

            Quaternion tilted = Quaternion.Euler(45f, 0f, 0f) * Quaternion.Inverse(follower.GripToKatanaOffset.rotation);
            Assert.That(RecordPose(tilted, EdgeStep, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);
            float widthAtClose = waveVisuals[0].localScale.x;

            strokeFrameId++;
            strokeTime += 0.05;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd), Is.True);
            Assert.That(span, Is.GreaterThan(widthAtClose));
            Assert.That(waveVisuals[0].gameObject.activeSelf, Is.True);
            Assert.That(waveVisuals[0].localScale.x, Is.EqualTo(span).Within(PositionTolerance));
            AssertVector(waveVisuals[0].position, (segmentStart + segmentEnd) * 0.5f);
        }

        [Test]
        public void LosingTracking_HidesTheKatanaButNotTheWave()
        {
            GiveTheFollowerWaveVisuals();
            SweepUntilLatch();
            Vector3 before = waveVisuals[0].position;

            strokeFrameId++;
            strokeTime += 0.05;
            Assert.That(follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime)), Is.False);

            Assert.That(katanaObject.activeSelf, Is.False, "the katana goes");
            Assert.That(waveVisuals[0].gameObject.activeSelf, Is.True, "the wave does not");
            Assert.That(waveVisuals[0].position, Is.Not.EqualTo(before), "and it keeps flying");
        }

        [Test]
        public void TwoWaves_AreShownOnTwoSlots()
        {
            GiveTheFollowerWaveVisuals();
            SweepUntilLatch();
            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));

            for (int i = 0; i < 2; i++)
            {
                Assert.That(follower.TryGetWave(i, out _, out _, out _, out _, out _, out _,
                    out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd), Is.True);
                Assert.That(waveVisuals[i].gameObject.activeSelf, Is.True, "slot " + i + " should be shown");
                AssertVector(waveVisuals[i].position, (segmentStart + segmentEnd) * 0.5f);
            }

            AssertVisualIsHidden(waveVisuals[2]);
            AssertVisualIsHidden(waveVisuals[3]);
        }

        [Test]
        public void WhenAWaveExpires_TheTailSlotIsHiddenAndTheRestReseat()
        {
            GiveTheFollowerWaveVisuals();
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double firstLatchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);
            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));
            Assert.That(waveVisuals[1].gameObject.activeSelf, Is.True);

            // Long enough for the first wave and not the second.
            strokeFrameId++;
            strokeTime = firstLatchedAt + SandboxSlashWaveStore.WaveLifetimeSeconds;
            Assert.That(follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition,
                UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)), Is.True);

            Assert.That(follower.WaveCount, Is.EqualTo(1), "the older wave expired");
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd), Is.True);

            // The survivor moved down to slot 0, and the tail slot went out.
            Assert.That(waveVisuals[0].gameObject.activeSelf, Is.True);
            AssertVector(waveVisuals[0].position, (segmentStart + segmentEnd) * 0.5f);
            AssertVisualIsHidden(waveVisuals[1]);
            AssertVisualIsHidden(waveVisuals[2]);
            AssertVisualIsHidden(waveVisuals[3]);
        }

        [Test]
        public void BeforeRenderDoesNotMoveTheWaveVisual()
        {
            GiveTheFollowerWaveVisuals();
            SweepUntilLatch();
            Vector3 position = waveVisuals[0].position;
            Quaternion rotation = waveVisuals[0].rotation;
            Vector3 scale = waveVisuals[0].localScale;

            strokeFrameId++;
            strokeTime += 0.05;
            Assert.That(
                follower.TryApplySample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition + EdgeStep * 4f,
                    UprightGrip(follower), BladeTrackingState.Position | BladeTrackingState.Rotation)),
                Is.True);

            AssertVector(waveVisuals[0].position, position);
            Assert.That(Quaternion.Angle(waveVisuals[0].rotation, rotation), Is.LessThan(AngleTolerance));
            AssertVector(waveVisuals[0].localScale, scale);
        }

        [Test]
        public void ManyUpdates_ReuseTheSameMeshAndMaterialUntouched()
        {
            GiveTheFollowerWaveVisuals();
            MeshFilter filter = waveVisuals[0].GetComponent<MeshFilter>();
            MeshRenderer renderer = waveVisuals[0].GetComponent<MeshRenderer>();
            Mesh mesh = filter.sharedMesh;
            Material material = renderer.sharedMaterial;
            int vertexCount = mesh.vertexCount;
            int indexCount = (int)mesh.GetIndexCount(0);

            SweepUntilLatch();
            for (int i = 0; i < 10; i++)
            {
                Assert.That(RecordPose(UprightGrip(follower), EdgeStep, SampleInterval), Is.True);
            }

            Assert.That(filter.sharedMesh, Is.SameAs(mesh));
            Assert.That(renderer.sharedMaterial, Is.SameAs(material));
            Assert.That(mesh.vertexCount, Is.EqualTo(vertexCount));
            Assert.That((int)mesh.GetIndexCount(0), Is.EqualTo(indexCount));

            for (int i = 0; i < waveVisuals.Length; i++)
            {
                Assert.That(waveVisuals[i].GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
                Assert.That(waveVisuals[i].GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(material));
            }
        }

        [Test]
        public void WithoutAnyWaveVisuals_TheWaveLogicIsUnchanged()
        {
            // No slots assigned at all.
            SweepUntilLatch();

            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out _, out _), Is.True);
            Assert.That(span, Is.GreaterThan(0f));

            SkipTime(0.05);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
        }

        [Test]
        public void TheSandboxScene_HasOneDiagnosticsOverlayReadingTheKatana()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SandboxScenePath, OpenSceneMode.Single);

                List<SandboxSlashDiagnosticsOverlay> overlays = new List<SandboxSlashDiagnosticsOverlay>();
                SandboxRightHandKatana sceneFollower = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    overlays.AddRange(root.GetComponentsInChildren<SandboxSlashDiagnosticsOverlay>(true));
                    if (sceneFollower == null)
                    {
                        sceneFollower = root.GetComponentInChildren<SandboxRightHandKatana>(true);
                    }
                }

                Assert.That(overlays.Count, Is.EqualTo(1), "exactly one diagnostics overlay");
                Assert.That(sceneFollower, Is.Not.Null);

                // The reference is a private serialized field set in the scene.
                SerializedProperty reference = new SerializedObject(overlays[0]).FindProperty("katana");
                Assert.That(reference, Is.Not.Null, "the overlay's katana field is gone");
                Assert.That(reference.objectReferenceValue, Is.SameAs(sceneFollower));
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
        public void Diagnostics_WithNoKatanaAssigned_StillWrites()
        {
            StringBuilder text = new StringBuilder();

            Assert.DoesNotThrow(() => SandboxSlashDiagnosticsOverlay.AppendDiagnostics(text, null));
            Assert.That(text.ToString(), Does.Contain("No SandboxRightHandKatana assigned"));
        }

        [Test]
        public void Diagnostics_WithNoWaves_ShowTheTrackingAndStrokeState()
        {
            StringBuilder text = new StringBuilder();
            SandboxSlashDiagnosticsOverlay.AppendDiagnostics(text, follower);
            string shown = text.ToString();

            Assert.That(shown, Does.Contain("Katana"));
            Assert.That(shown, Does.Contain("recorded 0"));
            Assert.That(shown, Does.Contain("accepted 0"));
            Assert.That(shown, Does.Contain("Latch         waiting"));
            Assert.That(shown, Does.Contain("Stroke begin  no"));
            Assert.That(shown, Does.Contain("Plane         no"));
            Assert.That(shown, Does.Contain("Frame         no"));
            Assert.That(shown, Does.Contain("Waves         0 / " + SandboxSlashWaveStore.Capacity));
            Assert.That(shown, Does.Not.Contain("#0"));
        }

        [Test]
        public void Diagnostics_WithAWave_ShowItsLatchSpanAndSegmentWithoutChangingAnything()
        {
            SweepUntilLatch();
            int accepted = follower.AcceptedSampleCount;
            int recorded = follower.RecordedPoseCount;
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float span,
                out _, out _, out Vector3 currentStart, out _), Is.True);

            StringBuilder text = new StringBuilder();
            SandboxSlashDiagnosticsOverlay.AppendDiagnostics(text, follower);
            string shown = text.ToString();

            Assert.That(shown, Does.Contain("Latch         ready"));
            Assert.That(shown, Does.Contain("Frame         yes  span"));
            Assert.That(shown, Does.Contain("Waves         1 / " + SandboxSlashWaveStore.Capacity));
            Assert.That(shown, Does.Contain("#0"));
            Assert.That(shown, Does.Contain("latched"));
            Assert.That(shown, Does.Contain("open"));
            Assert.That(shown, Does.Contain(" A ("));
            Assert.That(shown, Does.Contain(" B ("));

            // Reading is all it does.
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(accepted));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(recorded));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out float spanAfter,
                out _, out _, out Vector3 currentStartAfter, out _), Is.True);
            Assert.That(spanAfter, Is.EqualTo(span));
            AssertVector(currentStartAfter, currentStart);
        }

        [Test]
        public void Diagnostics_WithAClosedWave_ShowWhenItsSpanClosed()
        {
            SweepUntilLatch();
            Assert.That(RecordPose(UprightGrip(follower), EdgeStep * 4f, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);

            StringBuilder text = new StringBuilder();
            SandboxSlashDiagnosticsOverlay.AppendDiagnostics(text, follower);
            string shown = text.ToString();

            Assert.That(shown, Does.Contain("#0"));
            Assert.That(shown, Does.Contain("closed at"));
            Assert.That(shown, Does.Not.Contain("open"));
        }

        [Test]
        public void TheSandboxScene_HasOnePoseRecorderReadingTheKatana()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SandboxScenePath, OpenSceneMode.Single);

                List<SandboxSlashPoseRecorder> recorders = new List<SandboxSlashPoseRecorder>();
                SandboxRightHandKatana sceneFollower = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    recorders.AddRange(root.GetComponentsInChildren<SandboxSlashPoseRecorder>(true));
                    if (sceneFollower == null)
                    {
                        sceneFollower = root.GetComponentInChildren<SandboxRightHandKatana>(true);
                    }
                }

                Assert.That(recorders.Count, Is.EqualTo(1), "exactly one pose recorder");
                Assert.That(sceneFollower, Is.Not.Null);

                SerializedProperty reference = new SerializedObject(recorders[0]).FindProperty("katana");
                Assert.That(reference, Is.Not.Null, "the recorder's katana field is gone");
                Assert.That(reference.objectReferenceValue, Is.SameAs(sceneFollower));
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
        public void Recorder_WithNothingRecorded_CannotReplay()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(null);

            Assert.That(recorder.TryBeginReplay(0.0), Is.False);
            Assert.That(recorder.IsReplaying, Is.False);
            Assert.That(recorder.TryTakeNextReplaySample(0, out _), Is.False);
        }

        [Test]
        public void Recorder_StopsAtCapacityInsteadOfGrowing()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(null);
            recorder.BeginRecording();

            int stored = 0;
            for (int i = 0; i < SandboxSlashPoseRecorder.Capacity + 100; i++)
            {
                if (recorder.TryAppendRecordedSample(new BladePoseSample(i, i * SampleInterval, Vector3.zero,
                        Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation)))
                {
                    stored++;
                }
            }

            Assert.That(stored, Is.EqualTo(SandboxSlashPoseRecorder.Capacity));
            Assert.That(recorder.RecordedSampleCount, Is.EqualTo(SandboxSlashPoseRecorder.Capacity));
            Assert.That(recorder.IsRecording, Is.False, "a full recording ends itself");
        }

        [Test]
        public void Recorder_Clear_ForgetsTheRecordingAndEndsTheReplay()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            recorder.BeginRecording();
            double time = 2.0;
            Vector3 position = strokePosition;
            AppendSweepToRecording(recorder, UprightGrip(follower), EdgeStep, 10, ref time, ref position);
            recorder.Stop();

            Assert.That(recorder.TryBeginReplay(20.0), Is.True);
            Assert.That(recorder.TryTakeNextReplaySample(0, out _), Is.True);
            Assert.That(recorder.ReplayIndex, Is.EqualTo(1));

            recorder.Clear();

            Assert.That(recorder.RecordedSampleCount, Is.EqualTo(0));
            Assert.That(recorder.ReplayIndex, Is.EqualTo(0));
            Assert.That(recorder.IsReplaying, Is.False);
            Assert.That(follower.LiveInputEnabled, Is.True, "input goes back to the controller");
            Assert.That(recorder.TryBeginReplay(30.0), Is.False);
        }

        [Test]
        public void Recorder_KeepsTrackingLossAndMonotonicTimesAndReplaysThemOnItsOwnClock()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(null);
            recorder.BeginRecording();
            BladeTrackingState tracked = BladeTrackingState.Position | BladeTrackingState.Rotation;

            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(1, 7.000, Vector3.zero, Quaternion.identity, tracked)), Is.True);
            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(2, 7.011, Vector3.one, Quaternion.identity, tracked)), Is.True);
            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(3, 7.011, Vector3.one, Quaternion.identity, tracked)), Is.False,
                "a time that does not move forward is left out");
            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(4, 7.030, Vector3.zero, Quaternion.identity, BladeTrackingState.None)), Is.True,
                "tracking loss is recorded like any other sample");
            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(5, double.NaN, Vector3.zero, Quaternion.identity, tracked)), Is.False);
            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(6, 7.050, Vector3.up, Quaternion.identity, tracked)), Is.True);
            recorder.Stop();
            Assert.That(recorder.RecordedSampleCount, Is.EqualTo(4));

            Assert.That(recorder.TryBeginReplay(100.0), Is.True);

            double[] expected = { 100.000, 100.011, 100.030, 100.050 };
            double previous = double.NegativeInfinity;
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(recorder.TryTakeNextReplaySample(40 + i, out BladePoseSample sample), Is.True);
                Assert.That(sample.TimestampSeconds, Is.EqualTo(expected[i]).Within(1e-9));
                Assert.That(sample.TimestampSeconds, Is.GreaterThan(previous));
                Assert.That(sample.FrameId, Is.EqualTo(40 + i));
                previous = sample.TimestampSeconds;

                if (i == 2)
                {
                    Assert.That(sample.TrackingState, Is.EqualTo(BladeTrackingState.None));
                }
            }

            Assert.That(recorder.TryTakeNextReplaySample(99, out _), Is.False, "the recording has been fed through");
            Assert.That(recorder.IsReplaying, Is.False);
        }

        [Test]
        public void TurningLiveInputOff_StartsTheSlashStateOverOnlyWhenItChanges()
        {
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            follower.LiveInputEnabled = false;

            Assert.That(follower.WaveCount, Is.EqualTo(0));
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));
            Assert.That(katanaObject.activeSelf, Is.False);

            // Fed from elsewhere, samples still go through.
            Assert.That(RecordPose(UprightGrip(follower), EdgeStep, SampleInterval), Is.True);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1));

            follower.LiveInputEnabled = false;
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(1), "setting the same value is not a switch");

            follower.LiveInputEnabled = true;
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0), "switching back starts over too");
        }

        [Test]
        public void ReplayingARecordedSweep_StartsOverAndPublishesTheWaveAgain()
        {
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(1), "a live wave is already flying");

            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            recorder.BeginRecording();
            double time = 3.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            AppendSweepToRecording(recorder, UprightGrip(follower), EdgeStep, 7, ref time, ref position);
            recorder.Stop();

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);

            Assert.That(follower.LiveInputEnabled, Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(0), "replay starts from nothing");
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0));

            ReplayInto(recorder, follower, recorder.RecordedSampleCount);

            Assert.That(follower.WaveCount, Is.EqualTo(1), "the recorded sweep latched again");
            Assert.That(recorder.IsReplaying, Is.True);

            // With every sample fed, the next step ends the replay but keeps
            // input with it, so the replayed wave can still be watched.
            Assert.That(recorder.TryTakeNextReplaySample(99, out _), Is.False);
            Assert.That(recorder.IsReplaying, Is.False);
            Assert.That(recorder.IsShowingReplayResult, Is.True);
            Assert.That(follower.LiveInputEnabled, Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(1), "the replayed wave is still there");

            recorder.Stop();

            Assert.That(recorder.IsShowingReplayResult, Is.False);
            Assert.That(follower.LiveInputEnabled, Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(0), "Stop closes the replay result");
        }

        [Test]
        public void AfterAReplay_ItsWaveFliesOnUntilItExpiresAndClearHandsInputBack()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            recorder.BeginRecording();
            double time = 3.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            AppendSweepToRecording(recorder, UprightGrip(follower), EdgeStep, 7, ref time, ref position);
            recorder.Stop();

            Assert.That(recorder.TryTakeReplayResultTick(0, 0.1, out _), Is.False, "no replay result before a replay");

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(99, out _), Is.False);
            Assert.That(recorder.IsShowingReplayResult, Is.True);
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out Vector3 travelAxis, out _, out _,
                out _, out _, out Vector3 startBefore, out _), Is.True);

            Assert.That(recorder.TryTakeReplayResultTick(100, 0.1, out BladePoseSample tick), Is.True);
            Assert.That(tick.TrackingState, Is.EqualTo(BladeTrackingState.None), "no pose is made up after the recording");
            follower.TryRecordSample(tick);

            Assert.That(follower.LiveInputEnabled, Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 startAfter, out _), Is.True);
            Assert.That(Vector3.Dot(startAfter - startBefore, travelAxis), Is.GreaterThan(0f), "the wave flew on");

            for (int i = 0; i < 20 && follower.WaveCount > 0; i++)
            {
                Assert.That(recorder.TryTakeReplayResultTick(101 + i, 0.1, out tick), Is.True);
                follower.TryRecordSample(tick);
            }

            Assert.That(follower.WaveCount, Is.EqualTo(0), "the wave expired on the continued replay clock");
            Assert.That(follower.LiveInputEnabled, Is.False, "input stays with the replay until it is closed");

            recorder.Clear();

            Assert.That(follower.LiveInputEnabled, Is.True);
            Assert.That(recorder.IsShowingReplayResult, Is.False);
            Assert.That(recorder.TryTakeReplayResultTick(200, 0.1, out _), Is.False);
        }

        private static string ComparisonText(SandboxSlashPoseRecorder recorder)
        {
            StringBuilder text = new StringBuilder();
            recorder.AppendComparison(text);
            return text.ToString();
        }

        // Records the same seven-sample sweep that latches one wave.
        private void RecordReplayableSweep(SandboxSlashPoseRecorder recorder)
        {
            recorder.BeginRecording();
            double time = 3.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            AppendSweepToRecording(recorder, UprightGrip(follower), EdgeStep, 7, ref time, ref position);
            recorder.Stop();
        }

        private static void AssertPinnedWaveMatchesCurrent(SandboxSlashPoseRecorder recorder, SandboxRightHandKatana target, int index)
        {
            Assert.That(target.TryGetWave(index, out double latchedAt, out _, out _, out _, out _, out float acceptedSpan,
                out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd), Is.True);
            bool closed = target.TryGetWaveSpanClose(index, out double closedAt, out _, out _);

            Assert.That(recorder.TryGetPinnedWave(index, out double pinnedLatchedAt, out float pinnedSpan,
                out bool pinnedClosed, out double pinnedClosedAt, out Vector3 pinnedStart, out Vector3 pinnedEnd), Is.True);
            Assert.That(pinnedLatchedAt, Is.EqualTo(latchedAt).Within(1e-9));
            Assert.That(pinnedSpan, Is.EqualTo(acceptedSpan).Within(PositionTolerance));
            Assert.That(pinnedClosed, Is.EqualTo(closed));
            if (closed)
            {
                Assert.That(pinnedClosedAt, Is.EqualTo(closedAt).Within(1e-9));
            }
            else
            {
                Assert.That(double.IsNaN(pinnedClosedAt), Is.True);
            }

            Assert.That(Vector3.Distance(pinnedStart, segmentStart), Is.LessThan(PositionTolerance));
            Assert.That(Vector3.Distance(pinnedEnd, segmentEnd), Is.LessThan(PositionTolerance));
        }

        [Test]
        public void WithoutAPin_TheComparisonShowsNoneForPinned()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            SweepUntilLatch();

            Assert.That(recorder.HasPin, Is.False);
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(0));
            Assert.That(recorder.TryGetPinnedWave(0, out _, out _, out _, out _, out _, out _), Is.False);

            string text = ComparisonText(recorder);
            StringAssert.Contains("Current  waves 1", text);
            StringAssert.Contains("Pinned   none", text);
        }

        [Test]
        public void PinCurrent_CopiesTheKatanasResult()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            SweepUntilLatch();
            SkipTime(0.2);
            Assert.That(follower.WaveCount, Is.EqualTo(1));

            Assert.That(recorder.TryPinCurrent(), Is.True);

            Assert.That(recorder.HasPin, Is.True);
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(follower.WaveCount));
            Assert.That(recorder.PinnedAcceptedSampleCount, Is.EqualTo(follower.AcceptedSampleCount));
            Assert.That(recorder.PinnedLatchReady, Is.EqualTo(follower.IsLatchReady));
            AssertPinnedWaveMatchesCurrent(recorder, follower, 0);
            Assert.That(recorder.TryGetPinnedWave(1, out _, out _, out _, out _, out _, out _), Is.False);

            StringAssert.Contains("Pinned   waves 1", ComparisonText(recorder));
        }

        [Test]
        public void ThePin_DoesNotFollowTheKatanaAfterwards()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            SweepUntilLatch();
            Assert.That(recorder.TryPinCurrent(), Is.True);
            Assert.That(recorder.TryGetPinnedWave(0, out double pinnedLatchedAt, out float pinnedSpan,
                out _, out _, out Vector3 pinnedStart, out Vector3 pinnedEnd), Is.True);

            strokeFrameId++;
            strokeTime += 0.3;
            follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime));

            Assert.That(follower.TryGetWave(0, out _, out _, out _, out _, out _, out _,
                out _, out _, out Vector3 currentStart, out _), Is.True);
            Assert.That(Vector3.Distance(currentStart, pinnedStart), Is.GreaterThan(1f), "the current wave flew on");

            Assert.That(recorder.TryGetPinnedWave(0, out double latchedAtAfter, out float spanAfter,
                out _, out _, out Vector3 startAfter, out Vector3 endAfter), Is.True);
            Assert.That(latchedAtAfter, Is.EqualTo(pinnedLatchedAt));
            Assert.That(spanAfter, Is.EqualTo(pinnedSpan));
            Assert.That(startAfter, Is.EqualTo(pinnedStart));
            Assert.That(endAfter, Is.EqualTo(pinnedEnd));

            strokeFrameId++;
            strokeTime += 2.0;
            follower.TryRecordSample(UntrackedAt(strokeFrameId, strokeTime));

            Assert.That(follower.WaveCount, Is.EqualTo(0), "the current wave expired");
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(1), "the pin keeps it");
        }

        [Test]
        public void ThePin_SurvivesStopButNotClearOrANewRecording()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            SweepUntilLatch();
            Assert.That(recorder.TryPinCurrent(), Is.True);

            recorder.Stop();
            Assert.That(recorder.HasPin, Is.True, "Stop keeps the pin");
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(1));

            recorder.BeginRecording();
            Assert.That(recorder.HasPin, Is.False, "a new recording drops the pin");
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(0));
            StringAssert.Contains("Pinned   none", ComparisonText(recorder));
            recorder.Stop();

            Assert.That(recorder.TryPinCurrent(), Is.True);
            Assert.That(recorder.HasPin, Is.True);

            recorder.Clear();
            Assert.That(recorder.HasPin, Is.False, "Clear drops the pin");
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(0));
        }

        [Test]
        public void ThePin_SurvivesAReplayOfTheSameRecordingAndIsShownBesideIt()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            RecordReplayableSweep(recorder);

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(999, out _), Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(recorder.TryPinCurrent(), Is.True);

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);

            Assert.That(recorder.HasPin, Is.True, "a replay keeps the pin to compare against");
            Assert.That(follower.WaveCount, Is.EqualTo(0));
            string during = ComparisonText(recorder);
            StringAssert.Contains("Current  waves 0", during);
            StringAssert.Contains("Pinned   waves 1", during);

            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(999, out _), Is.False);

            // Same recording on the same replay clock, so the current result
            // lines up with the pin.
            Assert.That(follower.WaveCount, Is.EqualTo(recorder.PinnedWaveCount));
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(recorder.PinnedAcceptedSampleCount));
            AssertPinnedWaveMatchesCurrent(recorder, follower, 0);

            string after = ComparisonText(recorder);
            StringAssert.Contains("Current  waves 1", after);
            StringAssert.Contains("Pinned   waves 1", after);
        }

        // The one line of a comparison that starts with the given prefix.
        private static string ComparisonLine(string text, string prefix)
        {
            string found = null;
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith(prefix))
                {
                    Assert.That(found, Is.Null, "more than one line starts with \"" + prefix + "\"");
                    found = line;
                }
            }

            Assert.That(found, Is.Not.Null, "no line starts with \"" + prefix + "\"");
            return found;
        }

        [Test]
        public void TheComparison_NamesTheFirstCandidateAsCurrentWithoutAPin()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);

            string text = ComparisonText(recorder);

            Assert.That(ComparisonLine(text, "Candidate  "), Is.EqualTo("Candidate  First Candidate (only implemented option)"));
            StringAssert.EndsWith("(First Candidate)", ComparisonLine(text, "Current  "));
            Assert.That(ComparisonLine(text, "Pinned   "), Is.EqualTo("Pinned   none"), "no pin means no candidate on the pinned side");
            Assert.That(recorder.PinnedCandidateName, Is.Null);
        }

        [Test]
        public void PinCurrent_CopiesTheCandidateName()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            SweepUntilLatch();

            Assert.That(recorder.TryPinCurrent(), Is.True);

            Assert.That(recorder.PinnedCandidateName, Is.EqualTo("First Candidate"));
            string pinnedLine = ComparisonLine(ComparisonText(recorder), "Pinned   ");
            StringAssert.StartsWith("Pinned   waves 1", pinnedLine);
            StringAssert.EndsWith("(First Candidate)", pinnedLine);
        }

        [Test]
        public void ThePinnedCandidateName_SurvivesAReplay()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            RecordReplayableSweep(recorder);
            SweepUntilLatch();
            Assert.That(recorder.TryPinCurrent(), Is.True);

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            StringAssert.EndsWith("(First Candidate)", ComparisonLine(ComparisonText(recorder), "Pinned   "));

            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(999, out _), Is.False);

            Assert.That(recorder.PinnedCandidateName, Is.EqualTo("First Candidate"));
            string text = ComparisonText(recorder);
            StringAssert.EndsWith("(First Candidate)", ComparisonLine(text, "Current  "));
            StringAssert.EndsWith("(First Candidate)", ComparisonLine(text, "Pinned   "));
        }

        [Test]
        public void ThePinnedCandidateName_GoesWithClearAndANewRecording()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            SweepUntilLatch();

            Assert.That(recorder.TryPinCurrent(), Is.True);
            recorder.BeginRecording();
            Assert.That(recorder.PinnedCandidateName, Is.Null, "a new recording drops the pinned candidate");
            Assert.That(ComparisonLine(ComparisonText(recorder), "Pinned   "), Is.EqualTo("Pinned   none"));
            recorder.Stop();

            Assert.That(recorder.TryPinCurrent(), Is.True);
            recorder.Clear();
            Assert.That(recorder.PinnedCandidateName, Is.Null, "Clear drops the pinned candidate");
            Assert.That(ComparisonLine(ComparisonText(recorder), "Pinned   "), Is.EqualTo("Pinned   none"));
        }

        private void AssertProvisionalTuning()
        {
            Assert.That(follower.MinimumSpeed, Is.EqualTo(1.5f));
            Assert.That(follower.MinimumDisplacement, Is.EqualTo(0.15f));
            Assert.That(follower.MinimumEdgeLeadScore, Is.EqualTo(0.15f));
            Assert.That(follower.ReturnStrokeEdgeLeadScore, Is.EqualTo(-0.15f));
            Assert.That(follower.LatchChordMetres, Is.EqualTo(0.15f));
            Assert.That(follower.SpanCaptureTimeoutSeconds, Is.EqualTo(0.15f));
            Assert.That(follower.BeginBladeAxisViewDotMinimum, Is.EqualTo(0f));
        }

        [Test]
        public void TuningValues_StartAtTheFormerConstants()
        {
            AssertProvisionalTuning();
        }

        [Test]
        public void TuningSetters_RefuseInvalidValuesAndChangeNothing()
        {
            Assert.That(follower.TrySetMinimumSpeed(float.NaN), Is.False);
            Assert.That(follower.TrySetMinimumSpeed(0f), Is.False);
            Assert.That(follower.TrySetMinimumSpeed(SandboxRightHandKatana.GateMaximumSpeed + 1f), Is.False);
            Assert.That(follower.TrySetMinimumDisplacement(-0.1f), Is.False);
            Assert.That(follower.TrySetMinimumDisplacement(float.PositiveInfinity), Is.False);
            Assert.That(follower.TrySetMinimumEdgeLeadScore(1.1f), Is.False);
            Assert.That(follower.TrySetMinimumEdgeLeadScore(float.NaN), Is.False);
            Assert.That(follower.TrySetReturnStrokeEdgeLeadScore(-1.1f), Is.False);
            Assert.That(follower.TrySetLatchChordMetres(0f), Is.False);
            Assert.That(follower.TrySetLatchChordMetres(float.NaN), Is.False);
            Assert.That(follower.TrySetBeginBladeAxisViewDotMinimum(1.1f), Is.False);
            Assert.That(follower.TrySetBeginBladeAxisViewDotMinimum(float.NaN), Is.False);
            Assert.That(follower.TrySetSpanCaptureTimeoutSeconds(0f), Is.False);
            Assert.That(follower.TrySetSpanCaptureTimeoutSeconds(SandboxSlashWaveStore.WaveLifetimeSeconds), Is.False,
                "a timeout the store refuses would silently stop every wave");

            AssertProvisionalTuning();
        }

        [Test]
        public void RaisingMinimumSpeed_RejectsASweepTheDefaultAccepts()
        {
            // The edge sweep moves 0.06 m every 0.011 s: about 5.5 m/s.
            Assert.That(follower.TrySetMinimumSpeed(8f), Is.True);
            Sweep(UprightGrip(follower), EdgeStep, 7);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.WaveCount, Is.EqualTo(0));

            // The next sample is judged with whatever the value is by then.
            Assert.That(follower.TrySetMinimumSpeed(1.5f), Is.True);
            Sweep(UprightGrip(follower), EdgeStep, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
        }

        [Test]
        public void RaisingMinimumDisplacement_RejectsASwingTheDefaultAccepts()
        {
            // Nothing within the gate's 0.06 s window moves this far at 0.06 m a sample.
            Assert.That(follower.TrySetMinimumDisplacement(0.5f), Is.True);
            Sweep(UprightGrip(follower), EdgeStep, 7);

            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.WaveCount, Is.EqualTo(0));

            Assert.That(follower.TrySetMinimumDisplacement(0.15f), Is.True);
            Sweep(UprightGrip(follower), EdgeStep, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
        }

        [Test]
        public void RaisingMinimumEdgeLeadScore_RejectsADiagonalSweepTheDefaultAccepts()
        {
            // 60 degrees off the edge direction: score 0.5.
            Vector3 diagonalStep = new Vector3(0.866f, -0.5f, 0f) * 0.06f;
            Quaternion upright = UprightGrip(follower);

            Assert.That(follower.TrySetMinimumEdgeLeadScore(0.6f), Is.True);
            Sweep(upright, diagonalStep, 6);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));

            Assert.That(follower.TrySetMinimumEdgeLeadScore(0.15f), Is.True);
            Sweep(upright, diagonalStep, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
        }

        [Test]
        public void LoweringReturnStrokeEdgeLeadScore_KeepsAMildReturnFromReArming()
        {
            // 120 degrees off the edge direction: score -0.5, a return by default.
            Vector3 mildReturnStep = new Vector3(0.866f, 0.5f, 0f) * 0.06f;
            Quaternion upright = UprightGrip(follower);

            Assert.That(follower.TrySetReturnStrokeEdgeLeadScore(-0.8f), Is.True);
            Sweep(upright, EdgeStep, 5);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0));

            Sweep(upright, mildReturnStep, 8);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0), "-0.5 is not far enough onto the spine side for -0.8");

            Assert.That(follower.TrySetReturnStrokeEdgeLeadScore(-0.15f), Is.True);
            Sweep(upright, mildReturnStep, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "with the default it is a return");
        }

        [Test]
        public void RaisingLatchChord_DelaysLatchReadyOnTheSameAcceptedSamples()
        {
            Quaternion upright = UprightGrip(follower);

            // Seven samples reach 0.18 m of chord: ready by the default, not by 0.28 m.
            Assert.That(follower.TrySetLatchChordMetres(0.28f), Is.True);
            Sweep(upright, EdgeStep, 7);
            int accepted = follower.AcceptedSampleCount;
            Assert.That(accepted, Is.GreaterThan(1));
            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(0));

            // The same accepted samples, asked again: readiness follows the value.
            Assert.That(follower.TrySetLatchChordMetres(0.15f), Is.True);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(accepted));
            Assert.That(follower.IsLatchReady, Is.True);

            Assert.That(follower.TrySetLatchChordMetres(0.28f), Is.True);
            Assert.That(follower.IsLatchReady, Is.False);

            // Sweeping on reaches the longer distance, and latches there.
            int extraSamples = 0;
            for (int i = 0; i < 10 && follower.WaveCount == 0; i++)
            {
                Sweep(upright, EdgeStep, 1);
                extraSamples++;
            }

            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(extraSamples, Is.GreaterThan(1), "the longer distance takes more of the sweep");
        }

        [Test]
        public void ALongerSpanCaptureTimeout_ClosesLaterWavesLaterAndLeavesLatchedWavesAlone()
        {
            Quaternion upright = UprightGrip(follower);

            // Latched on the default 0.15 s window, before the value changes.
            SweepUntilLatch();
            Assert.That(follower.TryGetWave(0, out double firstLatchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);

            Assert.That(follower.TrySetSpanCaptureTimeoutSeconds(0.6f), Is.True);

            Assert.That(RecordPose(upright, EdgeStep * 4f, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out double firstClosedAt, out _, out _), Is.True,
                "a latched wave keeps the timeout it latched with");
            Assert.That(firstClosedAt - firstLatchedAt, Is.LessThan(0.6));

            // Latched after the change: still open where the old window would
            // have closed it, closed once the new one has run.
            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));
            Assert.That(follower.TryGetWave(1, out double secondLatchedAt, out _, out _, out _, out _, out _,
                out _, out _, out _, out _), Is.True);

            Assert.That(RecordPose(upright, EdgeStep * 4f, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(1, out _, out _, out _), Is.False);

            Assert.That(RecordPose(upright, EdgeStep * 4f, 0.45), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(1, out double secondClosedAt, out _, out _), Is.True);
            Assert.That(secondClosedAt - secondLatchedAt, Is.GreaterThanOrEqualTo(0.6));
        }

        [Test]
        public void ChangingATuningValue_KeepsRecordReplayAndPinComparisonWorking()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            RecordReplayableSweep(recorder);

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(999, out _), Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(recorder.TryPinCurrent(), Is.True);

            // A latch distance the recorded sweep never reaches.
            Assert.That(follower.TrySetLatchChordMetres(1f), Is.True);
            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            Assert.That(follower.LatchChordMetres, Is.EqualTo(1f), "starting a replay keeps the tuning");
            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(999, out _), Is.False);

            Assert.That(follower.WaveCount, Is.EqualTo(0));
            Assert.That(recorder.PinnedWaveCount, Is.EqualTo(1));
            string text = ComparisonText(recorder);
            StringAssert.StartsWith("Current  waves 0", ComparisonLine(text, "Current  "));
            StringAssert.StartsWith("Pinned   waves 1", ComparisonLine(text, "Pinned   "));

            // Back to the value the pin was taken with: the same recording lines up again.
            Assert.That(follower.TrySetLatchChordMetres(0.15f), Is.True);
            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(recorder.TryTakeNextReplaySample(999, out _), Is.False);
            AssertPinnedWaveMatchesCurrent(recorder, follower, 0);
        }

        // Title, tuning, view, accepted header, 8 accepted samples, begin, waves
        // header, 4 lines for each of 4 waves, the raw span note, and the
        // empty piece after the final newline.
        private const int MaxSlashDumpLines = 1 + 1 + 1 + 1 + 8 + 1 + 1 + 4 * 4 + 1 + 1;

        private static string SlashDumpText(SandboxSlashPoseRecorder recorder)
        {
            StringBuilder text = new StringBuilder();
            recorder.AppendSlashDump(text);
            return text.ToString();
        }

        [Test]
        public void SlashDump_WithAClosedWave_WritesBoundedTextAndLogsWithoutThrowing()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);

            // Far past the eight accepted slots, and on until the wave closes.
            Sweep(UprightGrip(follower), EdgeStep, 20);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(8));
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(RecordPose(UprightGrip(follower), EdgeStep * 4f, 0.2), Is.True);
            Assert.That(follower.TryGetWaveSpanClose(0, out _, out _, out _), Is.True);

            string dump = SlashDumpText(recorder);

            StringAssert.StartsWith("Slash dump", dump);
            StringAssert.Contains("Tuning  min speed 1.500", dump);
            StringAssert.Contains("  #7  t ", dump);
            StringAssert.DoesNotContain("  #8  t ", dump);
            StringAssert.Contains("Begin  t ", dump);
            StringAssert.Contains("latched t ", dump);
            StringAssert.Contains("closed t ", dump);
            StringAssert.Contains("frozen guide  r ", dump);
            StringAssert.Contains("  denominator ", dump);
            StringAssert.Contains("span-guide ", dump);
            StringAssert.Contains("travel-guide ", dump);
            StringAssert.Contains("current A ", dump);
            Assert.That(dump.Split('\n').Length, Is.LessThanOrEqualTo(MaxSlashDumpLines));

            LogAssert.Expect(LogType.Log, new Regex("^Slash dump"));
            Assert.DoesNotThrow(() => recorder.LogSlashDump());
        }

        [Test]
        public void SlashDump_WithNothingToShowOrNoKatana_DoesNotThrow()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            string empty = SlashDumpText(recorder);
            StringAssert.Contains("Accepted 0", empty);
            StringAssert.Contains("Begin  none", empty);
            StringAssert.Contains("Waves 0", empty);
            StringAssert.Contains("View  no reference", empty);

            Object.DestroyImmediate(recorderObject);
            SandboxSlashPoseRecorder unassigned = CreateRecorder(null);
            StringAssert.Contains("No katana assigned.", SlashDumpText(unassigned));
            LogAssert.Expect(LogType.Log, new Regex("^Slash dump"));
            Assert.DoesNotThrow(() => unassigned.LogSlashDump());
        }

        // Sweep, with every sample handed the given view forward.
        private void SweepViewing(Quaternion gripRotation, Vector3 step, int count, Vector3 viewForward)
        {
            for (int i = 0; i < count; i++)
            {
                strokeFrameId++;
                strokeTime += SampleInterval;
                strokePosition += step;
                Assert.That(
                    follower.TryRecordSample(new BladePoseSample(strokeFrameId, strokeTime, strokePosition, gripRotation,
                        BladeTrackingState.Position | BladeTrackingState.Rotation), viewForward),
                    Is.True);
            }
        }

        // The view reference is a private serialized field, assigned in the
        // scene; tests set it the way the editor does.
        private Transform GiveTheFollowerAViewFacing(Vector3 forward)
        {
            GameObject view = new GameObject("View");
            view.transform.SetParent(rigObject.transform, false);
            view.transform.rotation = Quaternion.LookRotation(forward);

            SerializedObject serialized = new SerializedObject(follower);
            SerializedProperty property = serialized.FindProperty("viewForwardReference");
            Assert.That(property, Is.Not.Null, "the katana's view reference field is gone");
            property.objectReferenceValue = view.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return view.transform;
        }

        [Test]
        public void WithoutAViewReference_AStrokeBeginsAndLatchesAsBefore()
        {
            Assert.That(follower.CurrentViewForward, Is.EqualTo(Vector3.zero));

            SweepUntilLatch();

            Assert.That(follower.TryGetStrokeBeginSample(out _), Is.True);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.StrokeBeginViewForward, Is.EqualTo(Vector3.zero), "no view was checked");
        }

        [Test]
        public void ABladePointingAwayFromTheView_DoesNotBeginUntilTheSameMotionFacesIt()
        {
            // The upright blade points along +Z.
            Quaternion upright = UprightGrip(follower);

            // Looking along -Z: every accepted-worthy sample faces away.
            SweepViewing(upright, EdgeStep, 7, Vector3.back);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));
            Assert.That(follower.TryGetStrokeBeginSample(out _), Is.False);
            Assert.That(follower.IsLatchReady, Is.False);
            Assert.That(follower.TryGetSlashFrameCandidate(out _, out _, out _, out _, out _, out _), Is.False);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(7), "a refused begin resets nothing");
            long lastRefusedFrameId = strokeFrameId;

            // The same motion goes on, now facing the view.
            SweepViewing(upright, EdgeStep, 1, Vector3.forward);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);
            Assert.That(begin.FrameId, Is.GreaterThan(lastRefusedFrameId));
            Assert.That(follower.StrokeBeginViewForward, Is.EqualTo(Vector3.forward));

            for (int i = 0; i < 10 && follower.WaveCount == 0; i++)
            {
                SweepViewing(upright, EdgeStep, 1, Vector3.forward);
            }

            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose latchedBegin), Is.True);
            Assert.That(latchedBegin.FrameId, Is.EqualTo(begin.FrameId), "the wave latched from that begin");
        }

        [Test]
        public void OnceBegun_LookingAwayDoesNotStopOrSplitTheStroke()
        {
            Quaternion upright = UprightGrip(follower);
            SweepViewing(upright, EdgeStep, 5, Vector3.forward);
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose begin), Is.True);
            int acceptedBefore = follower.AcceptedSampleCount;

            SweepViewing(upright, EdgeStep, 2, Vector3.back);

            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(acceptedBefore));
            Assert.That(follower.TryGetStrokeBeginSample(out EvaluatedBladePose stillBegin), Is.True);
            Assert.That(stillBegin.FrameId, Is.EqualTo(begin.FrameId));
        }

        [Test]
        public void RaisingTheBeginViewDot_RefusesABladeTheDefaultWouldBegin()
        {
            Quaternion upright = UprightGrip(follower);

            // 45 degrees between the blade and the view: dot about 0.71.
            Vector3 view = new Vector3(1f, 0f, 1f).normalized;

            Assert.That(follower.TrySetBeginBladeAxisViewDotMinimum(0.8f), Is.True);
            SweepViewing(upright, EdgeStep, 7, view);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));

            Assert.That(follower.TrySetBeginBladeAxisViewDotMinimum(0f), Is.True);
            SweepViewing(upright, EdgeStep, 1, view);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1));
        }

        [Test]
        public void Replay_ChecksTheBeginAgainstTheRecordedViewNotTheLiveOne()
        {
            Quaternion upright = UprightGrip(follower);
            GiveTheFollowerAViewFacing(Vector3.forward);
            Assert.That(follower.CurrentViewForward.z, Is.EqualTo(1f).Within(PositionTolerance));

            // Recorded while looking back along -Z.
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            recorder.BeginRecording();
            double time = 3.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            for (int i = 0; i < 7; i++)
            {
                time += SampleInterval;
                position += EdgeStep;
                Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(0, time, position, upright,
                    BladeTrackingState.Position | BladeTrackingState.Rotation), Vector3.back), Is.True);
            }

            recorder.Stop();

            Assert.That(recorder.TryBeginReplay(50.0), Is.True);
            ReplayInto(recorder, follower, recorder.RecordedSampleCount);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "the live view faces the blade; the recorded one did not");
            Assert.That(follower.WaveCount, Is.EqualTo(0));
            recorder.Stop();

            // The live path reads the reference, which faces the blade.
            Sweep(upright, EdgeStep, 7);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
        }

        [Test]
        public void TheSandboxScene_ChecksBeginsAgainstTheMainCamera()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SandboxScenePath, OpenSceneMode.Single);

                SandboxRightHandKatana sceneFollower = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (sceneFollower == null)
                    {
                        sceneFollower = root.GetComponentInChildren<SandboxRightHandKatana>(true);
                    }
                }

                Assert.That(sceneFollower, Is.Not.Null);
                SerializedProperty reference = new SerializedObject(sceneFollower).FindProperty("viewForwardReference");
                Assert.That(reference, Is.Not.Null, "the katana's view reference field is gone");

                Transform view = reference.objectReferenceValue as Transform;
                Assert.That(view, Is.Not.Null, "the katana has no view reference");
                Assert.That(view.name, Is.EqualTo("Main Camera"));
                Assert.That(view.GetComponent<Camera>(), Is.Not.Null);
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
        public void ObserveNewLatch_ReportsEachNewWaveOnceWhileItsBeginIsStillThere()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            Quaternion upright = UprightGrip(follower);

            Assert.That(recorder.ObserveNewLatch(), Is.False, "no wave yet");

            SweepViewing(upright, EdgeStep, 7, Vector3.forward);
            Assert.That(follower.WaveCount, Is.EqualTo(1));
            Assert.That(recorder.ObserveNewLatch(), Is.True);
            Assert.That(recorder.ObserveNewLatch(), Is.False, "the same wave is reported once");

            // At the latch the begin is still there, with what its view check saw.
            string dump = SlashDumpText(recorder);
            StringAssert.Contains("Begin  t ", dump);
            StringAssert.Contains("  view (", dump);
            StringAssert.Contains("dot 1.000", dump);

            SweepViewing(upright, EdgeStep, 2, Vector3.forward);
            Assert.That(recorder.ObserveNewLatch(), Is.False, "the stroke going on is not a new latch");

            ReArmStroke();
            SweepUntilLatch();
            Assert.That(follower.WaveCount, Is.EqualTo(2));
            Assert.That(recorder.ObserveNewLatch(), Is.True);

            SkipTime(2.0);
            Assert.That(follower.WaveCount, Is.EqualTo(0));
            Assert.That(recorder.ObserveNewLatch(), Is.False, "expiry is not a latch");
        }

        [Test]
        public void RawSpanTerms_AreTheIntersectionAlongTheSpanAndTheGuide()
        {
            // On the x = 0 plane: the span line runs up +Y from the origin, and
            // a guide from (0, 0, 1) heading down to z = 0 meets it at y = 1.
            Vector3 normal = Vector3.right;
            Vector3 guideDirection = new Vector3(0f, 1f, -1f).normalized;

            Assert.That(SandboxSlashWaveStore.TryEvaluateRawSpanTerms(normal, Vector3.zero, Vector3.up,
                new Vector3(0f, 0f, 1f), guideDirection, out float r, out float q, out float denominator), Is.True);
            Assert.That(r, Is.EqualTo(1f).Within(PositionTolerance));
            Assert.That(q, Is.EqualTo(Mathf.Sqrt(2f)).Within(PositionTolerance));
            Assert.That(Mathf.Abs(denominator), Is.EqualTo(Mathf.Sqrt(0.5f)).Within(PositionTolerance));
            Assert.That(SandboxSlashWaveStore.IsUsableRawSpan(r, q, denominator), Is.True);

            // Behind the span start: finite terms, not a usable candidate.
            Assert.That(SandboxSlashWaveStore.TryEvaluateRawSpanTerms(normal, Vector3.zero, Vector3.up,
                new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, -1f).normalized, out r, out q, out denominator), Is.True);
            Assert.That(r, Is.LessThan(0f));
            Assert.That(SandboxSlashWaveStore.IsUsableRawSpan(r, q, denominator), Is.False);

            // Nearly parallel: the meeting point is far out, and not usable.
            Assert.That(SandboxSlashWaveStore.TryEvaluateRawSpanTerms(normal, Vector3.zero, Vector3.up,
                new Vector3(0f, 0f, 1f), new Vector3(0f, 1f, -1e-4f).normalized, out r, out q, out denominator), Is.True);
            Assert.That(r, Is.GreaterThan(1000f));
            Assert.That(SandboxSlashWaveStore.IsUsableRawSpan(r, q, denominator), Is.False);

            // Exactly parallel: no finite terms at all.
            Assert.That(SandboxSlashWaveStore.TryEvaluateRawSpanTerms(normal, Vector3.zero, Vector3.up,
                new Vector3(0f, 0f, 1f), Vector3.up, out _, out _, out _), Is.False);
        }

        [Test]
        public void ReplayingARecordingWithATrackingGap_RefillsTheWindowAfterIt()
        {
            SandboxSlashPoseRecorder recorder = CreateRecorder(follower);
            Quaternion upright = UprightGrip(follower);
            recorder.BeginRecording();
            double time = 1.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            AppendSweepToRecording(recorder, upright, EdgeStep, 5, ref time, ref position);
            time += SampleInterval;
            Assert.That(recorder.TryAppendRecordedSample(new BladePoseSample(0, time, position, upright, BladeTrackingState.None)), Is.True);
            AppendSweepToRecording(recorder, upright, EdgeStep, 4, ref time, ref position);
            recorder.Stop();

            Assert.That(recorder.TryBeginReplay(10.0), Is.True);

            ReplayInto(recorder, follower, 5);
            Assert.That(follower.AcceptedSampleCount, Is.GreaterThan(0), "the sweep before the gap was accepted");

            ReplayInto(recorder, follower, 1);
            Assert.That(follower.RecordedPoseCount, Is.EqualTo(0), "the gap empties the history");
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0));

            ReplayInto(recorder, follower, 3);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(0), "three poses cannot span the window yet");

            ReplayInto(recorder, follower, 1);
            Assert.That(follower.AcceptedSampleCount, Is.EqualTo(1), "the window refilled after the gap");
        }

        // Update and Before Render only run in Play Mode. No controller exists
        // in a test run, so a live read would be untracked: it would hide the
        // katana and empty the history on every frame it got through.
        [UnityTest]
        public IEnumerator DuringReplay_TheControllerIsNotRead()
        {
            yield return new EnterPlayMode();

            GameObject rig = new GameObject("Sandbox Katana Rig");
            GameObject playKatana = new GameObject("Katana");
            SandboxRightHandKatana playFollower = rig.AddComponent<SandboxRightHandKatana>();
            playFollower.Katana = playKatana.transform;

            GameObject host = new GameObject("Slash Diagnostics");
            SandboxSlashPoseRecorder recorder = host.AddComponent<SandboxSlashPoseRecorder>();
            AssignRecorderKatana(recorder, playFollower);

            recorder.BeginRecording();
            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            AppendSweepToRecording(recorder, UprightGrip(playFollower), EdgeStep, 40, ref time, ref position);
            recorder.Stop();

            Assert.That(recorder.TryBeginReplay(Time.unscaledTimeAsDouble), Is.True);
            Assert.That(playFollower.LiveInputEnabled, Is.False);

            for (int i = 0; i < 6; i++)
            {
                yield return null;
            }

            Assert.That(recorder.IsReplaying, Is.True);
            Assert.That(recorder.ReplayIndex, Is.GreaterThanOrEqualTo(2), "the recorder fed samples on Update");
            Assert.That(playKatana.activeSelf, Is.True, "no untracked live pose hid the replayed katana");
            Assert.That(playFollower.RecordedPoseCount, Is.GreaterThanOrEqualTo(2),
                "no untracked live pose emptied the replayed history");

            recorder.Stop();
            Assert.That(playFollower.LiveInputEnabled, Is.True);

            yield return new ExitPlayMode();
        }

        [Test]
        public void WithoutAKatanaTransform_NoPoseIsApplied()
        {
            follower.Katana = null;

            Assert.That(follower.TryApplySample(Tracked(Vector3.one, Quaternion.identity)), Is.False);
        }
    }
}
