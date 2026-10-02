using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The state of every Rigidbody that carries building colliders, with the bodies touching them and the floor, at
    /// one moment: written by the check when the simulate time grows or the step stalls, and replayed by
    /// <c>BuildingStateReplayPlayModeTests</c> under several configurations (2026-09-29 diagnosis).
    /// </summary>
    [Serializable]
    public sealed class BuildingStateSnapshot
    {
        [Serializable]
        public sealed class Shape
        {
            public string name;
            public Vector3 localPosition;   // relative to the body's root
            public Quaternion localRotation;
            public Vector3[] vertices;
            public int[] triangles;
            public bool convex, shadow, providesContacts, box;
            public Vector3 boxCentre, boxSize;   // a box collider (the floor, a static): in the body's root frame
            public float contactOffset;
            public int memberFragment;   // the fused member the collider belongs to, or -1
        }

        [Serializable]
        public sealed class Body
        {
            public string name, kind;   // building-group | building-piece | npc | prop | static | other
            public int id, groupKey, memberCount;
            public bool kinematic, sleeping, useGravity, isGroup, isStatic;
            public float mass, linearDamping, angularDamping, maxAngularVelocity;
            public Vector3 position, centreOfMassLocal, inertiaTensor, linearVelocity, angularVelocity;
            public Quaternion rotation, inertiaTensorRotation;
            public string collisionDetectionMode;
            public int solverIterations, solverVelocityIterations;
            public Shape[] colliders;
        }

        public string run, why;
        public int frame, stepId, defaultSolverIterations, defaultSolverVelocityIterations, skipsInARow;
        public float stepSeconds, simulateMs, remainingMs, expectedMs, defaultContactOffset, sleepThreshold, bounceThreshold, defaultMaxDepenetrationVelocity, defaultMaxAngularSpeed;
        public Vector3 gravity;
        public Body[] bodies;
    }

    // Diagnosis (2026-09-29): which Rigidbody every collider of the world's pieces really belongs to, counted from the
    // physics scene rather than from the fusion's own numbers; and the building-state capture. Only reads.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private sealed class BodyTally
            {
                public Rigidbody body;
                public string kind = "other";
                public int colliders, shadows, memberColliders, foreignColliders;
                public bool group, resolvable;
                public LogicalFragmentId fragment;
            }

            private int _pbCaptures;
            private bool _pbCapturedSlow, _pbCapturedStall;
            private StreamWriter _pbCsv;

            /// <summary>Every collider's real owner body, per kind, with the anomalies the ownership tables would hide.</summary>
            private void PlayableBodiesAudit(string when, int frame)
            {
                BuildingFusion fusion = _world != null ? _world.Fusion : null;
                var tallies = new Dictionary<Rigidbody, BodyTally>();
                int unattachedBuilding = 0, unattachedOther = 0, staticColliders = 0, memberRootsWithBody = 0;
                var lines = new List<string>();
                foreach (Collider c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (!c.enabled) continue;
                    Rigidbody rb = c.attachedRigidbody;
                    bool shadow = IsShadow(c.transform);
                    bool member = fusion != null && fusion.TryGetGroupOf(rb, out FusedGroup g) && g.TryGetMemberOf(c, out _);
                    if (rb == null)
                    {
                        staticColliders++;
                        if (member || shadow) unattachedBuilding++;
                        continue;
                    }

                    if (!tallies.TryGetValue(rb, out BodyTally t))
                    {
                        t = new BodyTally { body = rb };
                        if (fusion != null && fusion.TryGetGroupOf(rb, out FusedGroup group)) { t.kind = "building-group"; t.group = true; }
                        else if (_world.Owners.TryResolveFragment(rb, out LogicalFragmentId f, out _)) { t.kind = KindOf(LineageOf(f)); t.resolvable = true; t.fragment = f; if (t.kind == "building") t.kind = "building-piece"; }
                        tallies[rb] = t;
                    }

                    t.colliders++;
                    if (shadow) t.shadows++;
                    else if (member) t.memberColliders++;
                    else if (t.group) t.foreignColliders++;
                }

                // Members that still carry a body of their own (the fusion should have destroyed it).
                if (fusion != null)
                {
                    foreach (FusedGroup g in fusion.Groups)
                        foreach (LogicalFragmentId f in g.Fragments)
                            if (_world.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.Root != null && o.Root.GetComponent<Rigidbody>() != null) memberRootsWithBody++;
                }

                var perKind = new Dictionary<string, int[]>();   // bodies, dynamic, kinematic, sleeping, colliders, shadows, foreign, unknown
                foreach (BodyTally t in tallies.Values)
                {
                    if (!perKind.TryGetValue(t.kind, out int[] k)) perKind[t.kind] = k = new int[8];
                    k[0]++;
                    if (t.body.isKinematic) k[2]++; else { k[1]++; if (t.body.IsSleeping()) k[3]++; }
                    k[4] += t.colliders; k[5] += t.shadows; k[6] += t.foreignColliders;
                    if (t.kind == "other") k[7]++;
                }

                var summary = new System.Text.StringBuilder();
                summary.Append("playable bodies ").Append(when).Append(" frame ").Append(frame).Append(": rigidbodies ").Append(tallies.Count).Append(", static colliders ").Append(staticColliders);
                foreach (KeyValuePair<string, int[]> kv in perKind)
                {
                    int[] k = kv.Value;
                    summary.Append("; ").Append(kv.Key).Append(": bodies ").Append(k[0]).Append(" (dynamic ").Append(k[1]).Append(", of them asleep ").Append(k[3]).Append(", kinematic ").Append(k[2]).Append("), colliders ").Append(k[4]).Append(" (shadows ").Append(k[5]).Append(", not a member's ").Append(k[6]).Append(')');
                }

                summary.Append("; anomalies: building colliders with no body ").Append(unattachedBuilding).Append(", member roots still with a body ").Append(memberRootsWithBody).Append(", bodies in no table ").Append(perKind.TryGetValue("other", out int[] other) ? other[0] : 0);
                Log(summary.ToString());
                if (fusion != null)
                {
                    var groups = new List<string>();
                    foreach (BodyTally t in tallies.Values)
                    {
                        if (!t.group) continue;
                        fusion.TryGetGroupOf(t.body, out FusedGroup g);
                        groups.Add("group " + g.Key + " " + t.body.name + ": " + (t.body.isKinematic ? "kinematic" : t.body.IsSleeping() ? "dynamic asleep" : "dynamic awake") + ", members " + g.MemberCount + ", colliders " + t.colliders + " (member " + t.memberColliders + ", shadow " + t.shadows + ", foreign " + t.foreignColliders + ")");
                    }

                    groups.Sort();
                    int shown = 0;
                    foreach (string g in groups) { if (shown++ >= 60) { Log("playable bodies ... " + (groups.Count - 60) + " more groups"); break; } Log("playable bodies " + g); }
                }

                int unknownShown = 0;
                foreach (BodyTally t in tallies.Values)
                {
                    if (t.kind != "other" || unknownShown++ >= 10) continue;
                    Log("playable bodies unknown body " + t.body.name + " (" + (t.body.isKinematic ? "kinematic" : "dynamic") + ", colliders " + t.colliders + ", parent " + (t.body.transform.parent != null ? t.body.transform.parent.name : "none") + ")");
                }

                if (_pbCsv == null)
                {
                    _pbCsv = new StreamWriter(Path.Combine(directory, "playable-bodies.csv"));
                    _pbCsv.WriteLine("when,frame,rigidbodies,staticColliders,kind,bodies,dynamic,asleep,kinematic,colliders,shadows,foreign");
                }

                foreach (KeyValuePair<string, int[]> kv in perKind)
                {
                    int[] k = kv.Value;
                    _pbCsv.WriteLine(string.Join(",", when.Replace(',', ' '), frame, tallies.Count, staticColliders, kv.Key, k[0], k[1], k[3], k[2], k[4], k[5], k[6]));
                }

                _pbCsv.Flush();
            }

            private static bool IsShadow(Transform t)
            {
                for (Transform at = t; at != null; at = at.parent)
                {
                    if (at.name.StartsWith("Shadow of")) return true;
                }

                return false;
            }

            /// <summary>Called each frame from the rest diagnosis: the building state is captured once when the simulate time first exceeds 6 ms and once when the step has been skipped 30 frames in a row.</summary>
            private void PlayableBodiesFrame(int frame, double simulateSeconds, int skipsInARow, double remaining, double expected)
            {
                if (_world == null || _world.Fusion == null) return;
                if (!_pbCapturedSlow && simulateSeconds > 0.006)
                {
                    _pbCapturedSlow = true;
                    PlayableBodiesAudit("simulate over 6 ms", frame);
                    PlayableBodiesCapture("simulate " + (simulateSeconds * 1000).ToString("F2", Inv) + " ms", frame, simulateSeconds, skipsInARow, remaining, expected);
                }

                if (!_pbCapturedStall && skipsInARow >= 30)
                {
                    _pbCapturedStall = true;
                    PlayableBodiesAudit("stall (30 skipped frames)", frame);
                    PlayableBodiesCapture("stall: " + skipsInARow + " skipped frames, remaining " + (remaining * 1000).ToString("F2", Inv) + " ms, expected " + (expected * 1000).ToString("F2", Inv) + " ms", frame, simulateSeconds, skipsInARow, remaining, expected);
                }
            }

            /// <summary>Every body carrying a building collider, the bodies whose bounds come within 0.3 m of one, the static colliders there, at this moment.</summary>
            private void PlayableBodiesCapture(string why, int frame, double simulateSeconds, int skipsInARow, double remaining, double expected)
            {
                BuildingFusion fusion = _world.Fusion;
                var byBody = new Dictionary<Rigidbody, List<Collider>>();
                var statics = new List<Collider>();
                var buildingBounds = new List<Bounds>();
                Collider[] all = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (Collider c in all)
                {
                    if (!c.enabled) continue;
                    Rigidbody rb = c.attachedRigidbody;
                    if (rb == null) { statics.Add(c); continue; }
                    if (!byBody.TryGetValue(rb, out List<Collider> list)) byBody[rb] = list = new List<Collider>();
                    list.Add(c);
                }

                var kinds = new Dictionary<Rigidbody, string>();
                foreach (KeyValuePair<Rigidbody, List<Collider>> kv in byBody)
                {
                    string kind = "other";
                    if (fusion.TryGetGroupOf(kv.Key, out _)) kind = "building-group";
                    else if (_world.Owners.TryResolveFragment(kv.Key, out LogicalFragmentId f, out _)) { kind = KindOf(LineageOf(f)); if (kind == "building") kind = "building-piece"; }
                    kinds[kv.Key] = kind;
                    if (kind.StartsWith("building")) foreach (Collider c in kv.Value) buildingBounds.Add(c.bounds);
                }

                bool Near(Bounds b)
                {
                    b.Expand(0.6f);
                    foreach (Bounds bb in buildingBounds) if (b.Intersects(bb)) return true;
                    return false;
                }

                var bodies = new List<BuildingStateSnapshot.Body>();
                foreach (KeyValuePair<Rigidbody, List<Collider>> kv in byBody)
                {
                    string kind = kinds[kv.Key];
                    if (!kind.StartsWith("building"))
                    {
                        bool near = false;
                        foreach (Collider c in kv.Value) if (Near(c.bounds)) { near = true; break; }
                        if (!near) continue;
                    }

                    bodies.Add(SnapshotBody(kv.Key, kv.Value, kind, fusion));
                }

                foreach (Collider c in statics)
                {
                    if (!Near(c.bounds)) continue;
                    var body = new BuildingStateSnapshot.Body { name = c.name, kind = "static", isStatic = true, kinematic = true, position = c.transform.position, rotation = c.transform.rotation };
                    var shape = ShapeOf(c, c.transform, -1, false);
                    if (shape != null) body.colliders = new[] { shape };
                    if (body.colliders != null) bodies.Add(body);
                }

                var snapshot = new BuildingStateSnapshot
                {
                    run = directory, why = why, frame = frame, stepId = (int)CutPhysicsStep.Clock.StepId, stepSeconds = (float)CutPhysicsStep.Clock.StepSeconds,
                    simulateMs = (float)(simulateSeconds * 1000), remainingMs = (float)(remaining * 1000), expectedMs = (float)(expected * 1000), skipsInARow = skipsInARow,
                    gravity = Physics.gravity, defaultContactOffset = Physics.defaultContactOffset, sleepThreshold = Physics.sleepThreshold, bounceThreshold = Physics.bounceThreshold,
                    defaultMaxDepenetrationVelocity = Physics.defaultMaxDepenetrationVelocity, defaultMaxAngularSpeed = Physics.defaultMaxAngularSpeed,
                    defaultSolverIterations = Physics.defaultSolverIterations, defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                    bodies = bodies.ToArray(),
                };
                string path = Path.Combine(directory, "building-state-" + (++_pbCaptures) + ".json");
                File.WriteAllText(path, JsonUtility.ToJson(snapshot));
                int shapes = 0; foreach (BuildingStateSnapshot.Body b in bodies) shapes += b.colliders != null ? b.colliders.Length : 0;
                Log("playable bodies capture " + _pbCaptures + " (" + why + ") at frame " + frame + " step " + snapshot.stepId + ": " + bodies.Count + " bodies, " + shapes + " colliders, written " + path);
            }

            private BuildingStateSnapshot.Body SnapshotBody(Rigidbody rb, List<Collider> colliders, string kind, BuildingFusion fusion)
            {
                var body = new BuildingStateSnapshot.Body
                {
                    name = rb.name, kind = kind, id = rb.GetInstanceID(), kinematic = rb.isKinematic, sleeping = rb.IsSleeping(), useGravity = rb.useGravity, mass = rb.mass,
                    linearDamping = rb.linearDamping, angularDamping = rb.angularDamping, maxAngularVelocity = rb.maxAngularVelocity, position = rb.transform.position, rotation = rb.transform.rotation,
                    centreOfMassLocal = rb.centerOfMass, inertiaTensor = rb.inertiaTensor, inertiaTensorRotation = rb.inertiaTensorRotation, linearVelocity = rb.isKinematic ? Vector3.zero : rb.linearVelocity,
                    angularVelocity = rb.isKinematic ? Vector3.zero : rb.angularVelocity, collisionDetectionMode = rb.collisionDetectionMode.ToString(), solverIterations = rb.solverIterations, solverVelocityIterations = rb.solverVelocityIterations,
                };
                if (fusion.TryGetGroupOf(rb, out FusedGroup g)) { body.isGroup = true; body.groupKey = g.Key; body.memberCount = g.MemberCount; }
                var shapes = new List<BuildingStateSnapshot.Shape>();
                foreach (Collider c in colliders)
                {
                    int member = -1;
                    if (g != null && g.TryGetMemberOf(c, out LogicalFragmentId f)) member = f.value;
                    var shape = ShapeOf(c, rb.transform, member, IsShadow(c.transform));
                    if (shape != null) shapes.Add(shape);
                }

                body.colliders = shapes.ToArray();
                return body;
            }

            private static BuildingStateSnapshot.Shape ShapeOf(Collider c, Transform root, int member, bool shadow)
            {
                var shape = new BuildingStateSnapshot.Shape
                {
                    name = c.name, localPosition = root.InverseTransformPoint(c.transform.position), localRotation = Quaternion.Inverse(root.rotation) * c.transform.rotation,
                    contactOffset = c.contactOffset, providesContacts = c.providesContacts, shadow = shadow, memberFragment = member,
                };
                if (c is MeshCollider mesh)
                {
                    if (mesh.sharedMesh == null) return null;
                    shape.convex = mesh.convex; shape.vertices = mesh.sharedMesh.vertices; shape.triangles = mesh.sharedMesh.triangles;
                    return shape;
                }

                if (c is BoxCollider box)
                {
                    shape.box = true; shape.boxCentre = box.center; shape.boxSize = Vector3.Scale(box.size, box.transform.lossyScale);
                    return shape;
                }

                return null;
            }

            private void PlayableBodiesSummary()
            {
                if (_world == null || _world.Fusion == null) return;
                PlayableBodiesAudit("end", Time.frameCount);
                _pbCsv?.Dispose();
                _pbCsv = null;
            }
        }
    }
}
