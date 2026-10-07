using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Diagnosis (the playable city unit, 2026-09-29): which pieces go abnormally fast and what the long frames hold. Only
    // reads; nothing of the product is changed.
    //   * playable-frames.csv, one row a frame: the owners live, the bodies awake and asleep, the system constraints, the
    //     pieces over the fast speed now -- to be joined by frame with the frame timeline's markers.
    //   * The first pieces past the anomalous speed: kind, lineage, split depth, mass, the frames its cut was accepted,
    //     published and committed, its last frames before the crossing and the frames after it (position, velocity), its
    //     building World D6 and how far its anchor point has drifted from where the joint holds it, and what it touches.
    //   * The piece that went farthest: whether up (its highest centre) or below the floor (the first frame under it).
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private const float PlayableAnomalousSpeed = 20f, PlayableSpinCapture = 45f;
            private int _pcCaptureKinds = 100;   // the floor-crossing and spin captures are numbered from 101, apart from the speed anomalies
            private const int PlayableHistory = 8, PlayableAnomalies = 10;

            private sealed class PlayableTrack
            {
                public readonly Vector3[] centre = new Vector3[PlayableHistory], velocity = new Vector3[PlayableHistory];
                public readonly int[] frame = new int[PlayableHistory];
                public int count;
                public float maxY = float.NegativeInfinity, minY = float.PositiveInfinity, farthest;
                public int maxYFrame, belowFloorFrame = -1, farthestFrame;
                public Vector3 first, belowFloorAt, belowFloorVelocity;
                public int anomaly = -1, afterLeft;
                public bool spinCaptured;
                public System.Text.StringBuilder report;
            }

            private readonly Dictionary<LogicalFragmentId, PlayableTrack> _pcTracks = new Dictionary<LogicalFragmentId, PlayableTrack>();
            private StreamWriter _pcFrames;
            private int _pcAnomalies;

            private void PlayableDiagFrame(int frame, float floorTop)
            {
                if (_pcFrames == null)
                {
                    _pcFrames = DetailWriter("playable-frames.csv");
                    _pcFrames.WriteLine("frame,t,owners,awake,asleep,kinematic,constraints,fastNow,anomalies");
                }

                PlayableCaptureFrame(frame);
                PlayableRestFrame(frame, floorTop);
                int awake = 0, asleep = 0, kinematic = 0, fastNow = 0;
                foreach (LogicalFragmentId fragment in _pcFragments)
                {
                    if (!_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.Body == null || owner.IsWithdrawn) continue;
                    Rigidbody body = owner.Body;
                    if (body.isKinematic) { kinematic++; continue; }
                    if (body.IsSleeping()) asleep++; else awake++;
                    Vector3 v = body.linearVelocity;
                    if (v.magnitude > PlayableFastSpeed) fastNow++;
                    if (!_pcPieces.TryGetValue(fragment, out PlayablePiece piece)) continue;   // building and prop pieces only
                    if (!_pcTracks.TryGetValue(fragment, out PlayableTrack track))
                    {
                        _pcTracks[fragment] = track = new PlayableTrack { first = body.worldCenterOfMass };
                    }

                    Vector3 c = body.worldCenterOfMass;
                    int slot = track.count % PlayableHistory;
                    track.centre[slot] = c; track.velocity[slot] = v; track.frame[slot] = frame; track.count++;
                    if (c.y > track.maxY) { track.maxY = c.y; track.maxYFrame = frame; }
                    track.minY = Mathf.Min(track.minY, c.y);
                    float d = (c - track.first).magnitude;
                    if (d > track.farthest) { track.farthest = d; track.farthestFrame = frame; }
                    if (track.belowFloorFrame < 0 && c.y < floorTop - 0.5f)
                    {
                        track.belowFloorFrame = frame; track.belowFloorAt = c; track.belowFloorVelocity = v;
                        // The crossing itself, with the frames before it: a capture of its own kind.
                        PlayableCaptureAnomaly(++_pcCaptureKinds, fragment, owner, frame, "floor crossing");
                    }

                    // A spin near the cap (the source of a fast birth): captured once per piece, before it is cut again.
                    if (!track.spinCaptured && body.angularVelocity.magnitude >= PlayableSpinCapture)
                    {
                        track.spinCaptured = true;
                        PlayableCaptureAnomaly(++_pcCaptureKinds, fragment, owner, frame, "spin " + body.angularVelocity.magnitude.ToString("F1", Inv) + " rad/s");
                    }

                    if (track.anomaly < 0 && _pcAnomalies < PlayableAnomalies && v.magnitude > PlayableAnomalousSpeed)
                    {
                        track.anomaly = _pcAnomalies++;
                        track.afterLeft = PlayableHistory;
                        PlayableCaptureAnomaly(track.anomaly + 1, fragment, owner, frame, "speed " + v.magnitude.ToString("F1", Inv) + " m/s");
                        track.report = new System.Text.StringBuilder();
                        track.report.Append("playable city anomaly ").Append(track.anomaly + 1).Append(": ").Append(piece.kind).Append(" piece ").Append(fragment.value)
                            .Append(" at frame ").Append(frame).Append(" speed ").Append(v.magnitude.ToString("F1", Inv)).Append(" m/s; ").Append(Describe(fragment, owner, piece))
                            .Append("; touching ").Append(PlayableContacts(owner, staticOnly: false)).Append("\n    before:");
                        for (int k = Mathf.Max(0, track.count - PlayableHistory); k < track.count; k++)
                        {
                            int s = k % PlayableHistory;
                            track.report.Append(" f").Append(track.frame[s]).Append(" c").Append(track.centre[s].ToString("F2")).Append(" v").Append(track.velocity[s].ToString("F1"));
                        }

                        track.report.Append("\n    after:");
                    }
                    else if (track.afterLeft > 0)
                    {
                        track.afterLeft--;
                        track.report.Append(" f").Append(frame).Append(" c").Append(c.ToString("F2")).Append(" v").Append(v.ToString("F1"));
                        if (track.afterLeft == 0) Log(track.report.ToString());
                    }
                }

                _pcFrames.WriteLine(string.Join(",", frame, Time.realtimeSinceStartupAsDouble.ToString("F4", Inv), _pcFragments.Count, awake, asleep, kinematic,
                    _world.Owners.SystemConstraintCount, fastNow, _pcAnomalies));
            }

            // Kind, lineage, depth, mass, the frames of the cut that made it, and its World D6 with the drift of its anchor.
            private string Describe(LogicalFragmentId fragment, PhysicsFragmentOwner owner, PlayablePiece piece)
            {
                var text = new System.Text.StringBuilder();
                text.Append("lineage ").Append(LineageOf(fragment)).Append(" depth ").Append(owner.Building.SplitDepth).Append(" building ").Append(owner.Building.IsBuildingDerived)
                    .Append(" mass ").Append(owner.Mass.ToString("G4", Inv)).Append(" kg anchored ").Append(piece.anchored).Append(" first seen frame ").Append(piece.firstFrame);
                if (_world.Ledger.TryGetOrigin(fragment, out CutOperationId origin, out float side))
                {
                    Accepted made = _accepted.FirstOrDefault(a => a.operation == origin);
                    text.Append("; made by op ").Append(origin.value).Append(" side ").Append(side);
                    if (made != null) text.Append(" accepted f").Append(made.acceptedFrame).Append(" provisional f").Append(made.provisionalFrame).Append(" published f").Append(made.publishedFrame).Append(" committed f").Append(made.committedFrame);
                }

                ConfigurableJoint joint = owner.BuildingWorldConstraint;
                if (joint == null) text.Append("; no World D6");
                else
                {
                    Vector3 held = joint.connectedBody != null ? joint.connectedBody.transform.TransformPoint(joint.connectedAnchor) : joint.connectedAnchor;
                    Vector3 at = joint.transform.TransformPoint(joint.anchor);
                    text.Append("; World D6 anchor drift ").Append((at - held).magnitude.ToString("F3", Inv)).Append(" m (linear limit ").Append(joint.linearLimit.limit.ToString("F3", Inv))
                        .Append(" m, twist ").Append(joint.lowAngularXLimit.limit.ToString("F1", Inv)).Append("..").Append(joint.highAngularXLimit.limit.ToString("F1", Inv))
                        .Append(", swing ").Append(joint.angularYLimit.limit.ToString("F1", Inv)).Append('/').Append(joint.angularZLimit.limit.ToString("F1", Inv)).Append(')');
                }

                return text.ToString();
            }

            private void PlayableDiagSummary()
            {
                _pcFrames?.Dispose();
                _pcFrames = null;
                Guarded("rest summary", PlayableRestSummary);
                foreach (PlayableTrack t in _pcTracks.Values.Where(t => t.anomaly >= 0 && t.afterLeft > 0)) Log(t.report.ToString() + " (run ended)");
                KeyValuePair<LogicalFragmentId, PlayableTrack> far = _pcTracks.OrderByDescending(p => p.Value.farthest).FirstOrDefault();
                if (far.Value == null) return;
                PlayableTrack f = far.Value;
                string way = f.belowFloorFrame >= 0 ? "went below the floor" : f.maxY - f.first.y > 3f ? "rose" : "stayed above the floor";
                Log("playable city farthest piece " + far.Key.value + ": " + f.farthest.ToString("F1", Inv) + " m from where it was first seen (" + f.first.ToString("F2") + ") at frame "
                    + f.farthestFrame + "; highest centre y " + f.maxY.ToString("F2", Inv) + " at frame " + f.maxYFrame + ", lowest " + f.minY.ToString("F2", Inv)
                    + "; " + way + (f.belowFloorFrame >= 0 ? " at frame " + f.belowFloorFrame + " at " + f.belowFloorAt.ToString("F2") + " velocity " + f.belowFloorVelocity.ToString("F1") : "")
                    + (f.anomaly >= 0 ? "; it is anomaly " + (f.anomaly + 1) : "; not among the first anomalies"));
                // Floating at the end: a live, dynamic building or prop piece whose centre is 1.5 m or more above the floor,
                // hardly moving, touching nothing -- and how many of them carry a World D6 (and how far its point drifted).
                int floating = 0, floatingWithD6 = 0, floatingAsleep = 0;
                var floatingLines = new List<string>();
                foreach (KeyValuePair<LogicalFragmentId, PlayablePiece> p in _pcPieces)
                {
                    if (!_world.Owners.TryGet(p.Key, out PhysicsFragmentOwner owner) || owner.Body == null || owner.IsWithdrawn || owner.Body.isKinematic) continue;
                    Vector3 c = owner.Body.worldCenterOfMass;
                    if (c.y < 1.6f || owner.Body.linearVelocity.magnitude > 0.1f) continue;
                    // Held up by nothing: straight below its lowest point, the first collider not its own is more than 0.2 m
                    // away (a piece resting on another -- contact without penetration -- is not floating).
                    Bounds b = owner.Root.GetComponentsInChildren<Collider>().Select(x => x.bounds).Aggregate((a, x) => { a.Encapsulate(x); return a; });
                    float gap = float.PositiveInfinity;
                    foreach (RaycastHit h in Physics.RaycastAll(new Vector3(c.x, b.min.y + 0.01f, c.z), Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore))
                        if (!h.collider.transform.IsChildOf(owner.Root.transform)) gap = Mathf.Min(gap, h.distance - 0.01f);
                    if (gap <= 0.2f) continue;
                    floating++;
                    if (owner.BuildingWorldConstraint != null) floatingWithD6++;
                    if (owner.Body.IsSleeping()) floatingAsleep++;
                    if (floatingLines.Count < 12) floatingLines.Add(p.Value.kind + " " + p.Key.value + " at " + c.ToString("F2") + " gap below " + gap.ToString("F2", Inv)
                        + " m asleep " + owner.Body.IsSleeping() + " " + Describe(p.Key, owner, p.Value));
                }

                Log("playable city floating pieces at the end (centre >= 1.5 m above the floor, <= 0.1 m/s, nothing within 0.2 m straight below): " + floating + ", of them with a World D6 " + floatingWithD6 + ", asleep " + floatingAsleep);
                foreach (string line in floatingLines) Log("playable city floating " + line);
                int below = _pcTracks.Values.Count(t => t.belowFloorFrame >= 0), rose = _pcTracks.Values.Count(t => t.belowFloorFrame < 0 && t.maxY - t.first.y > 3f);
                Log("playable city pieces tracked " + _pcTracks.Count + ": went below the floor " + below + ", rose more than 3 m " + rose + ", anomalies (over " + PlayableAnomalousSpeed + " m/s) " + _pcAnomalies + " logged");
            }
        }
    }
}
