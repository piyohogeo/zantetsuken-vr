using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Diagnosis (the piece replay unit, 2026-09-29): the physics state a replay needs, saved when a piece first goes
    // anomalously fast -- the piece, the pieces overlapping or within reach of it, and the floor -- as it was on the
    // frames before the crossing (a ring of every dynamic piece's pose and velocities, kept each frame), with the shapes,
    // mass properties, collider settings, joints and the project's physics defaults. Written as playable-anomaly-N.json
    // beside the check's other files (PieceReplaySnapshot is the schema). Only reads.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private const int CaptureRing = 12, CaptureAfter = 8, CaptureMax = 8;
            // Each kind of capture has room of its own, so that the spins met first cannot use up the room of the crossings and the speeds met later (k3 did).
            private const int CaptureSpin = 2, CaptureCrossing = 3, CaptureSpeed = 3;
            private int _capSpin, _capCrossing, _capSpeed;
            private readonly Dictionary<LogicalFragmentId, PieceReplaySnapshot.State[]> _capRings = new Dictionary<LogicalFragmentId, PieceReplaySnapshot.State[]>();
            private readonly Dictionary<LogicalFragmentId, int> _capRingCount = new Dictionary<LogicalFragmentId, int>();
            private readonly List<(PieceReplaySnapshot snapshot, int left)> _capOpen = new List<(PieceReplaySnapshot, int)>();
            private int _capSaved;
            private readonly Collider[] _capNear = new Collider[64];

            private static PieceReplaySnapshot.State StateOf(Rigidbody body, int frame, int stepId) => new PieceReplaySnapshot.State
            {
                frame = frame, stepId = stepId, position = body.position, rotation = body.rotation, centreOfMass = body.worldCenterOfMass,
                linearVelocity = body.linearVelocity, angularVelocity = body.angularVelocity, sleeping = body.IsSleeping(),
            };

            // Every dynamic building or prop piece's state this frame, into its ring.
            private void PlayableCaptureFrame(int frame)
            {
                int stepId = CutPhysicsStep.Clock != null ? (int)CutPhysicsStep.Clock.StepId : -1;
                foreach (LogicalFragmentId fragment in _pcFragments)
                {
                    if (!_pcPieces.ContainsKey(fragment)) continue;
                    if (!_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.Body == null || owner.IsWithdrawn) continue;   // kinematic pieces too: a partner's pose over the period can then be checked
                    if (!_capRings.TryGetValue(fragment, out PieceReplaySnapshot.State[] ring))
                    {
                        _capRings[fragment] = ring = new PieceReplaySnapshot.State[CaptureRing];
                        _capRingCount[fragment] = 0;
                    }

                    int n = _capRingCount[fragment];
                    ring[n % CaptureRing] = StateOf(owner.Body, frame, stepId);
                    _capRingCount[fragment] = n + 1;
                }

                // Open captures take their "after" states and are written when complete.
                for (int i = _capOpen.Count - 1; i >= 0; i--)
                {
                    (PieceReplaySnapshot snapshot, int left) = _capOpen[i];
                    foreach (PieceReplaySnapshot.Body body in snapshot.bodies)
                    {
                        if (body.kinematic || !_world.Owners.TryGet(new LogicalFragmentId(body.fragment), out PhysicsFragmentOwner owner) || owner.Body == null) continue;
                        var list = new List<PieceReplaySnapshot.State>(body.after ?? Array.Empty<PieceReplaySnapshot.State>()) { StateOf(owner.Body, frame, stepId) };
                        body.after = list.ToArray();
                    }

                    if (--left > 0) { _capOpen[i] = (snapshot, left); continue; }
                    _capOpen.RemoveAt(i);
                    string path = Path.Combine(directory, "playable-anomaly-" + snapshot.anomaly + ".json");
                    if (runParts.detail) File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
                    Log("playable city capture " + snapshot.anomaly + ": " + (runParts.detail ? "written " + path : "not written (the detailed diagnostics are left out)") + " (" + snapshot.bodies.Length + " bodies, floor " + (snapshot.floor != null) + ")");
                }
            }

            private PieceReplaySnapshot.State[] RingOf(LogicalFragmentId fragment)
            {
                if (!_capRings.TryGetValue(fragment, out PieceReplaySnapshot.State[] ring)) return Array.Empty<PieceReplaySnapshot.State>();
                int n = _capRingCount[fragment];
                var states = new List<PieceReplaySnapshot.State>();
                for (int k = Math.Max(0, n - CaptureRing); k < n; k++) states.Add(ring[k % CaptureRing]);
                return states.ToArray();
            }

            private PieceReplaySnapshot.Body BodyOf(LogicalFragmentId fragment, PhysicsFragmentOwner owner, string role)
            {
                Rigidbody rb = owner.Body;
                Transform root = owner.Root.transform;
                var colliders = new List<PieceReplaySnapshot.Shape>();
                foreach (MeshCollider c in owner.Root.GetComponentsInChildren<MeshCollider>())
                {
                    if (!c.enabled || c.sharedMesh == null) continue;
                    // The collider's frame relative to the body's root, and the convex mesh's own vertices.
                    Vector3 lp = root.InverseTransformPoint(c.transform.position);
                    Quaternion lr = Quaternion.Inverse(root.rotation) * c.transform.rotation;
                    PhysicsMaterial m = c.sharedMaterial;
                    colliders.Add(new PieceReplaySnapshot.Shape
                    {
                        name = c.name, localPosition = lp, localRotation = lr, vertices = c.sharedMesh.vertices, triangles = c.sharedMesh.triangles, convex = c.convex,
                        contactOffset = c.contactOffset, hasMaterial = m != null,
                        staticFriction = m != null ? m.staticFriction : -1f, dynamicFriction = m != null ? m.dynamicFriction : -1f, bounciness = m != null ? m.bounciness : -1f,
                        frictionCombine = m != null ? m.frictionCombine.ToString() : "default", bounceCombine = m != null ? m.bounceCombine.ToString() : "default",
                    });
                }

                var joints = new List<PieceReplaySnapshot.Joint>();
                foreach (ConfigurableJoint j in owner.Root.GetComponents<ConfigurableJoint>())
                {
                    LogicalFragmentId connected = default;
                    if (j.connectedBody != null)
                        foreach (LogicalFragmentId f in _pcFragments)
                            if (_world.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.Body == j.connectedBody) { connected = f; break; }
                    joints.Add(new PieceReplaySnapshot.Joint
                    {
                        kind = j.connectedBody == null ? "world" : "connected", connectedFragment = connected.value, connectedBodyIsKinematic = j.connectedBody != null && j.connectedBody.isKinematic,
                        anchor = j.anchor, connectedAnchor = j.connectedAnchor, axis = j.axis, secondaryAxis = j.secondaryAxis, autoConfigureConnectedAnchor = j.autoConfigureConnectedAnchor,
                        xMotion = j.xMotion.ToString(), yMotion = j.yMotion.ToString(), zMotion = j.zMotion.ToString(),
                        angularXMotion = j.angularXMotion.ToString(), angularYMotion = j.angularYMotion.ToString(), angularZMotion = j.angularZMotion.ToString(),
                        linearLimit = j.linearLimit.limit, lowAngularXLimit = j.lowAngularXLimit.limit, highAngularXLimit = j.highAngularXLimit.limit,
                        angularYLimit = j.angularYLimit.limit, angularZLimit = j.angularZLimit.limit, enableCollision = j.enableCollision, enablePreprocessing = j.enablePreprocessing,
                        projectionMode = j.projectionMode.ToString(),
                    });
                }

                return new PieceReplaySnapshot.Body
                {
                    fragment = fragment.value, role = role, kind = _pcPieces.TryGetValue(fragment, out PlayablePiece p) ? p.kind : "other", lineage = LineageOf(fragment),
                    depth = owner.Building.SplitDepth, buildingDerived = owner.Building.IsBuildingDerived, kinematic = rb.isKinematic, anchored = owner.FixedByAnchors,
                    mass = rb.mass, centreOfMassLocal = rb.centerOfMass, inertiaTensor = rb.inertiaTensor, inertiaTensorRotation = rb.inertiaTensorRotation,
                    useGravity = rb.useGravity, linearDamping = rb.linearDamping, angularDamping = rb.angularDamping, maxAngularVelocity = rb.maxAngularVelocity,
                    collisionDetectionMode = rb.collisionDetectionMode.ToString(), interpolation = rb.interpolation.ToString(), solverIterations = rb.solverIterations, solverVelocityIterations = rb.solverVelocityIterations,
                    now = StateOf(rb, Time.frameCount, CutPhysicsStep.Clock != null ? (int)CutPhysicsStep.Clock.StepId : -1), before = RingOf(fragment),
                    colliders = colliders.ToArray(), joints = joints.ToArray(),
                };
            }

            // The piece, everything within 0.3 m of its bounds, and the floor: written once the "after" frames are in.
            private void PlayableCaptureAnomaly(int anomaly, LogicalFragmentId fragment, PhysicsFragmentOwner owner, int frame, string why)
            {
                if (_capSaved >= CaptureMax) return;
                bool spin = why.StartsWith("spin"), crossing = why.StartsWith("floor");
                if ((spin ? _capSpin : crossing ? _capCrossing : _capSpeed) >= (spin ? CaptureSpin : crossing ? CaptureCrossing : CaptureSpeed)) return;
                if (spin) _capSpin++; else if (crossing) _capCrossing++; else _capSpeed++;
                _capSaved++;
                var bodies = new List<PieceReplaySnapshot.Body> { BodyOf(fragment, owner, "target") };
                var seen = new HashSet<LogicalFragmentId> { fragment };
                PieceReplaySnapshot.Floor floor = null;
                Bounds b = owner.Root.GetComponentsInChildren<Collider>().Where(c => c.enabled).Select(c => c.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; });
                int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.3f, _capNear, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    Collider other = _capNear[i];
                    if (other == null || other.transform.IsChildOf(owner.Root.transform)) continue;
                    if (other is BoxCollider && other.attachedRigidbody == null) continue;   // the floor: taken below, always

                    foreach (LogicalFragmentId f in _pcFragments)
                    {
                        if (seen.Contains(f) || !_world.Owners.TryGet(f, out PhysicsFragmentOwner o) || o.Root == null || !other.transform.IsChildOf(o.Root.transform)) continue;
                        seen.Add(f);
                        bodies.Add(BodyOf(f, o, "partner"));
                    }
                }

                // The floor, always (a replay without it has nothing to land on): the crowd's floor box, or the first static
                // box straight below the piece.
                GameObject floorObject = GameObject.Find("MobPlan Floor");
                BoxCollider floorBox = floorObject != null ? floorObject.GetComponent<BoxCollider>() : null;
                if (floorBox == null)
                    foreach (RaycastHit h in Physics.RaycastAll(b.center, Vector3.down, 100f, ~0, QueryTriggerInteraction.Ignore))
                        if (h.collider is BoxCollider box && h.collider.attachedRigidbody == null) { floorBox = box; break; }
                if (floorBox != null)
                    floor = new PieceReplaySnapshot.Floor { name = floorBox.name, centre = floorBox.bounds.center, size = floorBox.bounds.size, contactOffset = floorBox.contactOffset, hasMaterial = floorBox.sharedMaterial != null };

                // A piece born fast (first seen this frame) took its motion from the source the cut replaced: that source's
                // last states before the cut (its ring is kept after it retires), and the cut that made the piece.
                if (_world.Ledger.TryGetOrigin(fragment, out CutOperationId origin, out float side) && _world.Ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    PieceReplaySnapshot.State[] sourceRing = RingOf(cut.source);
                    bodies[0].madeByOperation = origin.value; bodies[0].madeOnSide = side; bodies[0].sourceFragment = cut.source.value; bodies[0].sourceBefore = sourceRing;
                    if (sourceRing.Length > 0)
                    {
                        PieceReplaySnapshot.State last = sourceRing[sourceRing.Length - 1];
                        Log("playable city capture " + anomaly + ": source " + cut.source.value + " (op " + origin.value + " side " + side + ") last seen f" + last.frame + " |v| " + last.linearVelocity.magnitude.ToString("F1", Inv)
                            + " |w| " + last.angularVelocity.magnitude.ToString("F1", Inv) + " rad/s (max angular " + Physics.defaultMaxAngularSpeed + ") at " + last.centreOfMass.ToString("F2") + "; piece now |v| " + owner.Body.linearVelocity.magnitude.ToString("F1", Inv)
                            + " |w| " + owner.Body.angularVelocity.magnitude.ToString("F1", Inv) + " first seen " + (_pcPieces.TryGetValue(fragment, out PlayablePiece pp) ? pp.firstFrame : -1) + " (this frame " + frame + ")");
                    }
                }

                // A joint partner not within reach is still a body of the replay.
                foreach (PieceReplaySnapshot.Joint j in bodies[0].joints)
                {
                    var f = new LogicalFragmentId(j.connectedFragment);
                    if (j.kind == "connected" && f.IsSet && !seen.Contains(f) && _world.Owners.TryGet(f, out PhysicsFragmentOwner o)) { seen.Add(f); bodies.Add(BodyOf(f, o, "joint partner")); }
                }

                var snapshot = new PieceReplaySnapshot
                {
                    anomaly = anomaly, why = why, frame = frame, stepId = CutPhysicsStep.Clock != null ? (int)CutPhysicsStep.Clock.StepId : -1, run = directory,
                    stepSeconds = CutPhysicsStep.Clock != null ? (float)CutPhysicsStep.Clock.StepSeconds : 1f / 45f,
                    gravity = Physics.gravity, defaultContactOffset = Physics.defaultContactOffset, sleepThreshold = Physics.sleepThreshold, bounceThreshold = Physics.bounceThreshold,
                    defaultSolverIterations = Physics.defaultSolverIterations, defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                    defaultMaxDepenetrationVelocity = Physics.defaultMaxDepenetrationVelocity, defaultMaxAngularSpeed = Physics.defaultMaxAngularSpeed,
                    bodies = bodies.ToArray(), floor = floor,
                };
                _capOpen.Add((snapshot, CaptureAfter));
                Log("playable city capture " + anomaly + " (" + why + "): piece " + fragment.value + " with " + (bodies.Count - 1) + " partner(s)" + (floor != null ? " and the floor" : "") + "; after " + CaptureAfter + " frames it is written");
            }
        }
    }

    /// <summary>The state a piece replay starts from (PlayableCityCapture writes it; the piece replay test reads it).</summary>
    [Serializable]
    public sealed class PieceReplaySnapshot
    {
        [Serializable] public sealed class State
        {
            public int frame, stepId;
            public Vector3 position, centreOfMass, linearVelocity, angularVelocity;
            public Quaternion rotation;
            public bool sleeping;
        }

        [Serializable] public sealed class Shape
        {
            public string name;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3[] vertices;
            public int[] triangles;
            public bool convex, hasMaterial;
            public float contactOffset, staticFriction, dynamicFriction, bounciness;
            public string frictionCombine, bounceCombine;
        }

        [Serializable] public sealed class Joint
        {
            public string kind;
            public int connectedFragment;
            public bool connectedBodyIsKinematic, autoConfigureConnectedAnchor, enableCollision, enablePreprocessing;
            public Vector3 anchor, connectedAnchor, axis, secondaryAxis;
            public string xMotion, yMotion, zMotion, angularXMotion, angularYMotion, angularZMotion, projectionMode;
            public float linearLimit, lowAngularXLimit, highAngularXLimit, angularYLimit, angularZLimit;
        }

        [Serializable] public sealed class Body
        {
            public int fragment, depth, solverIterations, solverVelocityIterations;
            public string role, kind, lineage, collisionDetectionMode, interpolation;
            public bool buildingDerived, kinematic, anchored, useGravity;
            public float mass, linearDamping, angularDamping, maxAngularVelocity;
            public Vector3 centreOfMassLocal, inertiaTensor;
            public Quaternion inertiaTensorRotation;
            public State now;
            public State[] before, after;
            // The cut that made this piece and the source it took its motion from (the source's last states before the cut).
            public int madeByOperation, sourceFragment;
            public float madeOnSide;
            public State[] sourceBefore;
            public Shape[] colliders;
            public Joint[] joints;
        }

        [Serializable] public sealed class Floor
        {
            public string name;
            public Vector3 centre, size;
            public float contactOffset;
            public bool hasMaterial;
        }

        public int anomaly, frame, stepId, defaultSolverIterations, defaultSolverVelocityIterations;
        public string run, why;
        public float stepSeconds, defaultContactOffset, sleepThreshold, bounceThreshold, defaultMaxDepenetrationVelocity, defaultMaxAngularSpeed;
        public Vector3 gravity;
        public Body[] bodies;
        public Floor floor;
    }
}
