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

    /// <summary>Why an update ended the stroke under way. Observation only (DESIGN 19.1.12): nothing decides by it.</summary>
    public enum SlashStrokeReset
    {
        None = 0,
        PoseUnusable = 1,
        HistoryRefused = 2,
        SpeedAboveMaximum = 3,
        ReturnStroke = 4,

        /// <summary>The motion stayed under the gate's minimum speed for <see cref="SlashWaveCore.StrokeQuietSeconds"/>: the swing was over (D-224).</summary>
        Quiet = 5,
    }

    /// <summary>What became of a stroke's one latch attempt in an update. Observation only.</summary>
    public enum SlashLatchAttempt
    {
        None = 0,
        NoFrame = 1,
        Latched = 2,
        NoSlot = 3,
        NotFinite = 4,
    }

    /// <summary>
    /// What one update of <see cref="SlashWaveCore"/> decided and saw on the way to a wave, for readouts (DESIGN
    /// 19.1.12): written after each decision and read by nothing that decides. A swing that gives no wave is read
    /// from these -- which gate turned its motion away, whether its begin faced the view, how far its emitter chord
    /// got, what ended it, whether its latch found a frame and a slot.
    /// </summary>
    public struct SlashUpdateObservation
    {
        /// <summary>The gate had a window (two samples 30-60 ms apart) to judge this update by.</summary>
        public bool GateEvaluated;

        public BladeEdgeGateReason GateReason;

        public double WindowSeconds;

        /// <summary>What the gate judged: the cut sample point's speed, its displacement over the window, the edge lead score.</summary>
        public float Speed;

        public float Displacement;

        public float EdgeLeadScore;

        /// <summary>A sample that would begin the stroke was checked against the view: its dot with it, and whether it passed.</summary>
        public bool BeginViewChecked;

        public bool BeginViewPassed;

        public float BeginViewDot;

        /// <summary>The stroke after the update: its accepted samples, and its emitter chord (the chord it had reached, when it was reset).</summary>
        public int AcceptedCount;

        public float ChordMetres;

        public SlashStrokeReset Reset;

        /// <summary>The stroke was ready to latch in this update, its chance not yet spent.</summary>
        public bool LatchReady;

        /// <summary>
        /// After this update the stroke's one latch chance is spent: it latched now, or had latched before and has not
        /// ended since -- a motion accepted into it gives no wave until something ends it.
        /// </summary>
        public bool LatchSpent;

        public SlashLatchAttempt Latch;

        public long LatchedSlashId;
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
    /// **Capacity.** A fixed <see cref="WaveCapacity"/>, selected at construction. A slot is looked for only when a wave is about to be published, never
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
        /// <summary>The default VR capacity. Screen input may choose a larger preallocated store.</summary>
        public const int Capacity = 4;

        /// <summary>The maximum supported mobile capacity, including hit-query scratch and lineage tracking.</summary>
        public const int MaximumCapacity = 16;

        /// <summary>The fixed gate window and the speed above which a motion is not a swing.</summary>
        public const double GateMinimumWindowSeconds = 0.030;
        public const double GateMaximumWindowSeconds = 0.060;
        public const float GateMaximumSpeed = 20f;

        /// <summary>
        /// A stroke ends once the gate has judged its motion under the minimum speed for this long (D-224, 2026-10-13):
        /// the swing is over, latched or not, and the next motion begins a stroke of its own. Before this a latched
        /// stroke ended only by a fast spine-first return or a tracking reset, so a swing after a slow return was
        /// accepted into the spent stroke and gave no wave (the whiffs of the Link play sd1PlayLink: 8 of 8).
        /// </summary>
        public const double StrokeQuietSeconds = 0.1;

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
            public bool IsScreenSlash;
            public Vector3 StartTravelPerMetre;
            public Vector3 EndTravelPerMetre;
            public float ScreenInitialTravelDistance;
            public double LastAdvancedAt;
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
        private double _quietSince = double.NaN;   // when the gate first judged the motion under the minimum speed, in a row

        private readonly Wave[] _waves;
        private int _waveCount;
        private long _lastSlashId;

        private readonly SlashSweep[] _sweeps;
        private int _sweepCount;

        private SlashBlade _blade;
        private SlashGestureSettings _gesture = SlashGestureSettings.Adopted;
        private ISlashLatchEstimator _latch = new EmitterChordLatch(0.35f);
        private ISlashFrameEstimator _frame = FixedSpanAngleFrame.Instance;
        private ISlashSpanCandidateEstimator _candidate = GuideRaySpanCandidate.Default;
        private ISlashSpanCloseEstimator _close = new CaptureTimeoutSpanClose(0.25f);
        private SlashWaveFlight _flight = SlashWaveFlight.Adopted;

        // One update's observation (19.1.12): written after each decision below and read by nothing that decides.
        private SlashUpdateObservation _observing;

        public SlashWaveCore(in SlashBlade blade, int capacity = Capacity)
        {
            if (capacity < 1 || capacity > MaximumCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _blade = blade;
            _waves = new Wave[capacity];
            _sweeps = new SlashSweep[capacity];
        }

        public int WaveCapacity => _waves.Length;

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
            _observing = default;

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

            // What this update left: the stroke's size now, unless a reset already kept the size it had.
            _observing.AcceptedCount = _acceptedCount;
            if (_observing.Reset == SlashStrokeReset.None)
            {
                _observing.ChordMetres = StrokeChord();
            }

            _observing.LatchSpent = _strokeLatchSpent;
            LastUpdate = _observing;
            return outcome;
        }

        /// <summary>
        /// Advances and expires waves without feeding a blade gesture. Call once per mobile update before latching
        /// that update's flicks, then consume SweepAt after all latches. A camera or input device is not needed in flight.
        /// </summary>
        public void Tick(double now)
        {
            _sweepCount = 0;
            _observing = default;
            if (!double.IsNaN(now) && !double.IsInfinity(now))
            {
                RemoveExpired(now);
                Advance(now, _waveCount, false, Vector3.zero, Vector3.zero);
            }

            LastUpdate = _observing;
        }

        /// <summary>
        /// Publishes an already recognized screen flick, with its plane and two expanding endpoint rays frozen now.
        /// Flight speed/lifetime are captured from Flight. This appends the latch sweep to this Tick's sweeps.
        /// </summary>
        public bool TryLatchScreenSlash(double now, in ScreenSlashFrame frame, out long slashId)
        {
            slashId = 0;
            _observing.LatchReady = true;
            _observing.LatchSpent = true;
            SlashWaveFlight flight = _flight;
            if (double.IsNaN(now) || double.IsInfinity(now) || !frame.IsValid || !flight.IsValid
                || !(now + flight.LifetimeSeconds > now) || double.IsInfinity(now + flight.LifetimeSeconds))
            {
                _observing.Latch = SlashLatchAttempt.NotFinite;
                LastUpdate = _observing;
                return false;
            }

            RemoveExpired(now);
            if (_waveCount >= _waves.Length)
            {
                _observing.Latch = SlashLatchAttempt.NoSlot;
                LastUpdate = _observing;
                return false;
            }

            float distance = flight.Speed * flight.LifetimeSeconds;
            float initialSpan = (frame.InitialB - frame.InitialA).magnitude;
            Vector3 finalA = frame.InitialA + frame.StartTravelPerMetre * distance;
            Vector3 finalB = frame.InitialB + frame.EndTravelPerMetre * distance;
            if (!float.IsFinite(distance) || !float.IsFinite(initialSpan)
                || !float.IsFinite((finalB - finalA).sqrMagnitude)
                || !SlashMath.IsFinite(finalA) || !SlashMath.IsFinite(finalB))
            {
                _observing.Latch = SlashLatchAttempt.NotFinite;
                LastUpdate = _observing;
                return false;
            }

            slashId = ++_lastSlashId;
            _waves[_waveCount++] = new Wave
            {
                SlashId = slashId,
                LatchedAt = now,
                SourceSlashPlane = frame.SourceSlashPlane,
                WaveOrigin = frame.Origin,
                TravelAxis = frame.TravelAxis,
                SpanAxis = frame.SpanAxis,
                AcceptedSpan = initialSpan,
                PreviousSegmentStart = frame.InitialA,
                PreviousSegmentEnd = frame.InitialB,
                CurrentSegmentStart = frame.InitialA,
                CurrentSegmentEnd = frame.InitialB,
                Flight = flight,
                IsScreenSlash = true,
                StartTravelPerMetre = frame.StartTravelPerMetre,
                EndTravelPerMetre = frame.EndTravelPerMetre,
                ScreenInitialTravelDistance = frame.InitialTravelDistance,
                LastAdvancedAt = now,
                Candidate = _candidate,
                Close = _close,
                SpanClosedAt = now,
                FrozenGuideOrigin = frame.Origin,
                FrozenGuideDirection = frame.EndTravelPerMetre.normalized,
                LastCandidateAt = double.NaN,
            };
            _observing.Latch = SlashLatchAttempt.Latched;
            _observing.LatchedSlashId = slashId;
            LastUpdate = _observing;
            AddSweep(new SlashSweep(slashId, now, true, frame.SourceSlashPlane, frame.TravelAxis, frame.SpanAxis,
                frame.InitialA, frame.InitialB, frame.InitialA, frame.InitialB));
            return true;
        }

        /// <summary>Ends the stroke under way: no history, no accepted samples, the latch chance back. Waves stay.</summary>
        public void ResetStroke()
        {
            _history.Clear();
            _acceptedCount = 0;
            _strokeLatchSpent = false;
            _strokeBeginView = Vector3.zero;
            _quietSince = double.NaN;
        }

        // The stroke ends inside an update: the observation keeps why, with the chord the stroke had reached.
        private void EndStroke(SlashStrokeReset why)
        {
            _observing.Reset = why;
            _observing.ChordMetres = StrokeChord();
            ResetStroke();
        }

        // The emitter chord of the stroke under way, its begin sample to its newest; 0 under two samples.
        private float StrokeChord()
        {
            if (_acceptedCount < 2)
            {
                return 0f;
            }

            Vector3 chord = _blade.Emitter(_accepted[_acceptedCount - 1]) - _blade.Emitter(_accepted[0]);
            float length = chord.magnitude;
            return float.IsFinite(length) ? length : 0f;
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

        /// <summary>What the last update decided and saw. Observation only: nothing here decides anything.</summary>
        public SlashUpdateObservation LastUpdate { get; private set; }

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
                EndStroke(SlashStrokeReset.PoseUnusable);
                return SlashInputOutcome.PoseUnusable;
            }

            if (!_history.TryAppend(evaluated))
            {
                EndStroke(SlashStrokeReset.HistoryRefused);
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
            _observing.GateEvaluated = true;
            _observing.GateReason = decision.Reason;
            _observing.WindowSeconds = motion.DeltaTimeSeconds;
            _observing.Speed = motion.Speed;
            _observing.Displacement = motion.CutSampleDisplacement.magnitude;
            _observing.EdgeLeadScore = motion.EdgeLeadScore;
            if (decision.IsAccepted)
            {
                _quietSince = double.NaN;
                AppendAccepted(current, viewForward);
                return;
            }

            if (decision.Reason == BladeEdgeGateReason.SpeedAboveMaximum)
            {
                EndStroke(SlashStrokeReset.SpeedAboveMaximum);
                return;
            }

            // Under the minimum speed: quiet. Quiet for StrokeQuietSeconds ends the stroke under way, latched or not
            // (D-224): the swing is over, and the next motion begins a stroke of its own rather than joining a spent one.
            if (decision.Reason == BladeEdgeGateReason.SpeedBelowMinimum)
            {
                if (double.IsNaN(_quietSince))
                {
                    _quietSince = current.TimestampSeconds;
                }
                else if (_acceptedCount > 0 && current.TimestampSeconds - _quietSince >= StrokeQuietSeconds)
                {
                    EndStroke(SlashStrokeReset.Quiet);
                }

                return;
            }

            _quietSince = double.NaN;

            // Past every non-directional check, a score this far onto the spine side is the return half of a stroke
            // under way. With nothing accepted it is just a rejected motion.
            if (_acceptedCount > 0
                && decision.Reason == BladeEdgeGateReason.EdgeLeadBelowThreshold
                && motion.EdgeLeadScore <= _gesture.ReturnStrokeEdgeLeadScore)
            {
                EndStroke(SlashStrokeReset.ReturnStroke);
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
            float dot = Vector3.Dot(pose.BladeAxis, normalizedView);
            bool passes = dot >= _gesture.BeginBladeAxisViewDotMinimum;
            _observing.BeginViewChecked = true;
            _observing.BeginViewDot = dot;
            _observing.BeginViewPassed = passes;
            return passes;
        }

        // Every condition a wave needs, at one instant: the stroke has not had its chance, it is ready, it has a frame
        // now, and there is a free slot. A stroke with a frame gets exactly one attempt.
        private void TryPublish(double now, Pose? trackingSpace)
        {
            if (_strokeLatchSpent || !IsLatchReady)
            {
                return;
            }

            _observing.LatchReady = true;
            if (!_frame.TryEstimate(AcceptedSamples, _blade, out SlashFrame frame))
            {
                // Not yet a frame; a later sample of the same stroke may still give one.
                _observing.Latch = SlashLatchAttempt.NoFrame;
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
            if (_waveCount >= _waves.Length)
            {
                _observing.Latch = SlashLatchAttempt.NoSlot;
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
                _observing.Latch = SlashLatchAttempt.NotFinite;
                return false;
            }

            double expiresAt = now + flight.LifetimeSeconds;
            if (double.IsNaN(expiresAt) || double.IsInfinity(expiresAt) || !(expiresAt > now))
            {
                _observing.Latch = SlashLatchAttempt.NotFinite;
                return false;
            }

            // Both ends have to survive the whole lifetime.
            Vector3 travel = travelAxis * (flight.Speed * flight.LifetimeSeconds);
            if (!SlashMath.IsFinite(travel) || !SlashMath.IsFinite(beginEmitter + travel) || !SlashMath.IsFinite(initialEnd + travel))
            {
                _observing.Latch = SlashLatchAttempt.NotFinite;
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
            _observing.Latch = SlashLatchAttempt.Latched;
            _observing.LatchedSlashId = _lastSlashId;

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

                if (_waves[i].IsScreenSlash)
                {
                    AdvanceScreenWave(i, now, (float)travelDistance);
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

        private void AdvanceScreenWave(int index, double now, float distance)
        {
            ref Wave wave = ref _waves[index];
            if (now < wave.LastAdvancedAt)
            {
                return;
            }

            // Absolute latch time and fixed rays avoid accumulating frame-to-frame drift.
            float rayDistance = wave.ScreenInitialTravelDistance + distance;
            Vector3 a = wave.WaveOrigin + wave.StartTravelPerMetre * rayDistance;
            Vector3 b = wave.WaveOrigin + wave.EndTravelPerMetre * rayDistance;
            float span = (b - a).magnitude;
            if (!SlashMath.IsFinite(a) || !SlashMath.IsFinite(b) || !float.IsFinite(span))
            {
                return;
            }

            wave.LastAdvancedAt = now;
            wave.AcceptedSpan = span;
            wave.PreviousSegmentStart = wave.CurrentSegmentStart;
            wave.PreviousSegmentEnd = wave.CurrentSegmentEnd;
            wave.CurrentSegmentStart = a;
            wave.CurrentSegmentEnd = b;
            AddSweep(new SlashSweep(wave.SlashId, now, false, wave.SourceSlashPlane, wave.TravelAxis, wave.SpanAxis,
                wave.PreviousSegmentStart, wave.PreviousSegmentEnd, a, b));
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
