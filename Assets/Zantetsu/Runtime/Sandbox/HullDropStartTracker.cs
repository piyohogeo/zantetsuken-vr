using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// CHECK ONLY (TL, 2026-10-01): a drop stopped by a re-cut, followed through the one transition it implies -- the
    /// stopped members' poses, the children the stopping hit's own operations make of them (born where their source
    /// stood), and the next drop that moves any of them -- matched by fragment, operation and drop id, each start once,
    /// its result kept. Until a held member moves, it must stay at its stop pose (read every frame). Once a later drop
    /// moves it, its start is checked once and the old stop pose is no longer asked of it, nor of its children; a cut by
    /// another hit ends the hold the same way. A mismatch found is never cleared by a later completion, and a stop whose
    /// start was never checked is not a pass.
    /// </summary>
    public sealed class HullDropStartTracker
    {
        public const double Tolerance = 1e-4;   // m

        private sealed class Hold
        {
            public LogicalFragmentId fragment;
            public Vector3 pose;   // local to the group Root
            public int stopDrop, byHit;
            public string origin;
            public bool handedOn, ended;
            public string endedHow;
            public double maxOff;
            public int reads;
        }

        public sealed class StartCheck
        {
            public int drop, stopDrop;
            public LogicalFragmentId fragment;
            public string origin;
            public double off;
            public bool ok;
        }

        private readonly Dictionary<LogicalFragmentId, Hold> _holds = new Dictionary<LogicalFragmentId, Hold>();
        private readonly HashSet<int> _seenDrops = new HashSet<int>(), _stopsTaken = new HashSet<int>();
        private readonly Dictionary<int, int> _startsByStop = new Dictionary<int, int>();
        private readonly List<StartCheck> _starts = new List<StartCheck>();
        private readonly List<string> _holdFailures = new List<string>();

        public IReadOnlyList<StartCheck> Starts => _starts;
        public IReadOnlyList<string> HoldFailures => _holdFailures;
        public int Stops => _stopsTaken.Count;
        public int HoldReads { get; private set; }
        public double MaxHoldOff { get; private set; }
        public int Handovers { get; private set; }
        public int HoldsEndedByAnotherCut { get; private set; }

        private static double World(Transform parent, Vector3 localOff) => (parent != null ? parent.TransformVector(localOff) : localOff).magnitude;

        private static HullGroup.DisplayMember Member(BuildingHullFusion h, LogicalFragmentId fragment, out HullGroup group)
        {
            foreach (HullGroup g in h.Groups)
                foreach (HullGroup.DisplayMember m in g.Members)
                    if (m.fragment == fragment && m.root != null) { group = g; return m; }
            group = null;
            return null;
        }

        /// <summary>One frame's reading, after the trial's Step: new stops, the stopping hits' children, new drops' starts, the holds.</summary>
        public void Frame(BuildingHullFusion h, LogicalCutLedger ledger)
        {
            // 1. A drop newly stopped by a re-cut: its members held at the poses read at the stop.
            foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
            {
                if (r.end != "stopped" || !_stopsTaken.Add(r.id)) continue;
                _startsByStop[r.id] = 0;
                foreach (BuildingHullFusion.DropMember m in r.members)
                    if (m.atRead) _holds[m.fragment] = new Hold { fragment = m.fragment, pose = m.at, stopDrop = r.id, byHit = r.stoppedByHit, origin = "stopped in drop " + r.id };
            }

            // 2. The stopping hit's own operations on a held member: its children are born where it stood, and held in its place.
            foreach (Hold hold in new List<Hold>(_holds.Values))
            {
                if (hold.handedOn || hold.ended) continue;
                BuildingHullFusion.HullHit hit = null;
                foreach (BuildingHullFusion.HullHit x in h.Hits) if (x.id == hold.byHit) hit = x;
                if (hit == null) continue;
                foreach (CutOperationId op in hit.displayOperations)
                {
                    if (!ledger.TryGetOperation(op, out LogicalCutOperation o) || o.source != hold.fragment) continue;
                    foreach (LogicalFragmentId child in new[] { o.positive, o.negative })
                        if (!_holds.ContainsKey(child)) _holds[child] = new Hold { fragment = child, pose = hold.pose, stopDrop = hold.stopDrop, byHit = hold.byHit, origin = "child of " + hold.fragment.value + " (operation " + op.value + " of hit " + hold.byHit + ")" };
                    hold.handedOn = true;
                    hold.endedHow = "cut by its stopping hit " + hold.byHit + " (operation " + op.value + ")";
                    Handovers++;
                }
            }

            // 3. A drop newly begun: each held member it moves starts at its hold's pose, checked once and kept; then no longer held.
            foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
            {
                if (!_seenDrops.Add(r.id)) continue;
                foreach (BuildingHullFusion.DropMember m in r.members)
                {
                    if (!_holds.TryGetValue(m.fragment, out Hold hold) || hold.ended || hold.handedOn) continue;
                    double off = World(m.root != null ? m.root.parent : null, m.from - hold.pose);
                    _starts.Add(new StartCheck { drop = r.id, stopDrop = hold.stopDrop, fragment = m.fragment, origin = hold.origin, off = off, ok = off < Tolerance });
                    _startsByStop[hold.stopDrop] = _startsByStop.TryGetValue(hold.stopDrop, out int n) ? n + 1 : 1;
                    hold.ended = true;
                    hold.endedHow = "moved on by drop " + r.id;
                }
            }

            // 4. The holds still standing: each held member where it was stopped, or ended by another hit's cut (its children are not held).
            foreach (Hold hold in _holds.Values)
            {
                if (hold.ended || hold.handedOn) continue;
                HullGroup.DisplayMember m = Member(h, hold.fragment, out _);
                if (m == null)
                {
                    if (!ledger.IsCurrentTarget(hold.fragment)) { hold.ended = true; hold.endedHow = "cut by another hit (its children not held to the old pose)"; HoldsEndedByAnotherCut++; }
                    continue;
                }

                double off = World(m.root.transform.parent, m.root.transform.localPosition - hold.pose);
                hold.reads++;
                HoldReads++;
                hold.maxOff = Math.Max(hold.maxOff, off);
                MaxHoldOff = Math.Max(MaxHoldOff, off);
                if (off >= Tolerance && !_holdFailures.Exists(f => f.StartsWith("fragment " + hold.fragment.value + " ", StringComparison.Ordinal)))
                    _holdFailures.Add("fragment " + hold.fragment.value + " (" + hold.origin + ") left its stop pose by " + off.ToString("R") + " m before any drop moved it, at frame " + Time.frameCount);
            }
        }

        /// <summary>Passed: every stop's start was checked at least once, every start matched, every hold kept its pose. A failure found stays.</summary>
        public bool Judge(out string detail)
        {
            var missing = new List<int>();
            foreach (int stop in _stopsTaken) if (!_startsByStop.TryGetValue(stop, out int n) || n == 0) missing.Add(stop);
            var bad = _starts.FindAll(s => !s.ok);
            double worst = 0.0;
            foreach (StartCheck s in _starts) worst = Math.Max(worst, s.off);
            detail = "stops " + Stops + ", starts checked " + _starts.Count + " (mismatched " + bad.Count + ", most off " + worst.ToString("R") + " m), stops with no start checked [" + string.Join(" ", missing) + "]; holds read " + HoldReads
                + " times (most off " + MaxHoldOff.ToString("R") + " m, failures " + _holdFailures.Count + "), handed to children " + Handovers + ", ended by another cut " + HoldsEndedByAnotherCut
                + (bad.Count > 0 ? "; mismatches: " + string.Join("; ", bad.ConvertAll(s => "drop " + s.drop + " fragment " + s.fragment.value + " (" + s.origin + ") off " + s.off.ToString("R") + " m")) : "")
                + (_holdFailures.Count > 0 ? "; " + string.Join("; ", _holdFailures) : "");
            return Stops > 0 && missing.Count == 0 && bad.Count == 0 && _holdFailures.Count == 0;
        }

        public IEnumerable<string> Lines()
        {
            foreach (StartCheck s in _starts) yield return "start: drop " + s.drop + " fragment " + s.fragment.value + " (" + s.origin + ", stop of drop " + s.stopDrop + ") off " + s.off.ToString("R") + " m => " + (s.ok ? "matched" : "MISMATCHED");
            foreach (Hold hold in _holds.Values) yield return "hold: fragment " + hold.fragment.value + " (" + hold.origin + "): read " + hold.reads + " times, most off " + hold.maxOff.ToString("R") + " m; " + (hold.endedHow ?? "still held");
        }
    }
}
