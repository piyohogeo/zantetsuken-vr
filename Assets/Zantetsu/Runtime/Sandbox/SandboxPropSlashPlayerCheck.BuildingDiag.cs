using System.Collections.Generic;
using System.IO;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Diagnostic only (the sliver oscillation of the college_001 Slash E2E, 2026-09-28): where the large back-and-forth of
    // a free building piece starts, and whether the constraint and mass properties it starts under are what the product
    // meant. Two records, and single-condition diagnostic switches. None of this is a product change.
    //
    // - building-steps.csv: every Provisional side while its pair exists (stage P), and every free Final piece for its
    //   first OnsetSteps physics steps (stage F): COM, pose, velocities, the D6's reference and the rotation from it,
    //   the mass properties the body carries, and what its colliders touch.
    // - building-massprops.csv: for each Provisional side and each Final piece when first seen, the volume, centre of
    //   mass and inertia computed from the shape's own convexes (the root's density) beside what the body carries.
    // - Switches, each for one diagnostic run, applied to a "sliver" -- a free Final piece lighter than SliverKg when
    //   first seen -- unless said otherwise:
    //     -diagSliverNoContacts   its colliders stop colliding with anything (a layer that ignores every layer)
    //     -diagSliverNoD6         its World D6 is destroyed
    //     -diagSliverAnchorAtCom  its World D6's anchor moves to its centre of mass (the reference pose is kept)
    //     -diagNoImpulse          every cut's separation impulse is 0 (the whole run)
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private const int OnsetSteps = 90;
            private const float SliverKg = 0.01f;
            private const int DiagLayer = 31;

            private StreamWriter _stepRows, _massRows, _handoffRows;
            private double _density = double.NaN;
            private bool _diagNoContacts, _diagNoD6, _diagAnchorAtCom, _diagNoImpulse;
            private float _diagInertiaScale = 1f;
            private int _diagSolverIterations;
            private readonly Dictionary<int, long> _onsetFrom = new Dictionary<int, long>();
            private readonly HashSet<string> _provisionalSeen = new HashSet<string>();
            private readonly HashSet<string> _stepWritten = new HashSet<string>();

            private void BuildingDiagBegin()
            {
                _diagNoContacts = Has("-diagSliverNoContacts");
                _diagNoD6 = Has("-diagSliverNoD6");
                _diagAnchorAtCom = Has("-diagSliverAnchorAtCom");
                _diagNoImpulse = Has("-diagNoImpulse");
                // -diagSliverInertiaScale k: a sliver's inertia (its principal moments) multiplied by k, its mass kept.
                if (float.TryParse(Value("-diagSliverInertiaScale"), System.Globalization.NumberStyles.Float, Inv, out float scale) && scale > 0f)
                {
                    _diagInertiaScale = scale;
                }

                // -diagSliverSolverIterations n: a sliver's own position iterations set to n and velocity iterations to n / 4.
                if (int.TryParse(Value("-diagSliverSolverIterations"), System.Globalization.NumberStyles.Integer, Inv, out int iterations) && iterations > 0)
                {
                    _diagSolverIterations = iterations;
                }
                if (_world.Owners.TryGet(_building.Fragment, out PhysicsFragmentOwner root)
                    && MassProps(root.Shape, 1.0, out double rootVolume, out _, out _))
                {
                    _density = root.Mass / rootVolume;
                }

                if (_diagNoImpulse)
                {
                    _world.Driver.SeparationStrength = mass => 0f;
                }

                if (_diagNoContacts)
                {
                    for (int layer = 0; layer < 32; layer++) Physics.IgnoreLayerCollision(DiagLayer, layer, true);
                }

                Log("building diag: density " + _density.ToString("R", Inv) + " kg/m3 (root mass / root convex volume); switches"
                    + (_diagNoContacts ? " sliverNoContacts" : "") + (_diagNoD6 ? " sliverNoD6" : "") + (_diagAnchorAtCom ? " sliverAnchorAtCom" : "")
                    + (_diagNoImpulse ? " noImpulse" : "") + (_diagInertiaScale != 1f ? " sliverInertiaScale " + _diagInertiaScale.ToString("R", Inv) : "")
                    + (!_diagNoContacts && !_diagNoD6 && !_diagAnchorAtCom && !_diagNoImpulse && _diagInertiaScale == 1f ? " none" : "")
                    + "; a sliver is a free Final piece under " + SliverKg.ToString("R", Inv) + " kg");
                if (light) return;
                // At each side's Final establishment (the product's own test hook, after it wrote the body): the centre and
                // inertia rotation it decided, what the body then holds, and the centre of the final shape's own convexes.
                _handoffRows = new StreamWriter(Path.Combine(directory, "building-handoff.csv"));
                _handoffRows.WriteLine("frame,op,side,fixed,decidedMass,bodyMass,decidedComX,decidedComY,decidedComZ,bodyComX,bodyComY,bodyComZ,"
                    + "brepComX,brepComY,brepComZ,decidedVsBody,decidedVsBrep,decidedRotX,decidedRotY,decidedRotZ,decidedRotW,bodyRotX,bodyRotY,bodyRotZ,bodyRotW,"
                    + "shapeFrameX,shapeFrameY,shapeFrameZ,shapeFrameAngle,l2oX,l2oY,l2oZ,l2oAngle");
                FinalHandoffPublication.establishedHook = DiagEstablished;
                _stepRows = new StreamWriter(Path.Combine(directory, "building-steps.csv"));
                _stepRows.WriteLine("frame,stepId,stage,op,piece,fixed,mass,comLocalX,comLocalY,comLocalZ,comX,comY,comZ,rotX,rotY,rotZ,rotW,vX,vY,vZ,wX,wY,wZ,"
                    + "inertiaX,inertiaY,inertiaZ,d6,connectedX,connectedY,connectedZ,anchorX,anchorY,anchorZ,comToAnchor,offset,total,twist,swingY,swingZ,sleeping,contacts");
                _massRows = new StreamWriter(Path.Combine(directory, "building-massprops.csv"));
                _massRows.WriteLine("frame,stage,op,piece,fixed,bodyMass,shapeMass,massRatio,convexes,volume,bodyComX,bodyComY,bodyComZ,shapeComX,shapeComY,shapeComZ,comError,"
                    + "bodyI1,bodyI2,bodyI3,shapeI1,shapeI2,shapeI3,inertiaRelError,decidedMass,decidedI1,decidedI2,decidedI3,linearVelocity,angularVelocity,"
                    + "worldBodyComX,worldBodyComY,worldBodyComZ,worldBrepComX,worldBrepComY,worldBrepComZ,worldColliderComX,worldColliderComY,worldColliderComZ,"
                    + "colliderVolume,colliders,colliderVsBody,colliderVsBrep,colliderVertices,colliderToBrepMax,shapeFrameLocalX,shapeFrameLocalY,shapeFrameLocalZ,shapeFrameLocalAngle");
            }

            private void DiagEstablished(bool positive)
            {
                if (_handoffRows == null) return;
                foreach (ProvisionalCutTransaction x in _world.Driver.Transactions)
                {
                    ProvisionalOwnerPair pair = x.Pair;
                    if (pair == null) continue;
                    PhysicsOwnerSide side = positive ? pair.Positive : pair.Negative;
                    PhysicsOwnerShape shape = positive ? pair.PositiveShape : pair.NegativeShape;
                    if (side == null || side.Body == null) continue;
                    string key = x.Operation.value + (positive ? "+" : "-");
                    if (!_provisionalSeen.Add("handoff " + key)) continue;
                    Rigidbody body = side.Body;
                    bool computed = MassProps(shape, 1.0, out _, out double3 com, out _);
                    Vector3 decided = (Vector3)side.CenterOfMass, held = body.centerOfMass, brep = (Vector3)(float3)com;
                    Quaternion dr = (Quaternion)side.InertiaRotation, br = body.inertiaTensorRotation;
                    Transform frame = side.ShapeFrame != null ? side.ShapeFrame.transform : null;
                    float4x4 l2o = shape != null ? shape.LocalToOwner : float4x4.identity;
                    Quaternion l2oRotation = new quaternion(new float3x3(l2o.c0.xyz, l2o.c1.xyz, l2o.c2.xyz));
                    _handoffRows.WriteLine(string.Join(",", Time.frameCount, x.Operation.value, positive ? "+" : "-", side.FixedByAnchors, F((float)side.Mass), F(body.mass),
                        F(decided.x), F(decided.y), F(decided.z), F(held.x), F(held.y), F(held.z), F(brep.x), F(brep.y), F(brep.z),
                        F(Vector3.Distance(decided, held)), F(computed ? Vector3.Distance(decided, brep) : float.NaN),
                        F(dr.x), F(dr.y), F(dr.z), F(dr.w), F(br.x), F(br.y), F(br.z), F(br.w),
                        frame != null ? F(frame.localPosition.x) : "", frame != null ? F(frame.localPosition.y) : "", frame != null ? F(frame.localPosition.z) : "",
                        frame != null ? F(Quaternion.Angle(frame.localRotation, Quaternion.identity)) : "",
                        F(l2o.c3.x), F(l2o.c3.y), F(l2o.c3.z), F(Quaternion.Angle(l2oRotation, Quaternion.identity))));
                }
            }

            private void BuildingDiagClose()
            {
                if (_handoffRows != null && FinalHandoffPublication.establishedHook == DiagEstablished) FinalHandoffPublication.establishedHook = null;
                _handoffRows?.Dispose();
                _handoffRows = null;
                _stepRows?.Dispose();
                _stepRows = null;
                _massRows?.Dispose();
                _massRows = null;
            }

            // The volume, the centre of mass and the inertia about it (density given) of a shape's convexes, in the
            // owner's frame: each face fanned from its first corner to a point of the convex, tetrahedra summed.
            private static unsafe bool MassProps(PhysicsOwnerShape shape, double density, out double volume, out double3 com, out double3x3 inertia)
            {
                volume = 0.0;
                com = double3.zero;
                inertia = double3x3.zero;
                if (shape == null || shape.IsFreed) return false;
                double3 first = double3.zero;
                double3x3 second = double3x3.zero;
                float4x4 toOwner = shape.LocalToOwner;
                for (int k = 0; k < shape.ConvexCount; k++)
                {
                    Zantetsu.ConvexCut.ConvexBrepRange r = shape.Convex(k);
                    Zantetsu.ConvexCut.ConvexBrepBank b = shape.BankOf(k);
                    double3 P(int local) => math.transform(toOwner, b.vertices[r.vertexBase + local]);
                    double3 o = P(0);
                    double v = 0.0;
                    double3 m1 = double3.zero;
                    double3x3 m2 = double3x3.zero;
                    for (int f = 0; f < r.faceCount; f++)
                    {
                        int s = b.faceOffsets[r.faceBase + f], e = b.faceOffsets[r.faceBase + f + 1];
                        double3 a = P(b.faceIndices[r.faceIndexBase + s]);
                        for (int i = s + 1; i + 1 < e; i++)
                        {
                            double3 c1 = P(b.faceIndices[r.faceIndexBase + i]);
                            double3 c2 = P(b.faceIndices[r.faceIndexBase + i + 1]);
                            double d = math.dot(a - o, math.cross(c1 - o, c2 - o)) / 6.0;
                            double3 sum = o + a + c1 + c2;
                            v += d;
                            m1 += d * sum / 4.0;
                            m2 += (d / 20.0) * (Outer(o) + Outer(a) + Outer(c1) + Outer(c2) + Outer(sum));
                        }
                    }

                    double sign = v < 0.0 ? -1.0 : 1.0;
                    volume += sign * v;
                    first += sign * m1;
                    second += sign * m2;
                }

                if (volume <= 0.0) return false;
                com = first / volume;
                double3x3 central = second - volume * Outer(com);
                double trace = central.c0.x + central.c1.y + central.c2.z;
                inertia = density * (trace * double3x3.identity - central);
                return true;
            }

            private static double3x3 Outer(double3 a) => new double3x3(a * a.x, a * a.y, a * a.z);

            private static double3x3 BodyTensor(Vector3 principal, Quaternion rotation)
            {
                var r = new double3x3((float3x3)new float3x3((quaternion)rotation));
                var d = new double3x3(new double3(principal.x, 0, 0), new double3(0, principal.y, 0), new double3(0, 0, principal.z));
                return math.mul(math.mul(r, d), math.transpose(r));
            }

            private static double3 SortedEigen(double3x3 m)
            {
                // Symmetric 3x3: Jacobi sweeps, enough for a comparison.
                double3x3 a = m;
                for (int sweep = 0; sweep < 32; sweep++)
                {
                    for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        double apq = a[q][p];
                        if (math.abs(apq) < 1e-30) continue;
                        double theta = 0.5 * math.atan2(2.0 * apq, a[q][q] - a[p][p]);
                        double c = math.cos(theta), s = math.sin(theta);
                        double3x3 j = double3x3.identity;
                        j[p][p] = c; j[q][q] = c; j[q][p] = s; j[p][q] = -s;
                        a = math.mul(math.transpose(j), math.mul(a, j));
                    }
                }

                double x = a.c0.x, y = a.c1.y, z = a.c2.z;
                double lo = math.min(x, math.min(y, z)), hi = math.max(x, math.max(y, z));
                return new double3(lo, x + y + z - lo - hi, hi);
            }

            private static double Norm(double3x3 m) => math.sqrt(math.dot(m.c0, m.c0) + math.dot(m.c1, m.c1) + math.dot(m.c2, m.c2));

            // The ground truth the other two are set beside: the volume and centre of the colliders the body really has,
            // from their meshes in the world (each mesh a closed convex; its triangles fanned from the origin).
            private static bool ColliderCentre(Rigidbody body, out double volume, out Vector3 centre, out int colliders)
            {
                volume = 0.0;
                centre = Vector3.zero;
                colliders = 0;
                double3 first = double3.zero;
                foreach (MeshCollider c in body.GetComponentsInChildren<MeshCollider>())
                {
                    if (!c.enabled || c.sharedMesh == null || !c.sharedMesh.isReadable) continue;
                    colliders++;
                    Vector3[] vs = c.sharedMesh.vertices;
                    int[] tris = c.sharedMesh.triangles;
                    Matrix4x4 m = c.transform.localToWorldMatrix;
                    double v = 0.0;
                    double3 m1 = double3.zero;
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        double3 a = (float3)m.MultiplyPoint3x4(vs[tris[i]]), b = (float3)m.MultiplyPoint3x4(vs[tris[i + 1]]), d = (float3)m.MultiplyPoint3x4(vs[tris[i + 2]]);
                        double vol = math.dot(a, math.cross(b, d)) / 6.0;
                        v += vol;
                        m1 += vol * (a + b + d) / 4.0;
                    }

                    double sign = v < 0.0 ? -1.0 : 1.0;
                    volume += sign * v;
                    first += sign * m1;
                }

                if (volume <= 0.0) return false;
                centre = (Vector3)(float3)(first / volume);
                return true;
            }

            // How far the colliders' own vertices, in the world, are from the nearest vertex of the shape's convexes placed
            // the way this check places them (the body's transform times LocalToOwner): 0 when the two are one geometry.
            private static unsafe float ColliderToBrep(Rigidbody body, PhysicsOwnerShape shape, out int count)
            {
                count = 0;
                if (shape == null || shape.IsFreed) return float.NaN;
                var brep = new List<Vector3>();
                float4x4 toWorld = math.mul((float4x4)body.transform.localToWorldMatrix, shape.LocalToOwner);
                for (int k = 0; k < shape.ConvexCount; k++)
                {
                    Zantetsu.ConvexCut.ConvexBrepRange r = shape.Convex(k);
                    Zantetsu.ConvexCut.ConvexBrepBank b = shape.BankOf(k);
                    for (int i = 0; i < r.vertexCount; i++) brep.Add((Vector3)math.transform(toWorld, b.vertices[r.vertexBase + i]));
                }

                float worst = 0f;
                foreach (MeshCollider c in body.GetComponentsInChildren<MeshCollider>())
                {
                    if (!c.enabled || c.sharedMesh == null || !c.sharedMesh.isReadable) continue;
                    foreach (Vector3 lv in c.sharedMesh.vertices)
                    {
                        Vector3 wv = c.transform.TransformPoint(lv);
                        float best = float.PositiveInfinity;
                        foreach (Vector3 bv in brep) best = Mathf.Min(best, (bv - wv).sqrMagnitude);
                        worst = Mathf.Max(worst, Mathf.Sqrt(best));
                        count++;
                    }
                }

                return count > 0 ? worst : float.NaN;
            }

            private void MassRow(int frame, string stage, long op, string piece, bool fixedSide, Rigidbody body, PhysicsOwnerShape shape,
                double decidedMass, float3 decidedInertia, Vector3 v, Vector3 w)
            {
                if (_massRows == null || body == null) return;
                bool computed = MassProps(shape, double.IsNaN(_density) ? 1.0 : _density, out double volume, out double3 com, out double3x3 inertia);
                double3x3 bodyTensor = BodyTensor(body.inertiaTensor, body.inertiaTensorRotation);
                double3 be = SortedEigen(bodyTensor), se = computed ? SortedEigen(inertia) : double3.zero;
                double shapeMass = computed ? volume * _density : double.NaN;
                Vector3 bc = body.centerOfMass;
                Vector3 worldBody = body.worldCenterOfMass;
                Vector3 worldBrep = body.transform.TransformPoint((Vector3)(float3)com);
                bool hasCollider = ColliderCentre(body, out double colliderVolume, out Vector3 worldCollider, out int colliderCount);
                float colliderToBrep = ColliderToBrep(body, shape, out int colliderVertices);
                MeshCollider any = body.GetComponentInChildren<MeshCollider>();
                Vector3 frameLocal = any != null ? body.transform.InverseTransformPoint(any.transform.position) : Vector3.zero;
                float frameAngle = any != null ? Quaternion.Angle(body.transform.rotation, any.transform.rotation) : float.NaN;
                _massRows.WriteLine(string.Join(",", frame, stage, op, piece, fixedSide, F(body.mass), F((float)shapeMass), F((float)(body.mass / shapeMass)),
                    shape != null ? shape.ConvexCount : 0, F((float)volume), F(bc.x), F(bc.y), F(bc.z), F((float)com.x), F((float)com.y), F((float)com.z),
                    F((float)math.distance((double3)(float3)bc, com)), F((float)be.x), F((float)be.y), F((float)be.z), F((float)se.x), F((float)se.y), F((float)se.z),
                    F(computed ? (float)(Norm(bodyTensor - inertia) / Norm(inertia)) : float.NaN), F((float)decidedMass),
                    F(decidedInertia.x), F(decidedInertia.y), F(decidedInertia.z), F(v.magnitude), F(w.magnitude),
                    F(worldBody.x), F(worldBody.y), F(worldBody.z), F(worldBrep.x), F(worldBrep.y), F(worldBrep.z),
                    F(worldCollider.x), F(worldCollider.y), F(worldCollider.z), F((float)colliderVolume), colliderCount,
                    F(hasCollider ? Vector3.Distance(worldCollider, worldBody) : float.NaN), F(hasCollider && computed ? Vector3.Distance(worldCollider, worldBrep) : float.NaN),
                    colliderVertices, F(colliderToBrep), F(frameLocal.x), F(frameLocal.y), F(frameLocal.z), F(frameAngle)));
            }

            private void StepRow(int frame, long step, string stage, long op, string piece, bool fixedSide, Rigidbody body, Transform root,
                ConfigurableJoint joint, PhysicsFragmentOwner owner)
            {
                if (_stepRows == null || body == null || root == null) return;
                string key = stage + op + piece + "@" + step;
                if (!_stepWritten.Add(key)) return;
                Vector3 com = body.worldCenterOfMass, v = body.linearVelocity, w = body.angularVelocity, cl = body.centerOfMass;
                Quaternion rot = root.rotation;
                float offset = float.NaN, total = float.NaN, twist = float.NaN, swingY = float.NaN, swingZ = float.NaN;
                Vector3 anchor = root.position, connected = Vector3.zero;
                if (joint != null)
                {
                    anchor = root.TransformPoint(joint.anchor);
                    connected = joint.connectedAnchor;
                    Vector3 away = anchor - connected;
                    offset = new Vector2(away.x, away.z).magnitude;
                    Vector3 axis = joint.axis.normalized, secondary = joint.secondaryAxis.normalized;
                    Quaternion relative = rot * Quaternion.LookRotation(Vector3.Cross(axis, secondary), secondary);
                    total = Quaternion.Angle(relative, Quaternion.identity);
                    SwingTwist(relative, out twist, out swingY, out swingZ);
                }

                _stepRows.WriteLine(string.Join(",", frame, step, stage, op, piece, fixedSide, F(body.mass), F(cl.x), F(cl.y), F(cl.z), F(com.x), F(com.y), F(com.z),
                    F(rot.x), F(rot.y), F(rot.z), F(rot.w), F(v.x), F(v.y), F(v.z), F(w.x), F(w.y), F(w.z),
                    F(body.inertiaTensor.x), F(body.inertiaTensor.y), F(body.inertiaTensor.z), joint != null,
                    F(connected.x), F(connected.y), F(connected.z), F(anchor.x), F(anchor.y), F(anchor.z), F(Vector3.Distance(com, anchor)),
                    F(offset), F(total), F(twist), F(swingY), F(swingZ), body.IsSleeping(), owner != null ? Contacts(owner, 0) : ""));
            }

            // Every frame: the Provisional sides of every accepted cut while its pair exists, and the free Final pieces in
            // their first steps.
            private void BuildingDiagFrame(int frame, long step)
            {
                foreach (Accepted a in _accepted)
                {
                    if (!_world.Owners.TryGetProvisional(a.operation, out ProvisionalOwnerPair pair) || pair.IsEnded) continue;
                    DiagSide(frame, step, a.operation.value, "+", pair.Positive, pair.PositiveShape);
                    DiagSide(frame, step, a.operation.value, "-", pair.Negative, pair.NegativeShape);
                }

                foreach (KeyValuePair<int, long> onset in _onsetFrom)
                {
                    if (step - onset.Value > OnsetSteps) continue;
                    var fragment = new LogicalFragmentId(onset.Key);
                    if (!_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.Body == null) continue;
                    _world.Ledger.TryGetOrigin(fragment, out CutOperationId origin, out _);
                    StepRow(frame, step, "F", origin.value, "f" + onset.Key, owner.FixedByAnchors, owner.Body, owner.Root.transform, owner.BuildingWorldConstraint, owner);
                }
            }

            private void DiagSide(int frame, long step, long op, string sign, PhysicsOwnerSide side, PhysicsOwnerShape shape)
            {
                if (side == null || side.Body == null || side.Root == null) return;
                string piece = "op" + op + sign;
                if (_provisionalSeen.Add(piece))
                {
                    MassRow(frame, "P", op, piece, side.FixedByAnchors, side.Body, shape, side.Mass, side.InertiaTensor,
                        (Vector3)side.LinearVelocity, (Vector3)side.AngularVelocity);
                }

                StepRow(frame, step, "P", op, piece, side.FixedByAnchors, side.Body, side.Root.transform, side.BuildingWorld, null);
            }

            // A Final piece seen for the first time: its mass properties, its onset window, and a diagnostic switch if it
            // is a sliver.
            private void BuildingDiagFirstSight(int frame, long step, int id, PhysicsFragmentOwner owner)
            {
                _world.Ledger.TryGetOrigin(new LogicalFragmentId(id), out CutOperationId origin, out _);
                MassRow(frame, "F", origin.value, "f" + id, owner.FixedByAnchors, owner.Body, owner.Shape, owner.Body.mass, (float3)owner.Body.inertiaTensor,
                    owner.Body.linearVelocity, owner.Body.angularVelocity);
                if (owner.FixedByAnchors) return;
                _onsetFrom[id] = step;
                if (owner.Body.mass >= SliverKg) return;
                string applied = "";
                if (_diagNoContacts)
                {
                    foreach (Collider c in owner.Root.GetComponentsInChildren<Collider>(true)) c.gameObject.layer = DiagLayer;
                    applied += " contacts off";
                }

                ConfigurableJoint joint = owner.BuildingWorldConstraint;
                if (_diagNoD6 && joint != null)
                {
                    Object.Destroy(joint);
                    applied += " D6 destroyed";
                }

                if (_diagSolverIterations > 0)
                {
                    owner.Body.solverIterations = _diagSolverIterations;
                    owner.Body.solverVelocityIterations = Mathf.Max(1, _diagSolverIterations / 4);
                    applied += " solver iterations " + _diagSolverIterations + "/" + Mathf.Max(1, _diagSolverIterations / 4);
                }

                if (_diagInertiaScale != 1f)
                {
                    owner.Body.inertiaTensor *= _diagInertiaScale;
                    applied += " inertia x" + _diagInertiaScale.ToString("R", Inv);
                }

                if (_diagAnchorAtCom && joint != null)
                {
                    joint.anchor = owner.Body.centerOfMass;
                    joint.connectedAnchor = owner.Body.worldCenterOfMass;
                    applied += " D6 anchor moved to the COM";
                }

                Log("building diag sliver: fragment " + id + " from op" + origin.value + " mass " + owner.Body.mass.ToString("R", Inv) + " kg depth "
                    + owner.Building.SplitDepth + " COM " + owner.Body.worldCenterOfMass.ToString("F3") + " actor origin " + owner.Root.transform.position.ToString("F3")
                    + " frame " + frame + " step " + step + (applied.Length > 0 ? "; diagnostic:" + applied : ""));
            }
        }
    }
}
