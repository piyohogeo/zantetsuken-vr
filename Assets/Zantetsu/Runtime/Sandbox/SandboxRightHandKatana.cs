using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.Core.Input;

[assembly: InternalsVisibleTo("Zantetsu.Core.EditModeTests")]

// The development save-path check. It has to exercise the real capture root,
// which the tests deliberately avoid, so it lives in an Editor assembly of its
// own rather than in the test one.
[assembly: InternalsVisibleTo("Zantetsu.Sandbox.Editor")]

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The sandbox's right-hand katana and everything a swing produces, for the
    /// shared Quest Link scene: it reads the right-hand controller's OpenXR
    /// grip pose, drives the katana transform through
    /// <see cref="BladePoseAdapter"/>, accepts slash gestures, and latches,
    /// advances and displays slash waves.
    ///
    /// Device input, pose conversion, display update, gesture acceptance and
    /// the slash waves deliberately live in one place; this assembly is the
    /// boundary that keeps XR device APIs out of the device-independent
    /// Zantetsu.Core. The aim (pointer) pose is never used. Nothing here acts
    /// on what a wave sweeps through yet: there is no hit query, narrowphase
    /// or cut.
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
    /// does not keep wiping the raw history. Too short a window, too little
    /// speed and too little displacement decide nothing -- they neither begin
    /// nor end a stroke. An impossible speed and every reset listed above drop
    /// both, but none of it hides the katana: a pose that can be shown is
    /// still shown.
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
    /// guide, whether or not the gate accepted it into the stroke, until a
    /// wave's span capture window closes and it steers by the guide it froze
    /// instead. A stroke gets one chance to latch -- if the store was full at
    /// that moment, that stroke does not get another when a slot later frees
    /// up. Only disabling the component ends the waves it owns, taking their
    /// display with them.
    ///
    /// The waves are shown through a fixed set of quads placed in the scene,
    /// one per slot the store has, synced at the end of each update -- never
    /// from Before Render -- from the store's current indices. They are
    /// display only: gameplay never reads a quad's transform, and nothing
    /// here builds or edits geometry.
    ///
    /// With a view reference assigned, a stroke only begins on an accepted
    /// sample whose blade axis faces the view forward closely enough, so a
    /// wind-up pointing away from where the player looks is not a begin.
    ///
    /// Live input can be handed over: with it turned off, nothing reads the
    /// controller and a replay feeds recorded samples through the same Update
    /// path instead. Handing it over either way starts the slash state over.
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

        // The normalised view forward the stroke's begin was checked against,
        // zero when it began without one. Cleared with the stroke; kept only
        // so a development readout can show what the check saw.
        private Vector3 strokeBeginViewForward;

        // Provisional Phase 0.52 gate values that stay fixed in code: the
        // sample window, and the speed above which a motion is not a swing.
        private const double GateMinimumWindowSeconds = 0.030;
        private const double GateMaximumWindowSeconds = 0.060;
        internal const float GateMaximumSpeed = 20f;

        // A derived vector shorter than this cannot be normalised into a
        // direction: a plane normal this short is a stroke with no swept area,
        // and a frame axis this short has no direction to report.
        private const float MinDerivedVectorLengthSquared = 1e-12f;

        // Provisional Phase 0.53 values, fixed in code like the gate's.
        // The emission control point sits halfway along the blade, apart from
        // the cut sample point at 70%.
        private const float EmissionControlPointRatio = 0.5f;

        [Tooltip("Katana visual root. Its local axes are the blade frame: +Z blade axis, -Y edge direction, +X side normal.")]
        [SerializeField] private Transform katana;

        [Tooltip("Head transform (the Main Camera) whose forward a stroke begin is checked against. Unassigned: no begin view check.")]
        [SerializeField] private Transform viewForwardReference;

        [Header("Provisional fixed grip-to-katana offset")]
        [SerializeField] private Vector3 offsetPosition = new Vector3(0f, 0f, 0.02f);
        [SerializeField] private Vector3 offsetEulerAngles = new Vector3(-15f, 0f, 0f);

        [Header("Blade")]
        [Tooltip("Blade length in metres, from the katana origin toward the tip.")]
        [SerializeField] private float bladeLength = 0.9f;

        [SerializeField] private bool drawGizmos = true;

        // First-candidate values that can be tuned while playing. They start at
        // the Phase 0.55 observation candidates and belong to this component
        // alone. A change is not carried back into what already happened --
        // the gate judges the next sample with it, the latch distance applies
        // to a stroke that has not latched, and the span capture timeout only
        // reaches waves latched afterwards, since each wave keeps its own.
        [Header("First candidate tuning")]
        [Tooltip("Minimum cut sample speed for an accepted sample, m/s.")]
        [SerializeField] private float minimumSpeed = 3.5f;

        [Tooltip("Minimum cut sample displacement across the gate window, m.")]
        [SerializeField] private float minimumDisplacement = 0.15f;

        [Tooltip("Edge lead score a motion must exceed to be accepted.")]
        [SerializeField] private float minimumEdgeLeadScore = 0.15f;

        [Tooltip("Edge lead score at or below which a rejected motion ends the stroke as its return half.")]
        [SerializeField] private float returnStrokeEdgeLeadScore = -0.15f;

        [Tooltip("Emitter chord a stroke must sweep before it latches, m.")]
        [SerializeField] private float latchChordMetres = 0.35f;

        [Tooltip("How long a newly latched wave's span keeps following the blade, s. Shorter than the wave's lifetime.")]
        [SerializeField] private float spanCaptureTimeoutSeconds = 0.25f;

        [Tooltip("A stroke only begins on a sample whose blade axis has at least this dot product with the view forward. Unused without a view reference.")]
        [SerializeField] private float beginBladeAxisViewDotMinimum = 0.5f;

        [Header("Slash wave display")]
        [Tooltip("One display slot per wave the store can hold, placed under a world-fixed root with identity scale.")]
        [SerializeField] private Transform[] waveVisuals = new Transform[SandboxSlashWaveStore.Capacity];
        // Assigned in the scene and only ever read from here; a slot left
        // unassigned simply shows nothing.

        // Provisional height of a wave quad in metres, fixed in code: display
        // only, and no part of the gameplay sweep.
        private const float WaveVisualHeight = 0.35f;

        // Whether Update and Before Render read the controller. Off only while
        // something else -- a replay -- feeds TryRecordSample instead.
        private bool liveInputEnabled = true;

        /// <summary>
        /// Whether this component reads the right-hand controller itself. A
        /// replay turns it off and feeds <see cref="TryRecordSample"/> in its
        /// place; nothing reads the controller while it is off, on Update or
        /// on Before Render. Every switch starts the slash state over -- the
        /// katana is hidden and the stroke, the history, the waves and their
        /// display are all cleared -- so samples from the controller and
        /// samples fed from elsewhere never meet in one stroke or one wave.
        /// </summary>
        internal bool LiveInputEnabled
        {
            get => liveInputEnabled;
            set
            {
                if (liveInputEnabled == value)
                {
                    return;
                }

                liveInputEnabled = value;
                ResetToKnownState();
            }
        }

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

        internal float MinimumSpeed => minimumSpeed;

        internal float MinimumDisplacement => minimumDisplacement;

        internal float MinimumEdgeLeadScore => minimumEdgeLeadScore;

        internal float ReturnStrokeEdgeLeadScore => returnStrokeEdgeLeadScore;

        internal float LatchChordMetres => latchChordMetres;

        internal float SpanCaptureTimeoutSeconds => spanCaptureTimeoutSeconds;

        internal float BeginBladeAxisViewDotMinimum => beginBladeAxisViewDotMinimum;

        /// <summary>
        /// The view forward the live path hands to the begin check: the
        /// reference's forward, or zero -- no check -- without a reference.
        /// </summary>
        internal Vector3 CurrentViewForward => viewForwardReference != null ? viewForwardReference.forward : Vector3.zero;

        /// <summary>
        /// The normalised view forward the stroke's begin was checked against,
        /// or zero with no stroke under way or a begin made without a view.
        /// </summary>
        internal Vector3 StrokeBeginViewForward => acceptedSampleCount > 0 ? strokeBeginViewForward : Vector3.zero;

        /// <summary>
        /// Sets the gate's minimum speed. False, changing nothing, unless it is
        /// finite, positive and no more than the fixed maximum speed.
        /// </summary>
        internal bool TrySetMinimumSpeed(float value)
        {
            if (!IsValidMinimumSpeed(value))
            {
                return false;
            }

            minimumSpeed = value;
            return true;
        }

        /// <summary>
        /// Sets the gate's minimum displacement. False, changing nothing,
        /// unless it is finite and positive.
        /// </summary>
        internal bool TrySetMinimumDisplacement(float value)
        {
            if (!IsFinitePositive(value))
            {
                return false;
            }

            minimumDisplacement = value;
            return true;
        }

        /// <summary>
        /// Sets the edge lead score a motion must exceed. False, changing
        /// nothing, unless it is finite and within [-1, 1].
        /// </summary>
        internal bool TrySetMinimumEdgeLeadScore(float value)
        {
            if (!IsWithinUnitRange(value))
            {
                return false;
            }

            minimumEdgeLeadScore = value;
            return true;
        }

        /// <summary>
        /// Sets the score at or below which a rejected motion is a return.
        /// False, changing nothing, unless it is finite and within [-1, 1].
        /// </summary>
        internal bool TrySetReturnStrokeEdgeLeadScore(float value)
        {
            if (!IsWithinUnitRange(value))
            {
                return false;
            }

            returnStrokeEdgeLeadScore = value;
            return true;
        }

        /// <summary>
        /// Sets the latch distance. False, changing nothing, unless it is
        /// finite and positive.
        /// </summary>
        internal bool TrySetLatchChordMetres(float value)
        {
            if (!IsFinitePositive(value))
            {
                return false;
            }

            latchChordMetres = value;
            return true;
        }

        /// <summary>
        /// Sets the span capture timeout for waves latched from now on. False,
        /// changing nothing, unless it is finite, positive and shorter than the
        /// wave lifetime -- the store refuses to latch with anything else.
        /// </summary>
        internal bool TrySetSpanCaptureTimeoutSeconds(float value)
        {
            if (!IsValidSpanCaptureTimeout(value))
            {
                return false;
            }

            spanCaptureTimeoutSeconds = value;
            return true;
        }

        /// <summary>
        /// Sets the dot product a begin's blade axis needs with the view
        /// forward. False, changing nothing, unless it is finite and within
        /// [-1, 1].
        /// </summary>
        internal bool TrySetBeginBladeAxisViewDotMinimum(float value)
        {
            if (!IsWithinUnitRange(value))
            {
                return false;
            }

            beginBladeAxisViewDotMinimum = value;
            return true;
        }

        private static bool IsFinitePositive(float value)
        {
            return float.IsFinite(value) && value > 0f;
        }

        private static bool IsValidMinimumSpeed(float value)
        {
            return IsFinitePositive(value) && value <= GateMaximumSpeed;
        }

        private static bool IsWithinUnitRange(float value)
        {
            return float.IsFinite(value) && value >= -1f && value <= 1f;
        }

        private static bool IsValidSpanCaptureTimeout(float value)
        {
            return IsFinitePositive(value) && value < SandboxSlashWaveStore.WaveLifetimeSeconds;
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
        /// One accepted sample of the stroke under way, oldest first, or false
        /// when the index is outside them. Read-only, for development readouts.
        /// </summary>
        internal bool TryGetAcceptedSample(int index, out EvaluatedBladePose sample)
        {
            if (index < 0 || index >= acceptedSampleCount)
            {
                sample = default;
                return false;
            }

            sample = acceptedSamples[index];
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
                if (acceptedSampleCount < 2 || !IsFinitePositive(latchChordMetres))
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
                return float.IsFinite(lengthSquared) && lengthSquared >= latchChordMetres * latchChordMetres;
            }
        }

        /// <summary>
        /// The stroke's first-candidate slash frame, per 19.1.5.1: the source
        /// slash plane, the begin and newest emitter points projected onto it,
        /// the travel axis (the begin sample's blade tip direction projected
        /// onto the plane), a span axis at 150 degrees to travel on the side
        /// indicated by the emitter chord, and the chord's length as the
        /// initial span. The initial endpoint is no longer the latest emitter.
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
            float side = Vector3.Dot(candidate.normal, Vector3.Cross(travel, chordDirection));
            if (!IsFinite(chordDirection) || !float.IsFinite(side) || side == 0f)
            {
                return false;
            }

            // Adopted fixed-angle frame: cos(150) T + sign(side) sin(150) (N x T).
            // Only the axis changes; chord length, guide and Close rules do not.
            Vector3 fixedSpan = -0.8660254037844386f * travel
                + (side > 0f ? 0.5f : -0.5f) * Vector3.Cross(candidate.normal, travel);
            if (!TryProjectOntoPlane(fixedSpan, candidate.normal, out fixedSpan))
            {
                return false;
            }

            plane = candidate;
            beginEmitter = projectedBegin;
            latestEmitter = projectedLatest;
            travelAxis = travel;
            spanAxis = fixedSpan;
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

        /// <summary>
        /// What one wave's last candidate evaluation saw: which guide, the
        /// intersection terms, and whether they were usable or widened the
        /// span. Observation only, as 19.1.12 asks for; the update had already
        /// decided before these were written.
        /// </summary>
        internal bool TryGetWaveCandidate(
            int index,
            out double candidateAt,
            out bool evaluated,
            out bool fromFrozenGuide,
            out Vector3 guideOrigin,
            out Vector3 guideDirection,
            out float rawSpan,
            out float q,
            out float denominator,
            out bool termsFinite,
            out bool usable,
            out bool widenedSpan)
        {
            return waveStore.TryGetWaveCandidate(
                index, out candidateAt, out evaluated, out fromFrozenGuide, out guideOrigin, out guideDirection,
                out rawSpan, out q, out denominator, out termsFinite, out usable, out widenedSpan);
        }

        /// <summary>When a wave closed and the guide it froze; false while open.</summary>
        internal bool TryGetWaveSpanClose(
            int index,
            out double spanClosedAt,
            out Vector3 frozenGuideOrigin,
            out Vector3 frozenGuideDirection)
        {
            return waveStore.TryGetWaveSpanClose(index, out spanClosedAt, out frozenGuideOrigin, out frozenGuideDirection);
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
            return TryRecordSample(sample, CurrentViewForward);
        }

        /// <summary>
        /// A development capture of the input, or null. Set by the recorder, it
        /// sees every update through <see cref="TryRecordSample"/> -- live and
        /// replayed alike, since that is the only path that appends -- and
        /// a reset ends the capture before the calculation loses its history.
        /// </summary>
        internal SandboxSlashCapture Capture { get; set; }

        /// <summary>
        /// Puts the calculation back to a known initial state: no pose history,
        /// no stroke in progress, no live wave and nothing shown. This is what
        /// the enable and disable boundaries already do, offered to the
        /// development capture so that a capture started midway does not begin
        /// from a state built by input it never saved.
        /// <para>
        /// Waves on screen disappear, because they are the state being
        /// discarded.
        /// </para>
        /// </summary>
        internal void ResetToKnownState()
        {
            Capture?.Stop("the calculation was reset; capturing ended before the reset");
            Capture = null;
            Hide();
            ResetStroke();
            waveStore.Clear();
            HideAllWaveVisuals();
        }

        /// <summary>
        /// Applies a saved run's tunable values, and reports anything that is
        /// not tunable and does not already match. A recomputation that quietly
        /// used its own blade length or wave speed would not be recomputing
        /// that run, so the mismatch is named rather than tolerated.
        /// </summary>
        internal bool TryApplyCaptureConditions(in SandboxSlashCapture.Conditions saved, out string mismatch)
        {
            mismatch = string.Empty;
            var refused = new System.Collections.Generic.List<string>();
            if (!TrySetMinimumSpeed(saved.MinimumSpeed))
            {
                refused.Add("minimum speed");
            }

            if (!TrySetMinimumDisplacement(saved.MinimumDisplacement))
            {
                refused.Add("minimum displacement");
            }

            if (!TrySetMinimumEdgeLeadScore(saved.MinimumEdgeLeadScore))
            {
                refused.Add("minimum edge lead score");
            }

            if (!TrySetReturnStrokeEdgeLeadScore(saved.ReturnStrokeEdgeLeadScore))
            {
                refused.Add("return stroke edge lead score");
            }

            if (!TrySetLatchChordMetres(saved.LatchChordMetres))
            {
                refused.Add("latch chord metres");
            }

            if (!TrySetSpanCaptureTimeoutSeconds(saved.SpanCaptureTimeoutSeconds))
            {
                refused.Add("span capture timeout seconds");
            }

            if (!TrySetBeginBladeAxisViewDotMinimum(saved.BeginBladeAxisViewDotMinimum))
            {
                refused.Add("begin blade axis view dot minimum");
            }

            // Not tunable at run time: they have to match already.
            SandboxSlashCapture.Conditions mine = CaptureConditions;
            AddIfDifferent(refused, "blade length", saved.BladeLength, mine.BladeLength);
            AddIfDifferent(refused, "grip offset position x", saved.GripOffsetPosition.x, mine.GripOffsetPosition.x);
            AddIfDifferent(refused, "grip offset position y", saved.GripOffsetPosition.y, mine.GripOffsetPosition.y);
            AddIfDifferent(refused, "grip offset position z", saved.GripOffsetPosition.z, mine.GripOffsetPosition.z);
            AddIfDifferent(refused, "grip offset rotation x", saved.GripOffsetRotation.x, mine.GripOffsetRotation.x);
            AddIfDifferent(refused, "grip offset rotation y", saved.GripOffsetRotation.y, mine.GripOffsetRotation.y);
            AddIfDifferent(refused, "grip offset rotation z", saved.GripOffsetRotation.z, mine.GripOffsetRotation.z);
            AddIfDifferent(refused, "grip offset rotation w", saved.GripOffsetRotation.w, mine.GripOffsetRotation.w);
            AddIfDifferent(
                refused, "emission control point ratio", saved.EmissionControlPointRatio,
                mine.EmissionControlPointRatio);
            AddIfDifferent(refused, "cut sample ratio", saved.CutSampleRatio, mine.CutSampleRatio);
            AddIfDifferent(refused, "wave speed", saved.WaveSpeed, mine.WaveSpeed);
            AddIfDifferent(refused, "wave lifetime seconds", saved.WaveLifetimeSeconds, mine.WaveLifetimeSeconds);
            AddIfDifferent(
                refused, "near parallel denominator", saved.NearParallelDenominator, mine.NearParallelDenominator);
            if (saved.WaveCapacity != mine.WaveCapacity)
            {
                refused.Add("live wave capacity");
            }

            if (refused.Count == 0)
            {
                return true;
            }

            mismatch = string.Join(", ", refused);
            return false;
        }

        private static void AddIfDifferent(
            System.Collections.Generic.ICollection<string> refused, string name, float saved, float mine)
        {
            // A saved value is written so it round trips, so anything other
            // than equality here is a real difference.
            if (!saved.Equals(mine))
            {
                refused.Add(name + " (saved " + saved.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", this katana " + mine.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            }
        }

        /// <summary>The values a capture has to save to be recomputable.</summary>
        internal SandboxSlashCapture.Conditions CaptureConditions => new SandboxSlashCapture.Conditions
        {
            MinimumSpeed = minimumSpeed,
            MinimumDisplacement = minimumDisplacement,
            MinimumEdgeLeadScore = minimumEdgeLeadScore,
            ReturnStrokeEdgeLeadScore = returnStrokeEdgeLeadScore,
            LatchChordMetres = latchChordMetres,
            SpanCaptureTimeoutSeconds = spanCaptureTimeoutSeconds,
            BeginBladeAxisViewDotMinimum = beginBladeAxisViewDotMinimum,
            BladeLength = bladeLength,
            GripOffsetPosition = offsetPosition,
            GripOffsetRotation = Quaternion.Euler(offsetEulerAngles),
            EmissionControlPointRatio = EmissionControlPointRatio,
            CutSampleRatio = CutSampleRatio,
            WaveSpeed = SandboxSlashWaveStore.WaveSpeed,
            WaveLifetimeSeconds = SandboxSlashWaveStore.WaveLifetimeSeconds,
            NearParallelDenominator = SandboxSlashWaveStore.NearParallelDenominatorThreshold,
            WaveCapacity = SandboxSlashWaveStore.Capacity,
        };

        /// <summary>Explicit view forward for live/replayed input; zero skips the begin view check.</summary>
        internal bool TryRecordSample(in BladePoseSample sample, Vector3 viewForward)
        {
            // Waves that have reached their expiry go first, so a latch later
            // in this same update can use the capacity they free. How many are
            // left is also how the waves already flying are told apart from one
            // latched further down this same update: the ones counted here fly
            // and take the live guide, and anything published after does not.
            // No flag or id is needed.
            waveStore.RemoveExpired(sample.TimestampSeconds);
            int wavesAlreadyFlying = waveStore.Count;

            bool recorded = TryRecordGestureSample(sample, viewForward, out EvaluatedBladePose current);
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

            // Last, so a wave latched or expired in this update is shown or
            // hidden in it too. Slots follow the store's current indices, so
            // there is no second bookkeeping to fall out of step after the
            // store compacts.
            SyncWaveVisuals();

            // After the update, so what the capture holds beside the input is
            // what this update produced from it. Rejected input is captured
            // too: it still steered the guide, or reset the stroke.
            Capture?.Append(sample, viewForward, recorded, acceptedSampleCount, this);

            return recorded;
        }

        private void SyncWaveVisuals()
        {
            if (waveVisuals == null)
            {
                return;
            }

            int liveWaves = waveStore.Count;
            for (int i = 0; i < waveVisuals.Length; i++)
            {
                Transform visual = waveVisuals[i];
                if (visual == null)
                {
                    continue;
                }

                if (i >= liveWaves
                    || !TryGetWaveVisualPlacement(i, out Vector3 center, out Quaternion rotation, out Vector3 scale))
                {
                    HideVisual(visual);
                    continue;
                }

                visual.SetPositionAndRotation(center, rotation);
                visual.localScale = scale;
                if (!visual.gameObject.activeSelf)
                {
                    visual.gameObject.SetActive(true);
                }
            }
        }

        // The quad that shows one wave: centred on its current segment, lying
        // in its plane, as wide as the accepted span. A wave whose plane,
        // axes or segment cannot give that is simply not shown -- nothing is
        // clamped or substituted, and this placement is never what gameplay
        // reads.
        private bool TryGetWaveVisualPlacement(int index, out Vector3 center, out Quaternion rotation, out Vector3 scale)
        {
            center = default;
            rotation = Quaternion.identity;
            scale = Vector3.one;

            if (!waveStore.TryGetWave(index, out _, out Plane plane, out _, out _, out Vector3 spanAxis,
                    out float acceptedSpan, out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd))
            {
                return false;
            }

            Vector3 normal = plane.normal;
            if (!IsFinite(normal) || !IsFinite(spanAxis) || !IsFinite(segmentStart) || !IsFinite(segmentEnd)
                || !float.IsFinite(acceptedSpan))
            {
                return false;
            }

            Vector3 inPlaneUp = Vector3.Cross(normal, spanAxis);
            if (!IsFinite(inPlaneUp))
            {
                return false;
            }

            float upLengthSquared = inPlaneUp.sqrMagnitude;
            if (!float.IsFinite(upLengthSquared) || upLengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float normalLengthSquared = normal.sqrMagnitude;
            if (!float.IsFinite(normalLengthSquared) || normalLengthSquared <= MinDerivedVectorLengthSquared)
            {
                return false;
            }

            float upLength = Mathf.Sqrt(upLengthSquared);
            inPlaneUp = new Vector3(inPlaneUp.x / upLength, inPlaneUp.y / upLength, inPlaneUp.z / upLength);
            if (!IsFinite(inPlaneUp))
            {
                return false;
            }

            center = (segmentStart + segmentEnd) * 0.5f;
            if (!IsFinite(center))
            {
                return false;
            }

            // Local Z is the plane normal and local Y the in-plane up, which
            // leaves local X on the span axis.
            rotation = Quaternion.LookRotation(normal, inPlaneUp);
            scale = new Vector3(acceptedSpan, WaveVisualHeight, 1f);
            return true;
        }

        private void HideAllWaveVisuals()
        {
            if (waveVisuals == null)
            {
                return;
            }

            for (int i = 0; i < waveVisuals.Length; i++)
            {
                if (waveVisuals[i] != null)
                {
                    HideVisual(waveVisuals[i]);
                }
            }
        }

        private static void HideVisual(Transform visual)
        {
            if (visual.gameObject.activeSelf)
            {
                visual.gameObject.SetActive(false);
            }
        }

        private bool TryRecordGestureSample(in BladePoseSample sample, Vector3 viewForward, out EvaluatedBladePose evaluated)
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

            EvaluateGesture(evaluated, viewForward);
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
                    out _,
                    out Vector3 travelAxis,
                    out Vector3 spanAxis,
                    out float acceptedSpan))
            {
                // Not yet a frame; a later sample of the same stroke may still
                // give one.
                return;
            }

            strokeLatchSpent = true;
            waveStore.TryLatch(
                nowSeconds, plane, beginEmitter, beginEmitter + spanAxis * acceptedSpan,
                travelAxis, spanAxis, acceptedSpan, spanCaptureTimeoutSeconds);
        }

        // Update boundary only. Before Render never reaches here.
        private void EvaluateGesture(in EvaluatedBladePose current, Vector3 viewForward)
        {
            // Values left invalid in the Inspector decide nothing, rather than
            // throw from the settings constructor on every update.
            if (!IsValidMinimumSpeed(minimumSpeed)
                || !IsFinitePositive(minimumDisplacement)
                || !IsWithinUnitRange(minimumEdgeLeadScore)
                || !IsWithinUnitRange(returnStrokeEdgeLeadScore)
                || !IsWithinUnitRange(beginBladeAxisViewDotMinimum))
            {
                return;
            }

            if (!poseHistory.TryEvaluateLatest(GateMinimumWindowSeconds, GateMaximumWindowSeconds, out BladeMotionSample motion))
            {
                // Not enough history to span the window yet: decide nothing.
                return;
            }

            // Built from the current values each time; a value type, so this
            // allocates nothing.
            BladeEdgeGateSettings gateSettings = new BladeEdgeGateSettings(
                GateMinimumWindowSeconds,
                GateMaximumWindowSeconds,
                minimumSpeed,
                GateMaximumSpeed,
                minimumDisplacement,
                minimumEdgeLeadScore);
            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(motion, gateSettings);
            if (decision.IsAccepted)
            {
                AppendAcceptedSample(current, viewForward);
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
                && motion.EdgeLeadScore <= returnStrokeEdgeLeadScore)
            {
                ResetStroke();
            }
        }

        private void AppendAcceptedSample(in EvaluatedBladePose pose, Vector3 viewForward)
        {
            // Only a sample that would begin the stroke is checked against the
            // view. One that fails is just not taken as the begin: this is a
            // begin candidate update, not a stroke split -- nothing is reset,
            // the raw history stays, and the next accepted sample of the same
            // motion is the next candidate. Once begun, the view is not asked.
            if (acceptedSampleCount == 0)
            {
                if (!PassesBeginViewCheck(pose, viewForward, out Vector3 checkedView))
                {
                    return;
                }

                strokeBeginViewForward = checkedView;
            }

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

        // True when there is no usable view forward -- no check -- or when the
        // blade axis has at least the minimum dot product with it.
        private bool PassesBeginViewCheck(in EvaluatedBladePose pose, Vector3 viewForward, out Vector3 normalizedView)
        {
            normalizedView = Vector3.zero;

            float lengthSquared = viewForward.sqrMagnitude;
            if (!IsFinite(viewForward) || !float.IsFinite(lengthSquared) || lengthSquared <= MinDerivedVectorLengthSquared)
            {
                return true;
            }

            float length = Mathf.Sqrt(lengthSquared);
            normalizedView = new Vector3(viewForward.x / length, viewForward.y / length, viewForward.z / length);
            return Vector3.Dot(pose.BladeAxis, normalizedView) >= beginBladeAxisViewDotMinimum;
        }

        // Waves are deliberately not touched here: they outlive the stroke.
        private void ResetStroke()
        {
            poseHistory.Clear();
            acceptedSampleCount = 0;
            strokeLatchSpent = false;
            strokeBeginViewForward = Vector3.zero;
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
        internal static BladePoseSample ReadRightHandGripSample()
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
            ResetToKnownState();
            Application.onBeforeRender += ApplyGripPoseForRender;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= ApplyGripPoseForRender;
            ResetToKnownState();
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
            if (!liveInputEnabled)
            {
                return;
            }

            TryRecordSample(ReadRightHandGripSample());
        }

        private void ApplyGripPoseForRender()
        {
            // A replayed pose must not be overwritten by the live one on its
            // way to the screen.
            if (!liveInputEnabled)
            {
                return;
            }

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
