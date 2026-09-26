using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// The explicit authoring of one occupancy primitive (DESIGN 7.2.3): a box or a capsule, placed by this object's
    /// transform and the values below. It is not a collider and takes no part in the physics scene. Its size is the
    /// value given here, never the object's bounds, mesh or collider; the transform's position and rotation place it,
    /// and scale is not applied to the size.
    /// <para>
    /// It is read once, when <see cref="PlayerLocomotionLevel"/> settles the level's occupancy. Changing, moving or
    /// destroying it afterwards changes nothing of that occupancy.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerLocomotionOccupancyVolume : MonoBehaviour
    {
        public enum Shape
        {
            Box,
            Capsule,
        }

        [SerializeField] private Shape shape = Shape.Box;

        [Tooltip("The centre, in this object's local space.")]
        [SerializeField] private Vector3 center = Vector3.zero;

        [Tooltip("A rotation on top of this object's own, in degrees.")]
        [SerializeField] private Vector3 rotationEuler = Vector3.zero;

        [Tooltip("Box: the full size along each axis, in metres.")]
        [SerializeField] private Vector3 size = Vector3.one;

        [Tooltip("Capsule: the radius, in metres.")]
        [SerializeField] private float radius = 0.5f;

        [Tooltip("Capsule: the height end to end along its local Y axis, in metres.")]
        [SerializeField] private float height = 2f;

        public Shape Kind => shape;

        /// <summary>Sets the authoring values (for building levels and tests from code).</summary>
        public void Set(Shape kind, Vector3 localCenter, Vector3 localRotationEuler, Vector3 boxSize, float capsuleRadius, float capsuleHeight)
        {
            shape = kind;
            center = localCenter;
            rotationEuler = localRotationEuler;
            size = boxSize;
            radius = capsuleRadius;
            height = capsuleHeight;
        }

        /// <summary>The box this authors, in world space as the object stands now.</summary>
        public LocomotionBox WorldBox()
        {
            return new LocomotionBox(
                transform.TransformPoint(center), transform.rotation * Quaternion.Euler(rotationEuler), size * 0.5f);
        }

        /// <summary>The capsule this authors, in world space as the object stands now.</summary>
        public LocomotionCapsule WorldCapsule()
        {
            return LocomotionCapsule.Upright(
                transform.TransformPoint(center), transform.rotation * Quaternion.Euler(rotationEuler), radius, height);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.8f);
            if (shape == Shape.Box)
            {
                LocomotionBox box = WorldBox();
                Gizmos.matrix = Matrix4x4.TRS(box.center, box.rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, box.halfExtents * 2f);
            }
            else
            {
                LocomotionCapsule capsule = WorldCapsule();
                Gizmos.DrawWireSphere(capsule.a, capsule.radius);
                Gizmos.DrawWireSphere(capsule.b, capsule.radius);
                Gizmos.DrawLine(capsule.a, capsule.b);
            }
        }
    }
}
