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
    /// Device input, pose conversion, display update, gesture acceptance and
    /// slash wave publication deliberately live in one place; this assembly is
    /// the boundary that keeps XR device APIs out of the device-independent
    /// Zantetsu.Core. The aim (pointer) pose is never used. Everything past
    /// publication -- raw span candidates, the guide ray, span close, wave
    /// travel and VFX -- is still absent.
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
    /// The stroke's source slash plane candidate, whether it has swept far
    /// enough to latch, and its first-candidate slash frame are all derived
    /// from the accepted samples on demand rather than stored, so they live
    /// and die with them.
    ///
    /// A published wave does not. Once latched it is state of its own in the
    /// wave store, flying on from that latch and outliving the stroke that
    /// produced it: losing tracking or sweeping back re-arms the next stroke,
    /// carries the waves forward, and leaves them otherwise alone. Every pose
    /// this component can show and record is offered to those waves as a live
    /// guide, whether or not the gate accepted it into the stroke.
    /// Only disabling the component ends the waves it owns. A stroke gets one
    /// chance to latch -- if the store was full at that moment, that stroke
    /// does not get another when a slot later frees up.
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

        private readonly SandboxSlashWaveStore waveStore = new SandboxSlashWaveStore();

        // The whole of the "one latch per stroke" rule: set when a stroke has
        // had its chance, cleared with the stroke itself.
        private bool strokeLatchSpent;

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

        // A derived vector shorter than this cannot be normalised into a
        // direction: a plane normal this short is a stroke with no swept area,
        // and a frame axis this short has no direction to report.
        private const float MinDerivedVectorLengthSquared = 1e-12f;

        // Provisional Phase 0.53 values, fixed in code like the gate's.
        // The emission control point sits halfway along the blade, apart from
        // the cut sample point at 70%.
        private const float EmissionControlPointRatio = 0.5f;

        // The emitter chord a stroke must have swept before it is worth
        // latching.
        private const float LatchChordMetres = 0.15f;

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

        /// <summary>
        /// Derives the stroke's source slash plane from the accepted samples,
        /// or false when there are fewer than two of them or the samples are
        /// too degenerate to give a finite plane. Nothing is stored: the plane is
        /// computed from the accepted samples every time it is asked for, so
        /// it becomes unavailable the moment they do.
        ///
        /// Each consecutive pair contributes the cross product of the later
        /// sample's blade axis with the movement between them, folded onto
        /// that sample's side normal, so movement along the blade contributes
        /// nothing and no separate projection is kept. The plane passes
        /// through the stroke's begin cut sample point, and the summed normal
        /// finally takes the side the newest accepted sample faces.
        /// </summary>
        internal bool TryGetSourceSlashPlaneCandidate(out Plane plane)
        {
            plane = default;

            if (acceptedSampleCount < 2)
            {
                return false;
            }

            Vector3 sum = Vector3.zero;
            for (int i = 1; i < acceptedSampleCount; i++)
            {
                Vector3 movement = acceptedSamples[i].CutSamplePosition - acceptedSamples[i - 1].CutSamplePosition;
                Vector3 candidate = Vector3.Cross(acceptedSamples[i].BladeAxis, movement);
                if (!IsFinite(candidate))
                {
                    return false;
                }

                if (Vector3.Dot(candidate, acceptedSamples[i].SideNormal) < 0f)
                {
                    candidate = -candidate;
                }

                sum += candidate;
            }

            if (!IsFinite(sum))
            {
                return false;
            }

            float lengthSquared = sum.sqrMagnitude;
            if (!float.IsFinite(lengthSquared) || lengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float length = Mathf.Sqrt(lengthSquared);
            Vector3 normal = new Vector3(sum.x / length, sum.y / length, sum.z / length);
            if (!IsFinite(normal))
            {
                return false;
            }

            if (Vector3.Dot(normal, acceptedSamples[acceptedSampleCount - 1].SideNormal) < 0f)
            {
                normal = -normal;
            }

            Vector3 origin = acceptedSamples[0].CutSamplePosition;
            if (!IsFinite(origin))
            {
                return false;
            }

            // A finite normal and a finite point can still give a distance
            // that overflows, so the constructed plane is what gets checked.
            plane = new Plane(normal, origin);
            if (!IsFinite(plane.normal) || !float.IsFinite(plane.distance))
            {
                plane = default;
                return false;
            }

            return true;
        }

        private static bool IsFinite(Vector3 v)
        {
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }

        /// <summary>
        /// Whether the stroke has swept far enough to be worth latching:
        /// the emitter chord from its begin sample to its newest one has
        /// reached the latch distance. Derived on demand, so it goes the
        /// moment the accepted samples do, and it says nothing about whether
        /// a plane or a frame can be derived -- bringing those together is a
        /// later concern.
        /// </summary>
        internal bool IsLatchReady
        {
            get
            {
                if (acceptedSampleCount < 2)
                {
                    return false;
                }

                Vector3 chord = EmitterPosition(acceptedSamples[acceptedSampleCount - 1])
                    - EmitterPosition(acceptedSamples[0]);
                if (!IsFinite(chord))
                {
                    return false;
                }

                float lengthSquared = chord.sqrMagnitude;
                return float.IsFinite(lengthSquared) && lengthSquared >= LatchChordMetres * LatchChordMetres;
            }
        }

        /// <summary>
        /// The stroke's first-candidate slash frame, per 19.1.5.1: the source
        /// slash plane, the begin and newest emitter points projected onto it,
        /// the travel axis (the begin sample's blade tip direction projected
        /// onto the plane), the span axis along the emitter chord, and that
        /// chord's length as the initial span. The two axes are reported as
        /// they come out; they are not orthogonalised.
        ///
        /// False when there are fewer than two accepted samples, when no plane
        /// can be derived, or when any projection or normalisation degenerates.
        /// Nothing is stored: like the plane, the frame lives and dies with the
        /// accepted samples.
        /// </summary>
        internal bool TryGetSlashFrameCandidate(
            out Plane plane,
            out Vector3 beginEmitter,
            out Vector3 latestEmitter,
            out Vector3 travelAxis,
            out Vector3 spanAxis,
            out float span)
        {
            plane = default;
            beginEmitter = default;
            latestEmitter = default;
            travelAxis = default;
            spanAxis = default;
            span = 0f;

            if (acceptedSampleCount < 2)
            {
                return false;
            }

            if (!TryGetSourceSlashPlaneCandidate(out Plane candidate))
            {
                return false;
            }

            EvaluatedBladePose begin = acceptedSamples[0];
            Vector3 projectedBegin = candidate.ClosestPointOnPlane(EmitterPosition(begin));
            Vector3 projectedLatest = candidate.ClosestPointOnPlane(EmitterPosition(acceptedSamples[acceptedSampleCount - 1]));
            if (!IsFinite(projectedBegin) || !IsFinite(projectedLatest))
            {
                return false;
            }

            if (!TryProjectOntoPlane(begin.BladeAxis, candidate.normal, out Vector3 travel))
            {
                return false;
            }

            Vector3 chord = projectedLatest - projectedBegin;
            if (!IsFinite(chord))
            {
                return false;
            }

            float chordLengthSquared = chord.sqrMagnitude;
            if (!float.IsFinite(chordLengthSquared) || chordLengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float chordLength = Mathf.Sqrt(chordLengthSquared);
            if (!float.IsFinite(chordLength) || !(chordLength > 0f))
            {
                return false;
            }

            Vector3 chordDirection = new Vector3(chord.x / chordLength, chord.y / chordLength, chord.z / chordLength);
            if (!IsFinite(chordDirection))
            {
                return false;
            }

            plane = candidate;
            beginEmitter = projectedBegin;
            latestEmitter = projectedLatest;
            travelAxis = travel;
            spanAxis = chordDirection;
            span = chordLength;
            return true;
        }

        // The emission control point, halfway along the blade. Derived from the
        // pose rather than stored alongside it.
        private Vector3 EmitterPosition(in EvaluatedBladePose pose)
        {
            return pose.KatanaPose.position + pose.BladeAxis * (bladeLength * EmissionControlPointRatio);
        }

        private static bool TryProjectOntoPlane(Vector3 direction, Vector3 normal, out Vector3 result)
        {
            result = default;

            Vector3 inPlane = direction - Vector3.Dot(direction, normal) * normal;
            if (!IsFinite(inPlane))
            {
                return false;
            }

            float lengthSquared = inPlane.sqrMagnitude;
            if (!float.IsFinite(lengthSquared) || lengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float length = Mathf.Sqrt(lengthSquared);
            result = new Vector3(inPlane.x / length, inPlane.y / length, inPlane.z / length);
            return IsFinite(result);
        }

        /// <summary>Number of slash waves this katana currently has alive.</summary>
        internal int WaveCount => waveStore.Count;

        /// <summary>
        /// Reads one live wave back by value. The store itself never leaves
        /// this component.
        /// </summary>
        internal bool TryGetWave(
            int index,
            out double latchedAt,
            out Plane sourceSlashPlane,
            out Vector3 waveOrigin,
            out Vector3 travelAxis,
            out Vector3 spanAxis,
            out float acceptedSpan,
            out Vector3 previousSegmentStart,
            out Vector3 previousSegmentEnd,
            out Vector3 currentSegmentStart,
            out Vector3 currentSegmentEnd)
        {
            return waveStore.TryGetWave(
                index,
                out latchedAt,
                out sourceSlashPlane,
                out waveOrigin,
                out travelAxis,
                out spanAxis,
                out acceptedSpan,
                out previousSegmentStart,
                out previousSegmentEnd,
                out currentSegmentStart,
                out currentSegmentEnd);
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
            // Waves that have reached their expiry go first, so a latch later
            // in this same update can use the capacity they free. How many are
            // left is also how the waves already flying are told apart from one
            // latched further down this same update: the ones counted here fly
            // and take the live guide, and anything published after does not.
            // No flag or id is needed.
            waveStore.RemoveExpired(sample.TimestampSeconds);
            int wavesAlreadyFlying = waveStore.Count;

            bool recorded = TryRecordGestureSample(sample, out EvaluatedBladePose current);
            TryPublishWave(sample.TimestampSeconds);

            // A pose the gate turned away is still a live guide, as long as it
            // could be shown and recorded. An unusable sample resets the stroke
            // and leaves the waves flying on their existing span.
            waveStore.Advance(
                sample.TimestampSeconds,
                wavesAlreadyFlying,
                recorded,
                recorded ? EmitterPosition(current) : Vector3.zero,
                recorded ? current.BladeAxis : Vector3.zero);

            return recorded;
        }

        private bool TryRecordGestureSample(in BladePoseSample sample, out EvaluatedBladePose evaluated)
        {
            // A sample that cannot be shown has already reset the stroke.
            if (!TryApplySample(sample, out evaluated))
            {
                return false;
            }

            // The history refuses a timestamp that does not move forward; it
            // clears itself, and the accepted samples go with it.
            if (!poseHistory.TryAppend(evaluated))
            {
                ResetStroke();
                return false;
            }

            EvaluateGesture(evaluated);
            return true;
        }

        // Every condition a wave needs, decided in one place at one instant:
        // the stroke has not latched yet, it has swept far enough, it has a
        // frame right now, and the store has room. A stroke with a frame gets
        // exactly one attempt, so a full store costs it its latch rather than
        // leaving it queued for the next free slot.
        private void TryPublishWave(double nowSeconds)
        {
            if (strokeLatchSpent || !IsLatchReady)
            {
                return;
            }

            if (!TryGetSlashFrameCandidate(
                    out Plane plane,
                    out Vector3 beginEmitter,
                    out Vector3 latestEmitter,
                    out Vector3 travelAxis,
                    out Vector3 spanAxis,
                    out float acceptedSpan))
            {
                // Not yet a frame; a later sample of the same stroke may still
                // give one.
                return;
            }

            strokeLatchSpent = true;
            waveStore.TryLatch(nowSeconds, plane, beginEmitter, latestEmitter, travelAxis, spanAxis, acceptedSpan);
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

        // Waves are deliberately not touched here: they outlive the stroke.
        private void ResetStroke()
        {
            poseHistory.Clear();
            acceptedSampleCount = 0;
            strokeLatchSpent = false;
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
        // subscription, so no re-entry flag is needed, it resets the history
        // and the stroke so neither is continued across the gap, and it ends
        // the waves this component owns.
        private void OnEnable()
        {
            Hide();
            ResetStroke();
            waveStore.Clear();
            Application.onBeforeRender += ApplyGripPoseForRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= ApplyGripPoseForRender;
            Hide();
            ResetStroke();
            waveStore.Clear();
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
