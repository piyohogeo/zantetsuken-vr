using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A development capture of what the slash path was actually given, so that
    /// a later comparison can recompute it instead of guessing.
    /// <para>
    /// The existing dump writes a summary of one instant to the Unity console.
    /// That was enough to watch a swing and not enough to recompute one: the
    /// console goes to <c>Editor.log</c>, which Unity overwrites on the next
    /// launch, and a summary holds no time series. What a recomputation needs
    /// is the ordered input this component's own choke point received --
    /// <see cref="SandboxRightHandKatana.TryRecordSample"/> takes one pose
    /// sample and one view forward per update and everything else is derived
    /// from them -- together with the tuning and geometry those updates ran
    /// under. That is what this captures.
    /// </para>
    /// <para>
    /// Rejected input is kept. A sample the gate turned away still steers the
    /// live guide, and a sample the history refuses resets the stroke, so a
    /// capture of accepted samples only would not reproduce the run. Each row
    /// therefore records the input as given and whether the update accepted it.
    /// </para>
    /// <para>
    /// Nothing is written while capturing: the lists grow up to a bounded
    /// update count, and files are written when the operator saves. At the limit,
    /// capturing stops and says so -- no row is dropped to make room for a
    /// newer one, because a run with a hole in the middle cannot be recomputed
    /// and would not look any different afterwards.
    /// </para>
    /// </summary>
    internal sealed class SandboxSlashCapture
    {
        /// <summary>
        /// How many updates a capture will hold before it stops.
        /// <para>
        /// This used to be a fixed 8192, which was sized on a wrong reading of
        /// the update rate: the device runs at 89.9 Hz, so 8192 updates is 91
        /// seconds, and a capture that arms itself when play starts spends that
        /// budget while the operator is still putting the headset on. The rows
        /// now grow as they are needed and this is only an upper bound -- about
        /// an hour and a half of updates -- kept so that a capture nobody ended
        /// cannot grow without limit.
        /// </para>
        /// </summary>
        internal const int DefaultMaximumUpdates = 500000;

        /// <summary>Where captures go. One directory per run, never reused.</summary>
        internal const string RootDirectory = @"C:\log\zantetsuken-vr\SlashSpan";

        /// <summary>
        /// The root this capture writes under. <see cref="RootDirectory"/> in
        /// use; a test points it elsewhere to check that a root which cannot be
        /// written to is reported as a failure rather than passing silently.
        /// </summary>
        internal string Root { get; set; } = RootDirectory;

        /// <summary>
        /// The upper bound on updates for this capture. <see
        /// cref="DefaultMaximumUpdates"/> in use; a test lowers it so the
        /// stop-when-full behaviour can be exercised without capturing half a
        /// million updates.
        /// </summary>
        internal int MaximumUpdates { get; set; } = DefaultMaximumUpdates;

        /// <summary>How a capture came to an end, which the operator has to be able to see.</summary>
        internal enum Ending
        {
            /// <summary>Never started.</summary>
            None,

            /// <summary>Still capturing.</summary>
            Capturing,

            /// <summary>The operator stopped it.</summary>
            Stopped,

            /// <summary>The arrays filled and capturing stopped there. The run is truncated, not holed.</summary>
            BufferFull,

            /// <summary>
            /// The tuning or geometry changed while capturing. A capture holds
            /// one set of conditions, and updates run under a second set could
            /// not be recomputed from it, so capturing ends at the boundary:
            /// the first update under the changed conditions is not kept.
            /// </summary>
            ConditionsChanged,
        }

        /// <summary>One update's input, as the choke point received it.</summary>
        private struct InputRow
        {
            public long FrameId;
            public double TimestampSeconds;
            public Vector3 GripPosition;
            public Quaternion GripRotation;
            public BladeTrackingState TrackingState;
            public Vector3 ViewForward;
            public bool Accepted;
            public int AcceptedSampleCount;
            public int WaveObservationStart;
            public int WaveObservationCount;
        }

        /// <summary>
        /// One wave as it stood after one update: the latch snapshot, the
        /// segment, the close state and the terms of the candidate that update
        /// evaluated. Enough to check a recomputation term by term.
        /// </summary>
        private struct WaveRow
        {
            public int WaveIndex;
            public double LatchedAt;
            public Vector3 PlaneNormal;
            public float PlaneDistance;
            public Vector3 WaveOrigin;
            public Vector3 TravelAxis;
            public Vector3 SpanAxis;
            public float AcceptedSpan;
            public Vector3 PreviousSegmentStart;
            public Vector3 PreviousSegmentEnd;
            public Vector3 CurrentSegmentStart;
            public Vector3 CurrentSegmentEnd;
            public bool SpanClosed;
            public double SpanClosedAt;
            public Vector3 FrozenGuideOrigin;
            public Vector3 FrozenGuideDirection;
            public bool CandidateEvaluated;
            public bool CandidateFromFrozenGuide;
            public Vector3 CandidateGuideOrigin;
            public Vector3 CandidateGuideDirection;
            public float RawSpan;
            public float Q;
            public float Denominator;
            public bool TermsFinite;
            public bool CandidateUsable;
            public bool CandidateWidenedSpan;
        }

        /// <summary>
        /// The values the captured updates ran under. Every one of them is an
        /// input to the recomputation, not an observation of it.
        /// </summary>
        internal struct Conditions
        {
            public float MinimumSpeed;
            public float MinimumDisplacement;
            public float MinimumEdgeLeadScore;
            public float ReturnStrokeEdgeLeadScore;
            public float LatchChordMetres;
            public float SpanCaptureTimeoutSeconds;
            public float BeginBladeAxisViewDotMinimum;
            public float BladeLength;
            public Vector3 GripOffsetPosition;
            public Quaternion GripOffsetRotation;
            public float EmissionControlPointRatio;
            public float CutSampleRatio;
            public float WaveSpeed;
            public float WaveLifetimeSeconds;
            public float NearParallelDenominator;
            public int WaveCapacity;
        }

        // Lists start with modest capacity and grow up to the update limit.
        // Wave rows are sparse -- a
        // measured run held 135 of them across 1973 updates -- so they cost far
        // less than one row per wave slot per update would.
        private readonly List<InputRow> inputs = new List<InputRow>(4096);
        private readonly List<WaveRow> waveRows = new List<WaveRow>(1024);
        private Ending ending = Ending.None;
        private Conditions conditions;
        private string runId = string.Empty;
        private string note = string.Empty;
        private DateTime startedUtc;
        private double firstTimestamp;
        private bool saved;

        /// <summary>Whether updates are being captured right now.</summary>
        internal bool IsCapturing => ending == Ending.Capturing;

        /// <summary>How the capture ended, or that it has not.</summary>
        internal Ending EndedBy => ending;

        /// <summary>Updates captured so far.</summary>
        internal int UpdateCount => inputs.Count;

        /// <summary>
        /// The identifier this run's directory is named with. Settable until
        /// the run is saved, because that is when the name is decided: the
        /// operator cannot type while wearing the headset, so they name the run
        /// after the swing rather than before it. The time in the name is still
        /// the time the capture began.
        /// </summary>
        internal string RunId
        {
            get => runId;
            set => runId = Sanitise(value);
        }

        /// <summary>The operator's own comment, saved with the run.</summary>
        internal string Note
        {
            get => note;
            set => note = value ?? string.Empty;
        }

        /// <summary>
        /// Starts a capture, discarding anything held from a previous one. The
        /// conditions are taken now and never revised: if the tuning or the
        /// geometry changes while capturing, <see cref="Append"/> ends the
        /// capture at that boundary with <see cref="Ending.ConditionsChanged"/>
        /// and does not keep the first update under the changed conditions, so
        /// everything the run holds ran under the one set saved with it.
        /// </summary>
        internal void Begin(string id, in Conditions runConditions)
        {
            inputs.Clear();
            waveRows.Clear();
            firstTimestamp = double.NaN;
            conditions = runConditions;
            runId = Sanitise(id);
            startedUtc = DateTime.UtcNow;
            ending = Ending.Capturing;
            EndingDetail = string.Empty;
            saved = false;
        }

        /// <summary>Stops capturing at the operator's word. Held rows stay.</summary>
        internal void Stop(string reason = "the operator ended it")
        {
            if (ending == Ending.Capturing)
            {
                ending = Ending.Stopped;
                EndingDetail = reason;
            }
        }

        /// <summary>
        /// What ended the capture, in words, for the record and the screen.
        /// Separate from <see cref="Note"/>, which is the operator's own text
        /// and is rewritten whenever they edit it.
        /// </summary>
        internal string EndingDetail { get; private set; } = string.Empty;

        /// <summary>
        /// Whether rows are held that have not been written to disk. A capture
        /// start has to refuse while this is true, or the rows are lost.
        /// </summary>
        internal bool HasUnsavedRows => inputs.Count > 0 && !saved;

        /// <summary>
        /// Appends one update. The wave observations are read from the katana
        /// after the update, so they are what that update produced.
        /// </summary>
        internal void Append(
            in BladePoseSample sample, Vector3 viewForward, bool accepted, int acceptedSampleCount,
            SandboxRightHandKatana katana)
        {
            if (ending != Ending.Capturing)
            {
                return;
            }

            if (inputs.Count >= MaximumUpdates)
            {
                // Full. Capturing ends here and the ending says why; no row is
                // thrown away to make room.
                ending = Ending.BufferFull;
                EndingDetail = "the capture filled at " + MaximumUpdates
                    + " updates; every earlier update is kept and the run is truncated here, not holed";
                return;
            }

            // A capture holds one set of conditions, and updates run under a
            // second set cannot be recomputed from it. Checked before this
            // update is kept, so the first update under the changed conditions
            // is excluded and everything before it stays exactly as captured.
            if (katana != null && !SameConditions(conditions, katana.CaptureConditions))
            {
                ending = Ending.ConditionsChanged;
                EndingDetail = "the tuning or geometry changed after update "
                    + (inputs.Count - 1).ToString(CultureInfo.InvariantCulture)
                    + "; capturing ended there and the first update under the changed conditions was not kept";
                return;
            }

            if (inputs.Count == 0)
            {
                firstTimestamp = sample.TimestampSeconds;
            }

            int waveStart = waveRows.Count;
            int waves = katana != null ? katana.WaveCount : 0;
            for (int i = 0; i < waves; i++)
            {
                if (!katana.TryGetWave(i, out double latchedAt, out Plane plane, out Vector3 waveOrigin,
                        out Vector3 travelAxis, out Vector3 spanAxis, out float acceptedSpan,
                        out Vector3 previousStart, out Vector3 previousEnd, out Vector3 currentStart,
                        out Vector3 currentEnd))
                {
                    continue;
                }

                bool closed = katana.TryGetWaveSpanClose(
                    i, out double spanClosedAt, out Vector3 frozenOrigin, out Vector3 frozenDirection);
                bool hasCandidate = katana.TryGetWaveCandidate(
                    i, out _, out bool evaluated, out bool fromFrozen, out Vector3 guideOrigin,
                    out Vector3 guideDirection, out float rawSpan, out float q, out float denominator,
                    out bool termsFinite, out bool usable, out bool widened);

                waveRows.Add(new WaveRow
                {
                    WaveIndex = i,
                    LatchedAt = latchedAt,
                    PlaneNormal = plane.normal,
                    PlaneDistance = plane.distance,
                    WaveOrigin = waveOrigin,
                    TravelAxis = travelAxis,
                    SpanAxis = spanAxis,
                    AcceptedSpan = acceptedSpan,
                    PreviousSegmentStart = previousStart,
                    PreviousSegmentEnd = previousEnd,
                    CurrentSegmentStart = currentStart,
                    CurrentSegmentEnd = currentEnd,
                    SpanClosed = closed,
                    SpanClosedAt = closed ? spanClosedAt : double.NaN,
                    FrozenGuideOrigin = closed ? frozenOrigin : Vector3.zero,
                    FrozenGuideDirection = closed ? frozenDirection : Vector3.zero,
                    CandidateEvaluated = hasCandidate && evaluated,
                    CandidateFromFrozenGuide = hasCandidate && fromFrozen,
                    CandidateGuideOrigin = hasCandidate ? guideOrigin : Vector3.zero,
                    CandidateGuideDirection = hasCandidate ? guideDirection : Vector3.zero,
                    RawSpan = hasCandidate ? rawSpan : 0f,
                    Q = hasCandidate ? q : 0f,
                    Denominator = hasCandidate ? denominator : 0f,
                    TermsFinite = hasCandidate && termsFinite,
                    CandidateUsable = hasCandidate && usable,
                    CandidateWidenedSpan = hasCandidate && widened,
                });
            }

            inputs.Add(new InputRow
            {
                FrameId = sample.FrameId,
                TimestampSeconds = sample.TimestampSeconds,
                GripPosition = sample.GripPosition,
                GripRotation = sample.GripRotation,
                TrackingState = sample.TrackingState,
                ViewForward = viewForward,
                Accepted = accepted,
                AcceptedSampleCount = acceptedSampleCount,
                WaveObservationStart = waveStart,
                WaveObservationCount = waveRows.Count - waveStart,
            });
        }

        /// <summary>
        /// Writes the capture to a directory of its own under
        /// <see cref="RootDirectory"/>, and says where or why not. The
        /// directory name carries the time and the run id, and an existing
        /// directory is never written into: a repeat gets a new name, and if
        /// even that is taken the save fails rather than mixing two runs.
        /// <para>
        /// Four kinds of thing are kept apart by name: <c>input.csv</c> is the
        /// raw input, <c>conditions.txt</c> what it ran under, <c>current.csv</c>
        /// what the current method made of it, and <c>comparison/</c> is left
        /// for a later comparison to write into.
        /// </para>
        /// </summary>
        internal bool TrySave(out string directory, out string failure)
        {
            directory = string.Empty;
            failure = string.Empty;

            if (inputs.Count == 0)
            {
                failure = "nothing was captured, so there is nothing to save";
                return false;
            }

            string chosen;
            try
            {
                if (!TryChooseDirectory(out chosen, out failure))
                {
                    return false;
                }

                Directory.CreateDirectory(chosen);
                Directory.CreateDirectory(Path.Combine(chosen, "comparison"));
                File.WriteAllText(Path.Combine(chosen, "conditions.txt"), BuildConditions(chosen), Utf8NoBom);
                File.WriteAllText(Path.Combine(chosen, "input.csv"), BuildInput(), Utf8NoBom);
                File.WriteAllText(Path.Combine(chosen, "current.csv"), BuildCurrent(), Utf8NoBom);
                File.WriteAllText(
                    Path.Combine(chosen, "comparison", "README.txt"),
                    "A later comparison writes its results here. The three files beside this directory are the run"
                    + " itself: input.csv is what the slash path was given, conditions.txt what it ran under, and"
                    + " current.csv what the current method made of it. A comparison must not overwrite any of them."
                    + Environment.NewLine,
                    Utf8NoBom);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name + ": " + exception.Message;
                return false;
            }

            // Read back, so that a save reported as successful is one whose
            // files are on disk and long enough to hold what was captured.
            try
            {
                foreach (string name in new[] { "conditions.txt", "input.csv", "current.csv" })
                {
                    string path = Path.Combine(chosen, name);
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    {
                        failure = name + " was not written";
                        return false;
                    }
                }

                int lines = File.ReadAllLines(Path.Combine(chosen, "input.csv")).Length;
                if (lines != inputs.Count + 1)
                {
                    failure = "input.csv holds " + lines + " lines for " + inputs.Count + " captured updates";
                    return false;
                }
            }
            catch (Exception exception)
            {
                failure = "written but unreadable (" + exception.GetType().Name + ": " + exception.Message + ")";
                return false;
            }

            directory = chosen;
            saved = true;
            return true;
        }

        private bool TryChooseDirectory(out string chosen, out string failure)
        {
            chosen = string.Empty;
            failure = string.Empty;
            string stamp = startedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string baseName = runId.Length > 0 ? stamp + "-" + runId : stamp;
            for (int attempt = 1; attempt <= 64; attempt++)
            {
                string candidate = Path.Combine(
                    Root, attempt == 1 ? baseName : baseName + "-" + attempt.ToString(CultureInfo.InvariantCulture));
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    chosen = candidate;
                    return true;
                }
            }

            failure = "every name from " + baseName + " to " + baseName + "-64 is already taken under "
                + Root + "; nothing was overwritten";
            return false;
        }

        private string BuildConditions(string directory)
        {
            var text = new StringBuilder(2048);
            text.Append("Slash span capture").Append(Environment.NewLine);
            text.Append("run id: ").Append(runId).Append(Environment.NewLine);
            text.Append("directory: ").Append(directory).Append(Environment.NewLine);
            text.Append("started (UTC): ").Append(startedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(Environment.NewLine);
            text.Append("saved (UTC): ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(Environment.NewLine);
            text.Append("updates captured: ").Append(inputs.Count).Append(" of at most ").Append(MaximumUpdates)
                .Append(Environment.NewLine);
            text.Append("wave observations: ").Append(waveRows.Count).Append(Environment.NewLine);
            text.Append("capture ended by: ").Append(ending).Append(Environment.NewLine);
            text.Append("ending detail: ").Append(EndingDetail.Length > 0 ? EndingDetail : "(none)")
                .Append(Environment.NewLine);
            if (ending == Ending.Capturing)
            {
                text.Append(
                    "WARNING: saved while still capturing; later updates are not in this run")
                    .Append(Environment.NewLine);
            }
            text.Append("first input timestamp: ").Append(Format(firstTimestamp)).Append(Environment.NewLine);
            text.Append("note: ").Append(note.Length > 0 ? note : "(none)").Append(Environment.NewLine);
            text.Append(Environment.NewLine);

            text.Append("-- coordinate space and units --").Append(Environment.NewLine);
            text.Append("positions and lengths: metres, Unity world space, left-handed, Y up").Append(Environment.NewLine);
            text.Append("rotations: Unity quaternions (x, y, z, w), world space").Append(Environment.NewLine);
            text.Append("times: seconds, Time.unscaledTimeAsDouble as the sample carried it, not rebased")
                .Append(Environment.NewLine);
            text.Append("grip pose: the right-hand device pose, never the aim pose").Append(Environment.NewLine);
            text.Append(Environment.NewLine);

            text.Append("-- tuning as the capture began --").Append(Environment.NewLine);
            text.Append("minimum speed: ").Append(Format(conditions.MinimumSpeed)).Append(Environment.NewLine);
            text.Append("minimum displacement: ").Append(Format(conditions.MinimumDisplacement)).Append(Environment.NewLine);
            text.Append("minimum edge lead score: ").Append(Format(conditions.MinimumEdgeLeadScore)).Append(Environment.NewLine);
            text.Append("return stroke edge lead score: ").Append(Format(conditions.ReturnStrokeEdgeLeadScore))
                .Append(Environment.NewLine);
            text.Append("latch chord metres: ").Append(Format(conditions.LatchChordMetres)).Append(Environment.NewLine);
            text.Append("span capture timeout seconds: ").Append(Format(conditions.SpanCaptureTimeoutSeconds))
                .Append(Environment.NewLine);
            text.Append("begin blade axis view dot minimum: ").Append(Format(conditions.BeginBladeAxisViewDotMinimum))
                .Append(Environment.NewLine);
            text.Append(Environment.NewLine);

            text.Append("-- geometry and constants --").Append(Environment.NewLine);
            text.Append("span axis method: fixed 150 degrees to travel, on the emitter chord side; initial span is chord length")
                .Append(Environment.NewLine);
            text.Append("blade length: ").Append(Format(conditions.BladeLength)).Append(Environment.NewLine);
            text.Append("grip to katana offset position: ").Append(Format(conditions.GripOffsetPosition))
                .Append(Environment.NewLine);
            text.Append("grip to katana offset rotation: ").Append(Format(conditions.GripOffsetRotation))
                .Append(Environment.NewLine);
            text.Append("emission control point ratio: ").Append(Format(conditions.EmissionControlPointRatio))
                .Append(Environment.NewLine);
            text.Append("cut sample ratio: ").Append(Format(conditions.CutSampleRatio)).Append(Environment.NewLine);
            text.Append("wave speed: ").Append(Format(conditions.WaveSpeed)).Append(Environment.NewLine);
            text.Append("wave lifetime seconds: ").Append(Format(conditions.WaveLifetimeSeconds)).Append(Environment.NewLine);
            text.Append("near parallel denominator: ").Append(Format(conditions.NearParallelDenominator))
                .Append(Environment.NewLine);
            text.Append("live wave capacity: ").Append(conditions.WaveCapacity).Append(Environment.NewLine);
            text.Append(Environment.NewLine);

            text.Append("-- code --").Append(Environment.NewLine);
            text.Append("unity version: ").Append(Application.unityVersion).Append(Environment.NewLine);
            text.Append("platform: ").Append(Application.platform).Append(Environment.NewLine);
            text.Append("graphics device: ").Append(SystemInfo.graphicsDeviceType).Append(Environment.NewLine);
            SandboxSlashCaptureCommit.Describe(out string commit, out string dirty);
            text.Append("commit: ").Append(commit).Append(Environment.NewLine);
            text.Append("uncommitted changes: ").Append(dirty).Append(Environment.NewLine);
            text.Append(Environment.NewLine);

            text.Append("-- what is here --").Append(Environment.NewLine);
            text.Append("input.csv     the raw input, one row per update, rejected input included")
                .Append(Environment.NewLine);
            text.Append("current.csv   what the current method made of it, for checking a recomputation")
                .Append(Environment.NewLine);
            text.Append("comparison/   left empty for a later comparison to write into").Append(Environment.NewLine);
            return text.ToString();
        }

        private string BuildInput()
        {
            var text = new StringBuilder(inputs.Count * 128);
            text.Append("update,frameId,t,gripX,gripY,gripZ,rotX,rotY,rotZ,rotW,trackingState,positionTracked,")
                .Append("rotationTracked,viewX,viewY,viewZ,accepted,acceptedSampleCount")
                .Append(Environment.NewLine);
            for (int i = 0; i < inputs.Count; i++)
            {
                InputRow row = inputs[i];
                text.Append(i).Append(',').Append(row.FrameId).Append(',').Append(Format(row.TimestampSeconds))
                    .Append(',').Append(Format(row.GripPosition.x)).Append(',').Append(Format(row.GripPosition.y))
                    .Append(',').Append(Format(row.GripPosition.z))
                    .Append(',').Append(Format(row.GripRotation.x)).Append(',').Append(Format(row.GripRotation.y))
                    .Append(',').Append(Format(row.GripRotation.z)).Append(',').Append(Format(row.GripRotation.w))
                    .Append(',').Append((int)row.TrackingState)
                    .Append(',').Append((row.TrackingState & BladeTrackingState.Position) != 0 ? 1 : 0)
                    .Append(',').Append((row.TrackingState & BladeTrackingState.Rotation) != 0 ? 1 : 0)
                    .Append(',').Append(Format(row.ViewForward.x)).Append(',').Append(Format(row.ViewForward.y))
                    .Append(',').Append(Format(row.ViewForward.z))
                    .Append(',').Append(row.Accepted ? 1 : 0)
                    .Append(',').Append(row.AcceptedSampleCount)
                    .Append(Environment.NewLine);
            }

            return text.ToString();
        }

        private string BuildCurrent()
        {
            var text = new StringBuilder(waveRows.Count * 256);
            text.Append("update,t,waveIndex,latchedAt,planeNormalX,planeNormalY,planeNormalZ,planeDistance,")
                .Append("originX,originY,originZ,travelX,travelY,travelZ,spanX,spanY,spanZ,acceptedSpan,")
                .Append("prevStartX,prevStartY,prevStartZ,prevEndX,prevEndY,prevEndZ,")
                .Append("curStartX,curStartY,curStartZ,curEndX,curEndY,curEndZ,")
                .Append("spanClosed,spanClosedAt,frozenOriginX,frozenOriginY,frozenOriginZ,")
                .Append("frozenDirX,frozenDirY,frozenDirZ,")
                .Append("candidateEvaluated,candidateFromFrozenGuide,guideOriginX,guideOriginY,guideOriginZ,")
                .Append("guideDirX,guideDirY,guideDirZ,rawSpan,q,denominator,termsFinite,candidateUsable,")
                .Append("candidateWidenedSpan")
                .Append(Environment.NewLine);
            for (int i = 0; i < inputs.Count; i++)
            {
                InputRow input = inputs[i];
                for (int w = 0; w < input.WaveObservationCount; w++)
                {
                    WaveRow row = waveRows[input.WaveObservationStart + w];
                    text.Append(i).Append(',').Append(Format(input.TimestampSeconds)).Append(',').Append(row.WaveIndex)
                        .Append(',').Append(Format(row.LatchedAt))
                        .Append(',').Append(Format(row.PlaneNormal)).Append(',').Append(Format(row.PlaneDistance))
                        .Append(',').Append(Format(row.WaveOrigin))
                        .Append(',').Append(Format(row.TravelAxis))
                        .Append(',').Append(Format(row.SpanAxis))
                        .Append(',').Append(Format(row.AcceptedSpan))
                        .Append(',').Append(Format(row.PreviousSegmentStart))
                        .Append(',').Append(Format(row.PreviousSegmentEnd))
                        .Append(',').Append(Format(row.CurrentSegmentStart))
                        .Append(',').Append(Format(row.CurrentSegmentEnd))
                        .Append(',').Append(row.SpanClosed ? 1 : 0)
                        .Append(',').Append(Format(row.SpanClosedAt))
                        .Append(',').Append(Format(row.FrozenGuideOrigin))
                        .Append(',').Append(Format(row.FrozenGuideDirection))
                        .Append(',').Append(row.CandidateEvaluated ? 1 : 0)
                        .Append(',').Append(row.CandidateFromFrozenGuide ? 1 : 0)
                        .Append(',').Append(Format(row.CandidateGuideOrigin))
                        .Append(',').Append(Format(row.CandidateGuideDirection))
                        .Append(',').Append(Format(row.RawSpan))
                        .Append(',').Append(Format(row.Q))
                        .Append(',').Append(Format(row.Denominator))
                        .Append(',').Append(row.TermsFinite ? 1 : 0)
                        .Append(',').Append(row.CandidateUsable ? 1 : 0)
                        .Append(',').Append(row.CandidateWidenedSpan ? 1 : 0)
                        .Append(Environment.NewLine);
                }
            }

            return text.ToString();
        }

        // No byte order mark: these files are read by whatever tool a later
        // comparison uses, and a mark on the first header field is a trap.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Round trip exactly: a capture is saved so it can be read back and
        // recomputed, and a shortened decimal would not recompute to the same
        // numbers.
        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Format(Vector3 value)
        {
            return Format(value.x) + "," + Format(value.y) + "," + Format(value.z);
        }

        private static string Format(Quaternion value)
        {
            return Format(value.x) + "," + Format(value.y) + "," + Format(value.z) + "," + Format(value.w);
        }

        /// <summary>
        /// Reads a saved run's conditions back. A recomputation has to apply
        /// these rather than assume the defaults, or it is not recomputing that
        /// run at all.
        /// </summary>
        internal static bool TryReadConditions(string conditionsPath, out Conditions read, out string failure)
        {
            read = default;
            failure = string.Empty;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(conditionsPath);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name + ": " + exception.Message;
                return false;
            }

            var found = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon <= 0 || line.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                found[line.Substring(0, colon)] = line.Substring(colon + 2);
            }

            var missing = new System.Collections.Generic.List<string>();
            read.MinimumSpeed = Number(found, "minimum speed", missing);
            read.MinimumDisplacement = Number(found, "minimum displacement", missing);
            read.MinimumEdgeLeadScore = Number(found, "minimum edge lead score", missing);
            read.ReturnStrokeEdgeLeadScore = Number(found, "return stroke edge lead score", missing);
            read.LatchChordMetres = Number(found, "latch chord metres", missing);
            read.SpanCaptureTimeoutSeconds = Number(found, "span capture timeout seconds", missing);
            read.BeginBladeAxisViewDotMinimum = Number(found, "begin blade axis view dot minimum", missing);
            read.BladeLength = Number(found, "blade length", missing);
            read.GripOffsetPosition = Vector(found, "grip to katana offset position", missing);
            Vector3 rotation = Vector(found, "grip to katana offset rotation", missing, out float w);
            read.GripOffsetRotation = new Quaternion(rotation.x, rotation.y, rotation.z, w);
            read.EmissionControlPointRatio = Number(found, "emission control point ratio", missing);
            read.CutSampleRatio = Number(found, "cut sample ratio", missing);
            read.WaveSpeed = Number(found, "wave speed", missing);
            read.WaveLifetimeSeconds = Number(found, "wave lifetime seconds", missing);
            read.NearParallelDenominator = Number(found, "near parallel denominator", missing);
            read.WaveCapacity = (int)Number(found, "live wave capacity", missing);

            if (missing.Count > 0)
            {
                failure = "these lines were missing or unreadable: " + string.Join(", ", missing);
                return false;
            }

            return true;
        }

        private static float Number(
            System.Collections.Generic.IDictionary<string, string> found, string key,
            System.Collections.Generic.ICollection<string> missing)
        {
            if (found.TryGetValue(key, out string text)
                && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }

            missing.Add(key);
            return 0f;
        }

        private static Vector3 Vector(
            System.Collections.Generic.IDictionary<string, string> found, string key,
            System.Collections.Generic.ICollection<string> missing)
        {
            return Vector(found, key, missing, out _);
        }

        private static Vector3 Vector(
            System.Collections.Generic.IDictionary<string, string> found, string key,
            System.Collections.Generic.ICollection<string> missing, out float w)
        {
            w = 0f;
            if (!found.TryGetValue(key, out string text))
            {
                missing.Add(key);
                return Vector3.zero;
            }

            string[] parts = text.Split(',');
            if (parts.Length < 3)
            {
                missing.Add(key);
                return Vector3.zero;
            }

            var value = new Vector3();
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float component))
                {
                    missing.Add(key);
                    return Vector3.zero;
                }

                value[i] = component;
            }

            if (parts.Length >= 4
                && !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out w))
            {
                missing.Add(key);
            }

            return value;
        }

        // Compared value by value rather than with ==, because a struct of
        // floats has no equality worth relying on and Vector3's own == is
        // approximate.
        private static bool SameConditions(in Conditions a, in Conditions b)
        {
            return Same(a.MinimumSpeed, b.MinimumSpeed)
                && Same(a.MinimumDisplacement, b.MinimumDisplacement)
                && Same(a.MinimumEdgeLeadScore, b.MinimumEdgeLeadScore)
                && Same(a.ReturnStrokeEdgeLeadScore, b.ReturnStrokeEdgeLeadScore)
                && Same(a.LatchChordMetres, b.LatchChordMetres)
                && Same(a.SpanCaptureTimeoutSeconds, b.SpanCaptureTimeoutSeconds)
                && Same(a.BeginBladeAxisViewDotMinimum, b.BeginBladeAxisViewDotMinimum)
                && Same(a.BladeLength, b.BladeLength)
                && Same(a.GripOffsetPosition.x, b.GripOffsetPosition.x)
                && Same(a.GripOffsetPosition.y, b.GripOffsetPosition.y)
                && Same(a.GripOffsetPosition.z, b.GripOffsetPosition.z)
                && Same(a.GripOffsetRotation.x, b.GripOffsetRotation.x)
                && Same(a.GripOffsetRotation.y, b.GripOffsetRotation.y)
                && Same(a.GripOffsetRotation.z, b.GripOffsetRotation.z)
                && Same(a.GripOffsetRotation.w, b.GripOffsetRotation.w)
                && Same(a.EmissionControlPointRatio, b.EmissionControlPointRatio)
                && Same(a.CutSampleRatio, b.CutSampleRatio)
                && Same(a.WaveSpeed, b.WaveSpeed)
                && Same(a.WaveLifetimeSeconds, b.WaveLifetimeSeconds)
                && Same(a.NearParallelDenominator, b.NearParallelDenominator)
                && a.WaveCapacity == b.WaveCapacity;
        }

        private static bool Same(float a, float b)
        {
            return a.Equals(b);
        }

        private static string Sanitise(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return string.Empty;
            }

            var kept = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                {
                    kept.Append(c);
                }
                else if (c == ' ')
                {
                    kept.Append('-');
                }
            }

            return kept.Length > 40 ? kept.ToString(0, 40) : kept.ToString();
        }
    }
}
