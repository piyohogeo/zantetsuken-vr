using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Development tool that records a few seconds of the right-hand grip
    /// samples the sandbox katana sees and feeds the same sequence back into
    /// it, so a swing can be run through the current slash pipeline again.
    ///
    /// Everything lives in one fixed array in memory: nothing is saved, traced
    /// or turned into a preset, and a recording that fills the array simply
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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxSlashPoseRecorder : MonoBehaviour
    {
        /// <summary>
        /// Fixed recording length in samples: about 5.7 s at the 90 Hz Quest
        /// Link mode.
        /// </summary>
        internal const int Capacity = 512;

        [Tooltip("The katana whose grip samples are recorded and replayed.")]
        [SerializeField] private SandboxRightHandKatana katana;

        // Samples as they came, except that TimestampSeconds holds the offset
        // from the first recorded sample. Allocated once.
        private readonly BladePoseSample[] samples = new BladePoseSample[Capacity];
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

        /// <summary>Starts a fresh recording, ending any replay first.</summary>
        internal void BeginRecording()
        {
            Stop();
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
            sample = default;

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

        /// <summary>Ends whatever is running and forgets the recording.</summary>
        internal void Clear()
        {
            Stop();
            sampleCount = 0;
            replayIndex = 0;
        }

        private void Update()
        {
            if (recording)
            {
                TryAppendRecordedSample(SandboxRightHandKatana.ReadRightHandGripSample());
                return;
            }

            // One sample per Update and no catching up: the recorded offsets
            // keep the order and spacing, not wall-clock timing.
            if (replaying)
            {
                if (TryTakeNextReplaySample(Time.frameCount, out BladePoseSample replayed) && katana != null)
                {
                    katana.TryRecordSample(replayed);
                }

                return;
            }

            // A shown replay result implies an assigned katana.
            if (TryTakeReplayResultTick(Time.frameCount, Time.unscaledDeltaTime, out BladePoseSample tick))
            {
                katana.TryRecordSample(tick);
            }
        }

        private void OnDisable()
        {
            // Never leave the katana deaf to the controller.
            Stop();
        }

        private void OnGUI()
        {
            const float Width = 340f;
            GUILayout.BeginArea(new Rect(Mathf.Max(0f, Screen.width - Width - 10f), 10f, Width, 64f), GUI.skin.box);
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
            GUILayout.EndArea();
        }
    }
}
