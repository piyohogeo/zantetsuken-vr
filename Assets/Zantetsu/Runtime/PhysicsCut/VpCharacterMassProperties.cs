using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What a character not yet cut weighs, as numbers (DESIGN 9, D-197): its prepared cut keeps them from its
    /// preparation and gives them to the cut's source body at the cut, with where the character then stands. No body
    /// follows the character to carry them.
    /// <para>
    /// **The frame.** The centre of mass and the inertia's principal axes are in the character root's frame taken as a
    /// rigid one -- its position and rotation, no scale -- as a Rigidbody's are in its own Transform's. With the root
    /// at position p and rotation q, the centre of mass stands at p + q * <see cref="CentreOfMass"/> and the principal
    /// axes are q * <see cref="InertiaTensorRotation"/>.
    /// </para>
    /// </summary>
    public readonly struct VpCharacterMassProperties
    {
        public readonly float Mass;

        /// <summary>The centre of mass, in the character root's rigid frame (metres).</summary>
        public readonly Vector3 CentreOfMass;

        /// <summary>The principal moments of inertia about the centre of mass.</summary>
        public readonly Vector3 InertiaTensor;

        /// <summary>The principal axes, in the character root's rigid frame.</summary>
        public readonly Quaternion InertiaTensorRotation;

        public VpCharacterMassProperties(float mass, Vector3 centreOfMass, Vector3 inertiaTensor, Quaternion inertiaTensorRotation)
        {
            Mass = mass;
            CentreOfMass = centreOfMass;
            InertiaTensor = inertiaTensor;
            InertiaTensorRotation = inertiaTensorRotation;
        }

        /// <summary>Whether a body can be given these: finite, a mass and moments above zero, a rotation that is one.</summary>
        public bool IsUsable
        {
            get
            {
                float q = InertiaTensorRotation.x * InertiaTensorRotation.x + InertiaTensorRotation.y * InertiaTensorRotation.y
                    + InertiaTensorRotation.z * InertiaTensorRotation.z + InertiaTensorRotation.w * InertiaTensorRotation.w;
                return float.IsFinite(Mass) && Mass > 0f
                    && float.IsFinite(CentreOfMass.x) && float.IsFinite(CentreOfMass.y) && float.IsFinite(CentreOfMass.z)
                    && float.IsFinite(InertiaTensor.x) && float.IsFinite(InertiaTensor.y) && float.IsFinite(InertiaTensor.z)
                    && InertiaTensor.x > 0f && InertiaTensor.y > 0f && InertiaTensor.z > 0f
                    && float.IsFinite(q) && Mathf.Abs(q - 1f) < 1e-3f;
            }
        }

        /// <summary>Where the centre of mass stands with the character root at this position and rotation.</summary>
        public Vector3 CentreOfMassAt(Vector3 rootPosition, Quaternion rootRotation) => rootPosition + rootRotation * CentreOfMass;

        /// <summary>
        /// The settings an authored body holds, read once, into the character root's frame: its mass, its centre of mass
        /// and its inertia with its principal axes, placed by where the body's Transform stands against the root's now.
        /// The body's settings and the two Transforms are all that is read -- not where the physics has the body -- and
        /// nothing is written to it. A character keeps the result; the body is not read again.
        /// <para>
        /// The body has to be in the scene when it is read: Unity gives no centre of mass of a body out of it (the read
        /// comes back as zero). For such a body, or none, the result is the default, which is not usable
        /// (<see cref="IsUsable"/>).
        /// </para>
        /// </summary>
        public static VpCharacterMassProperties FromBody(Rigidbody body, Transform characterRoot)
        {
            if (body == null || characterRoot == null || !body.gameObject.activeInHierarchy) return default;
            characterRoot.GetPositionAndRotation(out Vector3 rootPosition, out Quaternion rootRotation);
            body.transform.GetPositionAndRotation(out Vector3 bodyPosition, out Quaternion bodyRotation);
            Quaternion toRoot = Quaternion.Inverse(rootRotation);
            return new VpCharacterMassProperties(
                body.mass,
                toRoot * (bodyPosition + bodyRotation * body.centerOfMass - rootPosition),
                body.inertiaTensor,
                toRoot * bodyRotation * body.inertiaTensorRotation);
        }
    }
}
