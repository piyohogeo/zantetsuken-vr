using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The always-kinematic building's drops read back frame by frame (TL, 2026-10-01): each drop's members against
    // x(t) = x0 + (t / T) d + 1/2 g t (t - T) at the time the trial last applied, apart -- part way (the curve), at its
    // completion (x0 + d, no arc left), at a stop by a re-cut (the point on the curve kept, and the next drop of those
    // members starting from it), and the members' and the body's rotations unchanged.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private const double DropTolerance = 1e-4;   // m, as the in-plane judgement before it (not widened)

            private sealed class DropTrack
            {
                public int midReads, arcReads;
                public double maxMidError, maxArc, endError = double.NaN, maxRotation;
                public bool endRead;
            }

            private readonly Dictionary<int, DropTrack> _bhDrops = new Dictionary<int, DropTrack>();
            // A stop's transition (the stopped members, the stopping hit's children, the next drop) followed apart (2026-10-01).
            private readonly HullDropStartTracker _bhStarts = new HullDropStartTracker();

            private static double WorldDistance(Transform member, Vector3 localOff)
            {
                Transform parent = member.parent;
                return (parent != null ? parent.TransformVector(localOff) : localOff).magnitude;
            }

            private void HullDropsFrame(BuildingHullFusion h)
            {
                _bhStarts.Frame(h, _world.Ledger);
                foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
                {
                    if (!_bhDrops.TryGetValue(r.id, out DropTrack track)) _bhDrops[r.id] = track = new DropTrack();
                    if (track.endRead) continue;
                    double t = r.appliedSeconds;
                    foreach (BuildingHullFusion.DropMember m in r.members)
                    {
                        if (m.root == null) continue;
                        track.maxRotation = Math.Max(track.maxRotation, Quaternion.Angle(m.root.localRotation, m.rotation));
                        if (r.end == null)
                        {
                            if (!(r.Phase > 0.0 && r.Phase < 1.0)) continue;
                            Vector3 expected = BuildingHullFusion.DropPosition(m.from, r.deltaLocal, r.gravityLocal, r.seconds, t);
                            track.maxMidError = Math.Max(track.maxMidError, WorldDistance(m.root, m.root.localPosition - expected));
                            track.midReads++;
                            double arc = WorldDistance(m.root, r.gravityLocal * (float)(0.5 * t * (t - r.seconds)));
                            track.maxArc = Math.Max(track.maxArc, arc);
                            track.arcReads++;
                        }
                    }

                    if (r.end == null) continue;
                    // Its end, read once: completed at x0 + d (no arc left); stopped on its curve at the time applied, the pose kept for the next curve.
                    track.endRead = true;
                    double worst = 0.0;
                    int read = 0;
                    foreach (BuildingHullFusion.DropMember m in r.members)
                    {
                        // The pose the trial read back at the drop's end (a member a re-cut retires right after is gone by now), and now when it still stands.
                        if (!m.atRead) continue;
                        Vector3 expected = r.end == "completed" ? m.from + r.deltaLocal : BuildingHullFusion.DropPosition(m.from, r.deltaLocal, r.gravityLocal, r.seconds, t);
                        double off = (m.at - expected).magnitude;
                        if (m.root != null) off = Math.Max(off, WorldDistance(m.root, m.root.localPosition - expected));
                        worst = Math.Max(worst, off);
                        read++;
                    }

                    track.endError = read > 0 ? worst : double.NaN;
                }
            }

            private void HullDropsSummary(BuildingHullFusion h)
            {
                int completed = 0, stopped = 0, running = 0, cleared = 0, midReads = 0, arcReads = 0;
                double midError = 0, completedError = 0, stoppedError = 0, arc = 0, rotation = 0;
                int completedRead = 0, stoppedRead = 0;
                foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
                {
                    _bhDrops.TryGetValue(r.id, out DropTrack k);
                    k ??= new DropTrack();
                    if (r.end == "completed") completed++; else if (r.end == "stopped") stopped++; else if (r.end == "cleared") cleared++; else running++;
                    midReads += k.midReads; arcReads += k.arcReads; midError = Math.Max(midError, k.maxMidError); arc = Math.Max(arc, k.maxArc); rotation = Math.Max(rotation, k.maxRotation);
                    if (!double.IsNaN(k.endError))
                    {
                        if (r.end == "completed") { completedError = Math.Max(completedError, k.endError); completedRead++; }
                        if (r.end == "stopped") { stoppedError = Math.Max(stoppedError, k.endError); stoppedRead++; }
                    }

                    Log("building hull drop " + r.id + " (hit " + r.hit + ", group " + r.group + ", building " + r.building + "): g " + ((Vector3)r.gravityWorld).ToString("R") + " m/s2, T " + r.seconds.ToString("R") + " s, d " + ((Vector3)r.deltaWorld).ToString("R")
                        + " m (|d| " + Unity.Mathematics.math.length(r.deltaWorld).ToString("R") + "), started at clock " + r.start.ToString("R") + ", last applied t " + r.appliedSeconds.ToString("R") + " s (u " + r.Phase.ToString("R") + ", " + r.placements + " placements), "
                        + (r.end ?? "running") + "; members " + r.members.Count + "; read back: part way " + k.midReads + " (most off the curve " + k.maxMidError.ToString("R") + " m, the arc at most " + k.maxArc.ToString("R") + " m), at its end "
                        + (double.IsNaN(k.endError) ? "not read" : k.endError.ToString("R") + " m off " + (r.end == "completed" ? "x0 + d" : "its point on the curve")) + ", rotation at most " + k.maxRotation.ToString("R") + " deg" + (r.end == "stopped" ? "; stopped by hit " + r.stoppedByHit : ""));
                }

                Log("building hull drops: " + h.DropRecords.Count + " (completed " + completed + ", stopped by a re-cut " + stopped + ", cleared " + cleared + ", running " + running + "); the trial's own read-back at each end: " + h.SlideMembersMeasured + " members, most off the formula " + h.MaxDropPositionError.ToString("R") + " m, rotation at most " + h.MaxDropRotationDegrees.ToString("R") + " deg");
                Expect(midReads > 0 && midError < DropTolerance && arc > 0.01,
                    "[hull drop] part way, the members stood on x0 + (t/T) d + 1/2 g t (t - T) at the time applied (" + midReads + " reads, most off " + midError.ToString("R") + " m, limit " + DropTolerance.ToString("R") + " m; the arc seen up to " + arc.ToString("R") + " m)");
                Expect(completed > 0 && completedRead > 0 && completedError < DropTolerance && h.MaxDropPositionError < DropTolerance,
                    "[hull drop] at completion no arc was left: the members stood at x0 + d (" + completed + " completed, " + completedRead + " read, most off " + completedError.ToString("R") + " m; the trial's own read-back most off " + h.MaxDropPositionError.ToString("R") + " m)");
                // The stop's transition: the stopped members held, the stopping hit's children born there, the next drop's start checked once and kept.
                foreach (string line in _bhStarts.Lines()) Log("building hull drop stop: " + line);
                bool startsPassed = _bhStarts.Judge(out string startDetail);
                Expect(stopped > 0 && (stoppedRead == 0 || stoppedError < DropTolerance) && h.MaxDropPositionError < DropTolerance && startsPassed,
                    "[hull drop] a drop stopped by a re-cut kept its point on the curve, its members held there until moved, and the next drop started from it (" + stopped + " stopped, " + stoppedRead + " read after, most off " + stoppedError.ToString("R") + " m; " + startDetail + ")");
                Expect(rotation < 1e-3 && h.MaxDropRotationDegrees < 1e-3,
                    "[hull drop] no member's nor body's rotation changed (most " + rotation.ToString("R") + " deg read each frame, " + h.MaxDropRotationDegrees.ToString("R") + " deg by the trial at each end)");
            }
        }
    }
}
