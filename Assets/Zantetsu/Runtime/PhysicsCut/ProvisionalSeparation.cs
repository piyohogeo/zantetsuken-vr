using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The sibling separation constraint of DESIGN 7.1.1: the `ProvisionalSeparationConstraint` between the two
    /// provisional siblings of one cut, as a Unity <see cref="ConfigurableJoint"/> fixed to an anchor-offset D6.
    /// <para>
    /// The adopted plane's normal is the joint's own axis (X), in the local space of the actor the joint sits on. The
    /// two tangents (Y, Z) are free to slide and the relative rotation about the normal is free; the relative rotations
    /// about the two tangents are locked. Along the normal the pair moves inside a symmetric limit of one metre with
    /// the anchors offset by one metre along the normal, so that the relation the two actors are built with sits at the
    /// inward boundary and two metres apart is the other one. **This is the interval the joint is configured with, not
    /// a guarantee about any step**, and DESIGN 7.1.1 asks for no such guarantee: stopping, pulling back, impulses,
    /// jitter and the difference in motion after it is removed are all accepted as they come, and so are the sliding
    /// and parting along the plane, the turn about the normal, contacts of the old colliders with the outside and the
    /// rotation about the tangents still held after the two have parted. Nothing detects, re-centres, widens or bounds
    /// anything: no tangential distance limit, no limit on the turn about the normal, no friction.
    /// </para>
    /// <para>
    /// The sign of the axis, which sibling carries the joint, which anchor carries the offset and how the second axis
    /// is chosen are implementation details, as DESIGN 7.1.1 says. No drive, spring, damper, projection or break is
    /// used, and once configured nothing of it is updated.
    /// </para>
    /// </summary>
    internal static class ProvisionalSeparation
    {
        /// <summary>The symmetric limit and the anchor offset along the normal, both in metres (DESIGN 7.1.1).</summary>
        private const float Metre = 1f;

        internal static ConfigurableJoint Configure(
            PhysicsOwnerSide positive, PhysicsOwnerSide negative, float3 planeNormalOwner)
        {
            return Configure(positive.Root, negative.Body, planeNormalOwner, Metre);
        }

        /// <summary>
        /// The same constraint between any two siblings built at one placement (the building hull trial's short sibling
        /// constraint, 2026-09-30): the joint on the positive side's Root, the axis the plane normal in that Root's local
        /// space, the symmetric limit and the anchor offset both <paramref name="halfOpening"/> -- so that the relation the
        /// pair is published with is the inward boundary (no constraint error at the publication) and 2 x halfOpening
        /// apart along the normal is the other one. Everything else as the Provisional constraint.
        /// </summary>
        internal static ConfigurableJoint Configure(GameObject positiveRoot, Rigidbody negativeBody, float3 planeNormalOwner, float halfOpening)
        {
            // Both actors are built at the source's placement, so each one's local space is that placement and the
            // normal in the owner's frame is already the normal in the joint owner's local space.
            float3 axis = math.normalize(planeNormalOwner);
            float3 secondary = Orthogonal(axis);

            ConfigurableJoint joint = positiveRoot.AddComponent<ConfigurableJoint>();
            joint.connectedBody = negativeBody;
            joint.autoConfigureConnectedAnchor = false;
            joint.axis = axis;
            joint.secondaryAxis = secondary;

            // The relation the pair is built with is the inward boundary: the anchors are one metre apart along the
            // normal, and the symmetric limit of one metre makes two metres apart the other boundary.
            // Configure always adds a fresh component. anchor=zero, projection=None, breakForce/breakTorque=+Inf,
            // enableCollision=false are Unity 6000.3.22f1 fresh defaults, covered by baseline-equivalence tests.
            // Do not turn this into a pooled/deserialized-joint configurator without restoring those writes.
            joint.connectedAnchor = (Vector3)(axis * halfOpening);

            // Along the normal: limited. Along the plane and about the normal: free. About the tangents: locked.
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Free;
            joint.zMotion = ConfigurableJointMotion.Free;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Locked;
            joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.linearLimit = new SoftJointLimit { limit = halfOpening, bounciness = 0f, contactDistance = 0f };

            // No drive, spring, damper, projection or break (DESIGN 7.1.1); fresh defaults remain untouched.
            return joint;
        }

        /// <summary>
        /// A finite direction that is not parallel to <paramref name="axis"/>, made orthogonal to it. The choice is
        /// settled by which of the three unit directions the axis leans on least, so the same axis always gives the
        /// same answer.
        /// </summary>
        private static float3 Orthogonal(float3 axis)
        {
            float3 absolute = math.abs(axis);
            float3 candidate = absolute.x <= absolute.y && absolute.x <= absolute.z
                ? new float3(1f, 0f, 0f)
                : absolute.y <= absolute.z
                    ? new float3(0f, 1f, 0f)
                    : new float3(0f, 0f, 1f);

            float3 orthogonal = candidate - (axis * math.dot(axis, candidate));
            float length = math.length(orthogonal);
            return length > 0f ? orthogonal / length : new float3(0f, 1f, 0f);
        }
    }
}
