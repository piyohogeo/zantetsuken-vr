using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The scene draw comparison's camera path (TL, 2026-10-07): where the eye is and which way it faces at each moment
    /// of the path's own time, the same for both ways of drawing. A start and a list of segments, each lasting a given
    /// time at a constant rate:
    /// <code>
    /// start,x,y,z,yaw        the eye's position (world) and heading (degrees about the vertical); once, first
    /// hold,seconds           standing still
    /// goto,x,z,seconds       to that place in a straight line, the height and the heading kept
    /// face,yaw,seconds       to that heading by the shorter way
    /// turn,degrees,seconds   by that many degrees, signed (a whole turn is 360)
    /// </code>
    /// Lines beginning with '#' and empty lines are passed over. It is a function of time and nothing else: nothing of
    /// the scene, the frame rate or a previous frame enters it.
    /// </summary>
    public sealed class SceneDrawComparePath
    {
        public enum Kind { Hold, Goto, Face, Turn }

        public struct Pose
        {
            public Vector3 eye;
            public float yaw;
            public int segment;
        }

        private struct Segment
        {
            public Kind kind;
            public double begins, seconds;
            public Vector3 fromEye, toEye;
            public float fromYaw, turn;
        }

        private readonly List<Segment> _segments = new List<Segment>();
        private Vector3 _startEye;
        private float _startYaw;

        /// <summary>The path's whole time.</summary>
        public double Seconds { get; private set; }

        public int SegmentCount => _segments.Count;

        public Kind KindOf(int segment) => _segments[segment].kind;

        public double BeginOf(int segment) => _segments[segment].begins;

        public double SecondsOf(int segment) => _segments[segment].seconds;

        /// <summary>How far the eye travels over the whole path, in metres; and how far it turns, in degrees.</summary>
        public double Metres { get; private set; }

        public double Degrees { get; private set; }

        /// <summary>
        /// The eye at <paramref name="seconds"/> of the path's time: its start before 0, its end from
        /// <see cref="Seconds"/> on.
        /// </summary>
        public Pose At(double seconds)
        {
            if (_segments.Count == 0 || seconds <= 0)
            {
                return new Pose { eye = _startEye, yaw = _startYaw, segment = 0 };
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                Segment s = _segments[i];
                if (seconds < s.begins + s.seconds || i == _segments.Count - 1)
                {
                    float k = s.seconds > 0 ? Mathf.Clamp01((float)((seconds - s.begins) / s.seconds)) : 1f;
                    return new Pose { eye = Vector3.LerpUnclamped(s.fromEye, s.toEye, k), yaw = Mathf.Repeat(s.fromYaw + s.turn * k, 360f), segment = i };
                }
            }

            return default;
        }

        public static bool TryParse(string text, out SceneDrawComparePath path, out string failure)
        {
            path = null;
            failure = null;
            if (text == null)
            {
                failure = "no path";
                return false;
            }

            var made = new SceneDrawComparePath();
            CultureInfo inv = CultureInfo.InvariantCulture;
            bool started = false;
            Vector3 eye = default;
            float yaw = 0f;
            string[] lines = text.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string[] f = line.Split(',');
                var v = new double[f.Length];
                for (int i = 1; i < f.Length; i++)
                {
                    if (!double.TryParse(f[i].Trim(), NumberStyles.Float, inv, out v[i]) || double.IsNaN(v[i]) || double.IsInfinity(v[i]))
                    {
                        failure = "line " + (n + 1) + ": '" + f[i].Trim() + "' is not a number";
                        return false;
                    }
                }

                string word = f[0].Trim();
                if (word == "start")
                {
                    if (started || f.Length != 5)
                    {
                        failure = "line " + (n + 1) + ": 'start,x,y,z,yaw' once, before every segment";
                        return false;
                    }

                    started = true;
                    eye = made._startEye = new Vector3((float)v[1], (float)v[2], (float)v[3]);
                    yaw = made._startYaw = Mathf.Repeat((float)v[4], 360f);
                    continue;
                }

                if (!started)
                {
                    failure = "line " + (n + 1) + ": the path begins with 'start,x,y,z,yaw'";
                    return false;
                }

                var s = new Segment { begins = made.Seconds, fromEye = eye, toEye = eye, fromYaw = yaw };
                double seconds;
                if (word == "hold" && f.Length == 2)
                {
                    s.kind = Kind.Hold;
                    seconds = v[1];
                }
                else if (word == "goto" && f.Length == 4)
                {
                    s.kind = Kind.Goto;
                    s.toEye = new Vector3((float)v[1], eye.y, (float)v[2]);
                    seconds = v[3];
                }
                else if (word == "face" && f.Length == 3)
                {
                    s.kind = Kind.Face;
                    s.turn = Mathf.DeltaAngle(yaw, (float)v[1]);
                    seconds = v[2];
                }
                else if (word == "turn" && f.Length == 3)
                {
                    s.kind = Kind.Turn;
                    s.turn = (float)v[1];
                    seconds = v[2];
                }
                else
                {
                    failure = "line " + (n + 1) + ": not 'hold,seconds', 'goto,x,z,seconds', 'face,yaw,seconds' or 'turn,degrees,seconds': " + line;
                    return false;
                }

                if (!(seconds > 0))
                {
                    failure = "line " + (n + 1) + ": a segment lasts more than no time";
                    return false;
                }

                s.seconds = seconds;
                made._segments.Add(s);
                made.Seconds += seconds;
                made.Metres += Vector3.Distance(s.fromEye, s.toEye);
                made.Degrees += Math.Abs(s.turn);
                eye = s.toEye;
                yaw = Mathf.Repeat(yaw + s.turn, 360f);
            }

            if (!started || made._segments.Count == 0)
            {
                failure = "the path has no " + (started ? "segment" : "start");
                return false;
            }

            path = made;
            return true;
        }

        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            int holds = 0, moves = 0, turns = 0;
            double held = 0, moved = 0, turned = 0;
            foreach (Segment s in _segments)
            {
                if (s.kind == Kind.Hold) { holds++; held += s.seconds; }
                else if (s.kind == Kind.Goto) { moves++; moved += s.seconds; }
                else { turns++; turned += s.seconds; }
            }

            var text = new StringBuilder();
            text.Append(_segments.Count).Append(" segments, ").Append(Seconds.ToString("F1", inv)).Append(" s: standing ").Append(holds).Append(" (").Append(held.ToString("F1", inv))
                .Append(" s), moving ").Append(moves).Append(" (").Append(moved.ToString("F1", inv)).Append(" s, ").Append(Metres.ToString("F1", inv)).Append(" m), turning ").Append(turns)
                .Append(" (").Append(turned.ToString("F1", inv)).Append(" s, ").Append(Degrees.ToString("F0", inv)).Append(" degrees); from (")
                .Append(_startEye.x.ToString("F2", inv)).Append(", ").Append(_startEye.y.ToString("F2", inv)).Append(", ").Append(_startEye.z.ToString("F2", inv)).Append(") heading ")
                .Append(_startYaw.ToString("F1", inv));
            return text.ToString();
        }
    }
}
