using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Diagnostic only (the sliver oscillation of the college_001 Slash E2E, 2026-09-28): where the large back-and-forth of
    // a free building piece starts, and whether the constraint and mass properties it starts under are what the product
    // meant. Single-condition diagnostic switches, and two log lines: the root's density, and each sliver when first
    // seen. Its records (building-steps.csv, building-massprops.csv, building-handoff.csv) were retired on 2026-10-03.
    // None of this is a product change.
    //
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
            private const float SliverKg = 0.01f;
            private const int DiagLayer = 31;

            private double _density = double.NaN;
            private bool _diagNoContacts, _diagNoD6, _diagAnchorAtCom, _diagNoImpulse;
            private float _diagInertiaScale = 1f;
            private int _diagSolverIterations;

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

            // A Final piece seen for the first time: a diagnostic switch if it is a sliver, and its log line.
            private void BuildingDiagFirstSight(int frame, long step, int id, PhysicsFragmentOwner owner)
            {
                _world.Ledger.TryGetOrigin(new LogicalFragmentId(id), out CutOperationId origin, out _);
                if (owner.FixedByAnchors) return;
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
