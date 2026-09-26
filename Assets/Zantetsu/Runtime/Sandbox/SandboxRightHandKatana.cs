using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;

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
        internal const float CutSampleRatio = SlashBlade.CutSampleRatio;

        // The product slash core this component feeds (Phase 4.50): the gesture, the latch and the waves are all its.
        // This component reads the device, shows the katana and the waves, and holds the development tuning.
        private SlashWaveCore core;
        private float appliedLatchChord = float.NaN;
        private float appliedCaptureTimeout = float.NaN;

        // A derived vector shorter than this has no direction to show a wave quad along.
        private const float MinDerivedVectorLengthSquared = 1e-12f;

        /// <summary>The fixed gate ceiling, the core's.</summary>
        internal const float GateMaximumSpeed = SlashWaveCore.GateMaximumSpeed;

        [Tooltip("Katana visual root. Its local axes are the blade frame: +Z blade axis, -Y edge direction, +X side normal.")]
        [SerializeField] private Transform katana;

        [Tooltip("Head transform (the Main Camera) whose forward a stroke begin is checked against. Unassigned: no begin view check.")]
        [SerializeField] private Transform viewForwardReference;

        [Tooltip("The XR tracking space (the XR Origin's Camera Offset). The gesture is judged in it, so artificial movement of the origin is no part of a swing; the katana is shown, and a wave is latched, where it places them now. Unassigned: tracking space is world.")]
        [SerializeField] private Transform trackingSpace;

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
        [SerializeField] private Transform[] waveVisuals = new Transform[SlashWaveCore.Capacity];
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

        /// <summary>
        /// The product core, made on first use and brought up to this component's current values: the blade and the
        /// gesture values each time, the latch and close estimators only when their value changed -- a new instance,
        /// so a wave already published keeps the one it latched with.
        /// </summary>
        internal SlashWaveCore Core
        {
            get
            {
                SyncCore();
                return core;
            }
        }

        private void SyncCore()
        {
            var blade = new SlashBlade(GripToKatanaOffset, bladeLength);
            if (core == null)
            {
                core = new SlashWaveCore(in blade);
            }
            else
            {
                core.Blade = blade;
            }

            core.Gesture = new SlashGestureSettings
            {
                MinimumSpeed = minimumSpeed,
                MinimumDisplacement = minimumDisplacement,
                MinimumEdgeLeadScore = minimumEdgeLeadScore,
                ReturnStrokeEdgeLeadScore = returnStrokeEdgeLeadScore,
                BeginBladeAxisViewDotMinimum = beginBladeAxisViewDotMinimum,
            };

            // A value that cannot make its estimator -- a latch distance or a capture timeout that is not finite and
            // positive, as only the Inspector can give -- latches nothing, as it always did.
            if (!latchChordMetres.Equals(appliedLatchChord) || !spanCaptureTimeoutSeconds.Equals(appliedCaptureTimeout))
            {
                bool usable = EmitterChordLatch.IsUsable(latchChordMetres) && IsValidSpanCaptureTimeout(spanCaptureTimeoutSeconds);
                core.LatchEstimator = usable
                    ? new EmitterChordLatch(latchChordMetres)
                    : (ISlashLatchEstimator)NeverReadyLatch.Instance;
                if (IsValidSpanCaptureTimeout(spanCaptureTimeoutSeconds))
                {
                    core.SpanCloseEstimator = new CaptureTimeoutSpanClose(spanCaptureTimeoutSeconds);
                }

                appliedLatchChord = latchChordMetres;
                appliedCaptureTimeout = spanCaptureTimeoutSeconds;
            }
        }

        // A latch distance that is not usable latches nothing, as it always did.
        private sealed class NeverReadyLatch : ISlashLatchEstimator
        {
            internal static readonly NeverReadyLatch Instance = new NeverReadyLatch();

            public bool IsLatchReady(System.ReadOnlySpan<EvaluatedBladePose> accepted, in SlashBlade blade) => false;
        }

        /// <summary>The wave display slots, read only, for checks that compare what is shown with the live waves.</summary>
        internal System.Collections.Generic.IReadOnlyList<Transform> WaveVisualsForReadout => waveVisuals;

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
        /// reference's forward in tracking space, the space the gesture is
        /// judged in, or zero -- no check -- without a reference.
        /// </summary>
        internal Vector3 CurrentViewForward => viewForwardReference != null
            ? Quaternion.Inverse(TrackingSpacePose.rotation) * viewForwardReference.forward
            : Vector3.zero;

        /// <summary>
        /// Where the tracking space stands in the world now: its position and
        /// rotation, or identity without one. Scale is not applied; the XR
        /// origin is at unit scale.
        /// </summary>
        internal Pose TrackingSpacePose => trackingSpace != null
            ? new Pose(trackingSpace.position, trackingSpace.rotation)
            : new Pose(Vector3.zero, Quaternion.identity);

        /// <summary>A world direction expressed in tracking space, for readouts beside the gesture's values.</summary>
        internal Vector3 TrackingDirection(Vector3 worldDirection)
        {
            return Quaternion.Inverse(TrackingSpacePose.rotation) * worldDirection;
        }

        /// <summary>
        /// The normalised view forward the stroke's begin was checked against,
        /// or zero with no stroke under way or a begin made without a view.
        /// </summary>
        internal Vector3 StrokeBeginViewForward => Core.StrokeBeginViewForward;

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
        /// changing nothing, unless it is finite and positive. It may be as long
        /// as the wave lifetime or longer: such a wave expires with its span
        /// still open (DESIGN 19.1.1).
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
            return SlashGestureSettings.IsValidMinimumSpeed(value);
        }

        private static bool IsWithinUnitRange(float value)
        {
            return float.IsFinite(value) && value >= -1f && value <= 1f;
        }

        private static bool IsValidSpanCaptureTimeout(float value)
        {
            return IsFinitePositive(value);
        }

        /// <summary>
        /// Number of poses currently in the core's history.
        /// </summary>
        internal int RecordedPoseCount => Core.RecordedPoseCount;

        /// <summary>
        /// Number of accepted samples in the stroke under way. Zero means no stroke is under way.
        /// </summary>
        internal int AcceptedSampleCount => Core.AcceptedSampleCount;

        /// <summary>The stroke's begin sample, or false when no stroke is under way.</summary>
        internal bool TryGetStrokeBeginSample(out EvaluatedBladePose sample)
        {
            return TryGetAcceptedSample(0, out sample);
        }

        /// <summary>One accepted sample of the stroke under way, oldest first. Read-only, for readouts.</summary>
        internal bool TryGetAcceptedSample(int index, out EvaluatedBladePose sample)
        {
            SlashWaveCore slash = Core;
            if (index < 0 || index >= slash.AcceptedSampleCount)
            {
                sample = default;
                return false;
            }

            sample = slash.AcceptedSamples[index];
            return true;
        }

        /// <summary>The stroke's source slash plane candidate, derived on demand by the core (tracking space).</summary>
        internal bool TryGetSourceSlashPlaneCandidate(out Plane plane)
        {
            return Core.TryGetSourceSlashPlaneCandidate(out plane);
        }

        private static bool IsFinite(Vector3 v)
        {
            return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        }

        /// <summary>Whether the stroke has swept far enough to latch, by the core's current latch estimator.</summary>
        internal bool IsLatchReady => Core.IsLatchReady;

        /// <summary>
        /// The stroke's first-candidate slash frame (19.1.5.1), from the core's current frame estimator, in tracking
        /// space: the plane, the begin and newest emitter points on it, the travel and span axes, and the initial span.
        /// </summary>
        internal bool TryGetSlashFrameCandidate(
            out Plane plane,
            out Vector3 beginEmitter,
            out Vector3 latestEmitter,
            out Vector3 travelAxis,
            out Vector3 spanAxis,
            out float span)
        {
            bool ok = Core.TryGetSlashFrameCandidate(out SlashFrame frame);
            plane = frame.SourceSlashPlane;
            beginEmitter = frame.BeginEmitter;
            latestEmitter = frame.LatestEmitter;
            travelAxis = frame.TravelAxis;
            spanAxis = frame.SpanAxis;
            span = frame.InitialSpan;
            return ok;
        }

        /// <summary>
        /// <summary>Number of slash waves this katana currently has alive: the core's.</summary>
        internal int WaveCount => Core.WaveCount;

        /// <summary>The SlashId of one live wave, or 0 outside them.</summary>
        internal long SlashIdAt(int index) => Core.SlashIdAt(index);

        /// <summary>Reads one live wave back by value, from the core.</summary>
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
            return Core.TryGetWave(
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

        /// <summary>What one wave's last candidate evaluation saw, from the core (observation only, 19.1.12).</summary>
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
            return Core.TryGetWaveCandidate(
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
            return Core.TryGetWaveSpanClose(index, out spanClosedAt, out frozenGuideOrigin, out frozenGuideDirection);
        }

        /// <summary>The single provisional fixed grip-to-katana offset.</summary>
        internal Pose GripToKatanaOffset => new Pose(offsetPosition, Quaternion.Euler(offsetEulerAngles));

        /// <summary>Blade-local coordinate system of the katana visual root: the core blade's.</summary>
        internal BladeFrame BladeFrame => new SlashBlade(GripToKatanaOffset, bladeLength).Frame;

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
            Core.Reset();
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
            EmissionControlPointRatio = SlashBlade.EmissionControlPointRatio,
            CutSampleRatio = CutSampleRatio,
            WaveSpeed = Core.Flight.Speed,
            WaveLifetimeSeconds = Core.Flight.LifetimeSeconds,
            NearParallelDenominator = GuideRaySpanCandidate.DefaultNearParallelDenominator,
            WaveCapacity = SlashWaveCore.Capacity,
        };

        /// <summary>
        /// Explicit view forward for live/replayed input; zero skips the begin
        /// view check. The sample and the view are in tracking space -- the
        /// device's own space -- so a swing is judged without the player's
        /// artificial movement; what is shown and latched is placed in the
        /// world by the tracking space as it stands.
        /// </summary>
        internal bool TryRecordSample(in BladePoseSample sample, Vector3 viewForward)
        {
            // The product core does the whole update (DESIGN 19.1.5.1): expiry first, the gesture, one latch at most,
            // then the flying waves with the blade as it is now as their guide. This component only feeds it and shows
            // what it produced. Without a katana to show the sample is no pose at all to the gesture, as before.
            SlashWaveCore slash = Core;
            BladePoseSample input = katana != null
                ? sample
                : new BladePoseSample(sample.FrameId, sample.TimestampSeconds, sample.GripPosition, sample.GripRotation,
                    BladeTrackingState.None);
            Pose? space = trackingSpace != null ? TrackingSpacePose : (Pose?)null;
            SlashInputOutcome outcome = slash.Update(in input, viewForward, space, out EvaluatedBladePose current);
            if (outcome == SlashInputOutcome.PoseUnusable)
            {
                // A pose that cannot be shown is hidden; a missing katana or blade has nothing to hide.
                if (katana != null && slash.Blade.IsValid)
                {
                    Hide();
                }
            }
            else
            {
                Show(current);
            }

            bool recorded = outcome == SlashInputOutcome.Recorded;

            // Last, so a wave latched or expired in this update is shown or hidden in it too.
            SyncWaveVisuals();

            // After the update, so what the capture holds beside the input is what this update produced from it.
            Capture?.Append(sample, viewForward, recorded, slash.AcceptedSampleCount, this);
            return recorded;
        }

        private void SyncWaveVisuals()
        {
            if (waveVisuals == null)
            {
                return;
            }

            int liveWaves = Core.WaveCount;
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

            if (!Core.TryGetWave(index, out _, out Plane plane, out _, out _, out Vector3 spanAxis,
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

        // Shows the pose where the tracking space places it now; the evaluated pose itself stays in tracking space.
        private void Show(in EvaluatedBladePose evaluated)
        {
            Pose space = TrackingSpacePose;
            katana.SetPositionAndRotation(
                space.position + (space.rotation * evaluated.KatanaPose.position),
                space.rotation * evaluated.KatanaPose.rotation);
            katana.gameObject.SetActive(true);
        }

        private bool TryApplySample(in BladePoseSample sample, out EvaluatedBladePose evaluated)
        {
            SlashWaveCore slash = Core;
            if (katana == null || !slash.Blade.IsValid)
            {
                evaluated = default;
                slash.ResetStroke();
                return false;
            }

            if (!slash.TryEvaluatePose(sample, out evaluated))
            {
                Hide();
                slash.ResetStroke();
                return false;
            }

            Show(evaluated);
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
