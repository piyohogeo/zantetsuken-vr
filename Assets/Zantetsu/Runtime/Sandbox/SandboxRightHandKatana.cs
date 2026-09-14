using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.Core.Input;

[assembly: InternalsVisibleTo("Zantetsu.Core.EditModeTests")]

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Minimal Quest Link smoke component for the shared sandbox scene: it
    /// reads the right-hand controller's OpenXR grip pose, converts it with
    /// <see cref="BladePoseAdapter"/>, and drives the katana transform.
    ///
    /// Device input, pose conversion and display update deliberately live in
    /// one place; this assembly is the boundary that keeps XR device APIs out
    /// of the device-independent Zantetsu.Core. The aim (pointer) pose is
    /// never used. No velocity history, gesture, stroke or slash logic exists
    /// here.
    ///
    /// The katana is hidden whenever it is not showing a pose derived from a
    /// usable grip sample, including before the first one and after the
    /// component is re-enabled. Its visibility is therefore the whole
    /// following state: no separate flag or cached pose is kept.
    ///
    /// The one <see cref="BladePoseWindow"/> owned here is the only pose
    /// history, and it never leaves this component. Update is the only
    /// boundary that appends to it, so a frame contributes at most one sample.
    /// Before Render neither appends nor evaluates motion, but it does empty
    /// the history when it observes a sample that cannot be used.
    ///
    /// Appending and resetting are separate concerns. Any sample that cannot
    /// be shown -- untracked position or rotation, a non-finite value --
    /// empties the history at whichever boundary observed it, Before Render
    /// included, so a tracking gap seen only between two Updates still breaks
    /// the span. A shown sample the history itself refuses -- a timestamp that
    /// does not move forward -- empties it too.
    ///
    /// Gesture acceptance lives here too, on the Update boundary only: the
    /// span the history reports is run through <see cref="BladeEdgeGate"/>,
    /// and a pose that passes becomes an accepted sample. The first accepted
    /// sample of a stroke is its begin sample and is not replaced while the
    /// stroke continues. Whether a stroke is under way is just whether any
    /// accepted sample exists; there is no separate armed/active state.
    ///
    /// A motion that clears every non-directional check but leads with the
    /// spine is the return half of a stroke already under way: it is rejected,
    /// it drops the raw history and the accepted samples together, and it is
    /// not itself accepted, so only a new edge-leading pass begins the next
    /// stroke. With no stroke under way the same motion is only rejected; it
    /// does not keep wiping the raw history. Too
    /// short a window, too little speed and too little displacement decide
    /// nothing -- they neither begin nor end a stroke. An impossible speed and
    /// every reset listed above drop both, but none of it hides the katana: a
    /// pose that can be shown is still shown.
    ///
    /// The sandbox scene keeps the XR Origin at the world origin with a Floor
    /// tracking origin, so device poses are already world-space poses.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxRightHandKatana : MonoBehaviour
    {
        /// <summary>
        /// Fraction of the blade length at which the cut sample point sits.
        /// A fixed implementation constant, not a tuning value.
        /// </summary>
        internal const float CutSampleRatio = 0.7f;

        // Fixed implementation capacity, not a tuning value: eight samples
        // cover the 30-60 ms initial sample window at the 90 Hz Quest Link
        // mode. Nothing here keeps history for a longer span.
        private const int HistoryCapacity = 8;

        private readonly BladePoseWindow poseHistory = new BladePoseWindow(HistoryCapacity);

        // Fixed implementation capacity, not a tuning value. Allocated once;
        // the stroke never grows it, reallocates, or queues.
        private const int AcceptedSampleCapacity = 8;

        private readonly EvaluatedBladePose[] acceptedSamples = new EvaluatedBladePose[AcceptedSampleCapacity];
        private int acceptedSampleCount;

        // Provisional Phase 0.52 gate values, fixed in code on purpose: no
        // profile, asset, scene setting or tuning UI exists for them yet.
        private static readonly BladeEdgeGateSettings GateSettings = new BladeEdgeGateSettings(
            0.030,  // minimum window, seconds
            0.060,  // maximum window, seconds
            1.5f,   // minimum speed, m/s
            20f,    // maximum speed, m/s -- above this the motion is not a swing
            0.15f,  // minimum cut sample displacement, m
            0.15f); // minimum edge lead score

        // The mirror of the edge lead threshold: a motion this far onto the
        // spine side is the return half of the stroke.
        private const float ReturnStrokeEdgeLeadScore = -0.15f;

        [Tooltip("Katana visual root. Its local axes are the blade frame: +Z blade axis, -Y edge direction, +X side normal.")]
        [SerializeField] private Transform katana;

        [Header("Provisional fixed grip-to-katana offset")]
        [SerializeField] private Vector3 offsetPosition = new Vector3(0f, 0f, 0.02f);
        [SerializeField] private Vector3 offsetEulerAngles = new Vector3(-15f, 0f, 0f);

        [Header("Blade")]
        [Tooltip("Blade length in metres, from the katana origin toward the tip.")]
        [SerializeField] private float bladeLength = 0.9f;

        [SerializeField] private bool drawGizmos = true;

        /// <summary>Katana visual root driven by the grip pose.</summary>
        internal Transform Katana
        {
            get => katana;
            set
            {
                katana = value;
                Hide();
            }
        }

        /// <summary>
        /// Number of poses currently in the history. Derived and read-only:
        /// the history itself never leaves this component.
        /// </summary>
        internal int RecordedPoseCount => poseHistory.Count;

        /// <summary>
        /// Number of accepted samples in the stroke under way. Zero means no
        /// stroke is under way; nothing else records that.
        /// </summary>
        internal int AcceptedSampleCount => acceptedSampleCount;

        /// <summary>
        /// The stroke's begin sample, or false when no stroke is under way.
        /// The accepted samples themselves never leave this component.
        /// </summary>
        internal bool TryGetStrokeBeginSample(out EvaluatedBladePose sample)
        {
            if (acceptedSampleCount == 0)
            {
                sample = default;
                return false;
            }

            sample = acceptedSamples[0];
            return true;
        }

        /// <summary>The single provisional fixed grip-to-katana offset.</summary>
        internal Pose GripToKatanaOffset => new Pose(offsetPosition, Quaternion.Euler(offsetEulerAngles));

        /// <summary>Blade-local coordinate system of the katana visual root.</summary>
        // cross(BladeAxis, EdgeDirection) == SideNormal: cross(+Z, -Y) == +X.
        internal BladeFrame BladeFrame => new BladeFrame(
            Vector3.forward,
            Vector3.down,
            Vector3.right,
            Vector3.forward * (bladeLength * CutSampleRatio));

        /// <summary>
        /// Test seam and hot path: shows one grip pose sample without ever
        /// appending it. A sample whose position or rotation is untracked (or
        /// otherwise unusable) leaves the katana transform alone, hides it, and
        /// empties the history, so an unusable pose is never displayed and no
        /// span is carried across the gap it marks. A later usable sample
        /// resumes following from that sample. This is the Before Render path.
        /// </summary>
        internal bool TryApplySample(in BladePoseSample sample)
        {
            return TryApplySample(sample, out _);
        }

        /// <summary>
        /// Test seam and hot path: shows one grip pose sample and records it.
        /// This is the Update path, and the only one that appends. A sample the
        /// history refuses still shows, because it is a usable pose, just not a
        /// usable span end.
        /// </summary>
        internal bool TryRecordSample(in BladePoseSample sample)
        {
            // A sample that cannot be shown has already reset the stroke.
            if (!TryApplySample(sample, out EvaluatedBladePose evaluated))
            {
                return false;
            }

            // The history refuses a timestamp that does not move forward; it
            // clears itself, and the accepted samples go with it.
            if (!poseHistory.TryAppend(evaluated))
            {
                acceptedSampleCount = 0;
                return false;
            }

            EvaluateGesture(evaluated);
            return true;
        }

        // Update boundary only. Before Render never reaches here.
        private void EvaluateGesture(in EvaluatedBladePose current)
        {
            if (!poseHistory.TryEvaluateLatest(GateSettings.MinimumWindowSeconds, GateSettings.MaximumWindowSeconds, out BladeMotionSample motion))
            {
                // Not enough history to span the window yet: decide nothing.
                return;
            }

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(motion, GateSettings);
            if (decision.IsAccepted)
            {
                AppendAcceptedSample(current);
                return;
            }

            if (decision.Reason == BladeEdgeGateReason.SpeedAboveMaximum)
            {
                ResetStroke();
                return;
            }

            // Reaching the edge lead check means every non-directional check
            // passed, so a score this far onto the spine side is the return
            // half of the stroke rather than a slow or short motion. Only a
            // stroke that is under way has a return half: with nothing
            // accepted this is just a rejected motion, and clearing the raw
            // history would throw away samples for no reason.
            if (acceptedSampleCount > 0
                && decision.Reason == BladeEdgeGateReason.EdgeLeadBelowThreshold
                && motion.EdgeLeadScore <= ReturnStrokeEdgeLeadScore)
            {
                ResetStroke();
            }
        }

        private void AppendAcceptedSample(in EvaluatedBladePose pose)
        {
            if (acceptedSampleCount < AcceptedSampleCapacity)
            {
                acceptedSamples[acceptedSampleCount] = pose;
                acceptedSampleCount++;
                return;
            }

            // Full: keep the begin sample at index 0 and overwrite the last
            // slot with the newest one. No growth, no allocation, no shifting.
            acceptedSamples[AcceptedSampleCapacity - 1] = pose;
        }

        private void ResetStroke()
        {
            poseHistory.Clear();
            acceptedSampleCount = 0;
        }

        private bool TryApplySample(in BladePoseSample sample, out EvaluatedBladePose evaluated)
        {
            if (katana == null || !float.IsFinite(bladeLength) || bladeLength <= 0f)
            {
                evaluated = default;
                ResetStroke();
                return false;
            }

            if (!BladePoseAdapter.TryEvaluate(sample, GripToKatanaOffset, BladeFrame, out evaluated))
            {
                Hide();
                ResetStroke();
                return false;
            }

            katana.SetPositionAndRotation(evaluated.KatanaPose.position, evaluated.KatanaPose.rotation);
            katana.gameObject.SetActive(true);
            return true;
        }

        /// <summary>
        /// Reads the right-hand controller's grip pose. Position, rotation and
        /// tracking state all come from the device pose (never the aim pose);
        /// a missing device or a feature the runtime does not report yields an
        /// untracked sample rather than a stale one.
        /// </summary>
        private static BladePoseSample ReadRightHandGripSample()
        {
            long frameId = Time.frameCount;
            double timestampSeconds = Time.unscaledTimeAsDouble;

            InputDevice device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!device.isValid)
            {
                return new BladePoseSample(frameId, timestampSeconds, Vector3.zero, Quaternion.identity, BladeTrackingState.None);
            }

            bool hasPosition = device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 gripPosition);
            bool hasRotation = device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion gripRotation);
            if (!device.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState trackingState))
            {
                trackingState = InputTrackingState.None;
            }

            BladeTrackingState state = BladeTrackingState.None;
            if (hasPosition && (trackingState & InputTrackingState.Position) != 0)
            {
                state |= BladeTrackingState.Position;
            }

            if (hasRotation && (trackingState & InputTrackingState.Rotation) != 0)
            {
                state |= BladeTrackingState.Rotation;
            }

            return new BladePoseSample(frameId, timestampSeconds, gripPosition, gripRotation, state);
        }

        // The katana starts hidden and stays hidden across a disable/enable
        // cycle, so the pose left over from before is never shown again.
        //
        // The katana is applied on Before Render as well as on Update, the way
        // the camera's Tracked Pose Driver is, so the two do not show poses
        // sampled at different times. The enable/disable lifetime owns the
        // subscription, so no re-entry flag is needed, and it resets the
        // history and the stroke so neither is continued across the gap.
        private void OnEnable()
        {
            Hide();
            ResetStroke();
            Application.onBeforeRender += ApplyGripPoseForRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= ApplyGripPoseForRender;
            Hide();
            ResetStroke();
        }

        private void Hide()
        {
            if (katana != null)
            {
                katana.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            TryRecordSample(ReadRightHandGripSample());
        }

        private void ApplyGripPoseForRender()
        {
            TryApplySample(ReadRightHandGripSample());
        }

        private void OnDrawGizmos()
        {
            // Only a katana that is showing a usable grip pose is visible, so
            // its transform is the drawn blade frame.
            if (!drawGizmos || katana == null || !katana.gameObject.activeSelf)
            {
                return;
            }

            Vector3 origin = katana.position;
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(origin, origin + katana.forward * bladeLength);
            Gizmos.color = Color.red;
            Gizmos.DrawLine(origin, origin - katana.up * 0.1f);
            Gizmos.color = Color.green;
            Gizmos.DrawLine(origin, origin + katana.right * 0.1f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(origin + katana.forward * (bladeLength * CutSampleRatio), 0.02f);
        }
    }
}
