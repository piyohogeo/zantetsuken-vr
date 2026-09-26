using System;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Slash
{
    /// <summary>The gesture values of DESIGN 19.1.11 a sample is judged with. Fixed window and ceiling are constants.</summary>
    public struct SlashGestureSettings
    {
        /// <summary>The adopted values (Phase 0.55 observation candidates).</summary>
        public static SlashGestureSettings Adopted => new SlashGestureSettings
        {
            MinimumSpeed = 3.5f,
            MinimumDisplacement = 0.15f,
            MinimumEdgeLeadScore = 0.15f,
            ReturnStrokeEdgeLeadScore = -0.15f,
            BeginBladeAxisViewDotMinimum = 0.5f,
        };

        /// <summary>Minimum cut sample speed for an accepted sample, m/s; positive and no more than the ceiling.</summary>
        public float MinimumSpeed;

        /// <summary>Minimum cut sample displacement across the gate window, m.</summary>
        public float MinimumDisplacement;

        /// <summary>Edge lead score a motion must exceed to be accepted, in [-1, 1].</summary>
        public float MinimumEdgeLeadScore;

        /// <summary>Edge lead score at or below which a rejected motion ends the stroke as its return half.</summary>
        public float ReturnStrokeEdgeLeadScore;

        /// <summary>A stroke begins only on a sample whose blade axis has at least this dot with the view forward.</summary>
        public float BeginBladeAxisViewDotMinimum;

        public static bool IsValidMinimumSpeed(float value) => float.IsFinite(value) && value > 0f && value <= SlashWaveCore.GateMaximumSpeed;

        public static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;

        public static bool IsWithinUnitRange(float value) => float.IsFinite(value) && value >= -1f && value <= 1f;

        public bool IsValid =>
            IsValidMinimumSpeed(MinimumSpeed) && IsFinitePositive(MinimumDisplacement)
            && IsWithinUnitRange(MinimumEdgeLeadScore) && IsWithinUnitRange(ReturnStrokeEdgeLeadScore)
            && IsWithinUnitRange(BeginBladeAxisViewDotMinimum);
    }

    /// <summary>What one input sample was to the gesture.</summary>
    public enum SlashInputOutcome
    {
        /// <summary>Not a usable pose (untracked, not finite, or no usable blade). The stroke was reset.</summary>
        PoseUnusable = 0,

        /// <summary>A usable pose to show, but the history refused it (its time did not move forward). The stroke was reset.</summary>
        HistoryRefused = 1,

        /// <summary>A usable pose, taken into the gesture and offered to the flying waves as their live guide.</summary>
        Recorded = 2,
    }

    /// <summary>The flight of a wave, fixed at its latch (DESIGN 19.1.6): a finite positive speed and lifetime.</summary>
    public readonly struct SlashWaveFlight
    {
        /// <summary>The adopted values: 12 m/s for 1.5 s.</summary>
        public static SlashWaveFlight Adopted => new SlashWaveFlight(12f, 1.5f);

        public SlashWaveFlight(float speed, float lifetimeSeconds)
        {
            Speed = speed;
            LifetimeSeconds = lifetimeSeconds;
        }

        public float Speed { get; }

        public float LifetimeSeconds { get; }

        public bool IsValid => float.IsFinite(Speed) && Speed > 0f && float.IsFinite(LifetimeSeconds) && LifetimeSeconds > 0f;
    }

    /// <summary>
    /// One wave's sweep of one update (DESIGN 19.1.7): its SlashId, the plane and axes it latched with, and the
    /// previous and current segments whose closed hull is the sweep. A latch's first sweep is degenerate: both segments
    /// are the initial one. This is the whole of what a later hit query needs, and it is produced once per update.
    /// </summary>
    public readonly struct SlashSweep
    {
        public SlashSweep(
            long slashId, double at, bool latch, Plane plane, Vector3 travelAxis, Vector3 spanAxis,
            Vector3 previousA, Vector3 previousB, Vector3 currentA, Vector3 currentB)
        {
            SlashId = slashId;
            At = at;
            IsLatch = latch;
            SourceSlashPlane = plane;
            TravelAxis = travelAxis;
            SpanAxis = spanAxis;
            PreviousA = previousA;
            PreviousB = previousB;
            CurrentA = currentA;
            CurrentB = currentB;
        }

        public long SlashId { get; }

        /// <summary>The input time of the update this sweep belongs to.</summary>
        public double At { get; }

        /// <summary>The latch update's degenerate sweep: previous and current are both the initial segment.</summary>
        public bool IsLatch { get; }

        public Plane SourceSlashPlane { get; }

        public Vector3 TravelAxis { get; }

        public Vector3 SpanAxis { get; }

        public Vector3 PreviousA { get; }

        public Vector3 PreviousB { get; }

        public Vector3 CurrentA { get; }

        public Vector3 CurrentB { get; }
    }

    /// <summary>
    /// The product SlashWave Core (DESIGN 19.1, Phase 4.50): one right-hand blade's gesture, its latch, and the waves it
    /// has published, updated one input sample at a time.
    /// <para>
    /// **Spaces (U4).** The gesture -- history, speed, gate, accepted samples, plane and frame, view -- is judged in
    /// tracking space, so artificial movement of the player is no part of a swing. Three things go to the world through
    /// the tracking space's pose as it stands: a latch, converted once when it is published; the live guide of an open
    /// span, which is the blade as it is now; and nothing else. A published wave's fixed values never follow the player.
    /// </para>
    /// <para>
    /// **One update** (19.1.5.1): waves whose lifetime has run out go first and give their slot back; the sample is
    /// added to the gesture; an unlatched stroke that is ready, has a frame and finds a free slot publishes one wave --
    /// its initial segment and degenerate sweep, nothing else this update; then every wave that was already flying moves
    /// on, takes a candidate from the current or frozen guide, closes if its close estimator says so (keeping that
    /// update's candidate), and gives the sweep of this update. Nothing of a finished update is evaluated again.
    /// </para>
    /// <para>
    /// **Capacity.** A fixed <see cref="Capacity"/>. A slot is looked for only when a wave is about to be published, never
    /// reserved at a stroke's begin. A stroke that finds none has had its one chance: it is not queued, retried or
    /// latched later, and no live wave is moved or ended for it. A new stroke after re-arming can use a slot freed in
    /// the same update.
    /// </para>
    /// <para>
    /// **Exchangeable estimators.** Latch, frame, span candidate and span close are each one small interface. The current
    /// latch and frame estimators judge the stroke not yet latched; each wave keeps the candidate and close estimators,
    /// speed and lifetime it latched with, so changing the current ones reaches later waves only.
    /// </para>
    /// <para>Every wave has a <c>long</c> SlashId, the one the Trace already carries, issued once and never reused.</para>
    /// </summary>
    public sealed class SlashWaveCore
    {
        /// <summary>How many waves can be alive at once (U6: the saved device runs never had more than two).</summary>
        public const int Capacity = 4;

        /// <summary>The fixed gate window and the speed above which a motion is not a swing.</summary>
        public const double GateMinimumWindowSeconds = 0.030;
        public const double GateMaximumWindowSeconds = 0.060;
        public const float GateMaximumSpeed = 20f;

        // Eight samples cover the 30-60 ms window at 90 Hz; eight accepted samples are a stroke's fixed room.
        private const int HistoryCapacity = 8;
        private const int AcceptedSampleCapacity = 8;
        private const float MinDerivedVectorLengthSquared = 1e-12f;

        private struct Wave
        {
            public long SlashId;
            public double LatchedAt;
            public Plane SourceSlashPlane;
            public Vector3 WaveOrigin;
            public Vector3 TravelAxis;
            public Vector3 SpanAxis;
            public float AcceptedSpan;
            public Vector3 PreviousSegmentStart;
            public Vector3 PreviousSegmentEnd;
            public Vector3 CurrentSegmentStart;
            public Vector3 CurrentSegmentEnd;
            public SlashWaveFlight Flight;
            public ISlashSpanCandidateEstimator Candidate;
            public ISlashSpanCloseEstimator Close;

            // NaN until the span closes; there is no separate open/closed flag.
            public double SpanClosedAt;
            public Vector3 FrozenGuideOrigin;
            public Vector3 FrozenGuideDirection;

            // What the last candidate evaluation saw, for readouts only (19.1.12).
            public double LastCandidateAt;
            public bool LastCandidateEvaluated;
            public bool LastCandidateFromFrozenGuide;
            public Vector3 LastGuideOrigin;
            public Vector3 LastGuideDirection;
            public float LastRawSpan;
            public float LastQ;
            public float LastDenominator;
            public bool LastTermsFinite;
            public bool LastCandidateUsable;
            public bool LastCandidateWidenedSpan;
        }

        private readonly BladePoseWindow _history = new BladePoseWindow(HistoryCapacity);
        private readonly EvaluatedBladePose[] _accepted = new EvaluatedBladePose[AcceptedSampleCapacity];
        private int _acceptedCount;
        private bool _strokeLatchSpent;
        private Vector3 _strokeBeginView;

        private readonly Wave[] _waves = new Wave[Capacity];
        private int _waveCount;
        private long _lastSlashId;

        private readonly SlashSweep[] _sweeps = new SlashSweep[Capacity];
        private int _sweepCount;

        private SlashBlade _blade;
        private SlashGestureSettings _gesture = SlashGestureSettings.Adopted;
        private ISlashLatchEstimator _latch = new EmitterChordLatch(0.35f);
        private ISlashFrameEstimator _frame = FixedSpanAngleFrame.Instance;
        private ISlashSpanCandidateEstimator _candidate = GuideRaySpanCandidate.Default;
        private ISlashSpanCloseEstimator _close = new CaptureTimeoutSpanClose(0.25f);
        private SlashWaveFlight _flight = SlashWaveFlight.Adopted;

        public SlashWaveCore(in SlashBlade blade)
        {
            _blade = blade;
        }

        // ---- settings: the current ones; a wave keeps what it latched with ----------------------------------------

        public SlashBlade Blade
        {
            get => _blade;
            set => _blade = value;
        }

        public SlashGestureSettings Gesture
        {
            get => _gesture;
            set => _gesture = value;
        }

        public ISlashLatchEstimator LatchEstimator
        {
            get => _latch;
            set => _latch = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ISlashFrameEstimator FrameEstimator
        {
            get => _frame;
            set => _frame = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ISlashSpanCandidateEstimator SpanCandidateEstimator
        {
            get => _candidate;
            set => _candidate = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ISlashSpanCloseEstimator SpanCloseEstimator
        {
            get => _close;
            set => _close = value ?? throw new ArgumentNullException(nameof(value));
        }

        public SlashWaveFlight Flight
        {
            get => _flight;
            set => _flight = value;
        }

        // ---- the one entrance ---------------------------------------------------------------------------------

        /// <summary>
        /// Evaluates one device sample into the blade pose it shows: false for a sample whose position or rotation is
        /// untracked or otherwise unusable, or with no usable blade.
        /// </summary>
        public bool TryEvaluatePose(in BladePoseSample sample, out EvaluatedBladePose evaluated)
        {
            if (!_blade.IsValid)
            {
                evaluated = default;
                return false;
            }

            Pose offset = _blade.GripToKatana;
            BladeFrame frame = _blade.Frame;
            return BladePoseAdapter.TryEvaluate(sample, offset, frame, out evaluated);
        }

        /// <summary>
        /// One update of DESIGN 19.1.5.1 from one device sample in tracking space, with the view forward in the same
        /// space (zero: no begin view check). <paramref name="trackingSpace"/> is where the tracking space stands in the
        /// world now, or none when tracking space is the world; a latch and the live guide are placed by it.
        /// Says what the sample was; <paramref name="evaluated"/> is its pose in tracking space unless it was unusable.
        /// </summary>
        public SlashInputOutcome Update(in BladePoseSample sample, Vector3 viewForward, Pose? trackingSpace, out EvaluatedBladePose evaluated)
        {
            double now = sample.TimestampSeconds;
            _sweepCount = 0;

            // 1. Expire first, so a latch later in this update can use the slot. Those left are the waves already
            //    flying; anything published below this update is not among them.
            RemoveExpired(now);
            int flying = _waveCount;

            // 2. The sample into the gesture.
            SlashInputOutcome outcome = TryRecordGesture(sample, viewForward, out evaluated);
            bool recorded = outcome == SlashInputOutcome.Recorded;

            // 3. An unlatched stroke may publish one wave.
            TryPublish(now, trackingSpace);

            // 4-7. Every wave that was already flying, with the blade as it is now as its live guide.
            Vector3 guideEmitter = Vector3.zero;
            Vector3 guideAxis = Vector3.zero;
            if (recorded)
            {
                guideEmitter = _blade.Emitter(evaluated);
                guideAxis = evaluated.BladeAxis;
                if (trackingSpace.HasValue)
                {
                    Pose space = trackingSpace.Value;
                    guideEmitter = space.position + (space.rotation * guideEmitter);
                    guideAxis = space.rotation * guideAxis;
                }
            }

            Advance(now, flying, recorded, guideEmitter, guideAxis);
            return outcome;
        }

        /// <summary>Ends the stroke under way: no history, no accepted samples, the latch chance back. Waves stay.</summary>
        public void ResetStroke()
        {
            _history.Clear();
            _acceptedCount = 0;
            _strokeLatchSpent = false;
            _strokeBeginView = Vector3.zero;
        }

        /// <summary>Everything back to the start: no stroke, no wave. SlashIds are not reused.</summary>
        public void Reset()
        {
            ResetStroke();
            Array.Clear(_waves, 0, _waves.Length);
            _waveCount = 0;
            _sweepCount = 0;
        }

        // ---- reads ----------------------------------------------------------------------------------------------

        public int RecordedPoseCount => _history.Count;

        public int AcceptedSampleCount => _acceptedCount;

        public ReadOnlySpan<EvaluatedBladePose> AcceptedSamples => new ReadOnlySpan<EvaluatedBladePose>(_accepted, 0, _acceptedCount);

        public Vector3 StrokeBeginViewForward => _acceptedCount > 0 ? _strokeBeginView : Vector3.zero;

        public bool IsLatchReady => _latch.IsLatchReady(AcceptedSamples, _blade);

        public bool TryGetSourceSlashPlaneCandidate(out Plane plane) => SlashStrokePlane.TryEstimate(AcceptedSamples, out plane);

        /// <summary>The frame the current stroke would latch with now, in tracking space.</summary>
        public bool TryGetSlashFrameCandidate(out SlashFrame frame) => _frame.TryEstimate(AcceptedSamples, _blade, out frame);

        public int WaveCount => _waveCount;

        public long SlashIdAt(int index) => index >= 0 && index < _waveCount ? _waves[index].SlashId : 0;

        public bool TryGetWave(
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
            latchedAt = 0.0;
            sourceSlashPlane = default;
            waveOrigin = default;
            travelAxis = default;
            spanAxis = default;
            acceptedSpan = 0f;
            previousSegmentStart = default;
            previousSegmentEnd = default;
            currentSegmentStart = default;
            currentSegmentEnd = default;
            if (index < 0 || index >= _waveCount)
            {
                return false;
            }

            Wave wave = _waves[index];
            latchedAt = wave.LatchedAt;
            sourceSlashPlane = wave.SourceSlashPlane;
            waveOrigin = wave.WaveOrigin;
            travelAxis = wave.TravelAxis;
            spanAxis = wave.SpanAxis;
            acceptedSpan = wave.AcceptedSpan;
            previousSegmentStart = wave.PreviousSegmentStart;
            previousSegmentEnd = wave.PreviousSegmentEnd;
            currentSegmentStart = wave.CurrentSegmentStart;
            currentSegmentEnd = wave.CurrentSegmentEnd;
            return true;
        }

        /// <summary>The flight and the estimators one wave latched with -- not the current ones.</summary>
        public bool TryGetWaveMethod(
            int index, out SlashWaveFlight flight, out ISlashSpanCandidateEstimator candidate, out ISlashSpanCloseEstimator close)
        {
            flight = default;
            candidate = null;
            close = null;
            if (index < 0 || index >= _waveCount)
            {
                return false;
            }

            flight = _waves[index].Flight;
            candidate = _waves[index].Candidate;
            close = _waves[index].Close;
            return true;
        }

        public bool TryGetWaveSpanClose(int index, out double spanClosedAt, out Vector3 frozenGuideOrigin, out Vector3 frozenGuideDirection)
        {
            spanClosedAt = 0.0;
            frozenGuideOrigin = default;
            frozenGuideDirection = default;
            if (index < 0 || index >= _waveCount || double.IsNaN(_waves[index].SpanClosedAt))
            {
                return false;
            }

            spanClosedAt = _waves[index].SpanClosedAt;
            frozenGuideOrigin = _waves[index].FrozenGuideOrigin;
            frozenGuideDirection = _waves[index].FrozenGuideDirection;
            return true;
        }

        public bool TryGetWaveCandidate(
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
            candidateAt = 0.0;
            evaluated = false;
            fromFrozenGuide = false;
            guideOrigin = default;
            guideDirection = default;
            rawSpan = 0f;
            q = 0f;
            denominator = 0f;
            termsFinite = false;
            usable = false;
            widenedSpan = false;
            if (index < 0 || index >= _waveCount || double.IsNaN(_waves[index].LastCandidateAt))
            {
                return false;
            }

            Wave wave = _waves[index];
            candidateAt = wave.LastCandidateAt;
            evaluated = wave.LastCandidateEvaluated;
            fromFrozenGuide = wave.LastCandidateFromFrozenGuide;
            guideOrigin = wave.LastGuideOrigin;
            guideDirection = wave.LastGuideDirection;
            rawSpan = wave.LastRawSpan;
            q = wave.LastQ;
            denominator = wave.LastDenominator;
            termsFinite = wave.LastTermsFinite;
            usable = wave.LastCandidateUsable;
            widenedSpan = wave.LastCandidateWidenedSpan;
            return true;
        }

        /// <summary>How many sweeps the last update produced: one per wave that latched or moved in it.</summary>
        public int SweepCount => _sweepCount;

        public SlashSweep SweepAt(int index)
        {
            if (index < 0 || index >= _sweepCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _sweeps[index];
        }

        // ---- the steps ------------------------------------------------------------------------------------------

        private void RemoveExpired(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now))
            {
                return;
            }

            int kept = 0;
            for (int i = 0; i < _waveCount; i++)
            {
                if (now >= _waves[i].LatchedAt + _waves[i].Flight.LifetimeSeconds)
                {
                    continue;
                }

                if (kept != i)
                {
                    _waves[kept] = _waves[i];
                }

                kept++;
            }

            for (int i = kept; i < _waveCount; i++)
            {
                _waves[i] = default;
            }

            _waveCount = kept;
        }

        private SlashInputOutcome TryRecordGesture(in BladePoseSample sample, Vector3 viewForward, out EvaluatedBladePose evaluated)
        {
            // A sample that cannot be shown resets the stroke; so does one the history refuses.
            if (!TryEvaluatePose(sample, out evaluated))
            {
                ResetStroke();
                return SlashInputOutcome.PoseUnusable;
            }

            if (!_history.TryAppend(evaluated))
            {
                ResetStroke();
                return SlashInputOutcome.HistoryRefused;
            }

            EvaluateGesture(evaluated, viewForward);
            return SlashInputOutcome.Recorded;
        }

        private void EvaluateGesture(in EvaluatedBladePose current, Vector3 viewForward)
        {
            // Values that are not usable decide nothing, rather than throw from the gate settings.
            if (!_gesture.IsValid)
            {
                return;
            }

            if (!_history.TryEvaluateLatest(GateMinimumWindowSeconds, GateMaximumWindowSeconds, out BladeMotionSample motion))
            {
                return;
            }

            var gateSettings = new BladeEdgeGateSettings(
                GateMinimumWindowSeconds,
                GateMaximumWindowSeconds,
                _gesture.MinimumSpeed,
                GateMaximumSpeed,
                _gesture.MinimumDisplacement,
                _gesture.MinimumEdgeLeadScore);
            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(motion, gateSettings);
            if (decision.IsAccepted)
            {
                AppendAccepted(current, viewForward);
                return;
            }

            if (decision.Reason == BladeEdgeGateReason.SpeedAboveMaximum)
            {
                ResetStroke();
                return;
            }

            // Past every non-directional check, a score this far onto the spine side is the return half of a stroke
            // under way. With nothing accepted it is just a rejected motion.
            if (_acceptedCount > 0
                && decision.Reason == BladeEdgeGateReason.EdgeLeadBelowThreshold
                && motion.EdgeLeadScore <= _gesture.ReturnStrokeEdgeLeadScore)
            {
                ResetStroke();
            }
        }

        private void AppendAccepted(in EvaluatedBladePose pose, Vector3 viewForward)
        {
            // Only a sample that would begin the stroke is checked against the view; one that fails is just not the
            // begin, and nothing is reset.
            if (_acceptedCount == 0)
            {
                if (!PassesBeginViewCheck(pose, viewForward, out Vector3 checkedView))
                {
                    return;
                }

                _strokeBeginView = checkedView;
            }

            if (_acceptedCount < AcceptedSampleCapacity)
            {
                _accepted[_acceptedCount] = pose;
                _acceptedCount++;
                return;
            }

            // Full: the begin sample stays at 0 and the newest overwrites the last slot.
            _accepted[AcceptedSampleCapacity - 1] = pose;
        }

        private bool PassesBeginViewCheck(in EvaluatedBladePose pose, Vector3 viewForward, out Vector3 normalizedView)
        {
            normalizedView = Vector3.zero;
            float lengthSquared = viewForward.sqrMagnitude;
            if (!SlashMath.IsFinite(viewForward) || !float.IsFinite(lengthSquared) || lengthSquared <= MinDerivedVectorLengthSquared)
            {
                return true;
            }

            float length = Mathf.Sqrt(lengthSquared);
            normalizedView = new Vector3(viewForward.x / length, viewForward.y / length, viewForward.z / length);
            return Vector3.Dot(pose.BladeAxis, normalizedView) >= _gesture.BeginBladeAxisViewDotMinimum;
        }

        // Every condition a wave needs, at one instant: the stroke has not had its chance, it is ready, it has a frame
        // now, and there is a free slot. A stroke with a frame gets exactly one attempt.
        private void TryPublish(double now, Pose? trackingSpace)
        {
            if (_strokeLatchSpent || !IsLatchReady)
            {
                return;
            }

            if (!_frame.TryEstimate(AcceptedSamples, _blade, out SlashFrame frame))
            {
                // Not yet a frame; a later sample of the same stroke may still give one.
                return;
            }

            Plane plane = frame.SourceSlashPlane;
            Vector3 begin = frame.BeginEmitter;
            Vector3 travel = frame.TravelAxis;
            Vector3 span = frame.SpanAxis;

            // Settled in the world once, by the tracking space as it stands at the latch. Without one the frame is
            // already the world's and is passed on exactly as derived.
            if (trackingSpace.HasValue)
            {
                Pose space = trackingSpace.Value;
                Vector3 planePoint = space.position + (space.rotation * (-plane.normal * plane.distance));
                plane = new Plane(space.rotation * plane.normal, planePoint);
                begin = space.position + (space.rotation * begin);
                travel = space.rotation * travel;
                span = space.rotation * span;
            }

            _strokeLatchSpent = true;
            TryLatch(now, plane, begin, begin + span * frame.InitialSpan, travel, span, frame.InitialSpan);
        }

        /// <summary>
        /// Publishes one wave with the current flight and estimators, or changes nothing: no free slot, or a state
        /// that cannot stay finite for the wave's whole lifetime. Internal so tests can give it states no pose makes.
        /// </summary>
        internal bool TryLatch(
            double now, in Plane plane, Vector3 beginEmitter, Vector3 initialEnd, Vector3 travelAxis, Vector3 spanAxis,
            float acceptedSpan)
        {
            // The slot is looked for here, just before publishing, and nowhere else.
            if (_waveCount >= Capacity)
            {
                return false;
            }

            // Whether the close estimator would close before the lifetime ends is not a condition: a wave may expire
            // with its span still open (DESIGN 19.1.1).
            SlashWaveFlight flight = _flight;
            ISlashSpanCloseEstimator close = _close;
            if (double.IsNaN(now) || double.IsInfinity(now)
                || !SlashMath.IsFinite(plane.normal) || !float.IsFinite(plane.distance)
                || !SlashMath.IsFinite(beginEmitter) || !SlashMath.IsFinite(initialEnd)
                || !SlashMath.IsFinite(travelAxis) || !SlashMath.IsFinite(spanAxis)
                || !float.IsFinite(acceptedSpan) || acceptedSpan < 0f
                || !flight.IsValid)
            {
                return false;
            }

            double expiresAt = now + flight.LifetimeSeconds;
            if (double.IsNaN(expiresAt) || double.IsInfinity(expiresAt) || !(expiresAt > now))
            {
                return false;
            }

            // Both ends have to survive the whole lifetime.
            Vector3 travel = travelAxis * (flight.Speed * flight.LifetimeSeconds);
            if (!SlashMath.IsFinite(travel) || !SlashMath.IsFinite(beginEmitter + travel) || !SlashMath.IsFinite(initialEnd + travel))
            {
                return false;
            }

            _lastSlashId++;
            _waves[_waveCount] = new Wave
            {
                SlashId = _lastSlashId,
                LatchedAt = now,
                SourceSlashPlane = plane,
                WaveOrigin = beginEmitter,
                TravelAxis = travelAxis,
                SpanAxis = spanAxis,
                AcceptedSpan = acceptedSpan,
                PreviousSegmentStart = beginEmitter,
                PreviousSegmentEnd = initialEnd,
                CurrentSegmentStart = beginEmitter,
                CurrentSegmentEnd = initialEnd,
                Flight = flight,
                Candidate = _candidate,
                Close = close,
                SpanClosedAt = double.NaN,
                LastCandidateAt = double.NaN,
            };
            _waveCount++;

            // The latch update's one sweep: degenerate, the initial segment twice.
            AddSweep(new SlashSweep(
                _lastSlashId, now, true, plane, travelAxis, spanAxis, beginEmitter, initialEnd, beginEmitter, initialEnd));
            return true;
        }

        // At most one sweep per wave per update, so the fixed room always suffices in an update; a test that calls the
        // steps directly without an update in between cannot overrun it either.
        private void AddSweep(in SlashSweep sweep)
        {
            if (_sweepCount < _sweeps.Length)
            {
                _sweeps[_sweepCount++] = sweep;
            }
        }

        /// <summary>Steps 4-7 for the first <paramref name="flying"/> waves. Internal for the same tests as the latch.</summary>
        internal void Advance(double now, int flying, bool hasGuide, Vector3 guideEmitter, Vector3 guideBladeAxis)
        {
            if (double.IsNaN(now) || double.IsInfinity(now))
            {
                return;
            }

            int limit = flying < _waveCount ? flying : _waveCount;
            for (int i = 0; i < limit; i++)
            {
                double elapsed = now - _waves[i].LatchedAt;
                if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0.0)
                {
                    continue;
                }

                double travelDistance = _waves[i].Flight.Speed * elapsed;
                if (double.IsNaN(travelDistance) || double.IsInfinity(travelDistance) || Math.Abs(travelDistance) > float.MaxValue)
                {
                    continue;
                }

                Vector3 a = _waves[i].WaveOrigin + _waves[i].TravelAxis * (float)travelDistance;
                if (!SlashMath.IsFinite(a))
                {
                    continue;
                }

                // A wave never travels backwards: a time behind where it already is leaves it exactly as it was.
                if (Vector3.Dot(a - _waves[i].CurrentSegmentStart, _waves[i].TravelAxis) < 0f)
                {
                    continue;
                }

                bool hasCandidate = false;
                SlashSpanCandidate candidate = default;
                bool evaluated = false;
                bool fromFrozen = false;
                Vector3 usedOrigin = default;
                Vector3 usedDirection = default;
                Vector3 normal = _waves[i].SourceSlashPlane.normal;
                if (double.IsNaN(_waves[i].SpanClosedAt))
                {
                    // Span open: the blade as it is now, onto this wave's plane.
                    if (hasGuide && TryProjectGuide(_waves[i], guideEmitter, guideBladeAxis, out Vector3 liveOrigin, out Vector3 liveDirection))
                    {
                        candidate = EvaluateCandidate(_waves[i], a, liveOrigin, liveDirection);
                        hasCandidate = candidate.Valid;
                        evaluated = true;
                        usedOrigin = liveOrigin;
                        usedDirection = liveDirection;

                        // Closing keeps this update's live candidate; later updates use the frozen guide.
                        if (_waves[i].Close.ShouldClose(_waves[i].LatchedAt, now))
                        {
                            _waves[i].FrozenGuideOrigin = liveOrigin;
                            _waves[i].FrozenGuideDirection = liveDirection;
                            _waves[i].SpanClosedAt = now;
                        }
                    }
                }
                else
                {
                    candidate = EvaluateCandidate(_waves[i], a, _waves[i].FrozenGuideOrigin, _waves[i].FrozenGuideDirection);
                    hasCandidate = candidate.Valid;
                    evaluated = true;
                    fromFrozen = true;
                    usedOrigin = _waves[i].FrozenGuideOrigin;
                    usedDirection = _waves[i].FrozenGuideDirection;
                }

                float acceptedSpan = _waves[i].AcceptedSpan;
                float rawSpan = candidate.RawSpan;
                bool widened = hasCandidate && rawSpan > acceptedSpan && CanCarrySpan(_waves[i], a, rawSpan);
                if (widened)
                {
                    acceptedSpan = rawSpan;
                }

                // Written after the decision and read by nothing that decides.
                _waves[i].LastCandidateAt = now;
                _waves[i].LastCandidateEvaluated = evaluated;
                _waves[i].LastCandidateFromFrozenGuide = fromFrozen;
                _waves[i].LastGuideOrigin = usedOrigin;
                _waves[i].LastGuideDirection = usedDirection;
                _waves[i].LastRawSpan = rawSpan;
                _waves[i].LastQ = candidate.Q;
                _waves[i].LastDenominator = candidate.Denominator;
                _waves[i].LastTermsFinite = candidate.TermsFinite;
                _waves[i].LastCandidateUsable = hasCandidate;
                _waves[i].LastCandidateWidenedSpan = widened;

                Vector3 b = a + _waves[i].SpanAxis * acceptedSpan;
                if (!SlashMath.IsFinite(b))
                {
                    continue;
                }

                _waves[i].AcceptedSpan = acceptedSpan;
                _waves[i].PreviousSegmentStart = _waves[i].CurrentSegmentStart;
                _waves[i].PreviousSegmentEnd = _waves[i].CurrentSegmentEnd;
                _waves[i].CurrentSegmentStart = a;
                _waves[i].CurrentSegmentEnd = b;
                AddSweep(new SlashSweep(
                    _waves[i].SlashId, now, false, _waves[i].SourceSlashPlane, _waves[i].TravelAxis, _waves[i].SpanAxis,
                    _waves[i].PreviousSegmentStart, _waves[i].PreviousSegmentEnd, a, b));
            }
        }

        private static SlashSpanCandidate EvaluateCandidate(in Wave wave, Vector3 a, Vector3 guideOrigin, Vector3 guideDirection)
        {
            if (!SlashMath.IsFinite(a) || !SlashMath.IsFinite(guideOrigin) || !SlashMath.IsFinite(guideDirection))
            {
                return default;
            }

            Vector3 normal = wave.SourceSlashPlane.normal;
            if (!SlashMath.IsFinite(normal) || !float.IsFinite(wave.SourceSlashPlane.distance))
            {
                return default;
            }

            return wave.Candidate.Evaluate(normal, a, wave.SpanAxis, guideOrigin, guideDirection);
        }

        private static bool TryProjectGuide(in Wave wave, Vector3 guideEmitter, Vector3 guideBladeAxis, out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = default;
            if (!SlashMath.IsFinite(guideEmitter) || !SlashMath.IsFinite(guideBladeAxis))
            {
                return false;
            }

            Vector3 normal = wave.SourceSlashPlane.normal;
            if (!SlashMath.IsFinite(normal) || !float.IsFinite(wave.SourceSlashPlane.distance))
            {
                return false;
            }

            origin = wave.SourceSlashPlane.ClosestPointOnPlane(guideEmitter);
            if (!SlashMath.IsFinite(origin))
            {
                return false;
            }

            return SlashMath.TryProjectOntoPlane(guideBladeAxis, normal, out direction);
        }

        // A wider span is taken only if both ends stay finite now and at the end of the life the wave was given.
        private static bool CanCarrySpan(in Wave wave, Vector3 a, float span)
        {
            if (!float.IsFinite(span) || !SlashMath.IsFinite(a + wave.SpanAxis * span))
            {
                return false;
            }

            double travelDistance = wave.Flight.Speed * (double)wave.Flight.LifetimeSeconds;
            if (double.IsNaN(travelDistance) || double.IsInfinity(travelDistance) || Math.Abs(travelDistance) > float.MaxValue)
            {
                return false;
            }

            Vector3 travelEnd = wave.WaveOrigin + wave.TravelAxis * (float)travelDistance;
            return SlashMath.IsFinite(travelEnd) && SlashMath.IsFinite(travelEnd + wave.SpanAxis * span);
        }
    }
}
