using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Development tool that records a few seconds of the right-hand grip
    /// samples the sandbox katana sees and feeds the same sequence back into
    /// it, so a swing can be run through the current slash pipeline again.
    ///
    /// The short pose replay lives in a fixed array in memory; it is not a
    /// saved preset, and a recording that fills the array simply
    /// stops. Tracking-loss samples are kept like any other, because a replay
    /// has to break the stroke where the recording did; only a sample whose
    /// time does not move forward is left out, so recorded times are always
    /// monotonic.
    ///
    /// A replay takes the katana's input over. It turns the katana's live
    /// input off -- which starts its slash state over -- and feeds one recorded
    /// sample per Update into the same TryRecordSample the controller normally
    /// feeds, laying the recorded time offsets onto the clock as it stood when
    /// replay began. Once the whole recording has gone through, input stays
    /// with the replay so its result can be watched: each Update then feeds one
    /// untracked sample on the continued replay clock, which makes up no pose
    /// but keeps the replayed waves flying until they expire. Stop, Clear, a
    /// new recording or disabling this component hands input back to the
    /// controller, which starts over again, so replayed and live samples never
    /// meet in one stroke or one wave.
    ///
    /// One result can be pinned for comparison. Pin copies what the katana
    /// shows at that moment -- the wave count, each wave's latch time,
    /// accepted span, span close and current segment, and the stroke's
    /// accepted sample count and latch readiness -- into fixed arrays here,
    /// so a person can read it beside the current result after replaying the
    /// same recording again. Only this component's own display reads the pin:
    /// gameplay never does, nothing is judged or adopted from it, and it is
    /// never saved. A pin survives Stop and a new replay, and goes with Clear
    /// or a new recording.
    ///
    /// The display names the slash method the result came from. The first
    /// candidate is the only one implemented, so it is simply named here and
    /// copied along with a pin; there is nothing to choose between yet.
    ///
    /// The panel also steps the katana's first-candidate tuning values up and
    /// down while playing. Those values belong to the katana: nothing here
    /// keeps a copy, a pin does not record them, and the buttons only ever go
    /// through the katana's own checked setters.
    ///
    /// Dump writes the katana's current slash numbers to the Unity console
    /// once. It is throwaway development output for Phase 0.55 tuning: not a
    /// trace event, not a saved format, and nothing promises it stays the same
    /// or can be read back. Whether it stays is decided when tuning is done.
    /// With Auto dump on latch ticked, the same readout is logged once for
    /// each newly latched wave, while its stroke and begin are still there.
    ///
    /// Each recorded sample keeps the view forward it was taken with, and a
    /// replay hands that back with the sample, so the katana's begin view
    /// check sees the recorded head rather than wherever the head is now.
    ///
    /// Separately, Capture records full input updates and wave observations
    /// in bounded growing lists. Save writes them under
    /// C:\log\zantetsuken-vr\SlashSpan. End is not Save: save before leaving
    /// Play Mode or editing scripts, since a domain reload loses unsaved rows.
    /// Auto capture restarts only when no unsaved rows remain.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxSlashPoseRecorder : MonoBehaviour
    {
        /// <summary>
        /// Fixed recording length in samples: about 5.7 s at the 90 Hz Quest
        /// Link mode.
        /// </summary>
        internal const int Capacity = 512;

        /// <summary>
        /// Name of the slash method the katana runs: the first candidate, and
        /// the only one implemented.
        /// </summary>
        internal const string CandidateName = "First Candidate";

        // Step sizes for the tuning buttons.
        private const float SpeedStep = 0.25f;
        private const float DistanceStep = 0.025f;
        private const float ScoreStep = 0.05f;
        private const float TimeoutStep = 0.05f;
        private const float DotStep = 0.1f;

        [Tooltip("The katana whose grip samples are recorded and replayed.")]
        [SerializeField] private SandboxRightHandKatana katana;

        // Samples as they came, except that TimestampSeconds holds the offset
        // from the first recorded sample. Allocated once.
        private readonly BladePoseSample[] samples = new BladePoseSample[Capacity];

        // The view forward each sample was taken with, zero where there was
        // none. Same capacity, allocated once.
        private readonly Vector3[] viewForwards = new Vector3[Capacity];
        private int sampleCount;

        private bool recording;
        private double recordingOrigin;
        private double lastRecordedTime;

        private bool replaying;
        private int replayIndex;
        private double replayClockStart;
        // The replay clock's latest time, carried on after the last recorded
        // sample while the replay's result is shown.
        private double lastReplayTime;

        // The one pinned result, copied from the katana on Pin. Parallel fixed
        // arrays with one slot per wave the katana can hold; nothing else
        // reads them.
        private bool hasPin;
        private int pinnedWaveCount;
        private int pinnedAcceptedSampleCount;
        private bool pinnedLatchReady;
        private string pinnedCandidateName;
        private readonly double[] pinnedLatchedAt = new double[SandboxSlashWaveStore.Capacity];
        private readonly float[] pinnedAcceptedSpan = new float[SandboxSlashWaveStore.Capacity];
        private readonly bool[] pinnedSpanClosed = new bool[SandboxSlashWaveStore.Capacity];
        private readonly double[] pinnedSpanClosedAt = new double[SandboxSlashWaveStore.Capacity];
        private readonly Vector3[] pinnedSegmentStart = new Vector3[SandboxSlashWaveStore.Capacity];
        private readonly Vector3[] pinnedSegmentEnd = new Vector3[SandboxSlashWaveStore.Capacity];

        // Reused across draws: OnGUI runs more than once per frame.
        private readonly StringBuilder comparisonText = new StringBuilder(1024);
        private readonly StringBuilder dumpText = new StringBuilder(2048);

        [Tooltip("Log a slash dump once for each newly latched wave. Development output only.")]
        [SerializeField] private bool autoDumpOnLatch;

        // The capture of what the slash path was given. Held in memory while
        // capturing and written only when saved, so no update pays for a disk
        // write. It outlives a start/stop pair so a capture can be saved after
        // it has been ended.
        private readonly SandboxSlashCapture capture = new SandboxSlashCapture();

        [Tooltip("Names the capture's directory, after the date and time. Development only.")]
        [SerializeField] private string captureRunId = string.Empty;

        [Tooltip("Saved with the capture, for the observation the swing was recorded for.")]
        [SerializeField] private string captureNote = string.Empty;

        [Tooltip("Start a capture by itself whenever one can be started: at play start, and again after each "
            + "successful save. Development only.")]
        [SerializeField] private bool autoCaptureWhenIdle = true;

        // What the last save did, kept on screen: a failure that only reached
        // the console would look exactly like a success from the headset.
        private string lastSaveMessage = string.Empty;

        // The newest wave's latch time as last seen, NaN with no wave. A new
        // latch is the newest wave changing; an expiry leaves it alone.
        private double lastSeenNewestLatchedAt = double.NaN;

        internal bool IsRecording => recording;

        internal bool IsReplaying => replaying;

        /// <summary>
        /// A replay has fed every recorded sample and still holds the katana's
        /// input, so what it produced stays in view. Derived, not stored: the
        /// replay is over but live input has not been handed back.
        /// </summary>
        internal bool IsShowingReplayResult => !replaying && katana != null && !katana.LiveInputEnabled;

        internal int RecordedSampleCount => sampleCount;

        internal int ReplayIndex => replayIndex;

        internal bool HasPin => hasPin;

        internal int PinnedWaveCount => pinnedWaveCount;

        internal int PinnedAcceptedSampleCount => pinnedAcceptedSampleCount;

        internal bool PinnedLatchReady => pinnedLatchReady;

        /// <summary>The candidate the pinned result came from; null without a pin.</summary>
        internal string PinnedCandidateName => pinnedCandidateName;

        /// <summary>
        /// Starts a fresh recording, ending any replay first. A pin taken from
        /// the old recording goes with it.
        /// </summary>
        internal void BeginRecording()
        {
            Stop();
            ClearPin();
            sampleCount = 0;
            replayIndex = 0;
            recording = true;
        }

        /// <summary>
        /// Adds one sample to the recording. False when not recording, when
        /// the sample's time is not finite or does not move past the last one,
        /// or once the recording is full -- which also ends it.
        /// </summary>
        internal bool TryAppendRecordedSample(in BladePoseSample sample)
        {
            return TryAppendRecordedSample(sample, Vector3.zero);
        }

        /// <summary>
        /// The same, keeping the view forward the sample was taken with. Zero
        /// means none, and replay then skips the begin view check for it.
        /// </summary>
        internal bool TryAppendRecordedSample(in BladePoseSample sample, Vector3 viewForward)
        {
            if (!recording)
            {
                return false;
            }

            double time = sample.TimestampSeconds;
            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                return false;
            }

            if (sampleCount > 0 && !(time > lastRecordedTime))
            {
                return false;
            }

            if (sampleCount == 0)
            {
                recordingOrigin = time;
            }

            samples[sampleCount] = new BladePoseSample(
                sample.FrameId,
                time - recordingOrigin,
                sample.GripPosition,
                sample.GripRotation,
                sample.TrackingState);
            viewForwards[sampleCount] = viewForward;
            sampleCount++;
            lastRecordedTime = time;

            if (sampleCount >= Capacity)
            {
                recording = false;
            }

            return true;
        }

        /// <summary>
        /// Starts replaying from the first recorded sample, on a clock that
        /// begins at <paramref name="nowSeconds"/>. Ends any recording or
        /// replay result first. False when there is nothing to replay.
        /// </summary>
        internal bool TryBeginReplay(double nowSeconds)
        {
            Stop();

            if (sampleCount == 0 || double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
            {
                return false;
            }

            replayIndex = 0;
            replayClockStart = nowSeconds;
            lastReplayTime = nowSeconds;
            replaying = true;
            if (katana != null)
            {
                katana.LiveInputEnabled = false;
            }

            return true;
        }

        /// <summary>
        /// The next recorded sample, on the replay clock. When the recording
        /// has already been fed through, this ends the replay and returns
        /// false; input stays with the replay so its result can be watched.
        /// </summary>
        internal bool TryTakeNextReplaySample(long frameId, out BladePoseSample sample)
        {
            return TryTakeNextReplaySample(frameId, out sample, out _);
        }

        /// <summary>The same, with the view forward the sample was recorded with.</summary>
        internal bool TryTakeNextReplaySample(long frameId, out BladePoseSample sample, out Vector3 viewForward)
        {
            sample = default;
            viewForward = Vector3.zero;

            if (!replaying)
            {
                return false;
            }

            if (replayIndex >= sampleCount)
            {
                replaying = false;
                return false;
            }

            BladePoseSample recorded = samples[replayIndex];
            viewForward = viewForwards[replayIndex];
            replayIndex++;
            lastReplayTime = replayClockStart + recorded.TimestampSeconds;
            sample = new BladePoseSample(
                frameId,
                lastReplayTime,
                recorded.GripPosition,
                recorded.GripRotation,
                recorded.TrackingState);
            return true;
        }

        /// <summary>
        /// While a replay's result is shown, the sample that keeps the katana's
        /// waves moving: untracked, because the recording has no more poses,
        /// and <paramref name="deltaSeconds"/> further along the replay clock.
        /// A delta that is not positive and finite leaves the clock where it
        /// is. False when no replay result is shown.
        ///
        /// The tick only lets the waves advance and expire. It is not a
        /// recorded pose and does not continue the recording.
        /// </summary>
        internal bool TryTakeReplayResultTick(long frameId, double deltaSeconds, out BladePoseSample sample)
        {
            sample = default;

            if (!IsShowingReplayResult)
            {
                return false;
            }

            if (double.IsFinite(deltaSeconds) && deltaSeconds > 0.0)
            {
                lastReplayTime += deltaSeconds;
            }

            sample = new BladePoseSample(frameId, lastReplayTime, Vector3.zero, Quaternion.identity, BladeTrackingState.None);
            return true;
        }

        /// <summary>
        /// Ends a recording, a replay or a shown replay result, keeping what
        /// was recorded, and hands input back to the controller.
        /// </summary>
        internal void Stop()
        {
            recording = false;
            replaying = false;

            // Starts the katana over only if a replay still held its input.
            if (katana != null)
            {
                katana.LiveInputEnabled = true;
            }
        }

        /// <summary>Ends whatever is running and forgets the recording and the pin.</summary>
        internal void Clear()
        {
            Stop();
            ClearPin();
            sampleCount = 0;
            replayIndex = 0;
        }

        /// <summary>
        /// Pins what the katana shows right now, replacing any earlier pin.
        /// Only reads the katana. False, leaving the pin as it was, when no
        /// katana is assigned.
        /// </summary>
        internal bool TryPinCurrent()
        {
            if (katana == null)
            {
                return false;
            }

            int waveCount = Mathf.Min(katana.WaveCount, SandboxSlashWaveStore.Capacity);
            int pinned = 0;
            for (int i = 0; i < waveCount; i++)
            {
                if (!katana.TryGetWave(i, out double latchedAt, out _, out _, out _, out _, out float acceptedSpan,
                        out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd))
                {
                    continue;
                }

                bool closed = katana.TryGetWaveSpanClose(i, out double closedAt, out _, out _);
                pinnedLatchedAt[pinned] = latchedAt;
                pinnedAcceptedSpan[pinned] = acceptedSpan;
                pinnedSpanClosed[pinned] = closed;
                pinnedSpanClosedAt[pinned] = closed ? closedAt : double.NaN;
                pinnedSegmentStart[pinned] = segmentStart;
                pinnedSegmentEnd[pinned] = segmentEnd;
                pinned++;
            }

            pinnedWaveCount = pinned;
            pinnedAcceptedSampleCount = katana.AcceptedSampleCount;
            pinnedLatchReady = katana.IsLatchReady;
            pinnedCandidateName = CandidateName;
            hasPin = true;
            return true;
        }

        /// <summary>Forgets the pinned result.</summary>
        internal void ClearPin()
        {
            hasPin = false;
            pinnedWaveCount = 0;
            pinnedAcceptedSampleCount = 0;
            pinnedLatchReady = false;
            pinnedCandidateName = null;
        }

        /// <summary>
        /// One wave of the pinned result. <paramref name="spanClosedAt"/> is
        /// NaN while the span was still open when pinned. False when nothing
        /// is pinned or the index is out of range.
        /// </summary>
        internal bool TryGetPinnedWave(
            int index,
            out double latchedAt,
            out float acceptedSpan,
            out bool spanClosed,
            out double spanClosedAt,
            out Vector3 segmentStart,
            out Vector3 segmentEnd)
        {
            if (!hasPin || index < 0 || index >= pinnedWaveCount)
            {
                latchedAt = double.NaN;
                acceptedSpan = 0f;
                spanClosed = false;
                spanClosedAt = double.NaN;
                segmentStart = Vector3.zero;
                segmentEnd = Vector3.zero;
                return false;
            }

            latchedAt = pinnedLatchedAt[index];
            acceptedSpan = pinnedAcceptedSpan[index];
            spanClosed = pinnedSpanClosed[index];
            spanClosedAt = pinnedSpanClosedAt[index];
            segmentStart = pinnedSegmentStart[index];
            segmentEnd = pinnedSegmentEnd[index];
            return true;
        }

        /// <summary>
        /// Writes the current result above the pinned one. Current is read from
        /// the katana as it is now; Pinned is only ever the copy. Nothing is
        /// compared or judged here -- that is left to whoever reads it.
        /// </summary>
        internal void AppendComparison(StringBuilder text)
        {
            text.Append("Candidate  ").Append(CandidateName).Append(" (only implemented option)\n");

            text.Append("Current  ");
            if (katana == null)
            {
                text.Append("no katana\n");
            }
            else
            {
                int waveCount = katana.WaveCount;
                AppendResultHeader(text, waveCount, katana.AcceptedSampleCount, katana.IsLatchReady, CandidateName);
                for (int i = 0; i < waveCount; i++)
                {
                    if (!katana.TryGetWave(i, out double latchedAt, out _, out _, out _, out _, out float acceptedSpan,
                            out _, out _, out Vector3 segmentStart, out Vector3 segmentEnd))
                    {
                        continue;
                    }

                    bool closed = katana.TryGetWaveSpanClose(i, out double closedAt, out _, out _);
                    AppendWave(text, i, latchedAt, acceptedSpan, closed, closedAt, segmentStart, segmentEnd);
                }
            }

            text.Append("Pinned   ");
            if (!hasPin)
            {
                text.Append("none\n");
                return;
            }

            AppendResultHeader(text, pinnedWaveCount, pinnedAcceptedSampleCount, pinnedLatchReady, pinnedCandidateName);
            for (int i = 0; i < pinnedWaveCount; i++)
            {
                if (TryGetPinnedWave(i, out double latchedAt, out float acceptedSpan, out bool closed,
                        out double closedAt, out Vector3 segmentStart, out Vector3 segmentEnd))
                {
                    AppendWave(text, i, latchedAt, acceptedSpan, closed, closedAt, segmentStart, segmentEnd);
                }
            }
        }

        private static void AppendResultHeader(
            StringBuilder text, int waveCount, int acceptedSampleCount, bool latchReady, string candidateName)
        {
            text.Append("waves ").Append(waveCount)
                .Append("  accepted ").Append(acceptedSampleCount)
                .Append("  latch ").Append(latchReady ? "ready" : "waiting")
                .Append("  (").Append(candidateName).Append(")\n");
        }

        private static void AppendWave(
            StringBuilder text, int index, double latchedAt, float acceptedSpan, bool closed, double closedAt,
            Vector3 segmentStart, Vector3 segmentEnd)
        {
            text.Append("  #").Append(index).Append("  latched ");
            AppendSeconds(text, latchedAt);
            text.Append("  span ");
            AppendMetres(text, acceptedSpan);
            text.Append("  ");
            if (closed)
            {
                text.Append("closed at ");
                AppendSeconds(text, closedAt);
            }
            else
            {
                text.Append("open");
            }

            text.Append("\n      A ");
            AppendPoint(text, segmentStart);
            text.Append("  B ");
            AppendPoint(text, segmentEnd);
            text.Append('\n');
        }

        private static void AppendSeconds(StringBuilder text, double seconds)
        {
            text.Append(seconds.ToString("F3", CultureInfo.InvariantCulture)).Append(" s");
        }

        private static void AppendMetres(StringBuilder text, float metres)
        {
            text.Append(metres.ToString("F3", CultureInfo.InvariantCulture)).Append(" m");
        }

        private static void AppendPoint(StringBuilder text, Vector3 point)
        {
            text.Append('(')
                .Append(point.x.ToString("F2", CultureInfo.InvariantCulture)).Append(", ")
                .Append(point.y.ToString("F2", CultureInfo.InvariantCulture)).Append(", ")
                .Append(point.z.ToString("F2", CultureInfo.InvariantCulture)).Append(')');
        }

        private void Update()
        {
            TryAutoBeginCapture();

            if (recording)
            {
                TryAppendRecordedSample(
                    SandboxRightHandKatana.ReadRightHandGripSample(),
                    katana != null ? katana.CurrentViewForward : Vector3.zero);
                return;
            }

            // One sample per Update and no catching up: the recorded offsets
            // keep the order and spacing, not wall-clock timing.
            if (replaying)
            {
                if (TryTakeNextReplaySample(Time.frameCount, out BladePoseSample replayed, out Vector3 recordedView)
                    && katana != null)
                {
                    katana.TryRecordSample(replayed, recordedView);
                }

                return;
            }

            // A shown replay result implies an assigned katana.
            if (TryTakeReplayResultTick(Time.frameCount, Time.unscaledDeltaTime, out BladePoseSample tick))
            {
                katana.TryRecordSample(tick, Vector3.zero);
            }
        }

        // After every Update, so a latch made this frame -- live or replayed --
        // is seen in the same frame, with its stroke and begin still present.
        private void LateUpdate()
        {
            if (ObserveNewLatch() && autoDumpOnLatch)
            {
                LogSlashDump();
            }
        }

        private void OnDisable()
        {
            EndCapture();
            // Never leave the katana deaf to the controller.
            Stop();
        }

        /// <summary>
        /// Writes a one-off readout of the katana's slash state: the tuning
        /// values, each accepted sample's time and cut sample point, the
        /// stroke's begin, and every live wave's latch, close and current
        /// segment. Only reads the katana. Bounded by its fixed capacities --
        /// at most eight accepted samples and four waves.
        /// </summary>
        internal void AppendSlashDump(StringBuilder text)
        {
            text.Append("Slash dump (development output; not a saved or stable format)\n");
            if (katana == null)
            {
                text.Append("No katana assigned.\n");
                return;
            }

            text.Append("Tuning  min speed ");
            AppendNumber(text, katana.MinimumSpeed);
            text.Append("  min displacement ");
            AppendNumber(text, katana.MinimumDisplacement);
            text.Append("  min edge lead ");
            AppendNumber(text, katana.MinimumEdgeLeadScore);
            text.Append("  return edge lead ");
            AppendNumber(text, katana.ReturnStrokeEdgeLeadScore);
            text.Append("  latch chord ");
            AppendNumber(text, katana.LatchChordMetres);
            text.Append("  span capture ");
            AppendNumber(text, katana.SpanCaptureTimeoutSeconds);
            text.Append("  begin view dot ");
            AppendNumber(text, katana.BeginBladeAxisViewDotMinimum);
            text.Append('\n');

            Vector3 view = katana.CurrentViewForward;
            if (view == Vector3.zero)
            {
                text.Append("View  no reference\n");
            }
            else
            {
                text.Append("View  forward ");
                AppendVector(text, view);
                text.Append("  elevation ");
                AppendNumber(text, ElevationDegrees(view));
                text.Append(" deg");
                Transform shownKatana = katana.Katana;
                if (shownKatana != null && shownKatana.gameObject.activeSelf)
                {
                    text.Append("  blade now ");
                    AppendVector(text, shownKatana.forward);
                    text.Append("  elevation ");
                    AppendNumber(text, ElevationDegrees(shownKatana.forward));
                    text.Append(" deg  dot ");
                    AppendNumber(text, Vector3.Dot(shownKatana.forward, view.normalized));
                }

                text.Append('\n');
            }

            int acceptedCount = katana.AcceptedSampleCount;
            text.Append("Accepted ").Append(acceptedCount)
                .Append("  latch ").Append(katana.IsLatchReady ? "ready" : "waiting").Append('\n');
            for (int i = 0; i < acceptedCount; i++)
            {
                if (!katana.TryGetAcceptedSample(i, out EvaluatedBladePose accepted))
                {
                    continue;
                }

                text.Append("  #").Append(i).Append("  t ");
                AppendNumber(text, accepted.TimestampSeconds);
                text.Append("  cut ");
                AppendVector(text, accepted.CutSamplePosition);
                text.Append('\n');
            }

            if (katana.TryGetStrokeBeginSample(out EvaluatedBladePose begin))
            {
                text.Append("Begin  t ");
                AppendNumber(text, begin.TimestampSeconds);
                text.Append("  cut ");
                AppendVector(text, begin.CutSamplePosition);
                text.Append("  blade axis ");
                AppendVector(text, begin.BladeAxis);
                text.Append("  elevation ");
                AppendNumber(text, ElevationDegrees(begin.BladeAxis));
                text.Append(" deg");
                Vector3 beginView = katana.StrokeBeginViewForward;
                if (beginView == Vector3.zero)
                {
                    text.Append("  view none");
                }
                else
                {
                    text.Append("  view ");
                    AppendVector(text, beginView);
                    text.Append("  dot ");
                    AppendNumber(text, Vector3.Dot(begin.BladeAxis, beginView));
                }

                text.Append('\n');
            }
            else
            {
                text.Append("Begin  none\n");
            }

            int waveCount = katana.WaveCount;
            text.Append("Waves ").Append(waveCount).Append('\n');
            for (int i = 0; i < waveCount; i++)
            {
                if (!katana.TryGetWave(i, out double latchedAt, out Plane plane, out _, out Vector3 travelAxis,
                        out Vector3 spanAxis, out float acceptedSpan, out _, out _, out Vector3 segmentStart,
                        out Vector3 segmentEnd))
                {
                    continue;
                }

                text.Append("  #").Append(i).Append("  latched t ");
                AppendNumber(text, latchedAt);
                text.Append("  travel ");
                AppendVector(text, travelAxis);
                text.Append("  span axis ");
                AppendVector(text, spanAxis);
                text.Append("  accepted span ");
                AppendNumber(text, acceptedSpan);
                text.Append('\n');

                if (katana.TryGetWaveSpanClose(i, out double closedAt, out Vector3 frozenOrigin, out Vector3 frozenDirection))
                {
                    text.Append("      closed t ");
                    AppendNumber(text, closedAt);
                    text.Append("  frozen guide origin ");
                    AppendVector(text, frozenOrigin);
                    text.Append("  direction ");
                    AppendVector(text, frozenDirection);
                    text.Append('\n');

                    // The frozen guide against the current segment start: the
                    // same terms the store evaluates. Shown, never fed back.
                    text.Append("      frozen guide  r ");
                    if (SandboxSlashWaveStore.TryEvaluateRawSpanTerms(plane.normal, segmentStart, spanAxis, frozenOrigin,
                            frozenDirection, out float r, out float q, out float denominator))
                    {
                        AppendNumber(text, r);
                        text.Append("  q ");
                        AppendNumber(text, q);
                        text.Append("  denominator ");
                        text.Append(denominator.ToString("F5", CultureInfo.InvariantCulture));
                        text.Append("  usable ").Append(SandboxSlashWaveStore.IsUsableRawSpan(r, q, denominator) ? "yes" : "no");
                    }
                    else
                    {
                        text.Append("n/a (not finite)");
                    }

                    text.Append("  span-guide ");
                    AppendNumber(text, Vector3.Angle(spanAxis, frozenDirection));
                    text.Append(" deg  travel-guide ");
                    AppendNumber(text, Vector3.Angle(travelAxis, frozenDirection));
                    text.Append(" deg\n");
                }
                else
                {
                    text.Append("      open (its live guide is not kept)\n");
                }

                text.Append("      current A ");
                AppendVector(text, segmentStart);
                text.Append("  B ");
                AppendVector(text, segmentEnd);
                text.Append("  |B-A| ");
                AppendNumber(text, Vector3.Distance(segmentStart, segmentEnd));
                text.Append('\n');
            }

            text.Append("Raw span terms: closed waves only, from the frozen guide at the current segment start\n");
        }

        /// <summary>
        /// Whether the katana's newest wave is one not seen by the previous
        /// call: true once per latch, false for expiries and for a stroke that
        /// just goes on. Only reads the katana.
        /// </summary>
        internal bool ObserveNewLatch()
        {
            int waveCount = katana != null ? katana.WaveCount : 0;
            if (waveCount == 0
                || !katana.TryGetWave(waveCount - 1, out double newestLatchedAt, out _, out _, out _, out _, out _,
                    out _, out _, out _, out _))
            {
                lastSeenNewestLatchedAt = double.NaN;
                return false;
            }

            if (newestLatchedAt.Equals(lastSeenNewestLatchedAt))
            {
                return false;
            }

            lastSeenNewestLatchedAt = newestLatchedAt;
            return true;
        }

        /// <summary>Logs <see cref="AppendSlashDump"/> once to the Unity console.</summary>
        internal void LogSlashDump()
        {
            dumpText.Clear();
            AppendSlashDump(dumpText);
            Debug.Log(dumpText.ToString());
        }

        // Degrees above the horizontal; zero for a zero vector.
        private static float ElevationDegrees(Vector3 direction)
        {
            Vector3 unit = direction.normalized;
            return Mathf.Asin(Mathf.Clamp(unit.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private static void AppendNumber(StringBuilder text, double value)
        {
            text.Append(value.ToString("F3", CultureInfo.InvariantCulture));
        }

        private static void AppendVector(StringBuilder text, Vector3 value)
        {
            text.Append('(')
                .Append(value.x.ToString("F3", CultureInfo.InvariantCulture)).Append(", ")
                .Append(value.y.ToString("F3", CultureInfo.InvariantCulture)).Append(", ")
                .Append(value.z.ToString("F3", CultureInfo.InvariantCulture)).Append(')');
        }

        // A value the setter refuses -- at a bound, say -- leaves the value
        // as it was, so pressing further just does nothing.
        private static void DrawTuningControls(SandboxRightHandKatana target)
        {
            int step = TuningRow("Min speed       ", target.MinimumSpeed, " m/s");
            if (step != 0)
            {
                target.TrySetMinimumSpeed(Snap(target.MinimumSpeed + step * SpeedStep));
            }

            step = TuningRow("Min displacement", target.MinimumDisplacement, " m");
            if (step != 0)
            {
                target.TrySetMinimumDisplacement(Snap(target.MinimumDisplacement + step * DistanceStep));
            }

            step = TuningRow("Min edge lead   ", target.MinimumEdgeLeadScore, string.Empty);
            if (step != 0)
            {
                target.TrySetMinimumEdgeLeadScore(Snap(target.MinimumEdgeLeadScore + step * ScoreStep));
            }

            step = TuningRow("Return edge lead", target.ReturnStrokeEdgeLeadScore, string.Empty);
            if (step != 0)
            {
                target.TrySetReturnStrokeEdgeLeadScore(Snap(target.ReturnStrokeEdgeLeadScore + step * ScoreStep));
            }

            step = TuningRow("Latch chord     ", target.LatchChordMetres, " m");
            if (step != 0)
            {
                target.TrySetLatchChordMetres(Snap(target.LatchChordMetres + step * DistanceStep));
            }

            step = TuningRow("Span capture    ", target.SpanCaptureTimeoutSeconds, " s");
            if (step != 0)
            {
                target.TrySetSpanCaptureTimeoutSeconds(Snap(target.SpanCaptureTimeoutSeconds + step * TimeoutStep));
            }

            step = TuningRow("Begin view dot  ", target.BeginBladeAxisViewDotMinimum, string.Empty);
            if (step != 0)
            {
                target.TrySetBeginBladeAxisViewDotMinimum(Snap(target.BeginBladeAxisViewDotMinimum + step * DotStep));
            }
        }

        // One value with - and + buttons. Returns -1, 0 or +1.
        private static int TuningRow(string label, float value, string unit)
        {
            int step = 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + "  " + value.ToString("F3", CultureInfo.InvariantCulture) + unit, GUILayout.Width(300f));
            if (GUILayout.Button("-", GUILayout.Width(48f)))
            {
                step = -1;
            }

            if (GUILayout.Button("+", GUILayout.Width(48f)))
            {
                step = 1;
            }

            GUILayout.EndHorizontal();
            return step;
        }

        // Keeps repeated steps on round values instead of drifting.
        private static float Snap(float value)
        {
            return Mathf.Round(value * 1000f) / 1000f;
        }

        /// <summary>
        /// The capture's own controls and, just as importantly, its result. A
        /// save that failed has to be visible here: the console scrolls away,
        /// and on this machine <c>Editor.log</c> is overwritten on the next
        /// launch, which is how earlier device dumps were lost.
        /// </summary>
        private void DrawCaptureControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Capture", GUILayout.Width(60f));
            bool canStart = !capture.IsCapturing && !capture.HasUnsavedRows;
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && canStart;
            if (GUILayout.Button(capture.IsCapturing ? "Capturing..." : "Start (resets state)"))
            {
                BeginCapture();
            }

            GUI.enabled = wasEnabled;
            if (GUILayout.Button("End"))
            {
                EndCapture();
            }

            if (GUILayout.Button("Save"))
            {
                SaveCapture();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("run id", GUILayout.Width(60f));
            captureRunId = GUILayout.TextField(captureRunId ?? string.Empty, 40, GUILayout.Width(150f));
            GUILayout.Label("note", GUILayout.Width(40f));
            captureNote = GUILayout.TextField(captureNote ?? string.Empty, 200);
            GUILayout.EndHorizontal();

            GUILayout.Label(
                "  " + capture.EndedBy + "  " + capture.UpdateCount + " updates (at most "
                + capture.MaximumUpdates + ")" + (capture.HasUnsavedRows ? "  (not saved)" : string.Empty));
            if (capture.EndingDetail.Length > 0)
            {
                GUILayout.Label("  ended: " + capture.EndingDetail);
            }

            GUILayout.Label("  to " + SandboxSlashCapture.RootDirectory);
            GUILayout.Label("  the run id and note are read when you press Save, not at Start");
            if (autoCaptureWhenIdle)
            {
                GUILayout.Label("  auto capture is on: End and Save are the only presses needed");
            }
            if (lastSaveMessage.Length > 0)
            {
                GUILayout.Label(lastSaveMessage);
            }
        }

        /// <summary>
        /// Starts a capture, with the katana's current tuning and geometry as
        /// the run's conditions.
        /// </summary>
        internal bool BeginCapture()
        {
            if (katana == null)
            {
                lastSaveMessage = "  CAPTURE NOT STARTED: no katana assigned";
                return false;
            }

            if (recording || replaying || !katana.LiveInputEnabled)
            {
                lastSaveMessage = "  CAPTURE NOT STARTED: stop the pose recording or replay first";
                return false;
            }

            // Starting again would discard rows nobody has written down. A
            // capture is only replaced once it has been saved.
            if (capture.IsCapturing)
            {
                lastSaveMessage = "  CAPTURE NOT STARTED: already capturing -- End it first";
                return false;
            }

            if (capture.HasUnsavedRows)
            {
                lastSaveMessage = "  CAPTURE NOT STARTED: " + capture.UpdateCount
                    + " captured updates are not saved yet -- Save them first";
                return false;
            }

            // The calculation goes back to a known initial state first. A
            // capture started midway would otherwise record input whose result
            // depends on a history and on waves that were never saved, and a
            // replay of the file could not reproduce it. Waves on screen
            // disappear, because they are part of that discarded state.
            katana.ResetToKnownState();

            capture.Begin(captureRunId, katana.CaptureConditions);
            capture.Note = captureNote;
            katana.Capture = capture;
            lastSaveMessage = "  capturing from a reset state; any wave on screen was cleared."
                + " Nothing is written until Save";
            return true;
        }

        /// <summary>Ends a capture without saving it. The rows stay until the next start.</summary>
        internal void EndCapture()
        {
            capture.Stop();
            if (katana != null)
            {
                katana.Capture = null;
            }
        }

        /// <summary>
        /// Writes the capture and reports where, or why not. The result is left
        /// on screen either way.
        /// </summary>
        internal bool SaveCapture()
        {
            EndCapture();

            // Read here, not at the start: the operator takes the headset off
            // before saving, and that is the first moment they can name the run
            // and write down what they saw.
            capture.RunId = captureRunId;
            capture.Note = captureNote;
            if (capture.TrySave(out string directory, out string failure))
            {
                lastSaveMessage = "  SAVED " + capture.UpdateCount + " updates to " + directory;
                Debug.Log("SandboxSlashCapture: saved " + capture.UpdateCount + " updates to " + directory);
                return true;
            }

            lastSaveMessage = "  SAVE FAILED: " + failure;
            Debug.LogError("SandboxSlashCapture: save failed: " + failure);
            return false;
        }

        /// <summary>
        /// Starts a capture by itself when one can be started. Pressing Start
        /// is awkward with a headset in hand, so the capture arms itself: at
        /// play start, and again after each successful save.
        /// <para>
        /// It never starts while a capture is running or while rows are waiting
        /// to be saved, so it cannot discard anything -- and because a start
        /// resets the calculation, that is also what keeps the reset away from
        /// a session whose rows are not yet on disk.
        /// </para>
        /// </summary>
        internal bool TryAutoBeginCapture()
        {
            if (!autoCaptureWhenIdle || katana == null || !katana.isActiveAndEnabled
                || !katana.LiveInputEnabled || recording || replaying
                || capture.IsCapturing || capture.HasUnsavedRows)
            {
                return false;
            }

            string previousMessage = lastSaveMessage;
            bool started = BeginCapture();
            // The new run's state is already shown separately. Keep the last
            // save outcome visible long enough for the operator to read it.
            if (started && previousMessage.Length > 0)
                lastSaveMessage = previousMessage;
            return started;
        }

        /// <summary>Whether a capture arms itself when idle.</summary>
        internal bool AutoCaptureWhenIdle
        {
            get => autoCaptureWhenIdle;
            set => autoCaptureWhenIdle = value;
        }

        /// <summary>The capture this recorder owns, for tests.</summary>
        internal SandboxSlashCapture CaptureForTests => capture;

        /// <summary>The last save's outcome as the operator sees it, for tests.</summary>
        internal string LastSaveMessage => lastSaveMessage;

        /// <summary>The run identifier the next capture is named with.</summary>
        internal string CaptureRunId
        {
            get => captureRunId;
            set => captureRunId = value ?? string.Empty;
        }

        /// <summary>The operator's comment saved with the capture.</summary>
        internal string CaptureNote
        {
            get => captureNote;
            set => captureNote = value ?? string.Empty;
        }

        private void OnGUI()
        {
            const float Width = 560f;
            GUILayout.BeginArea(new Rect(Mathf.Max(0f, Screen.width - Width - 10f), 10f, Width, Mathf.Max(0f, Screen.height - 20f)));
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(
                "Pose replay  "
                + (recording ? "recording" : replaying ? "replaying" : IsShowingReplayResult ? "replay done" : "idle")
                + "  " + sampleCount + " / " + Capacity + "  index " + replayIndex);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Record"))
            {
                BeginRecording();
            }

            if (GUILayout.Button("Stop"))
            {
                Stop();
            }

            if (GUILayout.Button("Play"))
            {
                TryBeginReplay(Time.unscaledTimeAsDouble);
            }

            if (GUILayout.Button("Clear"))
            {
                Clear();
            }

            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Pin Current"))
            {
                TryPinCurrent();
            }

            if (GUILayout.Button("Clear Pin"))
            {
                ClearPin();
            }

            if (GUILayout.Button("Dump"))
            {
                LogSlashDump();
            }

            autoDumpOnLatch = GUILayout.Toggle(autoDumpOnLatch, "Auto dump on latch");

            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            autoCaptureWhenIdle = GUILayout.Toggle(
                autoCaptureWhenIdle, "Auto capture (starts itself at play start and after each save)");
            GUILayout.EndHorizontal();

            DrawCaptureControls();

            if (katana != null)
            {
                DrawTuningControls(katana);
            }

            comparisonText.Clear();
            AppendComparison(comparisonText);
            GUILayout.Label(comparisonText.ToString());
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }
}
