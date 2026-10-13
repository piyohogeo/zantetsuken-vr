using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A development readout of each swing of the katana and what the slash core made of it (DESIGN 19.1.12;
    /// 2026-10-13, for swings that give no slash). A swing is the blade moving: its cut sample point at
    /// <see cref="SpeedFloor"/> or faster, quiet gaps up to <see cref="GapSeconds"/> inside it. Its record gathers the
    /// core's observation of every update in it (<see cref="SlashUpdateObservation"/>): the gate's reasons, the speeds,
    /// the edge lead, the emitter chord, the begin view, the resets, the latch attempts, the tracking and the frame
    /// gaps. A swing that gave no wave says, from those, what kept it from one -- so that it can be read on the spot:
    /// the play menu shows the last swings, and each swing that ends is one line for the log. It decides nothing, and
    /// nothing of the slash reads it.
    /// </summary>
    internal sealed class SlashSwingDiagnosis
    {
        /// <summary>A cut sample point moving at least this fast, m/s, is a swing -- well under the gate's 3.5 m/s, so that a slow swing is a swing too.</summary>
        internal const float SpeedFloor = 1.0f;

        /// <summary>
        /// Quiet this long ends a swing. Longer than the core's <see cref="SlashWaveCore.StrokeQuietSeconds"/> and the
        /// gate window together, so that the stroke's own end by quiet (D-224) falls inside the swing's record.
        /// </summary>
        internal const double GapSeconds = 0.25;

        /// <summary>How many ended swings are kept, newest first.</summary>
        internal const int Kept = 8;

        private const int GateReasonCount = (int)BladeEdgeGateReason.EdgeLeadBelowThreshold + 1;

        /// <summary>One swing: when, how many samples, and everything the core observed of them.</summary>
        internal struct Swing
        {
            public int Number;

            /// <summary>The sample clock (the time the samples carried), first and last sample of the swing.</summary>
            public double BeganAt, EndedAt;

            public long FirstFrame, LastFrame;

            /// <summary>Samples in the swing; of them, unusable poses (tracking lost), and recorded ones the gate had no 30-60 ms window for.</summary>
            public int Samples, Untracked, NoWindow;

            /// <summary>How often the gate judged each way, by <see cref="BladeEdgeGateReason"/> (None is accepted).</summary>
            public int[] GateReasons;

            /// <summary>The most accepted samples the stroke had -- a stroke left over from the swing before counts too; see <see cref="AcceptedHere"/>.</summary>
            public int Accepted;

            /// <summary>Motions of this swing the gate accepted (into a stroke of its own or one left over).</summary>
            public int AcceptedHere;

            /// <summary>Motions accepted into a stroke whose latch chance was spent before this swing (a swing that follows a latched one the return half never ended).</summary>
            public int SpentCarried;

            /// <summary>The highest speed the gate judged (the cut sample point over its window), and sample to sample.</summary>
            public float PeakSpeed, PeakRawSpeed;

            /// <summary>The highest edge lead score the gate saw; -2 where it saw none.</summary>
            public float BestEdgeLead;

            /// <summary>The longest emitter chord the stroke reached.</summary>
            public float Chord;

            /// <summary>Would-be begin samples the view check turned away, and the worst of their dots.</summary>
            public int BeginViewFails;

            public float BeginViewWorstDot;

            public int ResetPoseUnusable, ResetHistoryRefused, ResetSpeedAboveMaximum, ResetReturnStroke, ResetQuiet;

            public int LatchNoFrame, LatchNoSlot, LatchNotFinite, Latched;

            public long LastSlashId;

            /// <summary>The longest time between two samples of the swing.</summary>
            public double LongestGapSeconds;
        }

        private readonly Swing[] _kept = new Swing[Kept];
        private int _keptCount, _next, _numbered;
        private Swing _open;
        private bool _swinging;
        private double _lastMovingAt, _lastAt;
        private bool _lastKnown, _any;
        private Vector3 _lastCutSample;

        /// <summary>Ended swings kept, up to <see cref="Kept"/>.</summary>
        internal int Count => _keptCount;

        /// <summary>Every swing that ended, counted.</summary>
        internal int Ended => _numbered;

        /// <summary>A swing is under way.</summary>
        internal bool Swinging => _swinging;

        /// <summary>An ended swing: 0 the newest.</summary>
        internal Swing At(int index)
        {
            if (index < 0 || index >= _keptCount)
            {
                return default;
            }

            return _kept[(_next - 1 - index + (Kept * 2)) % Kept];
        }

        /// <summary>
        /// One update: the sample as the katana was given it, what the core said of it, and its evaluated cut sample
        /// point when it had one. Returns the record line of a swing that ended with this update, or null.
        /// </summary>
        internal string Observe(in BladePoseSample sample, SlashInputOutcome outcome, in SlashUpdateObservation o, bool poseKnown, Vector3 cutSamplePosition)
        {
            double now = sample.TimestampSeconds;
            double gap = _any && now > _lastAt ? now - _lastAt : 0.0;
            float rawSpeed = 0f;
            if (poseKnown && _lastKnown && gap > 0.0)
            {
                rawSpeed = (cutSamplePosition - _lastCutSample).magnitude / (float)gap;
                if (!float.IsFinite(rawSpeed))
                {
                    rawSpeed = 0f;
                }
            }

            bool moving = poseKnown && rawSpeed >= SpeedFloor;
            string ended = null;
            if (_swinging && now - _lastMovingAt > GapSeconds)
            {
                // The quiet sample that ends the swing belongs to it (what the core saw of it -- a stroke ended by
                // quiet, say -- goes into this record); a moving one begins the next swing below.
                if (!moving)
                {
                    Add(in sample, outcome, in o, poseKnown, rawSpeed, gap);
                }

                ended = Close();
            }

            if (!_swinging && moving)
            {
                _open = new Swing { Number = _numbered + 1, BeganAt = now, FirstFrame = sample.FrameId, GateReasons = new int[GateReasonCount], BestEdgeLead = -2f, BeginViewWorstDot = 2f };
                _swinging = true;
                _lastMovingAt = now;
            }

            if (_swinging)
            {
                Add(in sample, outcome, in o, poseKnown, rawSpeed, gap);
                if (moving)
                {
                    _lastMovingAt = now;
                }
            }

            _any = true;
            _lastAt = now;
            _lastKnown = poseKnown;
            if (poseKnown)
            {
                _lastCutSample = cutSamplePosition;
            }

            return ended;
        }

        private void Add(in BladePoseSample sample, SlashInputOutcome outcome, in SlashUpdateObservation o, bool poseKnown, float rawSpeed, double gap)
        {
            _open.Samples++;
            _open.EndedAt = sample.TimestampSeconds;
            _open.LastFrame = sample.FrameId;
            if (_open.Samples > 1 && gap > _open.LongestGapSeconds)
            {
                _open.LongestGapSeconds = gap;
            }

            if (outcome == SlashInputOutcome.PoseUnusable || !poseKnown)
            {
                _open.Untracked++;
            }

            if (rawSpeed > _open.PeakRawSpeed)
            {
                _open.PeakRawSpeed = rawSpeed;
            }

            if (outcome == SlashInputOutcome.Recorded)
            {
                if (!o.GateEvaluated)
                {
                    _open.NoWindow++;
                }
                else
                {
                    int reason = (int)o.GateReason;
                    if (reason >= 0 && reason < GateReasonCount)
                    {
                        _open.GateReasons[reason]++;
                    }

                    if (o.Speed > _open.PeakSpeed) _open.PeakSpeed = o.Speed;
                    if (o.EdgeLeadScore > _open.BestEdgeLead) _open.BestEdgeLead = o.EdgeLeadScore;
                    // Accepted by the gate and taken into a stroke: a would-be begin the view check turned away is not.
                    if (o.GateReason == BladeEdgeGateReason.None && !(o.BeginViewChecked && !o.BeginViewPassed)) _open.AcceptedHere++;
                }
            }

            if (o.AcceptedCount > _open.Accepted) _open.Accepted = o.AcceptedCount;
            if (outcome == SlashInputOutcome.Recorded && o.GateEvaluated && o.GateReason == BladeEdgeGateReason.None
                && o.LatchSpent && o.Latch != SlashLatchAttempt.Latched && _open.Latched == 0)
            {
                _open.SpentCarried++;
            }
            if (o.ChordMetres > _open.Chord) _open.Chord = o.ChordMetres;
            if (o.BeginViewChecked && !o.BeginViewPassed)
            {
                _open.BeginViewFails++;
                if (o.BeginViewDot < _open.BeginViewWorstDot) _open.BeginViewWorstDot = o.BeginViewDot;
            }

            switch (o.Reset)
            {
                case SlashStrokeReset.PoseUnusable: _open.ResetPoseUnusable++; break;
                case SlashStrokeReset.HistoryRefused: _open.ResetHistoryRefused++; break;
                case SlashStrokeReset.SpeedAboveMaximum: _open.ResetSpeedAboveMaximum++; break;
                case SlashStrokeReset.ReturnStroke: _open.ResetReturnStroke++; break;
                case SlashStrokeReset.Quiet: _open.ResetQuiet++; break;
            }

            switch (o.Latch)
            {
                case SlashLatchAttempt.NoFrame: _open.LatchNoFrame++; break;
                case SlashLatchAttempt.NoSlot: _open.LatchNoSlot++; break;
                case SlashLatchAttempt.NotFinite: _open.LatchNotFinite++; break;
                case SlashLatchAttempt.Latched:
                    _open.Latched++;
                    _open.LastSlashId = o.LatchedSlashId;
                    break;
            }
        }

        private string Close()
        {
            _swinging = false;
            _numbered++;
            _kept[_next] = _open;
            _next = (_next + 1) % Kept;
            if (_keptCount < Kept)
            {
                _keptCount++;
            }

            return Line(in _open);
        }

        /// <summary>The last swings, newest first, one verdict a line; for the play menu.</summary>
        internal string Describe(int lines)
        {
            if (_keptCount == 0)
            {
                return _swinging ? "(a swing is under way)" : "no swing yet";
            }

            var text = new StringBuilder();
            for (int i = 0; i < lines && i < _keptCount; i++)
            {
                Swing s = At(i);
                if (i > 0)
                {
                    text.Append('\n');
                }

                text.Append(s.Number).Append(": ").Append(Verdict(in s));
            }

            return text.ToString();
        }

        /// <summary>
        /// What a swing came to, in one short line: latched, or the first of these that holds -- the latch refused
        /// (no slot, no frame, not finite), the stroke reset (too fast, tracking lost, the return half), a stroke that
        /// never reached the latch chord, a begin the view check turned away, a gate that turned every motion away, a
        /// swing the gate never had a window for.
        /// </summary>
        internal static string Verdict(in Swing s)
        {
            var inv = CultureInfo.InvariantCulture;
            if (s.Latched > 0)
            {
                return "latched (slash " + s.LastSlashId + "): peak " + s.PeakSpeed.ToString("F1", inv) + " m/s, chord " + s.Chord.ToString("F2", inv) + " m";
            }

            string tail = "; peak " + s.PeakSpeed.ToString("F1", inv) + " m/s, chord " + s.Chord.ToString("F2", inv) + " m, " + s.Samples + " samples";
            if (s.LatchNoSlot > 0) return "no latch: no slot, " + SlashWaveCore.Capacity + " waves alive" + tail;
            if (s.LatchNoFrame > 0) return "no latch: no plane from the stroke" + tail;
            if (s.LatchNotFinite > 0) return "no latch: the frame was not finite" + tail;
            if (s.ResetSpeedAboveMaximum > 0) return "no latch: too fast at the cut point (" + s.PeakSpeed.ToString("F1", inv) + " > " + SlashWaveCore.GateMaximumSpeed.ToString("F0", inv) + " m/s): stroke reset" + tail;
            if (s.ResetPoseUnusable > 0 || s.Untracked > 0) return "no latch: tracking lost on " + Mathf.Max(s.Untracked, s.ResetPoseUnusable) + " sample(s): stroke reset" + tail;
            if (s.ResetHistoryRefused > 0) return "no latch: a sample's time did not move forward: stroke reset" + tail;
            if (s.ResetReturnStroke > 0) return "no latch: the return half ended the stroke" + tail;
            if (s.SpentCarried > 0) return "no latch: the stroke before was spent and not re-armed (" + s.SpentCarried + " motions accepted into it; no fast return half seen)" + tail;
            if (s.AcceptedHere > 0) return "no latch: stroke short (chord " + s.Chord.ToString("F2", inv) + " m, " + s.Accepted + " accepted)" + tail;
            if (s.BeginViewFails > 0) return "no latch: blade not toward the view at the begin (dot " + s.BeginViewWorstDot.ToString("F2", inv) + ")" + tail;
            int judged = 0;
            for (int i = 0; i < s.GateReasons.Length; i++) judged += s.GateReasons[i];
            if (judged == 0) return "no latch: the gate had no 30-60 ms window (longest gap " + (s.LongestGapSeconds * 1000.0).ToString("F0", inv) + " ms)" + tail;
            int top = 1;
            for (int i = 2; i < s.GateReasons.Length; i++) if (s.GateReasons[i] > s.GateReasons[top]) top = i;
            switch ((BladeEdgeGateReason)top)
            {
                case BladeEdgeGateReason.SpeedBelowMinimum: return "no latch: speed under the gate (peak " + s.PeakSpeed.ToString("F1", inv) + " m/s)" + tail;
                case BladeEdgeGateReason.DisplacementBelowMinimum: return "no latch: displacement under the gate in the window" + tail;
                case BladeEdgeGateReason.EdgeLeadBelowThreshold: return "no latch: edge not leading (best " + s.BestEdgeLead.ToString("F2", inv) + ")" + tail;
                case BladeEdgeGateReason.NoLateralMotion: return "no latch: no lateral motion (along the blade)" + tail;
                default: return "no latch: gate reason " + (BladeEdgeGateReason)top + tail;
            }
        }

        /// <summary>The whole record of one swing, one line: for the log and the development log.</summary>
        internal static string Line(in Swing s)
        {
            var inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(320);
            text.Append("swing ").Append(s.Number).Append(" at ").Append(s.BeganAt.ToString("F3", inv)).Append(" s, ")
                .Append(((s.EndedAt - s.BeganAt) * 1000.0).ToString("F0", inv)).Append(" ms, frames ").Append(s.FirstFrame).Append('-').Append(s.LastFrame)
                .Append(", samples ").Append(s.Samples).Append(" (untracked ").Append(s.Untracked).Append(", no window ").Append(s.NoWindow)
                .Append(", longest gap ").Append((s.LongestGapSeconds * 1000.0).ToString("F0", inv)).Append(" ms); gate:");
            for (int i = 0; i < s.GateReasons.Length; i++)
            {
                if (s.GateReasons[i] > 0)
                {
                    text.Append(' ').Append(i == 0 ? "accepted" : ((BladeEdgeGateReason)i).ToString()).Append(' ').Append(s.GateReasons[i]);
                }
            }

            text.Append("; peak speed ").Append(s.PeakSpeed.ToString("F2", inv)).Append(" m/s (sample to sample ").Append(s.PeakRawSpeed.ToString("F2", inv))
                .Append("), best edge lead ").Append(s.BestEdgeLead <= -2f ? "none" : s.BestEdgeLead.ToString("F2", inv))
                .Append(", accepted ").Append(s.Accepted).Append(" (this swing's motions ").Append(s.AcceptedHere).Append(", into a spent stroke ").Append(s.SpentCarried).Append("), chord ").Append(s.Chord.ToString("F3", inv)).Append(" m, begin view fails ").Append(s.BeginViewFails);
            if (s.BeginViewFails > 0)
            {
                text.Append(" (worst dot ").Append(s.BeginViewWorstDot.ToString("F2", inv)).Append(')');
            }

            text.Append("; resets: pose ").Append(s.ResetPoseUnusable).Append(", history ").Append(s.ResetHistoryRefused)
                .Append(", too fast ").Append(s.ResetSpeedAboveMaximum).Append(", return ").Append(s.ResetReturnStroke).Append(", quiet ").Append(s.ResetQuiet)
                .Append("; latch: latched ").Append(s.Latched).Append(", no frame ").Append(s.LatchNoFrame).Append(", no slot ").Append(s.LatchNoSlot)
                .Append(", not finite ").Append(s.LatchNotFinite).Append(" -> ").Append(Verdict(in s));
            return text.ToString();
        }
    }
}
